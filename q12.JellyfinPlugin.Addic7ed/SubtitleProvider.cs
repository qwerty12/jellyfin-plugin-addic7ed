using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace q12.JellyfinPlugin.Addic7ed;

public class SubtitleProvider : ISubtitleProvider, IDisposable
{
    private const string SubtitleFormat = "srt";
    private readonly ILogger<SubtitleProvider> _logger;
    private readonly Downloader _downloader;
    private bool _isDisposed;

    public SubtitleProvider(ILogger<SubtitleProvider> logger)
    {
        _logger = logger;
        _downloader = new Downloader(logger, Plugin.ApplicationPaths.CachePath);
    }

    public string Name => "Addic7ed";

    public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Episode };

    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        if (request.IsAutomated || string.IsNullOrWhiteSpace(request.SeriesName) || !request.ParentIndexNumber.HasValue || !request.IndexNumber.HasValue)
        {
            return Enumerable.Empty<RemoteSubtitleInfo>();
        }

        (int id, string matchedTitle) = await _downloader.GetShowIdAsync(request.SeriesName, request.ProductionYear, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested || id == 0)
        {
            return Enumerable.Empty<RemoteSubtitleInfo>();
        }

        matchedTitle = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(matchedTitle.ToLowerInvariant());

        string basename = Path.GetFileName(request.MediaPath);
        string rlsgrp = SonarrParsing.ParseReleaseGroup(basename);
        string source = SonarrParsing.ParseQualityName(basename);
        _logger.LogDebug("Searching for {MatchedTitle} (matched ReleaseGroup: {ReleaseGroup}, matched Source: {Source})", matchedTitle, rlsgrp, source);

        static bool SubstringChecker(string needle, string haystack) =>
            !string.IsNullOrEmpty(needle) && haystack.ToLower(CultureInfo.InvariantCulture).Contains(needle.ToLower(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return (from sub in await _downloader.QueryAsync(id, request.ParentIndexNumber, cancellationToken).ConfigureAwait(false)
            where sub.Episode == request.IndexNumber
            orderby SubstringChecker(rlsgrp, sub.Version) ? 0 : 1,
                    SubstringChecker(source, sub.Version) ? 0 : 1,
                    sub.HearingImpaired descending
            select new RemoteSubtitleInfo()
            {
                ThreeLetterISOLanguageName = "eng",
                Id = sub.DownloadLinkFragment.Replace("/", ",", StringComparison.Ordinal),
                ProviderName = Name,
                Name = $"{matchedTitle} - {sub.Season:00}x{sub.Episode:00} - {sub.Title}: {sub.Version} ({(sub.HearingImpaired ? "HI " : string.Empty)}{sub.Language})",
                Format = SubtitleFormat,
            }).ToList();
    }

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        string url = Downloader.ServerUrl + id.Replace(",", "/", StringComparison.Ordinal);
        _logger.LogDebug("Attempting to download {SubtitleUrl}", url);
        var stream = new MemoryStream();
        await _downloader.DownloadSubtitleAsync(url, stream, cancellationToken).ConfigureAwait(false);
        return new SubtitleResponse()
        {
            Language = "en", Format = SubtitleFormat, IsForced = false, Stream = stream,
        };
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        if (disposing)
        {
            _downloader.Dispose();
        }

        _isDisposed = true;
    }
}
