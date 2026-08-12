// SCO LIDEX - Geofabrik/OpenStreetMap terrain-map generation for TSRE/Open Rails.
// This implementation is self-contained and follows TSRE's ACE image layout.

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
using GdalDataset = OSGeo.GDAL.Dataset;

namespace ORterr;

internal static partial class Program
{
    private const int MapImageSize = 4096;
    private const int MapTileParallelism = 2;
    private const string GeofabrikIndexUrl = "https://download.geofabrik.de/index-v1.json";

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
        MapSourceAvailability source = await ScanMapTileSourceAsync(route.RouteDir, mapper, tiles, cancellationToken);
        if (!source.CanRun)
        {
            throw new InvalidOperationException(source.Detail);
        }
    }

    private static async Task<MapSourceAvailability> ScanMapTileSourceAsync(
        string routeDir,
        GeoTileMapper mapper,
        IReadOnlyList<TerrainTile> selectedTiles,
        CancellationToken cancellationToken)
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
            IReadOnlyList<(double Lon, double Lat)> coveragePoints = GetMapCoveragePoints(mapper, selectedTiles);
            GeofabrikResolution resolution = await ResolveGeofabrikRegionAsync(client, routeDir, mapper, coveragePoints, cacheOnly: false, cancellationToken);
            GeofabrikRegion region = resolution.Region;
            Console.WriteLine("Map tiles: enabled; 4096x4096 image per normal 2048 m terrain tile.");
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
                !resolution.RemoteIndexAvailable || !resolution.RemoteExtractAvailable,
                resolution.Detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException)
        {
            Console.WriteLine($"Map source: FAILED ({ex.Message}).");
            return new MapSourceAvailability(false, false, true, ex.Message);
        }
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
        IReadOnlyList<(double Lon, double Lat)> coveragePoints = GetMapCoveragePoints(mapper, tiles);
        GeofabrikResolution resolution = await ResolveGeofabrikRegionAsync(client, route.RouteDir, mapper, coveragePoints, cacheOnly, cancellationToken);
        if (!resolution.CanRun)
        {
            throw new InvalidOperationException($"Geofabrik map source is unavailable and no usable cached extract exists: {resolution.Detail}");
        }
        GeofabrikRegion region = resolution.Region;
        string pbfPath = await EnsureGeofabrikExtractAsync(
            client,
            route.RouteDir,
            region,
            resolution.CachedExtractPath,
            cacheOnly || resolution.CacheOnly,
            cancellationToken);
        string terrainMapsDir = Path.Combine(route.RouteDir, "terrain_maps");
        Directory.CreateDirectory(terrainMapsDir);

        Console.WriteLine($"\nCreating {tiles.Count:N0} TSRE terrain map tile(s) from {region.Name}...");
        Console.WriteLine($"PBF cache: {pbfPath}");
        Console.WriteLine("STATUS: OSM - PROCESSING");
        ConfigureOsmRuntime();
        int completed = 0;
        int started = 0;
        using GdalDataset dataSource = Gdal.OpenEx(pbfPath, (uint)(GdalConst.OF_VECTOR | GdalConst.OF_READONLY), null, null, null)
            ?? throw new InvalidOperationException("GDAL could not open the Geofabrik .osm.pbf extract");
        List<OsmPrimitive> mapGeometry = LoadOsmGeometry(dataSource, mapper, tiles, cancellationToken);
        long pointCount = mapGeometry.Sum(p => (long)p.Points.Length);
        long estimatedBytes = (pointCount * 16L) + (mapGeometry.Count * 96L);
        Console.WriteLine($"OSM geometry retained for the complete run: {tiles.Count:N0} tile(s), {mapGeometry.Count:N0} geometry part(s), {pointCount:N0} points, estimated geometry memory {FormatByteCount(estimatedBytes)}.");
        Console.WriteLine("STATUS: OSM - MAKING MAPS");

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
                Console.WriteLine($"  [{sequence:N0}/{tiles.Count:N0}] {baseName}: rendering aligned OSM geometry...");
                using Bitmap bitmap = RenderMapBitmap(mapGeometry, mapper, worldTile, tileCancellationToken, out int renderedParts);
                Console.WriteLine(renderedParts > 0
                    ? $"    Rendered OSM geometry parts: {renderedParts:N0}"
                    : "    No mapped OSM features in this tile; applying the TSRE map background.");
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

        Console.WriteLine($"Map tiles complete: {completed:N0} TSRE F3 PNG cache file(s) created; terrain .t files unchanged.");
        Console.WriteLine("Existing F3 PNG cache files with matching tile names were overwritten.");
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SCO-LIDEX/1.200 (Open Rails terrain builder)");
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

    private static async Task<string> EnsureGeofabrikExtractAsync(
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
            return preferredCachedPath;
        }

        // A route-local file has priority even if a caller did not resolve a
        // manifest first. Existing extracts are intentionally not compared to
        // the latest remote size; purging is the user's refresh operation.
        if (IsUsableCacheFile(routePath))
        {
            Console.WriteLine("Using current route Geofabrik extract.");
            return routePath;
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
        return finalPath;
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
    private sealed record OsmPrimitive(OsmStyle Style, OsmPoint[] Points, double MinLon, double MinLat, double MaxLon, double MaxLat);

    private static List<OsmPrimitive> LoadOsmGeometry(
        GdalDataset dataSource,
        GeoTileMapper mapper,
        IReadOnlyList<TerrainTile> tiles,
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
        Console.WriteLine("OSM relation integrity: node stream unfiltered; completed geometry filtered to the terrain batch.");
        dataSource.ResetReading();
        List<OsmPrimitive> primitives = [];
        double progress = 0;
        IntPtr layerHandle = IntPtr.Zero;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Feature? feature = dataSource.GetNextFeature(ref layerHandle, ref progress, null!, "");
            if (feature is null) break;
            using Geometry? geometry = feature.GetGeometryRef();
            if (geometry is null) continue;
            CollectOsmGeometry(geometry, GetOsmStyle(feature), primitives);
        }
        primitives.Sort((left, right) => left.Style.DrawOrder.CompareTo(right.Style.DrawOrder));
        return primitives;
    }

    private static void CollectOsmGeometry(Geometry geometry, OsmStyle style, List<OsmPrimitive> destination)
    {
        wkbGeometryType type = geometry.GetGeometryType();
        int childCount = geometry.GetGeometryCount();
        if (childCount > 0 && type is not wkbGeometryType.wkbLineString and not wkbGeometryType.wkbLinearRing)
        {
            for (int i = 0; i < childCount; i++)
            {
                using Geometry child = geometry.GetGeometryRef(i);
                CollectOsmGeometry(child, style, destination);
            }
            return;
        }
        int pointCount = geometry.GetPointCount();
        if (pointCount < 2) return;
        OsmPoint[] points = new OsmPoint[pointCount];
        double minLon = double.PositiveInfinity;
        double minLat = double.PositiveInfinity;
        double maxLon = double.NegativeInfinity;
        double maxLat = double.NegativeInfinity;
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
        destination.Add(new OsmPrimitive(style, points, minLon, minLat, maxLon, maxLat));
    }

    private static OsmStyle GetOsmStyle(Feature feature)
    {
        string highway = GetOgrField(feature, "highway");
        string railway = GetOgrField(feature, "railway");
        string waterway = GetOgrField(feature, "waterway");
        string landuse = GetOgrField(feature, "landuse");
        string natural = GetOgrField(feature, "natural");
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
        if (natural == "wood") return FillStyle(141, 196, 108, TsreDrawOrder(7));
        if (landuse is "forest" or "wood") return FillStyle(133, 193, 133, TsreDrawOrder(7));
        if (natural == "water") return FillStyle(181, 208, 208, TsreDrawOrder(6));
        if (natural == "bay") return FillStyle(181, 208, 208, TsreDrawOrder(7));
        if (landuse == "reservoir") return FillStyle(181, 208, 208, TsreDrawOrder(5));
        if (landuse == "basin") return FillStyle(181, 208, 208, TsreDrawOrder(6));
        if (natural == "scrub") return FillStyle(181, 226, 181, TsreDrawOrder(7));
        if (natural == "wetland") return FillStyle(95, 180, 160, TsreDrawOrder(7));
        if (natural == "heath") return FillStyle(213, 216, 159, TsreDrawOrder(7));
        if (natural == "beach") return FillStyle(254, 240, 186, TsreDrawOrder(6));
        if (natural == "grassland") return FillStyle(198, 228, 180, TsreDrawOrder(7));
        if (landuse is "grass" or "village_green" or "recreation_ground" or "meadow") return FillStyle(207, 236, 168, TsreDrawOrder(7));
        if (landuse == "farmland") return FillStyle(233, 216, 189, TsreDrawOrder(8));
        if (landuse == "farmyard") return FillStyle(220, 190, 146, TsreDrawOrder(6));
        if (landuse == "farm") return FillStyle(234, 216, 184, TsreDrawOrder(7));
        if (landuse == "orchard") return FillStyle(207, 255, 168, TsreDrawOrder(7));
        if (landuse is "industrial" or "railway") return FillStyle(222, 208, 213, TsreDrawOrder(7));
        if (landuse == "commercial") return FillStyle(238, 200, 200, TsreDrawOrder(7));
        if (landuse == "retail") return FillStyle(234, 214, 214, TsreDrawOrder(7));
        if (landuse == "residential") return FillStyle(220, 220, 220, TsreDrawOrder(8));
        if (landuse == "quarry") return FillStyle(195, 195, 195, TsreDrawOrder(7));
        if (landuse == "cemetery") return FillStyle(151, 191, 164, TsreDrawOrder(7));
        if (landuse == "garages") return FillStyle(224, 224, 206, TsreDrawOrder(6));
        if (landuse == "greenhouse_horticulture") return FillStyle(231, 241, 222, TsreDrawOrder(6));
        if (landuse is "plant_nursery" or "allotments") return FillStyle(204, 220, 112, TsreDrawOrder(7));
        if (landuse == "landfill") return FillStyle(176, 176, 142, TsreDrawOrder(6));
        if (landuse is "construction" or "greenfield" or "brownfield") return FillStyle(176, 176, 142, TsreDrawOrder(7));
        if (amenity == "parking") return FillStyle(246, 238, 182, TsreDrawOrder(5));
        if (amenity == "school") return FillStyle(240, 240, 216, TsreDrawOrder(6), 210, 180, 160);
        if (amenity == "place_of_worship") return FillStyle(220, 130, 110, TsreDrawOrder(2), 150, 150, 150);
        if (leisure is "sports_centre" or "park") return FillStyle(206, 246, 202, TsreDrawOrder(7));
        if (leisure == "stadium") return FillStyle(206, 246, 202, TsreDrawOrder(5));
        if (leisure == "pitch") return FillStyle(137, 210, 174, TsreDrawOrder(4), 180, 180, 180);
        if (leisure == "track") return FillStyle(116, 219, 185, TsreDrawOrder(6), 180, 180, 180);
        if (leisure == "playground") return FillStyle(204, 254, 254, TsreDrawOrder(4), 180, 180, 180);
        if (leisure == "common") return FillStyle(199, 241, 163, TsreDrawOrder(5), 148, 214, 151);
        if (leisure == "garden") return FillStyle(199, 241, 163, TsreDrawOrder(6), 148, 214, 151);
        if (leisure == "golf_course") return FillStyle(199, 241, 163, TsreDrawOrder(5), 148, 214, 151);
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

    private static int DrawOsmPrimitive(Graphics graphics, GeoTileMapper mapper, WorldTile tile, OsmPrimitive primitive)
    {
        int pointCount = primitive.Points.Length;
        PointF[] points = new PointF[pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            (double x, double y) = mapper.ProjectToTilePixel(tile, primitive.Points[i].Longitude, primitive.Points[i].Latitude, MapImageSize);
            points[i] = new PointF((float)x, (float)y);
        }
        if (primitive.Style.Fill && pointCount >= 3)
        {
            using SolidBrush brush = new(primitive.Style.FillColor);
            graphics.FillPolygon(brush, points, FillMode.Winding);
        }
        if (primitive.Style.CasingColor != Color.Empty && primitive.Style.CasingWidth > 0)
        {
            using Pen casing = new(primitive.Style.CasingColor, primitive.Style.CasingWidth) { LineJoin = LineJoin.Round, StartCap = LineCap.Flat, EndCap = LineCap.Flat };
            graphics.DrawLines(casing, points);
        }
        if (primitive.Style.StrokeColor != Color.Empty && primitive.Style.StrokeWidth > 0)
        {
            using Pen pen = new(primitive.Style.StrokeColor, primitive.Style.StrokeWidth) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            if (primitive.Style.Fill && pointCount >= 3) graphics.DrawPolygon(pen, points);
            else graphics.DrawLines(pen, points);
        }
        return 1;
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
