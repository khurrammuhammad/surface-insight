# Third-party data and packages

**IP geolocation.** "IP to City Lite" by [DB-IP.com](https://db-ip.com), licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). It is downloaded at build time (`scripts/fetch-geo.ps1`), used unmodified and offline, and bundled in the release zips as `Assets\dbip-city-lite.mmdb`. Locations are approximate and describe where an address is registered or announced (often a CDN or cloud region), not who uses it.

**Map outlines.** [Natural Earth](https://www.naturalearthdata.com) land and boundary data (public domain), in `src/SurfaceInsight/Assets`.

**NuGet packages.** Microsoft.Data.Sqlite (MIT), Microsoft.Diagnostics.Tracing.TraceEvent (MIT), Microsoft.Windows.EventTracing.Processing.All (Microsoft software license terms), System.Management and System.Diagnostics.PerformanceCounter (MIT), MaxMind.Db (Apache 2.0).
