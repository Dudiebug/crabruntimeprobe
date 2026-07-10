using CrabRuntimeProbe.Dashboard.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace CrabRuntimeProbe.Dashboard;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly string? _fixture;
    private readonly bool _demo;
    private readonly DashboardStateStore _store = new();
    private readonly SteamGameLocator _gameLocator = new();
    private readonly DashboardResourceLocator _resourceLocator = new();
    private readonly LiveStatusReader _statusReader = new();
    private readonly CapabilityReadinessService _readinessService = new();
    private readonly PlayGuideReducer _playGuideReducer = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CampaignService? _campaignService;
    private LocalCampaignState? _campaign;
    private LiveStatusReadResult _status = new(
        LiveStatusSnapshot.Empty, false, true, false, "Waiting for RuntimeProbe status.", DateTimeOffset.UtcNow);
    private string _campaignName = "CrabSync Full Observe";
    private string _gameDirectory = string.Empty;
    private CampaignRole _selectedRole = CampaignRole.Host;
    private string _activity = "Loading campaign resources...";
    private string _lastBundle = string.Empty;
    private bool _needsCoverageOnly = true;
    private bool _initialized;
    private bool _processWasSeen;
    private bool _localGameRunning;
    private bool _autoCollected;
    private bool _stopRequested;
    private Process? _monitoredProcess;
    private CoverageRow? _selectedCoverage;
    private IReadOnlyList<ChecklistDefinition> _checklistDefinitions = ChecklistCatalog.All;
    private IReadOnlyList<PlayGuideCategory> _allPlayGuideCategories = Array.Empty<PlayGuideCategory>();
    private PlayGuideFilter _playGuideFilter = PlayGuideFilter.ToDo;

    public MainViewModel(string? fixture, bool demo)
    {
        _fixture = fixture;
        _demo = demo;
        Checklist = new ObservableCollection<ChecklistViewItem>();
        PlayGuideCategories = new ObservableCollection<PlayGuideCategory>();
        Coverage = new ObservableCollection<CoverageRow>();
        Readiness = new ObservableCollection<CapabilityReadiness>();
        ChecklistView = CollectionViewSource.GetDefaultView(Checklist);
        ChecklistView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ChecklistViewItem.Group)));
        CoverageView = CollectionViewSource.GetDefaultView(Coverage);
        CoverageView.Filter = FilterCoverage;

        InitializeCommand = new AsyncRelayCommand(InitializeAsync, () => !_initialized);
        BrowseGameCommand = new RelayCommand(BrowseGame);
        DetectGameCommand = new RelayCommand(DetectGame);
        PrepareAndStartCommand = new AsyncRelayCommand(PrepareAndStartAsync);
        PrepareCommand = new AsyncRelayCommand(PrepareAsync);
        StartMonitoringCommand = new AsyncRelayCommand(StartMonitoringAsync);
        OpenGameCommand = new AsyncRelayCommand(OpenGameAsync);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _campaign is not null);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => _campaign is not null);
        CombineCommand = new AsyncRelayCommand(CombineAsync);
        ResetCommand = new AsyncRelayCommand(ResetAsync);
        OpenLastBundleCommand = new RelayCommand(OpenLastBundle, () => File.Exists(_lastBundle));
        ViewDiagnosticsCommand = new RelayCommand(ViewDiagnostics);
        SupportSummaryCommand = new RelayCommand(CopySupportSummary);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ChecklistViewItem> Checklist { get; }
    public ObservableCollection<PlayGuideCategory> PlayGuideCategories { get; }
    public ObservableCollection<CoverageRow> Coverage { get; }
    public ObservableCollection<CapabilityReadiness> Readiness { get; }
    public ICollectionView ChecklistView { get; }
    public ICollectionView CoverageView { get; }
    public IReadOnlyList<CampaignRole> RoleOptions { get; } = new[] { CampaignRole.Host, CampaignRole.JoinedClient };

    public ICommand InitializeCommand { get; }
    public ICommand BrowseGameCommand { get; }
    public ICommand DetectGameCommand { get; }
    public ICommand PrepareAndStartCommand { get; }
    public ICommand PrepareCommand { get; }
    public ICommand StartMonitoringCommand { get; }
    public ICommand OpenGameCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CombineCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand OpenLastBundleCommand { get; }
    public ICommand ViewDiagnosticsCommand { get; }
    public ICommand SupportSummaryCommand { get; }

    public string CampaignName
    {
        get => _campaignName;
        set
        {
            if (Set(ref _campaignName, value)) _ = SavePreferencesAsync();
        }
    }

    public string GameDirectory
    {
        get => _gameDirectory;
        set
        {
            if (Set(ref _gameDirectory, value)) _ = SavePreferencesAsync();
        }
    }

    public CampaignRole SelectedRole
    {
        get => _selectedRole;
        set
        {
            if (!Set(ref _selectedRole, value)) return;
            Raise(nameof(IsHosting));
            Raise(nameof(IsJoiningFriend));
            Raise(nameof(RoleSummary));
            RefreshPlayGuide();
            _ = SavePreferencesAsync();
        }
    }

    public bool IsHosting
    {
        get => SelectedRole == CampaignRole.Host;
        set
        {
            if (value) SelectedRole = CampaignRole.Host;
            else if (SelectedRole == CampaignRole.Host) Raise();
        }
    }

    public bool IsJoiningFriend
    {
        get => SelectedRole == CampaignRole.JoinedClient;
        set
        {
            if (value) SelectedRole = CampaignRole.JoinedClient;
            else if (SelectedRole == CampaignRole.JoinedClient) Raise();
        }
    }

    public PlayGuideFilter SelectedPlayGuideFilter
    {
        get => _playGuideFilter;
        private set
        {
            if (!Set(ref _playGuideFilter, value)) return;
            Raise(nameof(IsPlayGuideToDoFilter));
            Raise(nameof(IsPlayGuideAllFilter));
            Raise(nameof(IsPlayGuideCompletedFilter));
            ApplyPlayGuideFilter();
        }
    }

    public bool IsPlayGuideToDoFilter
    {
        get => SelectedPlayGuideFilter == PlayGuideFilter.ToDo;
        set
        {
            if (value) SelectedPlayGuideFilter = PlayGuideFilter.ToDo;
            else if (SelectedPlayGuideFilter == PlayGuideFilter.ToDo) Raise();
        }
    }

    public bool IsPlayGuideAllFilter
    {
        get => SelectedPlayGuideFilter == PlayGuideFilter.All;
        set
        {
            if (value) SelectedPlayGuideFilter = PlayGuideFilter.All;
            else if (SelectedPlayGuideFilter == PlayGuideFilter.All) Raise();
        }
    }

    public bool IsPlayGuideCompletedFilter
    {
        get => SelectedPlayGuideFilter == PlayGuideFilter.Completed;
        set
        {
            if (value) SelectedPlayGuideFilter = PlayGuideFilter.Completed;
            else if (SelectedPlayGuideFilter == PlayGuideFilter.Completed) Raise();
        }
    }

    public string Activity { get => _activity; private set => Set(ref _activity, value); }
    public string LastBundle { get => _lastBundle; private set { if (Set(ref _lastBundle, value)) RaiseCommands(); } }
    public LocalCampaignState? Campaign
    {
        get => _campaign;
        private set
        {
            if (!Set(ref _campaign, value)) return;
            RaiseCommands();
            Raise(nameof(CampaignIdentitySummary));
            Raise(nameof(ElapsedSummary));
        }
    }
    public LiveStatusReadResult Status { get => _status; private set { if (Set(ref _status, value)) RaiseStatusProperties(); } }
    public LiveStatusSnapshot Snapshot => Status.Snapshot;
    public string RoleSummary => $"selected {SelectedRole.ToContract()} - observed {Snapshot.ObservedRole} - {Snapshot.AuthorityStatus}";
    public string LifecycleSummary => $"{Snapshot.Lifecycle.State} - generation {Snapshot.Lifecycle.Generation} - {Snapshot.Lifecycle.Context}";
    public string RuntimeSummary
    {
        get
        {
            var game = _localGameRunning || Snapshot.Runtime.GameProcessRunning
                ? "running" : Snapshot.Runtime.GameProcessState;
            var ue4ss = Snapshot.Runtime.Ue4ssState is "unknown" or ""
                ? Status.HasSnapshot ? "observed by heartbeat" : "unknown"
                : Snapshot.Runtime.Ue4ssState;
            var probe = Status.HasSnapshot
                ? Status.IsStale ? "heartbeat stale" : "heartbeat active"
                : Snapshot.Runtime.RuntimeProbeState;
            return $"game {game} - UE4SS {ue4ss} - probe {probe}";
        }
    }
    public string EvidenceSummary => $"{Snapshot.EvidenceHealth.State} - canonical {Snapshot.EvidenceHealth.CanonicalRows} - rejected {Snapshot.EvidenceHealth.RejectedRows}";
    public string HeartbeatSummary => Status.HasSnapshot
        ? $"sequence {Snapshot.Sequence} - heartbeat {Snapshot.HeartbeatAtUtc:HH:mm:ss} UTC{(Status.IsStale ? " - STALE" : string.Empty)}"
        : "No valid status snapshot yet";
    public string ChecklistSummary => $"{Checklist.Count(item => item.IsComplete)} / {Checklist.Count} checklist observations complete";
    public string PlayGuideOverallSummary
    {
        get
        {
            var actions = _allPlayGuideCategories.SelectMany(category => category.Actions).ToArray();
            return $"{actions.Count(action => action.IsDone)} of {actions.Length} play actions done";
        }
    }
    public double PlayGuideOverallPercentage
    {
        get
        {
            var actions = _allPlayGuideCategories.SelectMany(category => category.Actions).ToArray();
            return actions.Length == 0 ? 0 : Math.Round(actions.Count(action => action.IsDone) * 100d / actions.Length);
        }
    }
    public string PlayGuideFilterSummary => SelectedPlayGuideFilter switch
    {
        PlayGuideFilter.Completed => "Showing completed actions",
        PlayGuideFilter.All => "Showing all actions",
        _ => "Showing actions that still need attention"
    };
    public string PlayGuideEmptyMessage => PlayGuideCategories.Count == 0
        ? SelectedPlayGuideFilter == PlayGuideFilter.Completed
            ? "No completed actions yet—play normally and this view will update on its own."
            : "No actions match this filter."
        : string.Empty;
    public string CoverageSummary => $"{Coverage.Count(row => !row.NeedsCoverage)} / {Coverage.Count} catalog rows terminal - {Coverage.Count(row => row.NeedsCoverage)} need coverage";
    public string CampaignIdentitySummary
    {
        get
        {
            var campaignId = string.IsNullOrWhiteSpace(Snapshot.CampaignId)
                ? Campaign?.CampaignId ?? "no-campaign" : Snapshot.CampaignId;
            var sessionId = string.IsNullOrWhiteSpace(Snapshot.SessionId)
                ? Campaign?.SessionId ?? "no-session" : Snapshot.SessionId;
            return $"{campaignId} / {sessionId}";
        }
    }
    public string ProbeStageSummary => string.IsNullOrWhiteSpace(Snapshot.Runtime.CurrentProbeStage)
        ? "idle" : Snapshot.Runtime.CurrentProbeStage;
    public string ElapsedSummary
    {
        get
        {
            var start = Campaign?.PreparedAtUtc ?? Snapshot.WrittenAtUtc;
            if (start == DateTimeOffset.MinValue) return "not started";
            var elapsed = DateTimeOffset.UtcNow - start;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
    }
    public string ChecklistCompletionSummary
    {
        get
        {
            var applicable = Checklist.Count(item => item.State != ChecklistDisplayState.NotApplicable);
            var percent = applicable == 0 ? 0 : Math.Round(Checklist.Count(item => item.IsComplete) * 100d / applicable, 1);
            return $"{percent:0.0}% overall";
        }
    }
    public string CrashBadge => Snapshot.CrashSuspected ? "CRASH-SUSPECT" : "Crash-suspect: no";
    public string DirtyBadge => Snapshot.DirtyEvidence || Status.UsedLastGood ? "DIRTY EVIDENCE" : "Evidence dirty: no";
    public bool IsDirtyEvidence => Snapshot.DirtyEvidence || Status.UsedLastGood;
    public string IncompleteBadge => $"NEEDS COVERAGE: {Coverage.Count(row => row.NeedsCoverage)}";
    public string InventoryDepthSummary => $"depth {Snapshot.Safety.InventoryDepth}";
    public string CircuitBreakerSummary => Snapshot.Safety.CircuitBreakers.Count == 0
        ? "No circuit-breaker report"
        : string.Join("  |  ", Snapshot.Safety.CircuitBreakers.Select(pair => $"{pair.Key}: {pair.Value}"));
    public bool NeedsCoverageOnly
    {
        get => _needsCoverageOnly;
        set
        {
            if (!Set(ref _needsCoverageOnly, value)) return;
            CoverageView.Refresh();
            _ = SavePreferencesAsync();
        }
    }
    public CoverageRow? SelectedCoverage { get => _selectedCoverage; set => Set(ref _selectedCoverage, value); }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var preferences = await _store.LoadPreferencesAsync(_lifetime.Token);
            _campaignName = string.IsNullOrWhiteSpace(preferences.CampaignName)
                ? "CrabSync Full Observe" : preferences.CampaignName;
            _selectedRole = preferences.SelectedRole == CampaignRole.Unknown ? CampaignRole.Host : preferences.SelectedRole;
            _gameDirectory = preferences.GameDirectory;
            _needsCoverageOnly = preferences.NeedsCoverageOnly;
            Raise(nameof(CampaignName));
            Raise(nameof(SelectedRole));
            Raise(nameof(IsHosting));
            Raise(nameof(IsJoiningFriend));
            Raise(nameof(GameDirectory));
            Raise(nameof(NeedsCoverageOnly));
            CoverageView.Refresh();

            var resources = _resourceLocator.Locate();
            _campaignService = new CampaignService(_store, _resourceLocator);
            var definitions = await new ChecklistDefinitionLoader()
                .LoadAuthoritativeOrFallbackAsync(resources, _lifetime.Token);
            _checklistDefinitions = definitions;
            var catalogPath = Path.Combine(resources.CampaignRoot, "crabsync_coverage_catalog.json");
            if (!File.Exists(catalogPath)) throw new FileNotFoundException("Authoritative coverage catalog is missing.", catalogPath);
            Replace(Coverage, await new CoverageCatalogReader().ReadAsync(catalogPath, _lifetime.Token));
            Replace(Readiness, _readinessService.Calculate(Coverage));
            SelectedCoverage = Coverage.FirstOrDefault(row => row.NeedsCoverage) ?? Coverage.FirstOrDefault();

            Campaign = await _store.LoadCampaignAsync(_lifetime.Token);
            if (Campaign is not null)
            {
                _campaignName = Campaign.CampaignName;
                _selectedRole = Campaign.Role;
                _gameDirectory = Campaign.GameDirectory;
                _lastBundle = Campaign.LastBundlePath;
                Raise(nameof(CampaignName));
                Raise(nameof(SelectedRole));
                Raise(nameof(IsHosting));
                Raise(nameof(IsJoiningFriend));
                Raise(nameof(GameDirectory));
                Raise(nameof(LastBundle));
            }
            else if (string.IsNullOrWhiteSpace(GameDirectory))
            {
                var detected = _gameLocator.Detect().FirstOrDefault();
                if (detected is not null) GameDirectory = detected.InstallDirectory;
            }
            var localInstallation = _gameLocator.ValidateSelectedDirectory(GameDirectory);
            _localGameRunning = localInstallation is not null && new GameProcessService().IsRunning(localInstallation);

            if (_demo)
            {
                var snapshot = _statusReader.Parse(DemoStatus.Json, "embedded-demo");
                Status = new LiveStatusReadResult(snapshot, true, false, false, string.Empty, DateTimeOffset.UtcNow);
            }
            else if (!string.IsNullOrWhiteSpace(_fixture))
            {
                Status = Directory.Exists(_fixture)
                    ? await _statusReader.ReadLatestAsync(_fixture, cancellationToken: _lifetime.Token)
                    : new LiveStatusReadResult(
                        await _statusReader.ParseFileAsync(_fixture, _lifetime.Token), true, false, false,
                        string.Empty, DateTimeOffset.UtcNow);
            }
            else if (Campaign is not null)
            {
                Status = await _statusReader.ReadLatestAsync(Campaign.StatusDirectory, cancellationToken: _lifetime.Token);
            }

            RefreshChecklist(Status.Snapshot);
            Activity = _demo ? "Demo mode - no game files are changed" : "Ready - passive observation only";
            RaiseSummaryProperties();
            _ = MonitorAsync(definitions, _lifetime.Token);
        }
        catch (Exception ex)
        {
            Activity = "Initialization failed";
            ShowError(ex);
        }
    }

    private async Task PrepareAndStartAsync()
    {
        await PrepareAsync();
        if (Campaign is not null) await StartMonitoringAsync();
    }

    private async Task PrepareAsync()
    {
        try
        {
            var installation = RequireInstallation();
            Activity = "Installing the read-only payload and preparing a fresh generation...";
            Campaign = await RequireCampaignService().PrepareAsync(
                installation, SelectedRole, CampaignName,
                dashboardExecutablePath: Environment.ProcessPath,
                cancellationToken: _lifetime.Token);
            _autoCollected = false;
            _stopRequested = false;
            Activity = "Prepared - start Crab Champions when both computers are ready";
            await SavePreferencesAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task StartMonitoringAsync()
    {
        try
        {
            if (Campaign is null)
                Campaign = await RequireCampaignService().ResumeAsync(_lifetime.Token)
                           ?? throw new InvalidOperationException("Prepare a campaign first.");
            var installation = RequireInstallation();
            var process = new GameProcessService().Launch(installation);
            _monitoredProcess = process;
            _processWasSeen = true;
            _localGameRunning = true;
            Campaign = await RequireCampaignService().MarkMonitoringAsync(Campaign, _lifetime.Token);
            Activity = $"Monitoring Crab Champions process {process.Id} - make only natural gameplay actions";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private Task OpenGameAsync() => StartMonitoringAsync();

    public async Task AttachToRunningGameAsync()
    {
        try
        {
            if (_demo || !string.IsNullOrWhiteSpace(_fixture)) return;
            if (Campaign is null)
            {
                Activity = "Crab Champions opened the dashboard - choose a role and prepare the play guide once";
                return;
            }

            var installation = RequireInstallation();
            var process = new GameProcessService().FindRunning(installation);
            if (process is null)
            {
                Activity = "The dashboard was started by Crab Champions, but the game process is not visible yet";
                return;
            }

            _monitoredProcess = process;
            _processWasSeen = true;
            _localGameRunning = true;
            Campaign = await RequireCampaignService().MarkMonitoringAsync(Campaign, _lifetime.Token);
            Activity = $"Crab Champions opened the dashboard - monitoring process {process.Id}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task ResumeAsync()
    {
        try
        {
            Campaign = await RequireCampaignService().ResumeAsync(_lifetime.Token)
                       ?? throw new InvalidOperationException("No valid prepared campaign is available to resume.");
            SelectedRole = Campaign.Role;
            CampaignName = Campaign.CampaignName;
            GameDirectory = Campaign.GameDirectory;
            _stopRequested = false;
            Activity = "Campaign resumed - resume marker written; start monitoring when ready";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task StopAsync()
    {
        if (Campaign is null) return;
        try
        {
            await RequireCampaignService().RequestStopAsync(Campaign, _lifetime.Token);
            _stopRequested = true;
            Activity = "Stop requested - RuntimeProbe will flush evidence at a safe boundary";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task ExportAsync()
    {
        if (Campaign is null) return;
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to save the evidence bundle",
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(LastBundle))
                ? Path.GetDirectoryName(LastBundle)! : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await RequireCampaignService().RequestStopAsync(Campaign, _lifetime.Token);
            _stopRequested = true;
            Activity = "Finishing observation, validating, and hashing evidence...";
            await Task.Delay(TimeSpan.FromMilliseconds(500), _lifetime.Token);
            var result = await new EvidenceCollector().CollectAsync(
                Campaign, dialog.FolderName, cancellationToken: _lifetime.Token);
            LastBundle = result.ZipPath;
            Campaign = Campaign with { LastBundlePath = result.ZipPath, UpdatedAtUtc = DateTimeOffset.UtcNow };
            await _store.SaveCampaignAsync(Campaign, _lifetime.Token);
            Activity = result.DirtyEvidence
                ? "Finished - bundle exported with explicit dirty/omission markers"
                : "Finished - clean evidence bundle exported";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task CombineAsync()
    {
        var files = new OpenFileDialog
        {
            Title = "Select host and joined-client evidence bundles",
            Filter = "Evidence bundles (*.zip)|*.zip",
            Multiselect = true
        };
        if (files.ShowDialog() != true || files.FileNames.Length < 2)
        {
            if (files.FileNames.Length == 1)
                MessageBox.Show("Select at least two bundles.", "Combine evidence", MessageBoxButton.OK,
                    MessageBoxImage.Information);
            return;
        }
        var output = new OpenFolderDialog { Title = "Choose where to save the combined report" };
        if (output.ShowDialog() != true) return;
        try
        {
            Activity = "Verifying both manifests and correlating capture intervals...";
            var result = await new BundleCorrelationService().CombineAsync(files.FileNames, output.FolderName,
                _lifetime.Token);
            LastBundle = result.ZipPath;
            Activity = result.CorrelationEstablished
                ? "Clean host/joined-client correlation established"
                : "Combined report created - one or more compatibility/evidence gates remain";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task ResetAsync()
    {
        if (MessageBox.Show(
                "Reset dashboard campaign state and transient control markers? Canonical evidence will not be deleted.",
                "Reset dashboard state", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            await RequireCampaignService().ResetAsync(Campaign, _lifetime.Token);
            Campaign = null;
            _stopRequested = false;
            _monitoredProcess = null;
            Status = new LiveStatusReadResult(LiveStatusSnapshot.Empty, false, true, false,
                "No active campaign", DateTimeOffset.UtcNow);
            RefreshChecklist(Status.Snapshot);
            Activity = "Dashboard campaign state reset - canonical evidence was not deleted";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task MonitorAsync(IReadOnlyList<ChecklistDefinition> definitions, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(token))
        {
            if (_demo || !string.IsNullOrWhiteSpace(_fixture) || Campaign is null) continue;
            try
            {
                var next = await _statusReader.ReadLatestAsync(Campaign.StatusDirectory, cancellationToken: token);
                var installation = _gameLocator.ValidateSelectedDirectory(GameDirectory);
                var running = installation is not null && new GameProcessService().IsRunning(installation);
                _localGameRunning = running;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Status = next;
                    RefreshChecklist(next.Snapshot);
                    RaiseSummaryProperties();
                });

                if (installation is null) continue;
                if (running) _processWasSeen = true;
                if (_processWasSeen && !running && !_autoCollected && Campaign.Phase == "monitoring")
                {
                    _autoCollected = true;
                    var nonZeroExit = false;
                    try
                    {
                        nonZeroExit = _monitoredProcess is { HasExited: true } && _monitoredProcess.ExitCode != 0;
                    }
                    catch (InvalidOperationException) { }
                    var abnormal = nonZeroExit || next.Snapshot.CrashSuspected || next.Snapshot.DirtyEvidence
                                   || (!_stopRequested && next.IsStale);
                    var export = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "CrabRuntimeProbe Evidence");
                    var result = await new EvidenceCollector().CollectAsync(
                        Campaign, export, abnormalProcessExit: abnormal,
                        cancellationToken: token);
                    var finalized = Campaign with
                    {
                        LastBundlePath = result.ZipPath,
                        Phase = "collected",
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                    await _store.SaveCampaignAsync(finalized, token);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        Campaign = finalized;
                        LastBundle = result.ZipPath;
                        Activity = result.CrashSuspected
                            ? "Game exited unexpectedly - crash-suspect bundle exported"
                            : "Game exited - evidence bundle exported automatically";
                    });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                await Application.Current.Dispatcher.InvokeAsync(() => Activity = $"Monitor warning: {ex.Message}");
            }
        }
    }

    private void BrowseGame()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the Crab Champions installation directory",
            InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory : string.Empty
        };
        if (dialog.ShowDialog() == true) GameDirectory = dialog.FolderName;
    }

    private void DetectGame()
    {
        var detected = _gameLocator.Detect().FirstOrDefault();
        if (detected is null)
        {
            Activity = "Steam app 774801 was not detected; choose the installation manually";
            return;
        }
        GameDirectory = detected.InstallDirectory;
        Activity = "Crab Champions installation detected from Steam";
    }

    private void OpenLastBundle()
    {
        if (!File.Exists(LastBundle)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastBundle}\"") { UseShellExecute = true });
    }

    private void CopySupportSummary()
    {
        var summary = SupportSummary.Create(Campaign, Status);
        Clipboard.SetText(summary);
        Activity = "Support summary copied to the clipboard (anonymous runtime state only)";
    }

    private void ViewDiagnostics()
    {
        var readiness = string.Join(Environment.NewLine,
            Readiness.Select(item => $"{item.Category}: {(item.Complete ? "complete" : "incomplete")} ({item.ClosedRows}/{item.TotalRows})"));
        var text = string.Join(Environment.NewLine, new[]
        {
            SupportSummary.Create(Campaign, Status),
            $"campaignPhase={Campaign?.Phase ?? "none"}",
            $"statusSource={Snapshot.SourceFile}",
            $"statusWarning={Status.Error}",
            ChecklistSummary,
            CoverageSummary,
            string.Empty,
            readiness
        });
        MessageBox.Show(text, "Diagnostic summary", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private GameInstallation RequireInstallation() =>
        _gameLocator.ValidateSelectedDirectory(GameDirectory)
        ?? throw new DirectoryNotFoundException("Choose a valid Crab Champions installation directory first.");

    private CampaignService RequireCampaignService() =>
        _campaignService ?? throw new InvalidOperationException("Dashboard resources are not initialized.");

    private bool FilterCoverage(object value) => value is CoverageRow row && (!NeedsCoverageOnly || row.NeedsCoverage);

    private void RefreshChecklist(LiveStatusSnapshot snapshot)
    {
        Replace(Checklist, new ChecklistReducer(_checklistDefinitions).Reduce(snapshot));
        RefreshPlayGuide();
    }

    private void RefreshPlayGuide()
    {
        _allPlayGuideCategories = _playGuideReducer.Reduce(Checklist.ToArray(), SelectedRole, Status.Cleanliness);
        ApplyPlayGuideFilter();
    }

    private void ApplyPlayGuideFilter()
    {
        var filtered = _allPlayGuideCategories
            .Select(category => category with
            {
                Actions = category.Actions
                    .Where(action => PlayGuideReducer.MatchesFilter(action, SelectedPlayGuideFilter))
                    .ToArray()
            })
            .Where(category => category.Actions.Count > 0)
            .ToArray();
        Replace(PlayGuideCategories, filtered);
        Raise(nameof(PlayGuideOverallSummary));
        Raise(nameof(PlayGuideOverallPercentage));
        Raise(nameof(PlayGuideFilterSummary));
        Raise(nameof(PlayGuideEmptyMessage));
    }

    private async Task SavePreferencesAsync()
    {
        try
        {
            await _store.SavePreferencesAsync(new DashboardPreferences(
                1, SelectedRole, GameDirectory, _store.CampaignStatePath,
                Path.GetDirectoryName(LastBundle) ?? string.Empty, NeedsCoverageOnly, CampaignName), _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void ShowError(Exception exception)
    {
        Activity = exception.Message;
        MessageBox.Show(exception.Message, "CrabRuntimeProbe Dashboard", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private void RaiseCommands()
    {
        foreach (var command in new[] { StopCommand, ExportCommand, OpenLastBundleCommand })
        {
            if (command is AsyncRelayCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
            if (command is RelayCommand relayCommand) relayCommand.RaiseCanExecuteChanged();
        }
    }

    private void RaiseStatusProperties()
    {
        Raise(nameof(Snapshot));
        Raise(nameof(RoleSummary));
        Raise(nameof(LifecycleSummary));
        Raise(nameof(RuntimeSummary));
        Raise(nameof(EvidenceSummary));
        Raise(nameof(HeartbeatSummary));
        Raise(nameof(CampaignIdentitySummary));
        Raise(nameof(ProbeStageSummary));
        Raise(nameof(ElapsedSummary));
        Raise(nameof(CrashBadge));
        Raise(nameof(DirtyBadge));
        Raise(nameof(IsDirtyEvidence));
        Raise(nameof(InventoryDepthSummary));
        Raise(nameof(CircuitBreakerSummary));
    }

    private void RaiseSummaryProperties()
    {
        RaiseStatusProperties();
        Raise(nameof(ChecklistSummary));
        Raise(nameof(PlayGuideOverallSummary));
        Raise(nameof(PlayGuideOverallPercentage));
        Raise(nameof(PlayGuideFilterSummary));
        Raise(nameof(PlayGuideEmptyMessage));
        Raise(nameof(CoverageSummary));
        Raise(nameof(ChecklistCompletionSummary));
        Raise(nameof(IncompleteBadge));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
