using System.Windows;
using System.Windows.Controls;
using MessengerBubbles.Core;
using Microsoft.Web.WebView2.Core;

namespace MessengerBubbles;

/// <summary>
/// Messenger Bubbles running inside the status bar: the same watcher, chat panel and rules as the standalone
/// app (Program.StartAsync), with the avatar strip in place of floating bubbles and a bar menu in place of the tray.
/// Uses the standalone app's folder %LOCALAPPDATA%\MessengerBubbles, so an existing Facebook login carries over.
/// </summary>
sealed class MessengerModule : IDisposable
{
    // Same name as the standalone app: holding it keeps the two from showing every message twice.
    const string MutexName = "MessengerBubbles.SingleInstance";

    Mutex? _mutex;
    readonly Settings _settings;
    Watcher? _watcher;
    ChatPanel? _panel;
    GlobalHotkey? _hotkey;
    MenuItem? _menu;
    // Retries taking the shared name: at sign-in something can hold it for a while, and giving up then left
    // Messenger off until the bar was restarted by hand.
    readonly System.Windows.Threading.DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(5) };

    public MessengerStrip Strip { get; }

    /// <summary>False while the standalone Messenger Bubbles (or anything holding its name) runs; the strip then only says so.</summary>
    public bool Active => _watcher != null;

    public MessengerModule()
    {
        Texts.Current = Texts.Vietnamese;
        _settings = Settings.Load(AppPaths.SettingsFile);
        Strip = new MessengerStrip(_settings,
            url => _watcher?.GetLoadedResourceAsync(url) ?? Task.FromResult<byte[]?>(null),
            href => _panel?.IsShowingAsync(href) ?? Task.FromResult(false),
            () => _panel?.ScreenBox);
        Strip.AvatarClicked += (href, box) => _panel?.Toggle(href, box);
        _retry.Tick += async (_, _) =>
        {
            if (!TryActivate()) return;
            _retry.Stop();
            AppPaths.Log("messenger name freed, starting");
            await StartAsync();
        };
        if (!TryActivate())
        {
            AppPaths.Log("messenger name taken, retrying");
            Strip.Notice("Messenger Bubbles bản riêng đang chạy. Thoát nó là thanh tự bật chat.");
            _retry.Start();
        }
    }

    bool TryActivate()
    {
        var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            mutex.Dispose();
            return false;
        }
        _mutex = mutex;

        _watcher = new Watcher();
        _panel = new ChatPanel(_settings);
        _hotkey = new GlobalHotkey();
        _hotkey.Pressed += Strip.ToggleHidden;
        if (!_hotkey.Registered) AppPaths.Log("hotkey taken");

        _watcher.Scanned += Strip.Apply;
        _watcher.NeedsLogin += Strip.SetLogin;
        _watcher.HealthChanged += Strip.SetBroken;
        _panel.LayoutBroken += () => Strip.Notice(Texts.Current.PanelBroken);
        var watcher = _watcher;
        _panel.LeftOnOtherThread += (from, path) =>
        {
            if (watcher.FindRow(path) is { } row) Strip.SwitchTo(from, row);
        };
        Strip.ClearNotice();
        if (_menu != null) FillMenu(_menu);
        return true;
    }

    public async Task StartAsync()
    {
        if (_watcher == null || _panel == null) return;
        AppPaths.Log("messenger starting");
        try
        {
            await _watcher.StartAsync();
            _panel.Preload();
            AppPaths.Log("messenger started");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Strip.Notice(Texts.Current.NoRuntime);
        }
        catch (Exception e)
        {
            AppPaths.Log($"messenger start failed: {e.GetType().Name}");
            Strip.Notice("Không khởi động được Messenger. Xem %LOCALAPPDATA%\\MessengerBubbles\\app.log");
        }
    }

    /// <summary>The tray menu of the standalone app, as a submenu of the bar's right-click menu.</summary>
    public MenuItem BuildMenu()
    {
        _menu = new MenuItem { Header = "Messenger" };
        FillMenu(_menu);
        return _menu;
    }

    void FillMenu(MenuItem root)
    {
        var t = Texts.Current;
        root.Items.Clear();
        if (_watcher == null)
        {
            root.Items.Add(new MenuItem { Header = "Đang tắt: Messenger Bubbles bản riêng đang chạy", IsEnabled = false });
            return;
        }

        root.Items.Add(Item(t.OpenFacebook, _watcher.ShowWindow));
        var hide = new MenuItem { Header = "Ẩn avatar chat", InputGestureText = "Ctrl+Alt+M", IsCheckable = true };
        hide.Click += (_, _) => Strip.ToggleHidden();
        root.SubmenuOpened += (_, _) => hide.IsChecked = Strip.Hidden;
        root.Items.Add(hide);
        root.Items.Add(new Separator());
        root.Items.Add(Toggle(t.Preview, _settings.Preview, on => _settings.Preview = on));
        root.Items.Add(Toggle(t.Sound, _settings.Sound, on => _settings.Sound = on));
        root.Items.Add(SnoozeMenu());
        root.Items.Add(SizeMenu());
        root.Items.Add(Toggle(t.DisableGpu, _settings.DisableGpu, on => _settings.DisableGpu = on, t.RestartToApply));

        MenuItem SnoozeMenu()
        {
            var parent = new MenuItem { Header = t.Snooze };
            var off = Item(t.SnoozeOff, () => SetSnooze(null));
            parent.Items.Add(Item(t.SnoozeHour, () => SetSnooze(Snooze.ForOneHour(DateTime.Now))));
            parent.Items.Add(Item(t.SnoozeMorning, () => SetSnooze(Snooze.UntilMorning(DateTime.Now))));
            parent.Items.Add(new Separator());
            parent.Items.Add(off);
            // The pause ends by itself, so the label is worked out each time the menu opens.
            root.SubmenuOpened += (_, _) =>
            {
                var on = _settings.IsSnoozed(DateTime.Now);
                parent.Header = on ? string.Format(t.SnoozedUntil, _settings.SnoozedUntil) : t.Snooze;
                parent.IsChecked = on;
                off.IsEnabled = on;
            };
            return parent;
        }

        MenuItem SizeMenu()
        {
            var parent = new MenuItem { Header = t.PanelSize };
            foreach (var (text, size) in new[] { (t.Small, PanelSize.Small), (t.Medium, PanelSize.Medium), (t.Large, PanelSize.Large) })
            {
                var item = new MenuItem { Header = text, IsChecked = _settings.PanelSize == size };
                item.Click += (_, _) =>
                {
                    foreach (var other in parent.Items.OfType<MenuItem>()) other.IsChecked = other == item;
                    _settings.PanelSize = size;
                    Save();
                };
                parent.Items.Add(item);
            }
            return parent;
        }
    }

    void SetSnooze(DateTime? until)
    {
        _settings.SnoozedUntil = until;
        Save();
        Strip.Refresh();
    }

    MenuItem Toggle(string text, bool state, Action<bool> apply, string? note = null)
    {
        var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = state, ToolTip = note };
        item.Click += (_, _) =>
        {
            apply(item.IsChecked);
            Save();
            Strip.Refresh();
        };
        return item;
    }

    static MenuItem Item(string text, Action click)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => click();
        return item;
    }

    void Save()
    {
        try
        {
            _settings.Save(AppPaths.SettingsFile);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            AppPaths.Log($"settings not saved: {e.GetType().Name}");
        }
    }

    public void Dispose()
    {
        _retry.Stop();
        _hotkey?.Dispose();
        _watcher?.Exit();
        _panel?.Close();
        _mutex?.Dispose();
    }
}
