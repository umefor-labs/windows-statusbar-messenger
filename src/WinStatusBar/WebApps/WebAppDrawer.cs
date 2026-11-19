using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MessengerBubbles;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Box = MessengerBubbles.Core.Box;
using Geometry = MessengerBubbles.Core.Geometry;

namespace WinStatusBar.WebApps;

/// <summary>
/// A web app kept loaded in the background and shown as a drawer from the left edge of the screen,
/// like the Messenger chat panel. Reports the page's notifications and the unread count in its title.
/// </summary>
internal sealed class WebAppDrawer : Window
{
    const uint SwpNoZOrder = 0x0004, SwpNoSize = 0x0001, MonitorDefaultToNearest = 2;
    const int DwmwaWindowCornerPreference = 33, DwmwcpDoNotRound = 1;
    const double SlideMs = 200, DrawerWidthDip = 420;

    [StructLayout(LayoutKind.Sequential)] struct Point32 { public int X, Y; }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point32 point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    static readonly Regex TitleCount = new(@"^\((\d+)\+?\)");

    readonly WebApp _app;
    readonly WebView2 _web = new();
    readonly Border _frame = new();
    Task? _init;
    DispatcherTimer? _slide;
    DateTime _hiddenAt, _deactivatedAt;
    bool _expanded;
    Box? _anchor;
    TextBlock? _expandGlyph;

    /// <summary>A notification from the page: title, body, icon (may be null).</summary>
    public event Action<string, string, BitmapSource?>? Notified;
    /// <summary>The count the page puts in its title, like "(3) Muse"; 0 when none.</summary>
    public event Action<int>? TitleCountChanged;
    /// <summary>The site's icon, once the page has loaded it.</summary>
    public event Action<BitmapSource>? IconChanged;

    public Box? ScreenBox { get; private set; }

    public WebAppDrawer(WebApp app)
    {
        _app = app;
        Title = app.Name;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = DrawerWidthDip;
        Height = 600;
        _frame.Child = _web;
        _frame.BorderThickness = new Thickness(0, 0, 1, 0);
        _frame.SetResourceReference(Border.BorderBrushProperty, "BarBorder");
        var page = new DockPanel();
        var bar = BuildTitleBar();
        DockPanel.SetDock(bar, Dock.Top);
        page.Children.Add(bar);
        page.Children.Add(_frame);
        Content = page;
        Deactivated += (_, _) =>
        {
            _deactivatedAt = DateTime.Now;
            // Expanded it behaves like a maximized window: other windows can go over it.
            if (!_expanded) HideDrawer();
        };
    }

    /// <summary>A thin strip above the page: the name, expand/restore, close. Double-click also expands.</summary>
    FrameworkElement BuildTitleBar()
    {
        var bar = new DockPanel { Height = 28, LastChildFill = true };
        bar.SetResourceReference(Panel.BackgroundProperty, "BarBackground");
        var close = TitleButton("\uE711", "Đóng", HideDrawer);
        var expand = TitleButton("\uE740", "Mở rộng toàn màn hình", () => SetExpanded(!_expanded));
        _expandGlyph = (TextBlock)((Border)expand).Child;
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(expand, Dock.Right);
        bar.Children.Add(close);
        bar.Children.Add(expand);
        var title = new TextBlock { Text = _app.Label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "BarText");
        bar.Children.Add(title);
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) SetExpanded(!_expanded);
        };
        return new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) }.Also(b => b.SetResourceReference(Border.BorderBrushProperty, "BarBorder"));
    }

    static FrameworkElement TitleButton(string glyph, string tip, Action click)
    {
        var text = new TextBlock
        {
            Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "BarText");
        var button = new Border { Width = 40, Background = System.Windows.Media.Brushes.Transparent, Child = text, ToolTip = tip, Cursor = Cursors.Hand };
        button.MouseEnter += (_, _) => button.SetResourceReference(Border.BackgroundProperty, "BarBorder");
        button.MouseLeave += (_, _) => button.Background = System.Windows.Media.Brushes.Transparent;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        // Keep a double-click on a button from also reaching the title bar.
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return button;
    }

    /// <summary>Expanded fills the whole work area, like a maximized window; restored is the side drawer.</summary>
    void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        _expandGlyph!.Text = expanded ? "\uE73F" : "\uE740";
        ((FrameworkElement)_expandGlyph.Parent).ToolTip = expanded ? "Thu về khung bên cạnh" : "Mở rộng toàn màn hình";
        Topmost = !expanded;
        if (IsVisible && _anchor is { } anchor) Place(anchor, slide: false);
        Activate();
    }

    /// <summary>Loads the page off-screen at startup, so notifications arrive before the first click.</summary>
    public async Task PreloadAsync()
    {
        Left = -32000;
        Top = -32000;
        Show();
        try
        {
            await (_init ??= InitAsync());
        }
        catch (Exception e) when (e is not WebView2RuntimeNotFoundException)
        {
            UsageService.Log($"{_app.Name}: load failed: {e.GetType().Name}");
            _init = null;
        }
        if (ScreenBox == null) Hide();
    }

    async Task InitAsync()
    {
        Directory.CreateDirectory(_app.ProfileDir);
        var env = await CoreWebView2Environment.CreateAsync(null, _app.ProfileDir);
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.PermissionRequested += (_, e) =>
        {
            // The whole point: let the page notify, and show those notifications on the bar.
            if (e.PermissionKind == CoreWebView2PermissionKind.Notifications)
                e.State = CoreWebView2PermissionState.Allow;
        };
        core.NotificationReceived += OnNotification;
        core.NewWindowRequested += (_, e) =>
        {
            // Sign-in popups stay inside; other links open in the normal browser.
            e.Handled = true;
            if (_app.IsInside(e.Uri)) core.Navigate(e.Uri);
            else if (e.IsUserInitiated) OpenExternal(e.Uri);
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            var m = TitleCount.Match(core.DocumentTitle ?? "");
            TitleCountChanged?.Invoke(m.Success ? int.Parse(m.Groups[1].Value) : 0);
        };
        core.FaviconChanged += async (_, _) => await ReadFaviconAsync(core);
        core.ProcessFailed += (_, e) =>
        {
            UsageService.Log($"{_app.Name}: process failed: {e.ProcessFailedKind}");
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                Dispatcher.InvokeAsync(() => core.Reload());
        };
        _web.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideDrawer();
        };
        core.Navigate(_app.Url);
    }

    async void OnNotification(object? sender, CoreWebView2NotificationReceivedEventArgs e)
    {
        // Shown by the bar instead of Windows.
        e.Handled = true;
        var n = e.Notification;
        BitmapSource? icon = null;
        try
        {
            if (Uri.TryCreate(n.IconUri, UriKind.Absolute, out var u) && u.Scheme == "https")
                icon = await LoadImageAsync(u);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            UsageService.Log($"{_app.Name}: notification icon failed: {ex.GetType().Name}");
        }
        n.ReportShown();
        Notified?.Invoke(n.Title ?? _app.Name, n.Body ?? "", icon);
    }

    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    static async Task<BitmapSource?> LoadImageAsync(Uri uri)
    {
        var bytes = await Http.GetByteArrayAsync(uri);
        return Decode(bytes);
    }

    async Task ReadFaviconAsync(CoreWebView2 core)
    {
        try
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            if (copy.Length == 0) return;
            var image = Decode(copy.ToArray());
            if (image == null) return;
            File.WriteAllBytes(_app.IconCache, copy.ToArray());
            IconChanged?.Invoke(image);
        }
        catch (Exception e) when (e is IOException or COMException or NotSupportedException or InvalidOperationException)
        {
            UsageService.Log($"{_app.Name}: favicon failed: {e.GetType().Name}");
        }
    }

    public static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.DecodePixelWidth = 48;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public async void Toggle(Box anchor)
    {
        if (IsVisible && ScreenBox != null)
        {
            // Expanded and left behind other windows a while ago: bring it back to the front.
            if (_expanded && !IsActive && DateTime.Now - _deactivatedAt > TimeSpan.FromMilliseconds(500))
                Activate();
            else
                HideDrawer();
            return;
        }
        // Clicking the bar deactivates the drawer before the click lands: that click meant "close".
        if (DateTime.Now - _hiddenAt < TimeSpan.FromMilliseconds(500)) return;

        _anchor = anchor;
        Place(anchor, slide: true);
        Show();
        Activate();
        try
        {
            await (_init ??= InitAsync());
            _web.Focus();
        }
        catch (Exception e) when (e is not WebView2RuntimeNotFoundException)
        {
            UsageService.Log($"{_app.Name}: open failed: {e.GetType().Name}");
            _init = null;
        }
    }

    public void Reload()
    {
        if (_web.CoreWebView2 != null) _web.CoreWebView2.Navigate(_app.Url);
    }

    void HideDrawer()
    {
        if (!IsVisible || ScreenBox == null) return;
        ScreenBox = null;
        _hiddenAt = DateTime.Now;
        _slide?.Stop();
        Hide();
    }

    void Place(Box anchor, bool slide)
    {
        var monitor = MonitorFromPoint(new Point32 { X = (int)anchor.X, Y = (int)anchor.Y }, MonitorDefaultToNearest);
        GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var area = FloatingWindow.WorkAreaAt(anchor.X, anchor.Y);
        var box = _expanded ? area : Geometry.PlaceDrawerNear(anchor, DrawerWidthDip * dpi / 96.0, area);
        var right = Geometry.OnRightHalf(anchor, area);
        // The hairline goes on the side facing the screen; the drawer slides in from its own edge.
        _frame.BorderThickness = _expanded ? new Thickness(0) : right ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);
        var hiddenX = right ? box.X + box.W : box.X - box.W;
        ScreenBox = box;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var corners = DwmwcpDoNotRound;
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));

        _slide?.Stop();
        if (!slide || !SystemParameters.ClientAreaAnimation)
        {
            SetWindowPos(handle, IntPtr.Zero, (int)box.X, (int)box.Y, (int)box.W, (int)box.H, SwpNoZOrder);
            return;
        }
        SetWindowPos(handle, IntPtr.Zero, (int)hiddenX, (int)box.Y, (int)box.W, (int)box.H, SwpNoZOrder);
        var clock = Stopwatch.StartNew();
        _slide = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(12) };
        _slide.Tick += (_, _) =>
        {
            var t = Math.Min(1, clock.ElapsedMilliseconds / SlideMs);
            var eased = 1 - Math.Pow(1 - t, 3);
            SetWindowPos(handle, IntPtr.Zero, (int)(hiddenX + (box.X - hiddenX) * eased), (int)box.Y, 0, 0, SwpNoZOrder | SwpNoSize);
            if (t >= 1) _slide?.Stop();
        };
        _slide.Start();
    }

    static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }
}

internal static class ElementExtensions
{
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }
}
