using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using StickCast.Net;
using StickCast.Services;

namespace StickCast;

public partial class LocalMonitorWindow : Window
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static bool _vlcCoreInitialized;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _hudHideTimer = new() { Interval = TimeSpan.FromSeconds(3.2) };
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private Media? _currentMedia;

    private MonitorChoice _monitor;
    private string _state = "STOPPED";
    private TimeSpan _duration = TimeSpan.Zero;
    private string? _lastUrl;
    private string _lastTitle = "";
    private string _lastMime = "video/mp4";
    private string? _lastSubtitleUrl;
    private int _volume = 80;
    private bool _muted;
    private bool _isFullscreen;
    private bool _allowClose;
    private bool _seekDragging;
    private bool _thumbDragging;
    private bool _updatingVolumeUi;
    private System.Windows.Point _lastMousePt;
    private readonly Action<string>? _log;
    private readonly Action? _onPrevTrack;
    private readonly Action? _onNextTrack;
    private readonly Action? _onStopRequested;
    private readonly Action? _onMediaEnded;
    private readonly Action<string>? _onStateChanged;

    internal LocalMonitorWindow(
        MonitorChoice monitor,
        Action<string>? log = null,
        Action? onPrevTrack = null,
        Action? onNextTrack = null,
        Action? onStopRequested = null,
        Action? onMediaEnded = null,
        Action<string>? onStateChanged = null)
    {
        if (!_vlcCoreInitialized)
        {
            Core.Initialize();
            _vlcCoreInitialized = true;
        }

        _monitor = monitor;
        _log = log;
        _onPrevTrack = onPrevTrack;
        _onNextTrack = onNextTrack;
        _onStopRequested = onStopRequested;
        _onMediaEnded = onMediaEnded;
        _onStateChanged = onStateChanged;

        _libVlc = new LibVLC(
            "--no-video-title-show",
            "--sub-autodetect-file",
            "--freetype-color=16776960",
            "--freetype-bold",
            "--freetype-outline-thickness=4",
            "--freetype-shadow-opacity=200",
            "--freetype-background-opacity=140",
            "--freetype-rel-fontsize=16",
            "--network-caching=1000");
        _mediaPlayer = new MediaPlayer(_libVlc)
        {
            Volume = _volume,
            Mute = _muted,
        };

        InitializeComponent();

        VlcVideoView.MediaPlayer = _mediaPlayer;
        VlcVideoView.Loaded += (_, _) =>
        {
            if (VlcVideoView.MediaPlayer == null)
                VlcVideoView.MediaPlayer = _mediaPlayer;
        };

        _mediaPlayer.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            long len = _mediaPlayer.Length;
            if (len > 0) _duration = TimeSpan.FromMilliseconds(len);
            SetState("PLAYING", notifyHost: true);
        });
        _mediaPlayer.Paused += (_, _) => Dispatcher.BeginInvoke(() => SetState("PAUSED_PLAYBACK", notifyHost: true));
        _mediaPlayer.EndReached += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _uiTimer.Stop();
            SetState("STOPPED", notifyHost: false);
            _onMediaEnded?.Invoke();
        });
        _mediaPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _uiTimer.Stop();
            SetState("STOPPED", notifyHost: true);
            StateBadgeText.Text = "⚠ chyba přehrávání";
            _log?.Invoke("LibVLC: chyba při přehrávání média.");
        });

        MonitorLabelText.Text = $"💻 {monitor.Label}  •  Dvojklik nebo F11 = celá obrazovka";
        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(SeekSlider_DragStarted));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SeekSlider_DragCompleted));

        _uiTimer.Tick += (_, _) => OnUiTimerTick();
        _hudHideTimer.Tick += (_, _) =>
        {
            _hudHideTimer.Stop();
            if (_isFullscreen && !TopBar.IsMouseOver && !BottomBar.IsMouseOver && !_seekDragging)
            {
                TopBar.Visibility = Visibility.Collapsed;
                BottomBar.Visibility = Visibility.Collapsed;
            }
        };

        SourceInitialized += (_, _) => PlaceOnTargetMonitor(fullscreen: false);
        Closing += OnWindowClosing;
    }

    public string TransportState => _state;

    public TimeSpan CurrentPosition
    {
        get
        {
            if (PhotoView.Visibility == Visibility.Visible) return TimeSpan.Zero;
            long ms = _mediaPlayer.Time;
            return ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;
        }
    }

    public TimeSpan CurrentDuration => _duration;

    public int VolumePercent
    {
        get => _volume;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            _volume = clamped;
            _mediaPlayer.Volume = clamped;
            _updatingVolumeUi = true;
            VolumeSlider.Value = clamped;
            VolumeText.Text = $"{clamped}%";
            _updatingVolumeUi = false;
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            _mediaPlayer.Mute = value;
            MuteButton.Content = value ? "🔇" : "🔊";
        }
    }

    internal void UpdateMonitor(MonitorChoice monitor)
    {
        _monitor = monitor;
        MonitorLabelText.Text = $"💻 {monitor.Label}  •  Dvojklik nebo F11 = celá obrazovka";
    }

    public async Task PlayMediaAsync(string url, string title, string mime, string? subtitleUrl)
    {
        _lastUrl = url;
        _lastTitle = title;
        _lastMime = mime;
        _lastSubtitleUrl = subtitleUrl;

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

        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            _uiTimer.Stop();
            _mediaPlayer.Stop();
            VlcVideoView.Visibility = Visibility.Collapsed;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(url, UriKind.Absolute);
            bmp.EndInit();
            PhotoView.Source = bmp;
            PhotoView.Visibility = Visibility.Visible;
            _duration = TimeSpan.Zero;
            SetState("PLAYING", notifyHost: true);
            UpdateTimelineUi(TimeSpan.Zero, TimeSpan.Zero);
            return;
        }

        PhotoView.Visibility = Visibility.Collapsed;
        PhotoView.Source = null;
        VlcVideoView.Visibility = Visibility.Visible;
        if (VlcVideoView.MediaPlayer == null)
            VlcVideoView.MediaPlayer = _mediaPlayer;

        _duration = TimeSpan.Zero;
        SetState("TRANSITIONING", notifyHost: false);
        UpdateTimelineUi(TimeSpan.Zero, TimeSpan.Zero);

        var localSubPath = await ResolveLocalSubtitleFileAsync(subtitleUrl);

        await Task.Run(() =>
        {
            _mediaPlayer.Stop();
            _currentMedia?.Dispose();
            var mrl = new Uri(url, UriKind.Absolute);
            var media = new Media(_libVlc, mrl);
            if (!string.IsNullOrWhiteSpace(localSubPath) && File.Exists(localSubPath))
                media.AddOption($":sub-file={localSubPath}");
            _currentMedia = media;
            _mediaPlayer.Volume = _volume;
            _mediaPlayer.Mute = _muted;
            _mediaPlayer.Play(media);
        });

        _uiTimer.Start();
    }

    public void PausePlayback(bool notifyHost = false)
    {
        if (PhotoView.Visibility == Visibility.Visible) return;
        _mediaPlayer.SetPause(true);
        SetState("PAUSED_PLAYBACK", notifyHost);
    }

    public void ResumePlayback(bool notifyHost = false)
    {
        if (PhotoView.Visibility == Visibility.Visible) return;
        if (_state == "STOPPED" && !string.IsNullOrEmpty(_lastUrl))
        {
            _ = PlayMediaAsync(_lastUrl, _lastTitle, _lastMime, _lastSubtitleUrl);
            return;
        }
        _mediaPlayer.SetPause(false);
        SetState("PLAYING", notifyHost);
        _uiTimer.Start();
    }

    public void StopPlayback(bool notifyHost = false)
    {
        _uiTimer.Stop();
        _ = Task.Run(() => { try { _mediaPlayer.Stop(); } catch { } });
        SetState("STOPPED", notifyHost);
        UpdateTimelineUi(TimeSpan.Zero, _duration);
    }

    public void SeekPlayback(TimeSpan pos)
    {
        if (PhotoView.Visibility == Visibility.Visible) return;
        long totalMs = _mediaPlayer.Length;
        if (totalMs > 0)
            _duration = TimeSpan.FromMilliseconds(totalMs);

        long targetMs = (long)Math.Max(0, pos.TotalMilliseconds);
        if (totalMs > 0) targetMs = Math.Min(targetMs, totalMs);

        _mediaPlayer.Time = targetMs;
        UpdateTimelineUi(TimeSpan.FromMilliseconds(targetMs), _duration);
    }

    public void SeekRelative(double deltaSeconds)
    {
        if (PhotoView.Visibility == Visibility.Visible) return;
        var cur = CurrentPosition;
        var target = cur + TimeSpan.FromSeconds(deltaSeconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (_duration > TimeSpan.Zero && target > _duration) target = _duration;
        SeekPlayback(target);
    }

    public void ForceClose()
    {
        _allowClose = true;
        _uiTimer.Stop();
        _hudHideTimer.Stop();
        try { _mediaPlayer.Stop(); } catch { }
        try { _currentMedia?.Dispose(); } catch { }
        try { _mediaPlayer.Dispose(); } catch { }
        try { _libVlc.Dispose(); } catch { }
        Close();
    }

    private void SetState(string state, bool notifyHost)
    {
        _state = state;
        PlayPauseButton.Content = state == "PLAYING" ? "⏸ Pauza" : "▶ Přehrát";
        StateBadgeText.Text = state switch
        {
            "PLAYING" => "▶ přehrává",
            "PAUSED_PLAYBACK" => "⏸ pozastaveno",
            "TRANSITIONING" => "⏳ načítá…",
            _ => "⏹ zastaveno",
        };
        if (notifyHost)
            _onStateChanged?.Invoke(state);
    }

    private void OnUiTimerTick()
    {
        if (PhotoView.Visibility == Visibility.Visible) return;
        long len = _mediaPlayer.Length;
        if (len > 0)
            _duration = TimeSpan.FromMilliseconds(len);

        if (!_seekDragging)
            UpdateTimelineUi(CurrentPosition, _duration);
    }

    private void UpdateTimelineUi(TimeSpan pos, TimeSpan dur)
    {
        PosText.Text = DlnaRenderer.FormatTime(pos);
        DurText.Text = DlnaRenderer.FormatTime(dur);
        if (dur > TimeSpan.Zero)
        {
            SeekSlider.IsEnabled = true;
            SeekSlider.Maximum = dur.TotalSeconds;
            if (!_seekDragging)
                SeekSlider.Value = Math.Clamp(pos.TotalSeconds, 0, dur.TotalSeconds);
        }
        else
        {
            SeekSlider.IsEnabled = false;
            SeekSlider.Maximum = 1;
            SeekSlider.Value = 0;
        }
    }

    private async Task<string?> ResolveLocalSubtitleFileAsync(string? subtitleUrl)
    {
        if (string.IsNullOrWhiteSpace(subtitleUrl)) return null;
        try
        {
            if (File.Exists(subtitleUrl))
                return subtitleUrl;

            if (subtitleUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                subtitleUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var raw = await HttpClients.ShortFetch.GetStringAsync(subtitleUrl);
                if (string.IsNullOrWhiteSpace(raw)) return null;
                var tmp = Path.Combine(Subtitles.GetCacheDirectory(), "vlc_current.srt");
                await File.WriteAllTextAsync(tmp, raw, new System.Text.UTF8Encoding(false));
                return tmp;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Lokální titulky nelze načíst: {ex.Message}");
        }
        return null;
    }

    // ------------------------------------------------------------------ Transport bar handlers

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_state == "PLAYING") PausePlayback(notifyHost: true);
        else ResumePlayback(notifyHost: true);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback(notifyHost: false);
        _onStopRequested?.Invoke();
    }

    private void SeekBack_Click(object sender, RoutedEventArgs e) => SeekRelative(-10);

    private void SeekForward_Click(object sender, RoutedEventArgs e) => SeekRelative(10);

    private void PrevTrack_Click(object sender, RoutedEventArgs e)
    {
        PrepareForTrackSwitch("⏳ načítám předchozí…");
        _onPrevTrack?.Invoke();
    }

    private void NextTrack_Click(object sender, RoutedEventArgs e)
    {
        PrepareForTrackSwitch("⏳ načítám další…");
        _onNextTrack?.Invoke();
    }

    private void PrepareForTrackSwitch(string badgeText)
    {
        _uiTimer.Stop();
        _ = Task.Run(() => { try { _mediaPlayer.Stop(); } catch { } });
        _state = "TRANSITIONING";
        StateBadgeText.Text = badgeText;
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => IsMuted = !IsMuted;

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolumeUi) return;
        int vol = (int)Math.Round(VolumeSlider.Value);
        _volume = Math.Clamp(vol, 0, 100);
        if (_mediaPlayer != null) _mediaPlayer.Volume = _volume;
        if (VolumeText != null) VolumeText.Text = $"{_volume}%";
    }

    private void SeekSlider_MouseDown(object sender, MouseButtonEventArgs e) => _seekDragging = true;

    private void SeekSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _seekDragging = true;
        _thumbDragging = true;
    }

    private void SeekSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _thumbDragging = false;
        if (!_seekDragging) return;
        _seekDragging = false;
        SeekPlayback(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_thumbDragging || !_seekDragging) return;
        _seekDragging = false;
        SeekPlayback(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seekDragging && PosText != null)
            PosText.Text = DlnaRenderer.FormatTime(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    // ------------------------------------------------------------------ Fullscreen & placement

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
            TopBar.Visibility = Visibility.Visible;
            BottomBar.Visibility = Visibility.Visible;
            FullscreenButton.Content = "🗗 Okno (Esc / F11)";
            BottomFullscreenButton.Content = "🗗";
            _hudHideTimer.Stop();
            _hudHideTimer.Start();

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, IntPtr.Zero, monX, monY, monW, monH, SWP_NOZORDER | SWP_NOACTIVATE);
        }
        else
        {
            _hudHideTimer.Stop();
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            TopBar.Visibility = Visibility.Visible;
            BottomBar.Visibility = Visibility.Visible;
            FullscreenButton.Content = "⛶ Celá obrazovka (F11)";
            BottomFullscreenButton.Content = "⛶";

            int winW = Math.Max(680, (int)(monW * 0.80));
            int winH = Math.Max(460, (int)(monH * 0.80));
            int winX = monX + (monW - winW) / 2;
            int winY = monY + (monH - winH) / 2;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowPos(hwnd, IntPtr.Zero, winX, winY, winW, winH, SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFullscreen) return;
        var pt = e.GetPosition(this);
        if (Math.Abs(pt.X - _lastMousePt.X) < 2 && Math.Abs(pt.Y - _lastMousePt.Y) < 2)
            return;
        _lastMousePt = pt;
        TopBar.Visibility = Visibility.Visible;
        BottomBar.Visibility = Visibility.Visible;
        _hudHideTimer.Stop();
        _hudHideTimer.Start();
    }

    private void ToggleFullscreen() => PlaceOnTargetMonitor(!_isFullscreen);

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback(notifyHost: false);
        _onStopRequested?.Invoke();
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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.Escape when _isFullscreen:
                PlaceOnTargetMonitor(fullscreen: false);
                e.Handled = true;
                break;
            case Key.Space:
                if (_state == "PLAYING") PausePlayback(notifyHost: true);
                else ResumePlayback(notifyHost: true);
                e.Handled = true;
                break;
            case Key.Left:
                SeekRelative(-10);
                e.Handled = true;
                break;
            case Key.Right:
                SeekRelative(10);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Add:
            case Key.OemPlus:
                VolumePercent = Math.Clamp(VolumePercent + 5, 0, 100);
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Subtract:
            case Key.OemMinus:
                VolumePercent = Math.Clamp(VolumePercent - 5, 0, 100);
                e.Handled = true;
                break;
            case Key.M:
                IsMuted = !IsMuted;
                e.Handled = true;
                break;
            case Key.N:
            case Key.PageDown:
            case Key.MediaNextTrack:
                PrepareForTrackSwitch("⏳ načítám další…");
                _onNextTrack?.Invoke();
                e.Handled = true;
                break;
            case Key.P:
            case Key.PageUp:
            case Key.MediaPreviousTrack:
                PrepareForTrackSwitch("⏳ načítám předchozí…");
                _onPrevTrack?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            StopPlayback(notifyHost: false);
            _onStopRequested?.Invoke();
            Hide();
        }
    }
}

public sealed class LocalMonitorHost : ILocalMediaHost, IDisposable
{
    private readonly Action<string>? _log;
    private readonly Action? _onPrevTrack;
    private readonly Action? _onNextTrack;
    private readonly Action? _onStopRequested;
    private readonly Action? _onMediaEnded;
    private readonly Action<string>? _onStateChanged;
    private MonitorChoice _monitor;
    private LocalMonitorWindow? _window;
    private int _volume = 80;
    private bool _muted;

    internal LocalMonitorHost(
        MonitorChoice monitor,
        Action<string>? log = null,
        Action? onPrevTrack = null,
        Action? onNextTrack = null,
        Action? onStopRequested = null,
        Action? onMediaEnded = null,
        Action<string>? onStateChanged = null)
    {
        _monitor = monitor;
        _log = log;
        _onPrevTrack = onPrevTrack;
        _onNextTrack = onNextTrack;
        _onStopRequested = onStopRequested;
        _onMediaEnded = onMediaEnded;
        _onStateChanged = onStateChanged;
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
            _window = new LocalMonitorWindow(
                _monitor,
                _log,
                _onPrevTrack,
                _onNextTrack,
                _onStopRequested,
                _onMediaEnded,
                _onStateChanged)
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
        Application.Current.Dispatcher.InvokeAsync(() => _window?.ResumePlayback(notifyHost: false)).Task;

    public Task PauseAsync() =>
        Application.Current.Dispatcher.InvokeAsync(() => _window?.PausePlayback(notifyHost: false)).Task;

    public Task StopAsync() =>
        Application.Current.Dispatcher.InvokeAsync(() => _window?.StopPlayback(notifyHost: false)).Task;

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
