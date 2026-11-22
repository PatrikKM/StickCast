using System.Net;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using StickCast.Net;

namespace StickCast;

public sealed class PlaylistItem
{
    public PlaylistItem(string path, string? displayName = null, bool isUrl = false)
    {
        Path = path;
        DisplayName = displayName;
        IsUrl = isUrl;
    }

    public string Path { get; }
    public string? DisplayName { get; }
    public bool IsUrl { get; }
    public override string ToString() => DisplayName ?? System.IO.Path.GetFileName(Path);
}

public partial class MainWindow
{
    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var filter = Mime.FileDialogFilter + "|IPTV playlisty (*.m3u;*.m3u8)|*.m3u;*.m3u8";
        var dlg = new OpenFileDialog { Multiselect = true, Filter = filter, Title = "Vyber média nebo M3U playlist" };
        if (dlg.ShowDialog(this) == true) AddFiles(dlg.FileNames);
    }

    private void AddFileToFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not PlaylistItem item)
        {
            SetStatus("Vyber položku v playlistu.");
            return;
        }
        AddFavorite(GetPlaylistItemUrl(item), item.ToString());
    }

    private void CopyFileUrl_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is not PlaylistItem item)
        {
            SetStatus("Vyber položku v playlistu.");
            return;
        }
        CopyUrlToClipboard(GetPlaylistItemUrl(item));
    }

    private string GetPlaylistItemUrl(PlaylistItem item)
    {
        if (item.IsUrl) return item.Path;
        var ips = NetUtil.GetLocalIPv4();
        var ip = ips.Count > 0 ? ips[0] : IPAddress.Loopback;
        return _server.GetUrl(item.Path, ip);
    }

    private void SaveM3u_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0)
        {
            SetStatus("Playlist je prázdný – není co uložit.");
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "M3U playlist (*.m3u)|*.m3u|M3U8 playlist (*.m3u8)|*.m3u8",
            FileName = "playlist.m3u",
            Title = "Uložit playlist jako M3U"
        };
        if (dlg.ShowDialog(this) == true)
        {
            var content = M3uParser.Format(_files.Select(f => new M3uEntry(f.ToString(), f.Path)));
            File.WriteAllText(dlg.FileName, content);
            SetStatus($"Playlist uložen ({_files.Count} položek): {Path.GetFileName(dlg.FileName)}");
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        int idx = FilesList.SelectedIndex;
        if (idx <= 0) return;
        _files.Move(idx, idx - 1);
        if (_currentFileIndex == idx) _currentFileIndex--;
        else if (_currentFileIndex == idx - 1) _currentFileIndex++;
        FilesList.SelectedIndex = idx - 1;
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        int idx = FilesList.SelectedIndex;
        if (idx < 0 || idx >= _files.Count - 1) return;
        _files.Move(idx, idx + 1);
        if (_currentFileIndex == idx) _currentFileIndex++;
        else if (_currentFileIndex == idx + 1) _currentFileIndex--;
        FilesList.SelectedIndex = idx + 1;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var files = new List<string>();
        var enumOpts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true };
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                files.AddRange(Directory.EnumerateFiles(p, "*", enumOpts)
                    .Where(f => Mime.IsKnownExtension(Path.GetExtension(f)) || IsM3uFile(f))
                    .OrderBy(f => f));
            }
            else if (File.Exists(p))
            {
                files.Add(p);
            }
        }

        if (files.Count > 0)
        {
            AddFiles(files);
            MainTabs.SelectedIndex = 1;
        }
        else
        {
            SetStatus("Žádné podporované mediální soubory nebyly nalezeny.");
        }
    }

    private static bool IsM3uFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        int n = 0;
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            if (IsM3uFile(p))
            {
                try
                {
                    var text = File.ReadAllText(p);
                    if (M3uParser.IsChannelList(text))
                    {
                        foreach (var entry in M3uParser.Parse(text))
                        {
                            if (_files.Any(f => f.Path.Equals(entry.Url, StringComparison.OrdinalIgnoreCase))) continue;
                            _files.Add(new PlaylistItem(entry.Url, entry.Name, isUrl: true));
                            n++;
                        }
                        continue;
                    }
                }
                catch (Exception ex) { Log($"Chyba čtení M3U ({p}): {ex.Message}"); }
            }

            if (_files.Any(f => f.Path.Equals(p, StringComparison.OrdinalIgnoreCase))) continue;
            _files.Add(new PlaylistItem(p));
            n++;
        }
        SetStatus($"Přidáno položek: {n}");
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e) => RemoveSelectedFiles();

    private void FilesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            RemoveSelectedFiles();
            e.Handled = true;
        }
    }

    private void RemoveSelectedFiles()
    {
        foreach (var item in FilesList.SelectedItems.Cast<PlaylistItem>().ToList())
            _files.Remove(item);
        if (_currentFileIndex >= _files.Count) _currentFileIndex = _files.Count - 1;
    }

    private void ClearFiles_Click(object sender, RoutedEventArgs e)
    {
        _files.Clear();
        _currentFileIndex = -1;
        _slideshowTimer.Stop();
    }

    private async void PlaySelectedFile_Click(object sender, RoutedEventArgs e)
    {
        var idx = FilesList.SelectedIndex;
        if (idx < 0 && _files.Count > 0) idx = 0;
        if (idx >= 0) await SafeAsync(() => PlayFileAsync(idx));
    }

    private async void FilesList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesList.SelectedIndex >= 0) await SafeAsync(() => PlayFileAsync(FilesList.SelectedIndex));
    }

    private int GetNextPlaylistIndex(bool manual)
    {
        if (_files.Count == 0) return -1;
        if (!manual && _settings.RepeatMode == "one" && _currentFileIndex >= 0 && _currentFileIndex < _files.Count)
            return _currentFileIndex;
        if (_settings.Shuffle && _files.Count > 1)
        {
            int next;
            do { next = Random.Shared.Next(_files.Count); }
            while (next == _currentFileIndex);
            return next;
        }
        int candidate = _currentFileIndex + 1;
        if (candidate < _files.Count) return candidate;
        if (manual || _settings.RepeatMode == "all") return 0;
        return -1;
    }

    private async Task PlayFileAsync(int index)
    {
        if (index < 0 || index >= _files.Count) return;
        if (RequireDevice() is not { } r) return;
        var item = _files[index];
        if (!item.IsUrl && !File.Exists(item.Path))
        {
            SetStatus($"Soubor neexistuje: {item}");
            return;
        }

        bool transcode = TranscodeCheck.IsChecked == true;
        if (transcode && !ScreenStream.IsFfmpegAvailable())
        {
            var ans = MessageBox.Show(this,
                "Kompatibilní režim (překódování + vypálení titulků) vyžaduje ffmpeg.exe.\n\n" +
                "Chceš ho teď stáhnout automaticky?",
                "Potřeba ffmpeg.exe", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans == MessageBoxResult.Yes)
            {
                MainTabs.SelectedIndex = 4;
                await DownloadToolAsync(isFfmpeg: true);
            }
            return;
        }

        await _playLock.WaitAsync(_cts.Token);
        _isStartingPlayback = true;
        try
        {
            await SafeAsync(async () =>
            {
                StopScreenStream();
                StopTranscodeStream();
                _slideshowTimer.Stop();

                BeginPlayback();
                _playingFromPlaylist = true;
                _playingFromYtSearch = false;
                _currentYtIndex = -1;
                _currentFileIndex = index;
                FilesList.SelectedIndex = index;
                SetStatus(r.IsLocalMonitor ? "Otevírám na monitoru PC…" : "Posílám na TV…");

                string? subPath = null;
                if (!item.IsUrl && Mime.FromPath(item.Path).StartsWith("video", StringComparison.OrdinalIgnoreCase))
                    subPath = Subtitles.FindLocalSubtitleSrt(item.Path);

                bool burnLocalSubs = !r.IsLocalMonitor && (transcode || (BurnSubtitlesCheck.IsChecked == true && subPath != null && ScreenStream.IsFfmpegAvailable()));
                if (burnLocalSubs && (item.IsUrl || !Mime.FromPath(item.Path).StartsWith("image", StringComparison.OrdinalIgnoreCase)))
                {
                    var host = await DlnaRenderer.GetHostAddressAsync(r);
                    var local = NetUtil.GetLocalIpFor(host);
                    _transcodeStream = new TranscodeStream { Log = s => Dispatcher.BeginInvoke(() => Log(s)) };
                    const string liveId = "transcode";
                    _server.RegisterLiveStream(liveId, "video/mp2t", (s, ct) => _transcodeStream.StreamToAsync(item.Path, subPath, s, ct));
                    var liveUrl = _server.GetLiveUrl(liveId, local);

                    Log($"Transcode {item} → {liveUrl}");
                    await r.PlayAsync(liveUrl, item.ToString(), "video/mp2t", isLocal: true);
                    NowPlayingText.Text = $"🎞 {item}  ({index + 1}/{_files.Count})";
                }
                else if (item.IsUrl)
                {
                    await r.PlayAsync(item.Path, item.ToString(), Mime.FromUrl(item.Path), isLocal: false);
                    NowPlayingText.Text = $"📺 {item}  ({index + 1}/{_files.Count})";
                }
                else
                {
                    var host = await DlnaRenderer.GetHostAddressAsync(r);
                    var local = NetUtil.GetLocalIpFor(host);
                    var url = _server.GetUrl(item.Path, local);
                    var mime = Mime.FromPath(item.Path);
                    string? subtitleUrl = null;
                    if (subPath != null)
                    {
                        subtitleUrl = _server.GetUrl(subPath, local);
                        _server.RegisterSubtitleForMedia(url, subtitleUrl);
                        Log($"Titulky: {Path.GetFileName(subPath)}");
                    }
                    else
                    {
                        _server.RegisterSubtitleForMedia(url, null);
                    }

                    Log($"Soubor {item} → {url}");
                    await r.PlayAsync(url, item.ToString(), mime, isLocal: true, subtitleUrl: subtitleUrl);
                    NowPlayingText.Text = $"📁 {item}  ({index + 1}/{_files.Count})";

                    if (mime.StartsWith("image", StringComparison.OrdinalIgnoreCase) &&
                        _settings.SlideshowSeconds > 0 && (_files.Count > 1 || _settings.RepeatMode == "one"))
                    {
                        _slideshowTimer.Interval = TimeSpan.FromSeconds(_settings.SlideshowSeconds);
                        _slideshowTimer.Start();
                    }
                    else if (!mime.StartsWith("image", StringComparison.OrdinalIgnoreCase) &&
                             AutoNextCheck.IsChecked == true && _files.Count > 1)
                    {
                        int nextIdx = GetNextPlaylistIndex(manual: false);
                        if (nextIdx >= 0 && nextIdx != index)
                        {
                            var nextItem = _files[nextIdx];
                            string nextUrl = nextItem.IsUrl ? nextItem.Path : _server.GetUrl(nextItem.Path, local);
                            string nextMime = nextItem.IsUrl ? Mime.FromUrl(nextItem.Path) : Mime.FromPath(nextItem.Path);
                            _ = SafeAsync(() => r.TrySetNextUriAsync(nextUrl, nextItem.ToString(), nextMime, !nextItem.IsUrl), quiet: true);
                        }
                    }
                }

                SyncMiniPlayer();
                SetStatus(r.IsLocalMonitor ? "Přehrává se na monitoru PC" : "Přehrává se na TV");
            });
        }
        finally
        {
            _isStartingPlayback = false;
            _playLock.Release();
        }
    }

    private async Task NextAsync()
    {
        if (_playingFromYtSearch && _ytResults.Count > 0)
        {
            await PlayNextYtResultAsync();
            return;
        }
        if (_files.Count == 0) return;
        var next = GetNextPlaylistIndex(manual: true);
        if (next >= 0) await PlayFileAsync(next);
    }

    private async Task PrevAsync()
    {
        if (_playingFromYtSearch && _ytResults.Count > 0)
        {
            await PlayPrevYtResultAsync();
            return;
        }
        if (_files.Count == 0) return;
        int prev = _currentFileIndex <= 0 ? _files.Count - 1 : _currentFileIndex - 1;
        await PlayFileAsync(prev);
    }

    private async void Next_Click(object sender, RoutedEventArgs e) => await SafeAsync(NextAsync);
    private async void Prev_Click(object sender, RoutedEventArgs e) => await SafeAsync(PrevAsync);
}
