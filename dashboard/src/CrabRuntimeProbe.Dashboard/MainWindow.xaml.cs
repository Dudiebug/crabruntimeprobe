using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Application = System.Windows.Application;

namespace CrabRuntimeProbe.Dashboard;

public enum DashboardScreenshotView
{
    Simple,
    Overview,
    Checklist,
    Coverage
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

    private async Task CaptureReviewScreenshotsAsync(string overviewPath)
    {
        var extension = Path.GetExtension(overviewPath);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
        var stem = Path.Combine(Path.GetDirectoryName(overviewPath)!, Path.GetFileNameWithoutExtension(overviewPath));
        var targets = _screenshotView is { } requestedView
            ? new[] { (View: requestedView, Path: overviewPath) }
            : new[]
            {
                (View: DashboardScreenshotView.Simple, Path: overviewPath),
                (View: DashboardScreenshotView.Overview, Path: stem + ".overview" + extension),
                (View: DashboardScreenshotView.Checklist, Path: stem + ".checklist" + extension),
                (View: DashboardScreenshotView.Coverage, Path: stem + ".needs-coverage" + extension)
            };
        foreach (var target in targets)
        {
            SelectScreenshotView(target.View);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            // The grouped checklist has a deferred virtualization pass after its first layout.
            // Wait for that pass so automated visual review captures the real rendered tab.
            await Task.Delay(1000);
            UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            SaveWindowPng(target.Path);
        }
    }

    private void SelectScreenshotView(DashboardScreenshotView view)
    {
        RootTabs.SelectedIndex = view switch
        {
            DashboardScreenshotView.Simple => 0,
            DashboardScreenshotView.Overview => 0,
            DashboardScreenshotView.Checklist => 1,
            DashboardScreenshotView.Coverage => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, null)
        };
    }

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
        var sourceBounds = new Rect(0, 0, contentWidthDip, contentHeightDip);
        var contentBounds = new Rect(margin.Left, margin.Top, contentWidthDip, contentHeightDip);
        var background = Background
            ?? Application.Current.TryFindResource("PageBrush") as System.Windows.Media.Brush
            ?? System.Windows.Media.Brushes.Black;

        // Draw the content through a VisualBrush over an explicit background. Rendering the
        // window directly leaves transparent pixels where its root Grid has no background;
        // those pixels appear white in many image viewers. This path is also independent of
        // the desktop, foreground window, monitor bounds, and screen scaling.
        var snapshot = new DrawingVisual();
        using (var drawing = snapshot.RenderOpen())
        {
            drawing.DrawRectangle(background, null, bounds);
            drawing.DrawRectangle(new VisualBrush(content)
            {
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                Stretch = Stretch.Fill,
                Viewbox = sourceBounds,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = contentBounds,
                ViewportUnits = BrushMappingMode.Absolute
            }, null, contentBounds);
        }

        var bitmap = new RenderTargetBitmap(
            widthPixels, heightPixels, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(snapshot);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
