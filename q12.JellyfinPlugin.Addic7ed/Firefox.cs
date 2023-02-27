using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using PeanutButter.INI;
using SQLitePCL.pretty;

namespace q12.JellyfinPlugin.Addic7ed;

public static class Firefox
{
    private static string? _exePath;
    private static string? _profilePath;

    public static string? ExePath
    {
        get
        {
            if (_exePath != null)
            {
                return _exePath;
            }

            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var subKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe"))
            {
                _exePath = subKey.GetValue(null, null) as string;
            }

            return _exePath;
        }
    }

    public static string? ProfilePath
    {
        // Derived from https://github.com/borisbabic/browser_cookie3
        get
        {
            if (_profilePath != null)
            {
                return _profilePath;
            }

            var fxAppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox");
            var fxProfileIni = Path.Combine(fxAppDataDir, "profiles.ini");
            if (!File.Exists(fxProfileIni))
            {
                return null;
            }

            var parser = new INIFile(fxProfileIni);
            string? profilePath = null;
            foreach (var sectionName in parser.AllSections)
            {
                if (!sectionName.StartsWith("Install", StringComparison.Ordinal))
                {
                    continue;
                }

                profilePath = parser[sectionName]["Default"];
                if (profilePath != null)
                {
                    break;
                }
            }

            if (profilePath == null)
            {
                return null;
            }

            foreach (var sectionName in parser.AllSections)
            {
                if (!sectionName.StartsWith("Profile", StringComparison.Ordinal) || parser[sectionName]["Path"] != profilePath)
                {
                    continue;
                }

                var isAbsolute = parser[sectionName]["IsRelative"] == "0";
                if (!isAbsolute)
                {
                    profilePath = Path.Combine(fxAppDataDir, profilePath);
                }

                break;
            }

            _profilePath = Path.GetFullPath(profilePath);
            return _profilePath;
        }
    }

    public static string DefaultUa
    {
        get
        {
            var productVersion = FileVersionInfo.GetVersionInfo(ExePath).ProductVersion;
            var rv = productVersion;

            if ((int)double.Parse(productVersion, CultureInfo.InvariantCulture) is > 109 and < 120) // https://news.slashdot.org/story/23/01/01/2037227/firefox-changes-its-user-agent---because-of-internet-explorer-11
            {
                rv = "109.0";
            }

            return $"Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{rv}) Gecko/20100101 Firefox/{productVersion}";
        }
    }

    private sealed class FxSessionStore
    {
        [JsonPropertyName("cookies")]
        public FxSessionStoreCookies[]? Cookies { get; set; }
    }

    private sealed class FxSessionStoreCookies
    {
        [JsonPropertyName("host")]
        public string Host { get; set; }

        [JsonPropertyName("value")]
        public string Value { get; set; }

        [JsonPropertyName("path")]
        public string Path { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("secure")]
        public bool? Secure { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonPropertyName("httponly")]
        public bool? Httponly { get; set; }
    }

    private static byte[] Convert_JSONLZ4_To_JSON(byte[] jsonlz4)
    {
        // Lej77: https://github.com/piroor/treestyletab/issues/1678#issuecomment-351411816
        byte[] output;
        var uncompressedSize = jsonlz4.Length * 3; // size estimate for uncompressed data!

        // Decode whole file.
        do
        {
            output = new byte[uncompressedSize];
            try
            {
                uncompressedSize = DecodeLz4Block(jsonlz4, output, 8 + 4); // skip 8 byte magic number + 4 byte data size field
            }
            catch (IndexOutOfRangeException)
            {
                uncompressedSize *= 2;
                if (uncompressedSize > 33554432) /* Fail if we're allocating anything larger than 32MB */
                {
                    throw;
                }
            } // if there's more data than our output estimate, create a bigger output array and retry
        } while (uncompressedSize > output.Length);

        var trimmedOutput = output.ToList(); // remove excess bytes
        trimmedOutput.RemoveRange(uncompressedSize, output.Length - uncompressedSize);
        return trimmedOutput.ToArray();
    }

    private static int DecodeLz4Block(byte[] input, byte[] output, int sIdx = 0)
    {
        return DecodeLz4Block(input, output, sIdx, input.Length);
    }

    private static int DecodeLz4Block(byte[] input, byte[] output, int sIdx, int eIdx)
    {
        // This method's code was taken from node-lz4 by Pierre Curto. MIT license.
        // CHANGES: Added ; to all lines. Reformated one-liners. Removed n = eIdx. Fixed eIdx skipping end bytes if sIdx != 0.

        // Process each sequence in the incoming data
        var j = 0;
        for (var i = sIdx; i < eIdx;)
        {
            var token = input[i++];

            // Literals
            var literals_length = token >> 4;
            if (literals_length > 0)
            {
                // length of literals
                var l = literals_length + 240;
                while (l == 255)
                {
                    l = input[i++];
                    literals_length += l;
                }

                // Copy the literals
                var end = i + literals_length;
                while (i < end)
                {
                    output[j++] = input[i++];
                }

                // End of buffer?
                if (i == eIdx)
                {
                    return j;
                }
            }

            {
                // Match copy
                // 2 bytes offset (little endian)
                var offset = input[i++] | (input[i++] << 8);

                // 0 is an invalid offset value
                if (offset == 0 || offset > j)
                {
                    return -(i - 2);
                }

                // length of match copy
                var match_length = token & 0xf;
                var l = match_length + 240;
                while (l == 255)
                {
                    l = input[i++];
                    match_length += l;
                }

                // Copy the match
                var pos = j - offset; // position of the match copy in the current output
                var end = j + match_length + 4; // minmatch = 4
                while (j < end)
                {
                    output[j++] = output[pos++];
                }
            }
        }

        return j;
    }

    public static Task<CookieContainer> GetCookiesForDomainAsync(string domain, CancellationToken cancel = default)
    {
        return Task.Run(
            async () =>
        {
            var dbPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                var ret = new CookieContainer();

                File.Copy(Path.Combine(ProfilePath, "cookies.sqlite"), dbPath, true);
                using (var db = SQLite3.Open(dbPath, ConnectionFlags.ReadOnly, null))
                {
                    foreach (var row in db.Query($"SELECT name, value, host, path, expiry, isSecure, isHttpOnly FROM moz_cookies WHERE host LIKE '%{domain}%'"))
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            return ret;
                        }

                        ret.Add(new Cookie()
                        {
                            Name = row[0].ToString(),
                            Value = row[1].ToString(),
                            Domain = row[2].ToString(),
                            Path = row[3].ToString(),
                            Expires = DateTimeOffset.FromUnixTimeSeconds(row[4].ToInt64()).UtcDateTime,
                            Secure = row[5].ToBool(),
                            HttpOnly = row[6].ToBool(),
                        });
                    }
                }

                File.Copy(Path.Combine(ProfilePath, "sessionstore-backups", "recovery.jsonlz4"), dbPath, true);
                var sessionData = JsonSerializer.Deserialize<FxSessionStore>(Convert_JSONLZ4_To_JSON(await File.ReadAllBytesAsync(dbPath, cancel).ConfigureAwait(false)));
                if (sessionData != null)
                {
                    for (var i = 0; i < sessionData.Cookies?.Length; ++i)
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            return ret;
                        }

                        var cookie = sessionData.Cookies[i];
                        if (!cookie.Host.EndsWith(domain, StringComparison.Ordinal)) // 🤷‍♂️
                        {
                            continue;
                        }

                        ret.Add(new Cookie()
                        {
                            Name = cookie.Name,
                            Value = cookie.Value,
                            Domain = cookie.Host,
                            Path = cookie.Path,
                            Secure = cookie.Secure ?? false,
                            HttpOnly = cookie.Httponly ?? false,
                        });
                    }
                }

                return ret;
            }
            finally
            {
                File.Delete(dbPath);
            }
        }, cancel);
    }
}
