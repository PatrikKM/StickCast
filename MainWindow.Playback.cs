using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using StickCast.Net;

namespace StickCast;

public partial class MainWindow
{
    private bool IsYtAutoPlayEnabled()
    {
        if (YtAutoPlayCombo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return tag.Equals("true", StringComparison.OrdinalIgnoreCase);
        return _settings.YtAutoPlayNext;
    }

    private void BeginPlayback()
    {
        _playbackEpoch++;
        _pcPlaybackActive = true;
        _userStopped = false;
        _lastState = "";
        _lastPosition = TimeSpan.Zero;
        _lastDuration = TimeSpan.Zero;
        _ignoreStateUntil = DateTime.UtcNow.AddSeconds(5);
        _consecutivePollFailures = 0;
        _pollTimer.Interval = TimeSpan.FromSeconds(1);
        _pollTimer.Start();
    }

    private void OnLocalMonitorStateChanged(string state)
    {
        if (Current is not { IsLocalMonitor: true }) return;
        _lastState = state;
        if (state == "PLAYING")
        {
            _userStopped = false;
            _pcPlaybackActive = true;
            StateText.Text = "▶ přehrává";
            if (!_pollTimer.IsEnabled) _pollTimer.Start();
        }
        else if (state == "PAUSED_PLAYBACK")
        {
            StateText.Text = "⏸ pozastaveno";
        }
        else if (state == "STOPPED")
        {
            StateText.Text = "⏹ zastaveno";
        }
        SyncMiniPlayer();
    }

    private async Task OnLocalMonitorMediaEndedAsync()
    {
        if (Current is not { IsLocalMonitor: true } || _userStopped || _isStartingPlayback)
            return;

        _lastState = "STOPPED";
        StateText.Text = "⏹ zastaveno";
        SyncMiniPlayer();

        if (_playingFromPlaylist)
        {
            if (AutoNextCheck.IsChecked == true)
            {
                int next = GetNextPlaylistIndex(manual: false);
                if (next >= 0)
                {
                    Log("Konec souboru → další v playlistu");
                    await PlayFileAsync(next);
                    return;
                }
            }
        }
        else if (IsYtAutoPlayEnabled() && _ytResults.Count > 0)
        {
            Log("Konec skladby/videa → automaticky další vyhledaná položka");
            await PlayNextYtResultAsync();
            return;
        }

        _pcPlaybackActive = false;
        _pollTimer.Stop();
        SetStatus("Přehrávání dokončeno");
    }

    private async void Resume_Click(object sender, RoutedEventArgs e) =>
        await SafeAsync(ResumePlaybackAsync);

    private async Task ResumePlaybackAsync()
    {
        if (RequireDevice() is not { } r) return;
        _playbackEpoch++;

        if (!r.IsLocalMonitor && (_userStopped || _lastState is "STOPPED" or "NO_MEDIA_PRESENT" or ""))
        {
            if (_playingFromPlaylist && _currentFileIndex >= 0 && _currentFileIndex < _files.Count)
            {
                await PlayFileAsync(_currentFileIndex);
                return;
            }
            if (!string.IsNullOrWhiteSpace(_lastPlayedUrl))
            {
                await PlayUrlAsync(_lastPlayedUrl, _currentYtIndex >= 0 ? _currentYtIndex : null);
                return;
            }
        }

        _userStopped = false;
        _pcPlaybackActive = true;
        await r.ResumeAsync();
        _lastState = "PLAYING";
        StateText.Text = "▶ přehrává";
        _ignoreStateUntil = DateTime.UtcNow.AddSeconds(2);
        if (!_pollTimer.IsEnabled) _pollTimer.Start();
        SyncMiniPlayer();
    }

    private async void Pause_Click(object sender, RoutedEventArgs e) =>
        await SafeAsync(PausePlaybackAsync);

    private async Task PausePlaybackAsync()
    {
        if (RequireDevice() is not { } r) return;
        _playbackEpoch++;
        await r.PauseAsync();
        _lastState = "PAUSED_PLAYBACK";
        StateText.Text = "⏸ pozastaveno";
        _ignoreStateUntil = DateTime.UtcNow.AddSeconds(2);
        SyncMiniPlayer();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) =>
        await SafeAsync(StopPlaybackAsync);

    private async Task StopPlaybackAsync()
    {
        try { _resolveCts?.Cancel(); } catch { }
        StopScreenStream();
        StopTranscodeStream();
        _slideshowTimer.Stop();
        _pcPlaybackActive = false;
        _pollTimer.Stop();
        _playbackEpoch++;
        _userStopped = true;
        _lastState = "STOPPED";
        StateText.Text = "⏹ zastaveno";
        _lastPosition = TimeSpan.Zero;
        PositionSlider.Value = 0;
        PosText.Text = "0:00:00";
        SyncMiniPlayer();
        SetStatus("Přehrávání zastaveno");

        if (Current is { } r)
        {
            try { await r.StopAsync(); } catch { }
        }
    }

    private async Task TogglePauseResumeAsync()
    {
        if (RequireDevice() is not { } r) return;
        if (_lastState == "PLAYING" && !_userStopped)
            await PausePlaybackAsync();
        else
            await ResumePlaybackAsync();
    }

    private void PositionSlider_MouseDown(object sender, MouseButtonEventArgs e) => _seekDragging = true;

    private void PositionSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _seekDragging = true;
        _thumbDragging = true;
    }

    private async void PositionSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _thumbDragging = false;
        if (!_seekDragging) return;
        _seekDragging = false;
        await CommitSeekAsync(TimeSpan.FromSeconds(PositionSlider.Value));
    }

    private async void PositionSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_thumbDragging || !_seekDragging) return;
        _seekDragging = false;
        await CommitSeekAsync(TimeSpan.FromSeconds(PositionSlider.Value));
    }

    private void PositionSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (!_thumbDragging) _seekDragging = false;
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_seekDragging)
            PosText.Text = DlnaRenderer.FormatTime(TimeSpan.FromSeconds(PositionSlider.Value));
    }

    private async Task CommitSeekAsync(TimeSpan target)
    {
        if (Current is not { } r) return;
        _ignoreSeekUntil = DateTime.UtcNow.AddSeconds(2.5);
        _lastPosition = target;
        PosText.Text = DlnaRenderer.FormatTime(target);
        await SafeAsync(() => r.SeekAsync(target));
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        if (VolumeText != null) VolumeText.Text = $"{(int)VolumeSlider.Value}%";
        SyncMiniPlayer();
        if (_volumeFromDevice) return;
        _volumeTimer.Stop();
        _volumeTimer.Start();
    }

    private void AdjustVolume(int delta)
    {
        if (Current is not { HasVolume: true }) return;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);
    }

    private async void Mute_Click(object sender, RoutedEventArgs e) =>
        await SafeAsync(async () =>
        {
            if (RequireDevice() is not { } r) return;
            _muted = !_muted;
            await r.SetMuteAsync(_muted);
            MuteButton.Content = _muted ? "🔇" : "🔊";
        });

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase) return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;

        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true;
                await SafeAsync(TogglePauseResumeAsync);
                break;

            case Key.Left:
            case Key.Right:
                if (Current != null && _lastDuration > TimeSpan.Zero)
                {
                    e.Handled = true;
                    double baseSec = _seekDragging ? PositionSlider.Value : _lastPosition.TotalSeconds;
                    double nextSec = Math.Clamp(baseSec + (e.Key == Key.Right ? 10 : -10), 0, _lastDuration.TotalSeconds);
                    PositionSlider.Value = nextSec;
                    await CommitSeekAsync(TimeSpan.FromSeconds(nextSec));
                }
                break;

            case Key.Add:
            case Key.OemPlus:
                e.Handled = true;
                AdjustVolume(5);
                break;

            case Key.Subtract:
            case Key.OemMinus:
                e.Handled = true;
                AdjustVolume(-5);
                break;
        }
    }

    private async Task PollAsync()
    {
        if (!_pcPlaybackActive || _polling || _isStartingPlayback || _cts.IsCancellationRequested || Current is not { } r)
            return;
        _polling = true;
        long epoch = _playbackEpoch;
        var device = r;
        try
        {
            var state = await r.GetTransportStateAsync();
            var (pos, dur) = await r.GetPositionAsync();

            if (_cts.IsCancellationRequested || epoch != _playbackEpoch || !ReferenceEquals(device, Current) || _isStartingPlayback)
                return;

            if (_consecutivePollFailures > 0)
            {
                _consecutivePollFailures = 0;
                _pollTimer.Interval = TimeSpan.FromSeconds(1);
                SetStatus($"Připojeno: {r.FriendlyName}");
                Log($"Spojení se stickem obnoveno ({r.FriendlyName}).");
                _ = SafeAsync(() => SyncDeviceAudioAsync(r), quiet: true);
            }
            else if (++_pollCount % 10 == 0 && r.HasVolume)
            {
                _ = SafeAsync(async () =>
                {
                    var m = await r.GetMuteAsync();
                    if (m is bool muted && ReferenceEquals(r, Current))
                    {
                        _muted = muted;
                        MuteButton.Content = _muted ? "🔇" : "🔊";
                    }
                }, quiet: true);
            }

            StateText.Text = state switch
            {
                "PLAYING" => "▶ přehrává",
                "PAUSED_PLAYBACK" => "⏸ pozastaveno",
                "STOPPED" => "⏹ zastaveno",
                "TRANSITIONING" => "⏳ načítá…",
                "NO_MEDIA_PRESENT" => "bez média",
                _ => state,
            };
            SyncMiniPlayer();

            _lastDuration = dur;
            if (!_seekDragging && DateTime.UtcNow > _ignoreSeekUntil)
            {
                _lastPosition = pos;
                if (dur > TimeSpan.Zero)
                {
                    PositionSlider.IsEnabled = true;
                    PositionSlider.Maximum = dur.TotalSeconds;
                    PositionSlider.Value = Math.Clamp(pos.TotalSeconds, 0, dur.TotalSeconds);
                }
                else
                {
                    PositionSlider.IsEnabled = false;
                    PositionSlider.Maximum = 1;
                    PositionSlider.Value = 0;
                }
                PosText.Text = DlnaRenderer.FormatTime(pos);
            }
            DurText.Text = DlnaRenderer.FormatTime(dur);

            if (state is "PLAYING" or "PAUSED_PLAYBACK")
            {
                _lastState = state;
            }
            else if (!r.IsLocalMonitor && DateTime.UtcNow > _ignoreStateUntil)
            {
                bool finished = _lastState == "PLAYING" && state is "STOPPED" or "NO_MEDIA_PRESENT";
                if (finished && !_userStopped)
                {
                    if (_playingFromPlaylist && AutoNextCheck.IsChecked == true)
                    {
                        int next = GetNextPlaylistIndex(manual: false);
                        if (next >= 0)
                        {
                            Log("Konec souboru → další v playlistu");
                            _lastState = "";
                            _ignoreStateUntil = DateTime.UtcNow.AddSeconds(5);
                            await PlayFileAsync(next);
                            return;
                        }
                    }
                    else if (!_playingFromPlaylist && IsYtAutoPlayEnabled() && _ytResults.Count > 0)
                    {
                        Log("Konec skladby/videa → automaticky další vyhledaná položka");
                        _lastState = "";
                        _ignoreStateUntil = DateTime.UtcNow.AddSeconds(5);
                        await PlayNextYtResultAsync();
                        return;
                    }
                }
                if (finished)
                {
                    _pcPlaybackActive = false;
                    _pollTimer.Stop();
                }
                _lastState = state;
            }
        }
        catch
        {
            if (_cts.IsCancellationRequested || epoch != _playbackEpoch || !ReferenceEquals(device, Current))
                return;

            _consecutivePollFailures++;
            int delaySec = Math.Min(30, 1 << Math.Min(5, _consecutivePollFailures));
            _pollTimer.Interval = TimeSpan.FromSeconds(delaySec);
            StateText.Text = $"⚠ stick neodpovídá (další pokus za {delaySec} s)";
            SyncMiniPlayer();

            if (_consecutivePollFailures == 1)
            {
                SetStatus($"⚠ Stick {r.FriendlyName} neodpovídá – zkouším znovu…");
                Log("Stick neodpovídá, přecházím na pomalejší kontrolu (backoff).");
            }
            if (_consecutivePollFailures >= 3 && _consecutivePollFailures % 3 == 0 && !_discovering)
                _ = SafeAsync(TryAutoRediscoverAsync, quiet: true);
        }
        finally { _polling = false; }
    }
}
