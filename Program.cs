// SCO LIDEX - Open Rails / MSTS Cloud Terrain Builder
// Copyright (C) Scott Brunner, Beast of Burden
//
// This file contains the terrain generation engine: route/tile discovery,
// projection mapping, USGS DEM product lookup, GDAL raster sampling, seamless
// tile merging, distant mountain generation, and command-line entry points.
// SCO LIDEX is distributed under GNU GPL v3 or later. See LICENSE.txt.
//
// USGS cloud endpoint used for DEM product discovery:
//   https://tnmaccess.nationalmap.gov/api/v1/products
//
// Typical formatted request:
//   https://tnmaccess.nationalmap.gov/api/v1/products?bbox=-75.294138,40.664608,-75.263146,40.682954&prodFormats=GeoTIFF&outputFormat=JSON&datasets=Digital%20Elevation%20Model%20%28DEM%29%201%20meter
//
// The JSON response supplies product downloadURL values. GDAL then streams only
// the required GeoTIFF raster windows through /vsicurl/ instead of SCO LIDEX
// downloading whole source DEM files.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using MaxRev.Gdal.Core;
using OSGeo.GDAL;
using OSGeo.OGR;
using OSGeo.OSR;

namespace ORterr;

internal static partial class Program
{
    // Dormant test switch. The isolated 4 m writer remains compiled and ready,
    // but normal GUI/CLI builds must not expose experimental output until the
    // joint Open Rails format test is resumed deliberately.
    internal static readonly bool Experimental4mExportEnabled = false;

    private const int OrtsRawGridSize = 256;
    private const int LoRawGridSize = 256;
    private const int LoTileNormalTileSpan = 16;
    private const double OrtsPostSpacingMeters = 8.0;
    private const double OrtsTileSizeMeters = OrtsRawGridSize * OrtsPostSpacingMeters;
    private const double OrtsStoredTileSpanMeters = (OrtsRawGridSize - 1) * OrtsPostSpacingMeters;
    private const double LoTileSizeMeters = OrtsTileSizeMeters * LoTileNormalTileSpan;
    private const int TerrainPatchGridSize = 16;
    private const int TerrainSampleFloorOffset = 136;
    private const float TerrainSampleScale = 1.0f / 256.0f;
    private const double MaxRawHeightOffset = (ushort.MaxValue - 1) / 256.0;
    private const short RawMissingHeight = short.MinValue;
    private const ushort TokenTerrain = 136;
    private const ushort TokenTerrainSamples = 139;
    private const ushort TokenTerrainNSamples = 140;
    private const ushort TokenTerrainSampleFloor = 142;
    private const ushort TokenTerrainSampleScale = 143;
    private const ushort TokenTerrainSampleSize = 144;
    private const ushort TokenTerrainPatches = 157;
    private const ushort TokenTerrainPatchsets = 158;
    private const ushort TokenTerrainPatchset = 159;
    private const ushort TokenTerrainPatchsetPatches = 163;
    private const ushort TokenTerrainPatchsetPatch = 164;
    private const string PrimaryDemDataset = "Digital Elevation Model (DEM) 1 meter";
    private const string IntermediateDemDataset = "Original Product Resolution (OPR) Digital Elevation Model (DEM)";
    private const string FallbackDemDataset = "National Elevation Dataset (NED) 1/3 arc-second";
    private const string PrimaryDemLabel = "1m";
    private const string IntermediateDemLabel = "5m~";
    private const string FallbackDemLabel = "10m";

    // Counts the payload SCO LIDEX asks USGS/GDAL to provide during a run.
    // GDAL does the actual /vsicurl/ network reads internally, so this is a
    // practical transfer estimate based on product JSON bytes plus DEM raster
    // window samples requested by ReadRaster.
    private static long usgsDataBytesRead;

    private static readonly Dictionary<string, HashSet<string>> ProductUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private static string? ProductUrlCachePath;

    internal sealed record ScanOptions(
        bool CreateRouteTiles,
        bool CreateDistantMountains,
        bool CreateMapTiles,
        bool MarkerCoverage,
        bool TrackDatabaseCoverage,
        bool KmlCoverage,
        bool TextFileCoverage,
        bool CleanTileWipe,
        int TerrainRadius,
        int LoTileRadius);

    internal sealed record DemSourcePolicy(
        bool UsePrimary,
        bool UseIntermediate,
        bool UseFallback,
        bool UseGlobal)
    {
        internal static DemSourcePolicy All { get; } = new(true, true, true, true);
        internal static DemSourcePolicy None { get; } = new(false, false, false, false);
        internal bool HasAny => UsePrimary || UseIntermediate || UseFallback || UseGlobal;
    }

    internal sealed record ScanSummary(
        bool CanRun,
        int RouteTileTotal,
        int DistantMountainTotal,
        int UnreadableRouteTiles,
        int UnreadableDistantMountainTiles,
        bool HasWarnings,
        DemSourcePolicy DemSources,
        bool PrimaryServiceAvailable,
        bool IntermediateServiceAvailable,
        bool FallbackServiceAvailable,
        bool RouteCanRun,
        bool DistantMountainCanRun,
        bool MapCanRun,
        bool MapCacheOnly);

    internal sealed record PostProcessSelectionOptions(
        bool ShiftRouteTiles,
        bool ShiftDistantMountains,
        bool MarkerCoverage,
        bool TrackDatabaseCoverage,
        bool KmlCoverage,
        bool TextFileCoverage,
        int TerrainRadius,
        int LoTileRadius);

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--gui", StringComparer.OrdinalIgnoreCase))
        {
            using Mutex singleInstance = new(false, @"Local\SCO-LIDEX-GUI");
            if (!singleInstance.WaitOne(0))
            {
                MessageBox.Show(
                    "SCO LIDEX is already running.",
                    "SCO LIDEX",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new TopoForm());
            }
            catch (Exception ex)
            {
                WriteStartupErrorLog(ex);
                MessageBox.Show(
                    $"SCO LIDEX could not start. Details were written to:{Environment.NewLine}{GetStartupErrorLogPath()}",
                    "SCO LIDEX",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            return;
        }

        RunCommandLineAsync(args).GetAwaiter().GetResult();
    }

    private static async Task RunCommandLineAsync(string[] args)
    {
        AttachConsoleForCommandLine();
        if (args.Contains("--copernicus-probe", StringComparer.OrdinalIgnoreCase))
        {
            await RunCopernicusProbeAsync(args);
            return;
        }

        if (args.Contains("--map-probe", StringComparer.OrdinalIgnoreCase))
        {
            await RunMapTileProbeAsync(args, CancellationToken.None);
            return;
        }

        await RunConsoleAsync(args, CancellationToken.None);
    }

    private static void AttachConsoleForCommandLine()
    {
        if (Console.IsOutputRedirected)
        {
            return;
        }

        AttachConsole(AttachParentProcess);
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void WriteStartupErrorLog(Exception exception)
    {
        try
        {
            string path = GetStartupErrorLogPath();
            StringBuilder text = new();
            text.AppendLine($"SCO LIDEX startup error {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"Version: {ReadVersionText()}");
            text.AppendLine($"OS: {Environment.OSVersion}");
            text.AppendLine($"64-bit OS: {Environment.Is64BitOperatingSystem}");
            text.AppendLine($"64-bit Process: {Environment.Is64BitProcess}");
            text.AppendLine($"Base Directory: {AppContext.BaseDirectory}");
            text.AppendLine($"Current Directory: {Environment.CurrentDirectory}");
            text.AppendLine();
            text.AppendLine(exception.ToString());
            File.WriteAllText(path, text.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Last-resort startup logging must never create a second startup failure.
        }
    }

    private static string GetStartupErrorLogPath()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string directory = string.IsNullOrWhiteSpace(desktop) ? AppContext.BaseDirectory : desktop;
        return Path.Combine(directory, "SCOLIDEX-startup-error.txt");
    }

    private static string ReadVersionText()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "VERSION");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    internal static void ResetUsgsDataCounter()
    {
        Interlocked.Exchange(ref usgsDataBytesRead, 0);
    }

    internal static long GetUsgsDataBytesRead()
    {
        return Interlocked.Read(ref usgsDataBytesRead);
    }

    internal static string FormatUsgsDataBytesRead()
    {
        return FormatByteCount(GetUsgsDataBytesRead());
    }

    private static void AddUsgsDataBytes(long byteCount)
    {
        if (byteCount > 0)
        {
            Interlocked.Add(ref usgsDataBytesRead, byteCount);
        }
    }

    private static string FormatByteCount(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} {units[unit]}"
            : $"{value:N2} {units[unit]}";
    }

    // Main terrain-build entry point used by both the CLI and the GUI wrapper.
    // The GUI simply converts selected controls into command-line style args,
    // redirects Console output into the log window, and lets this engine run.
    internal static async Task RunConsoleAsync(string[] args, CancellationToken cancellationToken)
    {
        Console.WriteLine("=========================================");
        Console.WriteLine(" SCO LIDEX Cloud Terrain Builder ");
        Console.WriteLine("=========================================\n");

        bool overwriteFlag = args.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        bool inspectOnly = !args.Contains("--write", StringComparer.OrdinalIgnoreCase);
        bool markerCoverage = args.Contains("--marker-coverage", StringComparer.OrdinalIgnoreCase);
        bool trackDatabaseCoverage = args.Contains("--track-database-coverage", StringComparer.OrdinalIgnoreCase);
        bool kmlCoverage = args.Contains("--kml-coverage", StringComparer.OrdinalIgnoreCase);
        bool textFileCoverage = args.Contains("--text-file-coverage", StringComparer.OrdinalIgnoreCase);
        bool distantMountains = args.Contains("--distant-mountains", StringComparer.OrdinalIgnoreCase);
        bool createRouteTiles = !args.Contains("--no-route-tiles", StringComparer.OrdinalIgnoreCase);
        bool cleanTileTemplate = args.Contains("--clean-tile-template", StringComparer.OrdinalIgnoreCase);
        bool createMapTiles = args.Contains("--map-tiles", StringComparer.OrdinalIgnoreCase);
        bool mapCacheOnly = args.Contains("--map-cache-only", StringComparer.OrdinalIgnoreCase);
        bool routeSkippedByScan = args.Contains("--scan-skipped-route", StringComparer.OrdinalIgnoreCase);
        bool distantMountainsSkippedByScan = args.Contains("--scan-skipped-dm", StringComparer.OrdinalIgnoreCase);
        bool mapsSkippedByScan = args.Contains("--scan-skipped-maps", StringComparer.OrdinalIgnoreCase);
        bool primaryServiceAvailable = args.Contains("--usgs-1m-service-online", StringComparer.OrdinalIgnoreCase);
        bool intermediateServiceAvailable = args.Contains("--usgs-5m-service-online", StringComparer.OrdinalIgnoreCase);
        bool fallbackServiceAvailable = args.Contains("--usgs-10m-service-online", StringComparer.OrdinalIgnoreCase);
        DemSourcePolicy demSources = new(
            !args.Contains("--skip-usgs-1m", StringComparer.OrdinalIgnoreCase),
            !args.Contains("--skip-usgs-5m", StringComparer.OrdinalIgnoreCase),
            !args.Contains("--skip-usgs-10m", StringComparer.OrdinalIgnoreCase),
            !args.Contains("--skip-copernicus", StringComparer.OrdinalIgnoreCase));
        bool experimental4mTest = args.Contains("--experimental-4m-test", StringComparer.OrdinalIgnoreCase);
        if (experimental4mTest && !Experimental4mExportEnabled)
        {
            Console.WriteLine("Experimental 4m export is deactivated in this build. Normal 8m output remains active.");
            return;
        }
        int limit = ParseIntOption(args, "--limit", int.MaxValue);
        int terrainRadius = ParseIntOption(args, "--terrain-radius", 0);
        int loTileRadius = ParseIntOption(args, "--lo-radius", 1);
        double loSampleOffsetX = ParseDoubleOption(args, "--lo-sample-offset-x", 0);
        double loSampleOffsetY = ParseDoubleOption(args, "--lo-sample-offset-y", 0);
        double sourceOffsetX = ParseDoubleOption(args, "--source-offset-x", 0);
        double sourceOffsetZ = ParseDoubleOption(args, "--source-offset-z", 0);
        double sourceScaleX = ParseDoubleOption(args, "--source-scale-x", 1);
        double sourceScaleZ = ParseDoubleOption(args, "--source-scale-z", 1);
        double sourceBiasEastMeters = ParseDoubleOption(args, "--source-bias-east", 0);
        double sourceBiasNorthMeters = ParseDoubleOption(args, "--source-bias-north", 0);
        double postShiftEastMeters = ParseDoubleOption(args, "--post-shift-east", 0);
        double postShiftNorthMeters = ParseDoubleOption(args, "--post-shift-north", 0);
        string? requestedOutputDir = ParseStringOption(args, "--output");
        string? requestedMapTile = ParseStringOption(args, "--map-tile");
        string routeDir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "";

        if (string.IsNullOrWhiteSpace(routeDir))
        {
            Console.Write("Enter the full path to your Route directory: ");
            routeDir = Console.ReadLine()?.Trim('"', ' ') ?? "";
        }

        if (args.Contains("--post-process-shift", StringComparer.OrdinalIgnoreCase))
        {
            PostProcessSelectionOptions options = new(
                ShiftRouteTiles: createRouteTiles,
                ShiftDistantMountains: distantMountains,
                MarkerCoverage: markerCoverage,
                TrackDatabaseCoverage: trackDatabaseCoverage,
                KmlCoverage: kmlCoverage,
                TextFileCoverage: textFileCoverage,
                TerrainRadius: terrainRadius,
                LoTileRadius: loTileRadius);
            await PostProcessTerrainShiftAsync(routeDir, options, postShiftEastMeters, postShiftNorthMeters, cancellationToken);
            return;
        }

        int selectionSources = new[] { markerCoverage, trackDatabaseCoverage, kmlCoverage, textFileCoverage }.Count(v => v);
        if (selectionSources > 1)
        {
            Console.WriteLine("Error: choose only one selection source.");
            return;
        }

        if (markerCoverage && createRouteTiles)
        {
            try
            {
                EnsureMarkerCoverageTiles(routeDir, terrainRadius);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: failed while creating marker coverage tiles: {ex.Message}");
                return;
            }
        }
        else if (trackDatabaseCoverage && createRouteTiles)
        {
            try
            {
                EnsureTrackDatabaseCoverageTiles(routeDir, terrainRadius);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: failed while creating track database coverage tiles: {ex.Message}");
                return;
            }
        }
        else if (kmlCoverage && createRouteTiles)
        {
            try
            {
                EnsureKmlCoverageTiles(routeDir, terrainRadius);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: failed while creating KML coverage tiles: {ex.Message}");
                return;
            }
        }
        else if (textFileCoverage && createRouteTiles)
        {
            try
            {
                EnsureTextFileCoverageTiles(routeDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: failed while creating text-file coverage tiles: {ex.Message}");
                return;
            }
        }

        if (!RouteLayout.TryLoad(routeDir, out RouteLayout? route, out string routeError))
        {
            Console.WriteLine(routeError);
            return;
        }

        PrintRouteSummary(route!);
        ProductUrlCachePath = Path.Combine(route!.RouteDir, "SCOLIDEX-product-cache.json");
        LoadProductUrlCache(ProductUrlCachePath);
        string outputDir = requestedOutputDir
            ?? (inspectOnly ? Path.Combine(Environment.CurrentDirectory, "generated-tiles") : Path.Combine(route!.RouteDir, "tiles"));

        GeoTileMapper? mapper = GeoTileMapper.TryCreate(route!);
        if (mapper is not null)
        {
            PrintProjectionSummary(mapper);
            if (sourceOffsetX != 0 || sourceOffsetZ != 0 || sourceScaleX != 1 || sourceScaleZ != 1)
            {
                Console.WriteLine($"Applying DEM source calibration: offset X={sourceOffsetX}, Z={sourceOffsetZ}; scale X={sourceScaleX}, Z={sourceScaleZ}");
            }

            if (sourceBiasEastMeters != 0 || sourceBiasNorthMeters != 0)
            {
                Console.WriteLine($"Applying terrain geo bias: east={sourceBiasEastMeters:F0}m, north={sourceBiasNorthMeters:F0}m");
            }
        }

        Console.WriteLine("\nInitializing GDAL Engine...");
        GdalBase.ConfigureAll();
        Gdal.SetConfigOption("GDAL_HTTP_UNSAFESSL", "YES");

        using HttpClient httpClient = new() { Timeout = TimeSpan.FromMinutes(4) };
        Console.WriteLine("\n=========================================");
        Console.WriteLine(" RUN DATA SOURCE STATUS ");
        Console.WriteLine("=========================================");
        PrintDataSourceStatus("Terrain DEM - 1m", "USGS - The National Map", demSources.UsePrimary);
        Console.WriteLine($"Details: {FormatUsgsRunDetail(demSources.UsePrimary, primaryServiceAvailable)}");
        PrintDataSourceStatus("Terrain DEM - approximately 5m", "USGS - The National Map", demSources.UseIntermediate);
        Console.WriteLine($"Details: {FormatUsgsRunDetail(demSources.UseIntermediate, intermediateServiceAvailable)}");
        PrintDataSourceStatus("Terrain DEM - 10m", "USGS - The National Map", demSources.UseFallback);
        Console.WriteLine($"Details: {FormatUsgsRunDetail(demSources.UseFallback, fallbackServiceAvailable)}");
        PrintDataSourceStatus("Global terrain DEM - 30m", "Copernicus GLO-30", demSources.UseGlobal);
        if (createMapTiles)
        {
            PrintDataSourceStatus(
                "OpenStreetMap regional extract",
                mapCacheOnly ? "Local Geofabrik PBF cache" : "Geofabrik",
                true);
            Console.WriteLine(mapCacheOnly
                ? "Details: cached map extract will be used; Geofabrik will not be polled."
                : "Details: Geofabrik remote source is enabled with local-cache fallback.");
        }
        else if (mapsSkippedByScan)
        {
            PrintDataSourceStatus("OpenStreetMap regional extract", "Geofabrik", false);
            Console.WriteLine("Details: Scan found neither a usable remote source nor a usable cached PBF.");
        }
        if (routeSkippedByScan) Console.WriteLine("Run stage plan: normal terrain SKIPPED because Scan found no viable DEM source.");
        if (distantMountainsSkippedByScan) Console.WriteLine("Run stage plan: Distant Mountains SKIPPED because Scan found no viable 10m/global DEM source.");
        if (mapsSkippedByScan) Console.WriteLine("Run stage plan: map overlays SKIPPED because Geofabrik and the local PBF cache were unavailable during Scan.");
        RollingTerrainWriter? rollingWriter = null;
        int generatedCount = 0;
        int attempted = 0;
        int skipped = 0;
        int failed = 0;
        int builtWithPrimaryOnly = 0;
        int builtWithPrimaryAndIntermediate = 0;
        int builtWithPrimaryAndFallback = 0;
        int builtWithIntermediateOnly = 0;
        int builtWithIntermediateAndFallback = 0;
        int builtWithFallbackOnly = 0;
        int builtWithGlobal = 0;
        int builtWithNeighborFill = 0;
        int tilesUsingPrimary = 0;
        int tilesUsingIntermediate = 0;
        int tilesUsingFallback = 0;
        int tilesUsingGlobal = 0;
        SortedSet<string> retryableFailedNormalTileNames = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> unmappableFailedNormalTileNames = new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<TerrainTile> processingTiles = GetRouteTileProcessingList(route!, markerCoverage, trackDatabaseCoverage, kmlCoverage, textFileCoverage, terrainRadius);
        int totalTiles = processingTiles.Count;

        if (experimental4mTest)
        {
            if (inspectOnly)
            {
                Console.WriteLine("Experimental 4m test: inspect-only Scan uses the normal terrain preflight; no 4m files are written.");
                return;
            }

            if (mapper is null)
            {
                Console.WriteLine("Error: experimental 4m terrain requires route geography.");
                return;
            }

            if (distantMountains)
            {
                Console.WriteLine("Experimental 4m test: Distant Mountains are disabled and will not be generated.");
            }

            bool completed = await GenerateExperimental4mTerrainAsync(
                route,
                mapper,
                httpClient,
                outputDir,
                sourceBiasEastMeters,
                sourceBiasNorthMeters,
                demSources,
                cancellationToken);
            if (!completed)
            {
                Console.WriteLine("STATUS: FAILURE - TILES");
                return;
            }

            if (createMapTiles)
            {
                try
                {
                    await GenerateMapTilesAsync(route, mapper, route.TerrainTiles, requestedMapTile, limit, mapCacheOnly, cancellationToken);
                }
                catch
                {
                    Console.WriteLine("STATUS: FAILURE - OSM / MAPS");
                    throw;
                }
            }

            return;
        }

        if (createRouteTiles)
        {
            rollingWriter = inspectOnly ? null : new RollingTerrainWriter(outputDir, cleanTileTemplate);

            for (int tileIndex = 0; tileIndex < processingTiles.Count; tileIndex++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    Console.WriteLine("\nAbort requested; stopping before next tile.");
                    break;
                }

                TerrainTile tile = processingTiles[tileIndex];
                int tileNumber = tileIndex + 1;
                int remaining = totalTiles - tileNumber;
                Console.WriteLine($"\n[{tileNumber:N0}/{totalTiles:N0}] {tile.TileFile.Name} ({remaining:N0} remaining)");

                RawGrid? rawGrid = tile.RawHeightPath is null ? null : RawGrid.TryRead(tile.RawHeightPath, out RawGrid? grid, out _) ? grid : null;
                RawGridStats? stats = rawGrid?.GetStats();

                if (!overwriteFlag && stats is not null && !stats.IsEmpty)
                {
                    skipped++;
                    Console.WriteLine($"  -> Skipped: raw grid already has {stats.ValidCount:N0} height samples.");
                    rollingWriter?.FlushRowsBefore(tile.WorldTile?.Z - 1 ?? int.MinValue);
                    PrintProgressCheckpoint(tileNumber, totalTiles, generatedCount, skipped, failed);
                    continue;
                }

                Console.WriteLine($"  -> Raw heights: {Path.GetFileName(tile.RawHeightPath ?? "(not referenced)")}");

                if (stats is null)
                {
                    Console.WriteLine("  -> Could not read raw height grid.");
                }
                else
                {
                    Console.WriteLine($"  -> Grid {stats.Width}x{stats.Height}, valid={stats.ValidCount:N0}, missing={stats.MissingCount:N0}, min={stats.MinHeight}, max={stats.MaxHeight}");
                }

                if (inspectOnly)
                {
                    Console.WriteLine("  -> Inspect-only mode; no DEM request or route write performed.");
                    PrintProgressCheckpoint(tileNumber, totalTiles, generatedCount, skipped, failed);
                    continue;
                }

                if (attempted >= limit)
                {
                    Console.WriteLine("[STOP] Limit reached; skipping remaining tiles.");
                    break;
                }

                if (mapper is null || tile.WorldTile is null)
                {
                    failed++;
                    unmappableFailedNormalTileNames.Add(GetTerrainTileBaseName(tile));
                    MarkTerrainTileForAppendRetry(tile);
                    Console.WriteLine("  -> Cannot generate: tile-to-world or marker-based geographic coverage is unavailable.");
                    PrintProgressCheckpoint(tileNumber, totalTiles, generatedCount, skipped, failed);
                    continue;
                }

                rollingWriter?.FlushRowsBefore(tile.WorldTile.Z - 1);

                GeoSampleGrid sampleGrid = mapper.GetSampleGrid(tile.WorldTile, sourceOffsetX, sourceOffsetZ, sourceScaleX, sourceScaleZ, sourceBiasEastMeters, sourceBiasNorthMeters);
                (double minLon, double minLat, double maxLon, double maxLat) = sampleGrid.BoundingBox;
                Console.WriteLine($"  -> Estimated bbox lon {minLon:F6}..{maxLon:F6}, lat {minLat:F6}..{maxLat:F6}");

                try
                {
                    attempted++;
                    // Generate the tile into memory first. The rolling writer holds
                    // only nearby tile rows long enough to merge shared edges, then
                    // flushes older rows so large routes do not keep every tile live.
                    TerrainGenerationResult result = await StreamOrtsGridForSampleGridAsync(httpClient, sampleGrid, demSources);
                    Console.WriteLine(result.GlobalSamplesUsed > 0
                        ? "STATUS: TILES - GLOBAL - LOW RES"
                        : "STATUS: TILES - US - HIGH RES");
                    rollingWriter?.Add(new GeneratedTile(tile, result.Heights));
                    generatedCount++;
                    RawGridStats generatedStats = RawGrid.GetStats(result.Heights);
                    if (result.PrimarySamplesUsed > 0)
                    {
                        tilesUsingPrimary++;
                    }

                    if (result.IntermediateSamplesUsed > 0)
                    {
                        tilesUsingIntermediate++;
                    }

                    if (result.FallbackSamplesUsed > 0)
                    {
                        tilesUsingFallback++;
                    }

                    if (result.GlobalSamplesUsed > 0)
                    {
                        tilesUsingGlobal++;
                        builtWithGlobal++;
                    }

                    if (result.PrimarySamplesUsed > 0 && result.IntermediateSamplesUsed == 0 && result.FallbackSamplesUsed == 0 && result.GlobalSamplesUsed == 0)
                    {
                        builtWithPrimaryOnly++;
                    }
                    else if (result.PrimarySamplesUsed > 0 && result.IntermediateSamplesUsed > 0 && result.GlobalSamplesUsed == 0)
                    {
                        builtWithPrimaryAndIntermediate++;
                    }
                    else if (result.PrimarySamplesUsed > 0 && result.FallbackSamplesUsed > 0 && result.GlobalSamplesUsed == 0)
                    {
                        builtWithPrimaryAndFallback++;
                    }
                    else if (result.PrimarySamplesUsed == 0 && result.IntermediateSamplesUsed > 0 && result.FallbackSamplesUsed == 0 && result.GlobalSamplesUsed == 0)
                    {
                        builtWithIntermediateOnly++;
                    }
                    else if (result.IntermediateSamplesUsed > 0 && result.FallbackSamplesUsed > 0 && result.GlobalSamplesUsed == 0)
                    {
                        builtWithIntermediateAndFallback++;
                    }
                    else if (result.PrimarySamplesUsed == 0 && result.FallbackSamplesUsed > 0 && result.GlobalSamplesUsed == 0)
                    {
                        builtWithFallbackOnly++;
                    }

                    if (result.NeighborFilledSamples > 0)
                    {
                        builtWithNeighborFill++;
                    }

                    Console.WriteLine($"  -> Source samples used: {PrimaryDemLabel}={result.PrimarySamplesUsed:N0}, {IntermediateDemLabel}={result.IntermediateSamplesUsed:N0}, {FallbackDemLabel}={result.FallbackSamplesUsed:N0}, {GlobalDemLabel}={result.GlobalSamplesUsed:N0}, neighbor-fill={result.NeighborFilledSamples:N0}");
                    Console.WriteLine($"  -> Generated grid valid={generatedStats.ValidCount:N0}, missing={generatedStats.MissingCount:N0}, min={generatedStats.MinHeight}, max={generatedStats.MaxHeight}");
                }
                catch (Exception ex)
                {
                    failed++;
                    retryableFailedNormalTileNames.Add(GetTerrainTileBaseName(tile));
                    MarkTerrainTileForAppendRetry(tile);
                    Console.WriteLine($"  -> DEM generation failed: {ex.Message}");
                }

                PrintProgressCheckpoint(tileNumber, totalTiles, generatedCount, skipped, failed);
            }

            if (!inspectOnly && rollingWriter is not null && rollingWriter.PendingCount > 0)
            {
                try
                {
                    Console.WriteLine($"\nFlushing {rollingWriter.PendingCount:N0} pending generated tile(s) to {outputDir}...");
                    rollingWriter.FlushAll();
                    Console.WriteLine($"Rolling terrain writer peak memory window: {rollingWriter.PeakPendingCount:N0} tile grids.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: failed while writing generated terrain files: {ex.Message}");
                    return;
                }
            }
        }
        else
        {
            Console.WriteLine("\nRoute tile generation skipped by option.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("STATUS: OPERATION ABORTED");
            return;
        }

        if (createRouteTiles && failed > 0)
        {
            Console.WriteLine($"\nDone. Generated={generatedCount:N0}, skipped={skipped:N0}, failed={failed:N0}, total={totalTiles:N0}.");
            PrintFailedTileTextFileBlock(retryableFailedNormalTileNames);
            PrintUnmappableTileBlock(unmappableFailedNormalTileNames);
            Console.WriteLine("Terrain stage contains failures. DM and map stages were not started; fix the error or run Append first.");
            Console.WriteLine("STATUS: FAILURE - TILES");
            return;
        }

        if (createRouteTiles && !inspectOnly)
        {
            Console.WriteLine("STATUS: TILES - COMPLETE");
        }

        if (distantMountains)
        {
            string loOutputDir = inspectOnly
                ? Path.Combine(Environment.CurrentDirectory, "generated-lo_tiles")
                : Path.Combine(route.RouteDir, "lo_tiles");
            int dmFailures = await GenerateDistantMountainTilesAsync(route, httpClient, loOutputDir, loTileRadius, loSampleOffsetX, loSampleOffsetY, sourceBiasEastMeters, sourceBiasNorthMeters, markerCoverage, trackDatabaseCoverage, kmlCoverage, textFileCoverage, overwriteFlag, demSources, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("STATUS: OPERATION ABORTED");
                return;
            }

            if (dmFailures > 0)
            {
                Console.WriteLine("Distant Mountain stage contains failures. Map generation was not started; fix the error or run Append first.");
                Console.WriteLine("STATUS: FAILURE - DM");
                return;
            }

            Console.WriteLine("STATUS: DM - COMPLETE");
        }

        if (createMapTiles && !inspectOnly)
        {
            if (mapper is null)
            {
                Console.WriteLine("Error: map tiles cannot be created because route geography is unavailable.");
            }
            else
            {
                try
                {
                    await GenerateMapTilesAsync(route, mapper, processingTiles, requestedMapTile, limit, mapCacheOnly, cancellationToken);
                }
                catch
                {
                    Console.WriteLine("STATUS: FAILURE - OSM / MAPS");
                    throw;
                }
            }
        }
        else if (createMapTiles)
        {
            Console.WriteLine("\nMap tiles: inspect-only mode; no PBF download, ACE texture, or terrain material write performed.");
        }

        Console.WriteLine($"\nDone. Generated={generatedCount:N0}, skipped={skipped:N0}, failed={failed:N0}, total={totalTiles:N0}.");
        PrintFailedTileTextFileBlock(retryableFailedNormalTileNames);
        PrintUnmappableTileBlock(unmappableFailedNormalTileNames);
        if (!inspectOnly)
        {
            Console.WriteLine($"Resolution summary: {PrimaryDemLabel} only={builtWithPrimaryOnly:N0}, mixed {PrimaryDemLabel}+{IntermediateDemLabel}={builtWithPrimaryAndIntermediate:N0}, mixed {PrimaryDemLabel}+{FallbackDemLabel}={builtWithPrimaryAndFallback:N0}, {IntermediateDemLabel} only={builtWithIntermediateOnly:N0}, mixed {IntermediateDemLabel}+{FallbackDemLabel}={builtWithIntermediateAndFallback:N0}, {FallbackDemLabel} only={builtWithFallbackOnly:N0}, tiles including {GlobalDemLabel}={builtWithGlobal:N0}, neighbor-filled={builtWithNeighborFill:N0}.");
            Console.WriteLine($"Source use summary: tiles using {PrimaryDemLabel}={tilesUsingPrimary:N0}, {IntermediateDemLabel}={tilesUsingIntermediate:N0}, {FallbackDemLabel}={tilesUsingFallback:N0}, {GlobalDemLabel}={tilesUsingGlobal:N0}.");
        }

        Console.WriteLine("STATUS: OPERATION COMPLETE");
    }

    // Read-only preflight. This validates the selected route/tile set and checks
    // representative USGS product availability before Run is allowed to write.
    internal static async Task<ScanSummary> ScanRouteAsync(string routeDir, ScanOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine("=========================================");
        Console.WriteLine(" SCO LIDEX Route Scan ");
        Console.WriteLine("=========================================\n");
        Console.WriteLine("Scan is read-only. No route files will be created, changed, or deleted.\n");

        bool blockingFailure = false;
        int unreadableRouteTiles = 0;
        int unreadableDmTiles = 0;

        if (!RouteLayout.TryLoad(routeDir, out RouteLayout? route, out string routeError))
        {
            Console.WriteLine(routeError);
            return new ScanSummary(false, 0, 0, 0, 0, false, DemSourcePolicy.None, false, false, false, false, false, false, false);
        }

        PrintRouteSummary(route!);

        int selectionSources = new[] { options.MarkerCoverage, options.TrackDatabaseCoverage, options.KmlCoverage, options.TextFileCoverage }.Count(v => v);
        if (selectionSources > 1)
        {
            Console.WriteLine("Error: choose only one selection source.");
            return new ScanSummary(false, 0, 0, 0, 0, false, DemSourcePolicy.None, false, false, false, false, false, false, false);
        }

        IReadOnlyList<TerrainTile> processingTiles = [];
        if (options.CreateRouteTiles || options.CreateMapTiles)
        {
            try
            {
                processingTiles = GetRouteTileProcessingList(
                    route!,
                    options.MarkerCoverage,
                    options.TrackDatabaseCoverage,
                    options.KmlCoverage,
                    options.TextFileCoverage,
                    options.TerrainRadius);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: selection scan failed: {ex.Message}");
                blockingFailure = true;
            }

            bool createsSelectionCoverage = options.MarkerCoverage || options.TrackDatabaseCoverage || options.KmlCoverage || options.TextFileCoverage;
            string existingQualifier = createsSelectionCoverage ? " currently existing" : "";
            Console.WriteLine($"\nRoute tile scan: {processingTiles.Count:N0}{existingQualifier} selected tile(s).");
            if (createsSelectionCoverage)
            {
                Console.WriteLine("Route tile plan: Run will create and index any missing selected base terrain tiles.");
            }
            SortedSet<string> invalidTiles = new(StringComparer.OrdinalIgnoreCase);
            foreach (TerrainTile tile in processingTiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string baseName = Path.GetFileNameWithoutExtension(tile.TileFile.Name);
                if (!string.Equals(baseName, baseName.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    invalidTiles.Add($"{baseName} (not lower-case)");
                }

                if (baseName.StartsWith('_'))
                {
                    invalidTiles.Add($"{baseName} (unsupported large parent tile)");
                }

                if (!RouteLayout.TryDecodeTileName(tile.TileFile.Name, out _))
                {
                    invalidTiles.Add($"{baseName} (cannot decode tile name)");
                }

                if (tile.WorldTile is null)
                {
                    invalidTiles.Add($"{baseName} (cannot retrieve world position)");
                }

                if (tile.RawHeightPath is null || !File.Exists(tile.RawHeightPath))
                {
                    invalidTiles.Add($"{baseName} (missing _y.raw height grid)");
                    continue;
                }

                RawGridStats? stats = TryGetRawGridStats(tile.RawHeightPath);
                if (stats is null)
                {
                    invalidTiles.Add($"{baseName} (unreadable _y.raw height grid)");
                    continue;
                }

                if (stats.Width != OrtsRawGridSize || stats.Height != OrtsRawGridSize)
                {
                    invalidTiles.Add($"{baseName} (unexpected raw grid {stats.Width}x{stats.Height})");
                }
            }

            unreadableRouteTiles = invalidTiles.Count;
            if (invalidTiles.Count > 0)
            {
                blockingFailure = true;
                Console.WriteLine("Unreadable or unsupported route tiles:");
                foreach (string tile in invalidTiles.Take(80))
                {
                    Console.WriteLine(tile);
                }

                if (invalidTiles.Count > 80)
                {
                    Console.WriteLine($"...and {invalidTiles.Count - 80:N0} more.");
                }
            }
            else
            {
                Console.WriteLine("Route tile scan: all selected tiles are named, decoded, positioned, and readable.");
            }

            if (options.CreateMapTiles)
            {
                string terrainMapsPath = Path.Combine(route!.RouteDir, "terrain_maps");
                if (File.Exists(terrainMapsPath))
                {
                    Console.WriteLine("Error: terrain_maps exists as a file instead of a directory; TSRE F3 map-cache PNG files cannot be written.");
                    blockingFailure = true;
                }
                else
                {
                    Console.WriteLine($"Map reference preflight: {processingTiles.Count:N0} TSRE F3 terrain_maps PNG output(s); terrain .t materials and UVs will remain unchanged.");
                }
            }
        }
        else
        {
            Console.WriteLine("Route tile scan: skipped by option.");
        }

        if (options.CreateRouteTiles && options.CleanTileWipe)
        {
            FileInfo? cleanTemplate = FindGeneratedTerrainTileTemplate();
            if (cleanTemplate is null)
            {
                Console.WriteLine("Error: Clean Tile Wipe is selected, but no clean terrain .t template could be found in generated-tiles beside the executable or working folder.");
                blockingFailure = true;
            }
            else
            {
                Console.WriteLine($"Clean Tile Wipe template: {cleanTemplate.FullName}");
            }
        }

        HashSet<LoTileCoordinate> dmCoverage = [];
        if (options.CreateDistantMountains)
        {
            try
            {
                dmCoverage = BuildDistantMountainCoverage(
                    route!,
                    Math.Max(0, options.LoTileRadius),
                    options.MarkerCoverage,
                    options.TrackDatabaseCoverage,
                    options.KmlCoverage,
                    options.TextFileCoverage);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: Distant Mountain selection scan failed: {ex.Message}");
                blockingFailure = true;
            }

            Console.WriteLine($"Distant Mountain scan: {dmCoverage.Count:N0} selected lo_tile(s).");
            string loOutputDir = Path.Combine(route!.RouteDir, "lo_tiles");
            if (HasDemexStyleDistantMountainFiles(loOutputDir))
            {
                Console.WriteLine("Warning: DEMEX-style lo_tiles detected. Run will purge them and rebuild TSRE-style lo_tiles.");
            }

            if (FindLoTileTemplate(loOutputDir, null) is null)
            {
                Console.WriteLine("Error: no TSRE-style lo_tile .t template could be found.");
                unreadableDmTiles++;
                blockingFailure = true;
            }
        }
        else
        {
            Console.WriteLine("Distant Mountain scan: skipped by option.");
        }

        if (options.CreateRouteTiles && processingTiles.Count == 0 && options.CreateDistantMountains && dmCoverage.Count == 0)
        {
            Console.WriteLine("Error: selection did not produce any route or Distant Mountain tiles.");
            blockingFailure = true;
        }
        else if (options.CreateRouteTiles && processingTiles.Count == 0 && !options.CreateDistantMountains)
        {
            Console.WriteLine("Error: selection did not produce any route tiles.");
            blockingFailure = true;
        }
        else if (!options.CreateRouteTiles && options.CreateDistantMountains && dmCoverage.Count == 0)
        {
            Console.WriteLine("Error: selection did not produce any Distant Mountain tiles.");
            blockingFailure = true;
        }
        else if (!options.CreateRouteTiles && !options.CreateDistantMountains && !options.CreateMapTiles)
        {
            Console.WriteLine("Error: no output option is selected.");
            blockingFailure = true;
        }

        bool hasWarnings = false;
        DemSourcePolicy demSources = DemSourcePolicy.None;
        bool routeCanRun = false;
        bool distantMountainCanRun = false;
        MapSourceAvailability mapSource = new(false, false, false, "not selected");
        UsgsDatasetAvailability primaryStatus = new(false, 0, "not tested");
        UsgsDatasetAvailability intermediateStatus = new(false, 0, "not tested");
        UsgsDatasetAvailability fallbackStatus = new(false, 0, "not tested");
        SourceAvailability globalStatus = new(false, false, "not tested");

        GeoTileMapper? mapper = GeoTileMapper.TryCreate(route!);
        if (mapper is null)
        {
            Console.WriteLine("Error: could not create route geographic mapper.");
            blockingFailure = true;
        }
        else
        {
            PrintProjectionSummary(mapper);
            if (options.CreateMapTiles)
            {
                try
                {
                    TerrainTile? mapTile = processingTiles.FirstOrDefault(t => t.WorldTile is not null);
                    if (mapTile is null)
                    {
                        throw new InvalidOperationException("selection has no geographically positioned normal terrain tile");
                    }
                    ValidateMapProjectionAlignment(mapper, mapTile.WorldTile!);
                    mapSource = await ScanMapTileSourceAsync(route!.RouteDir, mapper, processingTiles, cancellationToken);
                    hasWarnings |= mapSource.HasWarning || !mapSource.CanRun;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Map source: FAILED ({ex.Message}).");
                    mapSource = new MapSourceAvailability(false, false, true, ex.Message);
                    hasWarnings = true;
                }
            }

            using HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
            GeoSampleGrid? sourceGrid = null;
            if (processingTiles.FirstOrDefault(t => t.WorldTile is not null) is TerrainTile firstTile)
            {
                sourceGrid = mapper.GetSampleGrid(firstTile.WorldTile!, 0, 0, 1, 1, 0, 0);
            }
            else if (dmCoverage.Count > 0)
            {
                LoTileCoordinate loTile = dmCoverage.OrderBy(t => t.X).ThenBy(t => t.Z).First();
                sourceGrid = mapper.GetAreaSampleGrid(
                    loTile.X + ((LoTileNormalTileSpan - 1) / 2.0),
                    loTile.Z + ((LoTileNormalTileSpan - 1) / 2.0),
                    LoTileSizeMeters,
                    LoTileSizeMeters,
                    LoRawGridSize,
                    0,
                    0,
                    0,
                    0);
            }

            if (sourceGrid is not null)
            {
                primaryStatus = await TestUsgsDatasetAsync(httpClient, sourceGrid, PrimaryDemDataset, cancellationToken);
                intermediateStatus = await TestUsgsDatasetAsync(httpClient, sourceGrid, IntermediateDemDataset, cancellationToken);
                fallbackStatus = await TestUsgsDatasetAsync(httpClient, sourceGrid, FallbackDemDataset, cancellationToken);
                globalStatus = await TestCopernicusDatasetAsync(httpClient, sourceGrid, cancellationToken);
                demSources = new DemSourcePolicy(
                    primaryStatus.ServiceAvailable && primaryStatus.ItemCount > 0,
                    intermediateStatus.ServiceAvailable && intermediateStatus.ItemCount > 0,
                    fallbackStatus.ServiceAvailable && fallbackStatus.ItemCount > 0,
                    globalStatus.ServiceAvailable && globalStatus.CoverageAvailable);
                hasWarnings |= !demSources.UsePrimary || !demSources.UseIntermediate ||
                    !demSources.UseFallback || !demSources.UseGlobal;
            }

            routeCanRun = options.CreateRouteTiles && demSources.HasAny;
            distantMountainCanRun = options.CreateDistantMountains && (demSources.UseFallback || demSources.UseGlobal);
        }

        bool anySelectedStageCanRun = routeCanRun || distantMountainCanRun || (options.CreateMapTiles && mapSource.CanRun);
        if (!anySelectedStageCanRun)
        {
            Console.WriteLine("Error: none of the selected production stages has an available source path.");
            blockingFailure = true;
        }

        hasWarnings |= (options.CreateRouteTiles && !routeCanRun) ||
            (options.CreateDistantMountains && !distantMountainCanRun) ||
            (options.CreateMapTiles && !mapSource.CanRun);

        Console.WriteLine("\n=========================================");
        Console.WriteLine(" SCAN DATA SOURCE STATUS ");
        Console.WriteLine("=========================================");
        PrintDataSourceStatus("Terrain DEM - 1m", "USGS - The National Map", demSources.UsePrimary);
        Console.WriteLine($"Details: {FormatUsgsScanDetail(primaryStatus, demSources.UsePrimary)}");
        PrintDataSourceStatus("Terrain DEM - approximately 5m", "USGS - The National Map", demSources.UseIntermediate);
        Console.WriteLine($"Details: {FormatUsgsScanDetail(intermediateStatus, demSources.UseIntermediate)}");
        PrintDataSourceStatus("Terrain DEM - 10m", "USGS - The National Map", demSources.UseFallback);
        Console.WriteLine($"Details: {FormatUsgsScanDetail(fallbackStatus, demSources.UseFallback)}");
        PrintDataSourceStatus("Global terrain DEM - 30m", "Copernicus GLO-30", demSources.UseGlobal);
        Console.WriteLine($"Details: {FormatGlobalScanDetail(globalStatus, demSources.UseGlobal)}");
        if (options.CreateMapTiles)
        {
            PrintDataSourceStatus(
                "OpenStreetMap regional extract",
                mapSource.CacheOnly ? "Local Geofabrik PBF cache" : "Geofabrik",
                mapSource.CanRun);
            Console.WriteLine($"Details: {mapSource.Detail}");
        }

        Console.WriteLine("\nRun plan:");
        Console.WriteLine($"  Normal terrain: {StagePlanText(options.CreateRouteTiles, routeCanRun)}");
        Console.WriteLine($"  Distant Mountains: {StagePlanText(options.CreateDistantMountains, distantMountainCanRun)}");
        Console.WriteLine($"  Map overlays: {StagePlanText(options.CreateMapTiles, mapSource.CanRun)}{(mapSource.CacheOnly ? " (cached PBF; no Geofabrik polling)" : "")}");

        string result = blockingFailure ? "FAILED" : hasWarnings ? "PASSED WITH WARNINGS" : "PASSED";
        Console.WriteLine($"\nScan result: {result}");
        return new ScanSummary(
            !blockingFailure,
            options.CreateRouteTiles ? processingTiles.Count : 0,
            options.CreateDistantMountains ? dmCoverage.Count : 0,
            unreadableRouteTiles,
            unreadableDmTiles,
            hasWarnings,
            demSources,
            primaryStatus.ServiceAvailable,
            intermediateStatus.ServiceAvailable,
            fallbackStatus.ServiceAvailable,
            routeCanRun,
            distantMountainCanRun,
            options.CreateMapTiles && mapSource.CanRun,
            mapSource.CacheOnly);
    }

    // Offset test tool. This does not contact USGS; it resamples existing _y.raw
    // grids so a route builder can experiment with a bias before rerunning DEM
    // generation with the chosen offset for the cleanest result.
    internal static Task PostProcessTerrainShiftAsync(
        string routeDir,
        PostProcessSelectionOptions options,
        double shiftEastMeters,
        double shiftNorthMeters,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("=========================================");
        Console.WriteLine(" SCO LIDEX Terrain Post Process ");
        Console.WriteLine("=========================================\n");
        Console.WriteLine("Post Process uses existing route height grids only. No USGS requests will be made.\n");
        Console.WriteLine($"Requested terrain shift: east={shiftEastMeters:F0}m, north={shiftNorthMeters:F0}m");

        if (Math.Abs(shiftEastMeters) < 0.001 && Math.Abs(shiftNorthMeters) < 0.001)
        {
            Console.WriteLine("No shift requested; nothing to do.");
            return Task.CompletedTask;
        }

        if (!RouteLayout.TryLoad(routeDir, out RouteLayout? route, out string routeError) || route is null)
        {
            Console.WriteLine(routeError);
            return Task.CompletedTask;
        }

        int totalWritten = 0;
        int totalFailed = 0;

        if (options.ShiftRouteTiles)
        {
            (int written, int failed) = PostProcessRouteTerrainShift(route, options, shiftEastMeters, shiftNorthMeters, cancellationToken);
            totalWritten += written;
            totalFailed += failed;
        }
        else
        {
            Console.WriteLine("Normal terrain Post Process skipped by option.");
        }

        if (options.ShiftDistantMountains)
        {
            (int written, int failed) = PostProcessDistantMountainShift(route, options, shiftEastMeters, shiftNorthMeters, cancellationToken);
            totalWritten += written;
            totalFailed += failed;
        }
        else
        {
            Console.WriteLine("Distant Mountain Post Process skipped by option.");
        }

        Console.WriteLine($"\nPost Process done. Written={totalWritten:N0}, failed={totalFailed:N0}.");
        return Task.CompletedTask;
    }

    private static (int Written, int Failed) PostProcessRouteTerrainShift(
        RouteLayout route,
        PostProcessSelectionOptions options,
        double shiftEastMeters,
        double shiftNorthMeters,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TerrainTile> selectedTiles = GetRouteTileProcessingList(
            route,
            options.MarkerCoverage,
            options.TrackDatabaseCoverage,
            options.KmlCoverage,
            options.TextFileCoverage,
            options.TerrainRadius);

        selectedTiles = selectedTiles
            .Where(t => t.WorldTile is not null && !string.IsNullOrWhiteSpace(t.RawHeightPath))
            .ToList();

        if (selectedTiles.Count == 0)
        {
            Console.WriteLine("Error: selection did not produce any writable normal terrain tiles.");
            return (0, 1);
        }

        Console.WriteLine($"Selected normal terrain tiles: {selectedTiles.Count:N0}");
        HashSet<(int X, int Z)> neededSourceTiles = BuildPostProcessSourceTileSet(selectedTiles);
        Dictionary<(int X, int Z), DecodedRawTile> sourceTiles = [];
        foreach (TerrainTile tile in route.TerrainTiles)
        {
            if (tile.WorldTile is null || string.IsNullOrWhiteSpace(tile.RawHeightPath))
            {
                continue;
            }

            (int X, int Z) key = (tile.WorldTile.X, tile.WorldTile.Z);
            if (!neededSourceTiles.Contains(key))
            {
                continue;
            }

            if (TryReadDecodedRawTile(tile, out DecodedRawTile? decoded, out string error) && decoded is not null)
            {
                sourceTiles[key] = decoded;
            }
            else
            {
                Console.WriteLine($"  -> Source read skipped {tile.TileFile.Name}: {error}");
            }
        }

        Console.WriteLine($"Loaded source height grids: {sourceTiles.Count:N0}");
        if (sourceTiles.Count == 0)
        {
            Console.WriteLine("Error: no source height grids could be read.");
            return (0, 1);
        }

        Dictionary<(int X, int Z), short[,]> shiftedGrids = [];
        int processed = 0;
        int failed = 0;
        double shiftSamplesX = shiftEastMeters / OrtsPostSpacingMeters;
        double shiftSamplesZ = shiftNorthMeters / OrtsPostSpacingMeters;

        foreach (TerrainTile tile in selectedTiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            WorldTile worldTile = tile.WorldTile!;
            Console.WriteLine($"\n[Post {processed:N0}/{selectedTiles.Count:N0}] {tile.TileFile.Name}");

            if (!TryReadDecodedRawTile(tile, out DecodedRawTile? target, out string targetError) || target is null)
            {
                failed++;
                Console.WriteLine($"  -> Failed: {targetError}");
                continue;
            }

            short[,] shifted = ShiftDecodedTile(worldTile.X, worldTile.Z, target.Heights, sourceTiles, shiftSamplesX, shiftSamplesZ);
            shiftedGrids[(worldTile.X, worldTile.Z)] = shifted;
            Console.WriteLine("  -> Prepared shifted height grid.");
        }

        MergeSharedEdges(shiftedGrids);

        int written = 0;
        foreach (TerrainTile tile in selectedTiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorldTile worldTile = tile.WorldTile!;
            if (!shiftedGrids.TryGetValue((worldTile.X, worldTile.Z), out short[,]? shifted))
            {
                continue;
            }

            if (!TryReadTerrainSampleEncoding(tile.TileFile.FullName, out TerrainSampleEncoding encoding))
            {
                failed++;
                Console.WriteLine($"  -> Failed writing {tile.TileFile.Name}: could not read terrain sample encoding.");
                continue;
            }

            try
            {
                EnsureExactFileNameCasing(tile.RawHeightPath!);
                WriteRawGrid(tile.RawHeightPath!, shifted, encoding);
                written++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  -> Failed writing {tile.TileFile.Name}: {ex.Message}");
            }
        }

        Console.WriteLine($"\nNormal terrain Post Process done. Written={written:N0}, failed={failed:N0}, total={selectedTiles.Count:N0}.");
        return (written, failed);
    }

    private static (int Written, int Failed) PostProcessDistantMountainShift(
        RouteLayout route,
        PostProcessSelectionOptions options,
        double shiftEastMeters,
        double shiftNorthMeters,
        CancellationToken cancellationToken)
    {
        string loTilesDir = Path.Combine(route.RouteDir, "lo_tiles");
        if (!Directory.Exists(loTilesDir))
        {
            Console.WriteLine("Distant Mountain Post Process skipped: no lo_tiles folder.");
            return (0, 0);
        }

        HashSet<LoTileCoordinate> selectedLoTiles = BuildDistantMountainCoverage(
            route,
            Math.Max(0, options.LoTileRadius),
            options.MarkerCoverage,
            options.TrackDatabaseCoverage,
            options.KmlCoverage,
            options.TextFileCoverage);
        selectedLoTiles.IntersectWith(EnumerateTsreLoTiles(loTilesDir));
        if (selectedLoTiles.Count == 0)
        {
            Console.WriteLine("Distant Mountain Post Process skipped: no selected existing TSRE-style lo_tiles.");
            return (0, 0);
        }

        Console.WriteLine($"\nSelected DM tiles: {selectedLoTiles.Count:N0}");
        HashSet<(int X, int Z)> neededSourceTiles = [];
        foreach (LoTileCoordinate tile in selectedLoTiles)
        {
            int loX = tile.X / LoTileNormalTileSpan;
            int loZ = tile.Z / LoTileNormalTileSpan;
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    neededSourceTiles.Add((loX + dx, loZ + dz));
                }
            }
        }

        Dictionary<(int X, int Z), DecodedLoRawTile> sourceTiles = [];
        foreach (LoTileCoordinate tile in EnumerateTsreLoTiles(loTilesDir))
        {
            int loX = tile.X / LoTileNormalTileSpan;
            int loZ = tile.Z / LoTileNormalTileSpan;
            if (!neededSourceTiles.Contains((loX, loZ)))
            {
                continue;
            }

            if (TryReadDecodedLoRawTile(loTilesDir, tile, out DecodedLoRawTile? decoded, out string error) && decoded is not null)
            {
                sourceTiles[(loX, loZ)] = decoded;
            }
            else
            {
                Console.WriteLine($"  -> DM source read skipped {LoTileNameFromTileXZ(tile.X, tile.Z)}.t: {error}");
            }
        }

        Console.WriteLine($"Loaded DM source height grids: {sourceTiles.Count:N0}");
        if (sourceTiles.Count == 0)
        {
            Console.WriteLine("Error: no DM source height grids could be read.");
            return (0, 1);
        }

        Dictionary<(int X, int Z), short[,]> shiftedGrids = [];
        int processed = 0;
        int failed = 0;
        double loSpacingMeters = LoTileSizeMeters / (LoRawGridSize - 1);
        double shiftSamplesX = shiftEastMeters / loSpacingMeters;
        double shiftSamplesZ = shiftNorthMeters / loSpacingMeters;

        foreach (LoTileCoordinate tile in selectedLoTiles.OrderBy(t => t.X).ThenBy(t => t.Z))
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            int loX = tile.X / LoTileNormalTileSpan;
            int loZ = tile.Z / LoTileNormalTileSpan;
            string loName = LoTileNameFromTileXZ(tile.X, tile.Z);
            Console.WriteLine($"\n[Post DM {processed:N0}/{selectedLoTiles.Count:N0}] {loName}.t");

            if (!TryReadDecodedLoRawTile(loTilesDir, tile, out DecodedLoRawTile? target, out string targetError) || target is null)
            {
                failed++;
                Console.WriteLine($"  -> Failed: {targetError}");
                continue;
            }

            short[,] shifted = ShiftDecodedLoTile(loX, loZ, target.Heights, sourceTiles, shiftSamplesX, shiftSamplesZ);
            shiftedGrids[(loX, loZ)] = shifted;
            Console.WriteLine("  -> Prepared shifted DM height grid.");
        }

        MergeSharedEdges(shiftedGrids);

        int written = 0;
        foreach (LoTileCoordinate tile in selectedLoTiles.OrderBy(t => t.X).ThenBy(t => t.Z))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int loX = tile.X / LoTileNormalTileSpan;
            int loZ = tile.Z / LoTileNormalTileSpan;
            if (!shiftedGrids.TryGetValue((loX, loZ), out short[,]? shifted))
            {
                continue;
            }

            string loName = LoTileNameFromTileXZ(tile.X, tile.Z);
            string tilePath = Path.Combine(loTilesDir, loName + ".t");
            string heightPath = Path.Combine(loTilesDir, loName + "_y.raw");
            if (!TryReadTerrainSampleEncoding(tilePath, out TerrainSampleEncoding encoding))
            {
                failed++;
                Console.WriteLine($"  -> Failed writing {loName}.t: could not read terrain sample encoding.");
                continue;
            }

            try
            {
                EnsureExactFileNameCasing(heightPath);
                WriteEncodedHeightGrid(heightPath, shifted, encoding);
                written++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  -> Failed writing {loName}.t: {ex.Message}");
            }
        }

        Console.WriteLine($"\nDistant Mountain Post Process done. Written={written:N0}, failed={failed:N0}, total={selectedLoTiles.Count:N0}.");
        return (written, failed);
    }

    private static HashSet<(int X, int Z)> BuildPostProcessSourceTileSet(IEnumerable<TerrainTile> selectedTiles)
    {
        HashSet<(int X, int Z)> needed = [];
        foreach (TerrainTile tile in selectedTiles)
        {
            if (tile.WorldTile is null)
            {
                continue;
            }

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    needed.Add((tile.WorldTile.X + dx, tile.WorldTile.Z + dz));
                }
            }
        }

        return needed;
    }

    private static bool TryReadDecodedRawTile(TerrainTile tile, out DecodedRawTile? decoded, out string error)
    {
        decoded = null;
        if (tile.WorldTile is null)
        {
            error = "tile is not matched to a world tile";
            return false;
        }

        if (string.IsNullOrWhiteSpace(tile.RawHeightPath) || !File.Exists(tile.RawHeightPath))
        {
            error = "missing _y.raw height grid";
            return false;
        }

        if (!TryReadTerrainSampleEncoding(tile.TileFile.FullName, out TerrainSampleEncoding encoding))
        {
            error = "could not read terrain sample encoding";
            return false;
        }

        byte[] bytes = File.ReadAllBytes(tile.RawHeightPath);
        int expectedBytes = OrtsRawGridSize * OrtsRawGridSize * sizeof(short);
        if (bytes.Length != expectedBytes)
        {
            error = $"expected {expectedBytes} bytes in raw grid, found {bytes.Length}";
            return false;
        }

        short[,] heights = new short[OrtsRawGridSize, OrtsRawGridSize];
        int offset = 0;
        for (int y = 0; y < OrtsRawGridSize; y++)
        {
            for (int x = 0; x < OrtsRawGridSize; x++)
            {
                short signedRaw = BitConverter.ToInt16(bytes, offset);
                ushort raw = BitConverter.ToUInt16(bytes, offset);
                heights[y, x] = signedRaw == RawMissingHeight
                    ? RawMissingHeight
                    : ClampToInt16Meters(encoding.Floor + (raw * encoding.Scale));
                offset += sizeof(short);
            }
        }

        decoded = new DecodedRawTile(tile.WorldTile.X, tile.WorldTile.Z, heights, encoding);
        error = "";
        return true;
    }

    private static short[,] ShiftDecodedTile(
        int tileX,
        int tileZ,
        short[,] original,
        IReadOnlyDictionary<(int X, int Z), DecodedRawTile> sourceTiles,
        double shiftSamplesX,
        double shiftSamplesZ)
    {
        short[,] shifted = new short[OrtsRawGridSize, OrtsRawGridSize];
        for (int y = 0; y < OrtsRawGridSize; y++)
        {
            for (int x = 0; x < OrtsRawGridSize; x++)
            {
                double globalX = (tileX * OrtsRawGridSize) + x;
                double globalZ = (tileZ * OrtsRawGridSize) + (OrtsRawGridSize - 1 - y);
                short sampled = SampleDecodedHeight(sourceTiles, globalX - shiftSamplesX, globalZ - shiftSamplesZ);
                shifted[y, x] = sampled == RawMissingHeight ? original[y, x] : sampled;
            }
        }

        return shifted;
    }

    private static short SampleDecodedHeight(
        IReadOnlyDictionary<(int X, int Z), DecodedRawTile> sourceTiles,
        double globalX,
        double globalZ)
    {
        int x0 = (int)Math.Floor(globalX);
        int z0 = (int)Math.Floor(globalZ);
        int x1 = x0 + 1;
        int z1 = z0 + 1;
        double fx = globalX - x0;
        double fz = globalZ - z0;

        (short Height, double Weight)[] samples =
        [
            (GetDecodedPost(sourceTiles, x0, z0), (1 - fx) * (1 - fz)),
            (GetDecodedPost(sourceTiles, x1, z0), fx * (1 - fz)),
            (GetDecodedPost(sourceTiles, x0, z1), (1 - fx) * fz),
            (GetDecodedPost(sourceTiles, x1, z1), fx * fz),
        ];

        double sum = 0;
        double weight = 0;
        foreach ((short height, double sampleWeight) in samples)
        {
            if (height == RawMissingHeight || sampleWeight <= 0)
            {
                continue;
            }

            sum += height * sampleWeight;
            weight += sampleWeight;
        }

        return weight <= 0 ? RawMissingHeight : ClampToInt16Meters(sum / weight);
    }

    private static short GetDecodedPost(IReadOnlyDictionary<(int X, int Z), DecodedRawTile> sourceTiles, int globalX, int globalZ)
    {
        int tileX = FloorDiv(globalX, OrtsRawGridSize);
        int tileZ = FloorDiv(globalZ, OrtsRawGridSize);
        int localX = globalX - (tileX * OrtsRawGridSize);
        int localNorth = globalZ - (tileZ * OrtsRawGridSize);
        int y = OrtsRawGridSize - 1 - localNorth;
        return sourceTiles.TryGetValue((tileX, tileZ), out DecodedRawTile? tile)
            ? tile.Heights[y, localX]
            : RawMissingHeight;
    }

    private static bool TryReadDecodedLoRawTile(string loTilesDir, LoTileCoordinate tile, out DecodedLoRawTile? decoded, out string error)
    {
        decoded = null;
        string loName = LoTileNameFromTileXZ(tile.X, tile.Z);
        string tilePath = Path.Combine(loTilesDir, loName + ".t");
        string heightPath = Path.Combine(loTilesDir, loName + "_y.raw");
        if (!File.Exists(tilePath))
        {
            error = "missing lo_tile .t file";
            return false;
        }

        if (!File.Exists(heightPath))
        {
            error = "missing lo_tile _y.raw height grid";
            return false;
        }

        if (!TryReadTerrainSampleEncoding(tilePath, out TerrainSampleEncoding encoding))
        {
            error = "could not read terrain sample encoding";
            return false;
        }

        byte[] bytes = File.ReadAllBytes(heightPath);
        int expectedBytes = LoRawGridSize * LoRawGridSize * sizeof(short);
        if (bytes.Length != expectedBytes)
        {
            error = $"expected {expectedBytes} bytes in raw grid, found {bytes.Length}";
            return false;
        }

        short[,] heights = new short[LoRawGridSize, LoRawGridSize];
        int offset = 0;
        for (int y = 0; y < LoRawGridSize; y++)
        {
            for (int x = 0; x < LoRawGridSize; x++)
            {
                ushort raw = BitConverter.ToUInt16(bytes, offset);
                heights[y, x] = raw == 0
                    ? RawMissingHeight
                    : ClampToInt16Meters(encoding.Floor + (raw * encoding.Scale));
                offset += sizeof(short);
            }
        }

        decoded = new DecodedLoRawTile(tile.X / LoTileNormalTileSpan, tile.Z / LoTileNormalTileSpan, heights);
        error = "";
        return true;
    }

    private static short[,] ShiftDecodedLoTile(
        int loTileX,
        int loTileZ,
        short[,] original,
        IReadOnlyDictionary<(int X, int Z), DecodedLoRawTile> sourceTiles,
        double shiftSamplesX,
        double shiftSamplesZ)
    {
        short[,] shifted = new short[LoRawGridSize, LoRawGridSize];
        for (int y = 0; y < LoRawGridSize; y++)
        {
            for (int x = 0; x < LoRawGridSize; x++)
            {
                double globalX = (loTileX * LoRawGridSize) + x;
                double globalZ = (loTileZ * LoRawGridSize) + (LoRawGridSize - 1 - y);
                short sampled = SampleDecodedLoHeight(sourceTiles, globalX - shiftSamplesX, globalZ - shiftSamplesZ);
                shifted[y, x] = sampled == RawMissingHeight ? original[y, x] : sampled;
            }
        }

        return shifted;
    }

    private static short SampleDecodedLoHeight(
        IReadOnlyDictionary<(int X, int Z), DecodedLoRawTile> sourceTiles,
        double globalX,
        double globalZ)
    {
        int x0 = (int)Math.Floor(globalX);
        int z0 = (int)Math.Floor(globalZ);
        int x1 = x0 + 1;
        int z1 = z0 + 1;
        double fx = globalX - x0;
        double fz = globalZ - z0;

        (short Height, double Weight)[] samples =
        [
            (GetDecodedLoPost(sourceTiles, x0, z0), (1 - fx) * (1 - fz)),
            (GetDecodedLoPost(sourceTiles, x1, z0), fx * (1 - fz)),
            (GetDecodedLoPost(sourceTiles, x0, z1), (1 - fx) * fz),
            (GetDecodedLoPost(sourceTiles, x1, z1), fx * fz),
        ];

        double sum = 0;
        double weight = 0;
        foreach ((short height, double sampleWeight) in samples)
        {
            if (height == RawMissingHeight || sampleWeight <= 0)
            {
                continue;
            }

            sum += height * sampleWeight;
            weight += sampleWeight;
        }

        return weight <= 0 ? RawMissingHeight : ClampToInt16Meters(sum / weight);
    }

    private static short GetDecodedLoPost(IReadOnlyDictionary<(int X, int Z), DecodedLoRawTile> sourceTiles, int globalX, int globalZ)
    {
        int tileX = FloorDiv(globalX, LoRawGridSize);
        int tileZ = FloorDiv(globalZ, LoRawGridSize);
        int localX = globalX - (tileX * LoRawGridSize);
        int localNorth = globalZ - (tileZ * LoRawGridSize);
        int y = LoRawGridSize - 1 - localNorth;
        return sourceTiles.TryGetValue((tileX, tileZ), out DecodedLoRawTile? tile)
            ? tile.Heights[y, localX]
            : RawMissingHeight;
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder != 0 && ((remainder < 0) != (divisor < 0)) ? quotient - 1 : quotient;
    }

    private static async Task<UsgsDatasetAvailability> TestUsgsDatasetAsync(HttpClient client, GeoSampleGrid sampleGrid, string datasetName, CancellationToken cancellationToken)
    {
        (double minLon, double minLat, double maxLon, double maxLat) = sampleGrid.BoundingBox;
        string apiUrl =
            "https://tnmaccess.nationalmap.gov/api/v1/products" +
            $"?bbox={minLon.ToString(CultureInfo.InvariantCulture)},{minLat.ToString(CultureInfo.InvariantCulture)},{maxLon.ToString(CultureInfo.InvariantCulture)},{maxLat.ToString(CultureInfo.InvariantCulture)}" +
            $"&prodFormats=GeoTIFF&outputFormat=JSON&datasets={Uri.EscapeDataString(datasetName)}";

        try
        {
            using HttpResponseMessage response = await client.GetAsync(apiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"USGS {GetDemSourceDisplayName(datasetName)}: FAILED ({(int)response.StatusCode} {response.ReasonPhrase}).");
                return new UsgsDatasetAvailability(false, 0, $"{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = ParseUsgsProductJson(json);
            int itemCount = doc.RootElement.TryGetProperty("items", out JsonElement items) ? items.GetArrayLength() : 0;
            Console.WriteLine($"USGS {GetDemSourceDisplayName(datasetName)}: active, {itemCount:N0} product(s) for representative bbox.");
            return new UsgsDatasetAvailability(true, itemCount, $"active, {itemCount:N0} representative product(s)");
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"USGS {GetDemSourceDisplayName(datasetName)}: FAILED ({ex.Message}).");
            return new UsgsDatasetAvailability(false, 0, ex.Message);
        }
    }

    private static void PrintDataSourceStatus(string data, string source, bool passed)
    {
        Console.WriteLine();
        Console.WriteLine($"Data:   {data}");
        Console.WriteLine($"Source: {source}");
        Console.WriteLine($"Status: {(passed ? "PASSED" : "FAILED")}");
    }

    private static string FormatUsgsScanDetail(UsgsDatasetAvailability status, bool enabledForRun)
    {
        if (!status.ServiceAvailable)
        {
            return $"SERVICE UNAVAILABLE; disabled for Run. {status.Detail}";
        }

        return status.ItemCount > 0
            ? $"SERVICE ONLINE / ROUTE COVERAGE AVAILABLE; {status.ItemCount:N0} representative product(s); {(enabledForRun ? "enabled for Run" : "not used")}."
            : "SERVICE ONLINE / NO ROUTE COVERAGE; disabled for Run and will not be polled tile by tile.";
    }

    private static string FormatUsgsRunDetail(bool enabledForRun, bool serviceAvailable) =>
        enabledForRun
            ? serviceAvailable
                ? "SERVICE ONLINE / ROUTE COVERAGE AVAILABLE; enabled for Run."
                : "Enabled by Scan Override or CLI; service availability was not preflighted."
            : serviceAvailable
                ? "SERVICE ONLINE / NO ROUTE COVERAGE; disabled for Run."
                : "SERVICE UNAVAILABLE; disabled for Run.";

    private static string FormatGlobalScanDetail(SourceAvailability status, bool enabledForRun)
    {
        if (!status.ServiceAvailable)
        {
            return $"Unavailable and disabled for Run. {status.Detail}";
        }

        return status.CoverageAvailable
            ? $"{(enabledForRun ? "Enabled for Run" : "Not used")}. {status.Detail}."
            : $"No representative land coverage; not used. {status.Detail}.";
    }

    private static string StagePlanText(bool selected, bool canRun) =>
        !selected ? "NOT SELECTED" : canRun ? "ENABLED" : "SKIPPED - required source unavailable";

    private static void PrintProgressCheckpoint(int processed, int total, int generated, int skipped, int failed)
    {
        if (processed == total || processed % 10 == 0)
        {
            Console.WriteLine($"  -> Progress: {processed:N0}/{total:N0} processed, {total - processed:N0} remaining, {generated:N0} generated, {skipped:N0} skipped, {failed:N0} failed.");
        }
    }

    private static string GetTerrainTileBaseName(TerrainTile tile)
    {
        return Path.GetFileNameWithoutExtension(tile.TileFile.Name).ToLowerInvariant();
    }

    private static void PrintFailedTileTextFileBlock(SortedSet<string> failedTileNames)
    {
        if (failedTileNames.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Failed tiles for SCOLIDEXTiles.txt:");
        Console.WriteLine("# Paste the tile names below into ROUTE\\SCOLIDEXTiles.txt and run Use Text File + Append.");
        foreach (string tileName in failedTileNames)
        {
            Console.WriteLine(tileName);
        }
    }

    private static void PrintUnmappableTileBlock(SortedSet<string> failedTileNames)
    {
        if (failedTileNames.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Unmappable failed tiles:");
        Console.WriteLine("# These tiles could not be matched to ORTS world tile coordinates.");
        Console.WriteLine("# A later Append/Text File retry cannot process them until the route has matching world files or SCO LIDEX supports this tile naming depth.");
        foreach (string tileName in failedTileNames)
        {
            Console.WriteLine(tileName);
        }
    }

    private static int ParseIntOption(string[] args, string optionName, int defaultValue)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, optionName, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return defaultValue;
        }

        return value;
    }

    private static double ParseDoubleOption(string[] args, string optionName, double defaultValue)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, optionName, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !double.TryParse(args[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return defaultValue;
        }

        return value;
    }

    private static string? ParseStringOption(string[] args, string optionName)
    {
        int index = Array.FindIndex(args, a => string.Equals(a, optionName, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // Coverage helpers create any missing flat route tiles before DEM work starts.
    // Radius-based sources expand from a center tile; text-file coverage is exact.
}
