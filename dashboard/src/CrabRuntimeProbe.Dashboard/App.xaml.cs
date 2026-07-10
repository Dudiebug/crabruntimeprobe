using CrabRuntimeProbe.Dashboard.Core;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace CrabRuntimeProbe.Dashboard;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, @"Local\CrabRuntimeProbe.Dashboard", out var ownsMutex);
        if (!ownsMutex)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var lines = await CoreSelfTest.RunAsync();
                MessageBox.Show(string.Join(Environment.NewLine, lines), "Self-test passed",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Self-test failed", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
            return;
        }

        var fixture = ArgumentValue(e.Args, "--fixture");
        var screenshot = ArgumentValue(e.Args, "--screenshot");
        var screenshotViewValue = ArgumentValue(e.Args, "--screenshot-tab");
        var screenshotView = ScreenshotView(screenshotViewValue);
        if (!string.IsNullOrWhiteSpace(screenshot) && !Path.IsPathFullyQualified(screenshot))
        {
            MessageBox.Show("--screenshot requires an absolute PNG path.", "Invalid screenshot path",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }
        if (screenshot is not null && screenshotView is null && screenshotViewValue is not null)
        {
            MessageBox.Show("--screenshot-tab must be play-guide, advanced-overview, checklist, or coverage.", "Invalid screenshot tab",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }
        var demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase) || screenshot is not null;
        var attachToRunningGame = e.Args.Contains("--game-autostart", StringComparer.OrdinalIgnoreCase);
        var window = new MainWindow(new MainViewModel(fixture, demo), screenshot, screenshotView, attachToRunningGame);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstanceMutex is not null)
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }

    private static string? ArgumentValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }

    private static DashboardScreenshotView? ScreenshotView(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" => null,
        "play-guide" or "playguide" or "simple" => DashboardScreenshotView.PlayGuide,
        "advanced" or "advanced-overview" or "overview" => DashboardScreenshotView.AdvancedOverview,
        "advanced-checklist" or "checklist" => DashboardScreenshotView.AdvancedChecklist,
        "advanced-coverage" or "coverage" => DashboardScreenshotView.AdvancedCoverage,
        _ => null
    };
}
