# Unofficial Addic7ed subtitle downloader plugin for Jellyfin

Written for personal use, so lots of limitations/bugs (some intentional):

* Downloads English subtitles only

* Supports episodes only - *not* movies

* Will only operate on manual requests

* There's no configuration to speak of. To log in, you must use a system-installed version of Firefox (main profile, no Container) to do so on the Addic7ed site itself. Because of this:

    * this won't work on anything other than Windows (the cookie extraction code isn't really cross-platform)

    * Jellyfin running as a service isn't supported - the plugin assumes Jellyfin is running as the same user as the Firefox instance to pull cookies from

* Subtitles are sorted in the following order: matching release group, matching source (HDTV etc.) and then hearing-impaired subtitles are prioritised

* The order of the Request headers is... unique. Detecting requests from this addon, SSL fingerprinting aside, isn't hard

    * `Accept-Language` includes `en-GB`

* Requests time out after 15 seconds, which is already too long, but unfortunately also too short. As Addic7ed's servers tend to be busy, you might need to repeat your request for it to go through

* The big list of shows is cached for a week (but requested once a day if a show couldn't be found), and a request for a show's season is stored for an hour

    * The HttpClient is meant to be torn down after an hour, this should get fresh cookies from your browser and cause a re-check of the download cap

* The code is very haphazardly thrown together

## Installation

Binaries aren't provided, 'cause this is a WIP, and I don't want my bad code to cause more than one person to hammer Addic7ed's servers.

Nevertheless, if you can build it yourself, do the following to install it:

1. Make a Jellyfin.Plugin.Addic7ed folder in C:\ProgramData\Jellyfin\Server\plugins\

2. Copy q12.JellyfinPlugin.Addic7ed.dll, PeanutButter.INI.dll and AngleSharp.dll into it

## Credits

* Addic7ed for spending their time creating quality subtitles for TV shows. Why go anywhere else but the source?

* The logic for handling Addic7ed and mapping show names etc. is pretty much from https://github.com/Diaoul/subliminal (and [Bazarr's fork](https://github.com/morpheus65535/bazarr/blob/master/libs/subliminal_patch/))

* The regexes for extracting the release name out of a given filename come from https://github.com/Sonarr/Sonarr/tree/develop/src/NzbDrone.Core/Parser

* The code to read Firefox's cookies was converted from https://github.com/borisbabic/browser_cookie3

    * [PeanutButter.INI](https://github.com/fluffynuts/PeanutButter/) is used to read Firefox's profiles.ini

    * [SQLitePCL](https://github.com/jellyfin/SQLitePCL.pretty.netstandard) to read cookies.sqlite

    * [Lej77's code](https://github.com/piroor/treestyletab/issues/1678#issuecomment-351411816) is used to decompress Fx's jsonlz4 files

* [AngleSharp, great HTML parser](https://anglesharp.github.io/)
