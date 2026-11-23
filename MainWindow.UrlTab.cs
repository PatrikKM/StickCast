using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using StickCast.Net;

namespace StickCast;

public partial class MainWindow
{
    private const int YtPageSize = 10;

    private async void PlayUrl_Click(object sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        var box = UrlBox.Text.Trim();
        bool boxIsHttp = box.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                         box.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!boxIsHttp && YtResultsPanel.Visibility == Visibility.Visible &&
            YtResultsList.SelectedItem is YtSearchResult sel &&
            !string.IsNullOrWhiteSpace(_ytLastQuery) &&
            box.Equals(_ytLastQuery, StringComparison.OrdinalIgnoreCase))
        {
            await PlayUrlAsync(sel.Url, ytIndex: YtResultsList.SelectedIndex);
            return;
        }
        await PlayUrlAsync(box);
    });

    private async void PasteAndPlay_Click(object sender, RoutedEventArgs e) => await SafeAsync(async () =>
    {
        if (!Clipboard.ContainsText())
        {
            SetStatus("Schránka neobsahuje text.");
            return;
        }
        var text = Clipboard.GetText().Trim();
        if (string.IsNullOrEmpty(text))
        {
            SetStatus("Schránka je prázdná.");
            return;
        }
        UrlBox.Text = text;
        await PlayUrlAsync(text);
    });

    private async void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SafeAsync(() => PlayUrlAsync(UrlBox.Text));
    }

    private string GetEffectiveSearchQuery()
    {
        var box = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(box))
            return _ytLastQuery;
        if (!string.IsNullOrWhiteSpace(_ytLastQuery) &&
            (box.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             box.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
            (_ytResults.Any(r => r.Url.Equals(box, StringComparison.OrdinalIgnoreCase)) ||
             _ytPageCache.Values.SelectMany(x => x).Any(r => r.Url.Equals(box, StringComparison.OrdinalIgnoreCase)) ||
             YtDlp.IsVideoPortal(box)))
        {
            return _ytLastQuery;
        }
        return box;
    }

    private async void YtSearch_Click(object sender, RoutedEventArgs e) =>
        await SafeAsync(() => SearchYouTubeAsync(GetEffectiveSearchQuery(), pageIndex: 0, isMusic: false));

    private async void YtMusicSearch_Click(object sender, RoutedEventArgs e) =>
        await SafeAsync(() => SearchYouTubeAsync(GetEffectiveSearchQuery(), pageIndex: 0, isMusic: true));

    private async void YtNextTrack_Click(object sender, RoutedEventArgs e) =>
        await SafeAsync(PlayNextYtResultAsync);

    private async void YtPrevPage_Click(object sender, RoutedEventArgs e)
    {
        var q = GetEffectiveSearchQuery();
        if (string.IsNullOrWhiteSpace(q) || _ytPageIndex <= 0) return;
        bool wasPlayingYt = _pcPlaybackActive && (_playingFromYtSearch || !_playingFromPlaylist);
        await SafeAsync(async () =>
        {
            await SearchYouTubeAsync(q, _ytPageIndex - 1, _ytLastIsMusic);
            if (wasPlayingYt && _ytResults.Count > 0)
                await PlayUrlAsync(_ytResults[0].Url, ytIndex: 0);
        });
    }

    private async void YtNextPage_Click(object sender, RoutedEventArgs e)
    {
        var q = GetEffectiveSearchQuery();
        if (string.IsNullOrWhiteSpace(q)) return;
        bool wasPlayingYt = _pcPlaybackActive && (_playingFromYtSearch || !_playingFromPlaylist);
        int prevPage = _ytPageIndex;
        await SafeAsync(async () =>
        {
            await SearchYouTubeAsync(q, _ytPageIndex + 1, _ytLastIsMusic);
            if (wasPlayingYt && _ytPageIndex > prevPage && _ytResults.Count > 0)
                await PlayUrlAsync(_ytResults[0].Url, ytIndex: 0);
        });
    }

    private void CloseYtResults_Click(object sender, RoutedEventArgs e)
    {
        YtResultsPanel.Visibility = Visibility.Collapsed;
        _ytResults.Clear();
        _ytPageCache.Clear();
        _ytPageIndex = 0;
        _playingFromYtSearch = false;
        _currentYtIndex = -1;
    }

    private async void YtResultsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (YtResultsList.SelectedItem is YtSearchResult res)
        {
            int idx = YtResultsList.SelectedIndex;
            await SafeAsync(() => PlayUrlAsync(res.Url, ytIndex: idx));
        }
    }

    private void YtResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Keep _currentYtIndex pointed at the actually playing item until playback starts
    }

    private async void PlaySelectedYt_Click(object sender, RoutedEventArgs e)
    {
        if (YtResultsList.SelectedItem is YtSearchResult res)
        {
            int idx = YtResultsList.SelectedIndex;
            await SafeAsync(() => PlayUrlAsync(res.Url, ytIndex: idx));
        }
    }

    private void AddYtToFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (YtResultsList.SelectedItem is YtSearchResult res)
            AddFavorite(res.Url, res.Title);
        else
            SetStatus("Vyber nejprve video ve výsledcích YouTube.");
    }

    private void CopyYtUrl_Click(object sender, RoutedEventArgs e)
    {
        if (YtResultsList.SelectedItem is YtSearchResult res)
            CopyUrlToClipboard(res.Url);
        else
            SetStatus("Vyber nejprve video ve výsledcích YouTube.");
    }

    private void OpenBoxInChrome_Click(object sender, RoutedEventArgs e)
    {
        var box = UrlBox.Text.Trim();
        if (box.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            box.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            OpenInChrome(box);
            return;
        }
        if (YtResultsList.SelectedItem is YtSearchResult yt)
        {
            OpenInChrome(yt.Url, yt.Title);
            return;
        }
        if (UrlSubTabs.SelectedIndex == 1 && FavoritesList.SelectedItem is UrlEntry fav)
        {
            OpenInChrome(fav.Url, fav.Title);
            return;
        }
        if (HistoryList.SelectedItem is UrlEntry hist)
        {
            OpenInChrome(hist.Url, hist.Title);
            return;
        }
        if (!string.IsNullOrEmpty(box))
        {
            OpenInChrome($"https://www.youtube.com/results?search_query={Uri.EscapeDataString(box)}", box);
            return;
        }
        SetStatus("Zadej URL nebo vyber video pro spuštění v Chrome.");
    }

    private void OpenYtInChrome_Click(object sender, RoutedEventArgs e)
    {
        if (YtResultsList.SelectedItem is YtSearchResult res)
            OpenInChrome(res.Url, res.Title);
        else if (!string.IsNullOrWhiteSpace(UrlBox.Text) && UrlBox.Text.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase))
            OpenInChrome(UrlBox.Text.Trim());
        else
            SetStatus("Vyber nejprve video ve výsledcích YouTube.");
    }

    private void OpenHistoryInChrome_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is UrlEntry entry)
            OpenInChrome(entry.Url, entry.Title);
        else
            SetStatus("Vyber položku v historii.");
    }

    private void OpenFavoriteInChrome_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is UrlEntry entry)
            OpenInChrome(entry.Url, entry.Title);
        else
            SetStatus("Vyber položku v oblíbených.");
    }

    private void OpenInChrome(string? url, string? title = null)
    {
        var clean = url?.Trim() ?? "";
        if (!Uri.TryCreate(clean, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatus("Neplatná HTTP/HTTPS URL adresa pro prohlížeč.");
            return;
        }

        try
        {
            var chromeExe = FindChromeExecutable();
            if (chromeExe != null)
            {
                var psi = new ProcessStartInfo(chromeExe) { UseShellExecute = false };
                psi.ArgumentList.Add(clean);
                Process.Start(psi);
                SetStatus($"🌐 Otevřeno v Google Chrome: {title ?? clean}");
            }
            else
            {
                Process.Start(new ProcessStartInfo(clean) { UseShellExecute = true });
                SetStatus($"🌐 Chrome nenalezen – otevřeno ve výchozím prohlížeči: {title ?? clean}");
            }
            AddRecentUrl(clean, title);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static string? FindChromeExecutable()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe"),
        };

        foreach (var path in candidates)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return path;
        }

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
                var regPath = key?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(regPath) && File.Exists(regPath))
                    return regPath;
            }
            catch { }
        }

        return null;
    }

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
        {
            if (UrlSubTabs.SelectedIndex == 1 && FavoritesList.SelectedItem is UrlEntry fav)
                url = fav.Url;
            else if (HistoryList.SelectedItem is UrlEntry hist)
                url = hist.Url;
            else if (YtResultsList.SelectedItem is YtSearchResult yt)
                url = yt.Url;
        }
        CopyUrlToClipboard(url);
    }

    private void AddFavoriteFromBox_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (!string.IsNullOrEmpty(url))
        {
            AddFavorite(url, ResolveDisplayTitle(url));
            return;
        }
        if (YtResultsList.SelectedItem is YtSearchResult yt)
        {
            AddFavorite(yt.Url, yt.Title);
            return;
        }
        if (HistoryList.SelectedItem is UrlEntry hist)
        {
            AddFavorite(hist.Url, hist.Title);
            return;
        }
        SetStatus("Zadej nebo vyber URL adresu pro uložení do oblíbených.");
    }

    // ------------------------------------------------------------------ History & Favorites

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiReady && HistoryList.SelectedItem is UrlEntry entry)
            UrlBox.Text = entry.Url;
    }

    private async void HistoryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is UrlEntry entry)
        {
            UrlBox.Text = entry.Url;
            await SafeAsync(() => PlayUrlAsync(entry.Url));
        }
    }

    private async void PlaySelectedHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is UrlEntry entry)
        {
            UrlBox.Text = entry.Url;
            await SafeAsync(() => PlayUrlAsync(entry.Url));
        }
        else
        {
            SetStatus("Vyber položku v historii.");
        }
    }

    private void AddHistoryToFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is UrlEntry entry)
            AddFavorite(entry.Url, entry.Title);
        else
            SetStatus("Vyber položku v historii.");
    }

    private void CopyHistoryUrl_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is UrlEntry entry)
            CopyUrlToClipboard(entry.Url);
        else
            SetStatus("Vyber položku v historii.");
    }

    private void RemoveHistory_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is UrlEntry entry)
        {
            _history.Remove(entry);
            SaveSettings();
            SetStatus("Položka odebrána z historie.");
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        SaveSettings();
        SetStatus("Historie vymazána.");
    }

    private void HistoryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && HistoryList.SelectedItem is UrlEntry entry)
        {
            _history.Remove(entry);
            SaveSettings();
            e.Handled = true;
        }
    }

    private void FavoritesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_uiReady && FavoritesList.SelectedItem is UrlEntry entry)
            UrlBox.Text = entry.Url;
    }

    private async void FavoritesList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FavoritesList.SelectedItem is UrlEntry entry)
        {
            UrlBox.Text = entry.Url;
            await SafeAsync(() => PlayUrlAsync(entry.Url));
        }
    }

    private async void PlaySelectedFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is UrlEntry entry)
        {
            UrlBox.Text = entry.Url;
            await SafeAsync(() => PlayUrlAsync(entry.Url));
        }
        else
        {
            SetStatus("Vyber položku v oblíbených.");
        }
    }

    private void CopyFavoriteUrl_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is UrlEntry entry)
            CopyUrlToClipboard(entry.Url);
        else
            SetStatus("Vyber položku v oblíbených.");
    }

    private void MoveFavoriteUp_Click(object sender, RoutedEventArgs e)
    {
        int idx = FavoritesList.SelectedIndex;
        if (idx <= 0) return;
        _favorites.Move(idx, idx - 1);
        FavoritesList.SelectedIndex = idx - 1;
        SaveSettings();
    }

    private void MoveFavoriteDown_Click(object sender, RoutedEventArgs e)
    {
        int idx = FavoritesList.SelectedIndex;
        if (idx < 0 || idx >= _favorites.Count - 1) return;
        _favorites.Move(idx, idx + 1);
        FavoritesList.SelectedIndex = idx + 1;
        SaveSettings();
    }

    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is UrlEntry entry)
        {
            _favorites.Remove(entry);
            SaveSettings();
            SetStatus("Odebráno z oblíbených.");
        }
    }

    private void ClearFavorites_Click(object sender, RoutedEventArgs e)
    {
        _favorites.Clear();
        SaveSettings();
        SetStatus("Oblíbené vymazány.");
    }

    private void FavoritesList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && FavoritesList.SelectedItem is UrlEntry entry)
        {
            _favorites.Remove(entry);
            SaveSettings();
            e.Handled = true;
        }
    }

    private void AddFavorite(string url, string? title = null)
    {
        var cleanUrl = url.Trim();
        if (string.IsNullOrEmpty(cleanUrl)) return;
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? ResolveDisplayTitle(cleanUrl) : title.Trim();
        var existing = _favorites.FirstOrDefault(f => f.Url.Equals(cleanUrl, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            int idx = _favorites.IndexOf(existing);
            _favorites[idx] = new UrlEntry(cleanUrl, cleanTitle);
            SetStatus($"Aktualizováno v oblíbených: {cleanTitle}");
        }
        else
        {
            _favorites.Insert(0, new UrlEntry(cleanUrl, cleanTitle));
            SetStatus($"⭐ Přidáno do oblíbených: {cleanTitle}");
        }
        SaveSettings();
    }

    private void CopyUrlToClipboard(string? url)
    {
        var clean = url?.Trim() ?? "";
        if (string.IsNullOrEmpty(clean))
        {
            SetStatus("Žádná URL adresa ke zkopírování.");
            return;
        }
        try
        {
            Clipboard.SetText(clean);
            SetStatus($"📋 URL zkopírována do schránky: {clean}");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private string ResolveDisplayTitle(string url) =>
        _ytResults.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase))?.Title
        ?? _ytPageCache.Values.SelectMany(x => x).FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase))?.Title
        ?? _favorites.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase))?.Title
        ?? _history.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase))?.Title
        ?? ShortTitle(url);

    // ------------------------------------------------------------------ YouTube / YouTube Music Search & PlayUrlAsync

    private async Task SearchYouTubeAsync(string query, int pageIndex = 0, bool? isMusic = null)
    {
        var cleanQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(cleanQuery))
        {
            SetStatus("Napiš hledaný výraz do pole výše.");
            return;
        }

        var ytdlp = YtDlp.Find();
        if (ytdlp == null)
        {
            MessageBox.Show(this,
                "Pro vyhledávání na YouTube / YouTube Music je potřeba program yt-dlp.exe.\n\n" +
                "Můžeš ho stáhnout jedním klikem v záložce ⚙ Stick → „⬇ Stáhnout yt-dlp.exe“.",
                "Potřeba yt-dlp", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool music = isMusic ?? _ytLastIsMusic;
        pageIndex = Math.Max(0, pageIndex);
        if (!cleanQuery.Equals(_ytLastQuery, StringComparison.OrdinalIgnoreCase) || music != _ytLastIsMusic)
        {
            _ytLastQuery = cleanQuery;
            _ytLastIsMusic = music;
            _ytPageCache.Clear();
            pageIndex = 0;
        }

        int startNum = pageIndex * YtPageSize + 1;
        int endNum = startNum + YtPageSize - 1;
        string sourceLabel = music ? "YouTube Music" : "YouTube";

        YtPrevPageButton.IsEnabled = false;
        YtNextPageButton.IsEnabled = false;
        try
        {
            if (!_ytPageCache.TryGetValue(pageIndex, out var results))
            {
                SetStatus($"Hledám na {sourceLabel} ({startNum}–{endNum}): {cleanQuery}…");
                results = await YtDlp.SearchAsync(
                    ytdlp,
                    cleanQuery,
                    maxResults: YtPageSize,
                    _cts.Token,
                    offset: pageIndex * YtPageSize,
                    youtubeMusic: music);
                if (results.Count > 0)
                    _ytPageCache[pageIndex] = results;
            }

            if (results.Count == 0 && pageIndex > 0)
            {
                SetStatus($"Další výsledky na {sourceLabel} už nebyly nalezeny.");
                return;
            }

            _ytPageIndex = pageIndex;
            _ytResults.Clear();
            foreach (var item in results) _ytResults.Add(item);
            if (_ytResults.Count > 0)
            {
                _currentYtIndex = -1;
                YtResultsList.SelectedIndex = 0;
            }

            int shownEnd = startNum + Math.Max(0, _ytResults.Count - 1);
            var prefix = music ? "🎵 Výsledky z YouTube Music" : "Výsledky z YouTube";
            YtResultsTitleText.Text = $"{prefix} ({startNum}–{shownEnd}, dvojklik = přehrát):";
            YtResultsPanel.Visibility = _ytResults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            SetStatus(_ytResults.Count > 0
                ? $"Zobrazeny výsledky {sourceLabel} {startNum}–{shownEnd} pro „{cleanQuery}“"
                : $"Na {sourceLabel} nebylo nic nalezeno.");
        }
        finally
        {
            YtPrevPageButton.IsEnabled = _ytPageIndex > 0;
            YtNextPageButton.IsEnabled = _ytResults.Count > 0;
        }
    }

    private async Task PlayNextYtResultAsync()
    {
        if (_ytResults.Count == 0) return;

        int baseIdx = _currentYtIndex >= 0
            ? _currentYtIndex
            : (YtResultsList.SelectedIndex >= 0 ? YtResultsList.SelectedIndex : -1);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            int nextIdx = baseIdx + 1;
            if (nextIdx >= 0 && nextIdx < _ytResults.Count)
            {
                var next = _ytResults[nextIdx];
                baseIdx = nextIdx;
                _currentYtIndex = nextIdx;
                _playingFromYtSearch = true;
                YtResultsList.SelectedIndex = nextIdx;
                YtResultsList.ScrollIntoView(next);
                long reqBefore = _playRequestId;
                bool ok = await PlayUrlAsync(next.Url, ytIndex: nextIdx, allowSkipOnError: true);
                if (ok || _playRequestId != reqBefore + 1)
                    return;
                Log($"Přeskakuji nedostupnou položku: {next.Title}");
                continue;
            }

            var q = !string.IsNullOrWhiteSpace(_ytLastQuery) ? _ytLastQuery : GetEffectiveSearchQuery();
            if (!string.IsNullOrWhiteSpace(q))
            {
                int prevPage = _ytPageIndex;
                await SearchYouTubeAsync(q, _ytPageIndex + 1, _ytLastIsMusic);
                if (_ytPageIndex > prevPage && _ytResults.Count > 0)
                {
                    var first = _ytResults[0];
                    baseIdx = 0;
                    _currentYtIndex = 0;
                    _playingFromYtSearch = true;
                    YtResultsList.SelectedIndex = 0;
                    YtResultsList.ScrollIntoView(first);
                    long reqBefore = _playRequestId;
                    bool ok = await PlayUrlAsync(first.Url, ytIndex: 0, allowSkipOnError: true);
                    if (ok || _playRequestId != reqBefore + 1)
                        return;
                    continue;
                }
            }
            break;
        }
    }

    private async Task PlayPrevYtResultAsync()
    {
        if (_ytResults.Count == 0) return;

        int baseIdx = _currentYtIndex >= 0
            ? _currentYtIndex
            : (YtResultsList.SelectedIndex >= 0 ? YtResultsList.SelectedIndex : 0);

        int prevIdx = baseIdx - 1;
        if (prevIdx >= 0 && prevIdx < _ytResults.Count)
        {
            var prev = _ytResults[prevIdx];
            _currentYtIndex = prevIdx;
            _playingFromYtSearch = true;
            YtResultsList.SelectedIndex = prevIdx;
            YtResultsList.ScrollIntoView(prev);
            await PlayUrlAsync(prev.Url, ytIndex: prevIdx);
            return;
        }

        var q = !string.IsNullOrWhiteSpace(_ytLastQuery) ? _ytLastQuery : GetEffectiveSearchQuery();
        if (_ytPageIndex > 0 && !string.IsNullOrWhiteSpace(q))
        {
            await SearchYouTubeAsync(q, _ytPageIndex - 1, _ytLastIsMusic);
            if (_ytResults.Count > 0)
            {
                int lastIdx = _ytResults.Count - 1;
                var last = _ytResults[lastIdx];
                _currentYtIndex = lastIdx;
                _playingFromYtSearch = true;
                YtResultsList.SelectedIndex = lastIdx;
                YtResultsList.ScrollIntoView(last);
                await PlayUrlAsync(last.Url, ytIndex: lastIdx);
            }
        }
    }

    private async Task<bool> PlayUrlAsync(string input, int? ytIndex = null, bool allowSkipOnError = false)
    {
        var url = input.Trim();
        if (url.Length == 0) return false;

        bool looksLikeHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!looksLikeHttp && !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            await SearchYouTubeAsync(url);
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUri))
        {
            SetStatus("Neplatná URL adresa");
            return false;
        }

        if (looksLikeHttp && await TryLoadRemoteM3uAsync(url, parsedUri))
            return true;

        if (RequireDevice() is not { } r) return false;

        long myReqId = ++_playRequestId;
        try { _resolveCts?.Cancel(); } catch { }
        await _playLock.WaitAsync(_cts.Token);
        if (myReqId != _playRequestId)
        {
            _playLock.Release();
            return false;
        }

        using var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _resolveCts = resolveCts;
        _isStartingPlayback = true;
        bool started = false;
        try
        {
            await SafeAsync(async () =>
            {
                StopScreenStream();
                StopTranscodeStream();
                _slideshowTimer.Stop();

                _lastPlayedUrl = url;
                int matchedYtIdx = ytIndex ?? -1;
                if (matchedYtIdx < 0)
                {
                    for (int i = 0; i < _ytResults.Count; i++)
                    {
                        if (_ytResults[i].Url.Equals(url, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedYtIdx = i;
                            break;
                        }
                    }
                }
                _playingFromYtSearch = matchedYtIdx >= 0;
                _currentYtIndex = matchedYtIdx;
                if (matchedYtIdx >= 0 && YtResultsList.SelectedIndex != matchedYtIdx)
                    YtResultsList.SelectedIndex = matchedYtIdx;

                var playUrl = url;
                bool resolvedByYtDlp = false;
                string? subPath = null;
                string? subSource = null;
                string? ytTitle = null;

                if (!Mime.UrlLooksLikeMedia(url))
                {
                    var ytdlp = YtDlp.Find();
                    if (ytdlp != null)
                    {
                        bool wantSubs = AutoCzechSubtitlesCheck.IsChecked == true;
                        SetStatus(wantSubs ? "Získávám odkaz a české titulky…" : "Získávám přímý odkaz přes yt-dlp…");
                        try
                        {
                            var resolved = await YtDlp.ResolveWithSubtitlesAsync(ytdlp, url, wantSubs, resolveCts.Token);
                            playUrl = resolved.MediaUrl;
                            ytTitle = resolved.Title;
                            subPath = resolved.SubtitleSrtPath;
                            subSource = resolved.SubtitleSource;
                            resolvedByYtDlp = true;
                            Log("yt-dlp → " + playUrl);
                            if (subPath != null) Log($"Titulky ({subSource}): {subPath}");
                        }
                        catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex) when (allowSkipOnError)
                        {
                            Log($"Nelze získat stream ({ex.Message})");
                            SetStatus($"Přeskakuji nedostupnou skladbu…");
                            return;
                        }
                        catch (Exception ex) when (!YtDlp.IsVideoPortal(url))
                        {
                            Log(ex.Message + " – zkouším poslat odkaz přímo.");
                        }
                    }
                    else if (YtDlp.IsVideoPortal(url))
                    {
                        MessageBox.Show(this,
                            "Odkazy na YouTube a jiné videoportály stick/TV sám neumí přehrát.\n\n" +
                            "Stáhni si yt-dlp.exe jedním klikem v záložce ⚙ Stick → „⬇ Stáhnout yt-dlp.exe“.",
                            "Potřeba yt-dlp", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                }

                BeginPlayback();
                _playingFromPlaylist = false;
                SetStatus(r.IsLocalMonitor ? "Otevírám na monitoru PC…" : "Posílám na TV…");

                var detectedMime = resolvedByYtDlp && !playUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                    ? "video/mp4"
                    : Mime.FromUrl(playUrl);
                bool isHls = detectedMime.Equals("application/vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase) ||
                             playUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                             playUrl.Contains("manifest.googlevideo.com", StringComparison.OrdinalIgnoreCase);

                var title = !string.IsNullOrWhiteSpace(ytTitle) ? ytTitle : ResolveDisplayTitle(url);
                var host = await DlnaRenderer.GetHostAddressAsync(r);
                var local = NetUtil.GetLocalIpFor(host);

                bool burnSubs = !r.IsLocalMonitor && BurnSubtitlesCheck.IsChecked == true && subPath != null;
                bool useTranscode = (isHls || TranscodeCheck.IsChecked == true || burnSubs) && ScreenStream.IsFfmpegAvailable();

                if (useTranscode)
                {
                    await StartUrlTranscodeAsync(r, playUrl, title, local, subPath);
                }
                else if (resolvedByYtDlp || playUrl.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) || subPath != null)
                {
                    var ext = isHls ? ".ts" : ".mp4";
                    var mime = isHls ? "video/mp2t" : "video/mp4";
                    var proxyUrl = _server.GetProxyUrl(playUrl, local, mime, ext);
                    string? subUrl = subPath != null ? _server.GetUrl(subPath, local) : null;
                    _server.RegisterSubtitleForMedia(proxyUrl, subUrl);
                    Log($"Lokální DLNA proxy → {proxyUrl}" + (subUrl != null ? $" (+ titulky {subUrl})" : ""));
                    try
                    {
                        await r.PlayAsync(proxyUrl, title, mime, isLocal: true, subtitleUrl: subUrl);
                    }
                    catch (DlnaException ex) when (ScreenStream.IsFfmpegAvailable())
                    {
                        Log($"Proxy odmítnuto ({ex.Message}) – přepínám na lokální ffmpeg MPEG-TS stream…");
                        await StartUrlTranscodeAsync(r, playUrl, title, local, subPath);
                    }
                }
                else
                {
                    await r.PlayAsync(playUrl, title, detectedMime, isLocal: false);
                }

                NowPlayingText.Text = "🌐 " + title + (subSource != null ? $"  [{subSource}]" : "");
                SyncMiniPlayer();
                var targetLabel = r.IsLocalMonitor ? "monitoru PC" : "TV";
                SetStatus(subSource != null ? $"Přehrává se na {targetLabel} ({subSource})" : $"Přehrává se na {targetLabel}");
                AddRecentUrl(url, title);
                started = true;
            });
            return started;
        }
        finally
        {
            if (ReferenceEquals(_resolveCts, resolveCts)) _resolveCts = null;
            _isStartingPlayback = false;
            _playLock.Release();
        }
    }

    private async Task<bool> TryLoadRemoteM3uAsync(string url, Uri parsedUri)
    {
        if (!parsedUri.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) &&
            !parsedUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            SetStatus("Načítám playlist…");
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            reqCts.CancelAfter(TimeSpan.FromSeconds(6));
            var text = await HttpClients.Standard.GetStringAsync(url, reqCts.Token);
            if (!M3uParser.IsChannelList(text)) return false;

            var entries = M3uParser.Parse(text);
            if (entries.Count == 0) return false;

            int firstNew = _files.Count;
            foreach (var entry in entries)
            {
                if (!_files.Any(f => f.Path.Equals(entry.Url, StringComparison.OrdinalIgnoreCase)))
                    _files.Add(new PlaylistItem(entry.Url, entry.Name, isUrl: true));
            }
            AddRecentUrl(url);
            MainTabs.SelectedIndex = 1;
            Log($"Načten IPTV playlist: {entries.Count} kanálů");
            SetStatus($"Načteno IPTV kanálů: {entries.Count}");
            if (firstNew < _files.Count) await PlayFileAsync(firstNew);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Kontrola M3U playlistu selhala ({ex.Message}) – zkouším přehrát přímo.");
            return false;
        }
    }

    private async Task StartUrlTranscodeAsync(DlnaRenderer r, string playUrl, string title, IPAddress local, string? subtitlePath = null)
    {
        StopTranscodeStream();
        _transcodeStream = new TranscodeStream { Log = s => Dispatcher.BeginInvoke(() => Log(s)) };
        const string liveId = "transcode";
        _server.RegisterLiveStream(liveId, "video/mp2t", (s, ct) => _transcodeStream.StreamToAsync(playUrl, subtitlePath, s, ct));
        var liveUrl = _server.GetLiveUrl(liveId, local);
        Log($"Stream přes lokální ffmpeg MPEG-TS → {liveUrl}" + (subtitlePath != null ? " (vypálené titulky)" : ""));
        await r.PlayAsync(liveUrl, title, "video/mp2t", isLocal: true);
    }

    private void AddRecentUrl(string url, string? title = null)
    {
        var cleanUrl = url.Trim();
        if (string.IsNullOrEmpty(cleanUrl)) return;
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? ResolveDisplayTitle(cleanUrl) : title.Trim();
        var existing = _history.FirstOrDefault(h => h.Url.Equals(cleanUrl, StringComparison.OrdinalIgnoreCase));
        if (existing != null) _history.Remove(existing);
        _history.Insert(0, new UrlEntry(cleanUrl, cleanTitle, DateTime.Now.ToString("dd.MM. HH:mm")));
        while (_history.Count > 50) _history.RemoveAt(_history.Count - 1);
        SaveSettings();
    }

    private static string ShortTitle(string url)
    {
        try
        {
            var u = new Uri(url);
            var name = Path.GetFileName(u.AbsolutePath);
            return string.IsNullOrEmpty(name) ? u.Host : Uri.UnescapeDataString(name);
        }
        catch { return url; }
    }
}
