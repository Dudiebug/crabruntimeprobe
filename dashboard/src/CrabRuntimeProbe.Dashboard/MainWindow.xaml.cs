using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Application = System.Windows.Application;

namespace CrabRuntimeProbe.Dashboard;

public enum DashboardScreenshotView
{
    PlayGuide,
    AdvancedOverview,
    AdvancedChecklist,
    AdvancedCoverage
}

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly string? _screenshotPath;
    private readonly DashboardScreenshotView? _screenshotView;

    public MainWindow(
        MainViewModel viewModel,
        string? screenshotPath = null,
        DashboardScreenshotView? screenshotView = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _screenshotPath = screenshotPath;
        _screenshotView = screenshotView;
        DataContext = viewModel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
        if (_screenshotPath is null) return;
        Width = 1440;
        Height = 900;
        WindowState = WindowState.Normal;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await CaptureReviewScreenshotsAsync(_screenshotPath);
        Application.Current.Shutdown(0);
    }

    private void Window_Closed(object? sender, EventArgs e) => _viewModel.Dispose();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyDarkWindowChrome();
    }

    private async Task CaptureReviewScreenshotsAsync(string overviewPath)
    {
        var extension = Path.GetExtension(overviewPath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
        var stem = Path.Combine(Path.GetDirectoryName(overviewPath)!, Path.GetFileNameWithoutExtension(overviewPath));
        var targets = _screenshotView is { } requestedView
            ? new[] { (View: requestedView, Path: overviewPath) }
            : new[]
            {
                (View: DashboardScreenshotView.PlayGuide, Path: overviewPath),
                (View: DashboardScreenshotView.AdvancedOverview, Path: stem + ".advanced-overview" + extension),
                (View: DashboardScreenshotView.AdvancedChecklist, Path: stem + ".advanced-checklist" + extension),
                (View: DashboardScreenshotView.AdvancedCoverage, Path: stem + ".advanced-needs-coverage" + extension)
            };
        foreach (var target in targets)
        {
            SelectScreenshotView(target.View);
            await CompleteScreenshotLayoutAsync();
            SaveWindowPng(target.Path);
        }
    }

    private void SelectScreenshotView(DashboardScreenshotView view)
    {
        if (view == DashboardScreenshotView.PlayGuide)
        {
            ModeTabs.SelectedIndex = 0;
            PlayGuideScroll.ScrollToTop();
            return;
        }

        ModeTabs.SelectedIndex = 1;
        RootTabs.SelectedIndex = view switch
        {
            DashboardScreenshotView.AdvancedOverview => 0,
            DashboardScreenshotView.AdvancedChecklist => 1,
            DashboardScreenshotView.AdvancedCoverage => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, null)
        };
        if (view == DashboardScreenshotView.AdvancedChecklist && AdvancedChecklistList.Items.Count > 0)
            AdvancedChecklistList.ScrollIntoView(AdvancedChecklistList.Items[0]);
    }

    private async Task CompleteScreenshotLayoutAsync()
    {
        // Nested tabs and grouped item controls each schedule a deferred layout/render pass.
        // Waiting for multiple completed passes avoids stale, blank, or partially generated captures.
        for (var pass = 0; pass < 3; pass++)
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(120);
        }
    }

    private void ApplyDarkWindowChrome()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));

            // COLORREF values are 0x00BBGGRR.
            var border = 0x004B3426;
            var caption = 0x00160D09;
            var captionText = 0x00FCFAF8;
            _ = DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 36, ref captionText, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    private void SaveWindowPng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (Content is not FrameworkElement content)
            throw new InvalidOperationException("The dashboard window has no renderable content.");

        content.UpdateLayout();
        var contentWidthDip = content.ActualWidth;
        var contentHeightDip = content.ActualHeight;
        if (!double.IsFinite(contentWidthDip) || !double.IsFinite(contentHeightDip)
            || contentWidthDip <= 0 || contentHeightDip <= 0)
            throw new InvalidOperationException("The dashboard content has not completed layout.");

        // A FrameworkElement's margin belongs to its parent layout slot and is not part of the
        // element's own rendered pixels. Recreate that client-area gutter in the bitmap so the
        // review image matches the dashboard composition without capturing window chrome.
        var margin = content.Margin;
        var widthDip = contentWidthDip + margin.Left + margin.Right;
        var heightDip = contentHeightDip + margin.Top + margin.Bottom;
        var dpi = VisualTreeHelper.GetDpi(content);
        var widthPixels = Math.Max(1, (int)Math.Ceiling(widthDip * dpi.DpiScaleX));
        var heightPixels = Math.Max(1, (int)Math.Ceiling(heightDip * dpi.DpiScaleY));
        var bounds = new Rect(0, 0, widthDip, heightDip);
        var background = Background
            ?? Application.Current.TryFindResource("PageBrush") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Black;

        var bitmap = new RenderTargetBitmap(
            widthPixels, heightPixels, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);

        // Render the live root visual directly. Reusing the root through consecutive
        // VisualBrush snapshots can make WPF omit already-composited child regions after a
        // tab switch, which produces a large black band in later review images. The root Grid
        // is explicitly opaque; the background pass also preserves the client-area gutter if
        // a future layout adds a margin. RenderTargetBitmap remains independent of the
        // foreground desktop, monitor bounds, and screen scaling.
        var backgroundVisual = new DrawingVisual();
        using (var drawing = backgroundVisual.RenderOpen())
            drawing.DrawRectangle(background, null, bounds);
        bitmap.Render(backgroundVisual);
        bitmap.Render(content);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
