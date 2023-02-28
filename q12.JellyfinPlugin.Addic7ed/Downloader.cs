using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using MediaBrowser.Common.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace q12.JellyfinPlugin.Addic7ed;

public sealed class SubtitleEntry
{
    public string Title { get; set; }

    public string Language { get; set; }

    public string Version { get; set; }

    public int Season { get; set; }

    public int Episode { get; set; }

    public bool HearingImpaired { get; set; }

    public string DownloadLinkFragment { get; set; }

    // public string? PageLink { get; set; }
}

public sealed class Downloader : IDisposable
{
    private const string ServerDomain = "addic7ed.com";
    public const string ServerUrl = $"https://www.{ServerDomain}";
    private static readonly Regex _showCellsRe = new(@"<td class=""(?:version|vr)"">.*?</td>", RegexOptions.Singleline);
    private static readonly Regex _seriesYearRe = new(@"^(?<series>[ \w'.:(),*&!?-]+?)(?: \((?<year>\d{4})\))?$");
    private static readonly string[] _sanitizeCharacters = { "-", ":", "(", ")", ".", "/" };
    private readonly ILogger<SubtitleProvider> _logger;
    private readonly string _showsCacheFile;
    private readonly IMemoryCache _showMemoryCache;
    private long _httpClientExpungeTime;
    private HttpClient? _httpClient;
    private HttpClientHandler? _httpClientHandler;
    private Dictionary<string, int>? _showIds;
    private int _downloadedSubtitleCount;
    private int _totalAllowedDownloads;

    public Downloader(ILogger<SubtitleProvider> logger, string cacheDirectory)
    {
        _logger = logger;
        _showMemoryCache = new MemoryCache(new MemoryCacheOptions());
        _showsCacheFile = Path.Combine(cacheDirectory, "addic7edShows");
    }

    private async Task<HttpClient> GetHttpClientAsync(CancellationToken cancel = default)
    {
        if (_httpClient is not null)
        {
            if (Environment.TickCount64 < _httpClientExpungeTime)
            {
                _logger.LogDebug("Reusing HttpClient");
                return _httpClient;
            }

            _httpClient.Dispose();
            _httpClient = null;
        }

        HttpClient? client = null;
        try
        {
            _logger.LogDebug("Instantiating new HttpClient");
            var cookies = await Firefox.GetCookiesForDomainAsync(ServerDomain, cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            if (cookies.Count == 0)
            {
                throw new AuthenticationException($"No cookies found, log into {ServerDomain} with Firefox");
            }

            _httpClientHandler?.Dispose();
            _httpClientHandler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli, CookieContainer = cookies, AllowAutoRedirect = false, CheckCertificateRevocationList = true };

            client = new HttpClient(_httpClientHandler, disposeHandler: false);
            // TODO: these headers' order, with each HttpRequestMessage adding its own headers too, is seriously shot to shit
            client.DefaultRequestHeaders.Add("User-Agent", Firefox.DefaultUa);
            client.DefaultRequestHeaders.Add("Accept-Language", "en-GB,en;q=0.5");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            client.DefaultRequestHeaders.Add("TE", "trailers");
            client.Timeout = TimeSpan.FromSeconds(10);

            cancel.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, ServerUrl + "/panel.php");

            bool hasWikisubtitlesuser = false, hasWikisubtitlespass = false, hasPhpsessid = false;
            foreach (Cookie cookie in cookies.GetAllCookies().Cast<Cookie>())
            {
                switch (cookie.Name)
                {
                    case "wikisubtitlesuser":
                        hasWikisubtitlesuser = true;
                        break;
                    case "wikisubtitlespass":
                        hasWikisubtitlespass = true;
                        break;
                    case "PHPSESSID":
                        hasPhpsessid = true;
                        break;
                }
            }

            if (!hasPhpsessid && (!hasWikisubtitlesuser || !hasWikisubtitlespass))
            {
                throw new AuthenticationException("No usable cookies");
            }

            request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            request.Headers.Add("Upgrade-Insecure-Requests", "1");
            request.Headers.Add("Sec-Fetch-Dest", "document");
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            request.Headers.Add("Sec-Fetch-Site", "none");
            request.Headers.Add("Sec-Fetch-User", "?1");

            var response = await client.SendAsync(request, cancel).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (!hasPhpsessid && response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
            {
                foreach (var cookie in SetCookieHeaderValue.ParseList((IList<string>?)setCookieHeaders))
                {
                    if (cookie.Name != "PHPSESSID")
                    {
                        continue;
                    }

                    _logger.LogWarning("Couldn't get {CookieName} cookie from Firefox, using own temporary one", cookie.Name);
                    cookies.Add(new Cookie() { Name = cookie.Name.ToString(), Value = cookie.Value.ToString(), Domain = $"www.{ServerDomain}", Path = "/" });
                    hasPhpsessid = true;
                    break;
                }
            }

            if (!hasPhpsessid)
            {
                throw new AuthenticationException($"No usable cookies - couldn't get a temporary session cookie from {ServerDomain}");
            }

            cancel.ThrowIfCancellationRequested();

            string content = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            using var context = BrowsingContext.New(AngleSharp.Configuration.Default);
            using var doc = await context.OpenAsync(req => req.Content(content), cancel).ConfigureAwait(false);
            var downloadsByUserToday = doc.QuerySelector("td > a[href*=\"mydownloads.php\"]").TextContent;
            var matches = Regex.Match(downloadsByUserToday, @"^(\d+) of (\d+)$");
            _downloadedSubtitleCount = int.Parse(matches.Groups[1].Value, CultureInfo.InvariantCulture);
            _totalAllowedDownloads = int.Parse(matches.Groups[2].Value, CultureInfo.InvariantCulture);
            _logger.LogInformation("Logged into Addic7ed, downloads: {Downloaded}/{Total}", _downloadedSubtitleCount, _totalAllowedDownloads);

            cancel.ThrowIfCancellationRequested();

            _httpClient = client;
            _httpClientExpungeTime = Environment.TickCount64 + (long)TimeSpan.FromHours(1).TotalMilliseconds;
            return client;
        }
        catch
        {
            client?.Dispose();

            if (_httpClientHandler is not null)
            {
                _httpClientHandler.Dispose();
                _httpClientHandler = null;
            }

            throw;
        }
    }

    private async Task<bool> ReadCachedShowIdsAsync(CancellationToken cancel = default)
    {
        if (!File.Exists(_showsCacheFile))
        {
            _logger.LogDebug("{ShowsCacheFile} doesn't exist", _showsCacheFile);
            return false;
        }

        if (File.GetLastWriteTime(_showsCacheFile) < DateTime.Now.AddDays(-7))
        {
            _logger.LogDebug("Shows cache file is older than a week");
            return false;
        }

        string jsonString = await File.ReadAllTextAsync(_showsCacheFile, cancel).ConfigureAwait(false);
        if (cancel.IsCancellationRequested)
        {
            return false;
        }

        var showIds = JsonSerializer.Deserialize<Dictionary<string, int>>(jsonString);
        if (cancel.IsCancellationRequested || showIds is not { Count: > 0 })
        {
            return false;
        }

        _logger.LogDebug("Shows read from {ShowsCacheFile}", _showsCacheFile);
        _showIds = showIds;
        return true;
    }

    private async Task<bool> GetShowIdsAsync(CancellationToken cancel = default)
    {
        _logger.LogDebug("Downloading list of shows from Addic7ed");
        HttpClient client = await GetHttpClientAsync(cancel).ConfigureAwait(false);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, ServerUrl + "/shows.php");

        request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.Add("Referer", "https://www.addic7ed.com/");
        request.Headers.Add("Upgrade-Insecure-Requests", "1");
        request.Headers.Add("Sec-Fetch-Dest", "document");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Sec-Fetch-User", "?1");

        HttpResponseMessage response = await client.SendAsync(request, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string content = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

        cancel.ThrowIfCancellationRequested();

        /*
            None of this seems performant at all. subliminal does it because
            "Assuming the site's markup is bad, and stripping it down to only contain what's needed."
        */
        var showCells = _showCellsRe.Matches(content)
                                    .Select(m => m.Value)
                                    .ToList();
        if (showCells.Count > 0)
        {
            content = $"<table>{string.Concat(showCells)}</table>";
        }

        using var context = BrowsingContext.New(AngleSharp.Configuration.Default);
        using var doc = await context.OpenAsync(req => req.Content(content), cancel).ConfigureAwait(false);
        var shows = doc.QuerySelectorAll("td > h3 > a[href^='/show/']");
        var showIds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var show in shows)
        {
            cancel.ThrowIfCancellationRequested();

            if (!int.TryParse(show.GetAttribute("href")?[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var showId))
            {
                continue;
            }

            var showClean = Sanitize(show.TextContent, _sanitizeCharacters);

            showIds[showClean] = showId;
            var match = _seriesYearRe.Match(show.TextContent);
            if (!match.Success || !match.Groups["year"].Success)
            {
                continue;
            }

            string series = Sanitize(match.Groups["series"].Value, _sanitizeCharacters);
            if (!showIds.ContainsKey(series))
            {
                showIds[series] = showId;
            }
        }

        if (showIds.Count == 0)
        {
            throw new ResourceNotFoundException("Addic7ed: No show IDs found!");
        }

        await File.WriteAllTextAsync(_showsCacheFile, JsonSerializer.Serialize(showIds), CancellationToken.None).ConfigureAwait(false);

        _showIds = showIds;
        return true;
    }

    public async Task<(int Id, string MatchedTitle)> GetShowIdAsync(string series, int? year = null, CancellationToken cancel = default)
    {
        var idsToLookFor = new[]
        {
            series,
            series.Replace(".", string.Empty, StringComparison.Ordinal),
            series.Replace(" & ", " and ", StringComparison.Ordinal),
            series.Replace(" and ", " & ", StringComparison.Ordinal),
            series.Replace("&", "and", StringComparison.Ordinal),
            series.Replace("and", "&", StringComparison.Ordinal),
        };
        if (_showIds is null || _showIds.Count == 0)
        {
            if (!await ReadCachedShowIdsAsync(cancel).ConfigureAwait(false))
            {
                await GetShowIdsAsync(cancel).ConfigureAwait(false);
            }
        }

        match_check:
        foreach (var serieses in idsToLookFor)
        {
            if (cancel.IsCancellationRequested)
            {
                break;
            }

            int showId;
            var seriesSanitized = Sanitize(serieses, _sanitizeCharacters);
            if (year is not null)
            {
                var key = $"{seriesSanitized} {year}";
                showId = _showIds.GetValueOrDefault(key);
                if (showId > 0)
                {
                    return (showId, key);
                }
            }

            showId = _showIds.GetValueOrDefault(seriesSanitized);
            if (showId > 0)
            {
                return (showId, seriesSanitized);
            }
        }

        if (!cancel.IsCancellationRequested && File.GetLastWriteTime(_showsCacheFile) < DateTime.Now.AddDays(-1))
        {
            _logger.LogInformation("Show {Series} not found and cache file older than a day, attempting redownload", series);
            if (await GetShowIdsAsync(cancel).ConfigureAwait(false))
            {
                goto match_check;
            }
        }

        return (0, string.Empty);
    }

    public async Task<List<SubtitleEntry>> QueryAsync(int showId, int? season, CancellationToken cancel = default)
    {
        if (season is null)
        {
            _logger.LogDebug("QueryAsync: season == null");
            return new List<SubtitleEntry>();
        }

        string cacheKey = $"{showId}#{season}";
        if (_showMemoryCache.TryGetValue(cacheKey, out List<SubtitleEntry> subtitles))
        {
            _logger.LogDebug("Using cached result for {CacheKey}", cacheKey);
            return subtitles;
        }

        HttpClient client = await GetHttpClientAsync(cancel).ConfigureAwait(false);
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl}/ajax_loadShow.php?show={showId}&season={season}&langs=|1|&hd=0&hi=0"); // should be English-only

        request.Headers.Add("Accept", "text/javascript, text/html, application/xml, text/xml, */*");
        request.Headers.Add("Referer", "https://www.addic7ed.com/");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Add("Sec-Fetch-Dest", "empty");
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");

        if (cancel.IsCancellationRequested)
        {
            return subtitles;
        }

        HttpResponseMessage response = await client.SendAsync(request, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            _logger.LogError("Server is busy");
            return subtitles;
        }

        string content = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        if (cancel.IsCancellationRequested)
        {
            return subtitles;
        }

        using var context = BrowsingContext.New(AngleSharp.Configuration.Default);
        using var doc = await context.OpenAsync(req => req.Content(content), cancel).ConfigureAwait(false);

        subtitles = new List<SubtitleEntry>();
        foreach (var row in doc.QuerySelectorAll("tr.epeven"))
        {
            if (cancel.IsCancellationRequested)
            {
                return subtitles;
            }

            var cells = row.QuerySelectorAll("td");

            var status = cells[5].TextContent;
            if (status.Contains('%', StringComparison.Ordinal))
            {
                continue;
            }

            var dlSelector = cells[9].QuerySelector("a");
            if (dlSelector is null)
            {
                continue;
            }

            var subtitleSeason = int.Parse(cells[0].TextContent, CultureInfo.InvariantCulture);
            var subtitleEpisode = int.Parse(cells[1].TextContent, CultureInfo.InvariantCulture);
            var title = cells[2].TextContent;
            // var pageLink = cells[2].QuerySelector("a")?.GetAttribute("href");
            var language = cells[3].TextContent;
            var version = cells[4].TextContent;
            var hearingImpaired = !string.IsNullOrEmpty(cells[6].TextContent);
            var downloadLinkFragment = dlSelector.GetAttribute("href");

            if (downloadLinkFragment is null)
            {
                continue;
            }

            subtitles.Add(new SubtitleEntry
            {
                Title = title,
                Language = language,
                Version = version,
                Season = subtitleSeason,
                Episode = subtitleEpisode,
                HearingImpaired = hearingImpaired,
                // PageLink = pageLink,
                DownloadLinkFragment = downloadLinkFragment,
            });
        }

        if (subtitles.Count > 0)
        {
            using var cacheEntry = _showMemoryCache.CreateEntry(cacheKey);
            cacheEntry.Value = subtitles;
            cacheEntry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            _logger.LogDebug("Caching {CacheKey} results for one hour", cacheKey);
        }

        return subtitles;
    }

    public async Task<byte[]> DownloadSubtitleAsync(string subtitleUrl, CancellationToken cancel = default)
    {
        if (_downloadedSubtitleCount >= _totalAllowedDownloads)
        {
            throw new RateLimitExceededException($"Addic7ed: Downloads per day exceeded ({_totalAllowedDownloads})");
        }

        HttpClient client = await GetHttpClientAsync(cancel).ConfigureAwait(false);
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, subtitleUrl);

        request.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.Add("Referer", ServerUrl);
        request.Headers.Add("Upgrade-Insecure-Requests", "1");
        request.Headers.Add("Sec-Fetch-Dest", "document");
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Sec-Fetch-User", "?1");

        cancel.ThrowIfCancellationRequested();

        HttpResponseMessage response = await client.SendAsync(request, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            throw new HttpRequestException("Too many requests");
        }

        if (response.Content is null)
        {
            throw new InvalidDataException($"Unable to download subtitle: empty response body for {subtitleUrl}");
        }

        if (response.Content.Headers.ContentType?.MediaType?.StartsWith("text/html", StringComparison.Ordinal) == true)
        {
            throw new RateLimitExceededException($"Addic7ed: Downloads per day exceeded ({_totalAllowedDownloads})");
        }

        _downloadedSubtitleCount++;
        cancel.ThrowIfCancellationRequested();
        return await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_httpClient is not null)
        {
            _httpClient.Dispose();
            _httpClient = null;
        }

        if (_httpClientHandler is not null)
        {
            _httpClientHandler.Dispose();
            _httpClientHandler = null;
        }

        _showMemoryCache.Dispose();
    }

    private static string Sanitize(string str, string[]? defaultCharacters = null)
    {
        if (string.IsNullOrWhiteSpace(str))
        {
            return str;
        }

        string[] characters = defaultCharacters ?? new[] { "-", ":", "(", ")", "." };
        if (characters.Length > 0)
        {
            str = Regex.Replace(str, $"[{Regex.Escape(string.Concat(characters))}]", " ");
        }

        characters = new[] { "'", "´", "`", "’" };
        str = Regex.Replace(str, $"[{Regex.Escape(string.Concat(characters))}]", string.Empty);

        str = Regex.Replace(str, @"\s+", " ");

        return str.Trim().ToLower(CultureInfo.InvariantCulture);
    }
}
