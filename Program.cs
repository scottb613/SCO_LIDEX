// SCO LIDEX - Open Rails / MSTS Cloud Terrain Builder
// Copyright (C) Scott Brunner, Beast of Burden
//
// This file contains the terrain generation engine: route/tile discovery,
// projection mapping, USGS DEM product lookup, GDAL raster sampling, seamless
// tile merging, distant mountain generation, and command-line entry points.
// SCO LIDEX is distributed under GNU GPL v3 or later. See LICENSE.txt.

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
using OSGeo.OSR;

namespace ORterr;

internal static partial class Program
{
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
    private const ushort TokenTerrainSampleFloor = 142;
    private const ushort TokenTerrainSampleScale = 143;
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
        bool MarkerCoverage,
        bool TrackDatabaseCoverage,
        bool KmlCoverage,
        bool TextFileCoverage,
        bool CleanTileWipe,
        int TerrainRadius,
        int LoTileRadius);

    internal sealed record ScanSummary(
        bool CanRun,
        int RouteTileTotal,
        int DistantMountainTotal,
        int UnreadableRouteTiles,
        int UnreadableDistantMountainTiles);

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
    private static async Task Main(string[] args)
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

        AttachConsoleForCommandLine();
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
        int builtWithNeighborFill = 0;
        int tilesUsingPrimary = 0;
        int tilesUsingIntermediate = 0;
        int tilesUsingFallback = 0;
        SortedSet<string> retryableFailedNormalTileNames = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> unmappableFailedNormalTileNames = new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<TerrainTile> processingTiles = GetRouteTileProcessingList(route!, markerCoverage, trackDatabaseCoverage, kmlCoverage, textFileCoverage, terrainRadius);
        int totalTiles = processingTiles.Count;

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
                    TerrainGenerationResult result = await StreamOrtsGridForSampleGridAsync(httpClient, sampleGrid);
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

                    if (result.PrimarySamplesUsed > 0 && result.IntermediateSamplesUsed == 0 && result.FallbackSamplesUsed == 0)
                    {
                        builtWithPrimaryOnly++;
                    }
                    else if (result.PrimarySamplesUsed > 0 && result.IntermediateSamplesUsed > 0)
                    {
                        builtWithPrimaryAndIntermediate++;
                    }
                    else if (result.PrimarySamplesUsed > 0 && result.FallbackSamplesUsed > 0)
                    {
                        builtWithPrimaryAndFallback++;
                    }
                    else if (result.PrimarySamplesUsed == 0 && result.IntermediateSamplesUsed > 0 && result.FallbackSamplesUsed == 0)
                    {
                        builtWithIntermediateOnly++;
                    }
                    else if (result.IntermediateSamplesUsed > 0 && result.FallbackSamplesUsed > 0)
                    {
                        builtWithIntermediateAndFallback++;
                    }
                    else if (result.PrimarySamplesUsed == 0 && result.FallbackSamplesUsed > 0)
                    {
                        builtWithFallbackOnly++;
                    }

                    if (result.NeighborFilledSamples > 0)
                    {
                        builtWithNeighborFill++;
                    }

                    Console.WriteLine($"  -> Source samples used: {PrimaryDemLabel}={result.PrimarySamplesUsed:N0}, {IntermediateDemLabel}={result.IntermediateSamplesUsed:N0}, {FallbackDemLabel}={result.FallbackSamplesUsed:N0}, neighbor-fill={result.NeighborFilledSamples:N0}");
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

        if (distantMountains)
        {
            string loOutputDir = inspectOnly
                ? Path.Combine(Environment.CurrentDirectory, "generated-lo_tiles")
                : Path.Combine(route.RouteDir, "lo_tiles");
            await GenerateDistantMountainTilesAsync(route, httpClient, loOutputDir, loTileRadius, loSampleOffsetX, loSampleOffsetY, sourceBiasEastMeters, sourceBiasNorthMeters, markerCoverage, trackDatabaseCoverage, kmlCoverage, textFileCoverage, overwriteFlag, cancellationToken);
        }

        Console.WriteLine($"\nDone. Generated={generatedCount:N0}, skipped={skipped:N0}, failed={failed:N0}, total={totalTiles:N0}.");
        PrintFailedTileTextFileBlock(retryableFailedNormalTileNames);
        PrintUnmappableTileBlock(unmappableFailedNormalTileNames);
        if (!inspectOnly)
        {
            Console.WriteLine($"Resolution summary: {PrimaryDemLabel} only={builtWithPrimaryOnly:N0}, mixed {PrimaryDemLabel}+{IntermediateDemLabel}={builtWithPrimaryAndIntermediate:N0}, mixed {PrimaryDemLabel}+{FallbackDemLabel}={builtWithPrimaryAndFallback:N0}, {IntermediateDemLabel} only={builtWithIntermediateOnly:N0}, mixed {IntermediateDemLabel}+{FallbackDemLabel}={builtWithIntermediateAndFallback:N0}, {FallbackDemLabel} only={builtWithFallbackOnly:N0}, neighbor-filled={builtWithNeighborFill:N0}.");
            Console.WriteLine($"Source use summary: tiles using {PrimaryDemLabel}={tilesUsingPrimary:N0}, {IntermediateDemLabel}={tilesUsingIntermediate:N0}, {FallbackDemLabel}={tilesUsingFallback:N0}.");
        }
    }

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
            return new ScanSummary(false, 0, 0, 0, 0);
        }

        PrintRouteSummary(route!);

        int selectionSources = new[] { options.MarkerCoverage, options.TrackDatabaseCoverage, options.KmlCoverage, options.TextFileCoverage }.Count(v => v);
        if (selectionSources > 1)
        {
            Console.WriteLine("Error: choose only one selection source.");
            return new ScanSummary(false, 0, 0, 0, 0);
        }

        IReadOnlyList<TerrainTile> processingTiles = [];
        if (options.CreateRouteTiles)
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

            Console.WriteLine($"\nRoute tile scan: {processingTiles.Count:N0} selected tile(s).");
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
        else if (!options.CreateRouteTiles && !options.CreateDistantMountains)
        {
            Console.WriteLine("Error: no output option is selected.");
            blockingFailure = true;
        }

        GeoTileMapper? mapper = GeoTileMapper.TryCreate(route!);
        if (mapper is null)
        {
            Console.WriteLine("Error: could not create route geographic mapper.");
            blockingFailure = true;
        }
        else
        {
            PrintProjectionSummary(mapper);
            using HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
            if (options.CreateRouteTiles && processingTiles.FirstOrDefault(t => t.WorldTile is not null) is TerrainTile firstTile)
            {
                GeoSampleGrid grid = mapper.GetSampleGrid(firstTile.WorldTile!, 0, 0, 1, 1, 0, 0);
                bool primaryOk = await TestUsgsDatasetAsync(httpClient, grid, PrimaryDemDataset, cancellationToken);
                bool intermediateOk = await TestUsgsDatasetAsync(httpClient, grid, IntermediateDemDataset, cancellationToken);
                bool fallbackOk = await TestUsgsDatasetAsync(httpClient, grid, FallbackDemDataset, cancellationToken);
                if (!primaryOk || !intermediateOk || !fallbackOk)
                {
                    blockingFailure = true;
                }
            }

            if (options.CreateDistantMountains && dmCoverage.Count > 0)
            {
                LoTileCoordinate loTile = dmCoverage.OrderBy(t => t.X).ThenBy(t => t.Z).First();
                GeoSampleGrid grid = mapper.GetAreaSampleGrid(
                    loTile.X + ((LoTileNormalTileSpan - 1) / 2.0),
                    loTile.Z + ((LoTileNormalTileSpan - 1) / 2.0),
                    LoTileSizeMeters,
                    LoTileSizeMeters,
                    LoRawGridSize,
                    0,
                    0,
                    0,
                    0);
                bool fallbackOk = await TestUsgsDatasetAsync(httpClient, grid, FallbackDemDataset, cancellationToken);
                if (!fallbackOk)
                {
                    blockingFailure = true;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(blockingFailure ? "Scan result: FAILED" : "Scan result: PASSED");
        return new ScanSummary(!blockingFailure, options.CreateRouteTiles ? processingTiles.Count : 0, options.CreateDistantMountains ? dmCoverage.Count : 0, unreadableRouteTiles, unreadableDmTiles);
    }

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

    private static async Task<bool> TestUsgsDatasetAsync(HttpClient client, GeoSampleGrid sampleGrid, string datasetName, CancellationToken cancellationToken)
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
                return false;
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using JsonDocument doc = ParseUsgsProductJson(json);
            int itemCount = doc.RootElement.TryGetProperty("items", out JsonElement items) ? items.GetArrayLength() : 0;
            Console.WriteLine($"USGS {GetDemSourceDisplayName(datasetName)}: active, {itemCount:N0} product(s) for representative bbox.");
            return true;
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"USGS {GetDemSourceDisplayName(datasetName)}: FAILED ({ex.Message}).");
            return false;
        }
    }

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

    private static void EnsureMarkerCoverageTiles(string routeDir, int terrainRadius)
    {
        if (string.IsNullOrWhiteSpace(routeDir) || !Directory.Exists(routeDir))
        {
            throw new DirectoryNotFoundException($"route directory does not exist: {routeDir}");
        }

        string routeName = new DirectoryInfo(routeDir).Name;
        string markerPath = Path.Combine(routeDir, routeName + ".mkr");
        if (!File.Exists(markerPath))
        {
            throw new FileNotFoundException($"could not find route marker file: {markerPath}");
        }

        terrainRadius = Math.Max(0, terrainRadius);
        List<RouteMarker> markers = ReadCoverageMarkers(markerPath);
        if (markers.Count == 0)
        {
            throw new InvalidOperationException($"marker file does not contain any Marker entries: {markerPath}");
        }

        HashSet<TileCoordinate> coverage = BuildMarkerNormalTileCoverage(markers, terrainRadius);
        EnsureNormalCoverageTiles(routeDir, coverage, $"Marker coverage: {markers.Count:N0} markers, radius {Math.Max(0, terrainRadius):N0}");
    }

    private static void EnsureTrackDatabaseCoverageTiles(string routeDir, int terrainRadius)
    {
        HashSet<TileCoordinate> trackTiles = ReadTrackDatabaseTileCoordinates(routeDir);
        if (trackTiles.Count == 0)
        {
            throw new InvalidOperationException("track database does not contain any readable TileX/TileZ references.");
        }

        HashSet<TileCoordinate> coverage = ExpandNormalTileCoverage(trackTiles, terrainRadius);
        EnsureNormalCoverageTiles(routeDir, coverage, $"Track database coverage: {trackTiles.Count:N0} track tile(s), radius {Math.Max(0, terrainRadius):N0}");
    }

    private static void EnsureKmlCoverageTiles(string routeDir, int terrainRadius)
    {
        HashSet<TileCoordinate> kmlTiles = ReadKmlTileCoordinates(routeDir);
        if (kmlTiles.Count == 0)
        {
            throw new InvalidOperationException("KML file did not produce any terrain tile coordinates.");
        }

        HashSet<TileCoordinate> coverage = ExpandNormalTileCoverage(kmlTiles, terrainRadius);
        EnsureNormalCoverageTiles(routeDir, coverage, $"KML coverage: {kmlTiles.Count:N0} tile(s), radius {Math.Max(0, terrainRadius):N0}");
    }

    private static void EnsureTextFileCoverageTiles(string routeDir)
    {
        HashSet<TileCoordinate> textTiles = ReadTextFileTileCoordinates(routeDir);
        if (textTiles.Count == 0)
        {
            throw new InvalidOperationException("SCOLIDEXTiles.txt did not contain any readable terrain tile names.");
        }

        EnsureNormalCoverageTiles(routeDir, textTiles, $"Text file coverage: {textTiles.Count:N0} exact tile(s)");
    }

    private static void EnsureNormalCoverageTiles(string routeDir, HashSet<TileCoordinate> coverage, string description)
    {
        FileInfo templateTile = FindTerrainTileTemplate(routeDir)
            ?? throw new FileNotFoundException("could not find a terrain .t template in generated-tiles or the route tiles folder");

        string tilesDir = Path.Combine(routeDir, "tiles");
        string worldDir = Path.Combine(routeDir, "world");
        Directory.CreateDirectory(tilesDir);
        Directory.CreateDirectory(worldDir);

        int createdTerrainTiles = 0;
        int createdRawGrids = 0;
        int createdWorldFiles = 0;
        foreach (TileCoordinate tile in coverage.OrderBy(t => t.X).ThenBy(t => t.Z))
        {
            string tileBaseName = RouteLayout.TileNameFromTileXZ(tile.X, tile.Z);
            string tilePath = Path.Combine(tilesDir, tileBaseName + ".t");
            string rawPath = Path.Combine(tilesDir, tileBaseName + "_y.raw");
            string worldPath = Path.Combine(worldDir, WorldFileName(tile.X, tile.Z));

            EnsureExactFileNameCasing(tilePath);
            EnsureExactFileNameCasing(rawPath);
            if (!File.Exists(tilePath))
            {
                File.WriteAllBytes(tilePath, CreateTerrainTileFromTemplate(templateTile, tileBaseName));
                createdTerrainTiles++;
            }

            if (!File.Exists(rawPath))
            {
                File.WriteAllBytes(rawPath, CreateEmptyRawGridBytes());
                createdRawGrids++;
            }

            if (!File.Exists(worldPath))
            {
                File.WriteAllText(worldPath, "SIMISA@@@@@@@@@@JINX0w0t______\r\n\r\nTr_Worldfile (\r\n)\r\n", Encoding.ASCII);
                createdWorldFiles++;
            }
        }

        Console.WriteLine(
            $"{description}, {coverage.Count:N0} total normal tiles. " +
            $"Created {createdTerrainTiles:N0} .t, {createdRawGrids:N0} raw, {createdWorldFiles:N0} world files.");
    }

    private static IReadOnlyList<TerrainTile> GetRouteTileProcessingList(
        RouteLayout route,
        bool markerCoverage,
        bool trackDatabaseCoverage,
        bool kmlCoverage,
        bool textFileCoverage,
        int terrainRadius)
    {
        IEnumerable<TerrainTile> tiles = route.TerrainTiles;
        HashSet<TileCoordinate>? coverage = null;
        string? coverageName = null;
        GeoTileMapper? mapper = route.TsreProjection is null ? null : GeoTileMapper.TryCreate(route);
        if (markerCoverage)
        {
            coverage = BuildMarkerNormalTileCoverage(route.Markers, terrainRadius, mapper);
            coverageName = "Marker";
        }
        else if (trackDatabaseCoverage)
        {
            HashSet<TileCoordinate> trackTiles = ReadTrackDatabaseTileCoordinates(route.RouteDir);
            coverage = ExpandNormalTileCoverage(trackTiles, terrainRadius);
            coverageName = "Track database";
            Console.WriteLine($"Track database source: {trackTiles.Count:N0} track tile coordinate(s).");
        }
        else if (kmlCoverage)
        {
            HashSet<TileCoordinate> kmlTiles = ReadKmlTileCoordinates(route.RouteDir, mapper);
            coverage = ExpandNormalTileCoverage(kmlTiles, terrainRadius);
            coverageName = "KML";
            Console.WriteLine($"KML source: {kmlTiles.Count:N0} tile coordinate(s).");
        }
        else if (textFileCoverage)
        {
            HashSet<TileCoordinate> textTiles = ReadTextFileTileCoordinates(route.RouteDir);
            coverage = textTiles;
            coverageName = "Text file";
            Console.WriteLine($"Text file source: {textTiles.Count:N0} tile coordinate(s).");
        }

        if (coverage is not null)
        {
            tiles = tiles.Where(tile =>
                tile.WorldTile is not null &&
                coverage.Contains(new TileCoordinate(tile.WorldTile.X, tile.WorldTile.Z)));
            Console.WriteLine($"{coverageName} terrain processing filter: {coverage.Count:N0} covered normal tile coordinate(s).");
        }

        return tiles
            .OrderBy(t => t.WorldTile?.Z ?? int.MaxValue)
            .ThenBy(t => t.WorldTile?.X ?? int.MaxValue)
            .ThenBy(t => t.TileFile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HashSet<TileCoordinate> BuildMarkerNormalTileCoverage(IEnumerable<RouteMarker> markers, int terrainRadius, GeoTileMapper? mapper = null)
    {
        HashSet<TileCoordinate> centerTiles = [];
        foreach (RouteMarker marker in markers)
        {
            centerTiles.Add(mapper?.GetTileCoordinateForLonLatProjected(marker.Longitude, marker.Latitude)
                ?? GeoTileMapper.GetTileCoordinateForLonLat(marker.Longitude, marker.Latitude));
        }

        return ExpandNormalTileCoverage(centerTiles, terrainRadius);
    }

    private static HashSet<TileCoordinate> ExpandNormalTileCoverage(IEnumerable<TileCoordinate> centerTiles, int terrainRadius)
    {
        terrainRadius = Math.Max(0, terrainRadius);
        HashSet<TileCoordinate> coverage = [];
        foreach (TileCoordinate center in centerTiles)
        {
            for (int dz = -terrainRadius; dz <= terrainRadius; dz++)
            {
                for (int dx = -terrainRadius; dx <= terrainRadius; dx++)
                {
                    coverage.Add(new TileCoordinate(center.X + dx, center.Z + dz));
                }
            }
        }

        return coverage;
    }

    private static HashSet<TileCoordinate> ReadTrackDatabaseTileCoordinates(string routeDir)
    {
        string tdbPath = FindRouteDataFile(routeDir, ".tdb");
        if (string.IsNullOrWhiteSpace(tdbPath) || !File.Exists(tdbPath))
        {
            throw new FileNotFoundException($"could not find a .tdb track database file in {routeDir}");
        }

        string text = File.ReadAllText(tdbPath);
        HashSet<TileCoordinate> tiles = [];

        foreach (Match match in TrackVectorSectionRegex().Matches(text))
        {
            AddTrackTileCandidate(tiles, match.Groups["tileX"].Value, match.Groups["tileZ"].Value);
            AddTrackTileCandidate(tiles, match.Groups["worldX"].Value, match.Groups["worldZ"].Value);
        }

        foreach (Match match in TrackUidRegex().Matches(text))
        {
            AddTrackTileCandidate(tiles, match.Groups["tileX"].Value, match.Groups["tileZ"].Value);
            AddTrackTileCandidate(tiles, match.Groups["worldX"].Value, match.Groups["worldZ"].Value);
        }

        foreach (Match match in TrackItemRDataRegex().Matches(text))
        {
            AddTrackTileCandidate(tiles, match.Groups["tileX"].Value, match.Groups["tileZ"].Value);
        }

        Console.WriteLine($"Track database: read {tiles.Count:N0} tile coordinate(s) from {Path.GetFileName(tdbPath)}.");
        return tiles;
    }

    private static void AddTrackTileCandidate(HashSet<TileCoordinate> tiles, string tileXText, string tileZText)
    {
        if (!int.TryParse(tileXText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tileX) ||
            !int.TryParse(tileZText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tileZ))
        {
            return;
        }

        if (tileX == 0 && tileZ == 0)
        {
            return;
        }

        tiles.Add(new TileCoordinate(tileX, tileZ));
    }

    private static string FindRouteDataFile(string routeDir, string extension)
    {
        string routeName = new DirectoryInfo(routeDir).Name;
        string preferredPath = Path.Combine(routeDir, routeName + extension);
        if (File.Exists(preferredPath))
        {
            return preferredPath;
        }

        return Directory.GetFiles(routeDir, "*" + extension).FirstOrDefault() ?? "";
    }

    private static HashSet<TileCoordinate> ReadTextFileTileCoordinates(string routeDir)
    {
        string tileListPath = Path.Combine(routeDir, "SCOLIDEXTiles.txt");
        if (!File.Exists(tileListPath))
        {
            string legacyTileListPath = Path.Combine(routeDir, "SCOtopoTiles.txt");
            if (!File.Exists(legacyTileListPath))
            {
                throw new FileNotFoundException($"could not find SCOLIDEXTiles.txt in {routeDir}");
            }

            tileListPath = legacyTileListPath;
        }

        HashSet<TileCoordinate> tiles = [];
        int lineNumber = 0;
        foreach (string rawLine in File.ReadLines(tileListPath))
        {
            lineNumber++;
            string line = rawLine.Split('#')[0].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParseTextTileLine(line, out TileCoordinate tile))
            {
                tiles.Add(tile);
                continue;
            }

            if (line.StartsWith('_'))
            {
                Console.WriteLine($"Text file: ignored unsupported underscore tile line {lineNumber}: {rawLine}");
            }
            else
            {
                Console.WriteLine($"Text file: ignored unreadable tile line {lineNumber}: {rawLine}");
            }
        }

        Console.WriteLine($"Text file: read {tiles.Count:N0} tile coordinate(s) from {Path.GetFileName(tileListPath)}.");
        return tiles;
    }

    private static bool TryParseTextTileLine(string line, out TileCoordinate tile)
    {
        tile = default;
        string value = line.Trim().Trim('"');
        if (value.EndsWith(".t", StringComparison.OrdinalIgnoreCase))
        {
            value = Path.GetFileNameWithoutExtension(value);
        }

        if (RouteLayout.TryDecodeTileName(value, out tile))
        {
            return true;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
        {
            tile = new TileCoordinate(x, z);
            return true;
        }

        return false;
    }

    private static HashSet<TileCoordinate> ReadKmlTileCoordinates(string routeDir, GeoTileMapper? mapper = null)
    {
        string routeName = new DirectoryInfo(routeDir).Name;
        string kmlPath = Path.Combine(routeDir, routeName + ".kml");

        if (!File.Exists(kmlPath))
        {
            throw new FileNotFoundException($"could not find route KML file: {kmlPath}");
        }

        XDocument doc = XDocument.Load(kmlPath);
        HashSet<TileCoordinate> tiles = [];
        foreach (XElement coordinates in doc.Descendants().Where(e => e.Name.LocalName.Equals("coordinates", StringComparison.OrdinalIgnoreCase)))
        {
            List<(double Lon, double Lat)> points = ParseKmlCoordinateText(coordinates.Value).ToList();
            if (points.Count == 0)
            {
                continue;
            }

            AddKmlPointTile(tiles, points[0], mapper);
            for (int i = 1; i < points.Count; i++)
            {
                AddKmlLineTiles(tiles, points[i - 1], points[i], mapper);
            }
        }

        Console.WriteLine($"KML: read {tiles.Count:N0} tile coordinate(s) from {Path.GetFileName(kmlPath)}.");
        return tiles;
    }

    private static IEnumerable<(double Lon, double Lat)> ParseKmlCoordinateText(string text)
    {
        foreach (string tuple in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = tuple.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
            {
                yield return (lon, lat);
            }
        }
    }

    private static void AddKmlPointTile(HashSet<TileCoordinate> tiles, (double Lon, double Lat) point, GeoTileMapper? mapper = null)
    {
        tiles.Add(mapper?.GetTileCoordinateForLonLatProjected(point.Lon, point.Lat)
            ?? GeoTileMapper.GetTileCoordinateForLonLat(point.Lon, point.Lat));
    }

    private static void AddKmlLineTiles(HashSet<TileCoordinate> tiles, (double Lon, double Lat) start, (double Lon, double Lat) end, GeoTileMapper? mapper = null)
    {
        double maxDelta = Math.Max(Math.Abs(end.Lon - start.Lon), Math.Abs(end.Lat - start.Lat));
        int steps = Math.Max(1, (int)Math.Ceiling(maxDelta / 0.002));
        for (int step = 0; step <= steps; step++)
        {
            double t = step / (double)steps;
            AddKmlPointTile(tiles, (
                start.Lon + ((end.Lon - start.Lon) * t),
                start.Lat + ((end.Lat - start.Lat) * t)),
                mapper);
        }
    }

    private static List<RouteMarker> ReadCoverageMarkers(string markerPath)
    {
        List<RouteMarker> markers = [];
        foreach (string line in File.ReadLines(markerPath))
        {
            Match match = CoverageMarkerRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            markers.Add(new RouteMarker(
                double.Parse(match.Groups["lon"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["lat"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value.Trim()));
        }

        return markers;
    }

    private static FileInfo? FindTerrainTileTemplate(string routeDir)
    {
        FileInfo? generatedTemplate = FindGeneratedTerrainTileTemplate();
        if (generatedTemplate is not null)
        {
            return generatedTemplate;
        }

        DirectoryInfo routeTiles = new(Path.Combine(routeDir, "tiles"));
        return routeTiles.Exists
            ? routeTiles.EnumerateFiles("*.t").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;
    }

    private static byte[] CreateTerrainTileFromTemplate(FileInfo templateTile, string tileBaseName)
    {
        string templateBaseName = Path.GetFileNameWithoutExtension(templateTile.Name);
        if (templateBaseName.Length != tileBaseName.Length)
        {
            throw new InvalidOperationException($"template tile name {templateBaseName} cannot be rewritten as {tileBaseName}");
        }

        byte[] bytes = File.ReadAllBytes(templateTile.FullName);
        ReplaceBytesInPlace(bytes, Encoding.ASCII.GetBytes(templateBaseName), Encoding.ASCII.GetBytes(tileBaseName));
        ReplaceBytesInPlace(bytes, Encoding.Unicode.GetBytes(templateBaseName), Encoding.Unicode.GetBytes(tileBaseName));
        return bytes;
    }

    private static void ReplaceBytesInPlace(byte[] haystack, byte[] needle, byte[] replacement)
    {
        if (needle.Length == 0 || needle.Length != replacement.Length)
        {
            return;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            Buffer.BlockCopy(replacement, 0, haystack, i, replacement.Length);
            i += needle.Length - 1;
        }
    }

    private static byte[] CreateEmptyRawGridBytes()
    {
        byte[] bytes = new byte[OrtsRawGridSize * OrtsRawGridSize * sizeof(short)];
        for (int i = 0; i < bytes.Length; i += sizeof(short))
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i, sizeof(short)), RawMissingHeight);
        }

        return bytes;
    }

    private static void MarkTerrainTileForAppendRetry(TerrainTile tile)
    {
        string rawPath = tile.RawHeightPath
            ?? Path.Combine(
                tile.TileFile.DirectoryName ?? "",
                Path.GetFileNameWithoutExtension(tile.TileFile.Name).ToLowerInvariant() + "_y.raw");

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(rawPath) ?? ".");
            EnsureExactFileNameCasing(rawPath);
            File.WriteAllBytes(rawPath, CreateEmptyRawGridBytes());
            Console.WriteLine("  -> Marked tile raw grid as missing so a later Append will retry it.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  -> Could not mark tile for Append retry: {ex.Message}");
        }
    }

    private static string WorldFileName(int tileX, int tileZ)
    {
        return string.Create(CultureInfo.InvariantCulture, $"w{tileX:+000000;-000000}{tileZ:+000000;-000000}.w");
    }

    private static async Task GenerateDistantMountainTilesAsync(
        RouteLayout route,
        HttpClient httpClient,
        string outputDir,
        int loTileRadius,
        double sampleOffsetX,
        double sampleOffsetY,
        double sourceBiasEastMeters,
        double sourceBiasNorthMeters,
        bool useMarkerCoverage,
        bool useTrackDatabaseCoverage,
        bool useKmlCoverage,
        bool useTextFileCoverage,
        bool overwriteFlag,
        CancellationToken cancellationToken)
    {
        if (useMarkerCoverage && route.Markers.Count == 0)
        {
            Console.WriteLine("\nDistant Mountains: skipped because the route has no markers.");
            return;
        }

        loTileRadius = Math.Max(0, loTileRadius);
        HashSet<LoTileCoordinate> coverage = BuildDistantMountainCoverage(
            route,
            loTileRadius,
            useMarkerCoverage,
            useTrackDatabaseCoverage,
            useKmlCoverage,
            useTextFileCoverage);
        GeoTileMapper? mapper = GeoTileMapper.TryCreate(route);
        if (mapper is null)
        {
            Console.WriteLine("\nDistant Mountains: skipped because route geographic mapper could not be created.");
            return;
        }

        if (coverage.Count == 0)
        {
            Console.WriteLine("\nDistant Mountains: skipped because no route/marker coverage tiles were found.");
            return;
        }

        Directory.CreateDirectory(outputDir);
        if (overwriteFlag)
        {
            DeleteExistingDistantMountainFiles(outputDir);
        }
        else if (HasDemexStyleDistantMountainFiles(outputDir))
        {
            Console.WriteLine("Distant Mountains: existing DEMEX-style lo_tiles detected; purging before TSRE-style rebuild.");
            DeleteExistingDistantMountainFiles(outputDir);
        }

        FileInfo? fallbackTemplateTile = FindLoTileTemplate(outputDir, null);
        if (fallbackTemplateTile is null)
        {
            Console.WriteLine("\nDistant Mountains: skipped because no TSRE-style lo_tile .t template could be found.");
            return;
        }

        int built = 0;
        int skipped = 0;
        int failed = 0;
        int total = coverage.Count;
        List<GeneratedLoTile> generatedLoTiles = [];
        string coverageDescription = useMarkerCoverage
            ? $"{route.Markers.Count:N0} markers"
            : useTrackDatabaseCoverage
                ? "track database"
                : useKmlCoverage
                    ? "KML"
                    : useTextFileCoverage
                        ? "text file"
            : $"{route.TerrainTiles.Count:N0} route tiles";
        Console.WriteLine($"\nDistant Mountains: {coverageDescription}, radius {loTileRadius:N0}, {total:N0} lo_tiles.");
        if (sampleOffsetX != 0 || sampleOffsetY != 0)
        {
            Console.WriteLine($"Distant Mountains: applying sample offset X={sampleOffsetX:F3}, Y={sampleOffsetY:F3} grid sample(s).");
        }

        int index = 0;
        foreach (LoTileCoordinate loTile in coverage.OrderBy(t => t.X).ThenBy(t => t.Z))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine("Distant Mountains: abort requested; stopping before next lo_tile.");
                break;
            }

            index++;
            string loName = LoTileNameFromTileXZ(loTile.X, loTile.Z);
            string tilePath = Path.Combine(outputDir, loName + ".t");
            string heightPath = Path.Combine(outputDir, loName + "_y.raw");
            Console.WriteLine($"\n[DM {index:N0}/{total:N0}] {loName}.t");

            EnsureExactFileNameCasing(tilePath);
            EnsureExactFileNameCasing(heightPath);

            RawGridStats? existingLoStats = TryGetRawGridStats(heightPath);
            if (!overwriteFlag && File.Exists(tilePath) && existingLoStats is not null && !existingLoStats.IsEmpty)
            {
                Console.WriteLine($"  -> Skipped: lo_tile raw grid already has {existingLoStats.ValidCount:N0} height samples.");
                skipped++;
                continue;
            }

            if (!overwriteFlag && File.Exists(tilePath) && existingLoStats is not null && existingLoStats.IsEmpty)
            {
                Console.WriteLine("  -> Existing lo_tile raw grid is empty; retrying DEM generation.");
            }

            try
            {
                GeoSampleGrid sampleGrid = mapper.GetAreaSampleGrid(
                    loTile.X + ((LoTileNormalTileSpan - 1) / 2.0),
                    loTile.Z + ((LoTileNormalTileSpan - 1) / 2.0),
                    LoTileSizeMeters,
                    LoTileSizeMeters,
                    LoRawGridSize,
                    sampleOffsetX,
                    sampleOffsetY,
                    sourceBiasEastMeters,
                    sourceBiasNorthMeters);
                Console.WriteLine(
                    $"  -> Estimated bbox lon {sampleGrid.BoundingBox.MinLon:F6}..{sampleGrid.BoundingBox.MaxLon:F6}, " +
                    $"lat {sampleGrid.BoundingBox.MinLat:F6}..{sampleGrid.BoundingBox.MaxLat:F6}");

                List<string> failures = [];
                DemWindowSearchResult result = await ReadDemWindowsForDatasetAsync(httpClient, sampleGrid, FallbackDemDataset, failures);
                short[,] heights = CreateMissingHeightGrid(LoRawGridSize, LoRawGridSize);
                int samplesUsed = MergeWindows(result.Windows, heights);
                if (samplesUsed == 0 || heights.Cast<short>().All(h => h == RawMissingHeight))
                {
                    throw new InvalidOperationException("no 10m DEM samples were read for this lo_tile. " + string.Join(" | ", failures.Take(4)));
                }

                int missingBeforeFill = heights.Cast<short>().Count(h => h == RawMissingHeight);
                if (missingBeforeFill > 0)
                {
                    Console.WriteLine($"  -> 10m mosaic still missing {missingBeforeFill:N0} samples; filling from neighbors.");
                    FillMissingHeights(heights);
                }

                FileInfo templateTile = FindLoTileTemplate(outputDir, loName) ?? fallbackTemplateTile;
                generatedLoTiles.Add(new GeneratedLoTile(loTile, loName, templateTile, tilePath, heightPath, heights, samplesUsed));
                built++;
                Console.WriteLine($"  -> Prepared TSRE-style lo_tile with 10m={samplesUsed:N0}, neighbor-fill={missingBeforeFill:N0} samples.");
            }
            catch (Exception ex)
            {
                failed++;
                MarkDistantMountainTileForAppendRetry(tilePath, heightPath);
                Console.WriteLine($"  -> Distant Mountain generation failed: {ex.Message}");
            }
        }

        if (generatedLoTiles.Count > 0)
        {
            Dictionary<(int X, int Z), short[,]> mergeGrid = generatedLoTiles.ToDictionary(
                tile => (tile.Tile.X / LoTileNormalTileSpan, tile.Tile.Z / LoTileNormalTileSpan),
                tile => tile.Heights);
            MergeSharedEdges(mergeGrid);

            foreach (GeneratedLoTile generated in generatedLoTiles.OrderBy(t => t.Tile.X).ThenBy(t => t.Tile.Z))
            {
                TerrainSampleEncoding loEncoding = CalculateSampleEncoding(generated.Heights);
                byte[] tileBytes = CreateTerrainTileFromTemplate(generated.TemplateTile, generated.Name);
                PatchTerrainSampleMetadata(tileBytes, loEncoding);
                File.WriteAllBytes(generated.TilePath, tileBytes);
                WriteEncodedHeightGrid(generated.HeightPath, generated.Heights, loEncoding);
                Console.WriteLine($"  -> Wrote {generated.Name}.t with merged edges using floor={loEncoding.Floor:F3}, scale={loEncoding.Scale:G9}.");
            }
        }

        int indexedLoTiles = WriteTsreLowTerrainIndex(route.RouteDir, outputDir);
        Console.WriteLine($"Distant Mountains: rebuilt TSRE low-terrain index with {indexedLoTiles:N0} lo_tiles.");
        Console.WriteLine($"\nDistant Mountains done. Generated={built:N0}, skipped={skipped:N0}, failed={failed:N0}, total={total:N0}.");
    }

    private static int WriteTsreLowTerrainIndex(string routeDir, string loTilesDir)
    {
        List<LoTileCoordinate> loTiles = EnumerateTsreLoTiles(loTilesDir).ToList();
        if (loTiles.Count == 0)
        {
            return 0;
        }

        string tdDir = Path.Combine(routeDir, "td");
        Directory.CreateDirectory(tdDir);

        TsreLowQuadTree quadTree = new();
        foreach (LoTileCoordinate loTile in loTiles)
        {
            quadTree.AddTile(loTile.X, loTile.Z);
        }

        quadTree.Save(tdDir);
        return loTiles.Count;
    }

    private static IEnumerable<LoTileCoordinate> EnumerateTsreLoTiles(string loTilesDir)
    {
        if (!Directory.Exists(loTilesDir))
        {
            yield break;
        }

        foreach (FileInfo tileFile in new DirectoryInfo(loTilesDir).EnumerateFiles("-*.t"))
        {
            string baseName = Path.GetFileNameWithoutExtension(tileFile.Name).ToLowerInvariant();
            string rawPath = Path.Combine(tileFile.DirectoryName ?? loTilesDir, baseName + "_y.raw");
            RawGridStats? rawStats = TryGetRawGridStats(rawPath);
            if (rawStats is null || rawStats.IsEmpty || !TryDecodeTileName(baseName, 11, out TileCoordinate tile))
            {
                continue;
            }

            yield return new LoTileCoordinate(
                FloorToMultiple(tile.X, LoTileNormalTileSpan),
                FloorToMultiple(tile.Z, LoTileNormalTileSpan));
        }
    }

    private static RawGridStats? TryGetRawGridStats(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || !RawGrid.TryRead(rawPath, out RawGrid? grid, out _) || grid is null)
        {
            return null;
        }

        return grid.GetStats();
    }

    private static void MarkDistantMountainTileForAppendRetry(string tilePath, string heightPath)
    {
        TryDeleteFile(tilePath);
        TryDeleteFile(heightPath);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only; append will still retry empty or unreadable raw grids.
        }
    }

    private static HashSet<LoTileCoordinate> BuildDistantMountainCoverage(
        RouteLayout route,
        int loTileRadius,
        bool useMarkerCoverage,
        bool useTrackDatabaseCoverage,
        bool useKmlCoverage,
        bool useTextFileCoverage)
    {
        loTileRadius = Math.Max(0, loTileRadius);
        HashSet<LoTileCoordinate> coverage = [];
        GeoTileMapper? mapper = route.TsreProjection is null ? null : GeoTileMapper.TryCreate(route);
        if (useMarkerCoverage)
        {
            foreach (RouteMarker marker in route.Markers)
            {
                TileCoordinate normalTile = mapper?.GetTileCoordinateForLonLatProjected(marker.Longitude, marker.Latitude)
                    ?? GeoTileMapper.GetTileCoordinateForLonLat(marker.Longitude, marker.Latitude);
                AddLoTileCoverage(coverage, normalTile.X, normalTile.Z, loTileRadius);
            }
        }
        else if (useTrackDatabaseCoverage)
        {
            HashSet<TileCoordinate> trackTiles = ReadTrackDatabaseTileCoordinates(route.RouteDir);
            foreach (TileCoordinate normalTile in trackTiles)
            {
                AddLoTileCoverage(coverage, normalTile.X, normalTile.Z, loTileRadius);
            }
        }
        else if (useKmlCoverage)
        {
            HashSet<TileCoordinate> kmlTiles = ReadKmlTileCoordinates(route.RouteDir, mapper);
            foreach (TileCoordinate normalTile in kmlTiles)
            {
                AddLoTileCoverage(coverage, normalTile.X, normalTile.Z, loTileRadius);
            }
        }
        else if (useTextFileCoverage)
        {
            HashSet<TileCoordinate> textTiles = ReadTextFileTileCoordinates(route.RouteDir);
            foreach (TileCoordinate normalTile in textTiles)
            {
                AddLoTileCoverage(coverage, normalTile.X, normalTile.Z, loTileRadius);
            }
        }
        else
        {
            List<WorldTile> sourceTiles = route.TerrainTiles
                .Select(t => t.WorldTile)
                .Where(t => t is not null)
                .Select(t => t!)
                .ToList();
            if (sourceTiles.Count == 0)
            {
                sourceTiles = route.WorldTiles.ToList();
            }

            foreach (WorldTile worldTile in sourceTiles)
            {
                AddLoTileCoverage(coverage, worldTile.X, worldTile.Z, loTileRadius);
            }
        }

        return coverage;
    }

    private static void AddLoTileCoverage(HashSet<LoTileCoordinate> coverage, int normalTileX, int normalTileZ, int loTileRadius)
    {
        int anchorX = FloorToMultiple(normalTileX, LoTileNormalTileSpan);
        int anchorZ = FloorToMultiple(normalTileZ, LoTileNormalTileSpan);
        for (int dz = -loTileRadius; dz <= loTileRadius; dz++)
        {
            for (int dx = -loTileRadius; dx <= loTileRadius; dx++)
            {
                coverage.Add(new LoTileCoordinate(
                    anchorX + (dx * LoTileNormalTileSpan),
                    anchorZ + (dz * LoTileNormalTileSpan)));
            }
        }
    }

    private static void EnsureExactFileNameCasing(string desiredPath)
    {
        string? directoryPath = Path.GetDirectoryName(desiredPath);
        string desiredName = Path.GetFileName(desiredPath);
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return;
        }

        FileInfo? existing = new DirectoryInfo(directoryPath)
            .EnumerateFiles()
            .FirstOrDefault(file => string.Equals(file.Name, desiredName, StringComparison.OrdinalIgnoreCase));
        if (existing is null || string.Equals(existing.Name, desiredName, StringComparison.Ordinal))
        {
            return;
        }

        string temporaryPath = Path.Combine(directoryPath, desiredName + ".casefix-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        existing.MoveTo(temporaryPath);
        File.Move(temporaryPath, desiredPath);
    }

    private static FileInfo? FindLoTileTemplate(string outputDir, string? tileBaseName)
    {
        DirectoryInfo routeLoTiles = new(outputDir);
        DirectoryInfo[] generatedLoTileDirectories =
        [
            new(Path.Combine(AppContext.BaseDirectory, "generated-lo_tiles")),
            new(Path.Combine(Environment.CurrentDirectory, "generated-lo_tiles")),
        ];

        if (!string.IsNullOrWhiteSpace(tileBaseName))
        {
            FileInfo? exactRouteTemplate = FindExactLoTileTemplate(routeLoTiles, tileBaseName);
            if (exactRouteTemplate is not null)
            {
                return exactRouteTemplate;
            }

            foreach (DirectoryInfo generatedLoTiles in generatedLoTileDirectories)
            {
                FileInfo? exactGeneratedTemplate = FindExactLoTileTemplate(generatedLoTiles, tileBaseName);
                if (exactGeneratedTemplate is not null)
                {
                    return exactGeneratedTemplate;
                }
            }
        }

        FileInfo? routeTemplate = routeLoTiles.Exists
            ? routeLoTiles.EnumerateFiles("-*.t").Where(IsTsreLoTile).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;
        if (routeTemplate is not null)
        {
            return routeTemplate;
        }

        foreach (DirectoryInfo generatedLoTiles in generatedLoTileDirectories)
        {
            FileInfo? generatedTemplate = generatedLoTiles.Exists
                ? generatedLoTiles.EnumerateFiles("-*.t").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
            if (generatedTemplate is not null)
            {
                return generatedTemplate;
            }
        }

        return null;
    }

    private static FileInfo? FindExactLoTileTemplate(DirectoryInfo directory, string tileBaseName)
    {
        if (!directory.Exists)
        {
            return null;
        }

        FileInfo? template = directory.EnumerateFiles(tileBaseName + ".t").FirstOrDefault();
        if (template is null)
        {
            return null;
        }

        bool isBundledTemplate = directory.FullName.Contains("generated-lo_tiles", StringComparison.OrdinalIgnoreCase);
        return isBundledTemplate || IsTsreLoTile(template) ? template : null;
    }

    private static int FloorToMultiple(int value, int multiple)
    {
        int remainder = value % multiple;
        return remainder < 0 ? value - remainder - multiple : value - remainder;
    }

    private static string LoTileNameFromTileXZ(int tileX, int tileZ)
    {
        return TileNameFromTileXZ(tileX, tileZ, zoom: 11, prefix: '-').ToLowerInvariant();
    }

    private static string TileNameFromTileXZ(int tileX, int tileZ, int zoom, char prefix)
    {
        const string hex = "0123456789ABCDEF";
        int rectX = -16384;
        int rectZ = -16384;
        int rectW = 16384;
        int rectH = 16384;
        StringBuilder name = new(prefix.ToString());
        int partial = 0;

        for (int z = 0; z < zoom; z++)
        {
            bool east = tileX >= rectX + rectW;
            bool north = tileZ >= rectZ + rectH;
            partial <<= 2;
            partial += (north ? 0 : 2) + (east ^ north ? 0 : 1);
            if (z % 2 == 1)
            {
                name.Append(hex[partial]);
                partial = 0;
            }

            if (east)
            {
                rectX += rectW;
            }

            if (north)
            {
                rectZ += rectH;
            }

            rectW /= 2;
            rectH /= 2;
        }

        if (zoom % 2 != 0)
        {
            name.Append(hex[partial << 2]);
        }

        return name.ToString().ToLowerInvariant();
    }

    private static bool TryDecodeTileName(string tileName, int zoom, out TileCoordinate tile)
    {
        const string hex = "0123456789ABCDEF";
        string key = Path.GetFileNameWithoutExtension(tileName).TrimStart('-', '_').ToUpperInvariant();
        tile = default;
        if (key.Length != (zoom + 1) / 2 || key.Any(c => !Uri.IsHexDigit(c)))
        {
            return false;
        }

        int rectX = -16384;
        int rectZ = -16384;
        int rectW = 16384;
        int rectH = 16384;
        for (int level = 0; level < zoom; level++)
        {
            int hexValue = hex.IndexOf(key[level / 2], StringComparison.Ordinal);
            int quadrant = level % 2 == 0 ? hexValue >> 2 : hexValue & 0b11;
            if (!TryDecodeTileQuadrant(quadrant, out bool east, out bool north))
            {
                return false;
            }

            if (east)
            {
                rectX += rectW;
            }

            if (north)
            {
                rectZ += rectH;
            }

            rectW /= 2;
            rectH /= 2;
        }

        tile = new TileCoordinate(rectX, rectZ);
        return true;
    }

    private static bool TryDecodeTileQuadrant(int quadrant, out bool east, out bool north)
    {
        for (int eastValue = 0; eastValue <= 1; eastValue++)
        {
            for (int northValue = 0; northValue <= 1; northValue++)
            {
                bool candidateEast = eastValue == 1;
                bool candidateNorth = northValue == 1;
                int candidate = (candidateNorth ? 0 : 2) + (candidateEast ^ candidateNorth ? 0 : 1);
                if (candidate == quadrant)
                {
                    east = candidateEast;
                    north = candidateNorth;
                    return true;
                }
            }
        }

        east = false;
        north = false;
        return false;
    }

    private static void WriteEncodedHeightGrid(string path, short[,] heights, TerrainSampleEncoding encoding)
    {
        int height = heights.GetLength(0);
        int width = heights.GetLength(1);
        byte[] bytes = new byte[width * height * sizeof(ushort)];
        int offset = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ushort raw = heights[y, x] == RawMissingHeight
                    ? (ushort)0
                    : (ushort)Math.Clamp((int)Math.Round((heights[y, x] - encoding.Floor) / encoding.Scale, MidpointRounding.AwayFromZero), 0, ushort.MaxValue - 1);
                byte[] pair = BitConverter.GetBytes(raw);
                bytes[offset++] = pair[0];
                bytes[offset++] = pair[1];
            }
        }

        File.WriteAllBytes(path, bytes);
    }

    private static void DeleteExistingDistantMountainFiles(string outputDir)
    {
        DirectoryInfo directory = new(outputDir);
        if (!directory.Exists)
        {
            return;
        }

        string[] patterns =
        [
            "-*.t",
            "-*_y.raw",
            "-*_e.raw",
            "-*_n.raw",
            "_*.t",
            "_*_y.raw",
            "_*_e.raw",
            "_*_n.raw",
        ];

        foreach (string pattern in patterns)
        {
            foreach (FileInfo file in directory.EnumerateFiles(pattern))
            {
                file.Delete();
            }
        }
    }

    private static bool HasDemexStyleDistantMountainFiles(string outputDir)
    {
        DirectoryInfo directory = new(outputDir);
        if (!directory.Exists)
        {
            return false;
        }

        FileInfo[] tileFiles = directory.EnumerateFiles("*.t").ToArray();
        if (tileFiles.Length == 0)
        {
            return false;
        }

        foreach (FileInfo tileFile in tileFiles)
        {
            if (!IsTsreLoTile(tileFile))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTsreLoTile(FileInfo tileFile)
    {
        string baseName = Path.GetFileNameWithoutExtension(tileFile.Name).ToLowerInvariant();
        if (!baseName.StartsWith("-", StringComparison.Ordinal) || !TryDecodeTileName(baseName, 11, out _))
        {
            return false;
        }

        string rawPath = Path.Combine(tileFile.DirectoryName ?? "", baseName + "_y.raw");
        return TryGetRawGridStats(rawPath) is not null;
    }

    private static bool TryReadTerrainSampleEncoding(string tilePath, out TerrainSampleEncoding encoding)
    {
        encoding = new TerrainSampleEncoding(0, TerrainSampleScale);
        byte[] bytes = File.ReadAllBytes(tilePath);
        bool hasFloor = TryReadBinaryTokenFloat(bytes, TokenTerrainSampleFloor, out float floor);
        bool hasScale = TryReadBinaryTokenFloat(bytes, TokenTerrainSampleScale, out float scale);
        if (!hasFloor && bytes.Length >= TerrainSampleFloorOffset + sizeof(float))
        {
            floor = BitConverter.ToSingle(bytes, TerrainSampleFloorOffset);
            hasFloor = true;
        }

        if (!hasScale || scale <= 0)
        {
            scale = TerrainSampleScale;
        }

        encoding = new TerrainSampleEncoding(hasFloor ? floor : 0, scale);
        return hasFloor || hasScale;
    }

    private static bool TryReadBinaryTokenFloat(byte[] bytes, ushort targetToken, out float value)
    {
        value = 0;
        if (bytes.Length < 32 || Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 8)) != "SIMISA@@")
        {
            return false;
        }

        return TryReadBinaryTokenFloat(bytes, targetToken, 32, bytes.Length, 0, out value);
    }

    private static bool TryReadBinaryTokenFloat(byte[] bytes, ushort targetToken, int start, int end, int depth, out float value)
    {
        value = 0;
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

            if (token == targetToken && payload + sizeof(float) <= blockEnd)
            {
                value = BitConverter.ToSingle(bytes, payload);
                return true;
            }

            if (depth < 8 && IsTerrainContainerToken(token) &&
                TryReadBinaryTokenFloat(bytes, targetToken, payload, blockEnd, depth + 1, out value))
            {
                return true;
            }

            position = blockEnd;
        }

        return false;
    }


    private static void WriteLoErrorGrid(string path, short[,] heights)
    {
        byte[] bytes = new byte[LoRawGridSize * LoRawGridSize * sizeof(float)];
        int offset = 0;
        for (int y = 0; y < LoRawGridSize; y++)
        {
            for (int x = 0; x < LoRawGridSize; x++)
            {
                float error = CalculateLocalHeightError(heights, x, y);
                byte[] encoded = BitConverter.GetBytes(error);
                bytes[offset++] = encoded[0];
                bytes[offset++] = encoded[1];
                bytes[offset++] = encoded[2];
                bytes[offset++] = encoded[3];
            }
        }

        File.WriteAllBytes(path, bytes);
    }

    private static void WriteLoNormalGrid(string path, short[,] heights)
    {
        byte[] bytes = new byte[LoRawGridSize * LoRawGridSize];
        int offset = 0;
        for (int y = 0; y < LoRawGridSize; y++)
        {
            for (int x = 0; x < LoRawGridSize; x++)
            {
                int left = heights[y, Math.Max(0, x - 1)] == RawMissingHeight ? heights[y, x] : heights[y, Math.Max(0, x - 1)];
                int right = heights[y, Math.Min(LoRawGridSize - 1, x + 1)] == RawMissingHeight ? heights[y, x] : heights[y, Math.Min(LoRawGridSize - 1, x + 1)];
                int down = heights[Math.Min(LoRawGridSize - 1, y + 1), x] == RawMissingHeight ? heights[y, x] : heights[Math.Min(LoRawGridSize - 1, y + 1), x];
                int up = heights[Math.Max(0, y - 1), x] == RawMissingHeight ? heights[y, x] : heights[Math.Max(0, y - 1), x];
                int slope = Math.Abs(right - left) + Math.Abs(up - down);
                bytes[offset++] = (byte)Math.Clamp(192 - slope, 32, 240);
            }
        }

        File.WriteAllBytes(path, bytes);
    }

    private static float CalculateLocalHeightError(short[,] heights, int x, int y)
    {
        short center = heights[y, x];
        if (center == RawMissingHeight)
        {
            return 0;
        }

        int min = center;
        int max = center;
        for (int dy = -1; dy <= 1; dy++)
        {
            int yy = Math.Clamp(y + dy, 0, LoRawGridSize - 1);
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = Math.Clamp(x + dx, 0, LoRawGridSize - 1);
                short value = heights[yy, xx];
                if (value == RawMissingHeight)
                {
                    continue;
                }

                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
        }

        return Math.Max(1.0f, (max - min) * 256.0f);
    }

    private static void PrintRouteSummary(RouteLayout route)
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
        Console.WriteLine($"Detected raw grid size is {OrtsRawGridSize}x{OrtsRawGridSize} int16 samples, not inline text heights.");
    }

    private static void PrintProjectionSummary(GeoTileMapper mapper)
    {
        Console.WriteLine($"Estimated route DEM bbox: lon {mapper.MinLon:F6}..{mapper.MaxLon:F6}, lat {mapper.MinLat:F6}..{mapper.MaxLat:F6}");
        Console.WriteLine($"Projection mode: {mapper.ProjectionName}");
        Console.WriteLine(mapper.ProjectionDetail);
    }

    private static async Task<TerrainGenerationResult> StreamOrtsGridForSampleGridAsync(
        HttpClient client,
        GeoSampleGrid sampleGrid)
    {
        List<string> failures = [];
        short[,] mergedHeights = CreateMissingHeightGrid();
        DemWindowSearchResult primarySearch = await ReadDemWindowsForDatasetAsync(client, sampleGrid, PrimaryDemDataset, failures);
        int primarySamplesUsed = MergeWindows(primarySearch.Windows, mergedHeights);

        int missingAfterPrimary = mergedHeights.Cast<short>().Count(v => v == RawMissingHeight);
        int intermediateSamplesUsed = 0;
        int fallbackSamplesUsed = 0;
        if (missingAfterPrimary > 0)
        {
            if (primarySearch.ProductSearchFailed)
            {
                throw new InvalidOperationException(
                    $"1m USGS product search failed, so this tile was left for a later append retry instead of falling back to lower-resolution data. " +
                    string.Join(" | ", failures.Take(4)));
            }

            Console.WriteLine($"  -> {PrimaryDemLabel} coverage left {missingAfterPrimary:N0} missing samples; trying {IntermediateDemLabel} fallback ({IntermediateDemDataset}).");
            DemWindowSearchResult intermediateSearch = await ReadDemWindowsForDatasetAsync(client, sampleGrid, IntermediateDemDataset, failures);
            intermediateSamplesUsed = MergeWindows(intermediateSearch.Windows, mergedHeights);
            if (intermediateSearch.ProductSearchFailed)
            {
                Console.WriteLine($"  -> {IntermediateDemLabel} product search failed; continuing to {FallbackDemLabel} fallback ({FallbackDemDataset}).");
            }
        }

        int missingAfterIntermediate = mergedHeights.Cast<short>().Count(v => v == RawMissingHeight);
        if (missingAfterIntermediate > 0)
        {
            Console.WriteLine($"  -> {IntermediateDemLabel} coverage left {missingAfterIntermediate:N0} missing samples; trying {FallbackDemLabel} fallback ({FallbackDemDataset}).");
            DemWindowSearchResult fallbackSearch = await ReadDemWindowsForDatasetAsync(client, sampleGrid, FallbackDemDataset, failures);
            fallbackSamplesUsed = MergeWindows(fallbackSearch.Windows, mergedHeights);
        }

        int missingBeforeFill = mergedHeights.Cast<short>().Count(v => v == RawMissingHeight);
        if (missingBeforeFill == OrtsRawGridSize * OrtsRawGridSize)
        {
            throw new InvalidOperationException("GDAL could not read a DEM window from the USGS products. " + string.Join(" | ", failures.Take(4)));
        }

        if (missingBeforeFill > 0)
        {
            Console.WriteLine($"  -> Mosaic still missing {missingBeforeFill:N0} samples after fallback; filling from neighbors.");
            FillMissingHeights(mergedHeights);
        }

        return new TerrainGenerationResult(mergedHeights, primarySamplesUsed, intermediateSamplesUsed, fallbackSamplesUsed, missingBeforeFill);
    }

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
            failures.Add($"{datasetName} product search failed for bbox lon {minLon:F6}..{maxLon:F6}, lat {minLat:F6}..{maxLat:F6}: {ex.Message}");
            List<string> cachedUrls = GetCachedProductUrls(datasetName);
            cachedUrls = FilterDemProductUrls(datasetName, cachedUrls);
            if (cachedUrls.Count == 0)
            {
                Console.WriteLine($"  -> USGS product search failed and no cached {GetDemSourceDisplayName(datasetName)} product URLs are available: {ex.Message}");
                return new DemWindowSearchResult([], ProductSearchFailed: true);
            }

            Console.WriteLine($"  -> USGS product search failed; trying {cachedUrls.Count:N0} cached {GetDemSourceDisplayName(datasetName)} product URLs from nearby tiles.");
            return new DemWindowSearchResult(
                ReadDemWindowsFromUrls(cachedUrls, datasetName, sampleGrid, failures),
                ProductSearchFailed: true);
        }

        using JsonDocument doc = ParseUsgsProductJson(jsonResponse);
        if (!doc.RootElement.TryGetProperty("items", out JsonElement items))
        {
            throw new InvalidOperationException("USGS product service response did not include an items list.");
        }

        if (items.GetArrayLength() == 0)
        {
            failures.Add($"No {GetDemSourceDisplayName(datasetName)} product found for tile bbox lon {minLon:F6}..{maxLon:F6}, lat {minLat:F6}..{maxLat:F6}.");
            return new DemWindowSearchResult([], ProductSearchFailed: false);
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
            failures.Add($"USGS found {GetDemSourceDisplayName(datasetName)} products but none had a downloadable GeoTIFF URL.");
            return new DemWindowSearchResult([], ProductSearchFailed: false);
        }

        urls = FilterDemProductUrls(datasetName, urls);
        AddCachedProductUrls(datasetName, urls);
        return new DemWindowSearchResult(
            ReadDemWindowsFromUrls(urls, datasetName, sampleGrid, failures),
            ProductSearchFailed: false);
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
                    readOk = TryReadDatasetSampleGrid(ds, sampleGrid, fillMissing: false, out heights, out missing, out failure);
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

                failures.Add(Path.GetFileName(new Uri(url).LocalPath) + ": " + failure);
            }
        }

        return windows;
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
        AddUsgsDataBytes((long)width * height * sizeof(float));

        double? noData = TryGetNoDataValue(elevationBand);
        for (int y = 0; y < gridHeight; y++)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                if (!TryBilinearSample(samples, width, height, xMin, yMin, rasterXs[y, x], rasterYs[y, x], noData, out double sample))
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

        failure = missing >= gridWidth * gridHeight ? "all sampled pixels were nodata" : "";
        return missing < gridWidth * gridHeight;
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

        float q00 = samples[(y0 * width) + x0];
        float q10 = samples[(y0 * width) + x1];
        float q01 = samples[(y1 * width) + x0];
        float q11 = samples[(y1 * width) + x1];
        if (!IsValidSample(q00, noData) || !IsValidSample(q10, noData) || !IsValidSample(q01, noData) || !IsValidSample(q11, noData))
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

    private static bool IsValidSample(float sample, double? noData)
    {
        return !float.IsNaN(sample) && (noData is null || Math.Abs(sample - noData.Value) >= 0.001);
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

    private sealed partial class RouteLayout
    {
        private RouteLayout(
            string routeDir,
            TileCoordinate? startTile,
            TsreGeoProjection? tsreProjection,
            IReadOnlyList<RouteMarker> markers,
            IReadOnlyList<WorldTile> worldTiles,
            IReadOnlyList<TerrainTile> terrainTiles)
        {
            RouteDir = routeDir;
            StartTile = startTile;
            TsreProjection = tsreProjection;
            Markers = markers;
            WorldTiles = worldTiles;
            TerrainTiles = terrainTiles;
        }

        public string RouteDir { get; }

        public TileCoordinate? StartTile { get; }

        public TsreGeoProjection? TsreProjection { get; }

        public IReadOnlyList<RouteMarker> Markers { get; }

        public IReadOnlyList<WorldTile> WorldTiles { get; }

        public IReadOnlyList<TerrainTile> TerrainTiles { get; }

        public static bool TryLoad(string routeDir, out RouteLayout? route, out string error)
        {
            route = null;

            if (string.IsNullOrWhiteSpace(routeDir) || !Directory.Exists(routeDir))
            {
                error = $"Error: route directory does not exist: {routeDir}";
                return false;
            }

            string tilesDir = Path.Combine(routeDir, "tiles");
            string worldDir = Path.Combine(routeDir, "world");

            string routeName = new DirectoryInfo(routeDir).Name;
            string trkPath = Path.Combine(routeDir, routeName + ".trk");
            if (!File.Exists(trkPath))
            {
                trkPath = Directory.GetFiles(routeDir, "*.trk").FirstOrDefault() ?? "";
            }

            if (!File.Exists(trkPath))
            {
                error = $"Error: could not find a .trk route file in {routeDir}";
                return false;
            }

            if (!Directory.Exists(tilesDir))
            {
                error = $"Error: could not find tiles folder at {tilesDir}";
                return false;
            }

            if (!Directory.Exists(worldDir))
            {
                error = $"Error: could not find world folder at {worldDir}";
                return false;
            }

            TileCoordinate? startTile;
            TsreGeoProjection? tsreProjection;
            IReadOnlyList<RouteMarker> markers;
            IReadOnlyList<WorldTile> worldTiles;
            IReadOnlyList<TerrainTile> terrainTiles;
            try
            {
                string trkText = File.ReadAllText(trkPath);
                startTile = ParseRouteStart(trkText);
                tsreProjection = ParseTsreGeoProjection(trkText);
                markers = ReadMarkers(routeDir, routeName);
                worldTiles = ReadWorldTiles(worldDir);
                terrainTiles = ReadTerrainTiles(tilesDir, worldTiles);
            }
            catch (Exception ex)
            {
                error = $"Error: failed while reading route files: {ex.Message}";
                return false;
            }

            if (terrainTiles.Count == 0)
            {
                error = $"Error: no terrain .t files found in {tilesDir}";
                return false;
            }

            if (worldTiles.Count == 0)
            {
                error = $"Error: no world .w files found in {worldDir}";
                return false;
            }

            route = new RouteLayout(routeDir, startTile, tsreProjection, markers, worldTiles, terrainTiles);
            error = "";
            return true;
        }

        private static TileCoordinate? ParseRouteStart(string trkText)
        {
            Match match = RouteStartRegex().Match(trkText);
            if (!match.Success)
            {
                return null;
            }

            return new TileCoordinate(
                int.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture));
        }

        private static TsreGeoProjection? ParseTsreGeoProjection(string trkText)
        {
            Match match = TsreGeoProjectionRegex().Match(trkText);
            if (!match.Success)
            {
                return null;
            }

            return new TsreGeoProjection(
                double.Parse(match.Groups["lat"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["lon"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture));
        }

        private static IReadOnlyList<RouteMarker> ReadMarkers(string routeDir, string routeName)
        {
            string markerPath = Path.Combine(routeDir, routeName + ".mkr");
            if (!File.Exists(markerPath))
            {
                return [];
            }

            List<RouteMarker> markers = [];
            foreach (string line in File.ReadLines(markerPath))
            {
                Match match = MarkerRegex().Match(line);
                if (!match.Success)
                {
                    continue;
                }

                markers.Add(new RouteMarker(
                    double.Parse(match.Groups["lon"].Value, CultureInfo.InvariantCulture),
                    double.Parse(match.Groups["lat"].Value, CultureInfo.InvariantCulture),
                    match.Groups["name"].Value.Trim()));
            }

            return markers;
        }

        private static IReadOnlyList<WorldTile> ReadWorldTiles(string worldDir)
        {
            List<WorldTile> tiles = [];
            foreach (FileInfo file in new DirectoryInfo(worldDir).EnumerateFiles("w*.w"))
            {
                Match match = WorldFileRegex().Match(file.Name);
                if (!match.Success)
                {
                    continue;
                }

                tiles.Add(new WorldTile(
                    int.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture),
                    file));
            }

            return tiles;
        }

        private static IReadOnlyList<TerrainTile> ReadTerrainTiles(string tilesDir, IReadOnlyList<WorldTile> worldTiles)
        {
            List<TerrainTile> tiles = [];
            foreach (FileInfo tileFile in new DirectoryInfo(tilesDir).EnumerateFiles("*.t"))
            {
                tiles.Add(new TerrainTile(tileFile, FindRawHeightPath(tileFile), FindMatchingWorldTile(tileFile, worldTiles)));
            }

            return tiles.OrderBy(t => t.TileFile.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static WorldTile? FindMatchingWorldTile(FileInfo tileFile, IReadOnlyList<WorldTile> worldTiles)
        {
            string tileName = Path.GetFileNameWithoutExtension(tileFile.Name);
            WorldTile? decodedTile = worldTiles.FirstOrDefault(
                worldTile => string.Equals(
                    TileNameFromTileXZ(worldTile.X, worldTile.Z),
                    tileName,
                    StringComparison.OrdinalIgnoreCase));
            if (decodedTile is not null)
            {
                return decodedTile;
            }

            if (TryDecodeTileName(tileFile.Name, out TileCoordinate decodedCoordinate))
            {
                FileInfo worldFile = worldTiles.FirstOrDefault(w => w.X == decodedCoordinate.X && w.Z == decodedCoordinate.Z)?.File
                    ?? new FileInfo(Path.Combine(tileFile.DirectoryName ?? "", WorldFileName(decodedCoordinate.X, decodedCoordinate.Z)));
                return new WorldTile(decodedCoordinate.X, decodedCoordinate.Z, worldFile);
            }

            return null;
        }

        public static string TileNameFromTileXZ(int tileX, int tileZ)
        {
            const int zoom = 15;
            const string hex = "0123456789ABCDEF";
            int rectX = -16384;
            int rectZ = -16384;
            int rectW = 16384;
            int rectH = 16384;
            StringBuilder name = new("-");
            int partial = 0;

            for (int z = 0; z < zoom; z++)
            {
                bool east = tileX >= rectX + rectW;
                bool north = tileZ >= rectZ + rectH;
                partial <<= 2;
                partial += (north ? 0 : 2) + (east ^ north ? 0 : 1);
                if (z % 2 == 1)
                {
                    name.Append(hex[partial]);
                    partial = 0;
                }

                if (east)
                {
                    rectX += rectW;
                }

                if (north)
                {
                    rectZ += rectH;
                }

                rectW /= 2;
                rectH /= 2;
            }

            name.Append(hex[partial << 2]);
            return name.ToString().ToLowerInvariant();
        }

        public static bool TryDecodeTileName(string tileName, out TileCoordinate tile)
        {
            const int zoom = 15;
            const string hex = "0123456789ABCDEF";
            string key = Path.GetFileNameWithoutExtension(tileName).TrimStart('-', '_').ToUpperInvariant();
            tile = default;
            if (key.Length != 8 || key.Any(c => !Uri.IsHexDigit(c)))
            {
                return false;
            }

            int rectX = -16384;
            int rectZ = -16384;
            int rectW = 16384;
            int rectH = 16384;
            for (int level = 0; level < zoom; level++)
            {
                int hexValue = hex.IndexOf(key[level / 2]);
                int quadrant = level % 2 == 0 ? hexValue >> 2 : hexValue & 0b11;
                if (!TryDecodeTileQuadrant(quadrant, out bool east, out bool north))
                {
                    return false;
                }

                if (east)
                {
                    rectX += rectW;
                }

                if (north)
                {
                    rectZ += rectH;
                }

                rectW /= 2;
                rectH /= 2;
            }

            tile = new TileCoordinate(rectX, rectZ);
            return true;
        }

        private static bool TryDecodeTileQuadrant(int quadrant, out bool east, out bool north)
        {
            for (int eastValue = 0; eastValue <= 1; eastValue++)
            {
                for (int northValue = 0; northValue <= 1; northValue++)
                {
                    bool candidateEast = eastValue == 1;
                    bool candidateNorth = northValue == 1;
                    int candidate = (candidateNorth ? 0 : 2) + (candidateEast ^ candidateNorth ? 0 : 1);
                    if (candidate == quadrant)
                    {
                        east = candidateEast;
                        north = candidateNorth;
                        return true;
                    }
                }
            }

            east = false;
            north = false;
            return false;
        }

        private static string? FindRawHeightPath(FileInfo tileFile)
        {
            string conventionalRawPath = Path.Combine(
                tileFile.DirectoryName ?? "",
                Path.GetFileNameWithoutExtension(tileFile.Name) + "_y.raw");
            if (File.Exists(conventionalRawPath))
            {
                return conventionalRawPath;
            }

            byte[] bytes = File.ReadAllBytes(tileFile.FullName);
            string unicodeText = Encoding.Unicode.GetString(bytes).Replace("\0", "");
            Match match = RawHeightRegex().Match(unicodeText);
            if (!match.Success)
            {
                string shiftedUnicodeText = Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1).Replace("\0", "");
                match = RawHeightRegex().Match(shiftedUnicodeText);
            }

            if (!match.Success)
            {
                string asciiText = Encoding.ASCII.GetString(bytes).Replace("\0", "");
                match = RawHeightRegex().Match(asciiText);
            }

            return match.Success ? Path.Combine(tileFile.DirectoryName ?? "", match.Value) : null;
        }

        [GeneratedRegex(@"RouteStart\s*\(\s*(?<x>-?\d+)\s+(?<z>-?\d+)")]
        private static partial Regex RouteStartRegex();

        [GeneratedRegex(@"TsreGeoProjection\s*\(\s*(?<lat>-?\d+(?:\.\d+)?)\s+(?<lon>-?\d+(?:\.\d+)?)\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<z>-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
        private static partial Regex TsreGeoProjectionRegex();

        [GeneratedRegex(@"Marker\s*\(\s*(?<lon>-?\d+(?:\.\d+)?)\s+(?<lat>-?\d+(?:\.\d+)?)\s+(?<name>[^)]*)\)")]
        private static partial Regex MarkerRegex();

        [GeneratedRegex(@"w(?<x>[+-]\d+)(?<z>[+-]\d+)\.w", RegexOptions.IgnoreCase)]
        private static partial Regex WorldFileRegex();

        [GeneratedRegex(@"-?[0-9a-f]{8}_y\.raw", RegexOptions.IgnoreCase)]
        private static partial Regex RawHeightRegex();
    }

    private sealed record TerrainTile(FileInfo TileFile, string? RawHeightPath, WorldTile? WorldTile);

    private sealed record WorldTile(int X, int Z, FileInfo File);

    private readonly record struct TileCoordinate(int X, int Z);

    private readonly record struct LoTileCoordinate(int X, int Z);

    private sealed record RouteMarker(double Longitude, double Latitude, string Name);

    private sealed record TsreGeoProjection(double CenterLat, double CenterLon, double CenterX, double CenterZ);

    private sealed class GeoTileMapper
    {
        private const double EarthRadiusMeters = 6_370_997.0;
        private const double UpperLeftGoodeX = -20_013_965.0;
        private const double UpperLeftGoodeY = 8_674_008.0;
        private const double WorldTileEastWestOffset = -16_385.0;
        private const double WorldTileNorthSouthOffset = 16_385.0;
        private const double Epsilon = 0.0000000001;
        private static readonly double[] LongitudeCenters =
        [
            -1.74532925199, -1.74532925199, 0.523598775598, 0.523598775598,
            -2.79252680319, -1.0471975512, -2.79252680319, -1.0471975512,
            0.349065850399, 2.44346095279, 0.349065850399, 2.44346095279,
        ];

        private readonly int minTileX;
        private readonly int minTileZ;
        private readonly TsreGeoProjection? tsreProjection;
        private readonly double tsreStepLat;
        private readonly double tsreStepLon;

        private GeoTileMapper(
            int minTileX,
            int minTileZ,
            TsreGeoProjection? tsreProjection)
        {
            this.minTileX = minTileX;
            this.minTileZ = minTileZ;
            this.tsreProjection = tsreProjection;
            if (tsreProjection is not null)
            {
                double centerLatRad = tsreProjection.CenterLat * Math.PI / 180.0;
                tsreStepLat = 111132.92 - (559.82 * Math.Cos(2 * centerLatRad)) + (1.175 * Math.Cos(4 * centerLatRad)) - (0.0023 * Math.Cos(6 * centerLatRad));
                tsreStepLon = (111412.84 * Math.Cos(centerLatRad)) - (93.5 * Math.Cos(3 * centerLatRad));
            }
        }

        public string ProjectionName => tsreProjection is null
            ? "MSTS/Open Rails interrupted Goode homolosine world-tile projection"
            : "TSRE route-centered projection";

        public string ProjectionDetail => tsreProjection is null
            ? "TsreGeoProjection not detected; using standard MSTS/Open Rails tile geography."
            : $"TsreGeoProjection detected: center lat={tsreProjection.CenterLat:F6}, lon={tsreProjection.CenterLon:F6}, X={tsreProjection.CenterX:F0}, Z={tsreProjection.CenterZ:F0}; meters/degree lat={tsreStepLat:F3}, lon={tsreStepLon:F3}.";

        public double MinLon { get; private set; }

        public double MinLat { get; private set; }

        public double MaxLon { get; private set; }

        public double MaxLat { get; private set; }

        public static GeoTileMapper? TryCreate(RouteLayout route)
        {
            if (route.WorldTiles.Count == 0)
            {
                return null;
            }

            List<WorldTile> coverageTiles = route.TerrainTiles
                .Select(t => t.WorldTile)
                .Where(t => t is not null)
                .Select(t => t!)
                .DistinctBy(t => (t.X, t.Z))
                .ToList();
            if (coverageTiles.Count == 0)
            {
                coverageTiles = route.WorldTiles.ToList();
            }

            int minX = coverageTiles.Min(t => t.X);
            int minZ = coverageTiles.Min(t => t.Z);
            GeoTileMapper mapper = new(minX, minZ, route.TsreProjection);
            List<(double Lon, double Lat)> corners = [];
            foreach (WorldTile tile in coverageTiles)
            {
                mapper.AddTileCorners(corners, tile.X, tile.Z, 1, 1);
            }

            mapper.SetBounds(corners);
            return mapper;
        }

        private void SetBounds(List<(double Lon, double Lat)> corners)
        {
            MinLon = corners.Min(c => c.Lon);
            MinLat = corners.Min(c => c.Lat);
            MaxLon = corners.Max(c => c.Lon);
            MaxLat = corners.Max(c => c.Lat);
        }

        public (double MinLon, double MinLat, double MaxLon, double MaxLat) GetBoundingBox(
            WorldTile tile,
            double sourceOffsetX = 0,
            double sourceOffsetZ = 0,
            double sourceScaleX = 1,
            double sourceScaleZ = 1)
        {
            double centerTileX = minTileX + ((tile.X - minTileX) * sourceScaleX) + sourceOffsetX;
            double centerTileZ = minTileZ + ((tile.Z - minTileZ) * sourceScaleZ) + sourceOffsetZ;
            List<(double Lon, double Lat)> corners = [];
            AddTileCorners(corners, centerTileX, centerTileZ, sourceScaleX, sourceScaleZ);
            return (corners.Min(c => c.Lon), corners.Min(c => c.Lat), corners.Max(c => c.Lon), corners.Max(c => c.Lat));
        }

        public GeoSampleGrid GetSampleGrid(
            WorldTile tile,
            double sourceOffsetX = 0,
            double sourceOffsetZ = 0,
            double sourceScaleX = 1,
            double sourceScaleZ = 1,
            double sourceBiasEastMeters = 0,
            double sourceBiasNorthMeters = 0)
        {
            double centerTileX = minTileX + ((tile.X - minTileX) * sourceScaleX) + sourceOffsetX;
            double centerTileZ = minTileZ + ((tile.Z - minTileZ) * sourceScaleZ) + sourceOffsetZ;
            double[,] longitudes = new double[OrtsRawGridSize, OrtsRawGridSize];
            double[,] latitudes = new double[OrtsRawGridSize, OrtsRawGridSize];
            double minLon = double.PositiveInfinity;
            double minLat = double.PositiveInfinity;
            double maxLon = double.NegativeInfinity;
            double maxLat = double.NegativeInfinity;
            double halfX = OrtsTileSizeMeters * sourceScaleX / 2.0;
            double halfZ = OrtsTileSizeMeters * sourceScaleZ / 2.0;
            double postSpacingX = (OrtsStoredTileSpanMeters * sourceScaleX) / (OrtsRawGridSize - 1);
            double postSpacingZ = (OrtsStoredTileSpanMeters * sourceScaleZ) / (OrtsRawGridSize - 1);

            for (int y = 0; y < OrtsRawGridSize; y++)
            {
                double localZ = halfZ - (postSpacingZ * y);
                for (int x = 0; x < OrtsRawGridSize; x++)
                {
                    double localX = -halfX + (postSpacingX * x);
                    (double lon, double lat) = ConvertWorldTileCoordinate(
                        centerTileX,
                        centerTileZ,
                        localX - sourceBiasEastMeters,
                        localZ - sourceBiasNorthMeters);
                    longitudes[y, x] = lon;
                    latitudes[y, x] = lat;
                    minLon = Math.Min(minLon, lon);
                    minLat = Math.Min(minLat, lat);
                    maxLon = Math.Max(maxLon, lon);
                    maxLat = Math.Max(maxLat, lat);
                }
            }

            return new GeoSampleGrid(longitudes, latitudes, (minLon, minLat, maxLon, maxLat));
        }

        public static TileCoordinate GetTileCoordinateForLonLat(double longitudeDegrees, double latitudeDegrees)
        {
            double lon = longitudeDegrees * Math.PI / 180.0;
            double lat = latitudeDegrees * Math.PI / 180.0;
            (double goodeX, double goodeY) = ForwardGoode(lat, lon);

            double tileXContinuous = ((goodeX - UpperLeftGoodeX) / OrtsTileSizeMeters) + 1.0 + WorldTileEastWestOffset;
            double tileZContinuous = WorldTileNorthSouthOffset - (((UpperLeftGoodeY - goodeY) / OrtsTileSizeMeters) + 1.0);

            return new TileCoordinate(
                (int)Math.Floor(tileXContinuous + 0.5),
                (int)Math.Floor(tileZContinuous + 0.5));
        }

        public TileCoordinate GetTileCoordinateForLonLatProjected(double longitudeDegrees, double latitudeDegrees)
        {
            if (tsreProjection is null)
            {
                return GetTileCoordinateForLonLat(longitudeDegrees, latitudeDegrees);
            }

            double line = (latitudeDegrees - tsreProjection.CenterLat) * tsreStepLat;
            double sample = (longitudeDegrees - tsreProjection.CenterLon) * tsreStepLon;
            double tileX = (sample + tsreProjection.CenterX) / OrtsTileSizeMeters;
            double tileZ = (line + tsreProjection.CenterZ) / OrtsTileSizeMeters;
            return new TileCoordinate((int)Math.Floor(tileX), (int)Math.Floor(tileZ));
        }

        public static GeoSampleGrid GetSampleGridForWorldArea(
            double centerTileX,
            double centerTileZ,
            double widthMeters,
            double heightMeters,
            int gridSize,
            double sampleOffsetX = 0,
            double sampleOffsetY = 0,
            double sourceBiasEastMeters = 0,
            double sourceBiasNorthMeters = 0)
        {
            double[,] longitudes = new double[gridSize, gridSize];
            double[,] latitudes = new double[gridSize, gridSize];
            double minLon = double.PositiveInfinity;
            double minLat = double.PositiveInfinity;
            double maxLon = double.NegativeInfinity;
            double maxLat = double.NegativeInfinity;
            double halfX = widthMeters / 2.0;
            double halfZ = heightMeters / 2.0;
            double sampleSpacingX = widthMeters / (gridSize - 1);
            double sampleSpacingZ = heightMeters / (gridSize - 1);
            double localOffsetX = sampleOffsetX * sampleSpacingX;
            double localOffsetZ = sampleOffsetY * sampleSpacingZ;

            for (int y = 0; y < gridSize; y++)
            {
                double localZ = halfZ - (heightMeters * y / (gridSize - 1)) + localOffsetZ;
                for (int x = 0; x < gridSize; x++)
                {
                    double localX = -halfX + (widthMeters * x / (gridSize - 1)) + localOffsetX;
                    (double lon, double lat) = ConvertGoodeWorldTileCoordinate(
                        centerTileX,
                        centerTileZ,
                        localX - sourceBiasEastMeters,
                        localZ - sourceBiasNorthMeters);
                    longitudes[y, x] = lon;
                    latitudes[y, x] = lat;
                    minLon = Math.Min(minLon, lon);
                    minLat = Math.Min(minLat, lat);
                    maxLon = Math.Max(maxLon, lon);
                    maxLat = Math.Max(maxLat, lat);
                }
            }

            return new GeoSampleGrid(longitudes, latitudes, (minLon, minLat, maxLon, maxLat));
        }

        public GeoSampleGrid GetAreaSampleGrid(
            double centerTileX,
            double centerTileZ,
            double widthMeters,
            double heightMeters,
            int gridSize,
            double sampleOffsetX = 0,
            double sampleOffsetY = 0,
            double sourceBiasEastMeters = 0,
            double sourceBiasNorthMeters = 0)
        {
            double[,] longitudes = new double[gridSize, gridSize];
            double[,] latitudes = new double[gridSize, gridSize];
            double minLon = double.PositiveInfinity;
            double minLat = double.PositiveInfinity;
            double maxLon = double.NegativeInfinity;
            double maxLat = double.NegativeInfinity;
            double halfX = widthMeters / 2.0;
            double halfZ = heightMeters / 2.0;
            double sampleSpacingX = widthMeters / (gridSize - 1);
            double sampleSpacingZ = heightMeters / (gridSize - 1);
            double localOffsetX = sampleOffsetX * sampleSpacingX;
            double localOffsetZ = sampleOffsetY * sampleSpacingZ;

            for (int y = 0; y < gridSize; y++)
            {
                double localZ = halfZ - (heightMeters * y / (gridSize - 1)) + localOffsetZ;
                for (int x = 0; x < gridSize; x++)
                {
                    double localX = -halfX + (widthMeters * x / (gridSize - 1)) + localOffsetX;
                    (double lon, double lat) = ConvertWorldTileCoordinate(
                        centerTileX,
                        centerTileZ,
                        localX - sourceBiasEastMeters,
                        localZ - sourceBiasNorthMeters);
                    longitudes[y, x] = lon;
                    latitudes[y, x] = lat;
                    minLon = Math.Min(minLon, lon);
                    minLat = Math.Min(minLat, lat);
                    maxLon = Math.Max(maxLon, lon);
                    maxLat = Math.Max(maxLat, lat);
                }
            }

            return new GeoSampleGrid(longitudes, latitudes, (minLon, minLat, maxLon, maxLat));
        }

        private void AddTileCorners(List<(double Lon, double Lat)> corners, double tileX, double tileZ, double scaleX, double scaleZ)
        {
            double halfX = OrtsTileSizeMeters * scaleX / 2.0;
            double halfZ = OrtsTileSizeMeters * scaleZ / 2.0;
            corners.Add(ConvertWorldTileCoordinate(tileX, tileZ, -halfX, -halfZ));
            corners.Add(ConvertWorldTileCoordinate(tileX, tileZ, -halfX, halfZ));
            corners.Add(ConvertWorldTileCoordinate(tileX, tileZ, halfX, -halfZ));
            corners.Add(ConvertWorldTileCoordinate(tileX, tileZ, halfX, halfZ));
        }

        private (double Lon, double Lat) ConvertWorldTileCoordinate(double tileX, double tileZ, double localX, double localZ)
        {
            return tsreProjection is null
                ? ConvertGoodeWorldTileCoordinate(tileX, tileZ, localX, localZ)
                : ConvertTsreWorldTileCoordinate(tileX, tileZ, localX, localZ);
        }

        private (double Lon, double Lat) ConvertTsreWorldTileCoordinate(double tileX, double tileZ, double localX, double localZ)
        {
            TsreGeoProjection projection = tsreProjection!;
            double sample = (OrtsTileSizeMeters * (tileX + 0.5)) + localX - projection.CenterX;
            double line = (OrtsTileSizeMeters * (tileZ + 0.5)) + localZ - projection.CenterZ;
            return (
                projection.CenterLon + (sample / tsreStepLon),
                projection.CenterLat + (line / tsreStepLat));
        }

        private static (double Lon, double Lat) ConvertGoodeWorldTileCoordinate(double tileX, double tileZ, double localX, double localZ)
        {
            double goodeSample = tileX - WorldTileEastWestOffset;
            double goodeLine = WorldTileNorthSouthOffset - tileZ;
            double goodeX = UpperLeftGoodeX + ((goodeSample - 1.0) * OrtsTileSizeMeters) + localX;
            double goodeY = UpperLeftGoodeY - ((goodeLine - 1.0) * OrtsTileSizeMeters) + localZ;

            (double lat, double lon) = InverseGoode(goodeX, goodeY);
            return (lon * 180.0 / Math.PI, lat * 180.0 / Math.PI);
        }

        private static (double Lat, double Lon) InverseGoode(double goodeX, double goodeY)
        {
            int region = GetGoodeRegion(goodeX, goodeY);
            double falseEasting = EarthRadiusMeters * LongitudeCenters[region];
            goodeX -= falseEasting;

            double latitude;
            double longitude;
            switch (region)
            {
                case 1:
                case 3:
                case 4:
                case 5:
                case 8:
                case 9:
                    latitude = goodeY / EarthRadiusMeters;
                    double temp = Math.Abs(latitude) - (Math.PI / 2.0);
                    longitude = Math.Abs(temp) > Epsilon
                        ? AdjustLongitude(LongitudeCenters[region] + (goodeX / (EarthRadiusMeters * Math.Cos(latitude))))
                        : LongitudeCenters[region];
                    break;
                default:
                    double arg = (goodeY + (0.0528035274542 * EarthRadiusMeters * Sign(goodeY))) / (1.4142135623731 * EarthRadiusMeters);
                    arg = Math.Clamp(arg, -1.0, 1.0);
                    double theta = Math.Asin(arg);
                    longitude = LongitudeCenters[region] + (goodeX / (0.900316316158 * EarthRadiusMeters * Math.Cos(theta)));
                    arg = ((2.0 * theta) + Math.Sin(2.0 * theta)) / Math.PI;
                    latitude = Math.Asin(Math.Clamp(arg, -1.0, 1.0));
                    break;
            }

            return (latitude, longitude);
        }

        private static (double X, double Y) ForwardGoode(double latitude, double longitude)
        {
            int region = GetGoodeRegionForLonLat(latitude, longitude);
            double adjustedLongitude = AdjustLongitude(longitude - LongitudeCenters[region]);

            switch (region)
            {
                case 1:
                case 3:
                case 4:
                case 5:
                case 8:
                case 9:
                    return (
                        (EarthRadiusMeters * LongitudeCenters[region]) + (EarthRadiusMeters * adjustedLongitude * Math.Cos(latitude)),
                        EarthRadiusMeters * latitude);
                default:
                    double theta = SolveMollweideTheta(latitude);
                    return (
                        (EarthRadiusMeters * LongitudeCenters[region]) +
                            (0.900316316158 * EarthRadiusMeters * adjustedLongitude * Math.Cos(theta)),
                        (1.4142135623731 * EarthRadiusMeters * Math.Sin(theta)) -
                            (0.0528035274542 * EarthRadiusMeters * Sign(latitude)));
            }
        }

        private static int GetGoodeRegionForLonLat(double latitude, double longitude)
        {
            const double transitionLatitude = 0.710987989993;
            const double minus100Degrees = -1.74532925199;
            const double minus40Degrees = -0.698131700798;
            const double minus20Degrees = -0.349065850399;
            const double plus80Degrees = 1.3962634016;

            if (latitude >= transitionLatitude)
            {
                return longitude <= minus40Degrees ? 0 : 2;
            }

            if (latitude >= 0)
            {
                return longitude <= minus40Degrees ? 1 : 3;
            }

            if (latitude >= -transitionLatitude)
            {
                if (longitude <= minus100Degrees)
                {
                    return 4;
                }

                if (longitude <= minus20Degrees)
                {
                    return 5;
                }

                return longitude <= plus80Degrees ? 8 : 9;
            }

            if (longitude <= minus100Degrees)
            {
                return 6;
            }

            if (longitude <= minus20Degrees)
            {
                return 7;
            }

            return longitude <= plus80Degrees ? 10 : 11;
        }

        private static double SolveMollweideTheta(double latitude)
        {
            double theta = latitude;
            double target = Math.PI * Math.Sin(latitude);
            for (int i = 0; i < 12; i++)
            {
                double numerator = (2.0 * theta) + Math.Sin(2.0 * theta) - target;
                double denominator = 2.0 + (2.0 * Math.Cos(2.0 * theta));
                if (Math.Abs(denominator) < Epsilon)
                {
                    break;
                }

                double delta = numerator / denominator;
                theta -= delta;
                if (Math.Abs(delta) < Epsilon)
                {
                    break;
                }
            }

            return theta;
        }

        private static int GetGoodeRegion(double goodeX, double goodeY)
        {
            if (goodeY >= EarthRadiusMeters * 0.710987989993)
            {
                return goodeX <= EarthRadiusMeters * -0.698131700798 ? 0 : 2;
            }

            if (goodeY >= 0)
            {
                return goodeX <= EarthRadiusMeters * -0.698131700798 ? 1 : 3;
            }

            if (goodeY >= EarthRadiusMeters * -0.710987989993)
            {
                if (goodeX <= EarthRadiusMeters * -1.74532925199)
                {
                    return 4;
                }

                if (goodeX <= EarthRadiusMeters * -0.349065850399)
                {
                    return 5;
                }

                return goodeX <= EarthRadiusMeters * 1.3962634016 ? 8 : 9;
            }

            if (goodeX <= EarthRadiusMeters * -1.74532925199)
            {
                return 6;
            }

            if (goodeX <= EarthRadiusMeters * -0.349065850399)
            {
                return 7;
            }

            return goodeX <= EarthRadiusMeters * 1.3962634016 ? 10 : 11;
        }

        private static double AdjustLongitude(double value)
        {
            return Math.Abs(value) > Math.PI ? value - (Sign(value) * 2.0 * Math.PI) : value;
        }

        private static double Sign(double value)
        {
            return value < 0 ? -1.0 : 1.0;
        }
    }

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
        int SamplesUsed);

    private sealed record TerrainGenerationResult(
        short[,] Heights,
        int PrimarySamplesUsed,
        int IntermediateSamplesUsed,
        int FallbackSamplesUsed,
        int NeighborFilledSamples);

    private sealed record GeoSampleGrid(
        double[,] Longitudes,
        double[,] Latitudes,
        (double MinLon, double MinLat, double MaxLon, double MaxLat) BoundingBox);

    private sealed record DemWindow(string ProductName, short[,] Heights, int ValidSamples);

    private sealed record DemWindowSearchResult(IReadOnlyList<DemWindow> Windows, bool ProductSearchFailed);

    private sealed record TerrainSampleEncoding(float Floor, float Scale);

    private sealed record PatchCornerRef(float[,] Grid, int X, int Y);

    private sealed record RawCornerRef(short[,] Grid, int X, int Y);

    private sealed class TsreLowQuadTree
    {
        private const int RootLevel = 256;
        private const int LowTileLevel = 16;
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

            tdFile.Root.AddTile(tileX, tileZ, LowTileLevel);
        }

        public void Save(string tdDir)
        {
            SaveIndex(Path.Combine(tdDir, "lo_td_idx.dat"));
            foreach (((int qx, int qz), TdFile tdFile) in tdFiles)
            {
                SaveTdFile(Path.Combine(tdDir, GetNameXy(qx) + GetNameXy(qz) + ".tdl"), tdFile);
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
