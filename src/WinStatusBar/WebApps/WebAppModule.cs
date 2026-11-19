using System.IO;
using System.Media;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MessengerBubbles;
using MessengerBubbles.Core;
using Box = MessengerBubbles.Core.Box;

namespace WinStatusBar.WebApps;

/// <summary>A site shown as a drawer from the bar. Pages from <see cref="Hosts"/> (and their subdomains) stay inside.</summary>
internal sealed record WebApp(string Name, string Url, string[] Hosts, string Letter, Color Color)
{
    public static readonly WebApp Muse = new("Muse", "https://muse.ai/",
        // Muse signs in with a Meta account.
        ["muse.ai", "meta.com", "meta.ai", "facebook.com", "instagram.com"], "M", Color.FromRgb(0x08, 0x66, 0xFF));

    string Dir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "WebApps", Name.ToLowerInvariant());

    public string ProfileDir => System.IO.Path.Combine(Dir, "profile");
    public string IconCache => System.IO.Path.Combine(Dir, "icon.png");

    public bool IsInside(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == "https" &&
        Hosts.Any(h => u.Host == h || u.Host.EndsWith("." + h));
}

/// <summary>
/// The site's icon on the bar, next to Messenger: click opens the drawer; a notification makes it blink,
/// shows a badge and the same preview card and sound as a new message.
/// </summary>
internal sealed class WebAppModule : IDisposable
{
    const double IconSize = 14;
    static readonly Brush Red = Frozen(Color.FromRgb(0xE5, 0x39, 0x35));

    readonly WebApp _app;
    readonly WebAppDrawer _drawer;
    readonly PreviewWindow _preview = new();
    readonly SoundGate _sound = new();
    readonly SoundPlayer _player;
    readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(550) };
    readonly Grid _face = new() { Width = IconSize, Height = IconSize, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _badgeText = new() { Foreground = Brushes.White, FontSize = 7, FontWeight = FontWeights.Bold, Margin = new Thickness(2, -1, 2, 0) };
    readonly Border _badge;
    BitmapSource? _icon;
    int _pending, _titleCount;
    bool _dim;

    public FrameworkElement Button { get; }

    public WebAppModule(WebApp app)
    {
        _app = app;
        _drawer = new WebAppDrawer(app);
        _player = new SoundPlayer(Assembly.GetExecutingAssembly().GetManifestResourceStream("new-message.wav"));
        _player.Load();

        _badge = new Border
        {
            Background = Red, CornerRadius = new CornerRadius(4.5), MinWidth = 9, Height = 9,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Child = _badgeText, Visibility = Visibility.Collapsed,
        };
        var root = new Grid
        {
            Width = IconSize + 7, Height = IconSize + 6, Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand, ToolTip = app.Name,
        };
        _face.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(_face);
        root.Children.Add(_badge);
        Button = root;
        SetIcon(LoadCachedIcon());

        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _preview.Hide();
            // Opening counts as reading what the notifications were about.
            _pending = 0;
            Refresh();
            _drawer.Toggle(PixelBox(root));
        };
        _blink.Tick += (_, _) =>
        {
            _dim = !_dim;
            _face.Opacity = _dim ? 0.3 : 1;
        };
        _drawer.Notified += OnNotified;
        _drawer.TitleCountChanged += n =>
        {
            _titleCount = n;
            Refresh();
        };
        _drawer.IconChanged += SetIcon;
    }

    public Task StartAsync() => _drawer.PreloadAsync();

    public MenuItem BuildMenu()
    {
        var root = new MenuItem { Header = _app.Name };
        var open = new MenuItem { Header = $"Mở {_app.Name}" };
        open.Click += (_, _) => _drawer.Toggle(PixelBox(Button));
        var reload = new MenuItem { Header = "Tải lại trang" };
        reload.Click += (_, _) => _drawer.Reload();
        root.Items.Add(open);
        root.Items.Add(reload);
        return root;
    }

    void OnNotified(string title, string body, BitmapSource? icon)
    {
        // Already looking at it.
        if (_drawer.ScreenBox != null) return;
        _pending++;
        Refresh();
        if (_sound.ShouldPlay(DateTime.Now)) _player.Play();
        if (PresentationSource.FromVisual(Button) == null) return;
        _preview.ShowBeside(PixelBox(Button), title, body, icon ?? _icon, null);
    }

    void Refresh()
    {
        var count = Math.Max(_pending, _titleCount);
        _badgeText.Text = count > 99 ? "99+" : count.ToString();
        _badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)Button).ToolTip = count > 0 ? $"{_app.Name}\n{count} thông báo mới" : _app.Name;
        // Blink only for notifications not yet opened, like an unread chat.
        if (_pending > 0 && !_blink.IsEnabled) _blink.Start();
        if (_pending == 0 && _blink.IsEnabled)
        {
            _blink.Stop();
            _dim = false;
            _face.Opacity = 1;
        }
    }

    BitmapSource? LoadCachedIcon()
    {
        try
        {
            return File.Exists(_app.IconCache) ? WebAppDrawer.Decode(File.ReadAllBytes(_app.IconCache)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The site's own icon once known; until then a letter in a circle.</summary>
    void SetIcon(BitmapSource? image)
    {
        _icon = image ?? _icon;
        _face.Children.Clear();
        if (_icon != null)
        {
            var img = new Image { Source = _icon, Width = IconSize, Height = IconSize };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            _face.Children.Add(img);
            return;
        }
        _face.Children.Add(new Ellipse { Fill = Frozen(_app.Color) });
        _face.Children.Add(new TextBlock
        {
            Text = _app.Letter, Foreground = Brushes.White, FontSize = 8, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
    }

    static Box PixelBox(FrameworkElement e)
    {
        var p = e.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(e);
        return new Box(p.X, p.Y, e.ActualWidth * dpi.DpiScaleX, e.ActualHeight * dpi.DpiScaleY);
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public void Dispose()
    {
        _blink.Stop();
        _preview.Close();
        _drawer.Close();
    }
}
