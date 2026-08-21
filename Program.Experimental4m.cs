// SCO LIDEX - HD Test 4m terrain generation and bounded rolling-row writing.
// Copyright (C) Scott Brunner, Beast of Burden
// Part of the SCO LIDEX Terrain Builder application.
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ORterr;

internal static partial class Program
{
    private const int ExperimentalRawGridSize = 512;
    private const double ExperimentalPostSpacingMeters = 4.0;

    private static async Task<bool> GenerateExperimental4mTerrainAsync(
        IReadOnlyList<TerrainTile> selectedTiles,
        GeoTileMapper mapper,
        HttpClient httpClient,
        string outputDir,
        double sourceBiasEastMeters,
        double sourceBiasNorthMeters,
        DemSourcePolicy sourcePolicy,
        bool overwriteFlag,
        CancellationToken cancellationToken)
    {
        List<TerrainTile> tiles = selectedTiles
            .OrderBy(tile => tile.WorldTile?.Z ?? int.MaxValue)
            .ThenBy(tile => tile.WorldTile?.X ?? int.MaxValue)
            .ToList();

        if (tiles.Count == 0)
        {
            Console.WriteLine("Error: HD Test - 4m Tiles found no normal terrain tiles.");
            return false;
        }

        List<string> unmapped = tiles
            .Where(tile => tile.WorldTile is null)
            .Select(tile => tile.TileFile.Name)
            .ToList();
        if (unmapped.Count > 0)
        {
            Console.WriteLine($"Error: HD Test - 4m Tiles require every route terrain tile to map to a world tile; unmapped={unmapped.Count:N0}.");
            return false;
        }

        WriteLogSection("HD Test - 4m Terrain Generation");
        WriteLogDetail("Selected tiles", $"{tiles.Count:N0}");
        WriteLogDetail("Mode", overwriteFlag ? "OVERWRITE" : "APPEND");
        WriteLogDetail("Height grid", "512x512 | 4m posts | 524,288-byte _y.raw");
        WriteLogDetail("Write policy", "continuous completed-row writes | bounded active seam window");

        RollingExperimentalTerrainWriter rollingWriter = new(outputDir);
        int generatedCount = 0;
        int skipped = 0;
        int failed = 0;
        bool aborted = false;
        SortedSet<string> retryableFailedTileNames = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < tiles.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                aborted = true;
                break;
            }
            TerrainTile tile = tiles[index];
            WorldTile worldTile = tile.WorldTile!;
            int tileNumber = index + 1;
            Console.WriteLine($"\n[4m {tileNumber:N0}/{tiles.Count:N0}] TILE: {tile.TileFile.Name} | {tiles.Count - tileNumber:N0} remaining");
            rollingWriter.FlushRowsBefore(worldTile.Z - 1);
            NormalizeTerrainMaterialFileIfLegacyMap(tile);

            RawGridStats? existingStats = TryGetRawGridStats(
                tile.RawHeightPath, ExperimentalRawGridSize);
            if (!overwriteFlag && existingStats is not null && !existingStats.IsEmpty)
            {
                skipped++;
                WriteLogDetail("Skipped", $"4m raw grid already has {existingStats.ValidCount:N0} valid height samples");
                PrintProgressCheckpoint(tileNumber, tiles.Count, generatedCount, skipped, failed);
                continue;
            }

            try
            {
                GeoSampleGrid sampleGrid = mapper.GetSampleGridForResolution(
                    worldTile,
                    ExperimentalRawGridSize,
                    ExperimentalPostSpacingMeters,
                    sourceBiasEastMeters: sourceBiasEastMeters,
                    sourceBiasNorthMeters: sourceBiasNorthMeters);
                TerrainGenerationResult result = await StreamOrtsGridForSampleGridAsync(httpClient, sampleGrid, sourcePolicy);
                Console.WriteLine(result.GlobalSamplesUsed > 0
                    ? "STATUS: TILES - GLOBAL - LOW RES"
                    : "STATUS: TILES - US - HIGH RES");
                rollingWriter.Add(new ExperimentalGeneratedTile(tile, result.Heights));
                generatedCount++;
                WriteLogDetail(
                    "Source samples used",
                    $"{PrimaryDemLabel}={result.PrimarySamplesUsed:N0}, " +
                    $"{IntermediateDemLabel}={result.IntermediateSamplesUsed:N0}, " +
                    $"{FallbackDemLabel}={result.FallbackSamplesUsed:N0}, " +
                    $"{GlobalDemLabel}={result.GlobalSamplesUsed:N0}, " +
                    $"neighbor-fill={result.NeighborFilledSamples:N0}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                retryableFailedTileNames.Add(GetTerrainTileBaseName(tile));
                MarkTerrainTileForAppendRetry(tile, ExperimentalRawGridSize);
                WriteLogDetail("HD Test 4m generation failed", ex.Message);
            }

            PrintProgressCheckpoint(tileNumber, tiles.Count, generatedCount, skipped, failed);
        }

        try
        {
            rollingWriter.FlushAll();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: failed while writing HD Test 4m terrain rows: {ex.Message}");
            return false;
        }

        WriteLogSubsection("HD Terrain Output");
        WriteLogDetail("Peak memory window", $"{rollingWriter.PeakPendingCount:N0} tile grid(s)", 4);
        Console.WriteLine(
            $"\n  HD TERRAIN DONE. Generated={generatedCount:N0}, skipped={skipped:N0}, " +
            $"failed={failed:N0}, total={tiles.Count:N0}.");
        PrintFailedTileTextFileBlock(retryableFailedTileNames);
        return !aborted && failed == 0;
    }

    private static void WriteExperimental4mTile(
        string outputDir,
        ExperimentalGeneratedTile generated,
        float[,] patchHeights)
    {
        TerrainSampleEncoding encoding = CalculateSampleEncoding(generated.Heights);
        byte[] tileBytes = File.ReadAllBytes(generated.Tile.TileFile.FullName);
        tileBytes = NormalizeLegacyMapTerrainMaterial(
            tileBytes, generated.Tile.TileFile.Name, out _);
        try
        {
            PatchTerrainTileHeights(tileBytes, patchHeights, encoding);
        }
        catch (InvalidOperationException)
        {
            FileInfo template = FindGeneratedTerrainTileTemplate()
                ?? throw new InvalidOperationException("could not find a clean terrain .t template for HD Test 4m output");
            tileBytes = CreateTerrainTileFromTemplate(
                template,
                Path.GetFileNameWithoutExtension(generated.Tile.TileFile.Name).ToLowerInvariant());
            PatchTerrainTileHeights(tileBytes, patchHeights, encoding);
        }

        bool patchedCount = TryPatchBinaryTokenInt(tileBytes, TokenTerrainNSamples, ExperimentalRawGridSize);
        bool patchedSize = TryPatchBinaryTokenFloat(tileBytes, TokenTerrainSampleSize, (float)ExperimentalPostSpacingMeters);
        if (!patchedCount || !patchedSize)
        {
            throw new InvalidOperationException("terrain .t sample-count/sample-size tokens could not be patched safely");
        }

        string tileName = generated.Tile.TileFile.Name.ToLowerInvariant();
        string baseName = Path.GetFileNameWithoutExtension(tileName);
        string tilePath = Path.Combine(outputDir, tileName);
        string rawPath = Path.Combine(outputDir, baseName + "_y.raw");
        EnsureExactFileNameCasing(tilePath);
        EnsureExactFileNameCasing(rawPath);
        File.WriteAllBytes(tilePath, tileBytes);
        WriteExperimentalRawGrid(rawPath, generated.Heights, encoding);
    }

    private static bool TryPatchBinaryTokenInt(byte[] bytes, ushort targetToken, int value)
    {
        bool patched = false;
        WalkBinaryTokens(bytes, 32, bytes.Length, 0, (token, payload, blockEnd) =>
        {
            if (token == targetToken && payload + sizeof(int) <= blockEnd)
            {
                BitConverter.GetBytes(value).CopyTo(bytes, payload);
                patched = true;
            }
        });
        return patched;
    }

    private static void WriteExperimentalRawGrid(string path, float[,] heights, TerrainSampleEncoding encoding)
    {
        int height = heights.GetLength(0);
        int width = heights.GetLength(1);
        if (height != ExperimentalRawGridSize || width != ExperimentalRawGridSize)
        {
            throw new InvalidOperationException($"HD Test raw grid is {width}x{height}, expected 512x512");
        }

        byte[] bytes = new byte[width * height * sizeof(ushort)];
        int offset = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ushort value = heights[y, x] == RawMissingHeight
                    ? (ushort)0
                    : (ushort)Math.Clamp(
                        (int)Math.Round((heights[y, x] - encoding.Floor) / encoding.Scale, MidpointRounding.AwayFromZero),
                        0,
                        ushort.MaxValue - 1);
                bytes[offset++] = (byte)(value & 0xff);
                bytes[offset++] = (byte)(value >> 8);
            }
        }
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteExperimentalRawGrid(string path, short[,] heights, TerrainSampleEncoding encoding)
    {
        int height = heights.GetLength(0);
        int width = heights.GetLength(1);
        if (height != ExperimentalRawGridSize || width != ExperimentalRawGridSize)
        {
            throw new InvalidOperationException($"HD Test raw grid is {width}x{height}, expected 512x512");
        }

        byte[] bytes = new byte[width * height * sizeof(ushort)];
        int offset = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ushort value = heights[y, x] == RawMissingHeight
                    ? (ushort)0
                    : (ushort)Math.Clamp(
                        (int)Math.Round((heights[y, x] - encoding.Floor) / encoding.Scale, MidpointRounding.AwayFromZero),
                        0,
                        ushort.MaxValue - 1);
                bytes[offset++] = (byte)(value & 0xff);
                bytes[offset++] = (byte)(value >> 8);
            }
        }

        File.WriteAllBytes(path, bytes);
    }

    private static void MergeExperimentalSharedEdges(IDictionary<(int X, int Z), short[,]> grids)
    {
        foreach (KeyValuePair<(int X, int Z), short[,]> item in grids)
        {
            (int x, int z) = item.Key;
            short[,] grid = item.Value;
            if (grids.TryGetValue((x + 1, z), out short[,]? east))
            {
                int edge = ExperimentalRawGridSize - 1;
                for (int y = 0; y < ExperimentalRawGridSize; y++)
                {
                    short merged = MergeHeights(grid[y, edge], east[y, 0]);
                    grid[y, edge] = merged;
                    east[y, 0] = merged;
                }
            }

            if (grids.TryGetValue((x, z + 1), out short[,]? north))
            {
                int edge = ExperimentalRawGridSize - 1;
                for (int xIndex = 0; xIndex < ExperimentalRawGridSize; xIndex++)
                {
                    short merged = MergeHeights(grid[0, xIndex], north[edge, xIndex]);
                    grid[0, xIndex] = merged;
                    north[edge, xIndex] = merged;
                }
            }
        }

        Dictionary<(int X, int Z), List<(short[,] Grid, int X, int Y)>> corners = [];
        foreach (KeyValuePair<(int X, int Z), short[,]> item in grids)
        {
            int edge = ExperimentalRawGridSize - 1;
            AddDecodedCorner(corners, item.Key, item.Value, 0, edge);
            AddDecodedCorner(corners, (item.Key.X + 1, item.Key.Z), item.Value, edge, edge);
            AddDecodedCorner(corners, (item.Key.X, item.Key.Z + 1), item.Value, 0, 0);
            AddDecodedCorner(corners, (item.Key.X + 1, item.Key.Z + 1), item.Value, edge, 0);
        }

        foreach (List<(short[,] Grid, int X, int Y)> refs in corners.Values.Where(refs => refs.Count > 1))
        {
            short[] valid = refs
                .Select(item => item.Grid[item.Y, item.X])
                .Where(value => value != RawMissingHeight)
                .ToArray();
            if (valid.Length == 0)
            {
                continue;
            }

            short merged = ClampToInt16Meters(valid.Average(value => value));
            foreach ((short[,] grid, int x, int y) in refs)
            {
                grid[y, x] = merged;
            }
        }
    }

    private static float[,] BuildExperimentalPatchHeights(float[,] heights)
    {
        float[,] patches = new float[TerrainPatchGridSize, TerrainPatchGridSize];
        int blockSize = ExperimentalRawGridSize / TerrainPatchGridSize;
        for (int patchY = 0; patchY < TerrainPatchGridSize; patchY++)
        {
            for (int patchX = 0; patchX < TerrainPatchGridSize; patchX++)
            {
                double sum = 0;
                int count = 0;
                for (int y = patchY * blockSize; y < (patchY + 1) * blockSize; y++)
                {
                    for (int x = patchX * blockSize; x < (patchX + 1) * blockSize; x++)
                    {
                        float value = heights[y, x];
                        if (value != RawMissingHeight)
                        {
                            sum += value;
                            count++;
                        }
                    }
                }
                patches[patchY, patchX] = count == 0 ? 0 : (float)((double)sum / count);
            }
        }
        return patches;
    }

    private static void MergeExperimentalSharedEdges(IDictionary<(int X, int Z), float[,]> grids)
    {
        foreach (KeyValuePair<(int X, int Z), float[,]> item in grids)
        {
            (int x, int z) = item.Key;
            float[,] grid = item.Value;
            if (grids.TryGetValue((x + 1, z), out float[,]? east))
            {
                int edge = ExperimentalRawGridSize - 1;
                for (int y = 0; y < ExperimentalRawGridSize; y++)
                {
                    float merged = MergeHeights(grid[y, edge], east[y, 0]);
                    grid[y, edge] = merged;
                    east[y, 0] = merged;
                }
            }
            if (grids.TryGetValue((x, z + 1), out float[,]? north))
            {
                int edge = ExperimentalRawGridSize - 1;
                for (int column = 0; column < ExperimentalRawGridSize; column++)
                {
                    float merged = MergeHeights(grid[0, column], north[edge, column]);
                    grid[0, column] = merged;
                    north[edge, column] = merged;
                }
            }
        }

        Dictionary<(int X, int Z), List<ExperimentalCornerRef>> corners = [];
        foreach (KeyValuePair<(int X, int Z), float[,]> item in grids)
        {
            (int x, int z) = item.Key;
            int edge = ExperimentalRawGridSize - 1;
            AddExperimentalCorner(corners, (x, z), item.Value, 0, edge);
            AddExperimentalCorner(corners, (x + 1, z), item.Value, edge, edge);
            AddExperimentalCorner(corners, (x, z + 1), item.Value, 0, 0);
            AddExperimentalCorner(corners, (x + 1, z + 1), item.Value, edge, 0);
        }
        foreach (List<ExperimentalCornerRef> refs in corners.Values.Where(refs => refs.Count > 1))
        {
            List<float> valid = refs.Select(item => item.Grid[item.Y, item.X]).Where(value => value != RawMissingHeight).ToList();
            if (valid.Count == 0)
            {
                continue;
            }
            float merged = valid.Average();
            foreach (ExperimentalCornerRef item in refs)
            {
                item.Grid[item.Y, item.X] = merged;
            }
        }
    }

    private static void AddExperimentalCorner(
        IDictionary<(int X, int Z), List<ExperimentalCornerRef>> corners,
        (int X, int Z) key,
        float[,] grid,
        int x,
        int y)
    {
        if (!corners.TryGetValue(key, out List<ExperimentalCornerRef>? refs))
        {
            refs = [];
            corners[key] = refs;
        }
        refs.Add(new ExperimentalCornerRef(grid, x, y));
    }

    private sealed record ExperimentalGeneratedTile(TerrainTile Tile, float[,] Heights);
    private sealed record ExperimentalCornerRef(float[,] Grid, int X, int Y);

    private sealed class RollingExperimentalTerrainWriter
    {
        private readonly string outputDir;
        private readonly Dictionary<(int X, int Z), ExperimentalGeneratedTile> pending = [];

        public RollingExperimentalTerrainWriter(string outputDir)
        {
            this.outputDir = outputDir;
            Directory.CreateDirectory(outputDir);
        }

        public int PeakPendingCount { get; private set; }

        public void Add(ExperimentalGeneratedTile generated)
        {
            WorldTile worldTile = generated.Tile.WorldTile
                ?? throw new InvalidOperationException(
                    $"Terrain tile {generated.Tile.TileFile.Name} is not matched to a world tile.");
            (int X, int Z) key = (worldTile.X, worldTile.Z);
            if (!pending.TryAdd(key, generated))
            {
                throw new InvalidOperationException(
                    $"duplicate generated world tile coordinate X={key.X}, Z={key.Z}");
            }

            MergeWithPendingNeighbors(key, generated.Heights);
            PeakPendingCount = Math.Max(PeakPendingCount, pending.Count);
        }

        public void FlushRowsBefore(int minZToKeep)
        {
            FlushRows(pending.Keys
                .Select(key => key.Z)
                .Distinct()
                .Where(z => z < minZToKeep)
                .OrderBy(z => z)
                .ToList());
        }

        public void FlushAll()
        {
            FlushRows(pending.Keys
                .Select(key => key.Z)
                .Distinct()
                .OrderBy(z => z)
                .ToList());
        }

        private void MergeWithPendingNeighbors((int X, int Z) key, float[,] heights)
        {
            if (pending.TryGetValue((key.X - 1, key.Z), out ExperimentalGeneratedTile? west))
            {
                MergeVerticalEdge(west.Heights, heights);
            }

            if (pending.TryGetValue((key.X + 1, key.Z), out ExperimentalGeneratedTile? east))
            {
                MergeVerticalEdge(heights, east.Heights);
            }

            if (pending.TryGetValue((key.X, key.Z - 1), out ExperimentalGeneratedTile? south))
            {
                MergeHorizontalEdge(south.Heights, heights);
            }

            if (pending.TryGetValue((key.X, key.Z + 1), out ExperimentalGeneratedTile? north))
            {
                MergeHorizontalEdge(heights, north.Heights);
            }
        }

        private void FlushRows(IReadOnlyCollection<int> rowsToFlush)
        {
            if (rowsToFlush.Count == 0)
            {
                return;
            }

            Dictionary<(int X, int Z), float[,]> rawWindow = pending.ToDictionary(
                item => item.Key, item => item.Value.Heights);
            MergeExperimentalSharedEdges(rawWindow);

            Dictionary<string, float[,]> patchesByTile =
                new(StringComparer.OrdinalIgnoreCase);
            Dictionary<(int X, int Z), float[,]> patchesByWorld = [];
            foreach (KeyValuePair<(int X, int Z), ExperimentalGeneratedTile> item in pending)
            {
                float[,] patches = BuildExperimentalPatchHeights(item.Value.Heights);
                patchesByTile[item.Value.Tile.TileFile.Name] = patches;
                patchesByWorld[item.Key] = patches;
            }

            MergeSharedPatchEdges(patchesByWorld);
            MergeSharedPatchCorners(patchesByWorld);

            List<(int X, int Z)> keysToFlush = pending.Keys
                .Where(key => rowsToFlush.Contains(key.Z))
                .OrderBy(key => key.Z)
                .ThenBy(key => key.X)
                .ToList();
            foreach ((int X, int Z) key in keysToFlush)
            {
                ExperimentalGeneratedTile generated = pending[key];
                WriteExperimental4mTile(
                    outputDir,
                    generated,
                    patchesByTile[generated.Tile.TileFile.Name]);
                Console.WriteLine(
                    $"  -> Wrote {generated.Tile.TileFile.Name} from rolling 4m seam window.");
                pending.Remove(key);
            }
        }

        private static void MergeVerticalEdge(float[,] west, float[,] east)
        {
            int edge = ExperimentalRawGridSize - 1;
            for (int y = 0; y < ExperimentalRawGridSize; y++)
            {
                float merged = MergeHeights(west[y, edge], east[y, 0]);
                west[y, edge] = merged;
                east[y, 0] = merged;
            }
        }

        private static void MergeHorizontalEdge(float[,] south, float[,] north)
        {
            int edge = ExperimentalRawGridSize - 1;
            for (int x = 0; x < ExperimentalRawGridSize; x++)
            {
                float merged = MergeHeights(south[0, x], north[edge, x]);
                south[0, x] = merged;
                north[edge, x] = merged;
            }
        }
    }
}
