// SCO LIDEX - Open Rails / MSTS Cloud Terrain Builder
// Copyright (C) Scott Brunner, Beast of Burden
// Distant-mountain coverage, generation, and low-terrain file handling.
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
    private static async Task<int> GenerateDistantMountainTilesAsync(
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
        DemSourcePolicy sourcePolicy,
        CancellationToken cancellationToken)
    {
        if (useMarkerCoverage && route.Markers.Count == 0)
        {
            Console.WriteLine("\nDistant Mountains: skipped because the route has no markers.");
            return 1;
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
            return 1;
        }

        if (coverage.Count == 0)
        {
            Console.WriteLine("\nDistant Mountains: skipped because no route/marker coverage tiles were found.");
            return 1;
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
            return 1;
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
        Console.WriteLine("STATUS: TILES - DISTANT MOUNTAINS");
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
                DemWindowSearchResult result = sourcePolicy.UseFallback
                    ? await ReadDemWindowsForDatasetAsync(httpClient, sampleGrid, FallbackDemDataset, failures)
                    : new DemWindowSearchResult([], ProductSearchFailed: false);
                short[,] heights = CreateMissingHeightGrid(LoRawGridSize, LoRawGridSize);
                int samplesUsed = MergeWindows(result.Windows, heights);
                int missingAfterTenMeter = heights.Cast<short>().Count(h => h == RawMissingHeight);
                int globalSamplesUsed = 0;
                if (missingAfterTenMeter > 0 && sourcePolicy.UseGlobal)
                {
                    Console.WriteLine(
                        $"  -> {FallbackDemLabel} coverage left {missingAfterTenMeter:N0} missing DM samples; " +
                        $"trying {GlobalDemLabel} fallback ({GlobalDemDisplayName}, AWS Open Data, low resolution DSM).");
                    globalSamplesUsed = MergeWindows(ReadCopernicusDemWindows(sampleGrid, failures), heights);
                }

                if ((samplesUsed == 0 && globalSamplesUsed == 0) || heights.Cast<short>().All(h => h == RawMissingHeight))
                {
                    throw new InvalidOperationException("no 10m or 30m global DEM samples were read for this lo_tile. " + string.Join(" | ", failures.Take(6)));
                }

                int missingBeforeFill = heights.Cast<short>().Count(h => h == RawMissingHeight);
                if (missingBeforeFill > 0)
                {
                    Console.WriteLine($"  -> DEM mosaic still missing {missingBeforeFill:N0} samples after global fallback; filling from neighbors.");
                    FillMissingHeights(heights);
                }

                FileInfo templateTile = FindLoTileTemplate(outputDir, loName) ?? fallbackTemplateTile;
                generatedLoTiles.Add(new GeneratedLoTile(loTile, loName, templateTile, tilePath, heightPath, heights, samplesUsed, globalSamplesUsed));
                built++;
                Console.WriteLine($"  -> Prepared TSRE-style lo_tile with 10m={samplesUsed:N0}, {GlobalDemLabel}={globalSamplesUsed:N0}, neighbor-fill={missingBeforeFill:N0} samples.");
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
        return failed;
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

        TsreTerrainQuadTree quadTree = new(targetLevel: 16, indexFileName: "lo_td_idx.dat", tileFileExtension: ".tdl");
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
}
