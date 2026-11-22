using System.Net;
using System.Windows;
using System.Windows.Controls;
using StickCast.Net;

namespace StickCast;

public partial class MainWindow
{
    private void PopulateLocalMonitorDevices()
    {
        var physicalMonitors = _monitors.Where(m => m.Width > 0 && m.Height > 0).ToList();
        if (physicalMonitors.Count == 0)
            physicalMonitors.Add(new Services.MonitorChoice("Monitor 1 (1920×1080) [hlavní]", 0, 0, 1920, 1080));

        for (int i = 0; i < physicalMonitors.Count; i++)
        {
            var mon = physicalMonitors[i];
            var id = (i + 1).ToString();
            if (!_localMonitorHosts.TryGetValue(id, out var host))
            {
                host = new LocalMonitorHost(mon, s => Dispatcher.BeginInvoke(() => Log(s)));
                _localMonitorHosts[id] = host;
            }
            else
            {
                host.UpdateMonitor(mon);
            }

            var friendlyName = $"💻 Tento počítač – {mon.Label}";
            var renderer = DlnaRenderer.CreateLocalMonitor(id, friendlyName, mon.Label, host);
            AddDevice(renderer, select: false);
        }
    }

    private async Task StartupAsync()
    {
        PopulateLocalMonitorDevices();

        if (!string.IsNullOrEmpty(_settings.LastLocation))
        {
            if (_settings.LastLocation.StartsWith("local://", StringComparison.OrdinalIgnoreCase))
            {
                var localMatch = _devices.FirstOrDefault(d => d.Location.Equals(_settings.LastLocation, StringComparison.OrdinalIgnoreCase));
                if (localMatch != null) DevicesCombo.SelectedItem = localMatch;
            }
            else
            {
                var r = await DlnaRenderer.FromLocationAsync(_settings.LastLocation);
                if (r != null) AddDevice(r, select: true);
            }
        }
        await DiscoverAsync(null);
        await SafeAsync(RefreshWifiStatusAsync, quiet: true);
        await SafeAsync(RefreshAudioDevicesAsync, quiet: true);
    }

    private async Task DiscoverAsync(IPAddress? unicast)
    {
        if (_discovering) return;
        _discovering = true;
        SearchButton.IsEnabled = false;
        SetStatus(unicast == null ? "Hledám DLNA přijímače a monitory…" : $"Zkouším {unicast}…");
        try
        {
            PopulateMonitors();
            PopulateLocalMonitorDevices();

            var locations = await Ssdp.SearchAsync(TimeSpan.FromSeconds(3), unicast, s => Dispatcher.BeginInvoke(() => Log(s)));
            var found = await Task.WhenAll(locations.Select(DlnaRenderer.FromLocationAsync));
            int count = 0;
            foreach (var r in found.Where(r => r != null))
            {
                count++;
                AddDevice(r!, select: false);
            }

            bool userPreferredLocal = !string.IsNullOrEmpty(_settings.LastLocation) &&
                                      _settings.LastLocation.StartsWith("local://", StringComparison.OrdinalIgnoreCase);

            if ((Current == null || (Current.IsLocalMonitor && !userPreferredLocal && count > 0)) && _devices.Count > 0)
            {
                DevicesCombo.SelectedItem =
                    (!string.IsNullOrEmpty(_settings.LastDeviceName)
                        ? _devices.FirstOrDefault(d => d.FriendlyName.Equals(_settings.LastDeviceName, StringComparison.OrdinalIgnoreCase))
                        : null)
                    ?? _devices.FirstOrDefault(LooksLikeStick)
                    ?? _devices.FirstOrDefault(d => !d.IsLocalMonitor)
                    ?? _devices[0];
            }

            int netCount = _devices.Count(d => !d.IsLocalMonitor);
            int localCount = _devices.Count(d => d.IsLocalMonitor);
            SetStatus(netCount == 0
                ? $"Síťová TV nenalezena – k dispozici monitor PC ({localCount}×)"
                : $"Nalezeno TV/sticků: {netCount} + monitor PC: {localCount}");
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _discovering = false;
            SearchButton.IsEnabled = true;
        }
    }

    private async Task TryAutoRediscoverAsync()
    {
        if (_discovering || _cts.IsCancellationRequested || Current is not { } lost) return;
        _discovering = true;
        try
        {
            var locations = await Ssdp.SearchAsync(TimeSpan.FromSeconds(3), null, null);
            if (_cts.IsCancellationRequested || !ReferenceEquals(lost, Current)) return;

            var found = (await Task.WhenAll(locations.Select(DlnaRenderer.FromLocationAsync)))
                .Where(r => r != null)
                .Cast<DlnaRenderer>()
                .ToList();

            foreach (var r in found) AddDevice(r, select: false);

            var sameLocation = found.FirstOrDefault(r => r.Location.Equals(lost.Location, StringComparison.OrdinalIgnoreCase));
            if (sameLocation != null)
            {
                _consecutivePollFailures = 0;
                _pollTimer.Interval = TimeSpan.FromSeconds(1);
                return;
            }

            var targetName = !string.IsNullOrEmpty(lost.FriendlyName) ? lost.FriendlyName : _settings.LastDeviceName;
            var relocated = !string.IsNullOrEmpty(targetName)
                ? found.FirstOrDefault(r => r.FriendlyName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                : found.FirstOrDefault(LooksLikeStick);

            if (relocated != null)
            {
                Log($"Stick znovu nalezen na nové adrese: {relocated.Location}");
                AddDevice(relocated, select: true);
                SetStatus($"Stick znovu nalezen: {relocated.FriendlyName}");
            }
        }
        catch { /* quiet background retry */ }
        finally { _discovering = false; }
    }

    private static bool LooksLikeStick(DlnaRenderer d)
    {
        var s = (d.FriendlyName + d.ModelName + d.Manufacturer).ToLowerInvariant();
        return new[] { "wifidisplay", "mira", "anycast", "ezcast", "cast", "dongle" }.Any(s.Contains);
    }

    private void AddDevice(DlnaRenderer r, bool select)
    {
        var existing = _devices.FirstOrDefault(d => d.Location.Equals(r.Location, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            _devices.Add(r);
            existing = r;
            Log($"Přijímač: {r.FriendlyName} | {r.Manufacturer} {r.ModelName} | {r.Location}");
        }
        if (select) DevicesCombo.SelectedItem = existing;
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SafeAsync(() => DiscoverAsync(null));

    private async void ManualConnect_Click(object sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        if (!IPAddress.TryParse(ManualIpBox.Text.Trim(), out var ip))
        {
            SetStatus("Zadej platnou IP adresu, např. 192.168.1.50");
            return;
        }
        await DiscoverAsync(ip);
    });

    private async void DevicesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        _playbackEpoch++;
        _pcPlaybackActive = false;
        _pollTimer.Stop();
        _consecutivePollFailures = 0;
        _pollTimer.Interval = TimeSpan.FromSeconds(1);
        _lastState = "";
        _lastPosition = TimeSpan.Zero;
        _lastDuration = TimeSpan.Zero;
        UpdateDeviceControlsState();

        if (Current is not { } r) return;
        _settings.LastLocation = r.Location;
        _settings.LastDeviceName = r.FriendlyName;
        _settings.Save();
        SetStatus($"Připojeno: {r.FriendlyName}");
        DeviceInfoText.Text = $"Vybraný přijímač: {r.FriendlyName}\nVýrobce: {r.Manufacturer}  Model: {r.ModelName}\n" +
                              $"Popis zařízení: {r.Location}\nOvládání hlasitosti: {(r.HasVolume ? "ano" : "ne")}";

        await SafeAsync(() => SyncDeviceAudioAsync(r), quiet: true);
    }

    private async Task SyncDeviceAudioAsync(DlnaRenderer r)
    {
        if (!r.HasVolume) return;
        var v = await r.GetVolumeAsync();
        if (v is int vol && ReferenceEquals(r, Current))
        {
            _volumeFromDevice = true;
            VolumeSlider.Value = Math.Clamp(vol, 0, 100);
            _volumeFromDevice = false;
            SyncMiniPlayer();
        }
        var m = await r.GetMuteAsync();
        if (m is bool muted && ReferenceEquals(r, Current))
        {
            _muted = muted;
            MuteButton.Content = _muted ? "🔇" : "🔊";
        }
    }
}
