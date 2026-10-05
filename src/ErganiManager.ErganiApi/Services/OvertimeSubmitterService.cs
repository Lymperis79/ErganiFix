using ErganiManager.Core.Interfaces;
using ErganiManager.Data;
using ErganiManager.Data.Entities;
using ErganiManager.ErganiApi.Models;
using Microsoft.EntityFrameworkCore;

namespace ErganiManager.ErganiApi.Services;

public sealed class OvertimeSubmitterService : IOvertimeSubmitter
{
    private readonly IErganiClient _client;
    private readonly IConnectionStateService _connectionState;
    private readonly ICredentialProtector _protector;

    public OvertimeSubmitterService(IErganiClient client, IConnectionStateService connectionState, ICredentialProtector protector)
    { _client = client; _connectionState = connectionState; _protector = protector; }

    public async Task<OvertimeBatchSubmissionResult> SubmitAsync(int companyId, IReadOnlyList<int> overtimeIds, CancellationToken ct = default)
    {
        if (overtimeIds.Count == 0) return new() { ErrorMessage = "No overtime records selected." };
        await using var db = new AppDbContext(_connectionState.GetDbOptions());
        var company = await db.Companies.FindAsync(new object[] { companyId }, ct);
        if (company == null) return new() { ErrorMessage = "Company not found." };
        var records = await db.Overtimes.Include(x => x.Employee).Include(x => x.Branch)
            .Where(x => overtimeIds.Contains(x.Id) && x.Employee != null && x.Employee.CompanyId == companyId && !x.IsCancelled && !x.SubmittedToErgani)
            .ToListAsync(ct);
        if (records.Count == 0) return new() { ErrorMessage = "No eligible unsent overtime records were selected." };

        foreach (var r in records)
        {
            var cutoff = r.OvertimeDate.ToDateTime(r.EndTime).AddHours(-2);
            if (DateTime.Now >= cutoff)
                return new() { ErrorMessage = $"Overtime for {r.Employee?.FullName ?? "employee"} ending at {r.EndTime:HH:mm} cannot be submitted now. The 2-hour submission cutoff was {cutoff:dd/MM/yyyy HH:mm}." };
        }

        var groups = records.GroupBy(x => new { x.BranchId, x.OvertimeDate, x.StartTime, x.EndTime, x.Justification, x.WeeklyWorkdaysNumber, x.AseeApproval });
        var credentials = new ErganiCredentials { Username = company.ErganiUsername, Password = _protector.Unprotect(company.ErganiPasswordEncrypted), Usertype = company.ErganiUsertype, BaseUrl = company.ErganiBaseUrl };
        var total = 0; string? lastProtocol = null; string? lastSubmissionId = null; string? lastResponse = null;

        foreach (var group in groups)
        {
            var branch = group.First().Branch;
            if (branch == null) continue;
            var submission = new CompanyOvertimeSubmission
            {
                BusinessBranchNumber = branch.BranchNumber, SepeServiceCode = branch.SepeServiceCode,
                BusinessPrimaryActivityCode = branch.ActivityCode, BusinessBranchActivityCode = branch.ActivityCode,
                KallikratisMunicipalCode = branch.KallikratisMunicipalCode,
                LegalRepresentativeTaxIdentificationNumber = company.TaxId,
                EmployeeOvertimes = group.Select(x => new OvertimeEntry
                {
                    EmployeeTaxIdentificationNumber = x.Employee!.TaxId, EmployeeSocialSecurityNumber = x.Employee.SocialSecurityNumber, EmployeeProfessionCode = x.Employee.ProfessionCode, EmployeeLastName = x.Employee.LastName, EmployeeFirstName = x.Employee.FirstName,
                    OvertimeDate = x.OvertimeDate, StartTime = x.StartTime, EndTime = x.EndTime, Justification = MapJustification(x.Justification),
                    WeeklyWorkdaysNumber = x.WeeklyWorkdaysNumber, AseeApproval = x.AseeApproval
                }).ToList()
            };
            var result = await _client.SubmitOvertimeAsync(credentials, new List<CompanyOvertimeSubmission> { submission }, ct);
            var response = result.Data?.FirstOrDefault();
            var success = result.Success && response?.IsBusinessError != true && !string.IsNullOrWhiteSpace(response?.Protocol);
            var protocol = response?.Protocol; var submissionId = response?.SubmissionId;
            var submittedDate = DateOnly.FromDateTime(DateTime.Today);
            foreach (var entity in group)
            {
                entity.SubmittedToErgani = success; entity.Protocol = protocol; entity.SubmissionId = submissionId;
                entity.ResponseRawJson = result.ResponseRawJson; entity.RequestPayloadJson = result.RequestPayloadJson; entity.SubmittedDate = success ? submittedDate : null; entity.HttpStatusCode = result.HttpStatusCode;
            }
            db.ApiSubmissionLogs.Add(new ApiSubmissionLog
            { CompanyId = companyId, EmployeeId = group.Count() == 1 ? group.First().EmployeeId : null, SubmissionType = "Overtime", RequestPayloadJson = result.RequestPayloadJson, ResponseRawJson = result.ResponseRawJson, SubmissionId = submissionId, Protocol = protocol, SubmissionDate = DateTime.UtcNow, HttpStatusCode = result.HttpStatusCode, Success = success, ErrorMessage = success ? null : (result.ErrorMessage ?? response?.Description ?? "Ergani overtime submission failed."), DurationMs = result.DurationMs });
            await db.SaveChangesAsync(ct);
            lastProtocol = protocol; lastSubmissionId = submissionId; lastResponse = result.ResponseRawJson;
            if (!success) return new() { Success = false, SubmittedCount = total, Protocol = protocol, SubmissionId = submissionId, ResponseRawJson = result.ResponseRawJson, ErrorMessage = result.ErrorMessage ?? response?.Description ?? "Ergani overtime submission failed." };
            total += group.Count();
        }
        return new() { Success = true, SubmittedCount = total, Protocol = lastProtocol, SubmissionId = lastSubmissionId, ResponseRawJson = lastResponse };
    }

    private static ApiOvertimeJustification MapJustification(OvertimeJustification value) => value switch
    {
        OvertimeJustification.AccidentPreventionOrDamageRestoration => ApiOvertimeJustification.ACCIDENT_PREVENTION_OR_DAMAGE_RESTORATION,
        OvertimeJustification.UrgentSeasonalTasks => ApiOvertimeJustification.URGENT_SEASONAL_TASKS,
        OvertimeJustification.ExceptionalWorkload => ApiOvertimeJustification.EXCEPTIONAL_WORKLOAD,
        OvertimeJustification.SupplementaryTasks => ApiOvertimeJustification.SUPPLEMENTARY_TASKS,
        OvertimeJustification.LostHoursSuddenCauses => ApiOvertimeJustification.LOST_HOURS_SUDDEN_CAUSES,
        OvertimeJustification.LostHoursOfficialHolidays => ApiOvertimeJustification.LOST_HOURS_OFFICIAL_HOLIDAYS,
        OvertimeJustification.LostHoursWeatherConditions => ApiOvertimeJustification.LOST_HOURS_WEATHER_CONDITIONS,
        OvertimeJustification.EmergencyClosureDay => ApiOvertimeJustification.EMERGENCY_CLOSURE_DAY,
        OvertimeJustification.NonWorkdayTasks => ApiOvertimeJustification.NON_WORKDAY_TASKS,
        _ => ApiOvertimeJustification.EXCEPTIONAL_WORKLOAD
    };
}
