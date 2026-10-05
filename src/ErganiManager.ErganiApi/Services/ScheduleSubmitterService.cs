using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ErganiManager.Core.Interfaces;
using ErganiManager.Core.Models;
using ErganiManager.Data;
using ErganiManager.Data.Entities;
using ErganiManager.ErganiApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErganiManager.ErganiApi.Services;

public class ScheduleSubmitterService : IScheduleSubmitter
{
    private readonly IErganiClient           _erganiClient;
    private readonly IConnectionStateService _connectionState;
    private readonly ICredentialProtector    _credentialProtector;
    private readonly ILogger<ScheduleSubmitterService> _logger;

    public ScheduleSubmitterService(
        IErganiClient erganiClient,
        IConnectionStateService connectionState,
        ICredentialProtector credentialProtector,
        ILogger<ScheduleSubmitterService> logger)
    {
        _erganiClient        = erganiClient;
        _connectionState     = connectionState;
        _credentialProtector = credentialProtector;
        _logger              = logger;
    }

    private AppDbContext OpenDb() => new AppDbContext(_connectionState.GetDbOptions());

    public async Task<ScheduleSubmitResult> SubmitScheduleDaysAsync(
        int companyId, IReadOnlyList<ScheduleDayDto> days)
    {
        var outcome = new ScheduleSubmitResult();
        if (days.Count == 0) return outcome;

        await using var db = OpenDb();
        var company = await db.Companies.FindAsync(companyId);
        if (company == null)
        {
            outcome.Error = "Company not found.";
            return outcome;
        }

        var credentials = BuildCredentials(company);
        var stopped     = false;   // Ergani unreachable — no point trying the remaining dates

        foreach (var branchGroup in days.GroupBy(d => d.BranchId))
        {
            var branch = await db.Branches.FindAsync(branchGroup.Key);
            if (branch == null)
            {
                foreach (var d in branchGroup)
                    AddDayResult(outcome, d, false, null, "Branch not found.");
                continue;
            }

            var employeeIds = branchGroup.Select(d => d.EmployeeId).Distinct().ToList();
            var employees   = await db.Employees
                .Where(e => employeeIds.Contains(e.Id)).ToListAsync();

            // One Ergani submission per date.
            foreach (var dateGroup in branchGroup.GroupBy(d => d.ScheduleDate).OrderBy(g => g.Key))
            {
                if (stopped)
                {
                    foreach (var d in dateGroup)
                        AddDayResult(outcome, d, false, null, "Not sent — Ergani was unreachable.");
                    continue;
                }

                var scheduleEntries = dateGroup
                    .Join(employees, d => d.EmployeeId, e => e.Id,
                        (d, e) => new EmployeeDailySchedule
                        {
                            EmployeeTaxIdentificationNumber = e.TaxId,
                            EmployeeLastName                = e.LastName,
                            EmployeeFirstName               = e.FirstName,
                            ScheduleDate                    = d.ScheduleDate,
                            WorkdayDetails                  = BuildWorkdayDetails(d)
                        })
                    .ToList();

                if (scheduleEntries.Count == 0)
                {
                    foreach (var d in dateGroup)
                        AddDayResult(outcome, d, false, null, "Employee not found.");
                    continue;
                }

                var submission = new CompanyDailyScheduleSubmission
                {
                    EmployerTaxIdentificationNumber = company.TaxId,
                    BusinessBranchNumber            = branch.BranchNumber,
                    SepeServiceCode                 = branch.SepeServiceCode,
                    BusinessPrimaryActivityCode     = branch.ActivityCode,
                    KallikratisMunicipalCode        = branch.KallikratisMunicipalCode,
                    EmployeeSchedules               = scheduleEntries
                };

                var result   = await _erganiClient.SubmitDailyScheduleAsync(
                    credentials, new List<CompanyDailyScheduleSubmission> { submission });
                var response = result.Data?.FirstOrDefault();

                // A 200 response that carries a description but no protocol is an Ergani business error.
                var businessError = result.IsBusinessError || response is { IsBusinessError: true };
                var success       = result.Success && !businessError;
                var error         = success
                    ? null
                    : (result.ErrorMessage ?? response?.Description ?? "Ergani schedule submission failed.");

                // Audit log — one row per attempt, successful or not (same table the work cards use).
                var distinctEmployees = dateGroup.Select(d => d.EmployeeId).Distinct().ToList();
                db.ApiSubmissionLogs.Add(new ApiSubmissionLog
                {
                    CompanyId          = companyId,
                    EmployeeId         = distinctEmployees.Count == 1 ? distinctEmployees[0] : null,
                    SubmissionType     = "DailySchedule",
                    ScheduleDate       = dateGroup.Key,
                    RequestPayloadJson = result.RequestPayloadJson,
                    ResponseRawJson    = result.ResponseRawJson,
                    SubmissionId       = response?.SubmissionId,
                    Protocol           = response?.Protocol,
                    SubmissionDate     = DateTime.UtcNow,
                    HttpStatusCode     = result.HttpStatusCode,
                    Success            = success,
                    ErrorMessage       = error,
                    DurationMs         = result.DurationMs
                });

                if (success)
                {
                    foreach (var day in dateGroup)
                    {
                        var entity = await db.Schedules.FindAsync(day.Id);
                        if (entity != null)
                        {
                            entity.SubmittedToErgani = true;
                            entity.SubmissionId      = response?.SubmissionId;
                            entity.Protocol          = response?.Protocol;
                        }
                    }
                }

                await db.SaveChangesAsync();

                foreach (var day in dateGroup)
                    AddDayResult(outcome, day, success, response?.Protocol, error);

                if (!success)
                {
                    _logger.LogWarning("Schedule submit failed for branch {B} date {D}: {E}",
                        branch.Id, dateGroup.Key, error);

                    if (result.IsServiceUnavailable) stopped = true;
                }
            }
        }

        return outcome;
    }

    private static void AddDayResult(
        ScheduleSubmitResult outcome, ScheduleDayDto day, bool success, string? protocol, string? error)
    {
        outcome.Days.Add(new ScheduleSubmitDayResult
        {
            EmployeeId = day.EmployeeId,
            Date       = day.ScheduleDate,
            Success    = success,
            Protocol   = protocol,
            Error      = error
        });
        if (success) outcome.Submitted++; else outcome.Failed++;
    }

    public async Task<(int Submitted, string? Error)> SubmitOvertimeDaysAsync(
        int companyId, IReadOnlyList<ScheduleDayDto> overtimeDays)
    {
        if (overtimeDays.Count == 0) return (0, null);

        await using var db = OpenDb();
        var company = await db.Companies.FindAsync(companyId);
        if (company == null) return (0, "Company not found.");

        var credentials = BuildCredentials(company);
        int submitted   = 0;

        foreach (var day in overtimeDays)
        {
            if (!day.ActualArrival.HasValue || !day.ActualDeparture.HasValue) continue;

            var employee = await db.Employees.FindAsync(day.EmployeeId);
            var branch   = await db.Branches.FindAsync(day.BranchId);
            if (employee == null || branch == null) continue;

            var overtimeStart = day.ActualArrival.Value.AddHours(8);
            var overtimeEnd   = day.ActualDeparture.Value;

            var submission = new CompanyOvertimeSubmission
            {
                BusinessBranchNumber        = branch.BranchNumber,
                SepeServiceCode             = branch.SepeServiceCode,
                BusinessPrimaryActivityCode = branch.ActivityCode,
                BusinessBranchActivityCode  = branch.ActivityCode,
                KallikratisMunicipalCode    = branch.KallikratisMunicipalCode,
                LegalRepresentativeTaxIdentificationNumber = company.TaxId,
                EmployeeOvertimes = new List<OvertimeEntry>
                {
                    new()
                    {
                        EmployeeTaxIdentificationNumber = employee.TaxId,
                        EmployeeSocialSecurityNumber    = employee.SocialSecurityNumber,
                        EmployeeProfessionCode          = employee.ProfessionCode,
                        EmployeeLastName                = employee.LastName,
                        EmployeeFirstName               = employee.FirstName,
                        OvertimeDate                    = day.ScheduleDate,
                        StartTime                       = TimeOnly.FromDateTime(overtimeStart),
                        EndTime                         = TimeOnly.FromDateTime(overtimeEnd),
                        Justification                   = ApiOvertimeJustification.EXCEPTIONAL_WORKLOAD,
                        WeeklyWorkdaysNumber            = employee.WeeklyWorkdays
                    }
                }
            };

            var result = await _erganiClient.SubmitOvertimeAsync(
                credentials, new List<CompanyOvertimeSubmission> { submission });

            if (result.Success)
            {
                var protocol = result.Data?.FirstOrDefault()?.Protocol;
                db.Overtimes.Add(new Overtime
                {
                    EmployeeId           = employee.Id,
                    BranchId             = branch.Id,
                    OvertimeDate         = day.ScheduleDate,
                    StartTime            = TimeOnly.FromDateTime(overtimeStart),
                    EndTime              = TimeOnly.FromDateTime(overtimeEnd),
                    Justification        = OvertimeJustification.ExceptionalWorkload,
                    WeeklyWorkdaysNumber = employee.WeeklyWorkdays,
                    SubmittedToErgani    = true,
                    Protocol             = protocol,
                    SubmissionId         = result.Data?.FirstOrDefault()?.SubmissionId,
                    CreatedAt            = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
                submitted++;
            }
            else
            {
                _logger.LogWarning("Overtime submit failed for employee {E} on {D}: {Err}",
                    employee.Id, day.ScheduleDate, result.ErrorMessage);
                return (submitted, result.ErrorMessage);
            }
        }

        return (submitted, null);
    }

    private ErganiCredentials BuildCredentials(Data.Entities.Company c) => new()
    {
        Username = c.ErganiUsername,
        Password = _credentialProtector.Unprotect(c.ErganiPasswordEncrypted),
        Usertype = c.ErganiUsertype,
    BaseUrl  = c.ErganiBaseUrl
    };

    private static List<WorkdayDetails> BuildWorkdayDetails(ScheduleDayDto day)
    {
        if (day.StartTime == null || day.EndTime == null) return new();
        return new()
        {
            new()
            {
                WorkDayType = day.WorkType switch
                {
                    AppWorkType.Home   => "ΤΗΛ",
                    AppWorkType.Office => "ΕΡΓ",
                    AppWorkType.Rest   => "ΑΝ",
                    AppWorkType.Absent => "ΜΕ",
                    _                  => "ΕΡΓ"
                },
                StartTime = day.StartTime.Value,
                EndTime   = day.EndTime.Value
            }
        };
    }
}
