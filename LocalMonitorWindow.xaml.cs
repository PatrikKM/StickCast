using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StickCast.Net;
using StickCast.Services;

namespace StickCast;

public partial class LocalMonitorWindow : Window
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private readonly DispatcherTimer _subTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private MonitorChoice _monitor;
    private List<SubtitleCue> _cues = new();
    private string _state = "STOPPED";
    private TimeSpan _duration = TimeSpan.Zero;
    private bool _isFullscreen;
    private bool _allowClose;
    private Action<string>? _log;

    internal LocalMonitorWindow(MonitorChoice monitor, Action<string>? log = null)
    {
        _monitor = monitor;
        _log = log;
        InitializeComponent();
        MonitorLabelText.Text = $"💻 {monitor.Label}  •  Dvojklik nebo F11 = celá obrazovka";
        _subTimer.Tick += (_, _) => UpdateSubtitleOverlay();
        SourceInitialized += (_, _) => PlaceOnTargetMonitor(fullscreen: false);
        Closing += OnWindowClosing;
    }

    public string TransportState => _state;
    public TimeSpan CurrentPosition => PhotoView.Visibility == Visibility.Visible ? TimeSpan.Zero : Player.Position;
    public TimeSpan CurrentDuration => _duration;

    public int VolumePercent
    {
        get => (int)Math.Round(Player.Volume * 100.0);
        set => Player.Volume = Math.Clamp(value, 0, 100) / 100.0;
    }

    public bool IsMuted
    {
        get => Player.IsMuted;
        set => Player.IsMuted = value;
    }

    internal void UpdateMonitor(MonitorChoice monitor)
    {
        _monitor = monitor;
        MonitorLabelText.Text = $"💻 {monitor.Label}  •  Dvojklik nebo F11 = celá obrazovka";
    }

    public async Task PlayMediaAsync(string url, string title, string mime, string? subtitleUrl)
    {
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Přehrávání" : title;
        Title = $"StickCast – {TitleText.Text}";

        if (!IsVisible)
        {
            Show();
            PlaceOnTargetMonitor(_isFullscreen);
        }
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();

        await LoadSubtitlesAsync(subtitleUrl);

        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            _subTimer.Stop();
            SubtitleBorder.Visibility = Visibility.Collapsed;
            Player.Stop();
            Player.Visibility = Visibility.Collapsed;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(url, UriKind.Absolute);
            bmp.EndInit();
            PhotoView.Source = bmp;
            PhotoView.Visibility = Visibility.Visible;
            _duration = TimeSpan.Zero;
            _state = "PLAYING";
            return;
        }

        PhotoView.Visibility = Visibility.Collapsed;
        PhotoView.Source = null;
        Player.Visibility = Visibility.Visible;
        _duration = TimeSpan.Zero;
        _state = "TRANSITIONING";

        Player.Stop();
        Player.Source = new Uri(url, UriKind.Absolute);
        Player.Play();
        _state = "PLAYING";
        if (_cues.Count > 0) _subTimer.Start();
        else
        {
            _subTimer.Stop();
            SubtitleBorder.Visibility = Visibility.Collapsed;
        }
    }

    public void PausePlayback()
    {
        if (PhotoView.Visibility == Visibility.Visible) return;
        Player.Pause();
        _state = "PAUSED_PLAYBACK";
    }

    public void ResumePlayback()
    {
        if (PhotoView.Visibility == Visibility.Visible) return;
        Player.Play();
        _state = "PLAYING";
    }

    public void StopPlayback()
    {
        _subTimer.Stop();
        Player.Stop();
        Player.Source = null;
        PhotoView.Source = null;
        PhotoView.Visibility = Visibility.Collapsed;
        SubtitleBorder.Visibility = Visibility.Collapsed;
        _duration = TimeSpan.Zero;
        _state = "STOPPED";
    }

    public void SeekPlayback(TimeSpan pos)
    {
        if (PhotoView.Visibility == Visibility.Visible) return;
        Player.Position = pos;
        UpdateSubtitleOverlay();
    }

    public void ForceClose()
    {
        _allowClose = true;
        _subTimer.Stop();
        try { Player.Stop(); } catch { }
        Close();
    }

    private async Task LoadSubtitlesAsync(string? subtitleUrl)
    {
        _cues.Clear();
        SubtitleBorder.Visibility = Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(subtitleUrl)) return;

        try
        {
            string raw;
            if (subtitleUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                subtitleUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                raw = await HttpClients.ShortFetch.GetStringAsync(subtitleUrl);
            }
            else if (File.Exists(subtitleUrl))
            {
                raw = await File.ReadAllTextAsync(subtitleUrl);
            }
            else
            {
                return;
            }

            _cues = Subtitles.ParseVttOrSrt(raw);
            _log?.Invoke($"Lokální přehrávač: načteno {_cues.Count} žlutých titulků.");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Lokální titulky nelze načíst: {ex.Message}");
        }
    }

    private void UpdateSubtitleOverlay()
    {
        if (_cues.Count == 0 || _state is "STOPPED" or "NO_MEDIA_PRESENT")
        {
            SubtitleBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var pos = Player.Position;
        SubtitleCue? active = null;
        for (int i = 0; i < _cues.Count; i++)
        {
            var c = _cues[i];
            if (pos >= c.Start && pos <= c.End)
            {
                active = c;
                break;
            }
        }

        if (active == null || string.IsNullOrWhiteSpace(active.Text))
        {
            SubtitleBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            SubtitleText.Text = active.Text;
            SubtitleBorder.Visibility = Visibility.Visible;
        }
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        _duration = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan : TimeSpan.Zero;
        _state = "PLAYING";
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        _subTimer.Stop();
        SubtitleBorder.Visibility = Visibility.Collapsed;
        _state = "STOPPED";
    }

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _subTimer.Stop();
        SubtitleBorder.Visibility = Visibility.Collapsed;
        _state = "STOPPED";
        _log?.Invoke($"Chyba lokálního přehrávače: {e.ErrorException?.Message}");
    }

    private void PlaceOnTargetMonitor(bool fullscreen)
    {
        _isFullscreen = fullscreen;
        int monW = _monitor.Width > 0 ? _monitor.Width : 1280;
        int monH = _monitor.Height > 0 ? _monitor.Height : 720;
        int monX = _monitor.OffsetX;
        int monY = _monitor.OffsetY;

        WindowState = WindowState.Normal;
        if (fullscreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            TopBar.Visibility = Visibility.Collapsed;
            FullscreenButton.Content = "🗗 Okno (Esc / F11)";
            SubtitleText.FontSize = 34;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, IntPtr.Zero, monX, monY, monW, monH, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            TopBar.Visibility = Visibility.Visible;
            FullscreenButton.Content = "⛶ Celá obrazovka (F11)";
            SubtitleText.FontSize = 26;

            int winW = Math.Max(640, (int)(monW * 0.78));
            int winH = Math.Max(420, (int)(monH * 0.78));
            int winX = monX + (monW - winW) / 2;
            int winY = monY + (monH - winH) / 2;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, IntPtr.Zero, winX, winY, winW, winH, SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    private void ToggleFullscreen() => PlaceOnTargetMonitor(!_isFullscreen);

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        Hide();
    }

    private void VideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _isFullscreen)
        {
            PlaceOnTargetMonitor(fullscreen: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            if (_state == "PLAYING") PausePlayback();
            else ResumePlayback();
            e.Handled = true;
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            StopPlayback();
            Hide();
        }
    }
}

public sealed class LocalMonitorHost : ILocalMediaHost, IDisposable
{
    private readonly Action<string>? _log;
    private MonitorChoice _monitor;
    private LocalMonitorWindow? _window;
    private int _volume = 80;
    private bool _muted;

    internal LocalMonitorHost(MonitorChoice monitor, Action<string>? log = null)
    {
        _monitor = monitor;
        _log = log;
    }

    internal void UpdateMonitor(MonitorChoice monitor)
    {
        _monitor = monitor;
        _window?.Dispatcher.Invoke(() => _window.UpdateMonitor(monitor));
    }

    private LocalMonitorWindow EnsureWindow()
    {
        if (_window == null)
        {
            _window = new LocalMonitorWindow(_monitor, _log)
            {
                VolumePercent = _volume,
                IsMuted = _muted,
            };
        }
        return _window;
    }

    public async Task PlayAsync(string url, string title, string mime, string? subtitleUrl)
    {
        await Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var w = EnsureWindow();
            await w.PlayMediaAsync(url, title, mime, subtitleUrl);
        }).Task.Unwrap();
    }

    public Task ResumeAsync() =>
        Application.Current.Dispatcher.InvokeAsync(() => _window?.ResumePlayback()).Task;

    public Task PauseAsync() =>
        Application.Current.Dispatcher.InvokeAsync(() => _window?.PausePlayback()).Task;

    public Task StopAsync() =>
        Application.Current.Dispatcher.InvokeAsync(() => _window?.StopPlayback()).Task;

    public Task SeekAsync(TimeSpan pos) =>
        Application.Current.Dispatcher.InvokeAsync(() => _window?.SeekPlayback(pos)).Task;

    public Task<(TimeSpan Position, TimeSpan Duration)> GetPositionAsync() =>
        Application.Current.Dispatcher.InvokeAsync(() =>
            _window != null ? (_window.CurrentPosition, _window.CurrentDuration) : (TimeSpan.Zero, TimeSpan.Zero)).Task;

    public Task<string> GetTransportStateAsync() =>
        Application.Current.Dispatcher.InvokeAsync(() => _window?.TransportState ?? "STOPPED").Task;

    public Task<int?> GetVolumeAsync() =>
        Application.Current.Dispatcher.InvokeAsync<int?>(() =>
        {
            if (_window != null) _volume = _window.VolumePercent;
            return _volume;
        }).Task;

    public Task SetVolumeAsync(int volume) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _volume = Math.Clamp(volume, 0, 100);
            if (_window != null) _window.VolumePercent = _volume;
        }).Task;

    public Task<bool?> GetMuteAsync() =>
        Application.Current.Dispatcher.InvokeAsync<bool?>(() =>
        {
            if (_window != null) _muted = _window.IsMuted;
            return _muted;
        }).Task;

    public Task SetMuteAsync(bool mute) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _muted = mute;
            if (_window != null) _window.IsMuted = mute;
        }).Task;

    public void Dispose()
    {
        if (_window != null)
        {
            try
            {
                Application.Current?.Dispatcher.Invoke(() => _window.ForceClose());
            }
            catch { }
            _window = null;
        }
    }
}
