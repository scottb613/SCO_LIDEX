// SCO LIDEX - terrain-resolution inspection and guarded route-wide conversion.
// Copyright (C) Scott Brunner, Beast of Burden
// Part of the SCO LIDEX Terrain Builder application.
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ORterr;

internal static partial class Program
{
    internal enum TerrainOutputResolution
    {
        Normal8m,
        HdTest4m,
    }

    internal sealed record TerrainResolutionTile(
        string TileName,
        string TilePath,
        string RawPath,
        TerrainOutputResolution Resolution,
        string DetectedLabel);

    internal sealed record TerrainResolutionIssue(string TileName, string Detail);

    internal sealed record TerrainResolutionInspection(
        TerrainOutputResolution RequestedResolution,
        int TotalTiles,
        int MatchingTiles,
        IReadOnlyList<TerrainResolutionTile> MismatchedTiles,
        IReadOnlyList<TerrainResolutionIssue> UnrecognizedTiles);

    internal static string TerrainOutputLabel(TerrainOutputResolution resolution) =>
        resolution == TerrainOutputResolution.HdTest4m
            ? "HD Test - 4m Tiles"
            : "Normal - 8m Tiles";

    internal static TerrainResolutionInspection InspectTerrainResolutions(
        string routeDir,
        TerrainOutputResolution requestedResolution)
    {
        if (!RouteLayout.TryLoad(routeDir, out RouteLayout? route, out string error) || route is null)
        {
            throw new InvalidOperationException(error);
        }

        List<TerrainResolutionTile> mismatches = [];
        List<TerrainResolutionIssue> unrecognized = [];
        int matching = 0;
        foreach (TerrainTile tile in route.TerrainTiles)
        {
            string tileName = Path.GetFileNameWithoutExtension(tile.TileFile.Name);
            string rawPath = tile.RawHeightPath
                ?? Path.Combine(tile.TileFile.DirectoryName ?? "", tileName + "_y.raw");
            if (!TryDetectTerrainResolution(tile.TileFile.FullName, rawPath,
                    out TerrainOutputResolution detected, out string detail))
            {
                unrecognized.Add(new TerrainResolutionIssue(tileName, detail));
                continue;
            }

            if (detected == requestedResolution)
            {
                matching++;
                continue;
            }

            mismatches.Add(new TerrainResolutionTile(
                tileName,
                tile.TileFile.FullName,
                rawPath,
                detected,
                TerrainOutputLabel(detected)));
        }

        return new TerrainResolutionInspection(
            requestedResolution,
            route.TerrainTiles.Count,
            matching,
            mismatches,
            unrecognized);
    }

    internal static int ResetTerrainResolutionMismatches(
        string routeDir,
        TerrainResolutionInspection inspection)
    {
        string tilesRoot = Path.GetFullPath(Path.Combine(routeDir, "tiles"));
        int targetSamples = TerrainGridSize(inspection.RequestedResolution);
        float targetSpacing = TerrainPostSpacing(inspection.RequestedResolution);
        byte[] emptyRaw = CreateEmptyRawGridBytes(targetSamples);
        List<StagedTerrainResolutionTile> staged = [];
        List<PromotedTerrainResolutionTile> promoted = [];

        try
        {
            foreach (TerrainResolutionTile mismatch in inspection.MismatchedTiles)
            {
                ValidateResolutionResetTarget(tilesRoot, mismatch);
                if (!TryDetectTerrainResolution(mismatch.TilePath, mismatch.RawPath,
                        out TerrainOutputResolution current, out string detail) ||
                    current != mismatch.Resolution)
                {
                    throw new InvalidOperationException(
                        $"terrain tile {mismatch.TileName} changed after Scan inspection ({detail})");
                }

                byte[] tileBytes = File.ReadAllBytes(mismatch.TilePath);
                PatchTerrainResolutionMetadata(tileBytes, targetSamples, targetSpacing);

                string token = $".scolidex-resolution-{Guid.NewGuid():N}";
                string stagedTile = mismatch.TilePath + token + ".tmp";
                string stagedRaw = mismatch.RawPath + token + ".tmp";
                File.WriteAllBytes(stagedTile, tileBytes);
                File.WriteAllBytes(stagedRaw, emptyRaw);
                staged.Add(new StagedTerrainResolutionTile(mismatch, stagedTile, stagedRaw));
            }

            foreach (StagedTerrainResolutionTile item in staged)
            {
                promoted.Add(PromoteTerrainResolutionTile(item));
            }

            foreach (PromotedTerrainResolutionTile item in promoted)
            {
                TryDeleteFile(item.TileBackupPath);
                TryDeleteFile(item.RawBackupPath);
            }

            return staged.Count;
        }
        catch
        {
            foreach (PromotedTerrainResolutionTile item in promoted.AsEnumerable().Reverse())
            {
                RestoreTerrainResolutionFile(item.Tile.TilePath, item.TileBackupPath);
                RestoreTerrainResolutionFile(item.Tile.RawPath, item.RawBackupPath);
            }

            throw;
        }
        finally
        {
            foreach (StagedTerrainResolutionTile item in staged)
            {
                TryDeleteFile(item.StagedTilePath);
                TryDeleteFile(item.StagedRawPath);
            }

            foreach (PromotedTerrainResolutionTile item in promoted)
            {
                TryDeleteFile(item.TileBackupPath);
                TryDeleteFile(item.RawBackupPath);
            }
        }
    }

    private static bool VerifyUniformTerrainResolution(
        string routeDir,
        TerrainOutputResolution expectedResolution)
    {
        TerrainResolutionInspection inspection = InspectTerrainResolutions(
            routeDir, expectedResolution);
        if (inspection.MismatchedTiles.Count == 0 &&
            inspection.UnrecognizedTiles.Count == 0)
        {
            Console.WriteLine(
                $"Terrain resolution validation: {inspection.TotalTiles:N0} / " +
                $"{inspection.TotalTiles:N0} tile(s) are {TerrainOutputLabel(expectedResolution)}.");
            return true;
        }

        Console.WriteLine(
            $"Error: terrain resolution validation found " +
            $"{inspection.MismatchedTiles.Count:N0} mismatch(es) and " +
            $"{inspection.UnrecognizedTiles.Count:N0} unrecognized tile(s).");
        return false;
    }

    internal static void RunTerrainResolutionProbe()
    {
        string probeRoot = Path.Combine(
            Path.GetTempPath(), $"SCOLIDEX-terrain-resolution-{Guid.NewGuid():N}");
        try
        {
            string tilesDir = Path.Combine(probeRoot, "tiles");
            string worldDir = Path.Combine(probeRoot, "world");
            Directory.CreateDirectory(tilesDir);
            Directory.CreateDirectory(worldDir);
            File.WriteAllText(
                Path.Combine(probeRoot, Path.GetFileName(probeRoot) + ".trk"),
                "SIMISA@@@@@@@@@@JINX0r1t______\r\n\r\nTr_RouteFile (\r\n)\r\n");

            FileInfo[] templates = EnumerateGeneratedTileTemplateDirectories()
                .SelectMany(directory => directory.EnumerateFiles("-*.t"))
                .Where(file => RouteLayout.TryDecodeTileName(file.Name, out _))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .DistinctBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            if (templates.Length != 2)
            {
                throw new InvalidOperationException(
                    "terrain resolution probe requires two generated normal terrain templates");
            }

            for (int index = 0; index < templates.Length; index++)
            {
                FileInfo template = templates[index];
                string tilePath = Path.Combine(tilesDir, template.Name);
                string baseName = Path.GetFileNameWithoutExtension(template.Name);
                string rawPath = Path.Combine(tilesDir, baseName + "_y.raw");
                byte[] tileBytes = File.ReadAllBytes(template.FullName);
                int gridSize = index == 0 ? OrtsRawGridSize : ExperimentalRawGridSize;
                float spacing = index == 0
                    ? (float)OrtsPostSpacingMeters
                    : (float)ExperimentalPostSpacingMeters;
                PatchTerrainResolutionMetadata(tileBytes, gridSize, spacing);
                File.WriteAllBytes(tilePath, tileBytes);
                File.WriteAllBytes(rawPath, CreateEmptyRawGridBytes(gridSize));

                RouteLayout.TryDecodeTileName(template.Name, out TileCoordinate coordinate);
                File.WriteAllText(
                    Path.Combine(worldDir, WorldFileName(coordinate.X, coordinate.Z)),
                    "SIMISA@@@@@@@@@@JINX0w0t______\r\n\r\nTr_Worldfile (\r\n)\r\n");
            }

            TerrainResolutionInspection eightMetre = InspectTerrainResolutions(
                probeRoot, TerrainOutputResolution.Normal8m);
            if (eightMetre.MatchingTiles != 1 ||
                eightMetre.MismatchedTiles.Count != 1 ||
                eightMetre.UnrecognizedTiles.Count != 0)
            {
                throw new InvalidOperationException(
                    $"mixed 8m/4m inspection reported matching={eightMetre.MatchingTiles}, " +
                    $"mismatched={eightMetre.MismatchedTiles.Count}, " +
                    $"unrecognized={eightMetre.UnrecognizedTiles.Count}: " +
                    string.Join("; ", eightMetre.UnrecognizedTiles.Select(
                        tile => $"{tile.TileName}={tile.Detail}")));
            }

            ResetTerrainResolutionMismatches(probeRoot, eightMetre);
            TerrainResolutionInspection allEight = InspectTerrainResolutions(
                probeRoot, TerrainOutputResolution.Normal8m);
            if (allEight.MatchingTiles != 2 || allEight.MismatchedTiles.Count != 0 ||
                allEight.UnrecognizedTiles.Count != 0)
            {
                throw new InvalidOperationException(
                    "4m-to-8m forced conversion did not produce a uniform route");
            }

            TerrainResolutionInspection fourMetre = InspectTerrainResolutions(
                probeRoot, TerrainOutputResolution.HdTest4m);
            ResetTerrainResolutionMismatches(probeRoot, fourMetre);
            TerrainResolutionInspection allFour = InspectTerrainResolutions(
                probeRoot, TerrainOutputResolution.HdTest4m);
            if (allFour.MatchingTiles != 2 || allFour.MismatchedTiles.Count != 0 ||
                allFour.UnrecognizedTiles.Count != 0 ||
                Directory.EnumerateFiles(tilesDir, "*.scolidex-*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new InvalidOperationException(
                    "8m-to-4m forced conversion did not produce a clean uniform route");
            }

            Console.WriteLine("Terrain resolution probe: PASSED");
            Console.WriteLine("  mixed 8m/4m route detected");
            Console.WriteLine("  4m-to-8m forced conversion verified");
            Console.WriteLine("  8m-to-4m forced conversion verified");
            Console.WriteLine("  temporary stage/backup cleanup verified");
        }
        finally
        {
            if (Directory.Exists(probeRoot))
            {
                Directory.Delete(probeRoot, recursive: true);
            }
        }
    }

    private static void ValidateResolutionResetTarget(
        string tilesRoot,
        TerrainResolutionTile tile)
    {
        string expectedPrefix = tilesRoot.TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string tilePath = Path.GetFullPath(tile.TilePath);
        string rawPath = Path.GetFullPath(tile.RawPath);
        string expectedRaw = Path.Combine(
            Path.GetDirectoryName(tilePath) ?? "",
            Path.GetFileNameWithoutExtension(tilePath) + "_y.raw");
        if (!tilePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !rawPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(tilePath), ".t", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(rawPath, expectedRaw, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(tilePath) || !File.Exists(rawPath))
        {
            throw new InvalidOperationException(
                $"refusing an unsafe terrain resolution reset target: {tile.TileName}");
        }
    }

    private static PromotedTerrainResolutionTile PromoteTerrainResolutionTile(
        StagedTerrainResolutionTile staged)
    {
        string token = $".scolidex-backup-{Guid.NewGuid():N}.tmp";
        string tileBackup = staged.Tile.TilePath + token;
        string rawBackup = staged.Tile.RawPath + token;
        try
        {
            File.Move(staged.Tile.TilePath, tileBackup);
            File.Move(staged.Tile.RawPath, rawBackup);
            File.Move(staged.StagedTilePath, staged.Tile.TilePath);
            File.Move(staged.StagedRawPath, staged.Tile.RawPath);
            return new PromotedTerrainResolutionTile(
                staged.Tile, tileBackup, rawBackup);
        }
        catch
        {
            RestoreTerrainResolutionFile(staged.Tile.TilePath, tileBackup);
            RestoreTerrainResolutionFile(staged.Tile.RawPath, rawBackup);
            throw;
        }
    }

    private static void RestoreTerrainResolutionFile(
        string destinationPath,
        string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            return;
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        File.Move(backupPath, destinationPath);
    }

    private static bool TryDetectTerrainResolution(
        string tilePath,
        string rawPath,
        out TerrainOutputResolution resolution,
        out string detail)
    {
        resolution = TerrainOutputResolution.Normal8m;
        if (!File.Exists(rawPath))
        {
            detail = "missing _y.raw height grid";
            return false;
        }

        long rawLength;
        try
        {
            rawLength = new FileInfo(rawPath).Length;
        }
        catch (Exception ex)
        {
            detail = $"unreadable _y.raw height grid: {ex.Message}";
            return false;
        }

        TerrainOutputResolution? rawResolution = rawLength switch
        {
            (long)OrtsRawGridSize * OrtsRawGridSize * sizeof(short) =>
                TerrainOutputResolution.Normal8m,
            (long)ExperimentalRawGridSize * ExperimentalRawGridSize * sizeof(short) =>
                TerrainOutputResolution.HdTest4m,
            _ => null,
        };
        if (rawResolution is null)
        {
            detail = $"unexpected _y.raw size {rawLength:N0} bytes";
            return false;
        }

        TerrainOutputResolution? metadataResolution =
            TryReadTerrainResolutionMetadata(tilePath, out int samples, out float spacing)
                ? ResolutionFromMetadata(samples, spacing)
                : null;
        if (metadataResolution is not null && metadataResolution != rawResolution)
        {
            detail =
                $"inconsistent .t metadata ({samples} samples at {spacing:0.###}m) " +
                $"and _y.raw size ({rawLength:N0} bytes)";
            return false;
        }

        resolution = rawResolution.Value;
        detail = TerrainOutputLabel(resolution);
        return true;
    }

    private static bool TryReadTerrainResolutionMetadata(
        string tilePath,
        out int samples,
        out float spacing)
    {
        samples = 0;
        spacing = 0;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(tilePath);
        }
        catch
        {
            return false;
        }

        if (bytes.Length < 32 ||
            Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 8)) != "SIMISA@@")
        {
            return false;
        }

        bool foundSamples = false;
        bool foundSpacing = false;
        int detectedSamples = 0;
        float detectedSpacing = 0;
        WalkBinaryTokens(bytes, 32, bytes.Length, 0, (token, payload, blockEnd) =>
        {
            if (token == TokenTerrainNSamples && payload + sizeof(int) <= blockEnd)
            {
                detectedSamples = BitConverter.ToInt32(bytes, payload);
                foundSamples = true;
            }
            else if (token == TokenTerrainSampleSize && payload + sizeof(float) <= blockEnd)
            {
                detectedSpacing = BitConverter.ToSingle(bytes, payload);
                foundSpacing = true;
            }
        });
        samples = detectedSamples;
        spacing = detectedSpacing;
        return foundSamples && foundSpacing;
    }

    private static TerrainOutputResolution? ResolutionFromMetadata(int samples, float spacing)
    {
        if (samples == OrtsRawGridSize && Math.Abs(spacing - OrtsPostSpacingMeters) < 0.01f)
        {
            return TerrainOutputResolution.Normal8m;
        }

        if (samples == ExperimentalRawGridSize &&
            Math.Abs(spacing - ExperimentalPostSpacingMeters) < 0.01f)
        {
            return TerrainOutputResolution.HdTest4m;
        }

        return null;
    }

    private static int TerrainGridSize(TerrainOutputResolution resolution) =>
        resolution == TerrainOutputResolution.HdTest4m
            ? ExperimentalRawGridSize
            : OrtsRawGridSize;

    private static float TerrainPostSpacing(TerrainOutputResolution resolution) =>
        resolution == TerrainOutputResolution.HdTest4m
            ? (float)ExperimentalPostSpacingMeters
            : (float)OrtsPostSpacingMeters;

    private static byte[] CreateEmptyRawGridBytes(int gridSize)
    {
        byte[] bytes = new byte[gridSize * gridSize * sizeof(short)];
        for (int offset = 0; offset < bytes.Length; offset += sizeof(short))
        {
            BitConverter.TryWriteBytes(
                bytes.AsSpan(offset, sizeof(short)), RawMissingHeight);
        }

        return bytes;
    }

    private static void PatchTerrainResolutionMetadata(
        byte[] tileBytes,
        int samples,
        float spacing)
    {
        bool patchedCount = TryPatchBinaryTokenInt(
            tileBytes, TokenTerrainNSamples, samples);
        bool patchedSize = TryPatchBinaryTokenFloat(
            tileBytes, TokenTerrainSampleSize, spacing);
        if (!patchedCount || !patchedSize)
        {
            throw new InvalidOperationException(
                "terrain .t sample-count/sample-size tokens could not be patched safely");
        }
    }

    private sealed record StagedTerrainResolutionTile(
        TerrainResolutionTile Tile,
        string StagedTilePath,
        string StagedRawPath);

    private sealed record PromotedTerrainResolutionTile(
        TerrainResolutionTile Tile,
        string TileBackupPath,
        string RawBackupPath);
}
