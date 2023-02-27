# Unofficial Addic7ed subtitle downloader plugin for Jellyfin

Written for personal use, so lots of (intentional) limitations/bugs:

* Downloads English subtitles only

* Only supports episodes and *not* movies

* There's no configuration to speak of. To log in, you must do so on the Addic7ed site itself with a system-installed version of Firefox on your main profile with no Container. This furthermore means:

    * this won't work on anything other than Windows
    
    * Jellyfin running as a service isn't supported - this assumes Jellyfin is running as the same user as the Firefox instance to pull cookies from

* Subtitles are sorted in the following order: matching release group, matching source (HDTV etc.) and then hearing impaired subtitles are prioritised

    * For automatic downloads, it's quite likely you'll end up with a mismatching subtitle downloaded. The odds of the file's release group being listed on Addic7ed's subtitle page varies

* The order of the Request headers is... unique. Detecting requests from this addon, SSL fingerprinting aside, isn't hard

    * `Accept-Language` includes `en-GB`

* Requests time out after 10 seconds. Addic7ed's servers tend to be busy and you might need to repeat your request for it to go through. 10 seconds is already too long IMO so I won't raise it

* The big list of shows is cached for a week (but requested once a day if a show couldn't be found), a request for a show's season is stored for an hour

    * The HttpClient is meant to be torn down after an hour, this should (hopefully) trigger a recheck on the cap placed on downloading subtitles each time

* The code is very haphazardly thrown together

## Credits

* Addic7ed for spending their time creating quality subtitles for TV shows. Why go anywhere else but the source?

* The logic for handling Addic7ed and mapping show names etc. is pretty much from https://github.com/Diaoul/subliminal (and [Bazarr's fork](https://github.com/morpheus65535/bazarr/blob/master/libs/subliminal_patch/))

* The regexes for extracting the release name out of a given filename come from https://github.com/Sonarr/Sonarr/tree/develop/src/NzbDrone.Core/Parser

* The included Base62 implementation is taken from https://github.com/JoyMoe/Base62.Net

* The code to read Firefox's cookies was converted from https://github.com/borisbabic/browser_cookie3

    * [PeanutButter.INI](https://github.com/fluffynuts/PeanutButter/) is used to read Firefox's profiles.ini
    
    * [SQLitePCL](https://github.com/jellyfin/SQLitePCL.pretty.netstandard) to read cookies.sqlite

    * [Lej77's code](https://github.com/piroor/treestyletab/issues/1678#issuecomment-351411816) is used to decompress Fx's jsonlz4 files

* [AngleSharp, great HTML parser](https://anglesharp.github.io/)
