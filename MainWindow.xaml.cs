using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using StickCast.Net;
using StickCast.Services;

namespace StickCast;

public partial class MainWindow : Window
{
    private readonly MediaServer _server = new();
    private readonly ObservableCollection<DlnaRenderer> _devices = new();
    private readonly ObservableCollection<PlaylistItem> _files = new();
    private readonly ObservableCollection<UrlEntry> _history = new();
    private readonly ObservableCollection<UrlEntry> _favorites = new();
    private readonly ObservableCollection<YtSearchResult> _ytResults = new();
    private readonly ObservableCollection<MonitorChoice> _monitors = new();
    private readonly ObservableCollection<AudioChoice> _audioDevices = new();
    private readonly Dictionary<int, List<YtSearchResult>> _ytPageCache = new();
    private readonly Dictionary<string, LocalMonitorHost> _localMonitorHosts = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _playLock = new(1, 1);

    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _volumeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _slideshowTimer = new();
    private readonly TrayIcon _tray;
    private readonly bool _uiReady;

    private GlobalHotkeyHost? _hotkeyHost;
    private ScreenStream? _screenStream;
    private TranscodeStream? _transcodeStream;
    private MiniPlayerWindow? _miniPlayer;
    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _resolveCts;
    private bool _isScreenStreaming;
    private bool _forceExit;
    private bool _pcPlaybackActive;
    private bool _polling, _discovering, _isStartingPlayback;
    private bool _seekDragging, _thumbDragging, _volumeFromDevice, _muted, _userStopped;
    private string _lastState = "";
    private string _ytLastQuery = "";
    private int _ytPageIndex;
    private bool _ytLastIsMusic;
    private bool _playingFromYtSearch;
    private int _currentYtIndex = -1;
    private DateTime _ignoreStateUntil = DateTime.MinValue;
    private DateTime _ignoreSeekUntil = DateTime.MinValue;
    private TimeSpan _lastPosition = TimeSpan.Zero;
    private TimeSpan _lastDuration = TimeSpan.Zero;
    private int _currentFileIndex = -1;
    private bool _playingFromPlaylist;
    private long _playbackEpoch;
    private long _playRequestId;
    private string? _lastPlayedUrl;
    private int _consecutivePollFailures;
    private int _pollCount;

    private DlnaRenderer? Current => DevicesCombo?.SelectedItem as DlnaRenderer;

    public MainWindow()
    {
        _tray = new TrayIcon("StickCast – ovladač TV sticku", () => IsVisible && WindowState != WindowState.Minimized);

        InitializeComponent();
        RestoreWindowPlacement();

        DevicesCombo.ItemsSource = _devices;
        FilesList.ItemsSource = _files;
        HistoryList.ItemsSource = _history;
        FavoritesList.ItemsSource = _favorites;
        YtResultsList.ItemsSource = _ytResults;
        MonitorCombo.ItemsSource = _monitors;
        AudioDeviceCombo.ItemsSource = _audioDevices;

        foreach (var h in _settings.HistoryEntries) _history.Add(h);
        foreach (var f in _settings.FavoriteEntries) _favorites.Add(f);
        AutoNextCheck.IsChecked = _settings.AutoNext;
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
        ShuffleCheck.IsChecked = _settings.Shuffle;
        TranscodeCheck.IsChecked = _settings.TranscodeMode;
        AutoCzechSubtitlesCheck.IsChecked = _settings.AutoCzechSubtitles;
        BurnSubtitlesCheck.IsChecked = _settings.BurnSubtitles;
        HotspotSsidBox.Text = _settings.HotspotSsid;
        HotspotPassBox.Text = _settings.HotspotPassword;
        AdbConnectEndpointBox.Text = _settings.LastAdbEndpoint;
        SelectComboByTag(RepeatCombo, _settings.RepeatMode, 0);
        SelectComboByTag(SlideshowCombo, _settings.SlideshowSeconds.ToString(), 0);
        SelectComboByTag(YtAutoPlayCombo, _settings.YtAutoPlayNext ? "true" : "false", 0);

        _volumeFromDevice = true;
        VolumeSlider.Value = _settings.Volume;
        VolumeText.Text = $"{_settings.Volume}%";
        _volumeFromDevice = false;

        _uiReady = true;
        PopulateMonitors();
        PopulateLocalMonitorDevices();
        UpdateDeviceControlsState();
        _audioDevices.Add(new AudioChoice("(bez zvuku)", null));
        AudioDeviceCombo.SelectedIndex = 0;
        RefreshToolsStatus();

        PositionSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(PositionSlider_DragStarted));
        PositionSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(PositionSlider_DragCompleted));

        _server.Log = s => Dispatcher.BeginInvoke(() => Log(s));
        try
        {
            _server.Start();
            Log($"Mediální server běží na portu {_server.Port}");
        }
        catch (Exception ex) { Log("Mediální server nelze spustit: " + ex.Message); }

        SetupTimersAndTray();
    }

    private void SetupTimersAndTray()
    {
        _pollTimer.Tick += async (_, _) => await SafeAsync(PollAsync, quiet: true);

        _volumeTimer.Tick += async (_, _) =>
        {
            _volumeTimer.Stop();
            await SafeAsync(async () =>
            {
                if (Current is { HasVolume: true } r) await r.SetVolumeAsync((int)VolumeSlider.Value);
            }, quiet: true);
        };

        _slideshowTimer.Tick += async (_, _) =>
        {
            _slideshowTimer.Stop();
            if (!_playingFromPlaylist || _userStopped || _files.Count == 0 || _settings.SlideshowSeconds <= 0) return;
            var next = GetNextPlaylistIndex(manual: false);
            if (next >= 0) await SafeAsync(() => PlayFileAsync(next));
        };

        _tray.ToggleWindowRequested += () => Dispatcher.BeginInvoke(ToggleWindowVisibility);
        _tray.PauseResumeRequested += () => Dispatcher.BeginInvoke(() => _ = SafeAsync(TogglePauseResumeAsync));
        _tray.StopRequested += () => Dispatcher.BeginInvoke(() => _ = SafeAsync(StopPlaybackAsync));
        _tray.MiracastRequested += () => Dispatcher.BeginInvoke(() =>
        {
            ShowAndActivate();
            Miracast_Click(this, new RoutedEventArgs());
        });
        _tray.ExitRequested += () => Dispatcher.BeginInvoke(() =>
        {
            _forceExit = true;
            Close();
        });

        SourceInitialized += (_, _) =>
        {
            _hotkeyHost = new GlobalHotkeyHost(this);
            _hotkeyHost.PlayPausePressed += () => _ = SafeAsync(TogglePauseResumeAsync, quiet: true);
            _hotkeyHost.PrevPressed += () => _ = SafeAsync(PrevAsync, quiet: true);
            _hotkeyHost.NextPressed += () => _ = SafeAsync(NextAsync, quiet: true);
        };
        Loaded += async (_, _) => await SafeAsync(StartupAsync, quiet: true);
        Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceExit && MinimizeToTrayCheck.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            SaveSettings();
            return;
        }

        _downloadCts?.Cancel();
        _cts.Cancel();
        _pollTimer.Stop();
        _volumeTimer.Stop();
        _slideshowTimer.Stop();
        _hotkeyHost?.Dispose();
        StopScreenStream();
        StopTranscodeStream();
        _miniPlayer?.Close();
        foreach (var host in _localMonitorHosts.Values) host.Dispose();
        _localMonitorHosts.Clear();
        _tray.Dispose();
        SaveSettings();
        _server.Dispose();
    }

    // =================================================================== settings & window

    private void ToggleWindowVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized) Hide();
        else ShowAndActivate();
    }

    private void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void MinimizeToTrayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_uiReady) _settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
    }

    private void ShuffleCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_uiReady) _settings.Shuffle = ShuffleCheck.IsChecked == true;
    }

    private void TranscodeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_uiReady) _settings.TranscodeMode = TranscodeCheck.IsChecked == true;
    }

    private void AutoCzechSubtitlesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_uiReady) _settings.AutoCzechSubtitles = AutoCzechSubtitlesCheck.IsChecked == true;
    }

    private void BurnSubtitlesCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_uiReady) _settings.BurnSubtitles = BurnSubtitlesCheck.IsChecked == true;
    }

    private void YtAutoPlayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiReady && YtAutoPlayCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            _settings.YtAutoPlayNext = tag.Equals("true", StringComparison.OrdinalIgnoreCase);
            _settings.Save();
        }
    }

    private void RepeatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiReady && RepeatCombo.SelectedItem is ComboBoxItem item && item.Tag is string mode)
            _settings.RepeatMode = mode;
    }

    private static void SelectComboByTag(ComboBox combo, string tag, int fallbackIndex)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = fallbackIndex;
    }

    private void SlideshowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        if (SlideshowCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var sec))
        {
            _settings.SlideshowSeconds = sec;
            if (sec <= 0) _slideshowTimer.Stop();
            else if (_playingFromPlaylist && _currentFileIndex >= 0 && _currentFileIndex < _files.Count &&
                     !_files[_currentFileIndex].IsUrl &&
                     Mime.FromPath(_files[_currentFileIndex].Path).StartsWith("image", StringComparison.OrdinalIgnoreCase) &&
                     (_files.Count > 1 || _settings.RepeatMode == "one"))
            {
                _slideshowTimer.Interval = TimeSpan.FromSeconds(sec);
                _slideshowTimer.Start();
            }
        }
    }

    private void RestoreWindowPlacement()
    {
        if (double.IsFinite(_settings.WindowWidth) && double.IsFinite(_settings.WindowHeight) &&
            _settings.WindowWidth >= MinWidth && _settings.WindowHeight >= MinHeight)
        {
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
        }

        if (double.IsFinite(_settings.WindowLeft) && double.IsFinite(_settings.WindowTop))
        {
            var vLeft = SystemParameters.VirtualScreenLeft;
            var vTop = SystemParameters.VirtualScreenTop;
            var vRight = vLeft + SystemParameters.VirtualScreenWidth;
            var vBottom = vTop + SystemParameters.VirtualScreenHeight;

            if (_settings.WindowLeft >= vLeft - 40 && _settings.WindowLeft < vRight - 80 &&
                _settings.WindowTop >= vTop - 20 && _settings.WindowTop < vBottom - 80)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.WindowLeft;
                Top = _settings.WindowTop;
            }
        }

        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveSettings()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.HistoryEntries = _history.Take(50).ToList();
        _settings.FavoriteEntries = _favorites.Take(200).ToList();
        _settings.RecentUrls = _history.Select(h => h.Url).Take(50).ToList();
        _settings.Volume = (int)VolumeSlider.Value;
        _settings.AutoNext = AutoNextCheck.IsChecked == true;
        _settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
        _settings.Shuffle = ShuffleCheck.IsChecked == true;
        _settings.TranscodeMode = TranscodeCheck.IsChecked == true;
        _settings.AutoCzechSubtitles = AutoCzechSubtitlesCheck.IsChecked == true;
        _settings.BurnSubtitles = BurnSubtitlesCheck.IsChecked == true;
        _settings.HotspotSsid = HotspotSsidBox.Text.Trim();
        _settings.HotspotPassword = HotspotPassBox.Text;
        _settings.LastAdbEndpoint = AdbConnectEndpointBox.Text.Trim();
        if (Current is { } r)
        {
            _settings.LastLocation = r.Location;
            _settings.LastDeviceName = r.FriendlyName;
        }
        _settings.Save();
    }

    private void UpdateDeviceControlsState()
    {
        bool hasDevice = Current != null;
        bool hasVol = Current?.HasVolume == true;
        VolumeSlider.IsEnabled = hasVol;
        MuteButton.IsEnabled = hasVol;
        PositionSlider.IsEnabled = hasDevice && _lastDuration > TimeSpan.Zero;
        SyncMiniPlayer();
    }

    // =================================================================== MiniPlayer & Helpers

    private void MiniPlayer_Click(object sender, RoutedEventArgs e)
    {
        if (_miniPlayer != null)
        {
            _miniPlayer.Activate();
            return;
        }

        var mp = new MiniPlayerWindow { Owner = this };
        mp.PrevClicked += () => _ = SafeAsync(PrevAsync);
        mp.PlayPauseClicked += () => _ = SafeAsync(TogglePauseResumeAsync);
        mp.StopClicked += () => _ = SafeAsync(StopPlaybackAsync);
        mp.NextClicked += () => _ = SafeAsync(NextAsync);
        mp.VolumeChangedByUser += vol =>
        {
            if (Current is { HasVolume: true }) VolumeSlider.Value = vol;
        };
        mp.Closed += (_, _) => _miniPlayer = null;
        _miniPlayer = mp;
        SyncMiniPlayer();
        mp.Show();
    }

    private void SyncMiniPlayer()
    {
        _miniPlayer?.UpdateState(
            NowPlayingText?.Text ?? "Nic se nepřehrává",
            StateText?.Text ?? "",
            (int)(VolumeSlider?.Value ?? 30),
            Current?.HasVolume == true);
    }

    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }

    private DlnaRenderer? RequireDevice()
    {
        if (Current is { } r) return r;
        SetStatus("Nejdřív vyber přijímač (🔍 Hledat). Stick musí být ve stejné Wi-Fi jako PC.");
        return null;
    }

    private async Task SafeAsync(Func<Task> action, bool quiet = false)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (quiet) Log(ex.Message);
            else ShowError(ex);
        }
    }

    private void ShowError(Exception ex)
    {
        var msg = ex is TaskCanceledException ? "Stick neodpověděl včas (timeout)" : ex.Message;
        SetStatus("⚠ " + msg);
        Log("CHYBA: " + msg);
    }

    private void SetStatus(string s) => StatusText.Text = s;

    private void Log(string s)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\r\n");
        LogBox.ScrollToEnd();
    }
}
