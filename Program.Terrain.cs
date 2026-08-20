// SCO LIDEX - Open Rails / MSTS Cloud Terrain Builder
// Copyright (C) Scott Brunner, Beast of Burden
// DEM acquisition, normal terrain generation, merging, and terrain file encoding.
// SCO LIDEX is distributed under GNU GPL v3 or later. See LICENSE.txt.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MaxRev.Gdal.Core;
using OSGeo.GDAL;
using OSGeo.OGR;
using OSGeo.OSR;

namespace ORterr;

internal static partial class Program
{
    // Public DEM products occasionally contain finite, undeclared sentinel
    // values. Reject them before interpolation so the normal source fallback
    // path can replace the affected posts instead of encoding terrain walls.
    private const double MinimumPlausibleDemElevationMeters = -1000.0;
    private const double MaximumPlausibleDemElevationMeters = 10000.0;
    private const double MaximumPlausibleDemCellDeltaMeters = 1500.0;
    private const string AllSamplesNoDataFailure = "all sampled pixels were nodata";
    private const string WindowOutsideRasterFailure = "window outside raster";

    private static void PrintRouteSummary(
        RouteLayout route,
        TerrainOutputResolution terrainResolution)
    {
        Console.WriteLine($"Route: {route.RouteDir}");
        Console.WriteLine($"Tiles: {route.TerrainTiles.Count}");
        Console.WriteLine($"World files: {route.WorldTiles.Count}");

        if (route.StartTile is not null)
        {
            Console.WriteLine($"RouteStart tile: X={route.StartTile.Value.X}, Z={route.StartTile.Value.Z}");
        }

        if (route.Markers.Count > 0)
        {
            double minLon = route.Markers.Min(m => m.Longitude);
            double maxLon = route.Markers.Max(m => m.Longitude);
            double minLat = route.Markers.Min(m => m.Latitude);
            double maxLat = route.Markers.Max(m => m.Latitude);

            Console.WriteLine($"Markers bbox: lon {minLon:F6}..{maxLon:F6}, lat {minLat:F6}..{maxLat:F6}");
        }

        if (route.TsreProjection is not null)
        {
            Console.WriteLine("Alternate projection detection: TsreGeoProjection found in .trk.");
        }
        else
        {
            Console.WriteLine("Alternate projection detection: TsreGeoProjection not found; using standard route projection.");
        }

        Console.WriteLine("\nImportant: this route uses external *_y.raw height grids.");
        int gridSize = TerrainGridSize(terrainResolution);
        Console.WriteLine(
            $"Selected raw grid size is {gridSize}x{gridSize} int16 samples " +
            $"for {TerrainOutputLabel(terrainResolution)}, not inline text heights.");
    }

    private static void PrintProjectionSummary(GeoTileMapper mapper)
    {
        Console.WriteLine($"Estimated route DEM bbox: lon {mapper.MinLon:F6}..{mapper.MaxLon:F6}, lat {mapper.MinLat:F6}..{mapper.MaxLat:F6}");
        Console.WriteLine($"Projection mode: {mapper.ProjectionName}");
        Console.WriteLine(mapper.ProjectionDetail);
    }

    // Fill one ORTS 256-post terrain grid. The order is intentional:
    // 1m first, then 5m~ Original Product Resolution, 10m, and finally the
    // key-free Copernicus 30m global source for posts outside US coverage.
    // Confirmed no-coverage results may advance to the next source. Temporary
    // service/query failures must not silently lower a tile's source quality.
    private static async Task<TerrainGenerationResult> StreamOrtsGridForSampleGridAsync(
        HttpClient client,
        GeoSampleGrid sampleGrid,
        DemSourcePolicy sourcePolicy)
    {
        List<string> failures = [];
        int gridHeight = sampleGrid.Longitudes.GetLength(0);
        int gridWidth = sampleGrid.Longitudes.GetLength(1);
        short[,] mergedHeights = CreateMissingHeightGrid(gridWidth, gridHeight);
        DemWindowSearchResult primarySearch = new([], SourceHiccup: false);
        int primarySamplesUsed = 0;
        if (sourcePolicy.UsePrimary)
        {
            primarySearch = await ReadDemWindowsForDatasetAsync(client, sampleGrid, PrimaryDemDataset, failures);
            primarySamplesUsed = MergeWindows(primarySearch.Windows, mergedHeights);
        }

        int missingAfterPrimary = mergedHeights.Cast<short>().Count(v => v == RawMissingHeight);
        ThrowIfSourceHiccupLeftMissingSamples(primarySearch, PrimaryDemLabel, missingAfterPrimary);
        int intermediateSamplesUsed = 0;
        int fallbackSamplesUsed = 0;
        int globalSamplesUsed = 0;
        if (missingAfterPrimary > 0 && sourcePolicy.UseIntermediate)
        {
            Console.WriteLine($"  -> {PrimaryDemLabel} coverage left {missingAfterPrimary:N0} missing samples; trying {IntermediateDemLabel} fallback ({IntermediateDemDataset}).");
            DemWindowSearchResult intermediateSearch = await ReadDemWindowsForDatasetAsync(client, sampleGrid, IntermediateDemDataset, failures);
            intermediateSamplesUsed = MergeWindows(intermediateSearch.Windows, mergedHeights);
            int missingAfterIntermediateSearch = mergedHeights.Cast<short>().Count(v => v == RawMissingHeight);
            ThrowIfSourceHiccupLeftMissingSamples(intermediateSearch, IntermediateDemLabel, missingAfterIntermediateSearch);
        }

        int missingAfterIntermediate = mergedHeights.Cast<short>().Count(v => v == RawMissingHeight);
        if (missingAfterIntermediate > 0 && sourcePolicy.UseFallback)
        {
            Console.WriteLine($"  -> {IntermediateDemLabel} coverage left {missingAfterIntermediate:N0} missing samples; trying {FallbackDemLabel} fallback ({FallbackDemDataset}).");
            DemWindowSearchResult fallbackSearch = await ReadDemWindowsForDatasetAsync(client, sampleGrid, FallbackDemDataset, failures);
            fallbackSamplesUsed = MergeWindows(fallbackSearch.Windows, mergedHeights);
            int missingAfterFallbackSearch = mergedHeights.Cast<short>().Count(v => v == RawMissingHeight);
            ThrowIfSourceHiccupLeftMissingSamples(fallbackSearch, FallbackDemLabel, missingAfterFallbackSearch);
        }

        int missingAfterFallback = mergedHeights.Cast<short>().Count(v => v == RawMissingHeight);
        if (missingAfterFallback > 0 && sourcePolicy.UseGlobal)
        {
            Console.WriteLine(
                $"  -> {FallbackDemLabel} coverage left {missingAfterFallback:N0} missing samples; " +
                $"trying {GlobalDemLabel} fallback ({GlobalDemDisplayName}, AWS Open Data, low resolution DSM).");
            globalSamplesUsed = MergeWindows(ReadCopernicusDemWindows(sampleGrid, failures), mergedHeights);
        }

        int missingBeforeFill = mergedHeights.Cast<short>().Count(v => v == RawMissingHeight);
        if (missingBeforeFill == gridWidth * gridHeight)
        {
            throw new InvalidOperationException("GDAL could not read a DEM window from USGS or Copernicus GLO-30. " + string.Join(" | ", failures.Take(6)));
        }

        if (missingBeforeFill > 0)
        {
            Console.WriteLine($"  -> Mosaic still missing {missingBeforeFill:N0} samples after fallback; filling from neighbors.");
            FillMissingHeights(mergedHeights);
        }

        return new TerrainGenerationResult(mergedHeights, primarySamplesUsed, intermediateSamplesUsed, fallbackSamplesUsed, globalSamplesUsed, missingBeforeFill);
    }

    private static void ThrowIfSourceHiccupLeftMissingSamples(
        DemWindowSearchResult search,
        string sourceLabel,
        int missingSamples)
    {
        if (!search.SourceHiccup || missingSamples == 0)
        {
            return;
        }

        throw new RetryableDemSourceException(
            $"temporary {sourceLabel} source hiccup left {missingSamples:N0} samples unresolved; " +
            "lower-resolution fallback was not accepted. " +
            (search.HiccupDetail ?? "The source did not return a usable response."));
    }

    // Ask USGS which GeoTIFF products overlap the route tile's bbox, filter the
    // resulting URLs, then ask GDAL to read only the raster windows needed.
    private static async Task<DemWindowSearchResult> ReadDemWindowsForDatasetAsync(
        HttpClient client,
        GeoSampleGrid sampleGrid,
        string datasetName,
        List<string> failures)
    {
        (double minLon, double minLat, double maxLon, double maxLat) = sampleGrid.BoundingBox;
        string apiUrl =
            "https://tnmaccess.nationalmap.gov/api/v1/products" +
            $"?bbox={minLon.ToString(CultureInfo.InvariantCulture)},{minLat.ToString(CultureInfo.InvariantCulture)},{maxLon.ToString(CultureInfo.InvariantCulture)},{maxLat.ToString(CultureInfo.InvariantCulture)}" +
            $"&prodFormats=GeoTIFF&outputFormat=JSON&datasets={Uri.EscapeDataString(datasetName)}";

        string jsonResponse;
        try
        {
            jsonResponse = await GetStringWithRetryAsync(client, apiUrl, datasetName);
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or InvalidOperationException)
        {
            string detail = $"product search failed for bbox lon {minLon:F6}..{maxLon:F6}, lat {minLat:F6}..{maxLat:F6}: {ex.Message}";
            return ReadCachedDemWindowsAfterSourceHiccup(datasetName, sampleGrid, failures, detail);
        }

        JsonDocument doc;
        try
        {
            doc = ParseUsgsProductJson(jsonResponse);
        }
        catch (InvalidOperationException ex)
        {
            string detail = $"product search returned unusable data: {ex.Message}";
            return ReadCachedDemWindowsAfterSourceHiccup(datasetName, sampleGrid, failures, detail);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            {
                string detail = "product search response did not contain a usable items list.";
                return ReadCachedDemWindowsAfterSourceHiccup(datasetName, sampleGrid, failures, detail);
            }

            if (items.GetArrayLength() == 0)
            {
                failures.Add($"No {GetDemSourceDisplayName(datasetName)} product found for tile bbox lon {minLon:F6}..{maxLon:F6}, lat {minLat:F6}..{maxLat:F6}.");
                Console.WriteLine($"  -> No {GetDemSourceDisplayName(datasetName)} coverage was returned for this tile; lower-resolution fallback is permitted.");
                return new DemWindowSearchResult([], SourceHiccup: false);
            }

            List<string> urls = [];
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (item.TryGetProperty("downloadURL", out JsonElement urlElement))
                {
                    string? url = urlElement.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        urls.Add(url);
                    }
                }
            }

            if (urls.Count == 0)
            {
                string detail = "products were returned but none contained a downloadable GeoTIFF URL.";
                return ReadCachedDemWindowsAfterSourceHiccup(datasetName, sampleGrid, failures, detail);
            }

            urls = FilterDemProductUrls(datasetName, urls);
            AddCachedProductUrls(datasetName, urls);
            int failuresBeforeRead = failures.Count;
            List<DemWindow> windows = ReadDemWindowsFromUrls(urls, datasetName, sampleGrid, failures);
            bool readHiccup = failures.Count > failuresBeforeRead;
            return new DemWindowSearchResult(
                windows,
                SourceHiccup: readHiccup,
                HiccupDetail: readHiccup
                    ? string.Join(" | ", failures.Skip(failuresBeforeRead).Take(3))
                    : null);
        }
    }

    private static DemWindowSearchResult ReadCachedDemWindowsAfterSourceHiccup(
        string datasetName,
        GeoSampleGrid sampleGrid,
        List<string> failures,
        string detail)
    {
        failures.Add($"{datasetName} {detail}");
        List<string> cachedUrls = FilterDemProductUrls(datasetName, GetCachedProductUrls(datasetName));
        if (cachedUrls.Count == 0)
        {
            Console.WriteLine(
                $"  -> USGS {GetDemSourceDisplayName(datasetName)} source hiccup; " +
                $"no cached product URLs are available: {detail}");
            return new DemWindowSearchResult([], SourceHiccup: true, HiccupDetail: detail);
        }

        Console.WriteLine(
            $"  -> USGS {GetDemSourceDisplayName(datasetName)} source hiccup; " +
            $"trying {cachedUrls.Count:N0} cached product URL(s) before marking the tile for retry.");
        int failuresBeforeRead = failures.Count;
        List<DemWindow> windows = ReadDemWindowsFromUrls(cachedUrls, datasetName, sampleGrid, failures);
        string cacheFailureDetail = failures.Count > failuresBeforeRead
            ? string.Join(" | ", failures.Skip(failuresBeforeRead).Take(3))
            : detail;
        return new DemWindowSearchResult(windows, SourceHiccup: true, HiccupDetail: cacheFailureDetail);
    }

    private static async Task<string> GetStringWithRetryAsync(HttpClient client, string apiUrl, string datasetName)
    {
        const int attempts = 3;
        Exception? lastError = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                string response = await client.GetStringAsync(apiUrl);
                AddUsgsDataBytes(Encoding.UTF8.GetByteCount(response));
                return response;
            }
            catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
            {
                lastError = ex;
                if (attempt == attempts)
                {
                    break;
                }

                int delaySeconds = attempt * 3;
                Console.WriteLine($"  -> USGS {GetDemSourceDisplayName(datasetName)} search attempt {attempt:N0}/{attempts:N0} failed: {ex.Message}; retrying in {delaySeconds:N0}s.");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }
        }

        throw new InvalidOperationException(lastError?.Message ?? "USGS product search failed.");
    }

    private static string GetDemSourceDisplayName(string datasetName)
    {
        if (string.Equals(datasetName, PrimaryDemDataset, StringComparison.OrdinalIgnoreCase))
        {
            return $"{PrimaryDemLabel} ({PrimaryDemDataset})";
        }

        if (string.Equals(datasetName, IntermediateDemDataset, StringComparison.OrdinalIgnoreCase))
        {
            return $"{IntermediateDemLabel} ({IntermediateDemDataset})";
        }

        if (string.Equals(datasetName, FallbackDemDataset, StringComparison.OrdinalIgnoreCase))
        {
            return $"{FallbackDemLabel} ({FallbackDemDataset})";
        }

        return datasetName;
    }

    private static List<DemWindow> ReadDemWindowsFromUrls(
        IReadOnlyList<string> urls,
        string datasetName,
        GeoSampleGrid sampleGrid,
        List<string> failures)
    {
        List<DemWindow> windows = [];
        foreach (string url in urls)
        {
            Dataset? ds = null;
            string productName = Path.GetFileName(new Uri(url).LocalPath);
            try
            {
                ds = Gdal.Open("/vsicurl/" + url, Access.GA_ReadOnly);
                if (ds is null && url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ds = Gdal.Open("/vsizip//vsicurl/" + url, Access.GA_ReadOnly);
                }
            }
            catch (Exception ex)
            {
                failures.Add(productName + ": open failed: " + ex.Message);
                continue;
            }

            if (ds is null)
            {
                failures.Add(productName + ": open failed");
                continue;
            }

            using (ds)
            {
                bool readOk;
                short[,] heights;
                int missing;
                string failure;
                try
                {
                    // GDAL streams these remote files through /vsicurl/. SCO LIDEX
                    // reads only the raster window needed for the current terrain
                    // grid and records that requested window size in the run total.
                    readOk = TryReadDatasetSampleGrid(ds, sampleGrid, fillMissing: false, AddUsgsDataBytes, out heights, out missing, out failure);
                }
                catch (Exception ex)
                {
                    failures.Add(productName + ": read failed: " + ex.Message);
                    continue;
                }

                if (readOk)
                {
                    int totalSamples = heights.GetLength(0) * heights.GetLength(1);
                    int valid = totalSamples - missing;
                    windows.Add(new DemWindow(productName, heights, valid));
                    Console.WriteLine($"  -> {GetDemSourceDisplayName(datasetName)} can contribute {valid:N0} / {totalSamples:N0} samples: {productName}");
                    continue;
                }

                if (IsDemCoverageGapFailure(failure))
                {
                    Console.WriteLine(
                        $"  -> {GetDemSourceDisplayName(datasetName)} has no usable samples " +
                        $"in this part of {productName}; lower-resolution fallback remains available.");
                    continue;
                }

                failures.Add(Path.GetFileName(new Uri(url).LocalPath) + ": " + failure);
            }
        }

        return windows;
    }

    private static bool IsDemCoverageGapFailure(string failure)
    {
        return string.Equals(
                failure, AllSamplesNoDataFailure, StringComparison.Ordinal) ||
            failure.StartsWith(
                WindowOutsideRasterFailure, StringComparison.Ordinal);
    }

    private static List<string> FilterDemProductUrls(string datasetName, IReadOnlyList<string> urls)
    {
        if (!string.Equals(datasetName, FallbackDemDataset, StringComparison.OrdinalIgnoreCase))
        {
            return urls.ToList();
        }

        Dictionary<string, (string Url, int Date)> newestByCell = new(StringComparer.OrdinalIgnoreCase);
        List<string> ungrouped = [];
        foreach (string url in urls)
        {
            string fileName = Path.GetFileName(new Uri(url).LocalPath);
            Match match = OneThirdArcSecondProductRegex().Match(fileName);
            if (!match.Success)
            {
                ungrouped.Add(url);
                continue;
            }

            string cell = match.Groups["cell"].Value;
            int date = int.Parse(match.Groups["date"].Value, CultureInfo.InvariantCulture);
            if (!newestByCell.TryGetValue(cell, out (string Url, int Date) existing) || date > existing.Date)
            {
                newestByCell[cell] = (url, date);
            }
        }

        List<string> filtered = newestByCell
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => kvp.Value.Url)
            .Concat(ungrouped)
            .ToList();

        if (filtered.Count < urls.Count)
        {
            Console.WriteLine($"  -> Filtered {GetDemSourceDisplayName(datasetName)} products from {urls.Count:N0} URLs to {filtered.Count:N0} newest cell URL(s).");
        }

        return filtered;
    }

    private static void AddCachedProductUrls(string datasetName, IEnumerable<string> urls)
    {
        if (!ProductUrlCache.TryGetValue(datasetName, out HashSet<string>? cachedUrls))
        {
            cachedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ProductUrlCache[datasetName] = cachedUrls;
        }

        bool changed = false;
        foreach (string url in urls)
        {
            changed |= cachedUrls.Add(url);
        }

        if (changed)
        {
            SaveProductUrlCache();
        }
    }

    private static void LoadProductUrlCache(string path)
    {
        ProductUrlCache.Clear();
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            Dictionary<string, string[]>? cache = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path));
            if (cache is null)
            {
                return;
            }

            foreach ((string datasetName, string[] urls) in cache)
            {
                ProductUrlCache[datasetName] = new HashSet<string>(urls.Where(u => !string.IsNullOrWhiteSpace(u)), StringComparer.OrdinalIgnoreCase);
            }

            int urlCount = ProductUrlCache.Values.Sum(urls => urls.Count);
            if (urlCount > 0)
            {
                Console.WriteLine($"Loaded {urlCount:N0} cached USGS product URLs from {path}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not read USGS product cache {path}: {ex.Message}");
        }
    }

    private static void SaveProductUrlCache()
    {
        if (string.IsNullOrWhiteSpace(ProductUrlCachePath))
        {
            return;
        }

        try
        {
            Dictionary<string, string[]> cache = ProductUrlCache.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.OrderBy(url => url, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
            JsonSerializerOptions options = new() { WriteIndented = true };
            File.WriteAllText(ProductUrlCachePath, JsonSerializer.Serialize(cache, options), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not write USGS product cache {ProductUrlCachePath}: {ex.Message}");
        }
    }

    private static List<string> GetCachedProductUrls(string datasetName)
    {
        return ProductUrlCache.TryGetValue(datasetName, out HashSet<string>? cachedUrls)
            ? cachedUrls.ToList()
            : [];
    }

    private static int MergeWindows(IEnumerable<DemWindow> windows, short[,] mergedHeights)
    {
        int totalUsed = 0;
        foreach (DemWindow window in windows.OrderByDescending(w => w.ValidSamples))
        {
            int used = MergeDemWindow(mergedHeights, window.Heights);
            totalUsed += used;
            Console.WriteLine($"  -> Mosaic used {used:N0} samples from {window.ProductName}");
            if (!mergedHeights.Cast<short>().Any(v => v == RawMissingHeight))
            {
                break;
            }
        }

        return totalUsed;
    }

    private static JsonDocument ParseUsgsProductJson(string jsonResponse)
    {
        try
        {
            return JsonDocument.Parse(jsonResponse);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("USGS product service returned invalid JSON.", ex);
        }
    }

    private static bool TryReadDatasetSampleGrid(
        Dataset ds,
        GeoSampleGrid sampleGrid,
        bool fillMissing,
        Action<long> addDataBytes,
        out short[,] heights,
        out int missing,
        out string failure)
    {
        heights = CreateMissingHeightGrid(sampleGrid.Longitudes.GetLength(1), sampleGrid.Longitudes.GetLength(0));
        missing = 0;
        failure = "";

        double[] geoTransform = new double[6];
        ds.GetGeoTransform(geoTransform);

        if (Math.Abs(geoTransform[1]) < double.Epsilon || Math.Abs(geoTransform[5]) < double.Epsilon)
        {
            failure = "invalid geotransform";
            return false;
        }

        // Convert each route terrain post from lon/lat into the DEM's native
        // pixel coordinates. One padded raster window is read, then each post
        // is bilinearly sampled from that local window.
        using DatasetCoordinateMapper coordinateMapper = new(ds);
        int gridHeight = sampleGrid.Longitudes.GetLength(0);
        int gridWidth = sampleGrid.Longitudes.GetLength(1);
        double[,] rasterXs = new double[gridHeight, gridWidth];
        double[,] rasterYs = new double[gridHeight, gridWidth];
        double left = double.PositiveInfinity;
        double right = double.NegativeInfinity;
        double bottom = double.PositiveInfinity;
        double top = double.NegativeInfinity;

        for (int y = 0; y < gridHeight; y++)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                (double datasetX, double datasetY) = coordinateMapper.Transform(sampleGrid.Longitudes[y, x], sampleGrid.Latitudes[y, x]);
                rasterXs[y, x] = ((datasetX - geoTransform[0]) / geoTransform[1]) - 0.5;
                rasterYs[y, x] = ((datasetY - geoTransform[3]) / geoTransform[5]) - 0.5;
                left = Math.Min(left, rasterXs[y, x]);
                right = Math.Max(right, rasterXs[y, x]);
                bottom = Math.Min(bottom, rasterYs[y, x]);
                top = Math.Max(top, rasterYs[y, x]);
            }
        }

        int xOff = (int)Math.Floor(left) - 1;
        int xEnd = (int)Math.Ceiling(right) + 2;
        int yOff = (int)Math.Floor(bottom) - 1;
        int yEnd = (int)Math.Ceiling(top) + 2;

        int xMin = Math.Clamp(Math.Min(xOff, xEnd), 0, ds.RasterXSize - 1);
        int xMax = Math.Clamp(Math.Max(xOff, xEnd), 1, ds.RasterXSize);
        int yMin = Math.Clamp(Math.Min(yOff, yEnd), 0, ds.RasterYSize - 1);
        int yMax = Math.Clamp(Math.Max(yOff, yEnd), 1, ds.RasterYSize);

        int width = xMax - xMin;
        int height = yMax - yMin;
        if (width <= 1 || height <= 1)
        {
            failure = $"window outside raster; raster={ds.RasterXSize}x{ds.RasterYSize}, gt=[{string.Join(",", geoTransform.Select(v => v.ToString("G6", CultureInfo.InvariantCulture)))}], px=({xOff},{yOff})-({xEnd},{yEnd})";
            return false;
        }

        Band elevationBand = ds.GetRasterBand(1);
        float[] samples = new float[width * height];
        elevationBand.ReadRaster(xMin, yMin, width, height, samples, width, height, 0, 0);
        addDataBytes((long)width * height * sizeof(float));

        double? noData = TryGetNoDataValue(elevationBand);
        RasterElevationTransform elevationTransform =
            GetRasterElevationTransform(ds, elevationBand);
        if (Math.Abs(elevationTransform.UnitToMeters - 1.0) > 0.0000001)
        {
            Console.WriteLine(
                $"  -> DEM vertical units: {elevationTransform.UnitName}; " +
                $"converting source elevations to metres.");
        }

        for (int y = 0; y < gridHeight; y++)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                if (!TryBilinearSample(
                        samples,
                        width,
                        height,
                        xMin,
                        yMin,
                        rasterXs[y, x],
                        rasterYs[y, x],
                        noData,
                        elevationTransform,
                        out double sample))
                {
                    heights[y, x] = RawMissingHeight;
                    missing++;
                    continue;
                }

                heights[y, x] = ClampToInt16Meters(sample);
            }
        }

        if (fillMissing)
        {
            FillMissingHeights(heights);
        }

        failure = missing >= gridWidth * gridHeight ? AllSamplesNoDataFailure : "";
        return missing < gridWidth * gridHeight;
    }

    private static RasterElevationTransform GetRasterElevationTransform(
        Dataset ds,
        Band elevationBand)
    {
        elevationBand.GetScale(out double bandScale, out int hasScale);
        elevationBand.GetOffset(out double bandOffset, out int hasOffset);
        bandScale = hasScale == 0 ? 1.0 : bandScale;
        bandOffset = hasOffset == 0 ? 0.0 : bandOffset;

        string unitName = elevationBand.GetUnitType()?.Trim() ?? "";
        double unitToMeters = TryGetUnitScaleToMeters(unitName, out double bandUnitScale)
            ? bandUnitScale
            : 1.0;

        // USGS OPR products commonly omit the raster-band unit while retaining
        // their original projected CRS. In that case the elevation samples use
        // the CRS linear unit too (for example Tennessee State Plane ftUS).
        if (string.IsNullOrWhiteSpace(unitName))
        {
            string projection = ds.GetProjection();
            if (!string.IsNullOrWhiteSpace(projection))
            {
                using SpatialReference spatialReference = new("");
                spatialReference.ImportFromWkt(ref projection);
                if (spatialReference.IsProjected() == 1)
                {
                    double crsUnitToMeters = spatialReference.GetLinearUnits();
                    string crsUnitName = spatialReference.GetLinearUnitsName();
                    if (double.IsFinite(crsUnitToMeters) &&
                        crsUnitToMeters > 0)
                    {
                        unitName = crsUnitName;
                        unitToMeters = crsUnitToMeters;
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(unitName))
        {
            unitName = "metres (assumed)";
        }

        return new RasterElevationTransform(
            bandScale * unitToMeters,
            bandOffset * unitToMeters,
            unitToMeters,
            unitName);
    }

    private static bool TryGetUnitScaleToMeters(
        string unitName,
        out double unitToMeters)
    {
        string normalized = unitName.Trim().ToLowerInvariant();
        if (normalized is "m" or "meter" or "meters" or "metre" or "metres")
        {
            unitToMeters = 1.0;
            return true;
        }

        if (normalized.Contains("survey foot", StringComparison.Ordinal) ||
            normalized.Contains("foot_us", StringComparison.Ordinal) ||
            normalized.Contains("ftus", StringComparison.Ordinal) ||
            normalized.Contains("us-ft", StringComparison.Ordinal))
        {
            unitToMeters = 1200.0 / 3937.0;
            return true;
        }

        if (normalized is "ft" or "foot" or "feet" ||
            normalized.Contains("international foot", StringComparison.Ordinal))
        {
            unitToMeters = 0.3048;
            return true;
        }

        unitToMeters = 1.0;
        return false;
    }

    private static short[,] CreateMissingHeightGrid()
    {
        return CreateMissingHeightGrid(OrtsRawGridSize, OrtsRawGridSize);
    }

    private static short[,] CreateMissingHeightGrid(int width, int height)
    {
        short[,] grid = new short[height, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                grid[y, x] = RawMissingHeight;
            }
        }

        return grid;
    }

    private static int MergeDemWindow(short[,] target, short[,] source)
    {
        int used = 0;
        int height = Math.Min(target.GetLength(0), source.GetLength(0));
        int width = Math.Min(target.GetLength(1), source.GetLength(1));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                short value = source[y, x];
                if (value == RawMissingHeight)
                {
                    continue;
                }

                if (target[y, x] != RawMissingHeight)
                {
                    continue;
                }

                target[y, x] = value;
                used++;
            }
        }

        return used;
    }

    private static bool TryBilinearSample(
        float[] samples,
        int width,
        int height,
        int xOrigin,
        int yOrigin,
        double rasterX,
        double rasterY,
        double? noData,
        RasterElevationTransform elevationTransform,
        out double value)
    {
        value = 0;
        double localX = rasterX - xOrigin;
        double localY = rasterY - yOrigin;
        int x0 = (int)Math.Floor(localX);
        int y0 = (int)Math.Floor(localY);
        int x1 = x0 + 1;
        int y1 = y0 + 1;

        if (x0 < 0 || y0 < 0 || x1 >= width || y1 >= height)
        {
            return false;
        }

        if (!TryConvertElevationSample(
                samples[(y0 * width) + x0],
                noData,
                elevationTransform,
                out double q00) ||
            !TryConvertElevationSample(
                samples[(y0 * width) + x1],
                noData,
                elevationTransform,
                out double q10) ||
            !TryConvertElevationSample(
                samples[(y1 * width) + x0],
                noData,
                elevationTransform,
                out double q01) ||
            !TryConvertElevationSample(
                samples[(y1 * width) + x1],
                noData,
                elevationTransform,
                out double q11))
        {
            return false;
        }

        double minimum = Math.Min(Math.Min(q00, q10), Math.Min(q01, q11));
        double maximum = Math.Max(Math.Max(q00, q10), Math.Max(q01, q11));
        if (maximum - minimum > MaximumPlausibleDemCellDeltaMeters)
        {
            return false;
        }

        double fx = localX - x0;
        double fy = localY - y0;
        double top = q00 + ((q10 - q00) * fx);
        double bottom = q01 + ((q11 - q01) * fx);
        value = top + ((bottom - top) * fy);
        return true;
    }

    private static bool TryConvertElevationSample(
        float sample,
        double? noData,
        RasterElevationTransform elevationTransform,
        out double valueMeters)
    {
        valueMeters = 0;
        if (!float.IsFinite(sample) ||
            (noData is not null && Math.Abs(sample - noData.Value) < 0.001))
        {
            return false;
        }

        valueMeters =
            (sample * elevationTransform.ValueScale) +
            elevationTransform.ValueOffset;
        return double.IsFinite(valueMeters) &&
            valueMeters >= MinimumPlausibleDemElevationMeters &&
            valueMeters <= MaximumPlausibleDemElevationMeters;
    }

    private static (double X, double Y) TransformLonLatToDataset(Dataset ds, double lon, double lat)
    {
        string projection = ds.GetProjection();
        if (string.IsNullOrWhiteSpace(projection))
        {
            return (lon, lat);
        }

        SpatialReference target = new("");
        target.ImportFromWkt(ref projection);
        target.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        if (target.IsGeographic() == 1)
        {
            return (lon, lat);
        }

        SpatialReference source = new("");
        source.ImportFromEPSG(4326);
        source.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
        using CoordinateTransformation transform = new(source, target);
        double[] point = [lon, lat, 0];
        transform.TransformPoint(point);
        return (point[0], point[1]);
    }

    // Wraps GDAL/OSR coordinate transformations for a source DEM. Each USGS
    // product may have its own projection; this class maps WGS84 lon/lat sample
    // posts into that raster's native coordinate system.
    private sealed class DatasetCoordinateMapper : IDisposable
    {
        private readonly SpatialReference? source;
        private readonly SpatialReference? target;
        private readonly CoordinateTransformation? transform;

        public DatasetCoordinateMapper(Dataset dataset)
        {
            string projection = dataset.GetProjection();
            if (string.IsNullOrWhiteSpace(projection))
            {
                return;
            }

            target = new SpatialReference("");
            target.ImportFromWkt(ref projection);
            target.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            if (target.IsGeographic() == 1)
            {
                return;
            }

            source = new SpatialReference("");
            source.ImportFromEPSG(4326);
            source.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            transform = new CoordinateTransformation(source, target);
        }

        public (double X, double Y) Transform(double lon, double lat)
        {
            if (transform is null)
            {
                return (lon, lat);
            }

            double[] point = [lon, lat, 0];
            transform.TransformPoint(point);
            return (point[0], point[1]);
        }

        public void Dispose()
        {
            transform?.Dispose();
            source?.Dispose();
            target?.Dispose();
        }
    }

    private static double? TryGetNoDataValue(Band band)
    {
        band.GetNoDataValue(out double value, out int hasValue);
        return hasValue == 0 ? null : value;
    }

    private static void WriteGeneratedTiles(string outputDir, IEnumerable<GeneratedTile> tiles, bool cleanTileTemplate)
    {
        Directory.CreateDirectory(outputDir);
        List<GeneratedTile> tileList = tiles.ToList();
        Dictionary<string, float[,]> patchHeights = BuildMergedPatchHeights(tileList);

        foreach (GeneratedTile generated in tileList)
        {
            WriteGeneratedTile(outputDir, generated, patchHeights[generated.Tile.TileFile.Name], cleanTileTemplate);
        }
    }

    private static void WriteGeneratedTile(string outputDir, GeneratedTile generated, float[,] patchHeights, bool cleanTileTemplate)
    {
        string tileOutputName = generated.Tile.TileFile.Name.ToLowerInvariant();
        string rawOutputName = Path.GetFileName(generated.Tile.RawHeightPath ?? "").ToLowerInvariant();
        string tileOutputPath = Path.Combine(outputDir, tileOutputName);
        string rawOutputPath = Path.Combine(outputDir, rawOutputName);
        TerrainSampleEncoding encoding = CalculateSampleEncoding(generated.Heights);

        byte[] tileBytes = CreatePatchedTerrainTileBytes(generated, patchHeights, encoding, cleanTileTemplate);
        PatchTerrainResolutionMetadata(
            tileBytes, OrtsRawGridSize, (float)OrtsPostSpacingMeters);
        EnsureExactFileNameCasing(tileOutputPath);
        EnsureExactFileNameCasing(rawOutputPath);
        File.WriteAllBytes(tileOutputPath, tileBytes);
        WriteRawGrid(rawOutputPath, generated.Heights, encoding);
    }

    private static byte[] CreatePatchedTerrainTileBytes(GeneratedTile generated, float[,] patchHeights, TerrainSampleEncoding encoding, bool cleanTileTemplate)
    {
        if (cleanTileTemplate)
        {
            return CreatePatchedCleanTemplateTileBytes(generated, patchHeights, encoding);
        }

        byte[] existingBytes = File.ReadAllBytes(generated.Tile.TileFile.FullName);
        existingBytes = NormalizeLegacyMapTerrainMaterial(
            existingBytes, generated.Tile.TileFile.Name, out _);
        try
        {
            PatchTerrainTileHeights(existingBytes, patchHeights, encoding);
            return existingBytes;
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"  -> Existing .t layout is unsupported for {generated.Tile.TileFile.Name}; rebuilding from clean template. {ex.Message}");
        }

        return CreatePatchedCleanTemplateTileBytes(generated, patchHeights, encoding);
    }

    private static byte[] CreatePatchedCleanTemplateTileBytes(GeneratedTile generated, float[,] patchHeights, TerrainSampleEncoding encoding)
    {
        FileInfo cleanTemplate = FindGeneratedTerrainTileTemplate()
            ?? throw new InvalidOperationException("could not find a clean terrain .t template in generated-tiles beside the executable or working folder");
        byte[] cleanBytes = CreateTerrainTileFromTemplate(cleanTemplate, Path.GetFileNameWithoutExtension(generated.Tile.TileFile.Name).ToLowerInvariant());
        PatchTerrainTileHeights(cleanBytes, patchHeights, encoding);
        return cleanBytes;
    }

    private static FileInfo? FindGeneratedTerrainTileTemplate()
    {
        return EnumerateGeneratedTileTemplateDirectories()
            .SelectMany(d => d.EnumerateFiles("*.t"))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool NormalizeTerrainMaterialFileIfLegacyMap(TerrainTile tile)
    {
        try
        {
            byte[] source = File.ReadAllBytes(tile.TileFile.FullName);
            byte[] normalized = NormalizeLegacyMapTerrainMaterial(
                source, tile.TileFile.Name, out bool changed);
            if (!changed)
            {
                return false;
            }

            string stagedPath = tile.TileFile.FullName + ".scolidex-material-stage";
            try
            {
                File.WriteAllBytes(stagedPath, normalized);
                File.Move(stagedPath, tile.TileFile.FullName, overwrite: true);
            }
            finally
            {
                TryDeleteFile(stagedPath);
            }

            Console.WriteLine(
                "  -> Reset legacy map terrain material to terrain.ace; " +
                "the separate terrain_maps overlay remains unchanged.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"  -> Could not normalize legacy map terrain material: {ex.Message}");
            return false;
        }
    }

    private static byte[] NormalizeLegacyMapTerrainMaterial(
        byte[] source,
        string tileName,
        out bool changed)
    {
        changed = Encoding.Unicode.GetString(source)
            .Contains("_map.ace", StringComparison.OrdinalIgnoreCase);
        if (!changed)
        {
            return source;
        }

        FileInfo template = FindGeneratedTerrainTileTemplate()
            ?? throw new InvalidOperationException(
                "could not find the clean terrain material template");
        byte[] templateBytes = File.ReadAllBytes(template.FullName);
        ValidateTerrainBinaryForMaterialReset(source, tileName);
        ValidateTerrainBinaryForMaterialReset(templateBytes, template.Name);

        const int terrainPosition = 32;
        int sourceTerrainEnd = terrainPosition + 8 +
            checked((int)BitConverter.ToUInt32(source, terrainPosition + 4));
        int templateTerrainEnd = terrainPosition + 8 +
            checked((int)BitConverter.ToUInt32(templateBytes, terrainPosition + 4));
        (int sourceMaterialStart, int sourceMaterialEnd) = FindDirectChildBlock(
            source, terrainPosition + 9, sourceTerrainEnd, 151);
        (int templateMaterialStart, int templateMaterialEnd) = FindDirectChildBlock(
            templateBytes, terrainPosition + 9, templateTerrainEnd, 151);

        int templateMaterialLength = templateMaterialEnd - templateMaterialStart;
        using MemoryStream assembled = new(
            source.Length + templateMaterialLength -
            (sourceMaterialEnd - sourceMaterialStart));
        assembled.Write(source, 0, sourceMaterialStart);
        assembled.Write(
            templateBytes, templateMaterialStart, templateMaterialLength);
        assembled.Write(
            source, sourceMaterialEnd, source.Length - sourceMaterialEnd);
        byte[] result = assembled.ToArray();
        int delta = templateMaterialLength -
            (sourceMaterialEnd - sourceMaterialStart);
        WriteUInt32(
            result,
            terrainPosition + 4,
            checked((uint)(BitConverter.ToUInt32(source, terrainPosition + 4) + delta)));

        CopyDefaultTerrainPatchMaterialState(templateBytes, result);
        string normalizedText = Encoding.Unicode.GetString(result);
        if (!normalizedText.Contains("terrain.ace", StringComparison.OrdinalIgnoreCase) ||
            normalizedText.Contains("_map.ace", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"terrain material reset validation failed for {tileName}");
        }

        return result;
    }

    private static void ValidateTerrainBinaryForMaterialReset(
        byte[] bytes,
        string fileName)
    {
        if (bytes.Length < 40 ||
            Encoding.ASCII.GetString(bytes, 0, 8) != "SIMISA@@" ||
            BitConverter.ToUInt16(bytes, 32) != TokenTerrain)
        {
            throw new InvalidDataException(
                $"{fileName} is not a supported binary terrain tile");
        }
    }

    private static void CopyDefaultTerrainPatchMaterialState(
        byte[] templateBytes,
        byte[] targetBytes)
    {
        List<int> templatePatches = [];
        List<int> targetPatches = [];
        WalkBinaryTokens(
            templateBytes, 32, templateBytes.Length, 0,
            (token, payload, blockEnd) =>
            {
                if (token == TokenTerrainPatchsetPatch && payload + 56 <= blockEnd)
                {
                    templatePatches.Add(payload);
                }
            });
        WalkBinaryTokens(
            targetBytes, 32, targetBytes.Length, 0,
            (token, payload, blockEnd) =>
            {
                if (token == TokenTerrainPatchsetPatch && payload + 56 <= blockEnd)
                {
                    targetPatches.Add(payload);
                }
            });

        int expected = TerrainPatchGridSize * TerrainPatchGridSize;
        if (templatePatches.Count != expected || targetPatches.Count != expected)
        {
            throw new InvalidDataException(
                $"terrain patch material reset expected {expected:N0} patches; " +
                $"template={templatePatches.Count:N0}, target={targetPatches.Count:N0}");
        }

        const int materialAndUvOffset = 28;
        const int materialAndUvBytes = 28;
        for (int index = 0; index < expected; index++)
        {
            Buffer.BlockCopy(
                templateBytes,
                templatePatches[index] + materialAndUvOffset,
                targetBytes,
                targetPatches[index] + materialAndUvOffset,
                materialAndUvBytes);
        }
    }

    private static IEnumerable<DirectoryInfo> EnumerateGeneratedTileTemplateDirectories()
    {
        string currentDirectoryPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "generated-tiles"));
        DirectoryInfo currentDirectory = new(currentDirectoryPath);
        if (currentDirectory.Exists)
        {
            yield return currentDirectory;
        }

        string appDirectoryPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "generated-tiles"));
        if (!appDirectoryPath.Equals(currentDirectoryPath, StringComparison.OrdinalIgnoreCase))
        {
            DirectoryInfo appDirectory = new(appDirectoryPath);
            if (appDirectory.Exists)
            {
                yield return appDirectory;
            }
        }
    }

    // Keeps only a small moving window of generated tiles in memory. A tile is
    // written after its neighboring edges have had a chance to merge, avoiding
    // full-route memory growth while preserving seamless transitions.
    private sealed class RollingTerrainWriter
    {
        private readonly string outputDir;
        private readonly bool cleanTileTemplate;
        private readonly Dictionary<(int X, int Z), GeneratedTile> pending = [];

        public RollingTerrainWriter(string outputDir, bool cleanTileTemplate)
        {
            this.outputDir = outputDir;
            this.cleanTileTemplate = cleanTileTemplate;
            Directory.CreateDirectory(outputDir);
        }

        public int PendingCount => pending.Count;

        public int PeakPendingCount { get; private set; }

        public void Add(GeneratedTile generated)
        {
            WorldTile worldTile = generated.Tile.WorldTile
                ?? throw new InvalidOperationException($"Terrain tile {generated.Tile.TileFile.Name} is not matched to a world tile.");
            (int X, int Z) key = (worldTile.X, worldTile.Z);
            if (pending.ContainsKey(key))
            {
                throw new InvalidOperationException($"duplicate generated world tile coordinate X={key.X}, Z={key.Z}");
            }

            pending[key] = generated;
            MergeWithPendingNeighbors(key, generated.Heights);
            PeakPendingCount = Math.Max(PeakPendingCount, pending.Count);
        }

        public void FlushRowsBefore(int minZToKeep)
        {
            List<int> rowsToFlush = pending.Keys
                .Select(key => key.Z)
                .Distinct()
                .Where(z => z < minZToKeep)
                .OrderBy(z => z)
                .ToList();
            if (rowsToFlush.Count == 0)
            {
                return;
            }

            FlushRows(rowsToFlush);
        }

        public void FlushAll()
        {
            FlushRows(pending.Keys.Select(key => key.Z).Distinct().OrderBy(z => z).ToList());
        }

        private void MergeWithPendingNeighbors((int X, int Z) key, short[,] heights)
        {
            if (pending.TryGetValue((key.X - 1, key.Z), out GeneratedTile? west))
            {
                AverageVerticalEdge(west.Heights, heights);
            }

            if (pending.TryGetValue((key.X + 1, key.Z), out GeneratedTile? east))
            {
                AverageVerticalEdge(heights, east.Heights);
            }

            if (pending.TryGetValue((key.X, key.Z - 1), out GeneratedTile? south))
            {
                AverageHorizontalEdge(south.Heights, heights);
            }

            if (pending.TryGetValue((key.X, key.Z + 1), out GeneratedTile? north))
            {
                AverageHorizontalEdge(heights, north.Heights);
            }
        }

        private void FlushRows(IReadOnlyCollection<int> rowsToFlush)
        {
            if (rowsToFlush.Count == 0)
            {
                return;
            }

            Dictionary<(int X, int Z), short[,]> rawWindow = pending.ToDictionary(item => item.Key, item => item.Value.Heights);
            MergeSharedCorners(rawWindow);

            List<GeneratedTile> windowTiles = pending.Values.ToList();
            Dictionary<string, float[,]> patchHeights = BuildMergedPatchHeights(windowTiles);
            List<(int X, int Z)> keysToFlush = pending.Keys
                .Where(key => rowsToFlush.Contains(key.Z))
                .OrderBy(key => key.Z)
                .ThenBy(key => key.X)
                .ToList();

            foreach ((int X, int Z) key in keysToFlush)
            {
                GeneratedTile generated = pending[key];
                WriteGeneratedTile(outputDir, generated, patchHeights[generated.Tile.TileFile.Name], cleanTileTemplate);
                pending.Remove(key);
            }
        }
    }

    private static TerrainSampleEncoding CalculateSampleEncoding(short[,] heights)
    {
        short min = short.MaxValue;
        short max = short.MinValue;
        foreach (short height in heights)
        {
            if (height == RawMissingHeight)
            {
                continue;
            }

            min = Math.Min(min, height);
            max = Math.Max(max, height);
        }

        if (min == short.MaxValue)
        {
            return new TerrainSampleEncoding(0, TerrainSampleScale);
        }

        float scale = (float)Math.Max(TerrainSampleScale, (double)(max - min) / (ushort.MaxValue - 1));
        return new TerrainSampleEncoding(min, scale);
    }

    private static void PatchTerrainTileHeights(byte[] tileBytes, float[,] patchHeights, TerrainSampleEncoding encoding)
    {
        PatchTerrainSampleMetadata(tileBytes, encoding);
        int patchedPatchCount = PatchTerrainPatchCenterHeights(tileBytes, patchHeights);
        int expectedPatchCount = TerrainPatchGridSize * TerrainPatchGridSize;
        if (patchedPatchCount != expectedPatchCount)
        {
            throw new InvalidOperationException(
                $"terrain .t patch layout is unsupported: patched {patchedPatchCount:N0} patch height(s), expected {expectedPatchCount:N0}");
        }
    }

    private static int PatchTerrainPatchCenterHeights(byte[] tileBytes, float[,] patchHeights)
    {
        int patchIndex = 0;
        WalkBinaryTokens(tileBytes, 32, tileBytes.Length, 0, (token, payload, blockEnd) =>
        {
            if (token != TokenTerrainPatchsetPatch)
            {
                return;
            }

            const int patchFlagsBytes = sizeof(int);
            const int patchCenterXBytes = sizeof(float);
            int heightOffset = payload + patchFlagsBytes + patchCenterXBytes;
            if (heightOffset + sizeof(float) > blockEnd)
            {
                throw new InvalidOperationException("terrain patch record is too short to contain a center-height field");
            }

            if (patchIndex >= TerrainPatchGridSize * TerrainPatchGridSize)
            {
                patchIndex++;
                return;
            }

            int patchY = patchIndex / TerrainPatchGridSize;
            int patchX = patchIndex % TerrainPatchGridSize;
            WriteSingle(tileBytes, heightOffset, patchHeights[patchY, patchX]);
            patchIndex++;
        });

        return patchIndex;
    }

    private static void WriteSingle(byte[] bytes, int offset, float value)
    {
        byte[] encoded = BitConverter.GetBytes(value);
        Buffer.BlockCopy(encoded, 0, bytes, offset, encoded.Length);
    }

    private static void PatchTerrainSampleMetadata(byte[] tileBytes, TerrainSampleEncoding encoding)
    {
        bool patchedFloor = TryPatchBinaryTokenFloat(tileBytes, TokenTerrainSampleFloor, encoding.Floor);
        bool patchedScale = TryPatchBinaryTokenFloat(tileBytes, TokenTerrainSampleScale, encoding.Scale);
        if (!patchedFloor || !patchedScale)
        {
            throw new InvalidOperationException("terrain .t sample floor/scale tokens could not be patched safely");
        }
    }

    private static bool TryPatchBinaryTokenFloat(byte[] bytes, ushort targetToken, float value)
    {
        if (bytes.Length < 32 || Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 8)) != "SIMISA@@")
        {
            return false;
        }

        bool patched = false;
        WalkBinaryTokens(bytes, 32, bytes.Length, 0, (token, payload, blockEnd) =>
        {
            if (token == targetToken && payload + sizeof(float) <= blockEnd)
            {
                WriteSingle(bytes, payload, value);
                patched = true;
            }
        });

        return patched;
    }

    private static bool IsTerrainContainerToken(ushort token)
    {
        return token is TokenTerrain
            or TokenTerrainSamples
            or TokenTerrainPatches
            or TokenTerrainPatchsets
            or TokenTerrainPatchset
            or TokenTerrainPatchsetPatches;
    }

    private static void WalkBinaryTokens(byte[] bytes, int start, int end, int depth, Action<ushort, int, int> visit)
    {
        if (depth > 12)
        {
            return;
        }

        int position = start;
        while (position + 9 <= end)
        {
            ushort token = BitConverter.ToUInt16(bytes, position);
            uint remainingBytes = BitConverter.ToUInt32(bytes, position + 4);
            long blockEndLong = (long)position + 8 + remainingBytes;
            if (remainingBytes == 0 || blockEndLong > end)
            {
                break;
            }

            int blockEnd = (int)blockEndLong;
            int labelLength = bytes[position + 8];
            int payload = position + 9 + (labelLength * 2);
            if (payload > blockEnd)
            {
                break;
            }

            visit(token, payload, blockEnd);

            if (IsTerrainContainerToken(token))
            {
                int childPayload = GetTerrainContainerChildPayload(token, payload, blockEnd);
                if (childPayload < blockEnd)
                {
                    WalkBinaryTokens(bytes, childPayload, blockEnd, depth + 1, visit);
                }
            }

            position = blockEnd;
        }
    }

    private static int GetTerrainContainerChildPayload(ushort token, int payload, int blockEnd)
    {
        return token == TokenTerrainPatchsets && payload + sizeof(int) <= blockEnd
            ? payload + sizeof(int)
            : payload;
    }

    private static float AveragePatchHeight(short[,] heights, int patchX, int patchY)
    {
        int blockSize = OrtsRawGridSize / TerrainPatchGridSize;
        int x0 = patchX * blockSize;
        int y0 = patchY * blockSize;
        double sum = 0;
        int count = 0;

        for (int y = y0; y < y0 + blockSize; y++)
        {
            for (int x = x0; x < x0 + blockSize; x++)
            {
                short value = heights[y, x];
                if (value == RawMissingHeight)
                {
                    continue;
                }

                sum += value;
                count++;
            }
        }

        return count == 0 ? 0 : (float)(sum / count);
    }

    private static void WriteRawGrid(string path, short[,] heights, TerrainSampleEncoding encoding)
    {
        byte[] bytes = new byte[OrtsRawGridSize * OrtsRawGridSize * sizeof(short)];
        int offset = 0;
        for (int y = 0; y < OrtsRawGridSize; y++)
        {
            for (int x = 0; x < OrtsRawGridSize; x++)
            {
                ushort value = heights[y, x] == RawMissingHeight
                    ? (ushort)0
                    : (ushort)Math.Clamp((int)Math.Round((heights[y, x] - encoding.Floor) / encoding.Scale, MidpointRounding.AwayFromZero), 0, ushort.MaxValue - 1);
                byte[] pair = BitConverter.GetBytes(value);
                bytes[offset++] = pair[0];
                bytes[offset++] = pair[1];
            }
        }

        File.WriteAllBytes(path, bytes);
    }

    private static Dictionary<(int X, int Z), short[,]> BuildMergeGridMap(IEnumerable<GeneratedTile> tiles)
    {
        Dictionary<(int X, int Z), short[,]> grids = [];
        foreach (GeneratedTile tile in tiles)
        {
            WorldTile worldTile = tile.Tile.WorldTile
                ?? throw new InvalidOperationException($"Terrain tile {tile.Tile.TileFile.Name} is not matched to a world tile.");

            grids.TryAdd((worldTile.X, worldTile.Z), tile.Heights);
        }

        return grids;
    }

    private static Dictionary<string, float[,]> BuildMergedPatchHeights(IEnumerable<GeneratedTile> tiles)
    {
        Dictionary<string, float[,]> patchesByTile = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<(int X, int Z), float[,]> patchesByWorld = [];
        foreach (GeneratedTile tile in tiles)
        {
            WorldTile worldTile = tile.Tile.WorldTile
                ?? throw new InvalidOperationException($"Terrain tile {tile.Tile.TileFile.Name} is not matched to a world tile.");

            float[,] patch = BuildPatchHeights(tile.Heights);
            patchesByTile[tile.Tile.TileFile.Name] = patch;
            patchesByWorld.TryAdd((worldTile.X, worldTile.Z), patch);
        }

        MergeSharedPatchEdges(patchesByWorld);
        MergeSharedPatchCorners(patchesByWorld);
        return patchesByTile;
    }

    private static float[,] BuildPatchHeights(short[,] heights)
    {
        float[,] patches = new float[TerrainPatchGridSize, TerrainPatchGridSize];
        for (int patchY = 0; patchY < TerrainPatchGridSize; patchY++)
        {
            for (int patchX = 0; patchX < TerrainPatchGridSize; patchX++)
            {
                patches[patchY, patchX] = AveragePatchHeight(heights, patchX, patchY);
            }
        }

        return patches;
    }

    private static void MergeSharedPatchEdges(IDictionary<(int X, int Z), float[,]> patches)
    {
        foreach (KeyValuePair<(int X, int Z), float[,]> item in patches)
        {
            (int x, int z) = item.Key;
            float[,] patch = item.Value;

            if (patches.TryGetValue((x + 1, z), out float[,]? east))
            {
                AverageVerticalPatchEdge(patch, east);
            }

            if (patches.TryGetValue((x, z + 1), out float[,]? north))
            {
                AverageHorizontalPatchEdge(patch, north);
            }
        }
    }

    private static void AverageVerticalPatchEdge(float[,] west, float[,] east)
    {
        int edge = TerrainPatchGridSize - 1;
        for (int y = 0; y < TerrainPatchGridSize; y++)
        {
            float merged = (west[y, edge] + east[y, 0]) / 2.0f;
            west[y, edge] = merged;
            east[y, 0] = merged;
        }
    }

    private static void AverageHorizontalPatchEdge(float[,] south, float[,] north)
    {
        int edge = TerrainPatchGridSize - 1;
        for (int x = 0; x < TerrainPatchGridSize; x++)
        {
            float merged = (south[0, x] + north[edge, x]) / 2.0f;
            south[0, x] = merged;
            north[edge, x] = merged;
        }
    }

    private static void MergeSharedPatchCorners(IDictionary<(int X, int Z), float[,]> patches)
    {
        Dictionary<(int X, int Z), List<PatchCornerRef>> corners = [];
        foreach (KeyValuePair<(int X, int Z), float[,]> item in patches)
        {
            (int x, int z) = item.Key;
            float[,] patch = item.Value;
            int edge = TerrainPatchGridSize - 1;

            AddPatchCorner(corners, (x, z), patch, 0, edge);
            AddPatchCorner(corners, (x + 1, z), patch, edge, edge);
            AddPatchCorner(corners, (x, z + 1), patch, 0, 0);
            AddPatchCorner(corners, (x + 1, z + 1), patch, edge, 0);
        }

        foreach (List<PatchCornerRef> refs in corners.Values.Where(r => r.Count > 1))
        {
            float merged = refs.Average(r => r.Grid[r.Y, r.X]);
            foreach (PatchCornerRef cornerRef in refs)
            {
                cornerRef.Grid[cornerRef.Y, cornerRef.X] = merged;
            }
        }
    }

    private static void AddPatchCorner(
        IDictionary<(int X, int Z), List<PatchCornerRef>> corners,
        (int X, int Z) key,
        float[,] grid,
        int x,
        int y)
    {
        if (!corners.TryGetValue(key, out List<PatchCornerRef>? refs))
        {
            refs = [];
            corners[key] = refs;
        }

        refs.Add(new PatchCornerRef(grid, x, y));
    }

    private static short[,] ConvertOneMeterDemToOrtsGrid(float[,] oneMeterGrid, float noDataValue)
    {
        int demHeight = oneMeterGrid.GetLength(0);
        int demWidth = oneMeterGrid.GetLength(1);
        short[,] result = new short[OrtsRawGridSize, OrtsRawGridSize];

        for (int y = 0; y < OrtsRawGridSize; y++)
        {
            int y0 = (int)Math.Floor((double)y * demHeight / OrtsRawGridSize);
            int y1 = Math.Max(y0 + 1, (int)Math.Floor((double)(y + 1) * demHeight / OrtsRawGridSize));

            for (int x = 0; x < OrtsRawGridSize; x++)
            {
                int x0 = (int)Math.Floor((double)x * demWidth / OrtsRawGridSize);
                int x1 = Math.Max(x0 + 1, (int)Math.Floor((double)(x + 1) * demWidth / OrtsRawGridSize));
                result[y, x] = AverageWindow(oneMeterGrid, noDataValue, x0, y0, x1, y1);
            }
        }

        FillMissingHeights(result);
        return result;
    }

    private static short AverageWindow(float[,] grid, float noDataValue, int x0, int y0, int x1, int y1)
    {
        double sum = 0;
        int count = 0;

        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                float value = grid[y, x];
                if (float.IsNaN(value) || Math.Abs(value - noDataValue) < 0.001f)
                {
                    continue;
                }

                sum += value;
                count++;
            }
        }

        if (count == 0)
        {
            return RawMissingHeight;
        }

        return ClampToInt16Meters(sum / count);
    }

    private static void FillMissingHeights(short[,] grid)
    {
        int height = grid.GetLength(0);
        int width = grid.GetLength(1);
        bool changed;

        do
        {
            changed = false;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (grid[y, x] != RawMissingHeight)
                    {
                        continue;
                    }

                    if (TryAverageNeighbors(grid, x, y, out short filled))
                    {
                        grid[y, x] = filled;
                        changed = true;
                    }
                }
            }
        }
        while (changed && grid.Cast<short>().Any(v => v == RawMissingHeight));
    }

    private static bool TryAverageNeighbors(short[,] grid, int x, int y, out short value)
    {
        int height = grid.GetLength(0);
        int width = grid.GetLength(1);
        int sum = 0;
        int count = 0;

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                int nx = x + dx;
                int ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height || grid[ny, nx] == RawMissingHeight)
                {
                    continue;
                }

                sum += grid[ny, nx];
                count++;
            }
        }

        value = count == 0 ? RawMissingHeight : ClampToInt16Meters((double)sum / count);
        return count > 0;
    }

    private static void MergeSharedEdges(IDictionary<(int X, int Z), short[,]> grids)
    {
        foreach (KeyValuePair<(int X, int Z), short[,]> item in grids)
        {
            (int x, int z) = item.Key;
            short[,] grid = item.Value;

            if (grids.TryGetValue((x + 1, z), out short[,]? east))
            {
                AverageVerticalEdge(grid, east);
            }

            if (grids.TryGetValue((x, z + 1), out short[,]? north))
            {
                AverageHorizontalEdge(grid, north);
            }
        }

        MergeSharedCorners(grids);
    }

    private static void AverageVerticalEdge(short[,] west, short[,] east)
    {
        int edge = OrtsRawGridSize - 1;
        for (int y = 0; y < OrtsRawGridSize; y++)
        {
            short merged = MergeHeights(west[y, edge], east[y, 0]);
            west[y, edge] = merged;
            east[y, 0] = merged;
        }
    }

    private static void AverageHorizontalEdge(short[,] south, short[,] north)
    {
        int edge = OrtsRawGridSize - 1;
        for (int x = 0; x < OrtsRawGridSize; x++)
        {
            short merged = MergeHeights(south[0, x], north[edge, x]);
            south[0, x] = merged;
            north[edge, x] = merged;
        }
    }

    private static short MergeHeights(short a, short b)
    {
        if (a == RawMissingHeight)
        {
            return b;
        }

        if (b == RawMissingHeight)
        {
            return a;
        }

        return ClampToInt16Meters(((double)a + b) / 2.0);
    }

    private static void MergeSharedCorners(IDictionary<(int X, int Z), short[,]> grids)
    {
        Dictionary<(int X, int Z), List<RawCornerRef>> corners = [];
        foreach (KeyValuePair<(int X, int Z), short[,]> item in grids)
        {
            (int x, int z) = item.Key;
            short[,] grid = item.Value;
            int edge = OrtsRawGridSize - 1;

            AddRawCorner(corners, (x, z), grid, 0, edge);
            AddRawCorner(corners, (x + 1, z), grid, edge, edge);
            AddRawCorner(corners, (x, z + 1), grid, 0, 0);
            AddRawCorner(corners, (x + 1, z + 1), grid, edge, 0);
        }

        foreach (List<RawCornerRef> refs in corners.Values.Where(r => r.Count > 1))
        {
            List<short> validHeights = refs
                .Select(r => r.Grid[r.Y, r.X])
                .Where(h => h != RawMissingHeight)
                .ToList();

            if (validHeights.Count == 0)
            {
                continue;
            }

            short merged = ClampToInt16Meters(validHeights.Average(h => h));
            foreach (RawCornerRef cornerRef in refs)
            {
                cornerRef.Grid[cornerRef.Y, cornerRef.X] = merged;
            }
        }
    }

    private static void AddRawCorner(
        IDictionary<(int X, int Z), List<RawCornerRef>> corners,
        (int X, int Z) key,
        short[,] grid,
        int x,
        int y)
    {
        if (!corners.TryGetValue(key, out List<RawCornerRef>? refs))
        {
            refs = [];
            corners[key] = refs;
        }

        refs.Add(new RawCornerRef(grid, x, y));
    }

    private static short ClampToInt16Meters(double value)
    {
        return (short)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), short.MinValue + 1, short.MaxValue);
    }

    [GeneratedRegex(@"Marker\s*\(\s*(?<lon>-?\d+(?:\.\d+)?)\s+(?<lat>-?\d+(?:\.\d+)?)\s+(?<name>[^)]*)\)")]
    private static partial Regex CoverageMarkerRegex();

    [GeneratedRegex(@"TrVectorSection\s*\(\s*\S+\s+\S+\s+(?<worldX>-?\d+)\s+(?<worldZ>-?\d+)\s+\S+\s+\S+\s+\S+\s+\S+\s+(?<tileX>-?\d+)\s+(?<tileZ>-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TrackVectorSectionRegex();

    [GeneratedRegex(@"UiD\s*\(\s*(?<worldX>-?\d+)\s+(?<worldZ>-?\d+)\s+\S+\s+\S+\s+(?<tileX>-?\d+)\s+(?<tileZ>-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TrackUidRegex();

    [GeneratedRegex(@"RData\s*\(\s*[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?\s+[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?\s+[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?\s+(?<tileX>-?\d+)\s+(?<tileZ>-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TrackItemRDataRegex();

    [GeneratedRegex(@"USGS_13_(?<cell>n\d+w\d+)_(?<date>\d{8})\.tif", RegexOptions.IgnoreCase)]
    private static partial Regex OneThirdArcSecondProductRegex();

    private sealed record GeneratedTile(TerrainTile Tile, short[,] Heights);

    private sealed record DecodedRawTile(int X, int Z, short[,] Heights, TerrainSampleEncoding Encoding);

    private sealed record DecodedLoRawTile(int X, int Z, short[,] Heights);

    private sealed record GeneratedLoTile(
        LoTileCoordinate Tile,
        string Name,
        FileInfo TemplateTile,
        string TilePath,
        string HeightPath,
        short[,] Heights,
        int SamplesUsed,
        int GlobalSamplesUsed);

    private sealed record TerrainGenerationResult(
        short[,] Heights,
        int PrimarySamplesUsed,
        int IntermediateSamplesUsed,
        int FallbackSamplesUsed,
        int GlobalSamplesUsed,
        int NeighborFilledSamples);

    private sealed record UsgsDatasetAvailability(bool ServiceAvailable, int ItemCount, string Detail);

    private sealed record SourceAvailability(bool ServiceAvailable, bool CoverageAvailable, string Detail);

    private sealed record GeoSampleGrid(
        double[,] Longitudes,
        double[,] Latitudes,
        (double MinLon, double MinLat, double MaxLon, double MaxLat) BoundingBox);

    private sealed record DemWindow(string ProductName, short[,] Heights, int ValidSamples);

    private sealed record DemWindowSearchResult(
        IReadOnlyList<DemWindow> Windows,
        bool SourceHiccup,
        string? HiccupDetail = null);

    private sealed class RetryableDemSourceException(string message) : InvalidOperationException(message);

    private sealed record TerrainSampleEncoding(float Floor, float Scale);

    private sealed record RasterElevationTransform(
        double ValueScale,
        double ValueOffset,
        double UnitToMeters,
        string UnitName);

    private sealed record PatchCornerRef(float[,] Grid, int X, int Y);

    private sealed record RawCornerRef(short[,] Grid, int X, int Y);

    // Minimal writer for TSRE's normal and low-terrain index files. Without
    // these indexes, valid tile assets can exist on disk but remain invisible
    // to TSRE's terrain editor.
    private sealed class TsreTerrainQuadTree
    {
        private const int RootLevel = 256;
        private const int TdBlockTileSize = 512;
        private const int TerrainDescSize = 67_108_864;
        private const int TerrainDepth = 6;
        private static readonly byte[] TdHeader =
        [
            0x53, 0x49, 0x4D, 0x49, 0x53, 0x41, 0x40, 0x40,
            0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40, 0x40,
            0x4A, 0x49, 0x4E, 0x58, 0x30, 0x64, 0x31, 0x62,
            0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x0D, 0x0A,
        ];

        private readonly SortedDictionary<(int Qx, int Qz), TdFile> tdFiles = [];
        private readonly int targetLevel;
        private readonly string indexFileName;
        private readonly string tileFileExtension;

        public TsreTerrainQuadTree(int targetLevel, string indexFileName, string tileFileExtension)
        {
            this.targetLevel = targetLevel;
            this.indexFileName = indexFileName;
            this.tileFileExtension = tileFileExtension;
        }

        public void AddTile(int tileX, int tileZ)
        {
            int qx = (int)Math.Floor(tileX / (double)TdBlockTileSize);
            int qz = (int)Math.Floor(tileZ / (double)TdBlockTileSize);
            (int Qx, int Qz) key = (qx, qz);
            if (!tdFiles.TryGetValue(key, out TdFile? tdFile))
            {
                tdFile = new TdFile(qx * TdBlockTileSize, qz * TdBlockTileSize);
                tdFiles.Add(key, tdFile);
            }

            tdFile.Root.AddTile(tileX, tileZ, targetLevel);
        }

        public void Save(string tdDir)
        {
            SaveIndex(Path.Combine(tdDir, indexFileName));
            foreach (((int qx, int qz), TdFile tdFile) in tdFiles)
            {
                SaveTdFile(Path.Combine(tdDir, GetNameXy(qx) + GetNameXy(qz) + tileFileExtension), tdFile);
            }
        }

        private void SaveIndex(string path)
        {
            using StreamWriter writer = new(path, false, new UnicodeEncoding(false, true));
            writer.WriteLine("SIMISA@@@@@@@@@@JINX0D0t______");
            writer.WriteLine();
            writer.WriteLine("terrain_desc (");
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "\tterrain_desc_size ( {0} )", TerrainDescSize));
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "\tDepth ( {0} )", TerrainDepth));
            writer.WriteLine("\tterrain_desc_tiles ( ");
            foreach ((int qx, int qz) in tdFiles.Keys)
            {
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture, "\t\tTdFile ( {0} {1} )", qx, qz));
            }

            writer.WriteLine("\t) ");
            writer.WriteLine(") ");
        }

        private static void SaveTdFile(string path, TdFile tdFile)
        {
            List<byte> treeData = [];
            tdFile.Root.Save(treeData);

            using FileStream stream = File.Create(path);
            using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: false);
            writer.Write(TdHeader);
            writer.Write(0x84);
            writer.Write(treeData.Count + 14);
            writer.Write((byte)0);
            writer.Write(0x87);
            writer.Write(treeData.Count + 5);
            writer.Write((byte)0);
            writer.Write(treeData.Count);
            writer.Write(treeData.ToArray());
        }

        private static string GetNameXy(int value)
        {
            char sign = value < 0 ? '-' : '+';
            return sign + Math.Abs(value).ToString("00000", CultureInfo.InvariantCulture);
        }

        private sealed class TdFile
        {
            public TdFile(int rootX, int rootZ)
            {
                Root = new QuadTile(RootLevel, rootX, rootZ);
            }

            public QuadTile Root { get; }
        }

        private sealed class QuadTile
        {
            private readonly QuadTile?[,] children = new QuadTile?[2, 2];
            private readonly bool[,] populated = new bool[2, 2];
            private readonly int level;
            private readonly int x;
            private readonly int z;

            public QuadTile(int level, int x, int z)
            {
                this.level = level;
                this.x = x;
                this.z = z;
            }

            public void AddTile(int tileX, int tileZ, int targetLevel)
            {
                int px = tileX >= x + level ? 1 : 0;
                int pz = tileZ >= z + level ? 1 : 0;
                if (level == targetLevel)
                {
                    populated[px, pz] = true;
                    return;
                }

                if (children[px, pz] is null)
                {
                    children[px, pz] = new QuadTile(level / 2, x + (px * level), z + (pz * level));
                }

                children[px, pz]!.AddTile(tileX, tileZ, targetLevel);
            }

            public void Save(List<byte> data)
            {
                byte divided = 0;
                byte visible = 0;

                if (children[0, 1] is not null) divided |= 0b1000;
                if (children[1, 1] is not null) divided |= 0b0100;
                if (children[1, 0] is not null) divided |= 0b0010;
                if (children[0, 0] is not null) divided |= 0b0001;
                if (populated[0, 1]) visible |= 0b1000;
                if (populated[1, 1]) visible |= 0b0100;
                if (populated[1, 0]) visible |= 0b0010;
                if (populated[0, 0]) visible |= 0b0001;

                data.Add((byte)((divided << 4) | visible));
                children[0, 1]?.Save(data);
                children[1, 1]?.Save(data);
                children[1, 0]?.Save(data);
                children[0, 0]?.Save(data);
            }
        }
    }

    // Lightweight reader for external *_y.raw int16 terrain height grids.
    // RawMissingHeight marks empty posts so Append can identify retryable tiles.
    private sealed class RawGrid
    {
        private RawGrid(short[,] heights)
        {
            Heights = heights;
        }

        public short[,] Heights { get; }

        public static bool TryRead(string path, out RawGrid? grid, out string error)
        {
            grid = null;

            if (!File.Exists(path))
            {
                error = $"Raw height file not found: {path}";
                return false;
            }

            byte[] bytes = File.ReadAllBytes(path);
            int expectedBytes = OrtsRawGridSize * OrtsRawGridSize * sizeof(short);
            if (bytes.Length != expectedBytes)
            {
                error = $"Expected {expectedBytes} bytes in raw grid, found {bytes.Length}.";
                return false;
            }

            short[,] heights = new short[OrtsRawGridSize, OrtsRawGridSize];
            int offset = 0;
            for (int y = 0; y < OrtsRawGridSize; y++)
            {
                for (int x = 0; x < OrtsRawGridSize; x++)
                {
                    heights[y, x] = BitConverter.ToInt16(bytes, offset);
                    offset += sizeof(short);
                }
            }

            grid = new RawGrid(heights);
            error = "";
            return true;
        }

        public RawGridStats GetStats()
        {
            return GetStats(Heights);
        }

        public static RawGridStats GetStats(short[,] heights)
        {
            int valid = 0;
            int missing = 0;
            short min = short.MaxValue;
            short max = short.MinValue;

            foreach (short height in heights)
            {
                if (height == RawMissingHeight)
                {
                    missing++;
                    continue;
                }

                valid++;
                min = Math.Min(min, height);
                max = Math.Max(max, height);
            }

            if (valid == 0)
            {
                min = RawMissingHeight;
                max = RawMissingHeight;
            }

            return new RawGridStats(OrtsRawGridSize, OrtsRawGridSize, valid, missing, min, max);
        }
    }

    private sealed record RawGridStats(int Width, int Height, int ValidCount, int MissingCount, short MinHeight, short MaxHeight)
    {
        public bool IsEmpty => ValidCount == 0;
    }
}
