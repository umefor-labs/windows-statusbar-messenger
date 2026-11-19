using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace WinStatusBar;

public partial class MainWindow : Window
{
    private const double BarHeight = 26;
    private static readonly TimeSpan UsageInterval = TimeSpan.FromMinutes(5);

    public MainWindow()
    {
        InitializeComponent();

        // Left: AI usage meters.
        AddMeter("Claude", Icons.Claude, "#D97757", UsageService.Claude);
        _codex = new UsageMeter("Codex", Icons.OpenAI, null);
        _codex.ToolTip = "Codex\nChưa có dữ liệu: dùng Codex một lần để có số.";
        UsagePanel.Children.Add(_codex);
        var agIcon = ExeIcon.Load(AntigravityUsage.ExePath());
        _antigravity = agIcon != null ? new UsageMeter("Antigravity", agIcon) : new UsageMeter("Antigravity", Icons.Gemini, "#8E75B2");
        _antigravity.ToolTip = "Antigravity\nMở Antigravity để cập nhật.";
        UsagePanel.Children.Add(_antigravity);

        // Messenger: chat avatars at the far left of the bar; they blink on new messages.
        _messenger = new MessengerBubbles.MessengerModule();
        UsagePanel.Children.Insert(0, _messenger.Strip);
        var menu = ContextMenu;
        menu.Items.Insert(menu.Items.IndexOf(MoveMenu) + 1, _messenger.BuildMenu());
        // Facebook's page can change under the Messenger code; a failure there must not take the bar down.
        Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            if (e.Exception.StackTrace?.Contains("MessengerBubbles.") != true) return;
            UsageService.Log($"messenger: {e.Exception.GetType().Name}: {e.Exception.Message}");
            e.Handled = true;
        };

        // Center: media.
        MediaHost.Content = new MediaPanel();

        // Right: system stats. NET first so its changing width never shifts the others.
        foreach (var (key, label, minWidth) in new[]
        {
            ("NET", "NET", 0.0), ("CPU", "CPU", 30.0), ("RAM", "RAM", 60.0), ("GPU", "GPU", 30.0),
            ("CPUT", "CPU°", 24.0), ("GPUT", "GPU°", 0.0),
        })
            _stats[key] = AddStat(label, minWidth);

        SourceInitialized += (_, _) =>
        {
            _settings = Settings.Load();
            _appBar = new AppBar(this, BarHeight, _settings.Edge);
            ApplyEdgeVisuals();
            _usage = new UsageService(new WindowInteropHelper(this).Handle);

            // GPU rendering sometimes leaves stale pixels on screen (e.g. after sleep), so old
            // and new bar values overlap. The bar is tiny, so software rendering costs nothing.
            var src = (HwndSource)PresentationSource.FromVisual(this);
            src.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                    Dispatcher.BeginInvoke(new Action(ForceRedraw));
            };
        };
        Loaded += (_, _) =>
        {
            StartMonitor();
            StartUsageLoop();
            StartAntigravityLoop();
            _ = _messenger.StartAsync();
        };
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _messenger.Dispose();
            _appBar?.Dispose();
        };

        // If we crash, still give the reserved strip back to Windows.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _appBar?.Dispose();
    }

    private UsageMeter AddMeter(string name, string icon, string? color, UsageProvider? provider)
    {
        var meter = new UsageMeter(name, icon, color);
        UsagePanel.Children.Add(meter);
        if (provider != null)
        {
            _meters[provider] = meter;
            meter.MouseLeftButtonUp += (_, _) => OpenLogin(provider);
        }
        return meter;
    }

    private void OpenLogin(UsageProvider provider)
    {
        var win = new LoginWindow(provider);
        win.Closed += async (_, _) => await RefreshAsync(provider);
        win.Show();
    }

    private void StartUsageLoop()
    {
        var token = _cts.Token;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                foreach (var p in _meters.Keys.ToList())
                    await RefreshAsync(p);
                try { await Task.Delay(UsageInterval, token); } catch (TaskCanceledException) { }
            }
        });
    }

    private void StartAntigravityLoop()
    {
        var token = _cts.Token;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var r = await Task.Run(AntigravityUsage.FetchAsync);
                if (r != null) _antigravity.Show(r); // null = Antigravity closed: keep last reading
                var c = await Task.Run(CodexLocalUsage.Fetch);
                if (c != null) _codex.Show(c);
                try { await Task.Delay(TimeSpan.FromMinutes(1), token); } catch (TaskCanceledException) { }
            }
        });
    }

    private async Task RefreshAsync(UsageProvider p)
    {
        if (_usage == null)
        {
            UsageService.Log($"{p.Name}: usage service not ready");
            return;
        }
        var result = await _usage.FetchAsync(p);
        _meters[p].Show(result);
    }

    private void StartMonitor()
    {
        var token = _cts.Token;
        Task.Run(async () =>
        {
            using var monitor = new SystemMonitor();
            while (!token.IsCancellationRequested)
            {
                var s = monitor.Sample();
                await Dispatcher.InvokeAsync(() => Show(s));
                try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }
            }
        }, token);
    }

    private void Show(SystemSnapshot s)
    {
        _stats["CPU"].Text = Pct(s.Cpu);
        _stats["RAM"].Text = $"{s.RamUsedGb:0.0}/{s.RamTotalGb:0}G";
        _stats["GPU"].Text = Pct(s.Gpu);
        _stats["CPUT"].Text = Temp(s.CpuTemp);
        _stats["GPUT"].Text = Temp(s.GpuTemp);
        _stats["NET"].Text = $"↓{Rate(s.NetDown)} ↑{Rate(s.NetUp)}";
    }

    private static string Pct(double? v) => v is double d ? $"{d:0}%" : "--";
    private static string Temp(double? v) => v is double d ? $"{d:0}°" : "--";

    private static string Rate(double bytesPerSec)
    {
        double k = bytesPerSec / 1024;
        if (k < 1000) return $"{k:0}K";
        double m = k / 1024;
        return m < 100 ? $"{m:0.0}M" : $"{m:0}M";
    }

    private TextBlock AddStat(string label, double minWidth)
    {
        var name = new TextBlock { Text = label + " ", VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "BarMuted");
        var value = new TextBlock { Text = "--", MinWidth = minWidth, VerticalAlignment = VerticalAlignment.Center };
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        item.Children.Add(name);
        item.Children.Add(value);
        StatsPanel.Children.Add(item);
        return value;
    }

    private void ForceRedraw()
    {
        InvalidateVisual();
        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero) RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, 0x0185); // INVALIDATE|ERASE|ALLCHILDREN|UPDATENOW
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr rgn, uint flags);

    private void ApplyEdgeVisuals()
    {
        bool bottom = _settings.Edge == BarEdge.Bottom;
        RootBorder.BorderThickness = bottom ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1);
        MoveMenu.Header = bottom ? "Chuyển lên trên" : "Chuyển xuống dưới";
    }

    private void OnMoveClick(object sender, RoutedEventArgs e)
    {
        _settings.Edge = _settings.Edge == BarEdge.Bottom ? BarEdge.Top : BarEdge.Bottom;
        _settings.Save();
        _appBar?.SetEdge(_settings.Edge);
        ApplyEdgeVisuals();
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private readonly Dictionary<string, TextBlock> _stats = new();
    private readonly Dictionary<UsageProvider, UsageMeter> _meters = new();
    private readonly CancellationTokenSource _cts = new();
    private AppBar? _appBar;
    private UsageService? _usage;
    private readonly UsageMeter _antigravity;
    private readonly UsageMeter _codex;
    private readonly MessengerBubbles.MessengerModule _messenger;
    private Settings _settings = new();
}
