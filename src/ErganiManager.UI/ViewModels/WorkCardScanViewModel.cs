using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ErganiManager.Core.Interfaces;
using ErganiManager.Core.Models;
using ErganiManager.LocalCache;
using ErganiManager.LocalCache.Entities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace ErganiManager.UI.ViewModels;

public class ScanResultRow
{
    public DateTime ScannedAt { get; init; }

    public string EmployeeName { get; init; } = string.Empty;

    public string MovementType { get; init; } = string.Empty;

    public bool Success { get; init; }

    public string Protocol { get; init; } = string.Empty;

    public string ErrorDescription { get; init; } = string.Empty;

    public string TimeText =>
        ScannedAt.ToString("HH:mm:ss");

    public string MovementIcon =>
        MovementType == "Arrival" ? "🟢" : "🔴";

    public string StatusIcon =>
        Success ? "✅" : "❌";

    public string StatusText =>
        Success
            ? $"Protocol: {Protocol}"
            : ErrorDescription;
}

public partial class WorkCardScanViewModel : ViewModelBase, IAdminSectionViewModel
{
    private readonly IWorkCardSubmitter _workCardSubmitter;
    private readonly IConnectionStateService _connectionState;

    private UserSession? _session;

    /*
     * The last failed submission is kept here so that the retry
     * dialog can submit exactly the same employee/movement again.
     */
    private WorkCardSubmissionRequest? _retryRequest;

    private static readonly TimeSpan ScanCooldown =
        TimeSpan.FromSeconds(20);

    private readonly Dictionary<string, DateTime> _lastScanTime = new();

    public ObservableCollection<ScanResultRow> RecentScans { get; } = new();

    /*
     * Ergani f_aitiologia codes.
     *
     * 001 = Problem with electricity / telecommunications
     * 002 = Problem with employer systems
     * 003 = Problem connecting to ERGANI
     *
     * The API expects the code itself, not the description.
     */
    public ObservableCollection<string> AvailableAitiologiai { get; } =
        new()
        {
            "001",
            "002",
            "003"
        };

    [ObservableProperty]
    private bool _hasActiveCompany;

    [ObservableProperty]
    private string _noCompanyMessage = string.Empty;

    [ObservableProperty]
    private string _barcodeInput = string.Empty;

    [ObservableProperty]
    private bool _isArrival = true;

    [ObservableProperty]
    private bool _autoDetect = true;

    [ObservableProperty]
    private bool _isProcessing;

    /*
     * Manual date/time override.
     * Only used when AutoDetect = false.
     */
    [ObservableProperty]
    private DateTimeOffset? _movementDate = DateTimeOffset.Now;

    [ObservableProperty]
    private TimeSpan? _movementTime =
        DateTime.Now.TimeOfDay;

    /*
     * Normal response
     */
    [ObservableProperty]
    private string _responseTitle = string.Empty;

    [ObservableProperty]
    private string _responseDetail = string.Empty;

    [ObservableProperty]
    private bool _responseSuccess;

    [ObservableProperty]
    private bool _hasResponse;

    /*
     * Retry dialog
     */
    [ObservableProperty]
    private bool _isRetryDialogOpen;

    [ObservableProperty]
    private string _retryEmployeeName = string.Empty;

    [ObservableProperty]
    private string _retryMovementType = string.Empty;

    [ObservableProperty]
    private DateTime _retryDateTime;

    [ObservableProperty]
    private string? _retryAitiologia;

    public WorkCardScanViewModel(
        IWorkCardSubmitter workCardSubmitter,
        IConnectionStateService connectionState)
    {
        _workCardSubmitter = workCardSubmitter;
        _connectionState = connectionState;
    }

    public void Initialize(UserSession session)
    {
        _session = session;

        HasActiveCompany = session.CompanyId.HasValue;

        NoCompanyMessage = session.CompanyId.HasValue
            ? string.Empty
            : "Select a company first.";
    }

    [RelayCommand]
    private async Task SubmitScanAsync()
    {
        if (_session?.CompanyId is not int companyId)
        {
            ShowResponse(
                false,
                "No Company",
                "Select a company before submitting a work card.");

            return;
        }

        var barcode = BarcodeInput.Trim();

        if (string.IsNullOrEmpty(barcode))
            return;

        /*
         * Prevent accidental duplicate scans.
         */
        if (_lastScanTime.TryGetValue(barcode, out var lastTime))
        {
            var elapsed = DateTime.Now - lastTime;

            if (elapsed < ScanCooldown)
            {
                var remaining =
                    (int)Math.Ceiling(
                        (ScanCooldown - elapsed).TotalSeconds);

                ShowResponse(
                    false,
                    "⏳ Already Scanned",
                    $"This badge was scanned {(int)elapsed.TotalSeconds}s ago.\n" +
                    $"Please wait {remaining} more second(s).");

                BarcodeInput = string.Empty;

                return;
            }
        }

        IsProcessing = true;
        HasResponse = false;

        try
        {
            /*
             * Resolve employee from local cache.
             */
            using var cache =
                LocalCacheDbContextFactory.Create();

            var employee = cache.CachedEmployees
                .FirstOrDefault(e =>
                    e.CompanyId == companyId &&
                    e.BarcodeId == barcode &&
                    e.IsActive);

            if (employee == null)
            {
                ShowResponse(
                    false,
                    "Unknown Badge",
                    $"No active employee found with barcode '{barcode}'.");

                return;
            }

            /*
             * Determine movement.
             */
            string movement;
            DateTime scanTime;

            if (AutoDetect)
            {
                var lastPending = cache.PendingSubmissions
                    .Where(p =>
                        p.EmployeeId == employee.Id &&
                        p.Synced)
                    .OrderByDescending(p => p.ScannedAt)
                    .FirstOrDefault();

                movement =
                    lastPending?.MovementType == "Arrival"
                        ? "Departure"
                        : "Arrival";

                scanTime = DateTime.Now;
            }
            else
            {
                movement =
                    IsArrival
                        ? "Arrival"
                        : "Departure";

                var date =
                    (MovementDate ?? DateTimeOffset.Now)
                    .LocalDateTime
                    .Date;

                var time =
                    MovementTime ??
                    DateTime.Now.TimeOfDay;

                scanTime = date + time;
            }

            /*
             * Build request once.
             *
             * We keep this same request if the submission fails
             * and the user chooses Retry.
             */
            var request = new WorkCardSubmissionRequest
            {
                EmployeeId = employee.Id,
                CompanyId = companyId,
                BranchId = employee.BranchId,
                MovementType = movement,
                MovementDateTime = scanTime
            };

            var result =
                await _workCardSubmitter.SubmitAsync(request);

            var name = employee.FullName;

            if (result.Success)
            {
                /*
                 * Store the successful scan in the local cache.
                 *
                 * This is important because PendingSubmissions is also used
                 * to determine the next movement (Arrival / Departure).
                 *
                 * A successful scan is kept in the cache as history and marked
                 * as already synchronized.
                 */

                var successfulSubmission = new PendingSubmission
                {
                    EmployeeId = employee.Id,
                    CompanyId = companyId,
                    BranchId = employee.BranchId,
                    EmployeeBarcodeId = employee.BarcodeId,
                    MovementType = movement,
                    ScannedAt = scanTime,
                    Synced = true,
                    SyncedAt = DateTime.UtcNow,
                    SyncAttempts = 0,
                    LastSyncError = null
                };

                cache.PendingSubmissions.Add(successfulSubmission);
                cache.SaveChanges();

                ShowResponse(
                    true,
                    $"✅ {movement.ToUpper()} — {name}",
                    $"Protocol:      {result.Protocol}\n" +
                    $"Submission ID: {result.SubmissionId}\n" +
                    $"Time:          {scanTime:HH:mm:ss dd/MM/yyyy}");

                RecentScans.Insert(
                    0,
                    new ScanResultRow
                    {
                        ScannedAt = scanTime,
                        EmployeeName = name,
                        MovementType = movement,
                        Success = true,
                        Protocol =
                            result.Protocol ?? string.Empty
                    });

                /*
                 * Successful scan — clear any previous retry state.
                 */
                ClearRetryState();
            }
            else
            {
                var error =
                    result.ErrorMessage ??
                    "Ergani unavailable.";

                ShowResponse(
                    false,
                    $"❌ Failed — {name}",
                    error);

                RecentScans.Insert(
                    0,
                    new ScanResultRow
                    {
                        ScannedAt = scanTime,
                        EmployeeName = name,
                        MovementType = movement,
                        Success = false,
                        ErrorDescription = error
                    });

                /*
                 * Save failed request and open retry dialog.
                 */
                OpenRetryDialog(
                    request,
                    name,
                    movement,
                    scanTime);
            }

            while (RecentScans.Count > 50)
                RecentScans.RemoveAt(
                    RecentScans.Count - 1);

            _lastScanTime[barcode] = DateTime.Now;

            BarcodeInput = string.Empty;
        }
        catch (Exception ex)
        {
            ShowResponse(
                false,
                "Error",
                ex.Message);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    /*
     * Opens the retry dialog after a failed submission.
     */
    private void OpenRetryDialog(
        WorkCardSubmissionRequest request,
        string employeeName,
        string movementType,
        DateTime movementDateTime)
    {
        _retryRequest = request;

        RetryEmployeeName = employeeName;
        RetryMovementType = movementType;
        RetryDateTime = movementDateTime;

        /*
         * Default to the first valid reason.
         */
        RetryAitiologia =
            AvailableAitiologiai.FirstOrDefault();

        IsRetryDialogOpen = true;
    }

    /*
     * Cancel retry.
     */
    [RelayCommand]
    private void CloseRetryDialog()
    {
        ClearRetryState();
    }

    /*
     * Retry the exact failed work-card submission,
     * now with f_aitiologia.
     */
    [RelayCommand]
    private async Task SubmitRetryAsync()
    {
        if (_retryRequest == null)
        {
            IsRetryDialogOpen = false;

            ShowResponse(
                false,
                "Retry Error",
                "There is no failed submission available to retry.");

            return;
        }

        if (string.IsNullOrWhiteSpace(RetryAitiologia))
        {
            ShowResponse(
                false,
                "Reason Required",
                "Please select an Ergani justification code.");

            return;
        }

        IsProcessing = true;

        try
        {
            var request = _retryRequest;

            /*
             * IWorkCardSubmitter already supports:
             *
             * SubmitAsync(request, aitiologia)
             */
            var result =
                await _workCardSubmitter.SubmitAsync(
                    request,
                    RetryAitiologia);

            if (result.Success)
            {
                ShowResponse(
                    true,
                    $"✅ Retry Successful — {RetryEmployeeName}",
                    $"Protocol:      {result.Protocol}\n" +
                    $"Submission ID: {result.SubmissionId}\n" +
                    $"Time:          {request.MovementDateTime:HH:mm:ss dd/MM/yyyy}\n" +
                    $"Reason:        {RetryAitiologia}");

                RecentScans.Insert(
                    0,
                    new ScanResultRow
                    {
                        ScannedAt =
                            request.MovementDateTime,

                        EmployeeName =
                            RetryEmployeeName,

                        MovementType =
                            RetryMovementType,

                        Success = true,

                        Protocol =
                            result.Protocol ??
                            string.Empty
                    });

                while (RecentScans.Count > 50)
                    RecentScans.RemoveAt(
                        RecentScans.Count - 1);

                ClearRetryState();
            }
            else
            {
                var error =
                    result.ErrorMessage ??
                    "Ergani rejected the retry submission.";

                ShowResponse(
                    false,
                    $"❌ Retry Failed — {RetryEmployeeName}",
                    error);

                /*
                 * Keep the retry dialog open so the user can
                 * select another reason and try again.
                 */
                IsRetryDialogOpen = true;
            }
        }
        catch (Exception ex)
        {
            ShowResponse(
                false,
                "Retry Error",
                ex.Message);

            IsRetryDialogOpen = true;
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void ClearRetryState()
    {
        _retryRequest = null;

        RetryEmployeeName = string.Empty;
        RetryMovementType = string.Empty;
        RetryDateTime = default;
        RetryAitiologia = null;

        IsRetryDialogOpen = false;
    }

    private void ShowResponse(
        bool success,
        string title,
        string detail)
    {
        ResponseSuccess = success;
        ResponseTitle = title;
        ResponseDetail = detail;
        HasResponse = true;
    }

    [RelayCommand]
    private void ClearResponse()
    {
        HasResponse = false;
        BarcodeInput = string.Empty;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        RecentScans.Clear();
    }
}