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
    private readonly SnapshotEvidenceService _snapshotEvidenceService = new();
    private readonly CapabilityReadinessService _readinessService = new();
    private readonly PlayGuideReducer _playGuideReducer = new();
    private readonly LiveDashboardReducer _liveDashboardReducer = new();
    private readonly ResearchPreparationService _researchPreparation = new();
    private readonly ResearchDashboardReducer _researchDashboardReducer = new();
    private readonly ResearchArtifactStore _researchArtifacts = new();
    private readonly BreadcrumbJournalReader _breadcrumbReader = new();
    private readonly GameProcessExitDetector _gameExitDetector = new();
    private readonly CancellationTokenSource _lifetime = new();
    private SnapshotReplayResult _lastGoodSnapshotReplay = SnapshotReplayResult.Empty;
    private string _lastGoodSnapshotScope = string.Empty;
    private CampaignService? _campaignService;
    private LocalCampaignState? _campaign;
    private LiveStatusReadResult _status = new(
        LiveStatusSnapshot.Empty, false, true, false, "Waiting for RuntimeProbe status.", DateTimeOffset.UtcNow);
    private LiveDashboardStatus _liveDashboard = LiveDashboardStatus.Empty;
    private string _campaignName = "CrabSync Full Observe";
    private string _gameDirectory = string.Empty;
    private CampaignRole _selectedRole = CampaignRole.Host;
    private string _readinessCorrelationCode = string.Empty;
    private string _activity = "Loading campaign resources...";
    private string _lastBundle = string.Empty;
    private bool _needsCoverageOnly = true;
    private bool _initialized;
    private bool _localGameRunning;
    private bool _autoCollected;
    private Process? _monitoredProcess;
    private CoverageRow? _selectedCoverage;
    private IReadOnlyList<ChecklistDefinition> _checklistDefinitions = ChecklistCatalog.All;
    private IReadOnlyList<PlayGuideCategory> _allPlayGuideCategories = Array.Empty<PlayGuideCategory>();
    private PlayGuideFilter _playGuideFilter = PlayGuideFilter.ToDo;
    private HookCandidateCatalog? _researchCatalog;
    private HookValidationLedger? _researchLedger;
    private TrustedHookManifest? _trustedManifest;
    private HookQuarantineState? _quarantine;
    private ResearchWorkspace? _researchWorkspace;
    private ResearchRunPlan? _researchPlan;
    private HookCandidateDefinition? _recommendedCandidate;
    private HookValidationDepth? _recommendedDepth;
    private BreadcrumbReadResult? _researchJournal;
    private HookRunClassification? _researchClassification;

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
        GenerateReadinessCodeCommand = new RelayCommand(GenerateReadinessCode, () => SelectedRole == CampaignRole.Host);
        PrepareReadinessCampaignCommand = new AsyncRelayCommand(PrepareReadinessCampaignAsync);
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
        Research = new ResearchViewModel(
            StartResearchAsync,
            RepeatResearchAsync,
            PrepareNextResearchDepthAsync,
            RunCandidateAloneAsync,
            QuarantineResearchCandidateAsync,
            ReturnToSafePlayGuideAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ChecklistViewItem> Checklist { get; }
    public ObservableCollection<PlayGuideCategory> PlayGuideCategories { get; }
    public ObservableCollection<CoverageRow> Coverage { get; }
    public ObservableCollection<CapabilityReadiness> Readiness { get; }
    public ICollectionView ChecklistView { get; }
    public ICollectionView CoverageView { get; }
    public IReadOnlyList<CampaignRole> RoleOptions { get; } = new[] { CampaignRole.Host, CampaignRole.JoinedClient };
    public ResearchViewModel Research { get; }

    public ICommand InitializeCommand { get; }
    public ICommand BrowseGameCommand { get; }
    public ICommand DetectGameCommand { get; }
    public ICommand PrepareAndStartCommand { get; }
    public ICommand PrepareCommand { get; }
    public ICommand GenerateReadinessCodeCommand { get; }
    public ICommand PrepareReadinessCampaignCommand { get; }
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
            Raise(nameof(ReadinessSetupHint));
            if (GenerateReadinessCodeCommand is RelayCommand generate) generate.RaiseCanExecuteChanged();
            RefreshPlayGuide();
            _ = SavePreferencesAsync();
        }
    }

    public string ReadinessCorrelationCode
    {
        get => _readinessCorrelationCode;
        set => Set(ref _readinessCorrelationCode, value);
    }

    public string ReadinessSetupHint => SelectedRole == CampaignRole.Host
        ? "Host: generate a local eight-character code, share it out of band, then prepare this computer."
        : "Joined client: enter the host's eight-character code exactly, then prepare this computer.";

    public string ReadinessInventorySummary => "Inventory collection is deferred and disabled. This profile never enables wrapper, count, item, metadata, or enhancement reads.";

    public string ReadinessPairingSummary => Campaign?.ReadinessPairing is { } pairing
        ? $"Prepared pair {pairing.PairId} - inventory {pairing.InventoryStage}"
        : "No readiness campaign prepared on this computer.";

    public bool IsReadinessCampaignPrepared => Campaign is { } campaign
                                              && ReadinessCampaignContracts.IsReadinessProfile(campaign.ProfileId)
                                              && campaign.ReadinessPairing is { HasValidPair: true };

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
            RefreshLiveDashboard();
            RaiseCommands();
            Raise(nameof(CampaignIdentitySummary));
            Raise(nameof(ElapsedSummary));
            Raise(nameof(ReadinessPairingSummary));
            Raise(nameof(IsReadinessCampaignPrepared));
        }
    }
    public LiveStatusReadResult Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            RefreshLiveDashboard();
            RaiseStatusProperties();
        }
    }
    public LiveStatusSnapshot Snapshot => Status.Snapshot;
    public LiveDashboardStatus LiveDashboard => _liveDashboard;
    public LiveCollectionState LiveState => LiveDashboard.State;
    public string LiveStateText => LiveDashboard.StateText;
    public string LiveDetail => LiveDashboard.Detail;
    public string HeartbeatAgeText => LiveDashboard.HeartbeatAgeText;
    public string SequenceProgressText => LiveDashboard.SequenceText;
    public string ActiveProfileText => LiveDashboard.ActiveProfile;
    public string SamplingCategoryText => LiveDashboard.SamplingCategoryText;
    public string CollectionReadinessText => LiveDashboard.ReadinessText;
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
        ? $"{HeartbeatAgeText} - {SequenceProgressText}{(Status.IsStale ? " - STALE" : string.Empty)}"
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
            await LoadResearchDefaultsAsync(resources);
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
                if (Campaign.ReadinessPairing is { } pairing)
                    _readinessCorrelationCode = pairing.CorrelationCode;
                Raise(nameof(CampaignName));
                Raise(nameof(SelectedRole));
                Raise(nameof(IsHosting));
                Raise(nameof(IsJoiningFriend));
                Raise(nameof(GameDirectory));
                Raise(nameof(LastBundle));
                await LoadResearchWorkspaceAsync(Campaign);
                await TryRecoverResearchPlanAsync(Campaign);
            }
            else if (string.IsNullOrWhiteSpace(GameDirectory))
            {
                var detected = _gameLocator.Detect().FirstOrDefault();
                if (detected is not null) GameDirectory = detected.InstallDirectory;
            }
            var localInstallation = _gameLocator.ValidateSelectedDirectory(GameDirectory);
            _localGameRunning = localInstallation is not null && new GameProcessService().IsRunning(localInstallation);
            RefreshLiveDashboard();

            if (_demo)
            {
                var snapshot = _statusReader.Parse(DemoStatus.Json, "embedded-demo");
                Status = new LiveStatusReadResult(
                    snapshot, true, false, false, string.Empty, snapshot.HeartbeatAtUtc.AddSeconds(1));
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
                Status = await ReadCampaignStatusAsync(Campaign, _lifetime.Token);
            }

            if (Campaign?.Phase == "monitoring" && (_localGameRunning || Status.HasSnapshot))
                _gameExitDetector.Begin(DateTimeOffset.UtcNow, processSeen: true);

            RefreshChecklist(Status.Snapshot);
            Activity = _demo ? "Demo mode - no game files are changed" : "Ready - hook-free snapshot observation";
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
            _researchPlan = null;
            _researchJournal = null;
            _researchClassification = null;
            await LoadResearchWorkspaceAsync(Campaign);
            _autoCollected = false;
            _gameExitDetector.Reset();
            Activity = "Prepared - start Crab Champions when both computers are ready";
            await SavePreferencesAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task StartMonitoringAsync()
    {
        try
        {
            if (Campaign?.Phase == "collected")
                throw new InvalidOperationException(
                    "This generation is already finalized. Prepare a fresh Play Guide or research run before launching again.");
            if (_researchPlan?.Manifest is not null && _researchClassification is not null)
                throw new InvalidOperationException(
                    "This research run is already classified. Choose Repeat, Prepare next depth, Run candidate alone, or Return to safe Play Guide.");
            if (Campaign is null)
                Campaign = await RequireCampaignService().ResumeAsync(_lifetime.Token, Environment.ProcessPath)
                           ?? throw new InvalidOperationException("Prepare a campaign first.");
            var installation = RequireInstallation();
            var process = new GameProcessService().Launch(installation);
            var processId = process.Id;
            ReplaceMonitoredProcess(process);
            _localGameRunning = true;
            RefreshLiveDashboard();
            _gameExitDetector.Begin(DateTimeOffset.UtcNow, processSeen: true);
            Campaign = await RequireCampaignService().MarkMonitoringAsync(Campaign, _lifetime.Token);
            Activity = _researchPlan?.Manifest is null
                ? $"Monitoring Crab Champions process {processId} - hook-free snapshots only"
                : $"Monitoring research process {processId} - trusted pool plus exactly one canary";
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

            var processId = process.Id;
            ReplaceMonitoredProcess(process);
            _localGameRunning = true;
            RefreshLiveDashboard();
            _gameExitDetector.Begin(DateTimeOffset.UtcNow, processSeen: true);
            Campaign = await RequireCampaignService().MarkMonitoringAsync(Campaign, _lifetime.Token);
            Activity = _researchPlan?.Manifest is null
                ? $"Crab Champions opened the dashboard - hook-free snapshot monitoring on process {processId}"
                : $"Crab Champions opened the dashboard - progressive research monitoring on process {processId}";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void GenerateReadinessCode()
    {
        if (SelectedRole != CampaignRole.Host)
        {
            Activity = "Only the host generates a pairing code; enter the host code on a joined client.";
            return;
        }

        ReadinessCorrelationCode = ReadinessCampaignContracts.GenerateCorrelationCode();
        Activity = "Readiness pairing code generated locally. Share it out of band with the joined client.";
    }

    private async Task PrepareReadinessCampaignAsync()
    {
        try
        {
            var installation = RequireInstallation();
            if (new GameProcessService().IsRunning(installation))
                throw new InvalidOperationException("Close Crab Champions before preparing a different readiness campaign.");
            Activity = "Preparing the bounded, read-only paired readiness campaign...";
            Campaign = await RequireCampaignService().PrepareReadinessCampaignAsync(
                installation,
                SelectedRole,
                ReadinessCampaignContracts.DefaultCampaignName,
                string.IsNullOrWhiteSpace(ReadinessCorrelationCode) ? null : ReadinessCorrelationCode,
                dashboardExecutablePath: Environment.ProcessPath,
                cancellationToken: _lifetime.Token);
            var pairing = Campaign.ReadinessPairing
                          ?? throw new InvalidDataException("Readiness preparation did not retain local pairing state.");
            ReadinessCorrelationCode = pairing.CorrelationCode;
            _researchPlan = null;
            _researchJournal = null;
            _researchClassification = null;
            _autoCollected = false;
            _gameExitDetector.Reset();
            RefreshResearchDashboard();
            Activity = "Readiness campaign prepared. Share the displayed local code out of band, then start both games when ready.";
            await SavePreferencesAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private Task StartResearchAsync() => PrepareResearchGenerationAsync(
        ResearchRunType.Combined, null, null, launchGame: true);

    private Task RepeatResearchAsync()
    {
        var canary = RequireCurrentCanary();
        return PrepareResearchGenerationAsync(_researchPlan?.Manifest?.RunType ?? ResearchRunType.Combined,
            canary.CandidateId, canary.ValidationDepth, launchGame: false);
    }

    private Task PrepareNextResearchDepthAsync()
    {
        var canary = RequireCurrentCanary();
        if (canary.ValidationDepth >= HookValidationDepth.FullPassiveEvidence)
            throw new InvalidOperationException("Depth 7 is already the deepest validation level.");
        return PrepareResearchGenerationAsync(_researchPlan?.Manifest?.RunType ?? ResearchRunType.Combined,
            canary.CandidateId, (HookValidationDepth)((int)canary.ValidationDepth + 1), launchGame: false);
    }

    private Task RunCandidateAloneAsync()
    {
        var canary = RequireCurrentCanary();
        return PrepareResearchGenerationAsync(ResearchRunType.CanaryOnly,
            canary.CandidateId, canary.ValidationDepth, launchGame: false);
    }

    private async Task PrepareResearchGenerationAsync(
        ResearchRunType runType,
        string? candidateId,
        HookValidationDepth? depth,
        bool launchGame)
    {
        try
        {
            var installation = RequireInstallation();
            if (new GameProcessService().IsRunning(installation))
                throw new InvalidOperationException("Close Crab Champions before preparing a new research generation.");
            Activity = "Installing the read-only payload and validating the next research manifest...";
            Campaign = await RequireCampaignService().PrepareAsync(
                installation, SelectedRole, "Progressive Broad Observation",
                dashboardExecutablePath: Environment.ProcessPath,
                cancellationToken: _lifetime.Token);
            var prepared = await _researchPreparation.PlanAsync(
                Campaign, runType, candidateId, depth, cancellationToken: _lifetime.Token);
            _researchWorkspace = prepared.Workspace;
            _researchCatalog = prepared.Workspace.Catalog;
            _researchLedger = prepared.Workspace.Ledger;
            _trustedManifest = prepared.Workspace.TrustedManifest;
            _quarantine = prepared.Workspace.Quarantine;
            _recommendedCandidate = prepared.RecommendedCandidate;
            _recommendedDepth = prepared.RecommendedDepth;
            _researchPlan = prepared.Plan;
            _researchJournal = null;
            _researchClassification = null;
            if (!prepared.Plan.IsValid)
                throw new InvalidDataException(string.Join(Environment.NewLine, prepared.Plan.Errors));
            await RequireCampaignService().ArmProgressiveObservationAsync(
                Campaign, prepared.Plan, cancellationToken: _lifetime.Token);
            _autoCollected = false;
            _gameExitDetector.Reset();
            RefreshResearchDashboard();
            Activity = launchGame
                ? "Research manifest armed - starting Crab Champions with one canary registered last"
                : "Research manifest prepared for the next game launch; nothing advanced in the prior process";
            await SavePreferencesAsync();
            if (launchGame) await StartMonitoringAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task QuarantineResearchCandidateAsync()
    {
        try
        {
            if (_researchWorkspace is null || _quarantine is null || _researchLedger is null)
                throw new InvalidOperationException("Research state is unavailable.");
            var canary = RequireCurrentCanary();
            var candidate = _researchWorkspace.Catalog.ById[canary.CandidateId];
            var runId = _researchPlan?.Manifest?.RunId ?? $"manual-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
            _quarantine = QuarantinePolicy.QuarantineExplicitly(_quarantine, candidate,
                canary.ValidationDepth, runId, "Explicit dashboard quarantine");
            var records = _researchLedger.Candidates.Select(record => record.CandidateId == candidate.Id
                ? record with { State = HookCandidateState.Quarantined, TrustedDepth = null }
                : record).ToArray();
            _researchLedger = _researchLedger with { UpdatedAtUtc = DateTimeOffset.UtcNow, Candidates = records };
            await _researchArtifacts.WriteQuarantineAsync(_researchWorkspace.QuarantinePath, _quarantine,
                _researchWorkspace.Catalog, _researchWorkspace.Catalog.GeneratedAtUtc, _lifetime.Token);
            await _researchArtifacts.WriteLedgerAsync(_researchWorkspace.LedgerPath, _researchLedger,
                _researchWorkspace.Catalog.GeneratedAtUtc,
                "Legacy observations remain history only and never confer compatibility-aware trust.", _lifetime.Token);
            _researchWorkspace = _researchWorkspace with { Quarantine = _quarantine, Ledger = _researchLedger };
            (_recommendedCandidate, _recommendedDepth) = ResearchPreparationService.Recommend(
                _researchWorkspace.Catalog, _researchLedger, _quarantine);
            RefreshResearchDashboard();
            Activity = $"{candidate.DisplayName} quarantined; it cannot auto-arm";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task ReturnToSafePlayGuideAsync()
    {
        try
        {
            var installation = RequireInstallation();
            if (new GameProcessService().IsRunning(installation))
                throw new InvalidOperationException("Close Crab Champions before returning to the safe profile.");
            Campaign = await RequireCampaignService().PrepareAsync(
                installation, SelectedRole, CampaignName,
                dashboardExecutablePath: Environment.ProcessPath,
                cancellationToken: _lifetime.Token);
            _researchPlan = null;
            _researchJournal = null;
            _researchClassification = null;
            await LoadResearchWorkspaceAsync(Campaign);
            RefreshResearchDashboard();
            Activity = "Safe Play Guide prepared - hook-free snapshot collection is restored for the next launch";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private HookCandidateSelection RequireCurrentCanary() =>
        _researchPlan?.Manifest?.Canary
        ?? throw new InvalidOperationException("No current canary run is available.");

    private async Task ResumeAsync()
    {
        try
        {
            Campaign = await RequireCampaignService().ResumeAsync(_lifetime.Token, Environment.ProcessPath)
                       ?? throw new InvalidOperationException("No valid prepared campaign is available to resume.");
            _researchPlan = null;
            _researchJournal = null;
            _researchClassification = null;
            SelectedRole = Campaign.Role;
            CampaignName = Campaign.CampaignName;
            GameDirectory = Campaign.GameDirectory;
            if (Campaign.ReadinessPairing is { } pairing)
                ReadinessCorrelationCode = pairing.CorrelationCode;
            await LoadResearchWorkspaceAsync(Campaign);
            _gameExitDetector.Reset();
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
            _researchPlan = null;
            _researchJournal = null;
            _researchClassification = null;
            ReplaceMonitoredProcess(null);
            _localGameRunning = false;
            _gameExitDetector.Reset();
            Status = new LiveStatusReadResult(LiveStatusSnapshot.Empty, false, true, false,
                "No active campaign", DateTimeOffset.UtcNow);
            RefreshChecklist(Status.Snapshot);
            RefreshResearchDashboard();
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
                var observedAt = DateTimeOffset.UtcNow;
                var next = await ReadCampaignStatusAsync(Campaign, token);
                var installation = _gameLocator.ValidateSelectedDirectory(GameDirectory);
                var runningProcess = installation is null ? null : new GameProcessService().FindRunning(installation);
                var running = runningProcess is not null;
                _localGameRunning = running;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Status = next;
                    RefreshChecklist(next.Snapshot);
                    RaiseSummaryProperties();
                });
                await RefreshResearchJournalAsync(token);

                if (installation is null) continue;
                if (runningProcess is not null) ReplaceMonitoredProcess(runningProcess);
                if (_gameExitDetector.Observe(running, observedAt)
                    && !_autoCollected && Campaign.Phase == "monitoring")
                {
                    _autoCollected = true;
                    var nonZeroExit = false;
                    try
                    {
                        nonZeroExit = _monitoredProcess is { HasExited: true } && _monitoredProcess.ExitCode != 0;
                    }
                    catch (InvalidOperationException) { }
                    // Missing/stale status is evidence-health information, not proof that the game crashed.
                    var abnormal = nonZeroExit || next.Snapshot.CrashSuspected;
                    await FinalizeResearchRunAsync(next, nonZeroExit, token);
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
                    ReplaceMonitoredProcess(null);
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

    private void ReplaceMonitoredProcess(Process? replacement)
    {
        if (ReferenceEquals(_monitoredProcess, replacement)) return;
        if (_monitoredProcess is not null && replacement is not null)
        {
            try
            {
                if (_monitoredProcess.Id == replacement.Id)
                {
                    replacement.Dispose();
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // Replace an exited/unassociated wrapper below.
            }
        }
        _monitoredProcess?.Dispose();
        _monitoredProcess = replacement;
    }

    private async Task<LiveStatusReadResult> ReadCampaignStatusAsync(
        LocalCampaignState campaign,
        CancellationToken cancellationToken)
    {
        var status = await _statusReader.ReadLatestAsync(
            campaign.StatusDirectory,
            scope: StatusReadScope.FromCampaign(campaign),
            cancellationToken: cancellationToken);
        if (status.HasSnapshot
            && status.Snapshot.Runtime.ActiveProfile.Equals(
                "progressive-broad-observation", StringComparison.OrdinalIgnoreCase))
        {
            // Progressive snapshot rows truthfully report hooksDisabled=false. They remain useful
            // research evidence, but they must never enter the hook-free Play Guide reducer.
            return status;
        }
        try
        {
            var snapshots = await _snapshotEvidenceService.LoadAsync(campaign, cancellationToken);
            var scope = SnapshotReplayScope.FromCampaign(campaign);
            var scopeKey = $"{scope.SessionId}|{scope.CampaignGeneration}|{scope.MachineId}";
            if (!_lastGoodSnapshotScope.Equals(scopeKey, StringComparison.Ordinal))
            {
                _lastGoodSnapshotScope = scopeKey;
                _lastGoodSnapshotReplay = SnapshotReplayResult.Empty;
            }
            if (snapshots.Replay.Rejections.Count > 0)
            {
                status = _snapshotEvidenceService.Merge(status, _lastGoodSnapshotReplay, scope);
                var detail = string.Join(", ", snapshots.Replay.Rejections
                    .Take(3)
                    .Select(item => item.Code));
                var warning = $"Snapshot evidence rejected ({snapshots.Replay.Rejections.Count}): {detail}";
                return status with
                {
                    Snapshot = status.Snapshot with
                    {
                        DirtyEvidence = true,
                        EvidenceHealth = status.Snapshot.EvidenceHealth with
                        {
                            State = "snapshot-rejected",
                            RejectedRows = status.Snapshot.EvidenceHealth.RejectedRows
                                           + snapshots.Replay.Rejections.Count,
                            DirtyRows = status.Snapshot.EvidenceHealth.DirtyRows + 1,
                            Detail = warning
                        }
                    },
                    Error = string.IsNullOrWhiteSpace(status.Error)
                        ? warning
                        : $"{status.Error}; {warning}"
                };
            }
            _lastGoodSnapshotReplay = snapshots.Replay;
            return _snapshotEvidenceService.Merge(status, snapshots.Replay, scope);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            // The append-only file may rotate between discovery and open. The next monitor tick retries.
            return status;
        }
        catch (UnauthorizedAccessException)
        {
            return status;
        }
    }

    private async Task LoadResearchDefaultsAsync(DashboardResources resources)
    {
        _researchCatalog = await _researchArtifacts.ReadCatalogAsync(
            Path.Combine(resources.CampaignRoot, "hook_candidate_catalog.json"), _lifetime.Token);
        _researchLedger = await _researchArtifacts.ReadLedgerAsync(
            Path.Combine(resources.CampaignRoot, "hook_validation_ledger.json"), _lifetime.Token);
        _trustedManifest = await _researchArtifacts.ReadTrustedManifestAsync(
            Path.Combine(resources.CampaignRoot, "trusted_hook_manifest.json"), _lifetime.Token);
        _quarantine = await _researchArtifacts.ReadQuarantineAsync(
            Path.Combine(resources.CampaignRoot, "hook_quarantine.json"), _lifetime.Token);
        (_recommendedCandidate, _recommendedDepth) = ResearchPreparationService.Recommend(
            _researchCatalog, _researchLedger, _quarantine);
        RefreshResearchDashboard();
    }

    private async Task LoadResearchWorkspaceAsync(LocalCampaignState campaign)
    {
        _researchWorkspace = await _researchPreparation.LoadWorkspaceAsync(campaign,
            cancellationToken: _lifetime.Token);
        _researchCatalog = _researchWorkspace.Catalog;
        _researchLedger = _researchWorkspace.Ledger;
        _trustedManifest = _researchWorkspace.TrustedManifest;
        _quarantine = _researchWorkspace.Quarantine;
        (_recommendedCandidate, _recommendedDepth) = ResearchPreparationService.Recommend(
            _researchCatalog, _researchLedger, _quarantine);
        RefreshResearchDashboard();
    }

    private async Task TryRecoverResearchPlanAsync(LocalCampaignState campaign)
    {
        if (_researchWorkspace is null || campaign.Phase is not ("prepared" or "monitoring")
            || !Directory.Exists(campaign.StatusDirectory))
            return;
        var matching = new List<HookRunManifest>();
        foreach (var path in Directory.EnumerateFiles(
                     campaign.StatusDirectory, "hook_run_manifest_*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var manifest = await _researchArtifacts.ReadRunManifestAsync(path, _lifetime.Token);
                if (manifest.SessionId == campaign.SessionId
                    && manifest.CampaignGeneration == campaign.Generation
                    && manifest.SelectedRole == campaign.Role)
                    matching.Add(manifest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ResearchSchemaException or System.Text.Json.JsonException)
            {
                // A malformed artifact cannot be resumed; the valid exact match below remains required.
            }
        }
        if (matching.Count != 1) return;
        var persisted = matching[0];
        var validated = new ResearchRunPlanner().CreatePlan(
            _researchWorkspace.Catalog,
            persisted.Compatibility,
            _researchWorkspace.TrustedManifest,
            _researchWorkspace.Ledger,
            _researchWorkspace.Quarantine,
            persisted.RunType,
            persisted.Canary?.CandidateId,
            persisted.Canary?.ValidationDepth,
            persisted.SelectedRole,
            persisted.CampaignGeneration,
            persisted.RunId,
            persisted.SessionId);
        if (!validated.IsValid || validated.Manifest is null
            || validated.Manifest.Compatibility.Fingerprint != persisted.Compatibility.Fingerprint
            || !validated.Manifest.RegistrationOrder.SequenceEqual(persisted.RegistrationOrder, StringComparer.Ordinal)
            || !validated.Manifest.TrustedCandidates.SequenceEqual(persisted.TrustedCandidates)
            || validated.Manifest.Canary != persisted.Canary)
            return;
        _researchPlan = validated with { Manifest = persisted };
        _researchClassification = null;
        _researchJournal = null;
        RefreshResearchDashboard();
    }

    private async Task RefreshResearchJournalAsync(CancellationToken cancellationToken)
    {
        var manifest = _researchPlan?.Manifest;
        if (manifest is null || Campaign is null) return;
        var path = Path.Combine(Campaign.StatusDirectory, $"hook_breadcrumbs_{manifest.RunId}.jsonl");
        if (!File.Exists(path))
        {
            await Application.Current.Dispatcher.InvokeAsync(RefreshResearchDashboard);
            return;
        }
        var journal = await _breadcrumbReader.ReadAsync(path, manifest.RunId, cancellationToken);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _researchJournal = journal;
            RefreshResearchDashboard();
        });
    }

    private async Task FinalizeResearchRunAsync(
        LiveStatusReadResult status,
        bool nonZeroExit,
        CancellationToken cancellationToken)
    {
        var manifest = _researchPlan?.Manifest;
        if (manifest is null || Campaign is null || _researchWorkspace is null) return;
        await RefreshResearchJournalAsync(cancellationToken);
        _researchJournal ??= new BreadcrumbReadResult(
            Array.Empty<HookBreadcrumb>(),
            new[] { new BreadcrumbReadIssue("journal-missing", "No breadcrumb journal was recovered.", 0, true) },
            false, null, null, new Dictionary<string, int>());
        var evidenceState = status.Snapshot.EvidenceHealth.State ?? string.Empty;
        var signals = new RunObservationSignals(
            CleanShutdown: !nonZeroExit && !status.Snapshot.CrashSuspected,
            ProcessExitObserved: true,
            AbnormalProcessExit: nonZeroExit || status.Snapshot.CrashSuspected,
            ExternalTermination: false,
            WriterStale: status.IsStale,
            EvidenceWriteFailed: evidenceState.Contains("write", StringComparison.OrdinalIgnoreCase) ||
                                 evidenceState.Contains("error", StringComparison.OrdinalIgnoreCase),
            StatusFaulted: LiveDashboard.State == LiveCollectionState.Faulted,
            CrashArtifactCorrelated: status.Snapshot.CrashSuspected,
            Ue4ssCallbackErrors: 0);
        _researchClassification = new HookRunClassifier().Classify(manifest, _researchJournal, signals);
        var classificationPath = Path.Combine(Campaign.StatusDirectory,
            $"hook_run_classification_{manifest.RunId}.json");
        await _researchArtifacts.WriteClassificationAsync(classificationPath, _researchClassification, cancellationToken);
        _researchLedger = ValidationLedgerReducer.Apply(
            _researchWorkspace.Ledger, manifest, _researchClassification, _researchJournal,
            lifecycleTransitionObserved: status.Snapshot.Lifecycle.Generation > 0,
            reducerFixtureCovered: true,
            newUe4ssCallbackError: signals.Ue4ssCallbackErrors > 0);
        _quarantine = QuarantinePolicy.AddCrashSuspect(
            _researchWorkspace.Quarantine, _researchWorkspace.Catalog, _researchClassification, manifest.RunId);
        if (manifest.Canary is { } completedCanary)
        {
            var promotion = TrustedManifestBuilder.Promote(
                _researchWorkspace.Catalog,
                _researchLedger,
                completedCanary.CandidateId,
                completedCanary.ValidationDepth,
                _researchWorkspace.Compatibility,
                requireBothRoles: true);
            _researchLedger = promotion.Ledger;
            _trustedManifest = promotion.Manifest;
        }
        else
        {
            _trustedManifest = TrustedManifestBuilder.Build(
                _researchWorkspace.Catalog, _researchLedger, _researchWorkspace.Compatibility);
        }
        await _researchArtifacts.WriteLedgerAsync(_researchWorkspace.LedgerPath, _researchLedger,
            _researchWorkspace.Catalog.GeneratedAtUtc,
            "Legacy observations remain history only and never confer compatibility-aware trust.", cancellationToken);
        await _researchArtifacts.WriteTrustedManifestAsync(_researchWorkspace.TrustedManifestPath, _trustedManifest,
            _researchWorkspace.Catalog.GeneratedAtUtc, cancellationToken);
        await _researchArtifacts.WriteQuarantineAsync(_researchWorkspace.QuarantinePath, _quarantine,
            _researchWorkspace.Catalog, _researchWorkspace.Catalog.GeneratedAtUtc, cancellationToken);
        // Every research authorization is single-process. Persist the outcome first, then
        // restore the hook-free config so a later game launch cannot silently repeat it.
        await RequireCampaignService().DisarmProgressiveObservationAsync(Campaign, cancellationToken);
        _researchWorkspace = _researchWorkspace with
        {
            Ledger = _researchLedger,
            TrustedManifest = _trustedManifest,
            Quarantine = _quarantine
        };
        (_recommendedCandidate, _recommendedDepth) = ResearchPreparationService.Recommend(
            _researchWorkspace.Catalog, _researchLedger, _quarantine);
        await Application.Current.Dispatcher.InvokeAsync(RefreshResearchDashboard);
    }

    private bool FilterCoverage(object value) => value is CoverageRow row && (!NeedsCoverageOnly || row.NeedsCoverage);

    private void RefreshChecklist(LiveStatusSnapshot snapshot)
    {
        Replace(Checklist, new ChecklistReducer(_checklistDefinitions).Reduce(snapshot));
        RefreshPlayGuide();
    }

    private void RefreshPlayGuide()
    {
        _allPlayGuideCategories = _playGuideReducer.Reduce(
            Checklist.ToArray(), SelectedRole, Status.Cleanliness, LiveDashboard.Capabilities);
        ApplyPlayGuideFilter();
    }

    private void RefreshLiveDashboard()
    {
        var useCampaignContext = !_demo && string.IsNullOrWhiteSpace(_fixture);
        var monitoringExpected = useCampaignContext
                                 && Campaign?.Phase is "prepared" or "monitoring" or "stop-requested";
        var collectionStopped = useCampaignContext && Campaign?.Phase is "collected";
        var next = _liveDashboardReducer.Reduce(Status, _localGameRunning, monitoringExpected, collectionStopped);
        if (EqualityComparer<LiveDashboardStatus>.Default.Equals(_liveDashboard, next))
        {
            RefreshResearchDashboard();
            return;
        }
        _liveDashboard = next;
        Raise(nameof(LiveDashboard));
        Raise(nameof(LiveState));
        Raise(nameof(LiveStateText));
        Raise(nameof(LiveDetail));
        Raise(nameof(HeartbeatAgeText));
        Raise(nameof(SequenceProgressText));
        Raise(nameof(ActiveProfileText));
        Raise(nameof(SamplingCategoryText));
        Raise(nameof(CollectionReadinessText));
        Raise(nameof(HeartbeatSummary));
        RefreshResearchDashboard();
    }

    private void RefreshResearchDashboard()
    {
        var candidateId = _researchPlan?.Manifest?.Canary?.CandidateId ?? _recommendedCandidate?.Id;
        var candidateRecord = string.IsNullOrWhiteSpace(candidateId)
            ? null
            : _researchLedger?.Candidates.FirstOrDefault(record => record.CandidateId == candidateId);
        var state = _researchDashboardReducer.Reduce(
            _researchPlan, _recommendedCandidate, _recommendedDepth, _researchJournal,
            _researchClassification, LiveDashboard, Snapshot.Safety, _quarantine,
            candidateRecord, _localGameRunning, Campaign is not null);
        Research.Apply(state);
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
        Raise(nameof(LiveDashboard));
        Raise(nameof(LiveState));
        Raise(nameof(LiveStateText));
        Raise(nameof(LiveDetail));
        Raise(nameof(HeartbeatAgeText));
        Raise(nameof(SequenceProgressText));
        Raise(nameof(ActiveProfileText));
        Raise(nameof(SamplingCategoryText));
        Raise(nameof(CollectionReadinessText));
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
        ReplaceMonitoredProcess(null);
        _lifetime.Dispose();
    }
}
