using System.IO;
using System.Media;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MessengerBubbles.Core;
using Box = MessengerBubbles.Core.Box;

namespace MessengerBubbles;

/// <summary>
/// The bubbles of Messenger Bubbles, moved onto the status bar: a chat icon (opens the chat list) followed by
/// one small avatar per conversation. An avatar with unread messages blinks until it is opened.
/// Same rules as BubbleController: at most BubbleSet.Max avatars, the rest behind a "+N" item.
/// </summary>
class MessengerStrip : StackPanel
{
    const int MaxAvatarBytes = 2_000_000;
    static readonly TimeSpan BlinkStep = TimeSpan.FromMilliseconds(550);

    readonly Func<string, Task<byte[]?>> _loadResource;
    readonly Func<string, Task<bool>> _isShowing;
    readonly Func<Box?> _panelBox;
    readonly Settings _settings;
    readonly BubbleSet _set = new();
    readonly Dictionary<string, ChatAvatar> _avatars = new();
    readonly ChatAvatar _list;
    readonly ChatAvatar _overflow;
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly PreviewWindow _preview = new();
    readonly SoundGate _sound = new();
    readonly SoundPlayer _player;
    readonly DispatcherTimer _blink = new() { Interval = BlinkStep };
    ChatAvatar? _previewFor;
    bool _blinkDim, _login, _broken;
    string? _notice;

    public event Action<string, Box>? AvatarClicked;

    /// <summary>Hidden by Ctrl+Alt+M or the menu: messages are still counted and still play the sound.</summary>
    public bool Hidden { get; private set; }

    public MessengerStrip(Settings settings, Func<string, Task<byte[]?>> loadResource, Func<string, Task<bool>> isShowing, Func<Box?> panelBox)
    {
        _settings = settings;
        _loadResource = loadResource;
        _isShowing = isShowing;
        _panelBox = panelBox;
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(0, 0, 14, 0);

        _list = ChatAvatar.ChatIcon();
        _list.MouseLeftButtonUp += (_, e) => { e.Handled = true; Open("", _list); };
        Children.Add(_list);

        _overflow = new ChatAvatar("+0") { Visibility = Visibility.Collapsed };
        _overflow.ToolTip = Texts.Current.OtherChats;
        _overflow.MouseLeftButtonUp += (_, e) => { e.Handled = true; Open("", _overflow); };
        _row.Children.Add(_overflow);
        Children.Add(_row);

        _player = new SoundPlayer(Assembly.GetExecutingAssembly().GetManifestResourceStream("new-message.wav"));
        _player.Load();
        _blink.Tick += (_, _) =>
        {
            _blinkDim = !_blinkDim;
            foreach (var a in _avatars.Values) a.Blink(_blinkDim && a.Unread > 0);
        };
        UpdateListTip();
    }

    public void ToggleHidden()
    {
        Hidden = !Hidden;
        if (Hidden) _preview.Hide();
        Visibility = Hidden ? Visibility.Collapsed : Visibility.Visible;
    }

    public async void Apply(ScanResult r)
    {
        // The user is already reading the conversation open in the panel.
        var fresh = new List<RowSnapshot>();
        // While paused the messages stay unread on Facebook; they only skip the avatar, preview and sound.
        if (!_settings.IsSnoozed(DateTime.Now))
            foreach (var row in r.NewMessages)
                if (!await _isShowing(row.ThreadHref))
                    fresh.Add(row);

        var known = _avatars.Keys.ToHashSet();
        foreach (var row in fresh) _set.OnNewMessage(row);
        foreach (var href in r.NowRead) _set.MarkRead(href);
        _list.SetBadge(r.UnreadThreads);
        Refresh();

        // New avatars load their picture in Refresh; this retries one that failed earlier.
        foreach (var row in fresh)
            if (known.Contains(row.ThreadHref) && _avatars.TryGetValue(row.ThreadHref, out var a) && a.Picture == null)
                _ = LoadPictureAsync(a, row.AvatarUrl);

        if (fresh.Count == 0) return;
        if (_settings.Sound && _sound.ShouldPlay(DateTime.Now)) _player.Play();
        var last = fresh[^1];
        if (_settings.Preview && !Hidden && _avatars.TryGetValue(last.ThreadHref, out var avatar))
        {
            // A new avatar was only just added: lay it out so it has a place on screen.
            UpdateLayout();
            if (PresentationSource.FromVisual(avatar) == null) return;
            var (name, text) = RowText.Parse(last.Text);
            _previewFor = avatar;
            _preview.ShowBeside(PixelBox(avatar), name, text, avatar.Picture, _panelBox());
        }
    }

    /// <summary>The user moved from one conversation to another inside the panel.</summary>
    public void SwitchTo(string from, RowSnapshot to)
    {
        _set.SwitchTo(from, to);
        Refresh();
    }

    /// <summary>Called after a scan and whenever a setting changes.</summary>
    public void Refresh()
    {
        foreach (var href in _avatars.Keys.Except(_set.Visible.Select(b => b.ThreadHref)).ToList())
        {
            _row.Children.Remove(_avatars[href]);
            _avatars.Remove(href);
        }
        foreach (var b in _set.Visible)
        {
            if (!_avatars.TryGetValue(b.ThreadHref, out var a))
            {
                a = NewAvatar(b);
                _avatars[b.ThreadHref] = a;
                // Newest next to the chat icon, after the "+N" item.
                _row.Children.Insert(1, a);
                _ = LoadPictureAsync(a, b.AvatarUrl);
            }
            a.SetBadge(b.Unread);
            a.Blink(b.Unread > 0 && _blinkDim);
        }
        _overflow.SetLabel($"+{_set.Overflow}");
        _overflow.Visibility = _set.Overflow > 0 ? Visibility.Visible : Visibility.Collapsed;

        var anyUnread = _set.Visible.Any(b => b.Unread > 0);
        if (anyUnread && !_blink.IsEnabled) _blink.Start();
        if (!anyUnread && _blink.IsEnabled)
        {
            _blink.Stop();
            _blinkDim = false;
        }
        UpdateListTip();
    }

    public void SetLogin(bool needed)
    {
        _login = needed;
        UpdateListTip();
    }

    public void SetBroken(bool broken)
    {
        _broken = broken;
        UpdateListTip();
    }

    /// <summary>A one-off problem worth showing on the chat icon until the next click.</summary>
    public void Notice(string text)
    {
        _notice = text;
        UpdateListTip();
    }

    void UpdateListTip()
    {
        var lines = new List<string> { "Messenger", Texts.Current.ChatList };
        if (_settings.IsSnoozed(DateTime.Now)) lines.Add(string.Format(Texts.Current.SnoozedUntil, _settings.SnoozedUntil));
        if (_login) lines.Add(Texts.Current.NeedLogin);
        if (_broken) lines.Add(Texts.Current.ListBroken);
        if (_notice != null) lines.Add(_notice);
        _list.ToolTip = string.Join("\n", lines);
        _list.SetAlert(_login || _broken || _notice != null);
    }

    ChatAvatar NewAvatar(BubbleState b)
    {
        var a = new ChatAvatar(Initial(b.Name)) { ToolTip = b.Name };
        a.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Open(b.ThreadHref, a);
        };
        // Right-click replaces dragging a bubble to the dismiss zone.
        var hide = new MenuItem { Header = "Ẩn khỏi thanh" };
        hide.Click += (_, _) =>
        {
            if (_previewFor == a) _preview.Hide();
            _set.Dismiss(b.ThreadHref);
            Refresh();
        };
        a.ContextMenu = new ContextMenu { Items = { hide } };
        // Middle click hides too.
        a.MouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            _set.Dismiss(b.ThreadHref);
            Refresh();
        };
        return a;
    }

    void Open(string href, FrameworkElement source)
    {
        _preview.Hide();
        if (href == "" && _notice != null)
        {
            _notice = null;
            UpdateListTip();
        }
        // Opening reads it; anything counted from here on came after.
        if (href != "") _set.MarkRead(href);
        Refresh();
        AvatarClicked?.Invoke(href, PixelBox(source));
    }

    /// <summary>The element's place on screen in physical pixels, which is what the chat panel works in.</summary>
    static Box PixelBox(FrameworkElement e)
    {
        var p = e.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(e);
        return new Box(p.X, p.Y, e.ActualWidth * dpi.DpiScaleX, e.ActualHeight * dpi.DpiScaleY);
    }

    public static string Initial(string name) => name.Length > 0 ? name[..1].ToUpper() : "?";

    async Task LoadPictureAsync(ChatAvatar a, string url)
    {
        var bytes = await _loadResource(url);
        if (bytes == null || bytes.Length > MaxAvatarBytes) return;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.DecodePixelWidth = 48;
            image.EndInit();
            image.Freeze();
            a.SetPicture(image);
            // A new avatar's preview appears before its picture has loaded.
            if (_previewFor == a) _preview.SetAvatar(image);
        }
        catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException)
        {
            AppPaths.Log($"avatar failed: {e.GetType().Name}");
        }
    }
}

/// <summary>A 20 DIP round face with an unread badge, sized for a 26 DIP bar.</summary>
class ChatAvatar : Grid
{
    const double Size = 20;
    static readonly Brush Blue = Frozen(Color.FromRgb(0x08, 0x66, 0xFF));
    static readonly Brush Red = Frozen(Color.FromRgb(0xE5, 0x39, 0x35));

    readonly Ellipse _face = new() { Width = Size, Height = Size, Fill = Blue };
    readonly Ellipse _ring = new() { Width = Size + 2, Height = Size + 2, StrokeThickness = 1.5, Stroke = Blue, Visibility = Visibility.Collapsed };
    readonly TextBlock _label = new()
    {
        Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };
    readonly TextBlock _badgeText = new() { Foreground = Brushes.White, FontSize = 8, FontWeight = FontWeights.Bold, Margin = new Thickness(3, 0, 3, 0) };
    readonly Border _badge;
    readonly UIElement _faceGroup;

    public BitmapSource? Picture { get; private set; }

    public int Unread { get; private set; }

    System.Windows.Shapes.Path? _logo;

    public ChatAvatar(string label) : this(label, null) { }

    ChatAvatar(string label, UIElement? glyph)
    {
        Width = Size + 6;
        Height = Size + 4;
        Margin = new Thickness(0, 0, 4, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Background = Brushes.Transparent; // hit-testable
        Cursor = Cursors.Hand;
        _label.Text = label;

        var face = new Grid { Width = Size + 2, Height = Size + 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(_ring);
        face.Children.Add(_face);
        face.Children.Add(glyph ?? _label);
        _faceGroup = face;
        Children.Add(face);

        _badge = new Border
        {
            Background = Red, CornerRadius = new CornerRadius(5), MinWidth = 10, Height = 10,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Child = _badgeText, Visibility = Visibility.Collapsed,
        };
        Children.Add(_badge);
    }

    /// <summary>The fixed chat icon at the start of the strip: the Messenger logo (Simple Icons, CC0).</summary>
    public static ChatAvatar ChatIcon()
    {
        var logo = new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse(WinStatusBar.Icons.Messenger),
            Fill = Blue, Width = Size, Height = Size, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var icon = new ChatAvatar("", logo) { _logo = logo };
        // The logo is its own shape: no round face behind it and no unread ring around it.
        icon._face.Fill = Brushes.Transparent;
        icon._ring.Stroke = Brushes.Transparent;
        return icon;
    }

    public void SetLabel(string label) => _label.Text = label;

    public void SetBadge(int count)
    {
        Unread = count;
        _badgeText.Text = count > 99 ? "99+" : count.ToString();
        _badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _ring.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One step of the unread blink: dim, then bright again.</summary>
    public void Blink(bool dim) => _faceGroup.Opacity = dim ? 0.3 : 1;

    /// <summary>Red face on the chat icon: login needed or the chat list could not be read.</summary>
    public void SetAlert(bool alert)
    {
        if (_logo != null) _logo.Fill = alert ? Red : Blue;
        else if (Picture == null) _face.Fill = alert ? Red : Blue;
    }

    public void SetPicture(BitmapSource image)
    {
        Picture = image;
        _face.Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        _label.Text = "";
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
