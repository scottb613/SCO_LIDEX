using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ORterr;

internal static partial class Program
{
    private static void RunRollingTerrainProbe()
    {
        string probeRoot = Path.Combine(
            Path.GetTempPath(),
            $"SCOLIDEX-rolling-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probeRoot);

        try
        {
            ProbeRolling4mWriter(Path.Combine(probeRoot, "terrain"));
            ProbeRollingDistantMountainWriter(Path.Combine(probeRoot, "lo_tiles"));
            ProbeLegacyMapMaterialReset(Path.Combine(probeRoot, "material"));
            ProbeRollingCounterContract();
            Console.WriteLine("Rolling terrain probe: PASSED");
            Console.WriteLine("  HD Test 4m writes bounded two-row seam windows");
            Console.WriteLine("  Distant Mountains write bounded two-row seam windows");
            Console.WriteLine("  shared edges/corners and output grid sizes verified");
            Console.WriteLine("  legacy map terrain material resets to terrain.ace");
            Console.WriteLine("  live GUI counter messages recognized");
        }
        finally
        {
            if (Directory.Exists(probeRoot))
            {
                Directory.Delete(probeRoot, recursive: true);
            }
        }
    }

    private static void ProbeRolling4mWriter(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        FileInfo template = FindGeneratedTerrainTileTemplate()
            ?? throw new InvalidOperationException(
                "rolling probe could not find a normal terrain template");
        const int columns = 3;
        const int rows = 4;
        Dictionary<(int X, int Z), short[,]> grids = [];
        RollingExperimentalTerrainWriter writer = new(outputDir);
        int sequence = 0;

        for (int z = 0; z < rows; z++)
        {
            for (int x = 0; x < columns; x++)
            {
                writer.FlushRowsBefore(z - 1);
                string baseName = $"-04bea{sequence:x3}";
                string tilePath = Path.Combine(outputDir, baseName + ".t");
                string rawPath = Path.Combine(outputDir, baseName + "_y.raw");
                File.WriteAllBytes(
                    tilePath,
                    CreateTerrainTileFromTemplate(template, baseName));
                short[,] heights = CreateProbeGrid(
                    ExperimentalRawGridSize,
                    (short)(100 + (z * 40) + (x * 7)));
                grids[(x, z)] = heights;
                TerrainTile tile = new(
                    new FileInfo(tilePath),
                    rawPath,
                    new WorldTile(x, z, new FileInfo(Path.Combine(outputDir, $"w{x}-{z}.w"))));
                writer.Add(new ExperimentalGeneratedTile(tile, heights));
                sequence++;
            }
        }

        writer.FlushAll();
        if (writer.PeakPendingCount > columns * 2 ||
            writer.PeakPendingCount >= columns * rows)
        {
            throw new InvalidOperationException(
                $"4m rolling window retained {writer.PeakPendingCount} of " +
                $"{columns * rows} tile grids");
        }

        VerifyProbeSeams(grids, ExperimentalRawGridSize);
        int expectedBytes = ExperimentalRawGridSize *
            ExperimentalRawGridSize * sizeof(short);
        VerifyProbeRawFiles(outputDir, columns * rows, expectedBytes);
    }

    private static void ProbeRollingDistantMountainWriter(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        FileInfo template = FindLoTileTemplate(outputDir, null)
            ?? throw new InvalidOperationException(
                "rolling probe could not find a Distant Mountain template");
        const int columns = 3;
        const int rows = 4;
        Dictionary<(int X, int Z), short[,]> grids = [];
        RollingDistantMountainWriter writer = new();

        for (int z = 0; z < rows; z++)
        {
            for (int x = 0; x < columns; x++)
            {
                LoTileCoordinate coordinate = new(
                    x * LoTileNormalTileSpan,
                    z * LoTileNormalTileSpan);
                writer.FlushRowsBefore(DistantMountainGridKey(coordinate).Z - 1);
                string name = LoTileNameFromTileXZ(coordinate.X, coordinate.Z);
                short[,] heights = CreateProbeGrid(
                    LoRawGridSize,
                    (short)(300 + (z * 40) + (x * 7)));
                grids[(x, z)] = heights;
                writer.Add(new GeneratedLoTile(
                    coordinate,
                    name,
                    template,
                    Path.Combine(outputDir, name + ".t"),
                    Path.Combine(outputDir, name + "_y.raw"),
                    heights,
                    0,
                    LoRawGridSize * LoRawGridSize));
            }
        }

        writer.FlushAll();
        if (writer.PeakPendingCount > columns * 2 ||
            writer.PeakPendingCount >= columns * rows)
        {
            throw new InvalidOperationException(
                $"DM rolling window retained {writer.PeakPendingCount} of " +
                $"{columns * rows} lo_tile grids");
        }

        VerifyProbeSeams(grids, LoRawGridSize);
        int expectedBytes = LoRawGridSize * LoRawGridSize * sizeof(short);
        VerifyProbeRawFiles(outputDir, columns * rows, expectedBytes);
    }

    private static short[,] CreateProbeGrid(int gridSize, short height)
    {
        short[,] grid = new short[gridSize, gridSize];
        for (int y = 0; y < gridSize; y++)
        {
            for (int x = 0; x < gridSize; x++)
            {
                grid[y, x] = height;
            }
        }

        return grid;
    }

    private static void VerifyProbeSeams(
        IReadOnlyDictionary<(int X, int Z), short[,]> grids,
        int gridSize)
    {
        int edge = gridSize - 1;
        foreach (KeyValuePair<(int X, int Z), short[,]> item in grids)
        {
            if (grids.TryGetValue((item.Key.X + 1, item.Key.Z), out short[,]? east))
            {
                for (int y = 0; y < gridSize; y++)
                {
                    if (item.Value[y, edge] != east[y, 0])
                    {
                        throw new InvalidOperationException("east/west seam mismatch");
                    }
                }
            }

            if (grids.TryGetValue((item.Key.X, item.Key.Z + 1), out short[,]? north))
            {
                for (int x = 0; x < gridSize; x++)
                {
                    if (item.Value[0, x] != north[edge, x])
                    {
                        throw new InvalidOperationException("north/south seam mismatch");
                    }
                }
            }
        }
    }

    private static void VerifyProbeRawFiles(
        string outputDir,
        int expectedCount,
        int expectedBytes)
    {
        FileInfo[] rawFiles = new DirectoryInfo(outputDir)
            .EnumerateFiles("*_y.raw")
            .ToArray();
        if (rawFiles.Length != expectedCount ||
            rawFiles.Any(file => file.Length != expectedBytes))
        {
            throw new InvalidOperationException(
                $"rolling output raw validation failed; count={rawFiles.Length}, " +
                $"expected={expectedCount}, bytes={expectedBytes}");
        }
    }

    private static void ProbeRollingCounterContract()
    {
        const string tileLine = "[4m 1/12] -04bea000.t (11 remaining)";
        const string sourceLine =
            "  -> Source samples used: 1m=262,144, 5m~=0, 10m=0, " +
            "30m (global)=0, neighbor-fill=0";
        if (!TopoForm.RecognizesRouteTileProgressForProbe(tileLine) ||
            !TopoForm.RecognizesRouteSourceProgressForProbe(sourceLine))
        {
            throw new InvalidOperationException(
                "GUI does not recognize HD Test live counter messages");
        }
    }

    private static void ProbeLegacyMapMaterialReset(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        FileInfo template = FindGeneratedTerrainTileTemplate()
            ?? throw new InvalidOperationException(
                "material probe could not find a normal terrain template");
        byte[] clean = File.ReadAllBytes(template.FullName);
        byte[] legacyMap = CreateMapPatchedTerrainBytes(
            clean, "-04bea000_map.ace");
        if (!Encoding.Unicode.GetString(legacyMap)
                .Contains("_map.ace", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "material probe did not construct a legacy map terrain tile");
        }

        byte[] normalized = NormalizeLegacyMapTerrainMaterial(
            legacyMap, "material-probe.t", out bool changed);
        string normalizedText = Encoding.Unicode.GetString(normalized);
        if (!changed ||
            !normalizedText.Contains("terrain.ace", StringComparison.OrdinalIgnoreCase) ||
            normalizedText.Contains("_map.ace", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "legacy map terrain material was not reset to terrain.ace");
        }

        PatchTerrainResolutionMetadata(
            normalized,
            ExperimentalRawGridSize,
            (float)ExperimentalPostSpacingMeters);
        string path = Path.Combine(outputDir, "material-probe.t");
        File.WriteAllBytes(path, normalized);
        if (!TryReadTerrainResolutionMetadata(path, out int samples, out float spacing) ||
            samples != ExperimentalRawGridSize ||
            Math.Abs(spacing - ExperimentalPostSpacingMeters) > 0.01)
        {
            throw new InvalidOperationException(
                "material reset did not preserve writable 4m terrain metadata");
        }
    }
}
