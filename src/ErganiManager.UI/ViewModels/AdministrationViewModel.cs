using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ErganiManager.Core.Interfaces;
using ErganiManager.Core.Models;
using System.Collections.ObjectModel;
using System.Linq;

namespace ErganiManager.UI.ViewModels;

/// <summary>Administrator tools: database backup, delete database, delete local cache.</summary>
public partial class AdministrationViewModel : ViewModelBase, IAdminSectionViewModel
{
    private enum PendingAction { None, DeleteCache, DeleteDatabase }

    private readonly IDatabaseMaintenanceService _maintenance;
    private PendingAction _pending = PendingAction.None;

    /// <summary>Raised after the database was deleted — the app returns to the setup wizard.</summary>
    public event EventHandler? DatabaseDeleted;

    [ObservableProperty] private string _databaseInfo = string.Empty;
    [ObservableProperty] private string _cacheInfo = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmText = string.Empty;

    private readonly IRoundingSettingsService _rounding;
    private readonly IPortalSettingsService _portal;

    // Schedule portal settings
    [ObservableProperty] private string _schedulePortalBaseUrl = string.Empty;
    [ObservableProperty] private string _schedulePortalTargetUrl = string.Empty;
    [ObservableProperty] private string _schedulePortalFromField = string.Empty;
    [ObservableProperty] private string _schedulePortalToField = string.Empty;
    [ObservableProperty] private string _schedulePortalDateFormat = string.Empty;
    [ObservableProperty] private bool _schedulePortalAutoSearch = true;
    [ObservableProperty] private bool _schedulePortalAutoDownload = true;
    [ObservableProperty] private string _schedulePortalSearchButton = string.Empty;
    [ObservableProperty] private string _schedulePortalDownloadFolder = string.Empty;
    [ObservableProperty] private string _schedulePortalStatus = string.Empty;

    // Work Cards portal settings
    [ObservableProperty] private string _workCardsPortalBaseUrl = string.Empty;
    [ObservableProperty] private string _workCardsPortalTargetUrl = string.Empty;
    [ObservableProperty] private string _workCardsPortalFromField = string.Empty;
    [ObservableProperty] private string _workCardsPortalToField = string.Empty;
    [ObservableProperty] private string _workCardsPortalDateFormat = string.Empty;
    [ObservableProperty] private bool _workCardsPortalAutoSearch = true;
    [ObservableProperty] private string _workCardsPortalSearchButton = string.Empty;
    [ObservableProperty] private string _workCardsPortalStatus = string.Empty;

    /// <summary>Editable rounding bands used by the "Rounded" column of the Declared vs actual report.</summary>
    public ObservableCollection<RoundingBandEdit> RoundingBands { get; } = new();
    [ObservableProperty] private string _roundingStatus = string.Empty;

    public AdministrationViewModel(IDatabaseMaintenanceService maintenance, IRoundingSettingsService rounding,
        IPortalSettingsService portal)
    {
        _maintenance = maintenance;
        _rounding = rounding;
        _portal = portal;
        var schedule = portal.Current.Schedule;
        var workCards = portal.Current.WorkCards;

        SchedulePortalBaseUrl = schedule.PortalBaseUrl;
        SchedulePortalTargetUrl = schedule.TargetPageUrl;
        SchedulePortalFromField = schedule.FromDateFieldId;
        SchedulePortalToField = schedule.ToDateFieldId;
        SchedulePortalDateFormat = schedule.DateFormat;
        SchedulePortalAutoSearch = schedule.AutoSearch;
        SchedulePortalAutoDownload = schedule.AutoDownload;
        SchedulePortalSearchButton = schedule.SearchButtonId;
        SchedulePortalDownloadFolder = schedule.DownloadFolder;

        WorkCardsPortalBaseUrl = workCards.PortalBaseUrl;
        WorkCardsPortalTargetUrl = workCards.TargetPageUrl;
        WorkCardsPortalFromField = workCards.FromDateFieldId;
        WorkCardsPortalToField = workCards.ToDateFieldId;
        WorkCardsPortalDateFormat = workCards.DateFormat;
        WorkCardsPortalAutoSearch = workCards.AutoSearch;
        WorkCardsPortalSearchButton = workCards.SearchButtonId;
        LoadRounding(_rounding.Current.Bands);
    }

    private void LoadRounding(System.Collections.Generic.IEnumerable<RoundingBand> bands)
    {
        RoundingBands.Clear();
        foreach (var b in bands.OrderBy(b => b.FromMinute))
            RoundingBands.Add(new RoundingBandEdit(b.FromMinute, b.ToMinute, b.RoundToMinute));
    }

    [RelayCommand]
    private void SaveSchedulePortal()
    {
        try
        {
            var schedule = BuildScheduleProfile();
            if (!ValidatePortalUrl(schedule.PortalBaseUrl, "Schedule", out var error))
            {
                SchedulePortalStatus = error;
                return;
            }

            var current = _portal.Current;
            current.Schedule = schedule;
            _portal.Save(current);
            SchedulePortalStatus = "✅ Schedule portal settings saved.";
        }
        catch (Exception ex)
        {
            SchedulePortalStatus = $"❌ Could not save Schedule portal settings: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveWorkCardsPortal()
    {
        try
        {
            var workCards = BuildWorkCardsProfile();
            if (!ValidatePortalUrl(workCards.PortalBaseUrl, "Work Cards", out var error))
            {
                WorkCardsPortalStatus = error;
                return;
            }

            var current = _portal.Current;
            current.WorkCards = workCards;
            _portal.Save(current);
            WorkCardsPortalStatus = "✅ Work Cards portal settings saved.";
        }
        catch (Exception ex)
        {
            WorkCardsPortalStatus = $"❌ Could not save Work Cards portal settings: {ex.Message}";
        }
    }

    private ErganiPortalProfile BuildScheduleProfile() => new()
    {
        PortalBaseUrl = (SchedulePortalBaseUrl ?? string.Empty).Trim(),
        TargetPageUrl = (SchedulePortalTargetUrl ?? string.Empty).Trim(),
        FromDateFieldId = (SchedulePortalFromField ?? string.Empty).Trim(),
        ToDateFieldId = (SchedulePortalToField ?? string.Empty).Trim(),
        DateFormat = string.IsNullOrWhiteSpace(SchedulePortalDateFormat) ? "dd/MM/yyyy" : SchedulePortalDateFormat.Trim(),
        AutoSearch = SchedulePortalAutoSearch,
        AutoDownload = SchedulePortalAutoDownload,
        SearchButtonId = (SchedulePortalSearchButton ?? string.Empty).Trim(),
        DownloadFolder = (SchedulePortalDownloadFolder ?? string.Empty).Trim()
    };

    private ErganiPortalProfile BuildWorkCardsProfile() => new()
    {
        PortalBaseUrl = (WorkCardsPortalBaseUrl ?? string.Empty).Trim(),
        TargetPageUrl = (WorkCardsPortalTargetUrl ?? string.Empty).Trim(),
        FromDateFieldId = (WorkCardsPortalFromField ?? string.Empty).Trim(),
        ToDateFieldId = (WorkCardsPortalToField ?? string.Empty).Trim(),
        DateFormat = string.IsNullOrWhiteSpace(WorkCardsPortalDateFormat) ? "dd/MM/yyyy" : WorkCardsPortalDateFormat.Trim(),
        AutoSearch = WorkCardsPortalAutoSearch,
        AutoDownload = false,
        SearchButtonId = (WorkCardsPortalSearchButton ?? string.Empty).Trim()
    };

    private static bool ValidatePortalUrl(string value, string name, out string message)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            message = $"❌ {name} portal address must be a full web address.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    [RelayCommand]
    private void AddRoundingBand() => RoundingBands.Add(new RoundingBandEdit(0, 0, 0));

    [RelayCommand]
    private void RemoveRoundingBand(RoundingBandEdit? band)
    {
        if (band != null) RoundingBands.Remove(band);
    }

    [RelayCommand]
    private void RestoreRoundingDefaults()
    {
        LoadRounding(RoundingRules.Defaults());
        RoundingStatus = "Defaults loaded — press Save to apply them.";
    }

    [RelayCommand]
    private void SaveRounding()
    {
        var bands = RoundingBands
            .Select(b => new RoundingBand { FromMinute = (int)(b.From ?? 0), ToMinute = (int)(b.To ?? 0), RoundToMinute = (int)(b.RoundTo ?? 0) })
            .OrderBy(b => b.FromMinute).ToList();

        foreach (var b in bands)
        {
            if (b.FromMinute < 0 || b.ToMinute > 59 || b.FromMinute > b.ToMinute)
            { RoundingStatus = $"❌ Invalid range {b.FromMinute}–{b.ToMinute}: minutes must be 0–59 and From ≤ To."; return; }
            if (b.RoundToMinute is < 0 or > 60)
            { RoundingStatus = "❌ 'Round to' must be between 0 and 60 minutes."; return; }
        }
        for (var i = 1; i < bands.Count; i++)
            if (bands[i].FromMinute <= bands[i - 1].ToMinute)
            { RoundingStatus = $"❌ Ranges overlap: {bands[i - 1].FromMinute}–{bands[i - 1].ToMinute} and {bands[i].FromMinute}–{bands[i].ToMinute}."; return; }

        try
        {
            _rounding.Save(new RoundingRules { Bands = bands });
            RoundingStatus = "✅ Rounding rules saved. Generate the report again to see them.";
        }
        catch (Exception ex) { RoundingStatus = $"❌ {ex.Message}"; }
    }

    public string SuggestedBackupName => _maintenance.SuggestBackupFileName();

    public void Initialize(UserSession session) => Refresh();

    private void Refresh()
    {
        DatabaseInfo = _maintenance.DescribeDatabase();
        try
        {
            var s = _maintenance.GetLocalCacheStatus();
            CacheInfo = $"Unsynced scans: {s.PendingScans} · Unresolved failed submissions: {s.FailedSubmissions}";
        }
        catch (Exception ex) { CacheInfo = $"Cache unreadable: {ex.Message}"; }
    }

    /// <summary>Called by the view after the user picked a destination file.</summary>
    public async Task BackupToAsync(string path)
    {
        IsBusy = true;
        try { StatusMessage = "✅ " + await _maintenance.BackupAsync(path); }
        catch (Exception ex) { StatusMessage = $"❌ Backup failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void AskDeleteCache()
    {
        _pending = PendingAction.DeleteCache;
        string extra;
        try
        {
            var s = _maintenance.GetLocalCacheStatus();
            extra = s.PendingScans + s.FailedSubmissions > 0
                ? $" It still holds {s.PendingScans} unsynced scan(s) and {s.FailedSubmissions} unresolved failed submission(s); a .bak copy of the file is kept."
                : string.Empty;
        }
        catch { extra = string.Empty; }
        ConfirmText = "Delete the temporary local database? It is recreated empty automatically." + extra;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void AskDeleteDatabase()
    {
        _pending = PendingAction.DeleteDatabase;
        ConfirmText = "PERMANENTLY DELETE the main database (" + DatabaseInfo + ")? " +
                      "All companies, employees, schedules and logs are lost. Make a backup first. " +
                      "The setup wizard opens afterwards.";
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelConfirm() { _pending = PendingAction.None; IsConfirmOpen = false; }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        var action = _pending;
        _pending = PendingAction.None;
        IsConfirmOpen = false;
        IsBusy = true;
        try
        {
            switch (action)
            {
                case PendingAction.DeleteCache:
                    var bak = _maintenance.DeleteLocalCache();
                    StatusMessage = "✅ Local cache deleted" + (bak != null ? $" (backup: {bak})." : ".") +
                                    " It is recreated on next use — use 🔄 Sync Cache to refill it.";
                    break;

                case PendingAction.DeleteDatabase:
                    await _maintenance.DeleteDatabaseAsync();
                    StatusMessage = "✅ Database deleted.";
                    DatabaseDeleted?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        catch (Exception ex) { StatusMessage = $"❌ {ex.Message}"; }
        finally { IsBusy = false; Refresh(); }
    }
}
