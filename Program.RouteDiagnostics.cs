// SCO LIDEX - recoverable tile failures and route-file diagnostics.
// Copyright (C) Scott Brunner, Beast of Burden
// Licensed under GNU GPL v3 or later. See LICENSE.txt.
namespace ORterr;

internal static partial class Program
{
    private static int operationTileExceptions;
    internal static bool HadTileExceptions => Volatile.Read(ref operationTileExceptions) > 0;
    private static void RecordTileException() => Interlocked.Increment(ref operationTileExceptions);
    private static void WriteOperationCompletion()
    {
        WriteLogDetail("Completion", HadTileExceptions
            ? $"Completed with {operationTileExceptions} tile exception(s); review skipped/failed filenames above."
            : "Completed without tile exceptions.");
        Console.WriteLine(HadTileExceptions ? "STATUS: OPERATION COMPLETE - TILE EXCEPTIONS" : "STATUS: OPERATION COMPLETE");
    }

    private static bool IsOperationWideFailure(Exception exception) =>
        exception is OperationCanceledException or OutOfMemoryException or UnauthorizedAccessException ||
        (exception is IOException && exception is not InvalidDataException and not FileNotFoundException and not EndOfStreamException);

    private static IReadOnlyList<TerrainTile> FilterReadableTerrainTiles(RouteLayout route,
        IReadOnlyList<TerrainTile> tiles, TerrainOutputResolution resolution)
    {
        var inspection = InspectTerrainResolutions(route.RouteDir, resolution);
        var issues = inspection.UnrecognizedTiles.ToDictionary(t => t.TileName, t => t.Detail,
            StringComparer.OrdinalIgnoreCase);
        List<TerrainTile> usable = [];
        foreach (TerrainTile tile in tiles)
        {
            string name = Path.GetFileNameWithoutExtension(tile.TileFile.Name);
            string? reason = issues.GetValueOrDefault(name);
            if (tile.WorldTile is null) reason = "tile has no decodable world position";
            if (name != name.ToLowerInvariant()) reason = "unsupported uppercase terrain filename";
            if (reason is null) usable.Add(tile);
            else
            {
                RecordTileException();
                WriteLogDetail("Tile exception - skipped", $"{tile.TileFile.FullName}: {reason}; existing files preserved");
            }
        }
        WriteLogDetail("Terrain tile eligibility", $"{usable.Count} usable; {tiles.Count - usable.Count} tile exception(s) skipped");
        return usable;
    }

    private sealed record RouteFileFinding(string Path, string Reason, WorldTile? Position);

    private static List<RouteFileFinding> InspectRouteFileDiagnostics(RouteLayout route)
    {
        List<RouteFileFinding> findings = [];
        var terrainPositions = route.TerrainTiles.Where(t => t.WorldTile is not null)
            .Select(t => new ScanLocation(t.WorldTile!.X, t.WorldTile.Z)).ToArray();
        var isolated = FindIsolatedScanLocations(terrainPositions.Concat(
            route.WorldTiles.Select(w => new ScanLocation(w.X, w.Z))).Distinct())
            .Select(p => (p.X, p.Z)).ToHashSet();
        foreach (TerrainTile tile in route.TerrainTiles)
        {
            if (tile.TileFile.Length == 0)
                findings.Add(new(tile.TileFile.FullName, "Zero-byte terrain file; terrain preflight determines whether this blocks the selected output.", tile.WorldTile));
            if (tile.WorldTile is WorldTile world && isolated.Contains((world.X, world.Z)))
                findings.Add(new(tile.TileFile.FullName, "Geographically isolated terrain tile; remains included when selected, can expand DEM and map coverage.", world));
        }
        var knownWorldPaths = route.WorldTiles.Select(w => w.File.FullName).Concat(route.SkippedOriginFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(Path.Combine(route.RouteDir, "world"), "*.w"))
            if (!knownWorldPaths.Contains(path))
                findings.Add(new(path, "World filename was not recognized by the route loader; not included in its world coverage.", null));
        var terrainSet = terrainPositions.Select(p => (p.X, p.Z)).ToHashSet();
        foreach (WorldTile world in route.WorldTiles)
        {
            List<string> reasons = [];
            if (world.File.Length == 0) reasons.Add("zero-byte world file");
            if (world.X == 0 && world.Z == 0) reasons.Add("world coordinate X=0, Z=0 (origin; verify whether intentional)");
            if (isolated.Contains((world.X, world.Z))) reasons.Add("geographically isolated world location");
            if (reasons.Count == 0) continue;
            reasons.Add(terrainSet.Contains((world.X, world.Z)) ? "matching terrain tile present" : "no matching normal terrain tile");
            reasons.Add("recognized filename remains included in map coverage; file contents are not used to decide whether it is empty of objects");
            findings.Add(new(world.File.FullName, string.Join("; ", reasons), world));
        }
        return findings;
    }

    private static bool WriteRouteFileDiagnostics(RouteLayout route, GeoTileMapper mapper)
    {
        var findings = InspectRouteFileDiagnostics(route);
        WriteLogSection("Route File Diagnostics");
        WriteLogDetail("Findings", findings.Count.ToString());
        foreach (RouteFileFinding finding in findings)
        {
            WriteLogDetail("File", finding.Path);
            WriteLogDetail("Reason / effect", finding.Reason, 4);
            if (finding.Position is WorldTile world) WriteDiagnosticPosition(mapper, world);
        }
        WriteLogDetail("Policy", "These diagnostics do not remove or silently exclude files. An isolated location is a warning, not proof of corruption. Null/origin exclusions are listed in Route Summary.");
        return findings.Count > 0;
    }

    private static void WriteDiagnosticPosition(GeoTileMapper mapper, WorldTile world)
    {
        try
        {
            var box = mapper.GetBoundingBox(world);
            WriteLogDetail("Position", FormattableString.Invariant(
                $"X={world.X}, Z={world.Z}; lon {box.MinLon:F6}..{box.MaxLon:F6}; lat {box.MinLat:F6}..{box.MaxLat:F6}"), 4);
        }
        catch (Exception ex) { WriteLogDetail("Position unavailable", ex.Message, 4); }
    }

    private static void WriteMapCoverageFailureDiagnostics(RouteLayout route, GeoTileMapper mapper,
        IReadOnlyList<TerrainTile> selectedTiles)
    {
        WriteLogSection("Map Coverage Failure Inputs");
        var inputs = selectedTiles.Where(t => t.WorldTile is not null)
            .Select(t => (Path: t.TileFile.FullName, World: t.WorldTile!))
            .Concat(route.WorldTiles.Select(w => (Path: w.File.FullName, World: w))).ToArray();
        var positioned = inputs.Select(input => (input.Path, input.World, Box: mapper.GetBoundingBox(input.World))).ToArray();
        if (positioned.Length == 0) return;
        foreach (var input in new[] {
            positioned.MinBy(p => p.Box.MinLon), positioned.MaxBy(p => p.Box.MaxLon),
            positioned.MinBy(p => p.Box.MinLat), positioned.MaxBy(p => p.Box.MaxLat) }.DistinctBy(p => p.Path))
        {
            WriteLogDetail("Geographic boundary file", input.Path);
            WriteDiagnosticPosition(mapper, input.World);
        }
        WriteLogDetail("Interpretation", "These files define the outer bounds; they are candidates for review, not necessarily faulty. See Route File Diagnostics for isolated/origin files.");
    }
}
