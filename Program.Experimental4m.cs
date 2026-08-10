// SCO LIDEX - isolated experimental 4 m normal-terrain test writer.
// This deliberately avoids changing the established 8 m production writer.

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
        RouteLayout route,
        GeoTileMapper mapper,
        HttpClient httpClient,
        string outputDir,
        double sourceBiasEastMeters,
        double sourceBiasNorthMeters,
        DemSourcePolicy sourcePolicy,
        CancellationToken cancellationToken)
    {
        List<TerrainTile> tiles = route.TerrainTiles
            .OrderBy(tile => tile.WorldTile?.Z ?? int.MaxValue)
            .ThenBy(tile => tile.WorldTile?.X ?? int.MaxValue)
            .ToList();

        if (tiles.Count == 0)
        {
            Console.WriteLine("Error: experimental 4m test found no normal terrain tiles.");
            return false;
        }

        List<string> unmapped = tiles
            .Where(tile => tile.WorldTile is null)
            .Select(tile => tile.TileFile.Name)
            .ToList();
        if (unmapped.Count > 0)
        {
            Console.WriteLine($"Error: experimental 4m test requires every route terrain tile to map to a world tile; unmapped={unmapped.Count:N0}.");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine("=========================================");
        Console.WriteLine(" EXPERIMENTAL 4m NORMAL TERRAIN TEST ");
        Console.WriteLine("=========================================");
        Console.WriteLine($"Normal terrain tiles: {tiles.Count:N0} (complete route footprint)");
        Console.WriteLine("Height grid: 512x512, 4m posts, 524,288-byte _y.raw");
        Console.WriteLine("Distant Mountains: disabled");
        Console.WriteLine("Write policy: generate every tile first; write nothing if any tile fails.");

        List<ExperimentalGeneratedTile> generated = new(tiles.Count);
        for (int index = 0; index < tiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TerrainTile tile = tiles[index];
            WorldTile worldTile = tile.WorldTile!;
            Console.WriteLine($"\n[4m {index + 1:N0}/{tiles.Count:N0}] {tile.TileFile.Name}");

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
                generated.Add(new ExperimentalGeneratedTile(tile, result.Heights));
                Console.WriteLine(
                    $"  -> EXPERIMENTAL 4m OUTPUT prepared; sources " +
                    $"{PrimaryDemLabel}={result.PrimarySamplesUsed:N0}, " +
                    $"{IntermediateDemLabel}={result.IntermediateSamplesUsed:N0}, " +
                    $"{FallbackDemLabel}={result.FallbackSamplesUsed:N0}, " +
                    $"{GlobalDemLabel}={result.GlobalSamplesUsed:N0}, " +
                    $"neighbor-fill={result.NeighborFilledSamples:N0}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"  -> Experimental generation failed: {ex.Message}");
                Console.WriteLine("Error: at least one 4m tile failed; no experimental terrain files were written.");
                return false;
            }
        }

        Dictionary<(int X, int Z), short[,]> grids = generated.ToDictionary(
            item => (item.Tile.WorldTile!.X, item.Tile.WorldTile.Z),
            item => item.Heights);
        MergeExperimentalSharedEdges(grids);

        Dictionary<string, float[,]> patchHeights = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<(int X, int Z), float[,]> patchHeightsByWorld = [];
        foreach (ExperimentalGeneratedTile item in generated)
        {
            float[,] patches = BuildExperimentalPatchHeights(item.Heights);
            patchHeights[item.Tile.TileFile.Name] = patches;
            patchHeightsByWorld[(item.Tile.WorldTile!.X, item.Tile.WorldTile.Z)] = patches;
        }
        MergeSharedPatchEdges(patchHeightsByWorld);
        MergeSharedPatchCorners(patchHeightsByWorld);

        Directory.CreateDirectory(outputDir);
        for (int index = 0; index < generated.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExperimentalGeneratedTile item = generated[index];
            WriteExperimental4mTile(outputDir, item, patchHeights[item.Tile.TileFile.Name]);
            Console.WriteLine($"  [write {index + 1:N0}/{generated.Count:N0}] {item.Tile.TileFile.Name}: EXPERIMENTAL 4m OUTPUT");
        }

        Console.WriteLine($"\nEXPERIMENTAL 4m OUTPUT complete: {generated.Count:N0} normal terrain tile(s), no DM tiles.");
        return true;
    }

    private static void WriteExperimental4mTile(
        string outputDir,
        ExperimentalGeneratedTile generated,
        float[,] patchHeights)
    {
        TerrainSampleEncoding encoding = CalculateSampleEncoding(generated.Heights);
        byte[] tileBytes = File.ReadAllBytes(generated.Tile.TileFile.FullName);
        try
        {
            PatchTerrainTileHeights(tileBytes, patchHeights, encoding);
        }
        catch (InvalidOperationException)
        {
            FileInfo template = FindGeneratedTerrainTileTemplate()
                ?? throw new InvalidOperationException("could not find a clean terrain .t template for experimental output");
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

    private static void WriteExperimentalRawGrid(string path, short[,] heights, TerrainSampleEncoding encoding)
    {
        int height = heights.GetLength(0);
        int width = heights.GetLength(1);
        if (height != ExperimentalRawGridSize || width != ExperimentalRawGridSize)
        {
            throw new InvalidOperationException($"experimental raw grid is {width}x{height}, expected 512x512");
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

    private static float[,] BuildExperimentalPatchHeights(short[,] heights)
    {
        float[,] patches = new float[TerrainPatchGridSize, TerrainPatchGridSize];
        int blockSize = ExperimentalRawGridSize / TerrainPatchGridSize;
        for (int patchY = 0; patchY < TerrainPatchGridSize; patchY++)
        {
            for (int patchX = 0; patchX < TerrainPatchGridSize; patchX++)
            {
                long sum = 0;
                int count = 0;
                for (int y = patchY * blockSize; y < (patchY + 1) * blockSize; y++)
                {
                    for (int x = patchX * blockSize; x < (patchX + 1) * blockSize; x++)
                    {
                        short value = heights[y, x];
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
                for (int column = 0; column < ExperimentalRawGridSize; column++)
                {
                    short merged = MergeHeights(grid[0, column], north[edge, column]);
                    grid[0, column] = merged;
                    north[edge, column] = merged;
                }
            }
        }

        Dictionary<(int X, int Z), List<ExperimentalCornerRef>> corners = [];
        foreach (KeyValuePair<(int X, int Z), short[,]> item in grids)
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
            List<short> valid = refs.Select(item => item.Grid[item.Y, item.X]).Where(value => value != RawMissingHeight).ToList();
            if (valid.Count == 0)
            {
                continue;
            }
            short merged = ClampToInt16Meters(valid.Average(value => value));
            foreach (ExperimentalCornerRef item in refs)
            {
                item.Grid[item.Y, item.X] = merged;
            }
        }
    }

    private static void AddExperimentalCorner(
        IDictionary<(int X, int Z), List<ExperimentalCornerRef>> corners,
        (int X, int Z) key,
        short[,] grid,
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

    private sealed record ExperimentalGeneratedTile(TerrainTile Tile, short[,] Heights);
    private sealed record ExperimentalCornerRef(short[,] Grid, int X, int Y);
}
