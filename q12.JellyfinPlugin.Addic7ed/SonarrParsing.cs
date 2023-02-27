// https://github.com/Sonarr/Sonarr/tree/develop/src/NzbDrone.Core/Parser

using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace q12.JellyfinPlugin.Addic7ed;

public class RegexReplace
{
    private readonly Regex _regex;
    private readonly string _replacementFormat;

    public RegexReplace(string pattern, string replacement, RegexOptions regexOptions)
    {
        _regex = new Regex(pattern, regexOptions);
        _replacementFormat = replacement;
    }

    public string Replace(string input)
    {
        return _regex.Replace(input, _replacementFormat);
    }

    public override string ToString()
    {
        return _regex.ToString();
    }
}

public static class SonarrParsing
{
    private static readonly RegexReplace WebsitePrefixRegex = new(@"^\[\s*[-a-z]+(\.[a-z]+)+\s*\][- ]*|^www\.[a-z]+\.(?:com|net|org)[ -]*",
        string.Empty,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly RegexReplace CleanTorrentSuffixRegex = new(@"\[(?:ettv|rartv|rarbg|cttv|publichd)\]$",
        string.Empty,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly RegexReplace CleanReleaseGroupRegex = new(@"^(.*?[-._ ](S\d+E\d+)[-._ ])|(-(RP|1|NZBGeek|Obfuscated|Scrambled|sample|Pre|postbot|xpost|Rakuv[a-z0-9]*|WhiteRev|BUYMORE|AsRequested|AlternativeToRequested|GEROV|Z0iDS3N|Chamele0n|4P|4Planet|AlteZachen|RePACKPOST))+$",
        string.Empty,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExceptionReleaseGroupRegex = new(@"(?<releasegroup>(Silence|afm72|Panda|Ghost|MONOLITH|Tigole|Joy|ImE|UTR|t3nzin|Anime Time|Project Angel|Hakata Ramen|HONE)(?=\]|\)))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReleaseGroupRegex = new(@"-(?<releasegroup>[a-z0-9]+(?<part2>-[a-z0-9]+)?(?!.+?(?:480p|576p|720p|1080p|2160p)))(?<!(?:WEB-DL|Blu-Ray|480p|576p|720p|1080p|2160p|DTS-HD|DTS-X|DTS-MA|DTS-ES|-ES|-EN|-CAT|[ ._]\d{4}-\d{2}|-\d{2})(?:\k<part2>)?)(?:\b|[-._ ]|$)|[-._ ]\[(?<releasegroup>[a-z0-9]+)\]$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InvalidReleaseGroupRegex = new(@"^([se]\d+|[0-9a-f]{8})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FileExtensionRegex = new(@"\.[a-z0-9]{2,4}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SourceRegex = new(@"\b(?:
                                                                (?<bluray>BluRay|Blu-Ray|HD-?DVD|BDMux|BD(?!$))|
                                                                (?<webdl>WEB[-_. ]DL(?:mux)?|WEBDL|AmazonHD|iTunesHD|MaxdomeHD|NetflixU?HD|WebHD|[. ]WEB[. ](?:[xh][ .]?26[45]|DDP?5[. ]1)|[. ](?-i:WEB)$|(?:720|1080|2160)p[-. ]WEB[-. ]|[-. ]WEB[-. ](?:720|1080|2160)p|\b\s\/\sWEB\s\/\s\b|(?:AMZN|NF|DP)[. -]WEB[. -](?!Rip))|
                                                                (?<webrip>WebRip|Web-Rip|WEBMux)|
                                                                (?<hdtv>HDTV)|
                                                                (?<bdrip>BDRip|BDLight)|
                                                                (?<brrip>BRRip)|
                                                                (?<dvd>DVD|DVDRip|NTSC|PAL|xvidvd)|
                                                                (?<dsr>WS[-_. ]DSR|DSR)|
                                                                (?<pdtv>PDTV)|
                                                                (?<sdtv>SDTV)|
                                                                (?<tvrip>TVRip)
                                                                )(?:\b|$|[ .])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex OtherSourceRegex = new(@"(?<hdtv>HD[-_. ]TV)|(?<sdtv>SD[-_. ]TV)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string ParseReleaseGroup(string title)
    {
        title = title.Trim();
        title = RemoveFileExtension(title);

        title = WebsitePrefixRegex.Replace(title);
        title = CleanTorrentSuffixRegex.Replace(title);

        title = CleanReleaseGroupRegex.Replace(title);

        var exceptionReleaseGroupRegex = ExceptionReleaseGroupRegex.Matches(title);

        if (exceptionReleaseGroupRegex.Count != 0)
        {
            return exceptionReleaseGroupRegex.OfType<Match>().Last().Groups["releasegroup"].Value;
        }

        /*var exceptionExactMatch = ExceptionReleaseGroupRegexExact.Matches(title);

        if (exceptionExactMatch.Count != 0)
        {
            return exceptionExactMatch.OfType<Match>().Last().Groups["releasegroup"].Value;
        }*/

        var matches = ReleaseGroupRegex.Matches(title);

        if (matches.Count != 0)
        {
            var group = matches.OfType<Match>().Last().Groups["releasegroup"].Value;
            if (int.TryParse(group, out int groupIsNumeric))
            {
                return string.Empty;
            }

            if (InvalidReleaseGroupRegex.IsMatch(group))
            {
                return string.Empty;
            }

            return group;
        }

        return string.Empty;
    }

    public static string RemoveFileExtension(string title)
    {
        return FileExtensionRegex.Replace(title, string.Empty);
    }

    public static string ParseQualityName(string name)
    {
        var normalizedName = name.Replace('_', ' ').Trim();

        var sourceMatches = SourceRegex.Matches(normalizedName);
        var sourceMatch = sourceMatches.OfType<Match>().LastOrDefault();

        if (sourceMatch is { Success: true })
        {
            for (var i = 1; i < sourceMatch.Groups.Count; ++i)
            {
                var group = sourceMatch.Groups[i];
                if (group.Success)
                {
                    var ret = SourceRegex.GroupNameFromNumber(i);
                    if (string.Equals(ret, "webdl", StringComparison.Ordinal))
                    {
                        ret = "web-dl"; // On average, more results for this on Addic7ed
                    }

                    return ret;
                }
            }
        }

        var otherSourceWatch = OtherSourceRegex.Match(name);
        if (otherSourceWatch.Success)
        {
            for (var i = 1; i < otherSourceWatch.Groups.Count; ++i)
            {
                var group = otherSourceWatch.Groups[i];
                if (group.Success)
                {
                    return OtherSourceRegex.GroupNameFromNumber(i);
                }
            }
        }

        return string.Empty;
    }
}
