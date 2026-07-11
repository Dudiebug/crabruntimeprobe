using CrabRuntimeProbe.Dashboard.Core;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace CrabRuntimeProbe.Dashboard;

public sealed class ResearchViewModel : INotifyPropertyChanged
{
    private ResearchDashboardStatus _state = ResearchDashboardStatus.Empty;

    public ResearchViewModel(
        Func<Task> start,
        Func<Task> repeat,
        Func<Task> nextDepth,
        Func<Task> alone,
        Func<Task> quarantine,
        Func<Task> safeMode)
    {
        StartResearchCommand = new AsyncRelayCommand(start, () => State.StartResearch.Enabled);
        RepeatSameTestCommand = new AsyncRelayCommand(repeat, () => State.RepeatSameTest.Enabled);
        PrepareNextDepthCommand = new AsyncRelayCommand(nextDepth, () => State.PrepareNextDepth.Enabled);
        RunCandidateAloneCommand = new AsyncRelayCommand(alone, () => State.RunCandidateAlone.Enabled);
        QuarantineCandidateCommand = new AsyncRelayCommand(quarantine, () => State.QuarantineCandidate.Enabled);
        ReturnSafePlayGuideCommand = new AsyncRelayCommand(safeMode, () => State.ReturnSafePlayGuide.Enabled);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand StartResearchCommand { get; }
    public ICommand RepeatSameTestCommand { get; }
    public ICommand PrepareNextDepthCommand { get; }
    public ICommand RunCandidateAloneCommand { get; }
    public ICommand QuarantineCandidateCommand { get; }
    public ICommand ReturnSafePlayGuideCommand { get; }

    public ResearchDashboardStatus State => _state;
    public int TrustedHookCount => State.TrustedHookCount;
    public string TrustedManifestHash => State.TrustedManifestHash;
    public string ActiveCanary => State.ActiveCanary;
    public string ActiveCanaryId => State.ActiveCanaryId;
    public string CanaryValidationDepth => State.CanaryValidationDepth;
    public string SuggestedAction => State.SuggestedAction;
    public string RegistrationState => State.RegistrationState;
    public int CallbackCount => State.CallbackCount;
    public string LastCompletedBreadcrumb => State.LastCompletedBreadcrumb;
    public string CircuitBreakerState => State.CircuitBreakerState;
    public string HeartbeatAndSequence => State.HeartbeatAndSequence;
    public string FinalRunClassification => State.FinalRunClassification;
    public string AttributionConfidence => State.AttributionConfidence;
    public string ClassificationReason => State.ClassificationReason;
    public bool IsRunActive => State.IsRunActive;
    public bool CanStartResearch => State.StartResearch.Enabled;
    public bool CanRepeatSameTest => State.RepeatSameTest.Enabled;
    public bool CanPrepareNextDepth => State.PrepareNextDepth.Enabled;
    public bool CanRunCandidateAlone => State.RunCandidateAlone.Enabled;
    public bool CanQuarantineCandidate => State.QuarantineCandidate.Enabled;
    public bool CanReturnSafePlayGuide => State.ReturnSafePlayGuide.Enabled;
    public string StartResearchExplanation => Explanation(State.StartResearch, "Starts a new process generation with the recommended canary registered last.");
    public string RepeatSameTestExplanation => Explanation(State.RepeatSameTest, "Prepares the same candidate and depth for the next launch.");
    public string PrepareNextDepthExplanation => Explanation(State.PrepareNextDepth, "Prepares one deeper validation level for the next launch.");
    public string RunCandidateAloneExplanation => Explanation(State.RunCandidateAlone, "Prepares a canary-only diagnostic for the next launch.");
    public string QuarantineCandidateExplanation => Explanation(State.QuarantineCandidate, "Prevents automatic arming until explicitly reviewed.");
    public string ReturnSafePlayGuideExplanation => Explanation(State.ReturnSafePlayGuide, "Rewrites the next launch to the hook-free Play Guide profile.");

    public void Apply(ResearchDashboardStatus state)
    {
        _state = state;
        foreach (var property in typeof(ResearchViewModel).GetProperties()
                     .Where(property => property.Name is not nameof(State) && property.Name is not nameof(PropertyChanged)))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property.Name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        foreach (var command in new[]
                 {
                     StartResearchCommand, RepeatSameTestCommand, PrepareNextDepthCommand,
                     RunCandidateAloneCommand, QuarantineCandidateCommand, ReturnSafePlayGuideCommand
                 }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged();
    }

    private static string Explanation(ResearchActionState action, string enabled) =>
        action.Enabled ? enabled : action.DisabledReason;
}
