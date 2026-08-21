// SCO LIDEX - Geofabrik/OpenStreetMap terrain-map and PolyVeg source processing.
// Copyright (C) Scott Brunner, Beast of Burden
// Part of the SCO LIDEX Terrain Builder application.
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MaxRev.Gdal.Core;
using OSGeo.GDAL;
using OSGeo.OGR;
using OSGeo.OSR;
using GdalDataset = OSGeo.GDAL.Dataset;

namespace ORterr;

internal static partial class Program
{
    private static int MapImageSize = 2048;
    private const int MapTileParallelism = 2;
    private const double TreeRowWidthMetres = 10.0;
    private const int NaturalWoodRed = 141;
    private const int NaturalWoodGreen = 196;
    private const int NaturalWoodBlue = 108;
    private const string GeofabrikIndexUrl = "https://download.geofabrik.de/index-v1.json";
    // Relation-safe route cut written during the first regional PBF scan. Maps
    // and PolyVeg reuse this compact, spatially indexed source until either the
    // regional source stamp or normal terrain footprint changes.
    private const string RouteOsmWorkingCacheFileName = "route-osm-working.gpkg";
    private const string RouteOsmWorkingManifestFileName = "route-osm-working.json";
    private static readonly string[] RouteOsmWorkingFields =
    [
        "osm_type", "osm_id", "osm_way_id", "name", "natural", "landuse", "water", "waterway",
        "wetland", "leaf_type", "leaf_cycle", "wood", "species", "building", "amenity",
        "leisure", "aeroway", "military", "man_made", "highway", "railway", "service",
        "surface", "tracktype", "bridge", "tunnel", "layer", "width", "lanes", "gauge",
        "tourism", "shop", "other_tags",
    ];

    private sealed record GeofabrikRegion(
        string Id,
        string Name,
        string PbfUrl,
        long SizeBytes,
        DateTimeOffset? Modified,
        double MinLon,
        double MinLat,
        double MaxLon,
        double MaxLat);
    private sealed record GeofabrikResolution(
        GeofabrikRegion Region,
        bool RemoteIndexAvailable,
        bool RemoteExtractAvailable,
        bool CachedExtractAvailable,
        string? CachedExtractPath,
        string Detail)
    {
        // A cached index is sufficient to identify the regional extract. Once
        // that extract answers remotely, Run can download it even if refreshing
        // the small live index failed during Scan.
        internal bool CanRun => RemoteExtractAvailable || CachedExtractAvailable;
        internal bool CacheOnly => !RemoteExtractAvailable && CachedExtractAvailable;
    }

    private sealed record MapSourceAvailability(bool CanRun, bool CacheOnly, bool HasWarning, string Detail);
    private sealed record GeofabrikExtract(string Path, bool Downloaded);
    private sealed record GeofabrikSourceSet(IReadOnlyList<GeofabrikResolution> Sources);
    private sealed record RouteOsmWorkingManifest(
        int SchemaVersion,
        string SourcePath,
        long SourceSizeBytes,
        DateTime SourceModifiedUtc,
        string TerrainTileFingerprint,
        long CacheSizeBytes,
        IReadOnlyList<RouteOsmWorkingSourceStamp>? Sources = null);
    private sealed record RouteOsmWorkingSourceStamp(
        string Path,
        long SizeBytes,
        DateTime ModifiedUtc);

    private static async Task RunMapTileProbeAsync(string[] args, CancellationToken cancellationToken)
    {
        string routeDir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "";
        if (!RouteLayout.TryLoad(routeDir, out RouteLayout? route, out string error) || route is null)
        {
            Console.WriteLine(error);
            return;
        }
        GeoTileMapper mapper = GeoTileMapper.TryCreate(route)
            ?? throw new InvalidOperationException("route geography is unavailable");
        bool marker = args.Contains("--marker-coverage", StringComparer.OrdinalIgnoreCase);
        bool track = args.Contains("--track-database-coverage", StringComparer.OrdinalIgnoreCase);
        bool kml = args.Contains("--kml-coverage", StringComparer.OrdinalIgnoreCase);
        bool text = args.Contains("--text-file-coverage", StringComparer.OrdinalIgnoreCase);
        int radius = ParseIntOption(args, "--terrain-radius", 0);
        IReadOnlyList<TerrainTile> tiles = GetRouteTileProcessingList(route, marker, track, kml, text, radius);
        Console.WriteLine($"Map probe: {tiles.Count:N0} selected existing normal terrain tile(s).");
        TerrainTile representative = tiles.FirstOrDefault(t => t.WorldTile is not null)
            ?? throw new InvalidOperationException("selection has no positioned terrain tile");
        ValidateMapProjectionAlignment(mapper, representative.WorldTile!);
        string[] outputNames = tiles.Select(t => GetTsreMapCacheFileName(t.WorldTile!)).ToArray();
        if (outputNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputNames.Length)
        {
            throw new InvalidOperationException("selected terrain tiles produce duplicate TSRE F3 map-cache names");
        }
        Console.WriteLine($"Map output probe: PASSED for {tiles.Count:N0} TSRE F3 terrain_maps PNG file(s); terrain materials and UVs will remain unchanged.");
        bool ignoreWorkingCache = args.Contains("--ignore-working-cache", StringComparer.OrdinalIgnoreCase);
        MapSourceAvailability source = await ScanMapTileSourceAsync(
            route, mapper, tiles, cancellationToken, ignoreWorkingCache);
        if (!source.CanRun)
        {
            throw new InvalidOperationException(source.Detail);
        }
    }

    private static async Task<MapSourceAvailability> ScanMapTileSourceAsync(
        RouteLayout route,
        GeoTileMapper mapper,
        IReadOnlyList<TerrainTile> selectedTiles,
        CancellationToken cancellationToken,
        bool ignoreWorkingCache = false)
    {
        try
        {
            ConfigureOsmRuntime();
            using OSGeo.OGR.Driver? osmDriver = Ogr.GetDriverByName("OSM");
            if (osmDriver is null)
            {
                return new MapSourceAvailability(false, false, true, "bundled GDAL OSM/PBF vector driver is unavailable");
            }

            using HttpClient client = CreateMapHttpClient(TimeSpan.FromSeconds(45));
            IReadOnlyList<(double Lon, double Lat)> coveragePoints = GetMapAndRouteCoveragePoints(mapper, selectedTiles, route.WorldTiles);
            GeofabrikResolution resolution = await ResolveGeofabrikRegionAsync(
                client, route.RouteDir, mapper, coveragePoints, cacheOnly: false, cancellationToken);
            GeofabrikRegion region = resolution.Region;
            Console.WriteLine($"Map tiles: enabled; {MapImageSize}x{MapImageSize} image per normal 2048 m terrain tile.");
            Console.WriteLine($"Map source: Geofabrik {region.Name} ({region.Id}), anonymous regional OpenStreetMap PBF.");
            Console.WriteLine(resolution.CachedExtractAvailable
                ? $"Map extract: existing route cache selected ({resolution.CachedExtractPath}); remote download not needed."
                : resolution.RemoteExtractAvailable
                    ? $"Map extract: remote available{FormatModified(region.Modified)}."
                    : $"Map extract: FAILED ({resolution.Detail}).");
            Console.WriteLine("Map alignment: each OSM vertex uses the same corrected terrain projection and tile-local meter frame as DEM sampling.");
            Console.WriteLine("Map output: TSRE F3 terrain_maps/<tile-hash>.png cache; terrain .t materials and UVs remain unchanged.");
            Console.WriteLine("Map runtime: bundled GDAL OSM/PBF driver available; no external route-editor or MSTS utility required.");
            return new MapSourceAvailability(
                resolution.CanRun,
                resolution.CacheOnly,
                HasMapSourceWarning(resolution),
                resolution.Detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException)
        {
            Console.WriteLine($"Map source: FAILED ({ex.Message}).");
            return new MapSourceAvailability(false, false, true, ex.Message);
        }
    }

    private static bool HasMapSourceWarning(GeofabrikResolution resolution)
    {
        return !resolution.RemoteIndexAvailable ||
            (!resolution.CachedExtractAvailable && !resolution.RemoteExtractAvailable);
    }

    internal static void RunMapSourceWarningProbe()
    {
        GeofabrikRegion region = new(
            "probe",
            "Probe Region",
            "https://example.invalid/probe.osm.pbf",
            1,
            null,
            -1,
            -1,
            1,
            1);

        GeofabrikResolution healthyCache = new(
            region, true, false, true, "probe.osm.pbf", "validated cache");
        GeofabrikResolution offlineCache = new(
            region, false, false, true, "probe.osm.pbf", "cached fallback");
        GeofabrikResolution healthyRemote = new(
            region, true, true, false, null, "remote available");
        GeofabrikResolution unavailable = new(
            region, true, false, false, null, "unavailable");

        if (HasMapSourceWarning(healthyCache) ||
            !HasMapSourceWarning(offlineCache) ||
            HasMapSourceWarning(healthyRemote) ||
            !HasMapSourceWarning(unavailable))
        {
            throw new InvalidOperationException("map-source warning classification probe failed");
        }

        Console.WriteLine("Map source warning probe: PASSED");
        Console.WriteLine("  validated cache with healthy index is a clean pass");
        Console.WriteLine("  cached-index fallback and unavailable sources remain warnings");
    }

    private static void ValidateMapProjectionAlignment(GeoTileMapper mapper, WorldTile tile)
    {
        GeoSampleGrid reference = mapper.GetAreaSampleGrid(tile.X, tile.Z, OrtsTileSizeMeters, OrtsTileSizeMeters, 3);
        double maxError = 0;
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                (double pixelX, double pixelY) = mapper.ProjectToTilePixel(
                    tile,
                    reference.Longitudes[y, x],
                    reference.Latitudes[y, x],
                    MapImageSize);
                double expectedX = x * (MapImageSize / 2.0);
                double expectedY = y * (MapImageSize / 2.0);
                maxError = Math.Max(maxError, Math.Sqrt(Math.Pow(pixelX - expectedX, 2) + Math.Pow(pixelY - expectedY, 2)));
            }
        }
        if (maxError > 0.01)
        {
            throw new InvalidOperationException($"map/terrain projection round-trip error is {maxError:F4} pixels");
        }
        Console.WriteLine($"Map/terrain alignment check: PASSED, maximum corner/center error {maxError:F6} pixel(s).");
    }

    private static async Task GenerateMapTilesAsync(
        RouteLayout route,
        GeoTileMapper mapper,
        IReadOnlyList<TerrainTile> selectedTiles,
        string? requestedMapTile,
        int limit,
        bool cacheOnly,
        CancellationToken cancellationToken)
    {
        List<TerrainTile> tiles = selectedTiles
            .Where(t => t.WorldTile is not null && t.TileFile.Exists)
            .DistinctBy(t => t.TileFile.FullName, StringComparer.OrdinalIgnoreCase)
            .Where(t => string.IsNullOrWhiteSpace(requestedMapTile) ||
                string.Equals(Path.GetFileNameWithoutExtension(t.TileFile.Name), requestedMapTile.Trim().TrimEnd('.', 't'), StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.WorldTile!.Z)
            .ThenBy(t => t.WorldTile!.X)
            .Take(Math.Max(0, limit))
            .ToList();
        if (tiles.Count == 0)
        {
            Console.WriteLine("Error: Create Map Tiles has no selected normal terrain tiles to process.");
            return;
        }

        using HttpClient client = CreateMapHttpClient(Timeout.InfiniteTimeSpan);
        IReadOnlyList<(double Lon, double Lat)> coveragePoints = GetMapAndRouteCoveragePoints(
            mapper, tiles, route.WorldTiles);
        GeofabrikResolution resolution = await ResolveGeofabrikRegionAsync(
            client, route.RouteDir, mapper, coveragePoints, cacheOnly, cancellationToken);
        if (!resolution.CanRun)
        {
            throw new InvalidOperationException(
                $"Geofabrik map source is unavailable and no usable cached extract exists: {resolution.Detail}");
        }
        GeofabrikRegion region = resolution.Region;
        GeofabrikExtract extract = await EnsureGeofabrikExtractAsync(
            client,
            route.RouteDir,
            region,
            resolution.CachedExtractPath,
            cacheOnly || resolution.CacheOnly,
            cancellationToken);
        string pbfPath = extract.Path;
        string terrainMapsDir = Path.Combine(route.RouteDir, "terrain_maps");
        Directory.CreateDirectory(terrainMapsDir);

        Console.WriteLine($"\nCreating {tiles.Count:N0} TSRE terrain map tile(s) from {region.Name}...");
        Console.WriteLine($"PBF cache: {pbfPath}");
        Console.WriteLine("STATUS: OSM - PROCESSING");
        WriteOsmLogSection("OSM / POLYVEG PROCESSING");
        WriteOsmLogBullet("This is a time-intensive operation.");
        WriteOsmLogBullet("Completed work is checkpointed; long operations report every five minutes.");
        WriteOsmLogSubsection("OSM SOURCE READING");
        ConfigureOsmRuntime();
        int completed = 0;
        int started = 0;
        string? routeWorkingCache = FindCurrentRouteOsmWorkingCache(route, pbfPath);
        if (routeWorkingCache is null)
        {
            WriteOsmLogSubsection("ROUTE OSM EXTRACTION");
            WriteOsmLogEntry("Scanning the regional PBF once and carving a reusable route subset.");
            routeWorkingCache = BuildRouteOsmWorkingCacheFromSources(
                route,
                mapper,
                [pbfPath],
                cancellationToken);
        }
        else
        {
            WriteOsmLogEntry($"Using current compact route OSM cache: {routeWorkingCache}");
        }
        WriteOsmLogSubsection("ROUTE FEATURE PROCESSING");
        using GdalDataset dataSource = Gdal.OpenEx(
            routeWorkingCache,
            (uint)(GdalConst.OF_VECTOR | GdalConst.OF_READONLY),
            null, null, null)
            ?? throw new InvalidOperationException("GDAL could not open the compact route OSM cache");
        List<OsmPrimitive> mapGeometry = LoadOsmGeometry(
            dataSource,
            route,
            mapper,
            tiles,
            pbfPath,
            extract.Downloaded,
            cancellationToken);
        long pointCount = mapGeometry.Sum(p =>
            (long)p.Points.Length + p.InnerRings.Sum(ring => (long)ring.Length));
        long estimatedBytes = (pointCount * 16L) + (mapGeometry.Count * 96L);
        WriteOsmLogEntry(
            $"Retained: {mapGeometry.Count:N0} geometry parts; {pointCount:N0} points; {FormatByteCount(estimatedBytes)} memory.");
        Console.WriteLine("STATUS: OSM - MAKING MAPS");
        WriteOsmLogSection("MAP TILE RENDERING");
        WriteOsmLogEntry($"Rendering {tiles.Count:N0} aligned TSRE map tiles.");
        int sequenceWidth = tiles.Count.ToString(CultureInfo.InvariantCulture).Length;

        await Parallel.ForEachAsync(
            tiles,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MapTileParallelism,
                CancellationToken = cancellationToken
            },
            (tile, tileCancellationToken) =>
        {
            tileCancellationToken.ThrowIfCancellationRequested();
            WorldTile worldTile = tile.WorldTile!;
            string baseName = Path.GetFileNameWithoutExtension(tile.TileFile.Name).ToLowerInvariant();
            string pngName = GetTsreMapCacheFileName(worldTile);
            string pngPath = Path.Combine(terrainMapsDir, pngName);
            string pngTemp = pngPath + ".tmp";
            int sequence = Interlocked.Increment(ref started);

            try
            {
                WriteOsmLogEntry(
                    $"[{sequence.ToString().PadLeft(sequenceWidth)}/{tiles.Count}] {baseName} — rendering.");
                using Bitmap bitmap = RenderMapBitmap(mapGeometry, mapper, worldTile, tileCancellationToken, out int renderedParts);
                WriteOsmLogEntry(renderedParts > 0
                    ? $"[{sequence.ToString().PadLeft(sequenceWidth)}/{tiles.Count}] {baseName} — {renderedParts:N0} geometry parts."
                    : $"[{sequence.ToString().PadLeft(sequenceWidth)}/{tiles.Count}] {baseName} — background only.");
                bitmap.Save(pngTemp, ImageFormat.Png);

                File.Move(pngTemp, pngPath, overwrite: true);
                Interlocked.Increment(ref completed);
            }
            finally
            {
                if (File.Exists(pngTemp)) File.Delete(pngTemp);
            }
            return ValueTask.CompletedTask;
        });

        WriteOsmLogSection("OSM / MAP RESULTS");
        WriteOsmLogEntry($"Created {completed:N0} TSRE F3 PNG map files.");
        WriteOsmLogEntry("Existing matching PNG files replaced; terrain files unchanged.");
        Console.WriteLine("STATUS: OSM / MAPS - COMPLETE");
    }

    private static string GetTsreMapCacheFileName(WorldTile tile)
    {
        // TSRE's MapWindow hashes the low-corner map coordinates as X*10000+Y.
        // The route terrain coordinate exposed by LIDEX is Z=-Y, so the disk
        // cache name must subtract Z. Example: -11021,14358 -> -110224358.png.
        int hash = checked((tile.X * 10000) - tile.Z);
        return hash.ToString(CultureInfo.InvariantCulture) + ".png";
    }

    private static HttpClient CreateMapHttpClient(TimeSpan timeout)
    {
        HttpClient client = new() { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SCO-LIDEX/1.400 (Open Rails terrain builder)");
        return client;
    }

    private static void ConfigureOsmRuntime()
    {
        GdalBase.ConfigureAll();
        Ogr.RegisterAll();
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "runtimes", "any", "native", "gdal-data", "osmconf.ini"),
            Path.Combine(AppContext.BaseDirectory, "gdal-data", "osmconf.ini"),
            Path.Combine(Gdal.GetConfigOption("GDAL_DATA", "") ?? "", "osmconf.ini")
        ];
        string? configuration = candidates.FirstOrDefault(File.Exists);
        if (configuration is null)
        {
            throw new InvalidOperationException("bundled GDAL osmconf.ini was not found");
        }
        Gdal.SetConfigOption("OSM_CONFIG_FILE", configuration);
        Gdal.SetConfigOption("OGR_INTERLEAVED_READING", "YES");
    }

    private static async Task<GeofabrikResolution> ResolveGeofabrikRegionAsync(
        HttpClient client,
        string routeDir,
        GeoTileMapper mapper,
        IReadOnlyList<(double Lon, double Lat)>? coveragePoints,
        bool cacheOnly,
        CancellationToken cancellationToken)
    {
        string cacheDir = GetMapCacheDirectory();
        Directory.CreateDirectory(cacheDir);
        string indexCachePath = Path.Combine(cacheDir, "geofabrik-index-v1.json");
        string? indexJson = null;
        bool remoteIndexAvailable = false;
        string detail = "Geofabrik index unavailable";

        if (!cacheOnly)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(GeofabrikIndexUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                indexJson = await response.Content.ReadAsStringAsync(cancellationToken);
                File.WriteAllText(indexCachePath, indexJson, new UTF8Encoding(false));
                remoteIndexAvailable = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                detail = $"Geofabrik index request failed: {ex.Message}";
            }
        }

        if (indexJson is null && File.Exists(indexCachePath))
        {
            indexJson = File.ReadAllText(indexCachePath);
            detail += "; cached regional index used";
        }

        coveragePoints ??=
        [
            (mapper.MinLon, mapper.MinLat),
            (mapper.MinLon, mapper.MaxLat),
            (mapper.MaxLon, mapper.MinLat),
            (mapper.MaxLon, mapper.MaxLat),
            ((mapper.MinLon + mapper.MaxLon) / 2.0, (mapper.MinLat + mapper.MaxLat) / 2.0)
        ];

        if (indexJson is null)
        {
            ReusableOsmCache? reusable = FindReusableRouteOsmCache(
                routeDir,
                manifest => coveragePoints.All(point =>
                    point.Lon >= manifest.MinLongitude && point.Lon <= manifest.MaxLongitude &&
                    point.Lat >= manifest.MinLatitude && point.Lat <= manifest.MaxLatitude));
            if (reusable is not null)
            {
                OsmCacheManifest manifest = reusable.Manifest;
                GeofabrikRegion cachedRegion = new(
                    manifest.RegionId,
                    manifest.RegionName,
                    manifest.DownloadUrl,
                    manifest.SizeBytes,
                    manifest.SourceModifiedUtc,
                    manifest.MinLongitude,
                    manifest.MinLatitude,
                    manifest.MaxLongitude,
                    manifest.MaxLatitude);
                RegisterRouteOsmCache(reusable.RoutePath, manifest);
                return new GeofabrikResolution(
                    cachedRegion,
                    false,
                    false,
                    true,
                    reusable.PbfPath,
                    string.Equals(reusable.RoutePath, Path.GetFullPath(routeDir), StringComparison.OrdinalIgnoreCase)
                        ? "Geofabrik is unavailable; validated current-route manifest and PBF used"
                        : $"Geofabrik is unavailable; validated route cache used from {reusable.RoutePath}");
            }

            throw new InvalidOperationException(detail + "; no cached regional index or covering route cache exists");
        }

        using JsonDocument document = JsonDocument.Parse(indexJson);
        List<(GeofabrikRegion Region, double Area)> candidates = [];

        foreach (JsonElement feature in document.RootElement.GetProperty("features").EnumerateArray())
        {
            if (!feature.TryGetProperty("properties", out JsonElement properties) ||
                !properties.TryGetProperty("urls", out JsonElement urls) ||
                !urls.TryGetProperty("pbf", out JsonElement pbfElement))
            {
                continue;
            }

            (double minLon, double minLat, double maxLon, double maxLat) = GetJsonGeometryEnvelope(feature.GetProperty("geometry"));
            JsonElement geometry = feature.GetProperty("geometry");
            bool coversSelection = coveragePoints.All(point =>
                point.Lon >= minLon && point.Lon <= maxLon &&
                point.Lat >= minLat && point.Lat <= maxLat &&
                JsonGeometryContains(geometry, point.Lon, point.Lat));
            if (!coversSelection)
            {
                continue;
            }

            string id = properties.GetProperty("id").GetString() ?? "unknown";
            string name = properties.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? id : id;
            candidates.Add((
                new GeofabrikRegion(id, name, pbfElement.GetString()!, 0, null, minLon, minLat, maxLon, maxLat),
                (maxLon - minLon) * (maxLat - minLat)));
        }

        GeofabrikRegion chosen = candidates.OrderBy(c => c.Area).Select(c => c.Region).FirstOrDefault()
            ?? throw new InvalidOperationException("no single Geofabrik regional extract covers every selected terrain tile");

        HashSet<string> coveringRegionIds = candidates
            .Select(candidate => candidate.Region.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ReusableOsmCache? cached = FindReusableRouteOsmCache(
            routeDir,
            manifest => coveringRegionIds.Contains(manifest.RegionId));
        if (cached is not null)
        {
            GeofabrikRegion indexedRegion = candidates
                .Where(candidate => string.Equals(candidate.Region.Id, cached.Manifest.RegionId, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.Region)
                .First();
            GeofabrikRegion cachedRegion = indexedRegion with
            {
                Name = cached.Manifest.RegionName,
                PbfUrl = cached.Manifest.DownloadUrl,
                SizeBytes = cached.Manifest.SizeBytes,
                Modified = cached.Manifest.SourceModifiedUtc
            };
            RegisterRouteOsmCache(cached.RoutePath, cached.Manifest);
            return new GeofabrikResolution(
                cachedRegion,
                remoteIndexAvailable,
                false,
                true,
                cached.PbfPath,
                string.Equals(cached.RoutePath, Path.GetFullPath(routeDir), StringComparison.OrdinalIgnoreCase)
                    ? "using current-route OSM cache; remote PBF not polled"
                    : $"using covering OSM cache from {cached.RoutePath}; remote PBF not polled");
        }

        if (cacheOnly)
        {
            return new GeofabrikResolution(chosen, remoteIndexAvailable, false, false, null, detail);
        }

        try
        {
            using HttpRequestMessage head = new(HttpMethod.Head, chosen.PbfUrl);
            using HttpResponseMessage headResponse = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            headResponse.EnsureSuccessStatusCode();
            GeofabrikRegion availableRegion = chosen with
            {
                SizeBytes = headResponse.Content.Headers.ContentLength ?? 0,
                Modified = headResponse.Content.Headers.LastModified
            };
            return new GeofabrikResolution(
                availableRegion,
                remoteIndexAvailable,
                true,
                false,
                null,
                remoteIndexAvailable
                    ? "remote Geofabrik index and PBF are available"
                    : "cached Geofabrik index used; remote PBF is available");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new GeofabrikResolution(
                chosen,
                remoteIndexAvailable,
                false,
                false,
                null,
                $"Geofabrik PBF request failed: {ex.Message}");
        }
    }

    private static async Task<GeofabrikSourceSet> ResolveGeofabrikSourceSetAsync(
        HttpClient client,
        string routeDir,
        GeoTileMapper mapper,
        IReadOnlyList<(double Lon, double Lat)> coveragePoints,
        bool cacheOnly,
        CancellationToken cancellationToken)
    {
        GeofabrikResolution fallback = await ResolveGeofabrikRegionAsync(
            client, routeDir, mapper, coveragePoints, cacheOnly, cancellationToken);
        string indexPath = Path.Combine(GetMapCacheDirectory(), "geofabrik-index-v1.json");
        if (!File.Exists(indexPath)) return new GeofabrikSourceSet([fallback]);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(indexPath));
        List<(GeofabrikRegion Region, JsonElement Geometry)> relevantExtracts = [];
        foreach (JsonElement feature in document.RootElement.GetProperty("features").EnumerateArray())
        {
            JsonElement properties = feature.GetProperty("properties");
            string id = properties.GetProperty("id").GetString() ?? "";
            if (!properties.TryGetProperty("urls", out JsonElement urls) ||
                !urls.TryGetProperty("pbf", out JsonElement pbf))
            {
                continue;
            }
            (double minLon, double minLat, double maxLon, double maxLat) =
                GetJsonGeometryEnvelope(feature.GetProperty("geometry"));
            JsonElement geometry = feature.GetProperty("geometry").Clone();
            if (coveragePoints.Any(point =>
                    point.Lon >= minLon && point.Lon <= maxLon &&
                    point.Lat >= minLat && point.Lat <= maxLat &&
                    JsonGeometryContains(geometry, point.Lon, point.Lat)))
            {
                string name = properties.TryGetProperty("name", out JsonElement nameElement)
                    ? nameElement.GetString() ?? id
                    : id;
                relevantExtracts.Add((
                    new GeofabrikRegion(id, name, pbf.GetString()!, 0, null,
                        minLon, minLat, maxLon, maxLat),
                    geometry));
            }
        }
        if (relevantExtracts.Count == 0)
        {
            return new GeofabrikSourceSet([fallback]);
        }

        var coverageCandidates = relevantExtracts
            .Select(extract => new
            {
                Extract = extract,
                Area = Math.Max(1e-12,
                    (extract.Region.MaxLon - extract.Region.MinLon) *
                    (extract.Region.MaxLat - extract.Region.MinLat)),
                Covers = coveragePoints
                    .Select(point => JsonGeometryContains(
                        extract.Geometry, point.Lon, point.Lat))
                    .ToArray(),
            })
            .GroupBy(candidate => string.Concat(candidate.Covers.Select(value => value ? '1' : '0')),
                StringComparer.Ordinal)
            .Select(group => group.OrderBy(candidate => candidate.Area).First())
            .ToList();

        bool[] uncovered = Enumerable.Repeat(true, coveragePoints.Count).ToArray();
        List<int> selectedIndexes = [];
        while (uncovered.Any(value => value))
        {
            int bestIndex = -1;
            double bestCost = double.PositiveInfinity;
            int bestNewCoverage = 0;
            for (int index = 0; index < coverageCandidates.Count; index++)
            {
                if (selectedIndexes.Contains(index)) continue;
                int newlyCovered = 0;
                for (int point = 0; point < uncovered.Length; point++)
                {
                    if (uncovered[point] && coverageCandidates[index].Covers[point]) newlyCovered++;
                }
                if (newlyCovered == 0) continue;
                double cost = coverageCandidates[index].Area / newlyCovered;
                if (cost < bestCost ||
                    (Math.Abs(cost - bestCost) < 1e-12 && newlyCovered > bestNewCoverage))
                {
                    bestIndex = index;
                    bestCost = cost;
                    bestNewCoverage = newlyCovered;
                }
            }
            if (bestIndex < 0) return new GeofabrikSourceSet([fallback]);
            selectedIndexes.Add(bestIndex);
            for (int point = 0; point < uncovered.Length; point++)
            {
                if (coverageCandidates[bestIndex].Covers[point]) uncovered[point] = false;
            }
        }

        // Remove any extract made redundant by later selections.
        for (int selected = selectedIndexes.Count - 1; selected >= 0; selected--)
        {
            int candidateToRemove = selectedIndexes[selected];
            bool stillCovered = Enumerable.Range(0, coveragePoints.Count).All(point =>
                selectedIndexes.Any(index => index != candidateToRemove &&
                    coverageCandidates[index].Covers[point]));
            if (stillCovered) selectedIndexes.RemoveAt(selected);
        }
        List<(GeofabrikRegion Region, JsonElement Geometry)> selectedExtracts = selectedIndexes
            .Select(index => coverageCandidates[index].Extract)
            .ToList();

        List<GeofabrikResolution> resolutions = [];
        foreach ((GeofabrikRegion indexedRegion, _) in selectedExtracts.OrderBy(extract => extract.Region.Id, StringComparer.Ordinal))
        {
            string routePath = GetRouteGeofabrikExtractPath(routeDir, indexedRegion.Id);
            if (IsUsableCacheFile(routePath))
            {
                FileInfo cached = new(routePath);
                resolutions.Add(new GeofabrikResolution(
                    indexedRegion with { SizeBytes = cached.Length, Modified = cached.LastWriteTimeUtc },
                    fallback.RemoteIndexAvailable, false, true, routePath,
                    "using current-route regional extract"));
                continue;
            }
            if (cacheOnly)
            {
                resolutions.Add(new GeofabrikResolution(
                    indexedRegion, fallback.RemoteIndexAvailable, false, false, null,
                    "regional extract is not cached"));
                continue;
            }
            try
            {
                using HttpRequestMessage head = new(HttpMethod.Head, indexedRegion.PbfUrl);
                using HttpResponseMessage response = await client.SendAsync(
                    head, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                resolutions.Add(new GeofabrikResolution(
                    indexedRegion with
                    {
                        SizeBytes = response.Content.Headers.ContentLength ?? 0,
                        Modified = response.Content.Headers.LastModified,
                    },
                    fallback.RemoteIndexAvailable, true, false, null,
                    "remote regional extract is available"));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                resolutions.Add(new GeofabrikResolution(
                    indexedRegion, fallback.RemoteIndexAvailable, false, false, null,
                    $"regional extract request failed: {ex.Message}"));
            }
        }
        Console.WriteLine(
            "Geofabrik source selection: smallest covering global extract set is " +
            string.Join(" + ", selectedExtracts.Select(extract => extract.Region.Name)) + ".");
        return new GeofabrikSourceSet(resolutions);
    }

    private static IReadOnlyList<(double Lon, double Lat)> GetMapCoveragePoints(
        GeoTileMapper mapper,
        IReadOnlyList<TerrainTile> tiles)
    {
        List<(double Lon, double Lat)> points = new(tiles.Count * 5);
        foreach (TerrainTile tile in tiles)
        {
            WorldTile worldTile = tile.WorldTile!;
            (double minLon, double minLat, double maxLon, double maxLat) = mapper.GetBoundingBox(worldTile);
            points.Add((minLon, minLat));
            points.Add((minLon, maxLat));
            points.Add((maxLon, minLat));
            points.Add((maxLon, maxLat));
            points.Add(((minLon + maxLon) / 2.0, (minLat + maxLat) / 2.0));
        }
        return points;
    }

    private static IReadOnlyList<(double Lon, double Lat)> GetMapAndRouteCoveragePoints(
        GeoTileMapper mapper,
        IReadOnlyList<TerrainTile> mapTiles,
        IReadOnlyList<WorldTile> routeWorldTiles)
    {
        List<(double Lon, double Lat)> points = [.. GetMapCoveragePoints(mapper, mapTiles)];
        foreach (WorldTile worldTile in routeWorldTiles.DistinctBy(tile => (tile.X, tile.Z)))
        {
            (double minLon, double minLat, double maxLon, double maxLat) = mapper.GetBoundingBox(worldTile);
            points.Add((minLon, minLat));
            points.Add((minLon, maxLat));
            points.Add((maxLon, minLat));
            points.Add((maxLon, maxLat));
            points.Add(((minLon + maxLon) / 2.0, (minLat + maxLat) / 2.0));
        }
        return points;
    }

    private static (double MinLon, double MinLat, double MaxLon, double MaxLat) GetJsonGeometryEnvelope(JsonElement geometry)
    {
        double minLon = double.PositiveInfinity;
        double minLat = double.PositiveInfinity;
        double maxLon = double.NegativeInfinity;
        double maxLat = double.NegativeInfinity;
        void Visit(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            if (element.GetArrayLength() >= 2 &&
                element[0].ValueKind == JsonValueKind.Number && element[1].ValueKind == JsonValueKind.Number)
            {
                double lon = element[0].GetDouble();
                double lat = element[1].GetDouble();
                minLon = Math.Min(minLon, lon);
                minLat = Math.Min(minLat, lat);
                maxLon = Math.Max(maxLon, lon);
                maxLat = Math.Max(maxLat, lat);
                return;
            }

            foreach (JsonElement child in element.EnumerateArray())
            {
                Visit(child);
            }
        }

        Visit(geometry.GetProperty("coordinates"));
        return (minLon, minLat, maxLon, maxLat);
    }

    private static bool JsonGeometryContains(JsonElement geometry, double longitude, double latitude)
    {
        string type = geometry.GetProperty("type").GetString() ?? "";
        JsonElement coordinates = geometry.GetProperty("coordinates");
        if (type == "Polygon")
        {
            return JsonPolygonContains(coordinates, longitude, latitude);
        }
        if (type == "MultiPolygon")
        {
            foreach (JsonElement polygon in coordinates.EnumerateArray())
            {
                if (JsonPolygonContains(polygon, longitude, latitude)) return true;
            }
        }
        return false;
    }

    private static bool JsonPolygonContains(JsonElement polygon, double longitude, double latitude)
    {
        bool inside = false;
        int ringIndex = 0;
        foreach (JsonElement ring in polygon.EnumerateArray())
        {
            bool ringInside = false;
            int count = ring.GetArrayLength();
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                double xi = ring[i][0].GetDouble();
                double yi = ring[i][1].GetDouble();
                double xj = ring[j][0].GetDouble();
                double yj = ring[j][1].GetDouble();
                if (((yi > latitude) != (yj > latitude)) &&
                    longitude < ((xj - xi) * (latitude - yi) / (yj - yi)) + xi)
                {
                    ringInside = !ringInside;
                }
            }
            if (ringIndex++ == 0) inside = ringInside;
            else if (ringInside) inside = false;
        }
        return inside;
    }

    private static async Task<GeofabrikExtract> EnsureGeofabrikExtractAsync(
        HttpClient client,
        string routeDir,
        GeofabrikRegion region,
        string? preferredCachedPath,
        bool cacheOnly,
        CancellationToken cancellationToken)
    {
        string routePath = GetRouteGeofabrikExtractPath(routeDir, region.Id);
        if (preferredCachedPath is not null && IsUsableCacheFile(preferredCachedPath))
        {
            bool currentRouteCache = IsPathInside(preferredCachedPath, GetRouteOsmDirectory(routeDir));
            Console.WriteLine(currentRouteCache
                ? "Using current route Geofabrik extract."
                : $"Using existing Geofabrik extract in place: {preferredCachedPath}");
            return new GeofabrikExtract(preferredCachedPath, false);
        }

        // A route-local file has priority even if a caller did not resolve a
        // manifest first. Existing extracts are intentionally not compared to
        // the latest remote size; purging is the user's refresh operation.
        if (IsUsableCacheFile(routePath))
        {
            Console.WriteLine("Using current route Geofabrik extract.");
            return new GeofabrikExtract(routePath, false);
        }

        if (cacheOnly)
        {
            throw new InvalidOperationException(
                $"cached Geofabrik extract is unavailable or incomplete: {routePath}");
        }

        string finalPath = routePath;
        Directory.CreateDirectory(GetRouteOsmProviderDirectory(routeDir));

        string partialPath = finalPath + ".part";
        Console.WriteLine("STATUS: OSM - DOWNLOAD");
        long existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        using HttpRequestMessage request = new(HttpMethod.Get, region.PbfUrl);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            existing = 0;
        }
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using FileStream target = new(partialPath, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        byte[] buffer = new byte[1024 * 1024];
        long total = existing;
        long nextReport = total + (64L * 1024 * 1024);
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            if (total >= nextReport)
            {
                Console.WriteLine($"  Map extract downloaded: {FormatByteCount(total)} / {FormatByteCount(region.SizeBytes)}");
                nextReport += 64L * 1024 * 1024;
            }
        }
        await target.FlushAsync(cancellationToken);
        target.Close();
        if (region.SizeBytes > 0 && total != region.SizeBytes)
        {
            throw new InvalidDataException($"Geofabrik download is incomplete: expected {region.SizeBytes:N0} bytes, received {total:N0}");
        }
        File.Move(partialPath, finalPath, overwrite: true);
        WriteRouteOsmManifest(routeDir, region, finalPath);
        Console.WriteLine($"Saved route-local OSM cache: {finalPath}");
        return new GeofabrikExtract(finalPath, true);
    }

    private static Bitmap RenderMapBitmap(IReadOnlyList<OsmPrimitive> primitives, GeoTileMapper mapper, WorldTile tile, CancellationToken cancellationToken, out int renderedParts)
    {
        Bitmap bitmap = new(MapImageSize, MapImageSize, PixelFormat.Format24bppRgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        // Match TSRE MapDataOSM::draw: warm paper base and non-antialiased
        // vector rendering. Terrain lighting will darken the final draped ACE.
        graphics.Clear(Color.FromArgb(241, 238, 232));
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.Default;
        (double minLon, double minLat, double maxLon, double maxLat) = mapper.GetBoundingBox(tile);
        double padLon = (maxLon - minLon) * 0.02;
        double padLat = (maxLat - minLat) * 0.02;

        renderedParts = 0;
        foreach (OsmPrimitive primitive in primitives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (primitive.MaxLon < minLon - padLon || primitive.MinLon > maxLon + padLon ||
                primitive.MaxLat < minLat - padLat || primitive.MinLat > maxLat + padLat)
            {
                continue;
            }
            renderedParts += DrawOsmPrimitive(graphics, mapper, tile, primitive);
        }
        return bitmap;
    }

    private readonly record struct OsmStyle(
        Color FillColor,
        Color StrokeColor,
        float StrokeWidth,
        Color CasingColor,
        float CasingWidth,
        int DrawOrder)
    {
        public bool Fill => FillColor != Color.Empty;
    }
    private readonly record struct OsmPoint(double Longitude, double Latitude);
    private sealed record OsmPrimitive(
        OsmStyle Style,
        string SourceSortKey,
        int SourcePartSequence,
        OsmPoint[] Points,
        OsmPoint[][] InnerRings,
        bool IsPolygon,
        double MinLon,
        double MinLat,
        double MaxLon,
        double MaxLat);

    private sealed record PolyVegClassification(
        string Category,
        string StyleId,
        int DrawOrder,
        int FillRed,
        int FillGreen,
        int FillBlue);

    private static void WriteOsmLogSection(string title)
    {
        WriteLogSection(title);
    }

    private static void WriteOsmLogSubsection(string title)
    {
        WriteLogSubsection(title);
    }

    private static void WriteOsmLogBullet(string message)
    {
        WriteLogBullet(message);
    }

    private static void WriteOsmLogEntry(string message, int indent = 2)
    {
        int safeIndent = Math.Max(0, indent);
        string bullet = safeIndent >= 4 ? "• " : "";
        Console.WriteLine($"{new string(' ', safeIndent)}{bullet}[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private sealed class ProcessingCheckpoints
    {
        private readonly int total;
        private readonly int interval;
        private int nextCheckpoint;
        private int lastReported = -1;

        internal ProcessingCheckpoints(int totalItems)
        {
            total = Math.Max(0, totalItems);
            interval = Math.Max(1, (total + 9) / 10);
            nextCheckpoint = interval;
        }

        internal void Report(int completed, string description)
        {
            if (completed == lastReported) return;
            if (total == 0 || (completed < nextCheckpoint && completed < total)) return;
            WriteOsmLogEntry($"{description}: {Math.Min(completed, total):N0} / {total:N0}.", indent: 4);
            lastReported = completed;
            while (nextCheckpoint <= completed)
            {
                nextCheckpoint += interval;
            }
        }
    }

    private sealed class ProcessingHeartbeat : IDisposable
    {
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
        private readonly string task;
        private readonly System.Threading.Timer timer;
        private int finished;

        internal ProcessingHeartbeat(string taskDescription)
        {
            task = taskDescription;
            WriteOsmLogEntry($"{task}...");
            timer = new System.Threading.Timer(
                _ => WriteHeartbeat(), null, HeartbeatInterval, HeartbeatInterval);
        }

        internal void Complete()
        {
            if (Interlocked.Exchange(ref finished, 1) != 0) return;
            timer.Dispose();
            WriteOsmLogEntry($"{task} complete.");
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref finished, 1);
            timer.Dispose();
        }

        private void WriteHeartbeat()
        {
            if (Volatile.Read(ref finished) != 0) return;
            WriteOsmLogEntry($"Still processing: {task}.", indent: 4);
        }
    }

    private static string? FindCurrentRouteOsmWorkingCache(RouteLayout route, string sourcePath)
    {
        string osmDirectory = GetRouteOsmDirectory(route.RouteDir);
        string cachePath = Path.Combine(osmDirectory, RouteOsmWorkingCacheFileName);
        string manifestPath = Path.Combine(osmDirectory, RouteOsmWorkingManifestFileName);
        if (!File.Exists(cachePath) || !File.Exists(manifestPath) || !File.Exists(sourcePath)) return null;
        try
        {
            RouteOsmWorkingManifest? manifest = JsonSerializer.Deserialize<RouteOsmWorkingManifest>(
                File.ReadAllText(manifestPath));
            FileInfo source = new(sourcePath);
            string fingerprint = RoutePolyVegGeodataBuilder.GetRouteCoverageFingerprint(route);
            if (manifest is null || manifest.SchemaVersion != 4 ||
                !string.Equals(Path.GetFullPath(manifest.SourcePath), source.FullName, StringComparison.OrdinalIgnoreCase) ||
                manifest.SourceSizeBytes != source.Length ||
                manifest.SourceModifiedUtc != source.LastWriteTimeUtc ||
                manifest.CacheSizeBytes != new FileInfo(cachePath).Length ||
                !string.Equals(manifest.TerrainTileFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return cachePath;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Route working OSM cache could not be validated: {ex.Message}");
            return null;
        }
    }

    private static string? FindUsableRouteOsmWorkingCache(RouteLayout route)
    {
        string osmDirectory = GetRouteOsmDirectory(route.RouteDir);
        string cachePath = Path.Combine(osmDirectory, RouteOsmWorkingCacheFileName);
        string manifestPath = Path.Combine(osmDirectory, RouteOsmWorkingManifestFileName);
        if (!File.Exists(cachePath) || !File.Exists(manifestPath)) return null;
        try
        {
            RouteOsmWorkingManifest? manifest = JsonSerializer.Deserialize<RouteOsmWorkingManifest>(
                File.ReadAllText(manifestPath));
            string fingerprint = RoutePolyVegGeodataBuilder.GetRouteCoverageFingerprint(route);
            if (manifest is null || manifest.SchemaVersion != 4 ||
                manifest.CacheSizeBytes != new FileInfo(cachePath).Length ||
                !string.Equals(manifest.TerrainTileFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            using DataSource? probe = Ogr.Open(cachePath, 0);
            return probe is not null && probe.GetLayerCount() > 0 ? cachePath : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Compact route OSM cache could not be validated: {ex.Message}");
            return null;
        }
    }

    private static bool RouteOsmWorkingSourceNeedsRefresh(RouteLayout route)
    {
        string manifestPath = Path.Combine(
            GetRouteOsmDirectory(route.RouteDir), RouteOsmWorkingManifestFileName);
        try
        {
            if (!File.Exists(manifestPath)) return true;
            RouteOsmWorkingManifest? manifest = JsonSerializer.Deserialize<RouteOsmWorkingManifest>(
                File.ReadAllText(manifestPath));
            if (manifest is null) return true;
            IReadOnlyList<RouteOsmWorkingSourceStamp> sources = manifest.Sources is { Count: > 0 }
                ? manifest.Sources
                : [new RouteOsmWorkingSourceStamp(
                    manifest.SourcePath, manifest.SourceSizeBytes, manifest.SourceModifiedUtc)];
            return sources.Any(stamp =>
            {
                if (!File.Exists(stamp.Path)) return true;
                FileInfo source = new(stamp.Path);
                return source.Length != stamp.SizeBytes || source.LastWriteTimeUtc != stamp.ModifiedUtc;
            });
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Compact route OSM source status could not be checked: {ex.Message}");
            return true;
        }
    }

    private static string BuildRouteOsmWorkingCacheFromSources(
        RouteLayout route,
        GeoTileMapper mapper,
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        using Geometry extractionCoverage = BuildRouteOsmExtractionCoverage(route, mapper);
        Envelope extractionEnvelope = new();
        extractionCoverage.GetEnvelope(extractionEnvelope);
        using ProcessingHeartbeat stage = new(
            "Extracting compact route geometry from the regional OSM source");
        using RouteOsmWorkingCacheWriter writer = new(
            route, sourcePaths[0], extractionCoverage, cancellationToken, sourcePaths);
        foreach (string sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteOsmLogEntry($"Regional source: {sourcePath}");
            using GdalDataset source = Gdal.OpenEx(
                sourcePath,
                (uint)(GdalConst.OF_VECTOR | GdalConst.OF_READONLY),
                null, null, null)
                ?? throw new InvalidOperationException($"GDAL could not open regional OSM source: {sourcePath}");
            for (int index = 0; index < source.GetLayerCount(); index++)
            {
                using Layer layer = source.GetLayer(index);
                if (string.Equals(layer.GetName(), "points", StringComparison.OrdinalIgnoreCase)) continue;
                layer.SetSpatialFilterRect(
                    extractionEnvelope.MinX,
                    extractionEnvelope.MinY,
                    extractionEnvelope.MaxX,
                    extractionEnvelope.MaxY);
            }
            source.ResetReading();
            double progress = 0;
            IntPtr layerHandle = IntPtr.Zero;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Feature? feature = source.GetNextFeature(ref layerHandle, ref progress, null!, "");
                if (feature is null) break;
                using Geometry? geometry = feature.GetGeometryRef();
                if (geometry is not null) writer.Add(feature, geometry);
            }
        }
        writer.Complete();
        stage.Complete();
        return FindUsableRouteOsmWorkingCache(route)
            ?? throw new InvalidDataException("the compact multi-source route OSM cache failed validation");
    }

    private static Geometry BuildRouteOsmExtractionCoverage(
        RouteLayout route,
        GeoTileMapper mapper)
    {
        using Geometry tilePolygons = new(wkbGeometryType.wkbMultiPolygon);
        foreach (WorldTile tile in RoutePolyVegGeodataBuilder.GetRouteCoverageTiles(route))
        {
            GeoSampleGrid corners = mapper.GetAreaSampleGrid(
                tile.X, tile.Z, OrtsTileSizeMeters, OrtsTileSizeMeters, 2);
            using Geometry ring = new(wkbGeometryType.wkbLinearRing);
            ring.AddPoint_2D(corners.Longitudes[1, 0], corners.Latitudes[1, 0]);
            ring.AddPoint_2D(corners.Longitudes[1, 1], corners.Latitudes[1, 1]);
            ring.AddPoint_2D(corners.Longitudes[0, 1], corners.Latitudes[0, 1]);
            ring.AddPoint_2D(corners.Longitudes[0, 0], corners.Latitudes[0, 0]);
            ring.AddPoint_2D(corners.Longitudes[1, 0], corners.Latitudes[1, 0]);
            using Geometry polygon = new(wkbGeometryType.wkbPolygon);
            polygon.AddGeometry(ring);
            tilePolygons.AddGeometry(polygon);
        }
        using Geometry exactCoverage = tilePolygons.UnionCascaded()
            ?? throw new InvalidOperationException("GDAL could not combine route terrain coverage");

        // Roughly 2.5-3.3 km at inhabited latitudes: safely exceeds the 2048 m
        // context used for roads, waterways, and other permanent exclusions.
        return exactCoverage.Buffer(0.03, 8)
            ?? throw new InvalidOperationException("GDAL could not buffer route extraction coverage");
    }

    private sealed class RouteOsmWorkingCacheWriter : IDisposable
    {
        private readonly string sourcePath;
        private readonly IReadOnlyList<string> sourcePaths;
        private readonly string cachePath;
        private readonly string temporaryCachePath;
        private readonly string manifestPath;
        private readonly string temporaryManifestPath;
        private readonly string fingerprint;
        private readonly CancellationToken cancellationToken;
        private readonly Geometry extractionCoverage;
        private readonly Envelope extractionEnvelope = new();
        private readonly DataSource dataSource;
        private readonly SpatialReference geographic;
        private readonly Dictionary<string, Layer> layers = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> retainedFeatureKeys = new(StringComparer.Ordinal);
        private bool completed;
        private int featureCount;

        internal RouteOsmWorkingCacheWriter(
            RouteLayout route,
            string pbfPath,
            Geometry coverage,
            CancellationToken token,
            IReadOnlyList<string>? allSourcePaths = null)
        {
            sourcePath = Path.GetFullPath(pbfPath);
            sourcePaths = (allSourcePaths ?? [pbfPath])
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string osmDirectory = GetRouteOsmDirectory(route.RouteDir);
            Directory.CreateDirectory(osmDirectory);
            cachePath = Path.Combine(osmDirectory, RouteOsmWorkingCacheFileName);
            temporaryCachePath = Path.Combine(osmDirectory, "route-osm-working.tmp.gpkg");
            manifestPath = Path.Combine(osmDirectory, RouteOsmWorkingManifestFileName);
            temporaryManifestPath = manifestPath + ".tmp";
            fingerprint = RoutePolyVegGeodataBuilder.GetRouteCoverageFingerprint(route);
            cancellationToken = token;
            extractionCoverage = coverage.Clone();
            extractionCoverage.GetEnvelope(extractionEnvelope);
            if (File.Exists(temporaryCachePath)) File.Delete(temporaryCachePath);
            if (File.Exists(temporaryManifestPath)) File.Delete(temporaryManifestPath);
            OSGeo.OGR.Driver driver = Ogr.GetDriverByName("GPKG")
                ?? throw new InvalidOperationException("bundled GDAL GeoPackage driver is unavailable");
            dataSource = driver.CreateDataSource(temporaryCachePath, [])
                ?? throw new InvalidOperationException("GDAL could not create the compact route OSM cache");
            geographic = new SpatialReference("");
            geographic.ImportFromEPSG(4326);
            geographic.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        }

        internal void Add(Feature source, Geometry geometry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string layerName = source.GetDefnRef().GetName();
            if (string.Equals(layerName, "points", StringComparison.OrdinalIgnoreCase)) return;
            Envelope envelope = new();
            geometry.GetEnvelope(envelope);
            if (envelope.MaxX < extractionEnvelope.MinX || envelope.MinX > extractionEnvelope.MaxX ||
                envelope.MaxY < extractionEnvelope.MinY || envelope.MinY > extractionEnvelope.MaxY)
            {
                return;
            }
            using Geometry? clipped = ClipToRouteExtractionCoverage(geometry);
            if (clipped is null || clipped.IsEmpty()) return;
            string featureKey = string.Create(
                CultureInfo.InvariantCulture,
                $"{layerName}|{StableOsmDrawSortKey(source)}|{envelope.MinX:F7}|{envelope.MinY:F7}|{envelope.MaxX:F7}|{envelope.MaxY:F7}");
            if (!retainedFeatureKeys.Add(featureKey)) return;
            if (!layers.TryGetValue(layerName, out Layer? layer))
            {
                layer = dataSource.CreateLayer(
                    layerName,
                    geographic,
                    wkbGeometryType.wkbUnknown,
                    ["SPATIAL_INDEX=YES"])
                    ?? throw new InvalidOperationException($"could not create compact OSM layer {layerName}");
                foreach (string fieldName in RouteOsmWorkingFields)
                {
                    using FieldDefn field = new(fieldName, FieldType.OFTString);
                    field.SetWidth(fieldName == "other_tags" ? 0 : 254);
                    if (layer.CreateField(field, 1) != 0)
                        throw new InvalidOperationException($"could not create compact OSM field {layerName}.{fieldName}");
                }
                layers.Add(layerName, layer);
            }

            using Feature output = new(layer.GetLayerDefn());
            foreach (string fieldName in RouteOsmWorkingFields)
            {
                string value = GetOgrField(source, fieldName);
                if (!string.IsNullOrEmpty(value)) output.SetField(fieldName, value);
            }
            output.SetGeometry(clipped);
            if (layer.CreateFeature(output) != 0)
                throw new InvalidOperationException($"could not write compact OSM feature in {layerName}");
            featureCount++;
        }

        private Geometry? ClipToRouteExtractionCoverage(Geometry geometry)
        {
            try
            {
                if (!geometry.Intersects(extractionCoverage)) return null;
                return geometry.Intersection(extractionCoverage);
            }
            catch
            {
                using Geometry? repaired = geometry.MakeValid([]);
                if (repaired is null || repaired.IsEmpty() ||
                    !repaired.Intersects(extractionCoverage))
                {
                    return null;
                }
                return repaired.Intersection(extractionCoverage);
            }
        }

        internal void Complete()
        {
            if (completed) return;
            foreach (Layer layer in layers.Values) layer.SyncToDisk();
            DisposeDataSource();
            FileInfo source = new(sourcePath);
            FileInfo compactCache = new(temporaryCachePath);
            RouteOsmWorkingSourceStamp[] sourceStamps = sourcePaths
                .Select(path => new FileInfo(path))
                .Select(file => new RouteOsmWorkingSourceStamp(
                    file.FullName, file.Length, file.LastWriteTimeUtc))
                .ToArray();
            RouteOsmWorkingManifest manifest = new(
                4,
                source.FullName,
                source.Length,
                source.LastWriteTimeUtc,
                fingerprint,
                compactCache.Length,
                sourceStamps);
            File.WriteAllText(
                temporaryManifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            string cacheBackupPath = cachePath + ".previous";
            string manifestBackupPath = manifestPath + ".previous";
            if (File.Exists(cacheBackupPath)) File.Delete(cacheBackupPath);
            if (File.Exists(manifestBackupPath)) File.Delete(manifestBackupPath);
            bool cacheBackedUp = false;
            bool manifestBackedUp = false;
            try
            {
                if (File.Exists(cachePath))
                {
                    File.Move(cachePath, cacheBackupPath);
                    cacheBackedUp = true;
                }
                if (File.Exists(manifestPath))
                {
                    File.Move(manifestPath, manifestBackupPath);
                    manifestBackedUp = true;
                }
                File.Move(temporaryCachePath, cachePath);
                File.Move(temporaryManifestPath, manifestPath);
                if (cacheBackedUp) File.Delete(cacheBackupPath);
                if (manifestBackedUp) File.Delete(manifestBackupPath);
            }
            catch
            {
                if (File.Exists(cachePath)) File.Delete(cachePath);
                if (File.Exists(manifestPath)) File.Delete(manifestPath);
                if (cacheBackedUp && File.Exists(cacheBackupPath))
                    File.Move(cacheBackupPath, cachePath);
                if (manifestBackedUp && File.Exists(manifestBackupPath))
                    File.Move(manifestBackupPath, manifestPath);
                throw;
            }
            completed = true;
            Console.WriteLine(
                $"Compact route OSM cache complete: {featureCount:N0} applicable feature(s) saved to {cachePath}");
        }

        private void DisposeDataSource()
        {
            foreach (Layer layer in layers.Values) layer.Dispose();
            layers.Clear();
            dataSource.Dispose();
            geographic.Dispose();
            extractionCoverage.Dispose();
        }

        public void Dispose()
        {
            if (!completed)
            {
                DisposeDataSource();
                if (File.Exists(temporaryCachePath)) File.Delete(temporaryCachePath);
                if (File.Exists(temporaryManifestPath)) File.Delete(temporaryManifestPath);
            }
        }
    }

    private static List<OsmPrimitive> LoadOsmGeometry(
        GdalDataset dataSource,
        RouteLayout route,
        GeoTileMapper mapper,
        IReadOnlyList<TerrainTile> tiles,
        string pbfPath,
        bool forceRouteDerivativeRefresh,
        CancellationToken cancellationToken)
    {
        double minLon = double.PositiveInfinity;
        double minLat = double.PositiveInfinity;
        double maxLon = double.NegativeInfinity;
        double maxLat = double.NegativeInfinity;
        foreach (TerrainTile terrainTile in tiles)
        {
            (double tileMinLon, double tileMinLat, double tileMaxLon, double tileMaxLat) = mapper.GetBoundingBox(terrainTile.WorldTile!);
            minLon = Math.Min(minLon, tileMinLon);
            minLat = Math.Min(minLat, tileMinLat);
            maxLon = Math.Max(maxLon, tileMaxLon);
            maxLat = Math.Max(maxLat, tileMaxLat);
        }
        using RoutePolyVegGeodataBuilder? geodataBuilder = RoutePolyVegGeodataBuilder.TryCreate(
            route,
            mapper,
            pbfPath,
            forceRouteDerivativeRefresh,
            cancellationToken);
        if (geodataBuilder is not null)
        {
            minLon = Math.Min(minLon, geodataBuilder.MinLongitude);
            minLat = Math.Min(minLat, geodataBuilder.MinLatitude);
            maxLon = Math.Max(maxLon, geodataBuilder.MaxLongitude);
            maxLat = Math.Max(maxLat, geodataBuilder.MaxLatitude);
        }
        double padLon = (maxLon - minLon) * 0.01;
        double padLat = (maxLat - minLat) * 0.01;
        for (int i = 0; i < dataSource.GetLayerCount(); i++)
        {
            using Layer layer = dataSource.GetLayer(i);
            // GDAL's OSM driver assembles ways and multipolygon relations from
            // the node stream. Filtering the points layer first can remove
            // vertices far outside the route even when the completed polygon
            // crosses a selected tile (long rivers are a common example).
            // Keep nodes unfiltered for relation integrity; filter only the
            // completed feature layers to the current terrain batch.
            if (string.Equals(layer.GetName(), "points", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            layer.SetSpatialFilterRect(minLon - padLon, minLat - padLat, maxLon + padLon, maxLat + padLat);
        }
        WriteOsmLogEntry("Relation-safe read; completed geometry limited to route coverage.");
        dataSource.ResetReading();
        List<OsmPrimitive> primitives = [];
        double progress = 0;
        int featuresRead = 0;
        int nextReadPercent = 10;
        IntPtr layerHandle = IntPtr.Zero;
        using (ProcessingHeartbeat stage = new("Reading and classifying OSM features"))
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Feature? feature = dataSource.GetNextFeature(ref layerHandle, ref progress, null!, "");
                if (feature is null) break;
                string layerName = feature.GetDefnRef().GetName();
                // The OSM driver must consume its node/point stream to assemble
                // complete ways and relations, but LIDEX neither exports nor
                // renders standalone points. Avoid geometry wrappers, topology
                // checks, tag classification, and styling for millions of nodes.
                if (string.Equals(layerName, "points", StringComparison.OrdinalIgnoreCase))
                {
                    ReportOsmReadProgress(progress, featuresRead, ref nextReadPercent);
                    continue;
                }
                using Geometry? geometry = feature.GetGeometryRef();
                if (geometry is null) continue;
                featuresRead++;
                geodataBuilder?.Collect(feature, geometry);
                int sourcePartSequence = 0;
                CollectOsmGeometry(
                    geometry, GetOsmStyle(feature), StableOsmDrawSortKey(feature),
                    ref sourcePartSequence, primitives);
                // GDAL can report 100% before its relation/feature stream is
                // actually exhausted. Reserve the formal 100% checkpoint for EOF.
                ReportOsmReadProgress(progress, featuresRead, ref nextReadPercent);
            }
            if (nextReadPercent <= 100)
            {
                WriteOsmLogEntry(
                    $"OSM source read: 100% ({featuresRead:N0} route features classified).",
                    indent: 4);
            }
            stage.Complete();
        }
        geodataBuilder?.WriteAndPromote();
        primitives.Sort((left, right) =>
        {
            int order = left.Style.DrawOrder.CompareTo(right.Style.DrawOrder);
            if (order != 0) return order;
            order = StringComparer.Ordinal.Compare(left.SourceSortKey, right.SourceSortKey);
            return order != 0 ? order : left.SourcePartSequence.CompareTo(right.SourcePartSequence);
        });
        return primitives;
    }

    private static void ReportOsmReadProgress(double progress, int featuresRead, ref int nextReadPercent)
    {
        // GDAL can report 100% before its relation/feature stream is actually
        // exhausted. Reserve the formal 100% checkpoint for EOF.
        int readPercent = Math.Clamp((int)Math.Floor(progress * 100.0), 0, 99);
        if (readPercent < nextReadPercent) return;
        WriteOsmLogEntry(
            $"OSM source read: {readPercent}% ({featuresRead:N0} route features classified).",
            indent: 4);
        while (nextReadPercent <= readPercent) nextReadPercent += 10;
    }

    private static void CollectOsmGeometry(
        Geometry geometry,
        OsmStyle style,
        string sourceSortKey,
        ref int sourcePartSequence,
        List<OsmPrimitive> destination)
    {
        wkbGeometryType type = geometry.GetGeometryType();
        int childCount = geometry.GetGeometryCount();
        if (type is wkbGeometryType.wkbPolygon or wkbGeometryType.wkbPolygon25D)
        {
            if (childCount == 0) return;
            using Geometry exterior = geometry.GetGeometryRef(0);
            OsmPoint[] exteriorPoints = ReadOsmPoints(exterior, minimumPoints: 3,
                out double minLon, out double minLat, out double maxLon, out double maxLat);
            if (exteriorPoints.Length == 0) return;
            List<OsmPoint[]> innerRings = [];
            for (int ringIndex = 1; ringIndex < childCount; ringIndex++)
            {
                using Geometry inner = geometry.GetGeometryRef(ringIndex);
                OsmPoint[] points = ReadOsmPoints(inner, minimumPoints: 3,
                    out _, out _, out _, out _);
                if (points.Length > 0) innerRings.Add(points);
            }
            destination.Add(new OsmPrimitive(
                style, sourceSortKey, sourcePartSequence++, exteriorPoints, innerRings.ToArray(), true,
                minLon, minLat, maxLon, maxLat));
            return;
        }
        if (childCount > 0 && type is not wkbGeometryType.wkbLineString and not wkbGeometryType.wkbLinearRing)
        {
            for (int i = 0; i < childCount; i++)
            {
                using Geometry child = geometry.GetGeometryRef(i);
                CollectOsmGeometry(child, style, sourceSortKey, ref sourcePartSequence, destination);
            }
            return;
        }
        OsmPoint[] linePoints = ReadOsmPoints(geometry, minimumPoints: 2,
            out double lineMinLon, out double lineMinLat, out double lineMaxLon, out double lineMaxLat);
        if (linePoints.Length == 0) return;
        destination.Add(new OsmPrimitive(
            style, sourceSortKey, sourcePartSequence++, linePoints, [], false,
            lineMinLon, lineMinLat, lineMaxLon, lineMaxLat));
    }

    private static OsmPoint[] ReadOsmPoints(
        Geometry geometry,
        int minimumPoints,
        out double minLon,
        out double minLat,
        out double maxLon,
        out double maxLat)
    {
        int pointCount = geometry.GetPointCount();
        if (pointCount < minimumPoints)
        {
            minLon = minLat = maxLon = maxLat = 0.0;
            return [];
        }
        OsmPoint[] points = new OsmPoint[pointCount];
        minLon = double.PositiveInfinity;
        minLat = double.PositiveInfinity;
        maxLon = double.NegativeInfinity;
        maxLat = double.NegativeInfinity;
        for (int i = 0; i < pointCount; i++)
        {
            double lon = geometry.GetX(i);
            double lat = geometry.GetY(i);
            points[i] = new OsmPoint(lon, lat);
            minLon = Math.Min(minLon, lon);
            minLat = Math.Min(minLat, lat);
            maxLon = Math.Max(maxLon, lon);
            maxLat = Math.Max(maxLat, lat);
        }
        return points;
    }

    private static string StableOsmDrawSortKey(Feature feature)
    {
        string osmId = GetOgrField(feature, "osm_id");
        if (!string.IsNullOrWhiteSpace(osmId))
            return osmId.Contains('/') ? osmId.Trim() : "relation/" + osmId.Trim();
        string osmWayId = GetOgrField(feature, "osm_way_id");
        if (!string.IsNullOrWhiteSpace(osmWayId))
            return osmWayId.Contains('/') ? osmWayId.Trim() : "way/" + osmWayId.Trim();
        return "fid/" + feature.GetFID().ToString("D20", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static OsmStyle GetOsmStyle(Feature feature)
    {
        string highway = GetOgrField(feature, "highway");
        string railway = GetOgrField(feature, "railway");
        string waterway = GetOgrField(feature, "waterway");
        string landuse = GetOgrField(feature, "landuse");
        string natural = GetOgrTag(feature, "natural").Trim().ToLowerInvariant();
        string building = GetOgrField(feature, "building");
        string leisure = GetOgrField(feature, "leisure");
        string amenity = GetOgrField(feature, "amenity");
        string aeroway = GetOgrField(feature, "aeroway");
        string shop = GetOgrField(feature, "shop");

        // Colors, widths, and layer priorities follow TSRE MapDataOSM::draw and
        // OSMFeatures::LAYER. TSRE stores a feature in ways[9-layer], so a
        // larger OSM layer is painted earlier. Filled areas use the base order;
        // linear detail uses the following slot so rivers remain visible over
        // their water polygons instead of being obscured by adjacent land use.
        if (!string.IsNullOrEmpty(building)) return FillStyle(190, 173, 173, TsreDrawOrder(1), 169, 148, 165);
        if (!string.IsNullOrEmpty(shop)) return FillStyle(200, 170, 170, TsreDrawOrder(1), 169, 148, 165);
        if (natural == "tree_row")
        {
            float widthPixels = (float)(TreeRowWidthMetres * MapImageSize / OrtsTileSizeMeters);
            return LineStyle(NaturalWoodRed, NaturalWoodGreen, NaturalWoodBlue, widthPixels, TsreDrawOrder(6));
        }
        PolyVegClassification? polyVeg = GetPolyVegClassification(feature);
        if (polyVeg is not null)
            return FillStyle(polyVeg.FillRed, polyVeg.FillGreen, polyVeg.FillBlue, polyVeg.DrawOrder);
        if (natural == "water") return FillStyle(181, 208, 208, TsreDrawOrder(6));
        if (natural == "bay") return FillStyle(181, 208, 208, TsreDrawOrder(7));
        if (landuse == "reservoir") return FillStyle(181, 208, 208, TsreDrawOrder(5));
        if (landuse == "basin") return FillStyle(181, 208, 208, TsreDrawOrder(6));
        if (natural == "beach") return FillStyle(254, 240, 186, TsreDrawOrder(6));
        if (landuse is "industrial" or "railway") return FillStyle(222, 208, 213, TsreDrawOrder(7));
        if (landuse == "commercial") return FillStyle(238, 200, 200, TsreDrawOrder(7));
        if (landuse == "retail") return FillStyle(234, 214, 214, TsreDrawOrder(7));
        if (landuse == "residential") return FillStyle(220, 220, 220, TsreDrawOrder(8));
        if (landuse == "quarry") return FillStyle(195, 195, 195, TsreDrawOrder(7));
        if (landuse == "garages") return FillStyle(224, 224, 206, TsreDrawOrder(6));
        if (landuse == "landfill") return FillStyle(176, 176, 142, TsreDrawOrder(6));
        if (landuse is "construction" or "greenfield" or "brownfield") return FillStyle(176, 176, 142, TsreDrawOrder(7));
        if (amenity == "parking") return FillStyle(246, 238, 182, TsreDrawOrder(5));
        if (amenity == "school") return FillStyle(240, 240, 216, TsreDrawOrder(6), 210, 180, 160);
        if (amenity == "place_of_worship") return FillStyle(220, 130, 110, TsreDrawOrder(2), 150, 150, 150);
        if (aeroway == "terminal") return FillStyle(204, 153, 254, TsreDrawOrder(1), 154, 117, 182);
        if (aeroway is "runway" or "taxiway") return LineStyle(187, 187, 204, 10, TsreDrawOrder(4, 1));
        if (!string.IsNullOrEmpty(waterway)) return LineStyle(181, 208, 208, 10, TsreDrawOrder(6, 1));

        if (railway == "rail") return LineStyle(70, 70, 70, 4, TsreDrawOrder(2, 2));
        if (railway == "tram") return LineStyle(90, 90, 90, 2, TsreDrawOrder(2, 2));
        if (!string.IsNullOrEmpty(railway)) return LineStyle(50, 50, 50, 2, TsreDrawOrder(2, 1));
        if (!string.IsNullOrEmpty(highway))
        {
            Color border = Color.FromArgb(180, 180, 180);
            return highway switch
            {
                "motorway" or "trunk" => CasedLine(255, 69, 0, 12, border, 14, TsreDrawOrder(2, 3)),
                "motorway_link" or "trunk_link" => CasedLine(255, 89, 20, 8, border, 10, TsreDrawOrder(3, 3)),
                "primary" => CasedLine(228, 109, 113, 10, border, 12, TsreDrawOrder(3, 3)),
                "primary_link" => CasedLine(228, 129, 133, 8, border, 10, TsreDrawOrder(3, 3)),
                "secondary" => CasedLine(253, 191, 111, 10, border, 12, TsreDrawOrder(3, 3)),
                "secondary_link" => CasedLine(253, 211, 121, 8, border, 10, TsreDrawOrder(4, 3)),
                "tertiary" => CasedLine(252, 250, 116, 10, border, 12, TsreDrawOrder(3, 3)),
                "tertiary_link" => CasedLine(252, 255, 136, 8, border, 10, TsreDrawOrder(4, 3)),
                "residential" or "construction" => CasedLine(254, 254, 254, 10, border, 12, TsreDrawOrder(4, 3)),
                "unclassified" => CasedLine(254, 254, 254, 6, border, 8, TsreDrawOrder(4, 3)),
                "service" => CasedLine(254, 254, 254, 4, border, 6, TsreDrawOrder(4, 3)),
                "footway" => CasedLine(254, 200, 200, 2, border, 4, TsreDrawOrder(5, 3)),
                "bridleway" => CasedLine(200, 254, 200, 2, border, 4, TsreDrawOrder(5, 3)),
                "steps" => CasedLine(254, 100, 100, 2, border, 4, TsreDrawOrder(5, 3)),
                "cycleway" => CasedLine(200, 200, 254, 2, border, 4, TsreDrawOrder(4, 3)),
                "track" => CasedLine(220, 220, 220, 2, border, 4, TsreDrawOrder(4, 3)),
                "path" => CasedLine(230, 230, 230, 2, border, 4, TsreDrawOrder(5, 3)),
                "byway" or "pedestrian" or "living_street" => CasedLine(230, 230, 230, 2, border, 4, TsreDrawOrder(4, 3)),
                _ => LineStyle(50, 50, 50, 1, TsreDrawOrder(4, 3))
            };
        }
        return LineStyle(50, 50, 50, 1, 50);
    }

    private static PolyVegClassification? GetPolyVegClassification(Feature feature)
    {
        string natural = GetOgrField(feature, "natural").Trim().ToLowerInvariant();
        string landuse = GetOgrField(feature, "landuse").Trim().ToLowerInvariant();
        string leisure = GetOgrField(feature, "leisure").Trim().ToLowerInvariant();
        string tourism = GetOgrField(feature, "tourism").Trim().ToLowerInvariant();
        if (natural == "wood") return new(
            "woodland", "natural=wood", TsreDrawOrder(7), NaturalWoodRed, NaturalWoodGreen, NaturalWoodBlue);
        if (landuse is "forest" or "wood") return new("woodland", $"landuse={landuse}", TsreDrawOrder(7), 133, 193, 133);
        if (natural == "scrub") return new("scrub", "natural=scrub", TsreDrawOrder(7), 181, 226, 181);
        if (natural == "wetland") return new("wetland", "natural=wetland", TsreDrawOrder(7), 95, 180, 160);
        if (natural == "heath") return new("heath", "natural=heath", TsreDrawOrder(7), 213, 216, 159);
        if (natural == "grassland") return new("grassland", "natural=grassland", TsreDrawOrder(7), 198, 228, 180);
        if (landuse is "grass" or "meadow" or "pasture") return new("grassland", $"landuse={landuse}", TsreDrawOrder(7), 207, 236, 168);
        if (landuse == "farmland") return new("agriculture", "landuse=farmland", TsreDrawOrder(8), 233, 216, 189);
        if (landuse == "farmyard") return new("agriculture", "landuse=farmyard", TsreDrawOrder(6), 220, 190, 146);
        if (landuse == "farm") return new("agriculture", "landuse=farm", TsreDrawOrder(7), 234, 216, 184);
        if (landuse == "greenhouse_horticulture") return new("agriculture", "landuse=greenhouse_horticulture", TsreDrawOrder(6), 231, 241, 222);
        if (landuse is "orchard" or "vineyard") return new("orchard", $"landuse={landuse}", TsreDrawOrder(7), 207, 255, 168);
        if (landuse is "plant_nursery" or "allotments") return new("orchard", $"landuse={landuse}", TsreDrawOrder(7), 204, 220, 112);
        if (landuse is "village_green" or "recreation_ground") return new("parkland", $"landuse={landuse}", TsreDrawOrder(7), 207, 236, 168);
        if (leisure == "park") return new("parkland", "leisure=park", TsreDrawOrder(7), 206, 246, 202);
        if (leisure == "common") return new("parkland", "leisure=common", TsreDrawOrder(5), 199, 241, 163);
        if (leisure == "garden") return new("parkland", "leisure=garden", TsreDrawOrder(6), 199, 241, 163);
        if (leisure == "golf_course") return new("golf_course", "leisure=golf_course", TsreDrawOrder(5), 199, 241, 163);
        if (landuse == "cemetery") return new("cemetery", "landuse=cemetery", TsreDrawOrder(7), 151, 191, 164);
        if (leisure == "sports_centre") return new("sports", "leisure=sports_centre", TsreDrawOrder(7), 206, 246, 202);
        if (leisure == "stadium") return new("sports", "leisure=stadium", TsreDrawOrder(5), 206, 246, 202);
        if (leisure == "pitch") return new("sports", "leisure=pitch", TsreDrawOrder(4), 137, 210, 174);
        if (leisure == "track") return new("sports", "leisure=track", TsreDrawOrder(6), 116, 219, 185);
        if (leisure == "playground") return new("sports", "leisure=playground", TsreDrawOrder(4), 204, 254, 254);
        if (tourism == "zoo") return new("zoo", "tourism=zoo", TsreDrawOrder(6), 164, 242, 161);
        return null;
    }

    private static int TsreDrawOrder(int osmLayer, int withinLayer = 0)
        => ((9 - Math.Clamp(osmLayer, 0, 9)) * 10) + withinLayer;

    private static OsmStyle FillStyle(int r, int g, int b, int order, int strokeR = -1, int strokeG = -1, int strokeB = -1)
        => new(Color.FromArgb(r, g, b), strokeR < 0 ? Color.Empty : Color.FromArgb(strokeR, strokeG, strokeB), 1, Color.Empty, 0, order);

    private static OsmStyle LineStyle(int r, int g, int b, float width, int order)
        => new(Color.Empty, Color.FromArgb(r, g, b), width, Color.Empty, 0, order);

    private static OsmStyle CasedLine(int r, int g, int b, float width, Color casing, float casingWidth, int order)
        => new(Color.Empty, Color.FromArgb(r, g, b), width, casing, casingWidth, order);

    private static string GetOgrField(Feature feature, string name)
    {
        int index = feature.GetFieldIndex(name);
        return index >= 0 && feature.IsFieldSetAndNotNull(index) ? feature.GetFieldAsString(index) ?? "" : "";
    }

    private static string GetOgrTag(Feature feature, string name)
    {
        string direct = GetOgrField(feature, name);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        string otherTags = GetOgrField(feature, "other_tags");
        if (string.IsNullOrEmpty(otherTags))
        {
            return "";
        }

        string marker = $"\"{name}\"=>\"";
        int start = otherTags.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return "";
        }

        start += marker.Length;
        StringBuilder value = new();
        for (int index = start; index < otherTags.Length; index++)
        {
            char character = otherTags[index];
            if (character == '\\' && index + 1 < otherTags.Length)
            {
                value.Append(otherTags[++index]);
                continue;
            }

            if (character == '"')
            {
                return value.ToString();
            }

            value.Append(character);
        }

        return "";
    }

    private static int DrawOsmPrimitive(Graphics graphics, GeoTileMapper mapper, WorldTile tile, OsmPrimitive primitive)
    {
        PointF[] points = ProjectOsmRing(mapper, tile, primitive.Points);
        if (primitive.IsPolygon && primitive.Style.Fill && points.Length >= 3)
        {
            using SolidBrush brush = new(primitive.Style.FillColor);
            using GraphicsPath path = new(FillMode.Alternate);
            path.AddPolygon(points);
            foreach (OsmPoint[] innerRing in primitive.InnerRings)
            {
                PointF[] innerPoints = ProjectOsmRing(mapper, tile, innerRing);
                if (innerPoints.Length >= 3) path.AddPolygon(innerPoints);
            }
            graphics.FillPath(brush, path);
        }
        if (primitive.Style.CasingColor != Color.Empty && primitive.Style.CasingWidth > 0)
        {
            using Pen casing = new(primitive.Style.CasingColor, primitive.Style.CasingWidth) { LineJoin = LineJoin.Round, StartCap = LineCap.Flat, EndCap = LineCap.Flat };
            if (primitive.IsPolygon) graphics.DrawPolygon(casing, points);
            else graphics.DrawLines(casing, points);
        }
        if (primitive.Style.StrokeColor != Color.Empty && primitive.Style.StrokeWidth > 0)
        {
            using Pen pen = new(primitive.Style.StrokeColor, primitive.Style.StrokeWidth) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (primitive.IsPolygon && points.Length >= 3)
            {
                graphics.DrawPolygon(pen, points);
                foreach (OsmPoint[] innerRing in primitive.InnerRings)
                {
                    PointF[] innerPoints = ProjectOsmRing(mapper, tile, innerRing);
                    if (innerPoints.Length >= 3) graphics.DrawPolygon(pen, innerPoints);
                }
            }
            else graphics.DrawLines(pen, points);
        }
        return 1;
    }

    private static PointF[] ProjectOsmRing(GeoTileMapper mapper, WorldTile tile, OsmPoint[] ring)
    {
        PointF[] points = new PointF[ring.Length];
        for (int i = 0; i < ring.Length; i++)
        {
            (double x, double y) = mapper.ProjectToTilePixel(
                tile, ring[i].Longitude, ring[i].Latitude, MapImageSize);
            points[i] = new PointF((float)x, (float)y);
        }
        return points;
    }

    private static void RunMapPolygonHoleProbe()
    {
        using Geometry outer = new(wkbGeometryType.wkbLinearRing);
        outer.AddPoint_2D(4, 4);
        outer.AddPoint_2D(60, 4);
        outer.AddPoint_2D(60, 60);
        outer.AddPoint_2D(4, 60);
        outer.AddPoint_2D(4, 4);
        using Geometry inner = new(wkbGeometryType.wkbLinearRing);
        inner.AddPoint_2D(20, 20);
        inner.AddPoint_2D(44, 20);
        inner.AddPoint_2D(44, 44);
        inner.AddPoint_2D(20, 44);
        inner.AddPoint_2D(20, 20);
        using Geometry polygon = new(wkbGeometryType.wkbPolygon);
        polygon.AddGeometry(outer);
        polygon.AddGeometry(inner);
        List<OsmPrimitive> primitives = [];
        int sequence = 0;
        CollectOsmGeometry(
            polygon, FillStyle(181, 208, 208, 60), "probe/water", ref sequence, primitives);
        if (primitives.Count != 1 || primitives[0].InnerRings.Length != 1 || !primitives[0].IsPolygon)
            throw new InvalidOperationException("map polygon hole was flattened during OSM collection");

        using Bitmap bitmap = new(64, 64, PixelFormat.Format24bppRgb);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        using GraphicsPath path = new(FillMode.Alternate);
        path.AddPolygon(primitives[0].Points.Select(point =>
            new PointF((float)point.Longitude, (float)point.Latitude)).ToArray());
        foreach (OsmPoint[] ring in primitives[0].InnerRings)
        {
            path.AddPolygon(ring.Select(point =>
                new PointF((float)point.Longitude, (float)point.Latitude)).ToArray());
        }
        using SolidBrush brush = new(Color.FromArgb(181, 208, 208));
        graphics.FillPath(brush, path);
        if (bitmap.GetPixel(10, 10).ToArgb() != brush.Color.ToArgb() ||
            bitmap.GetPixel(32, 32).ToArgb() != Color.White.ToArgb())
            throw new InvalidOperationException("map polygon renderer filled an inner island ring");
    }

    // Direct port of TSRE AceLib::save: uncompressed planar RGB, no legacy tool.
    private static void WriteTsreAce(string path, Bitmap bitmap)
    {
        using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("SIMISA@@@@@@@@@@"));
        writer.Write(1);
        writer.Write(0);
        writer.Write(bitmap.Width);
        writer.Write(bitmap.Height);
        writer.Write(14);
        writer.Write(3);
        writer.Write(0);
        for (int i = 0; i < 31; i++) writer.Write(0);
        foreach (int value in new[] { 8, 0, 3, 0, 8, 0, 4, 0, 8, 0, 5, 0 }) writer.Write(value);
        int offset = (bitmap.Height * 4) + 200;
        for (int row = 0; row < bitmap.Height; row++) writer.Write(offset + (row * bitmap.Width * 3 * 4));

        Rectangle rectangle = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] sourceRow = new byte[Math.Abs(data.Stride)];
            byte[] plane = new byte[bitmap.Width];
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr rowPointer = IntPtr.Add(data.Scan0, y * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(rowPointer, sourceRow, 0, sourceRow.Length);
                for (int channel = 2; channel >= 0; channel--)
                {
                    for (int x = 0; x < bitmap.Width; x++) plane[x] = sourceRow[(x * 3) + channel];
                    writer.Write(plane);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static byte[] CreateMapPatchedTerrainBytes(byte[] source, string aceName)
    {
        if (source.Length < 40 || Encoding.ASCII.GetString(source, 0, 8) != "SIMISA@@")
        {
            throw new InvalidDataException("terrain .t is not a supported binary SIMISA terrain file");
        }
        int terrainPosition = 32;
        if (BitConverter.ToUInt16(source, terrainPosition) != TokenTerrain)
        {
            throw new InvalidDataException("terrain .t does not begin with token 136");
        }
        int terrainEnd = terrainPosition + 8 + checked((int)BitConverter.ToUInt32(source, terrainPosition + 4));
        (int materialStart, int materialEnd) = FindDirectChildBlock(source, terrainPosition + 9, terrainEnd, 151);
        byte[] materialBlock = RebuildMaterialBlock(source.AsSpan(materialStart, materialEnd - materialStart), aceName, out int materialIndex);

        using MemoryStream assembled = new(source.Length + materialBlock.Length + 512);
        assembled.Write(source, 0, materialStart);
        assembled.Write(materialBlock);
        assembled.Write(source, materialEnd, source.Length - materialEnd);
        byte[] result = assembled.ToArray();
        int delta = materialBlock.Length - (materialEnd - materialStart);
        WriteUInt32(result, terrainPosition + 4, checked((uint)(BitConverter.ToUInt32(source, terrainPosition + 4) + delta)));
        PatchMapTerrainUvs(result, materialIndex);
        return result;
    }

    private static (int Start, int End) FindDirectChildBlock(byte[] bytes, int start, int end, ushort wanted)
    {
        int position = start;
        while (position + 9 <= end)
        {
            ushort token = BitConverter.ToUInt16(bytes, position);
            int blockEnd = position + 8 + checked((int)BitConverter.ToUInt32(bytes, position + 4));
            if (blockEnd > end || blockEnd <= position) break;
            if (token == wanted) return (position, blockEnd);
            position = blockEnd;
        }
        throw new InvalidDataException($"terrain .t token {wanted} was not found");
    }

    private static byte[] RebuildMaterialBlock(ReadOnlySpan<byte> block, string aceName, out int materialIndex)
    {
        int blockEnd = block.Length;
        int payload = 9 + (block[8] * 2);
        int count = BitConverter.ToInt32(block.Slice(payload, 4));
        if (count <= 0 || (count & 1) != 0) throw new InvalidDataException("terrain material count is invalid");
        int normalCount = count / 2;
        List<byte[]> children = [];
        int position = payload + 4;
        while (position + 9 <= blockEnd && children.Count < count)
        {
            int end = position + 8 + checked((int)BitConverter.ToUInt32(block.Slice(position + 4, 4)));
            if (end > blockEnd || end <= position) throw new InvalidDataException("terrain material block is truncated");
            children.Add(block.Slice(position, end - position).ToArray());
            position = end;
        }
        if (children.Count != count) throw new InvalidDataException("terrain material entries are incomplete");

        byte[] aceUtf16 = Encoding.Unicode.GetBytes(aceName);
        List<byte[]> normalChildren = children.Take(normalCount).ToList();
        List<byte[]> alphaChildren = children.Skip(normalCount).ToList();
        List<int> existingMapMaterials = normalChildren
            .Select((child, index) => (child, index))
            .Where(item => item.child.AsSpan().IndexOf(aceUtf16) >= 0)
            .Select(item => item.index)
            .ToList();

        if (existingMapMaterials.Count > 0)
        {
            // A reset may leave the old map material unreferenced. Reuse it.
            // Older/repeated runs may also have appended duplicates; retain one
            // paired normal/alpha entry so the terrain table cannot accumulate.
            for (int duplicate = existingMapMaterials.Count - 1; duplicate >= 1; duplicate--)
            {
                int index = existingMapMaterials[duplicate];
                normalChildren.RemoveAt(index);
                alphaChildren.RemoveAt(index);
            }
            materialIndex = existingMapMaterials[0];
            if (existingMapMaterials.Count == 1)
            {
                return block.ToArray();
            }
        }
        else
        {
            materialIndex = normalChildren.Count;
            normalChildren.Add(BuildTerrainMaterial("DetailTerrain", [(aceName, 1, 0), ("microtex.ace", 1, 1)], [[1, 0, 0, 0], [2, 0, 1, BitConverter.SingleToInt32Bits(512f)]]));
            alphaChildren.Add(BuildTerrainMaterial("AlphaTerrain", [(aceName, 1, 0)], [[1, 0, 0, 0]]));
        }

        using MemoryStream payloadStream = new();
        using (BinaryWriter writer = new(payloadStream, Encoding.Unicode, true))
        {
            writer.Write(normalChildren.Count + alphaChildren.Count);
            foreach (byte[] child in normalChildren) writer.Write(child);
            foreach (byte[] child in alphaChildren) writer.Write(child);
        }
        return BuildTokenBlock(151, payloadStream.ToArray());
    }

    private static byte[] BuildTerrainMaterial(string shader, (string Name, int A, int B)[] textures, int[][] slots)
    {
        using MemoryStream texturePayload = new();
        using (BinaryWriter writer = new(texturePayload, Encoding.Unicode, true))
        {
            writer.Write(textures.Length);
            foreach ((string name, int a, int b) in textures)
            {
                using MemoryStream item = new();
                using (BinaryWriter iw = new(item, Encoding.Unicode, true))
                {
                    WriteUnicodeString(iw, name);
                    iw.Write(a);
                    iw.Write(b);
                }
                writer.Write(BuildTokenBlock(154, item.ToArray()));
            }
        }
        using MemoryStream slotPayload = new();
        using (BinaryWriter writer = new(slotPayload, Encoding.Unicode, true))
        {
            writer.Write(slots.Length);
            foreach (int[] slot in slots)
            {
                using MemoryStream item = new();
                using (BinaryWriter iw = new(item, Encoding.Unicode, true)) foreach (int value in slot) iw.Write(value);
                writer.Write(BuildTokenBlock(156, item.ToArray()));
            }
        }
        using MemoryStream material = new();
        using (BinaryWriter writer = new(material, Encoding.Unicode, true))
        {
            WriteUnicodeString(writer, shader);
            writer.Write(BuildTokenBlock(153, texturePayload.ToArray()));
            writer.Write(BuildTokenBlock(155, slotPayload.ToArray()));
        }
        return BuildTokenBlock(152, material.ToArray());
    }

    private static byte[] BuildTokenBlock(ushort token, byte[] payload)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.Unicode, true);
        writer.Write((int)token);
        writer.Write(payload.Length + 1);
        writer.Write((byte)0);
        writer.Write(payload);
        return stream.ToArray();
    }

    private static void WriteUnicodeString(BinaryWriter writer, string value)
    {
        writer.Write((ushort)value.Length);
        writer.Write(Encoding.Unicode.GetBytes(value));
    }

    private static void PatchMapTerrainUvs(byte[] bytes, int materialIndex)
    {
        int patchIndex = 0;
        WalkBinaryTokens(bytes, 32, bytes.Length, 0, (token, payload, blockEnd) =>
        {
            if (token != TokenTerrainPatchsetPatch || payload + 56 > blockEnd || patchIndex >= TerrainPatchGridSize * TerrainPatchGridSize) return;
            int patchX = patchIndex % TerrainPatchGridSize;
            int patchY = patchIndex / TerrainPatchGridSize;
            float textureStep = 1f / TerrainPatchGridSize;
            WriteInt32(bytes, payload + 28, materialIndex);
            WriteSingle(bytes, payload + 32, textureStep * patchX);
            WriteSingle(bytes, payload + 36, textureStep * patchY);
            WriteSingle(bytes, payload + 40, textureStep / TerrainPatchGridSize);
            WriteSingle(bytes, payload + 44, 0);
            WriteSingle(bytes, payload + 48, 0);
            WriteSingle(bytes, payload + 52, textureStep / TerrainPatchGridSize);
            patchIndex++;
        });
        if (patchIndex != TerrainPatchGridSize * TerrainPatchGridSize)
        {
            throw new InvalidDataException($"terrain .t contains {patchIndex} patch records; expected 256");
        }
    }

    private static void WriteInt32(byte[] bytes, int offset, int value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4);
    private static void WriteUInt32(byte[] bytes, int offset, uint value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4);

    private static string FormatModified(DateTimeOffset? modified) => modified is null ? "" : $", updated {modified.Value:yyyy-MM-dd}";
}
