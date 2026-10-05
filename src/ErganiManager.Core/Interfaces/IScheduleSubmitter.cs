using ErganiManager.Core.Models;

namespace ErganiManager.Core.Interfaces;

/// <summary>Outcome for one schedule day that was sent to Ergani.</summary>
public sealed class ScheduleSubmitDayResult
{
    public int EmployeeId { get; init; }
    public DateOnly Date { get; init; }
    public bool Success { get; init; }
    public string? Protocol { get; init; }
    public string? Error { get; init; }
}

public sealed class ScheduleSubmitResult
{
    /// <summary>Schedule days accepted by Ergani.</summary>
    public int Submitted { get; set; }

    /// <summary>Schedule days that were rejected or not sent.</summary>
    public int Failed { get; set; }

    /// <summary>Set when the run could not even start (e.g. company not found).</summary>
    public string? Error { get; set; }

    public List<ScheduleSubmitDayResult> Days { get; } = new();
}

public interface IScheduleSubmitter
{
    /// <summary>Submits exactly the days passed in. Each date is one Ergani submission and
    /// is written to the API submission log (request, response, protocol, errors).</summary>
    Task<ScheduleSubmitResult> SubmitScheduleDaysAsync(
        int companyId, IReadOnlyList<ScheduleDayDto> days);

    Task<(int Submitted, string? Error)> SubmitOvertimeDaysAsync(
        int companyId, IReadOnlyList<ScheduleDayDto> overtimeDays);
}
