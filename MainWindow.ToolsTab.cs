using System.Net;
using System.Windows;
using System.Windows.Controls;
using StickCast.Net;
using StickCast.Services;

namespace StickCast;

public partial class MainWindow
{
    private void PopulateMonitors()
    {
        _monitors.Clear();
        foreach (var mon in Win32Helpers.EnumerateMonitors())
            _monitors.Add(mon);
        MonitorCombo.SelectedIndex = _monitors.Count > 1 ? 1 : 0;
    }

    private async void RefreshAudioDevices_Click(object sender, RoutedEventArgs e) =>
        await SafeAsync(RefreshAudioDevicesAsync, quiet: true);

    private async Task RefreshAudioDevicesAsync()
    {
        var selected = (AudioDeviceCombo.SelectedItem as AudioChoice)?.DeviceName;
        var list = await ScreenStream.ListAudioDevicesAsync(_cts.Token);
        _audioDevices.Clear();
        _audioDevices.Add(new AudioChoice("(bez zvuku)", null));
        foreach (var d in list) _audioDevices.Add(new AudioChoice(d, d));
        AudioDeviceCombo.SelectedItem = _audioDevices.FirstOrDefault(a => a.DeviceName == selected) ?? _audioDevices[0];
    }

    private async void ScreenStream_Click(object sender, RoutedEventArgs e)
    {
        if (_isScreenStreaming)
        {
            StopScreenStream();
            await SafeAsync(async () => { if (Current is { } r) await r.StopAsync(); }, quiet: true);
            SetStatus("DLNA stream obrazovky zastaven");
            return;
        }

        if (!ScreenStream.IsFfmpegAvailable())
        {
            var ans = MessageBox.Show(this,
                "Pro DLNA streamování obrazovky je potřeba ffmpeg.exe.\n\n" +
                "Chceš ho teď stáhnout automaticky? (Případně můžeš použít Miracast Win+K.)",
                "Potřeba ffmpeg.exe", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans == MessageBoxResult.Yes)
            {
                MainTabs.SelectedIndex = 4;
                await DownloadToolAsync(isFfmpeg: true);
            }
            return;
        }

        if (RequireDevice() is not { } dev) return;

        var mon = MonitorCombo.SelectedItem as MonitorChoice ?? new MonitorChoice("Celá plocha", 0, 0, 0, 0);
        int maxW = ScreenResCombo.SelectedItem is ComboBoxItem ri && int.TryParse(ri.Tag?.ToString(), out var rw) ? rw : 1280;
        int fps = ScreenFpsCombo.SelectedItem is ComboBoxItem fi && int.TryParse(fi.Tag?.ToString(), out var fv) ? fv : 25;
        string? audio = (AudioDeviceCombo.SelectedItem as AudioChoice)?.DeviceName;
        var options = new ScreenCaptureOptions(mon.OffsetX, mon.OffsetY, mon.Width, mon.Height, fps, maxW, audio);

        await _playLock.WaitAsync(_cts.Token);
        _isStartingPlayback = true;
        try
        {
            await SafeAsync(async () =>
            {
                StopScreenStream();
                StopTranscodeStream();
                _slideshowTimer.Stop();

                _screenStream = new ScreenStream { Log = s => Dispatcher.BeginInvoke(() => Log(s)) };
                const string liveId = "screen";
                _server.RegisterLiveStream(liveId, "video/mp2t", (stream, ct) => _screenStream.StreamToAsync(stream, options, ct));

                var host = await DlnaRenderer.GetHostAddressAsync(dev);
                var local = NetUtil.GetLocalIpFor(host);
                var liveUrl = _server.GetLiveUrl(liveId, local);
                Log($"DLNA stream obrazovky → {liveUrl}");

                BeginPlayback();
                _playingFromPlaylist = false;
                _isScreenStreaming = true;
                ScreenStreamButton.Content = "⏹ Zastavit DLNA stream obrazovky";
                ScreenStreamStatus.Text = $"Streamuji obrazovku ({mon.Label}, {fps} fps) na {dev.FriendlyName}…";

                SetStatus("Spouštím DLNA stream obrazovky…");
                await dev.PlayAsync(liveUrl, "Obrazovka PC", "video/mp2t", isLocal: true);
                NowPlayingText.Text = "🖥 Živý stream obrazovky PC";
                SyncMiniPlayer();
                SetStatus("Streamování obrazovky běží");
            });
        }
        finally
        {
            _isStartingPlayback = false;
            _playLock.Release();
        }
    }

    private void StopScreenStream()
    {
        if (!_isScreenStreaming && _screenStream == null) return;
        _isScreenStreaming = false;
        _server.UnregisterLiveStream("screen");
        _screenStream?.Dispose();
        _screenStream = null;
        ScreenStreamButton.Content = "📺 Streamovat obrazovku přes DLNA (ffmpeg)";
        ScreenStreamStatus.Text = "";
    }

    private void StopTranscodeStream()
    {
        if (_transcodeStream == null) return;
        _server.UnregisterLiveStream("transcode");
        _transcodeStream.Dispose();
        _transcodeStream = null;
    }

    // =================================================================== Phone (ADB + scrcpy)

    private async void AdbPair_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = AdbPairEndpointBox.Text.Trim();
        var code = AdbPairCodeBox.Text.Trim();
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(code))
        {
            AdbStatusText.Text = "⚠ Zadej párovací IP:port i 6místný kód z obrazovky Bezdrátového ladění.";
            return;
        }

        AdbStatusText.Text = $"Páruji s {endpoint}…";
        await SafeAsync(async () =>
        {
            var (ok, output) = await AdbController.RunAdbAsync(_cts.Token, "pair", endpoint, code);
            AdbStatusText.Text = ok
                ? $"✅ Spárováno: {output}"
                : $"⚠ Párování selhalo: {output}";
            Log($"adb pair {endpoint} → {output}");
            if (ok && string.IsNullOrWhiteSpace(AdbConnectEndpointBox.Text) && endpoint.Contains(':'))
                AdbConnectEndpointBox.Text = endpoint[..endpoint.LastIndexOf(':')] + ":";
        });
    }

    private async void AdbConnect_Click(object sender, RoutedEventArgs e)
    {
        var endpoint = AdbConnectEndpointBox.Text.Trim();
        if (string.IsNullOrEmpty(endpoint) || !endpoint.Contains(':'))
        {
            AdbStatusText.Text = "⚠ Zadej hlavní IP:port z obrazovky Bezdrátového ladění (např. 192.168.1.42:41567).";
            return;
        }

        _settings.LastAdbEndpoint = endpoint;
        _settings.Save();
        AdbStatusText.Text = $"Připojuji k {endpoint}…";
        await SafeAsync(async () =>
        {
            var (ok, output) = await AdbController.RunAdbAsync(_cts.Token, "connect", endpoint);
            bool connected = ok && output.Contains("connected", StringComparison.OrdinalIgnoreCase);
            AdbStatusText.Text = connected
                ? $"✅ Připojeno: {output}"
                : $"⚠ Připojení selhalo: {output}";
            SetStatus(connected ? $"Telefon připojen přes Wi-Fi ({endpoint})" : "Připojení telefonu přes Wi-Fi selhalo");
            Log($"adb connect {endpoint} → {output}");
        });
    }

    private async void AdbDevices_Click(object sender, RoutedEventArgs e)
    {
        AdbStatusText.Text = "Hledám připojené telefony…";
        await SafeAsync(async () =>
        {
            var (ok, output) = await AdbController.RunAdbAsync(_cts.Token, "devices", "-l");
            AdbStatusText.Text = ok ? output : $"⚠ {output}";
        });
    }

    private async void AdbDisconnect_Click(object sender, RoutedEventArgs e)
    {
        AdbStatusText.Text = "Odpojuji ADB a uvolňuji Wi-Fi…";
        await SafeAsync(async () =>
        {
            await AdbController.DisconnectAllAsync(_cts.Token);
            AdbStatusText.Text = "✅ Bezdrátové ADB odpojeno (Wi-Fi uvolněna pro přímé zrcadlení na TV).";
            SetStatus("ADB odpojeno");
        });
    }

    private async void Scrcpy_Click(object sender, RoutedEventArgs e)
    {
        var scrcpy = AdbController.FindExecutable("scrcpy.exe");
        if (scrcpy == null)
        {
            MessageBox.Show(this,
                "Program scrcpy.exe nebyl nalezen.\n\n" +
                "1. Stáhni si scrcpy pro Windows z github.com/Genymobile/scrcpy/releases\n" +
                $"2. Rozbal ho do složky:\n{ToolDownloader.GetToolsDirectory()}\n(nebo vedle StickCast.exe či do PATH).\n\n" +
                "Na telefonu (Motorola / Xiaomi) pak zapni:\n" +
                "Nastavení → O telefonu → 7× klepni na Číslo sestavení → Možnosti pro vývojáře → Ladění USB nebo Bezdrátové ladění.",
                "Potřeba scrcpy.exe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await SafeAsync(async () =>
        {
            await AdbController.LaunchScrcpyAsync(
                scrcpy,
                AdbConnectEndpointBox.Text,
                ScrcpyWifiTuningCheck.IsChecked == true,
                ScrcpyNoAudioCheck.IsChecked == true,
                _cts.Token);
            SetStatus("Spuštěno zrcadlení telefonu (scrcpy)");
        });
    }

    // =================================================================== Tools, Firewall, Wi-Fi & Miracast

    private void RefreshToolsStatus()
    {
        var ytdlp = YtDlp.Find();
        var ffmpeg = ScreenStream.FindFfmpeg();
        ToolsStatusText.Text =
            $"yt-dlp.exe: {(ytdlp != null ? "✅ " + ytdlp : "❌ nenalezen")}\n" +
            $"ffmpeg.exe: {(ffmpeg != null ? "✅ " + ffmpeg : "❌ nenalezen")}";
    }

    private async void DownloadYtDlp_Click(object sender, RoutedEventArgs e) => await DownloadToolAsync(isFfmpeg: false);
    private async void DownloadFfmpeg_Click(object sender, RoutedEventArgs e) => await DownloadToolAsync(isFfmpeg: true);
    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    private async Task DownloadToolAsync(bool isFfmpeg)
    {
        if (_downloadCts != null) return;
        _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        DownloadYtDlpButton.IsEnabled = false;
        DownloadFfmpegButton.IsEnabled = false;
        CancelDownloadButton.Visibility = Visibility.Visible;
        ToolDownloadPanel.Visibility = Visibility.Visible;
        ToolDownloadProgress.IsIndeterminate = true;
        ToolDownloadStatus.Text = isFfmpeg ? "Stahuji ffmpeg.exe…" : "Stahuji yt-dlp.exe…";

        var progress = new Progress<(double? Percent, string Status)>(p =>
        {
            if (p.Percent is double pct)
            {
                ToolDownloadProgress.IsIndeterminate = false;
                ToolDownloadProgress.Value = Math.Clamp(pct, 0, 100);
            }
            else
            {
                ToolDownloadProgress.IsIndeterminate = true;
            }
            ToolDownloadStatus.Text = p.Status;
        });

        try
        {
            string path = isFfmpeg
                ? await ToolDownloader.DownloadFfmpegAsync(progress, _downloadCts.Token)
                : await ToolDownloader.DownloadYtDlpAsync(progress, _downloadCts.Token);
            Log($"Nástroj stažen: {path}");
            SetStatus($"Staženo: {Path.GetFileName(path)}");
            RefreshToolsStatus();
            if (isFfmpeg) await SafeAsync(RefreshAudioDevicesAsync, quiet: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stahování zrušeno.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            DownloadYtDlpButton.IsEnabled = true;
            DownloadFfmpegButton.IsEnabled = true;
            CancelDownloadButton.Visibility = Visibility.Collapsed;
            ToolDownloadPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void CheckFirewall_Click(object sender, RoutedEventArgs e)
    {
        FirewallStatusText.Text = "Kontroluji síť a Windows Firewall…";
        await SafeAsync(async () =>
        {
            IPAddress localIp = IPAddress.Loopback;
            if (Current is { } r)
            {
                var host = await DlnaRenderer.GetHostAddressAsync(r);
                localIp = NetUtil.GetLocalIpFor(host);
            }
            else
            {
                var ips = NetUtil.GetLocalIPv4();
                if (ips.Count > 0) localIp = ips[0];
            }
            var res = await NetworkDiag.CheckMediaServerReachabilityAsync(_server.Port, localIp, _cts.Token);
            FirewallStatusText.Text = res.Summary + (string.IsNullOrWhiteSpace(res.Advice) ? "" : "\n" + res.Advice);
        });
    }

    private async void AllowFirewall_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        await SafeAsync(async () =>
        {
            bool ok = await NetworkDiag.TryAddFirewallRuleAsync(exe, _cts.Token);
            FirewallStatusText.Text = ok
                ? "✅ Pravidlo ve Windows Firewallu bylo přidáno."
                : "⚠ Pravidlo se nepodařilo přidat (UAC zrušeno nebo chyba).";
        });
    }

    private async Task RefreshWifiStatusAsync()
    {
        var ssid = await NetworkDiag.GetCurrentWifiSsidAsync(_cts.Token);
        CurrentWifiText.Text = string.IsNullOrEmpty(ssid)
            ? "Aktuální Wi-Fi: nepřipojeno / ethernet"
            : $"Aktuální Wi-Fi: {ssid}";
    }

    private async void ConnectHotspot_Click(object sender, RoutedEventArgs e)
    {
        var ssid = HotspotSsidBox.Text.Trim();
        var pass = HotspotPassBox.Text;
        if (string.IsNullOrEmpty(ssid))
        {
            SetStatus("Zadej název Wi-Fi (SSID) sticku.");
            return;
        }
        _settings.HotspotSsid = ssid;
        _settings.HotspotPassword = pass;
        _settings.Save();

        SetStatus($"Připojuji PC k Wi-Fi {ssid}…");
        await SafeAsync(async () =>
        {
            var (ok, msg) = await NetworkDiag.ConnectToWifiAsync(ssid, pass, _cts.Token);
            SetStatus(ok ? $"Připojeno k {ssid}" : msg);
            Log(msg);
            await RefreshWifiStatusAsync();
        });
    }

    private async void ProbeStick_Click(object sender, RoutedEventArgs e)
    {
        var host = Current?.Host ?? "192.168.203.1";
        StickProbeText.Text = $"Kontroluji {host}…";
        await SafeAsync(async () =>
        {
            var info = await StickAdmin.ProbeAsync(host, _cts.Token);
            StickProbeText.Text = info.Reachable
                ? $"✅ Admin rozhraní dostupné ({info.Url}) – HTTP {info.StatusCode}" +
                  (string.IsNullOrWhiteSpace(info.Title) ? "" : $", titulek: {info.Title}") +
                  (string.IsNullOrWhiteSpace(info.Server) ? "" : $", server: {info.Server}") +
                  (string.IsNullOrWhiteSpace(info.Hint) ? "" : $", info: {info.Hint}")
                : $"⚠ Admin rozhraní na {info.Url} neodpovídá ({info.Error ?? "nedostupné"}). " +
                  "Zkontroluj Wi-Fi připojení ke sticku.";
        });
    }

    private void Miracast_Click(object sender, RoutedEventArgs e)
    {
        if (!Win32Helpers.TrySendWinK(this))
        {
            SetStatus("Okno StickCast není aktivní – pro zrcadlení klikni do okna nebo stiskni Win+K.");
            return;
        }
        SetStatus("Otevřen panel Odeslat – vyber WifiDisplay");
    }

    private void ProjectSettings_Click(object sender, RoutedEventArgs e) => Open("ms-settings:project");
    private void AddDisplay_Click(object sender, RoutedEventArgs e) => Open("ms-settings-connectabledevices:devicediscovery");
    private void OpenAdmin_Click(object sender, RoutedEventArgs e) => Open("http://192.168.203.1");
    private void WifiSettings_Click(object sender, RoutedEventArgs e) => Open("ms-availablenetworks:");
}
