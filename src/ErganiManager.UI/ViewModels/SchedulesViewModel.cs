using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ErganiManager.Core.Interfaces;
using ErganiManager.Core.Models;
using ErganiManager.UI.Services;

namespace ErganiManager.UI.ViewModels;

public enum ScheduleViewMode { Month, Week }

/// <summary>One visual cell in the calendar grid.</summary>
public partial class CalendarCellViewModel : ViewModelBase
{
    public bool IsInMonth { get; init; }
    public DateOnly? Date { get; init; }

    [ObservableProperty] private AppWorkType? _workType;
    [ObservableProperty] private string _timeRangeText = string.Empty;
    [ObservableProperty] private bool _hasSchedule;
    [ObservableProperty] private bool _isToday;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isSubmitted;

    public string DayNumberText => Date?.Day.ToString() ?? string.Empty;
    public string WorkTypeIcon => WorkType switch
    {
        AppWorkType.Office => "🏢",
        AppWorkType.Home   => "🏠",
        AppWorkType.Rest   => "💤",
        AppWorkType.Absent => "❌",
        _                  => ""
    };
}

/// <summary>One line of the Ergani schedule submission log.</summary>
public class ScheduleLogRow
{
    public int Id { get; init; }
    public DateOnly? ScheduleDate { get; init; }
    /// <summary>UTC time of the attempt.</summary>
    public DateTime SubmissionDate { get; init; }
    public bool Success { get; init; }
    public string? Protocol { get; init; }
    public string? ErrorMessage { get; init; }
    public int? HttpStatusCode { get; init; }
    public string? ResponseRawJson { get; init; }

    public string StatusIcon => Success ? "✅" : "❌";
    public string ScheduleDateText => ScheduleDate?.ToString("dd/MM/yyyy") ?? "—";
    public string SubmittedAtText => SubmissionDate.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public bool HasProtocol => !string.IsNullOrWhiteSpace(Protocol);
    public string Summary => Success
        ? (HasProtocol ? $"Protocol: {Protocol}" : "Accepted by Ergani")
        : (string.IsNullOrWhiteSpace(ErrorMessage) ? "Failed" : ErrorMessage!);
}

public partial class SchedulesViewModel : ViewModelBase, IAdminSectionViewModel
{
    private readonly IScheduleService   _scheduleService;
    private readonly IEmployeeService   _employeeService;
    private readonly IBranchService     _branchService;
    private readonly IScheduleSubmitter _scheduleSubmitter;
    private readonly IErganiDocumentService _documentService;
    private UserSession? _session;

    // ── Employee / navigation ─────────────────────────────────────────────────
    public ObservableCollection<EmployeeDto> AvailableEmployees { get; } = new();
    [ObservableProperty] private EmployeeDto? _selectedEmployee;

    [ObservableProperty] private int _year  = DateTime.Today.Year;
    [ObservableProperty] private int _month = DateTime.Today.Month;
    [ObservableProperty] private int _weekOffset = 0; // 0 = current week
    public string MonthLabel => new DateOnly(Year, Month, 1).ToString("MMMM yyyy");
    public string WeekLabel
    {
        get
        {
            var (from, to) = CurrentWeekRange;
            return $"{from:dd/MM} – {to:dd/MM/yyyy}";
        }
    }
    private (DateOnly From, DateOnly To) CurrentWeekRange
    {
        get
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var monday = today.AddDays(-(int)today.DayOfWeek == 0 ? 6 : (int)today.DayOfWeek - 1);
            monday = monday.AddDays(_weekOffset * 7);
            return (monday, monday.AddDays(6));
        }
    }

    [ObservableProperty] private ScheduleViewMode _viewMode = ScheduleViewMode.Month;
    public bool IsMonthView => ViewMode == ScheduleViewMode.Month;
    public bool IsWeekView  => ViewMode == ScheduleViewMode.Week;

    public ObservableCollection<CalendarCellViewModel> Cells { get; } = new();

    [ObservableProperty] private bool _hasActiveCompany;
    [ObservableProperty] private string _noCompanyMessage = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    // ── Multi-day selection ───────────────────────────────────────────────────
    private readonly HashSet<DateOnly> _selectedDates = new();
    public int SelectedDayCount => _selectedDates.Count;
    public bool HasSelection => _selectedDates.Count > 0;
    public string SelectionLabel => _selectedDates.Count == 0
        ? "No days selected"
        : $"{_selectedDates.Count} day(s) selected";

    /// <summary>With days selected the button submits only those; otherwise it submits the month.</summary>
    public string SubmitScheduleButtonText => HasSelection
        ? $"📤 Submit selected ({_selectedDates.Count})"
        : "📤 Submit Schedule";
    public string DeleteSelectedButtonText => $"🗑 Delete selected ({_selectedDates.Count})";

    // ── Bulk / day dialog ─────────────────────────────────────────────────────
    [ObservableProperty] private bool _isDayDialogOpen;
    [ObservableProperty] private DateOnly _editingDate;
    [ObservableProperty] private int _editingScheduleId;
    [ObservableProperty] private AppWorkType _editingWorkType = AppWorkType.Office;
    [ObservableProperty] private TimeSpan? _editingStartTime = new(9, 0, 0);
    [ObservableProperty] private TimeSpan? _editingEndTime   = new(17, 0, 0);
    [ObservableProperty] private string _editingComments = string.Empty;
    [ObservableProperty] private string _editingActualText = string.Empty;
    [ObservableProperty] private string _editingSubmissionText = string.Empty;
    [ObservableProperty] private BranchDto? _editingBranch;
    [ObservableProperty] private bool _editingSubmitted;

    // ── Delete confirmation ───────────────────────────────────────────────────
    [ObservableProperty] private bool _isConfirmDeleteOpen;
    [ObservableProperty] private string _confirmDeleteMessage = string.Empty;
    private readonly List<DateOnly> _pendingDeleteDates = new();

    // ── Submission log (Ergani responses + PDF) ───────────────────────────────
    public ObservableCollection<ScheduleLogRow> LogRows { get; } = new();
    [ObservableProperty] private bool _isLogOpen;
    [ObservableProperty] private bool _hasLogRows;
    [ObservableProperty] private string _logTitle = string.Empty;
    [ObservableProperty] private string _logStatus = string.Empty;
    [ObservableProperty] private bool _isResponseOpen;
    [ObservableProperty] private string _responseTitle = string.Empty;
    [ObservableProperty] private string _responseText = string.Empty;

    // ── Week bulk ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isBulkDialogOpen;
    [ObservableProperty] private AppWorkType _bulkWorkType = AppWorkType.Office;
    [ObservableProperty] private TimeSpan? _bulkStartTime = new(9, 0, 0);
    [ObservableProperty] private TimeSpan? _bulkEndTime   = new(17, 0, 0);
    [ObservableProperty] private bool _bulkMonday    = true;
    [ObservableProperty] private bool _bulkTuesday   = true;
    [ObservableProperty] private bool _bulkWednesday = true;
    [ObservableProperty] private bool _bulkThursday  = true;
    [ObservableProperty] private bool _bulkFriday    = true;
    [ObservableProperty] private bool _bulkSaturday  = false;
    [ObservableProperty] private bool _bulkSunday    = false;

    // ── Clone to other employees ──────────────────────────────────────────────
    [ObservableProperty] private bool _isCloneDialogOpen;
    [ObservableProperty] private DateOnly? _cloneFrom;
    [ObservableProperty] private DateOnly? _cloneTo;
    public ObservableCollection<CloneTargetEmployee> CloneTargets { get; } = new();

    public ObservableCollection<BranchDto> AvailableBranches { get; } = new();
    public ObservableCollection<AppWorkType> AvailableWorkTypes { get; } =
        new(Enum.GetValues<AppWorkType>());
    public bool ShowTimeFields     => EditingWorkType is AppWorkType.Office or AppWorkType.Home;
    public bool BulkShowTimeFields => BulkWorkType    is AppWorkType.Office or AppWorkType.Home;
    public bool IsEditingExisting  => EditingScheduleId != 0;
    /// <summary>Only Office/Home days that are not yet at Ergani can be submitted from the day dialog.</summary>
    public bool CanSubmitEditingDay => ShowTimeFields && !EditingSubmitted;

    public SchedulesViewModel(IScheduleService scheduleService,
        IEmployeeService employeeService, IBranchService branchService,
        IScheduleSubmitter scheduleSubmitter, IErganiDocumentService documentService)
    {
        _scheduleService   = scheduleService;
        _employeeService   = employeeService;
        _branchService     = branchService;
        _scheduleSubmitter = scheduleSubmitter;
        _documentService   = documentService;
    }

    public void Initialize(UserSession session)
    {
        _session = session;
        HasActiveCompany = session.CompanyId.HasValue;
        NoCompanyMessage = session.CompanyId.HasValue ? string.Empty
            : "Select a company first.";
        if (HasActiveCompany) _ = LoadFiltersAsync();
    }

    private async Task LoadFiltersAsync()
    {
        if (_session?.CompanyId is not int companyId) return;

        var employees = await _employeeService
            .GetByCompanyAsync(companyId, activeOnly: true).ConfigureAwait(false);
        var branches  = await _branchService
            .GetByCompanyAsync(companyId).ConfigureAwait(false);

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            AvailableEmployees.Clear();
            foreach (var e in employees) AvailableEmployees.Add(e);
            AvailableBranches.Clear();
            foreach (var b in branches) AvailableBranches.Add(b);
            SelectedEmployee = AvailableEmployees.FirstOrDefault();
            EditingBranch    = AvailableBranches.FirstOrDefault();
        });

        await RefreshCalendarAsync();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task PreviousMonthAsync()
    {
        if (Month == 1) { Month = 12; Year--; } else Month--;
        OnPropertyChanged(nameof(MonthLabel));
        await RefreshCalendarAsync();
    }

    [RelayCommand]
    private async Task NextMonthAsync()
    {
        if (Month == 12) { Month = 1; Year++; } else Month++;
        OnPropertyChanged(nameof(MonthLabel));
        await RefreshCalendarAsync();
    }

    [RelayCommand]
    private async Task PreviousWeekAsync()
    {
        _weekOffset--;
        OnPropertyChanged(nameof(WeekLabel));
        await RefreshCalendarAsync();
    }

    [RelayCommand]
    private async Task NextWeekAsync()
    {
        _weekOffset++;
        OnPropertyChanged(nameof(WeekLabel));
        await RefreshCalendarAsync();
    }

    [RelayCommand]
    private async Task SetViewModeAsync(string mode)
    {
        ViewMode = mode == "Week" ? ScheduleViewMode.Week : ScheduleViewMode.Month;
        OnPropertyChanged(nameof(IsMonthView));
        OnPropertyChanged(nameof(IsWeekView));
        await RefreshCalendarAsync();
    }

    partial void OnSelectedEmployeeChanged(EmployeeDto? value) =>
        _ = RefreshCalendarAsync();

    // ── Calendar build ────────────────────────────────────────────────────────

    private async Task RefreshCalendarAsync()
    {
        if (SelectedEmployee == null) return;

        _selectedDates.Clear();
        NotifySelectionChanged();

        if (ViewMode == ScheduleViewMode.Month)
            await BuildMonthCalendarAsync();
        else
            await BuildWeekCalendarAsync();
    }

    private async Task BuildMonthCalendarAsync()
    {
        var schedules = await _scheduleService
            .GetMonthAsync(SelectedEmployee!.Id, Year, Month).ConfigureAwait(false);
        var byDate = schedules.ToDictionary(s => s.ScheduleDate);
        var today  = DateOnly.FromDateTime(DateTime.Today);
        var first  = new DateOnly(Year, Month, 1);
        var days   = DateTime.DaysInMonth(Year, Month);
        int startDow = ((int)first.DayOfWeek + 6) % 7; // Mon=0

        var cells = new List<CalendarCellViewModel>();
        for (int i = 0; i < startDow; i++)
            cells.Add(new CalendarCellViewModel { IsInMonth = false });

        for (int d = 1; d <= days; d++)
        {
            var date = new DateOnly(Year, Month, d);
            byDate.TryGetValue(date, out var sched);
            cells.Add(new CalendarCellViewModel
            {
                IsInMonth     = true,
                Date          = date,
                HasSchedule   = sched != null,
                IsSubmitted   = sched?.SubmittedToErgani ?? false,
                WorkType      = sched != null ? (AppWorkType?)sched.WorkType : null,
                TimeRangeText = sched is { StartTime: not null, EndTime: not null }
                    ? $"{sched.StartTime:HH:mm}–{sched.EndTime:HH:mm}" : "",
                IsToday       = date == today
            });
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Cells.Clear();
            foreach (var c in cells) Cells.Add(c);
        });
    }

    private async Task BuildWeekCalendarAsync()
    {
        var (from, to) = CurrentWeekRange;
        var schedules  = await _scheduleService
            .GetMonthAsync(SelectedEmployee!.Id, from.Year, from.Month).ConfigureAwait(false);

        // If week spans two months, also fetch next month
        if (to.Month != from.Month)
        {
            var extra = await _scheduleService
                .GetMonthAsync(SelectedEmployee.Id, to.Year, to.Month).ConfigureAwait(false);
            schedules = schedules.Concat(extra).ToList();
        }

        var byDate = schedules.ToDictionary(s => s.ScheduleDate);
        var today  = DateOnly.FromDateTime(DateTime.Today);
        var cells  = new List<CalendarCellViewModel>();

        for (var d = from; d <= to; d = d.AddDays(1))
        {
            byDate.TryGetValue(d, out var sched);
            cells.Add(new CalendarCellViewModel
            {
                IsInMonth     = true,
                Date          = d,
                HasSchedule   = sched != null,
                IsSubmitted   = sched?.SubmittedToErgani ?? false,
                WorkType      = sched != null ? (AppWorkType?)sched.WorkType : null,
                TimeRangeText = sched is { StartTime: not null, EndTime: not null }
                    ? $"{sched.StartTime:HH:mm}–{sched.EndTime:HH:mm}" : "",
                IsToday       = d == today
            });
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Cells.Clear();
            foreach (var c in cells) Cells.Add(c);
        });
    }

    // ── Cell click — multi-select with Ctrl support ───────────────────────────

    /// <summary>Called from code-behind. ctrl=true → toggle selection.
    /// ctrl=false → open day dialog if single day already selected or toggle.</summary>
    public void OnCellClick(CalendarCellViewModel cell, bool ctrl)
    {
        if (!cell.IsInMonth || cell.Date == null) return;
        var date = cell.Date.Value;

        if (ctrl)
        {
            // Toggle selection
            if (_selectedDates.Contains(date)) { _selectedDates.Remove(date); cell.IsSelected = false; }
            else { _selectedDates.Add(date); cell.IsSelected = true; }
            NotifySelectionChanged();
        }
        else if (_selectedDates.Count == 0)
        {
            // Single click with no existing selection → open day dialog
            _ = OpenDayDialogAsync(cell);
        }
        else
        {
            // Toggle this date and keep multi-select mode
            if (_selectedDates.Contains(date)) { _selectedDates.Remove(date); cell.IsSelected = false; }
            else { _selectedDates.Add(date); cell.IsSelected = true; }
            NotifySelectionChanged();
        }
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedDayCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(SubmitScheduleButtonText));
        OnPropertyChanged(nameof(DeleteSelectedButtonText));
    }

    [RelayCommand]
    private void ClearSelection()
    {
        _selectedDates.Clear();
        foreach (var c in Cells) c.IsSelected = false;
        NotifySelectionChanged();
    }

    // ── Single day dialog ─────────────────────────────────────────────────────

    private async Task OpenDayDialogAsync(CalendarCellViewModel cell)
    {
        if (cell.Date is not DateOnly date || SelectedEmployee == null) return;

        ScheduleDayDto? day;
        try { day = await _scheduleService.GetByDateAsync(SelectedEmployee.Id, date); }
        catch (Exception ex) { StatusMessage = $"❌ {ex.Message}"; return; }

        // GetByDateAsync also returns a stub (Id = 0) when the day only has work cards.
        var existing = day is { Id: > 0 } ? day : null;

        EditingDate       = date;
        EditingScheduleId = existing?.Id ?? 0;
        EditingWorkType   = existing?.WorkType ?? AppWorkType.Office;
        EditingStartTime  = existing?.StartTime?.ToTimeSpan() ?? new TimeSpan(9, 0, 0);
        EditingEndTime    = existing?.EndTime?.ToTimeSpan()   ?? new TimeSpan(17, 0, 0);
        EditingComments   = existing?.Comments ?? string.Empty;
        EditingSubmitted  = existing?.SubmittedToErgani ?? false;
        EditingBranch     = (existing != null
                                ? AvailableBranches.FirstOrDefault(b => b.Id == existing.BranchId)
                                : null)
                            ?? AvailableBranches.FirstOrDefault();

        EditingActualText = day?.ActualArrival != null || day?.ActualDeparture != null
            ? string.Format(Loc[L.ActualClockTimes],
                day?.ActualArrival?.ToString("HH:mm") ?? "—",
                day?.ActualDeparture?.ToString("HH:mm") ?? "—")
            : string.Empty;

        EditingSubmissionText = existing == null
            ? string.Empty
            : existing.SubmittedToErgani
                ? string.Format(Loc[L.SubmittedProtocol], existing.Protocol)
                : Loc[L.NotSubmitted];

        IsDayDialogOpen = true;
    }

    [RelayCommand]
    private void CloseDayDialog() => IsDayDialogOpen = false;

    partial void OnEditingWorkTypeChanged(AppWorkType value)
    {
        OnPropertyChanged(nameof(ShowTimeFields));
        OnPropertyChanged(nameof(CanSubmitEditingDay));
    }

    partial void OnEditingScheduleIdChanged(int value) =>
        OnPropertyChanged(nameof(IsEditingExisting));

    partial void OnEditingSubmittedChanged(bool value) =>
        OnPropertyChanged(nameof(CanSubmitEditingDay));

    private ScheduleDayDto BuildEditingDto() => new()
    {
        Id           = EditingScheduleId,
        EmployeeId   = SelectedEmployee!.Id,
        BranchId     = EditingBranch!.Id,
        ScheduleDate = EditingDate,
        WorkType     = EditingWorkType,
        StartTime    = EditingStartTime.HasValue ? TimeOnly.FromTimeSpan(EditingStartTime.Value) : null,
        EndTime      = EditingEndTime.HasValue   ? TimeOnly.FromTimeSpan(EditingEndTime.Value)   : null,
        Comments     = EditingComments
    };

    [RelayCommand]
    private async Task SaveDayAsync()
    {
        if (SelectedEmployee == null || EditingBranch == null) return;
        try
        {
            await _scheduleService.UpsertDayAsync(BuildEditingDto());
            IsDayDialogOpen = false;
            await RefreshCalendarAsync();
        }
        catch (Exception ex) { StatusMessage = $"❌ {ex.Message}"; }
    }

    /// <summary>Saves the day as shown in the dialog and submits just that one day to Ergani.</summary>
    [RelayCommand]
    private async Task SaveAndSubmitDayAsync()
    {
        if (SelectedEmployee == null || EditingBranch == null || _session?.CompanyId is not int companyId) return;
        try
        {
            var date = EditingDate;
            await _scheduleService.UpsertDayAsync(BuildEditingDto());
            IsDayDialogOpen = false;

            var saved = await _scheduleService.GetByDateAsync(SelectedEmployee.Id, date);
            if (saved is not { Id: > 0 })
            { StatusMessage = "❌ Could not reload the saved day."; return; }

            await SubmitDaysAsync(companyId, new List<ScheduleDayDto> { saved }, 1);
        }
        catch (Exception ex) { StatusMessage = $"❌ {ex.Message}"; }
    }

    // ── Delete (single day from the dialog, or all selected days) ─────────────

    [RelayCommand]
    private void DeleteDay()
    {
        if (EditingScheduleId == 0) return;
        RequestDelete(new[] { EditingDate });
    }

    [RelayCommand]
    private void DeleteSelected() => RequestDelete(_selectedDates.ToList());

    private void RequestDelete(IEnumerable<DateOnly> dates)
    {
        var byDate = Cells.Where(c => c.Date.HasValue).ToDictionary(c => c.Date!.Value);
        var withSchedule = dates
            .Distinct()
            .Where(d => byDate.TryGetValue(d, out var c) && c.HasSchedule)
            .OrderBy(d => d)
            .ToList();

        if (withSchedule.Count == 0)
        {
            StatusMessage = "There is no schedule to delete on the selected day(s).";
            return;
        }

        var submitted = withSchedule.Count(d => byDate[d].IsSubmitted);

        var message = withSchedule.Count == 1
            ? $"Delete the schedule for {withSchedule[0]:dd/MM/yyyy}?"
            : $"Delete the schedule of {withSchedule.Count} days?";

        if (submitted > 0)
            message += submitted == 1 && withSchedule.Count == 1
                ? "\n\nThis day was already sent to Ergani. Deleting only removes it from this app — it does not change what Ergani received."
                : $"\n\n{submitted} of them were already sent to Ergani. Deleting only removes them from this app — it does not change what Ergani received.";

        _pendingDeleteDates.Clear();
        _pendingDeleteDates.AddRange(withSchedule);
        ConfirmDeleteMessage = message;
        IsConfirmDeleteOpen = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmDeleteOpen = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (SelectedEmployee == null) { IsConfirmDeleteOpen = false; return; }

        var dates = _pendingDeleteDates.ToList();
        IsConfirmDeleteOpen = false;

        try
        {
            var removed = await _scheduleService.DeleteDaysAsync(SelectedEmployee.Id, dates);
            IsDayDialogOpen = false;
            StatusMessage = $"✅ Deleted the schedule of {removed} day(s).";
            await RefreshCalendarAsync();
        }
        catch (Exception ex) { StatusMessage = $"❌ {ex.Message}"; }
    }

    // ── Bulk apply (multi-selected days or week) ──────────────────────────────

    [RelayCommand]
    private void OpenBulkDialog()
    {
        if (SelectedEmployee == null) return;
        // Pre-fill from / to based on current view and selection
        IsBulkDialogOpen = true;
        BulkWorkType  = AppWorkType.Office;
        BulkStartTime = new TimeSpan(9, 0, 0);
        BulkEndTime   = new TimeSpan(17, 0, 0);
        // Day-of-week checkboxes stay as user left them
    }

    [RelayCommand]
    private void CloseBulkDialog() => IsBulkDialogOpen = false;

    partial void OnBulkWorkTypeChanged(AppWorkType value) =>
        OnPropertyChanged(nameof(BulkShowTimeFields));

    [RelayCommand]
    private async Task ApplyBulkAsync()
    {
        if (SelectedEmployee == null || EditingBranch == null) return;

        DateOnly from, to;
        IReadOnlySet<DayOfWeek>? dowFilter = null;

        if (_selectedDates.Count > 0)
        {
            // Apply to exactly the selected dates — ignore day-of-week checkboxes
            from      = _selectedDates.Min();
            to        = _selectedDates.Max();
            dowFilter = _selectedDates.Select(d => d.DayOfWeek).ToHashSet();
        }
        else if (ViewMode == ScheduleViewMode.Week)
        {
            var (wFrom, wTo) = CurrentWeekRange;
            from = wFrom; to = wTo;
            dowFilter = BuildDowFilter();
        }
        else
        {
            from = new DateOnly(Year, Month, 1);
            to   = new DateOnly(Year, Month, DateTime.DaysInMonth(Year, Month));
            dowFilter = BuildDowFilter();
        }

        try
        {
            var count = await _scheduleService.BulkSetAsync(
                SelectedEmployee.Id, EditingBranch.Id, from, to,
                BulkWorkType,
                BulkShowTimeFields && BulkStartTime.HasValue ? TimeOnly.FromTimeSpan(BulkStartTime.Value) : null,
                BulkShowTimeFields && BulkEndTime.HasValue   ? TimeOnly.FromTimeSpan(BulkEndTime.Value)   : null,
                dowFilter);

            IsBulkDialogOpen = false;
            StatusMessage = $"✅ Applied to {count} day(s).";
            ClearSelection();
            await RefreshCalendarAsync();
        }
        catch (Exception ex) { StatusMessage = $"❌ {ex.Message}"; }
    }

    private HashSet<DayOfWeek> BuildDowFilter()
    {
        var s = new HashSet<DayOfWeek>();
        if (BulkMonday)    s.Add(DayOfWeek.Monday);
        if (BulkTuesday)   s.Add(DayOfWeek.Tuesday);
        if (BulkWednesday) s.Add(DayOfWeek.Wednesday);
        if (BulkThursday)  s.Add(DayOfWeek.Thursday);
        if (BulkFriday)    s.Add(DayOfWeek.Friday);
        if (BulkSaturday)  s.Add(DayOfWeek.Saturday);
        if (BulkSunday)    s.Add(DayOfWeek.Sunday);
        return s;
    }

    // ── Clone to other employees ──────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenCloneDialogAsync()
    {
        if (SelectedEmployee == null) return;

        if (ViewMode == ScheduleViewMode.Week)
        {
            var (f, t) = CurrentWeekRange;
            CloneFrom = f; CloneTo = t;
        }
        else
        {
            CloneFrom = new DateOnly(Year, Month, 1);
            CloneTo   = new DateOnly(Year, Month, DateTime.DaysInMonth(Year, Month));
        }

        var targets = AvailableEmployees
            .Where(e => e.Id != SelectedEmployee.Id)
            .Select(e => new CloneTargetEmployee { Employee = e })
            .ToList();

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            CloneTargets.Clear();
            foreach (var t in targets) CloneTargets.Add(t);
        });

        IsCloneDialogOpen = true;
    }

    [RelayCommand]
    private void CloseCloneDialog() => IsCloneDialogOpen = false;

    [RelayCommand]
    private async Task ApplyCloneAsync()
    {
        if (SelectedEmployee == null || CloneFrom == null || CloneTo == null) return;

        var targetIds = CloneTargets.Where(t => t.IsSelected).Select(t => t.Employee.Id).ToList();
        if (targetIds.Count == 0)
        {
            StatusMessage = "Select at least one employee to copy to.";
            return;
        }

        try
        {
            var count = await _scheduleService.CopyToEmployeesAsync(
                SelectedEmployee.Id, CloneFrom.Value, CloneTo.Value, targetIds);

            IsCloneDialogOpen = false;
            StatusMessage = $"✅ Copied {count} schedule entry(ies) to {targetIds.Count} employee(s).";
        }
        catch (Exception ex) { StatusMessage = $"❌ {ex.Message}"; }
    }

    // ── Export ────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportMonthAsync()
    {
        if (SelectedEmployee == null) { StatusMessage = "Select an employee first."; return; }
        try
        {
            var schedules = await _scheduleService
                .GetMonthAsync(SelectedEmployee.Id, Year, Month).ConfigureAwait(false);
            var folder   = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var filePath = ExcelImportExportService.ExportMonthSchedule(
                SelectedEmployee.FullName, Year, Month, schedules, folder);
            StatusMessage = $"✅ Exported: {Path.GetFileName(filePath)}";
        }
        catch (Exception ex) { StatusMessage = $"❌ {ex.Message}"; }
    }

    // ── Import ────────────────────────────────────────────────────────────────

    public event EventHandler? ImportRequested;

    [RelayCommand]
    private void RequestImport() => ImportRequested?.Invoke(this, EventArgs.Empty);

    public async Task ImportFromFileAsync(string filePath)
    {
        if (SelectedEmployee == null || EditingBranch == null)
        { StatusMessage = "Select an employee and branch first."; return; }
        try
        {
            StatusMessage = "Parsing file…";
            var rows = ExcelImportExportService.ParseScheduleImportFile(filePath);
            if (rows.Count == 0) { StatusMessage = "No valid rows found."; return; }
            int saved = 0;
            foreach (var row in rows)
            {
                await _scheduleService.UpsertDayAsync(new ScheduleDayDto
                {
                    EmployeeId   = SelectedEmployee.Id,
                    BranchId     = EditingBranch.Id,
                    ScheduleDate = row.Date,
                    WorkType     = row.WorkType,
                    StartTime    = row.StartTime,
                    EndTime      = row.EndTime,
                    Comments     = row.Comments
                }).ConfigureAwait(false);
                saved++;
            }
            StatusMessage = $"✅ Imported {saved} day(s).";
            await RefreshCalendarAsync();
        }
        catch (Exception ex) { StatusMessage = $"❌ {ex.Message}"; }
    }

    // ── Submit to Ergani ──────────────────────────────────────────────────────

    /// <summary>Submits the selected days when any are selected; with no selection it submits
    /// the whole displayed month (the previous behaviour).</summary>
    [RelayCommand]
    private async Task SubmitScheduleToErganiAsync()
    {
        if (SelectedEmployee == null || _session?.CompanyId is not int companyId)
        { StatusMessage = "Select an employee first."; return; }
        try
        {
            var picked = _selectedDates.ToHashSet();
            var candidates = new List<ScheduleDayDto>();

            if (picked.Count > 0)
            {
                // A selected week can span two months — load each month once.
                foreach (var ym in picked.Select(d => (d.Year, d.Month)).Distinct())
                    candidates.AddRange(await _scheduleService.GetMonthAsync(SelectedEmployee.Id, ym.Year, ym.Month));
                candidates = candidates.Where(c => picked.Contains(c.ScheduleDate)).ToList();
            }
            else
            {
                candidates = await _scheduleService.GetMonthAsync(SelectedEmployee.Id, Year, Month);
            }

            await SubmitDaysAsync(companyId, candidates, picked.Count > 0 ? picked.Count : null);
        }
        catch (Exception ex) { StatusMessage = $"❌ {ex.Message}"; }
    }

    private async Task SubmitDaysAsync(int companyId, List<ScheduleDayDto> candidates, int? pickedCount)
    {
        var toSubmit = candidates
            .Where(c => !c.SubmittedToErgani && (c.WorkType is AppWorkType.Office or AppWorkType.Home))
            .ToList();

        var notes = new List<string>();
        var alreadySent = candidates.Count(c => c.SubmittedToErgani);
        var restAbsent  = candidates.Count(c => !c.SubmittedToErgani && (c.WorkType is AppWorkType.Rest or AppWorkType.Absent));
        var noSchedule  = pickedCount.HasValue ? pickedCount.Value - candidates.Count : 0;
        if (alreadySent > 0) notes.Add($"{alreadySent} already submitted");
        if (restAbsent  > 0) notes.Add($"{restAbsent} rest/absent (not sent to Ergani)");
        if (noSchedule  > 0) notes.Add($"{noSchedule} without a schedule");
        var skipped = notes.Count > 0 ? $" Skipped: {string.Join(", ", notes)}." : string.Empty;

        if (toSubmit.Count == 0)
        { StatusMessage = "Nothing to submit." + skipped; return; }

        StatusMessage = $"Submitting {toSubmit.Count} day(s) to Ergani…";

        var result = await _scheduleSubmitter.SubmitScheduleDaysAsync(companyId, toSubmit);

        if (result.Error != null)
            StatusMessage = $"❌ {result.Error}";
        else if (result.Failed == 0)
            StatusMessage = $"✅ Submitted {result.Submitted} day(s) to Ergani.{skipped}";
        else
        {
            var first = result.Days.First(d => !d.Success);
            StatusMessage = $"❌ {result.Submitted} submitted, {result.Failed} failed — " +
                            $"{first.Date:dd/MM}: {first.Error}. Open 📜 Log for the details.{skipped}";
        }

        await RefreshCalendarAsync();
    }

    // ── Submission log: Ergani responses + PDF ────────────────────────────────

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        if (SelectedEmployee == null || _session?.CompanyId is not int companyId)
        { StatusMessage = "Select an employee first."; return; }
        try
        {
            var entries = await _scheduleService.GetSubmissionLogAsync(companyId, SelectedEmployee.Id);

            LogRows.Clear();
            foreach (var e in entries)
                LogRows.Add(new ScheduleLogRow
                {
                    Id              = e.Id,
                    ScheduleDate    = e.ScheduleDate,
                    SubmissionDate  = e.SubmissionDate,
                    Success         = e.Success,
                    Protocol        = e.Protocol,
                    ErrorMessage    = e.ErrorMessage,
                    HttpStatusCode  = e.HttpStatusCode,
                    ResponseRawJson = e.ResponseRawJson
                });

            HasLogRows = LogRows.Count > 0;
            LogTitle   = $"📜 Schedule log — {SelectedEmployee.FullName}";
            LogStatus  = HasLogRows ? $"{LogRows.Count} submission(s), newest first." : string.Empty;
            IsLogOpen  = true;
        }
        catch (Exception ex) { StatusMessage = $"❌ {ex.Message}"; }
    }

    [RelayCommand]
    private void CloseLog() => IsLogOpen = false;

    [RelayCommand]
    private void ShowResponse(ScheduleLogRow row)
    {
        // A failure with no HTTP response (e.g. no connection) has only the error text.
        var text = !string.IsNullOrWhiteSpace(row.ResponseRawJson) ? row.ResponseRawJson : row.ErrorMessage;
        if (string.IsNullOrWhiteSpace(text))
        { LogStatus = "No stored Ergani response for this entry."; return; }

        ResponseTitle = $"Ergani response — {row.ScheduleDateText} ({row.SubmittedAtText})";
        ResponseText  = text;
        IsResponseOpen = true;
    }

    [RelayCommand]
    private void CloseResponse() => IsResponseOpen = false;

    [RelayCommand]
    private async Task DownloadPdfAsync(ScheduleLogRow row)
    {
        if (_session?.CompanyId is not int companyId || !row.HasProtocol)
        { LogStatus = "A protocol is required before downloading the PDF."; return; }
        try
        {
            var submittedDate = DateOnly.FromDateTime(row.SubmissionDate.ToLocalTime());
            var bytes = await _documentService.DownloadPdfAsync(
                companyId, "Documents/WTODaily", row.Protocol!, submittedDate);

            var folder   = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var who      = SelectedEmployee?.FullName ?? "employee";
            var safeName = string.Join("_", who.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            var day      = row.ScheduleDate?.ToString("yyyyMMdd") ?? "schedule";
            var path     = Path.Combine(folder, $"Ergani_Schedule_{safeName}_{day}_{row.Protocol}.pdf");

            await File.WriteAllBytesAsync(path, bytes);
            LogStatus = $"✅ PDF saved to Desktop: {Path.GetFileName(path)}";
        }
        catch (Exception ex) { LogStatus = $"❌ PDF download failed: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SubmitOvertimesAsync()
    {
        if (SelectedEmployee == null || _session?.CompanyId is not int companyId)
        { StatusMessage = "Select an employee first."; return; }
        try
        {
            StatusMessage = "Detecting overtime and submitting…";
            var schedules = await _scheduleService
                .GetMonthAsync(SelectedEmployee.Id, Year, Month).ConfigureAwait(false);
            var overtimeDays = schedules
                .Where(s => s.ActualArrival.HasValue && s.ActualDeparture.HasValue
                    && (s.ActualDeparture.Value - s.ActualArrival.Value).TotalHours > 8)
                .ToList();
            if (overtimeDays.Count == 0)
            { StatusMessage = "No overtime days detected this month (work > 8h)."; return; }

            var (count, error) = await _scheduleSubmitter
                .SubmitOvertimeDaysAsync(companyId, overtimeDays).ConfigureAwait(false);
            StatusMessage = error != null
                ? $"❌ {error} ({count} submitted before error)"
                : $"✅ Submitted {count} overtime record(s) to Ergani.";
        }
        catch (Exception ex) { StatusMessage = $"❌ {ex.Message}"; }
    }
}

/// <summary>Row in the Clone dialog — one per employee with a checkbox.</summary>
public partial class CloneTargetEmployee : ObservableObject
{
    public EmployeeDto Employee { get; init; } = null!;
    [ObservableProperty] private bool _isSelected;
    public string Name => Employee.FullName;
}
