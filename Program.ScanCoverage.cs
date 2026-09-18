// SCO LIDEX - representative source coverage and isolated-tile diagnostics.
// Copyright (C) Scott Brunner, Beast of Burden
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

namespace ORterr;

internal static partial class Program
{
    private const int ScanLocationLimit = 5;
    private sealed record ScanLocation(int X, int Z, bool Distant = false)
    {
        internal string Name => (Distant ? LoTileNameFromTileXZ(X, Z) : RouteLayout.TileNameFromTileXZ(X, Z)) + ".t";
    }

    // Coarse connectivity deliberately tolerates gaps in route coverage. This
    // is a diagnostic heuristic, never an instruction to omit/delete tiles.
    private static List<List<ScanLocation>> GroupScanLocations(IEnumerable<ScanLocation> locations)
    {
        const int cellSpan = 64; // 131 km; adjacent occupied cells form a group.
        var cells = locations.Distinct().GroupBy(p =>
            (X: (int)Math.Floor(p.X / (double)cellSpan), Z: (int)Math.Floor(p.Z / (double)cellSpan)))
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Z).ThenBy(p => p.X).ToList());
        List<List<ScanLocation>> groups = [];
        while (cells.Count > 0)
        {
            var start = cells.Keys.First();
            Queue<(int X, int Z)> pending = new();
            pending.Enqueue(start);
            List<ScanLocation> group = [];
            while (pending.TryDequeue(out var cell))
            {
                if (!cells.Remove(cell, out var members)) continue;
                group.AddRange(members);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (cells.ContainsKey((cell.X + dx, cell.Z + dz))) pending.Enqueue((cell.X + dx, cell.Z + dz));
            }
            groups.Add(group.OrderBy(p => p.Z).ThenBy(p => p.X).ToList());
        }
        return groups.OrderByDescending(g => g.Count).ThenBy(g => g[0].Z).ThenBy(g => g[0].X).ToList();
    }

    private static List<ScanLocation> SelectScanLocations(IEnumerable<ScanLocation> locations)
    {
        var groups = GroupScanLocations(locations);
        if (groups.Count == 0) return [];
        var main = groups[0];
        double centerX = main.Average(p => (double)p.X), centerZ = main.Average(p => (double)p.Z);
        List<ScanLocation> selected = [main.OrderBy(p =>
            Math.Pow(p.X - centerX, 2) + Math.Pow(p.Z - centerZ, 2)).ThenBy(p => p.Z).ThenBy(p => p.X).First()];
        var remaining = groups.SelectMany(g => g).Where(p => p != selected[0]).ToList();
        // A main-group sample comes first. Farthest-point sampling then includes
        // remote groups and different parts of long routes without scanning all
        // source tiles or allowing filename ordering to choose the sole sample.
        while (selected.Count < ScanLocationLimit && remaining.Count > 0)
        {
            ScanLocation next = remaining.OrderByDescending(p => selected.Min(s =>
                Math.Pow((double)p.X - s.X, 2) + Math.Pow((double)p.Z - s.Z, 2)))
                .ThenBy(p => p.Z).ThenBy(p => p.X).First();
            selected.Add(next);
            remaining.Remove(next);
        }
        return selected;
    }

    private static List<ScanLocation> FindIsolatedScanLocations(IEnumerable<ScanLocation> locations)
    {
        var groups = GroupScanLocations(locations);
        int total = groups.Sum(g => g.Count);
        if (groups.Count < 2 || groups[0].Count < total * 0.75) return [];
        int minX = groups[0].Min(p => p.X), maxX = groups[0].Max(p => p.X);
        int minZ = groups[0].Min(p => p.Z), maxZ = groups[0].Max(p => p.Z);
        return groups.Skip(1).Where(g =>
        {
            double dx = Math.Max(0, Math.Max(minX - g.Max(p => p.X), g.Min(p => p.X) - maxX));
            double dz = Math.Max(0, Math.Max(minZ - g.Max(p => p.Z), g.Min(p => p.Z) - maxZ));
            return g.Count <= total * 0.10 && dx * dx + dz * dz >= 128.0 * 128.0;
        }).SelectMany(g => g).ToList();
    }

    private static bool WarnAboutIsolatedRouteTiles(RouteLayout route, GeoTileMapper mapper)
    {
        var isolated = FindIsolatedScanLocations(route.TerrainTiles.Where(t => t.WorldTile is not null)
            .Select(t => new ScanLocation(t.WorldTile!.X, t.WorldTile.Z)));
        if (isolated.Count == 0) return false;
        WriteLogSection("Isolated Route Tiles");
        WriteLogDetail("Warning", $"{isolated.Count:N0} terrain tile(s) lie in small groups far from the main route coverage. Check whether they are intentional.");
        foreach (ScanLocation tile in isolated.Take(20))
        {
            WorldTile world = new(tile.X, tile.Z, new FileInfo(Path.Combine(route.RouteDir, tile.Name)));
            var bounds = mapper.GetBoundingBox(world);
            WriteLogDetail(tile.Name, FormattableString.Invariant(
                $"X={tile.X}, Z={tile.Z} | lon {(bounds.MinLon + bounds.MaxLon) / 2:F5}, lat {(bounds.MinLat + bounds.MaxLat) / 2:F5}"));
        }
        if (isolated.Count > 20) WriteLogDetail("Additional isolated tiles", $"{isolated.Count - 20:N0}");
        WriteLogDetail("Action", "Review these tiles in a test copy. Scan does not remove them; selected isolated tiles remain included in Run and may fail where elevation data is absent.");
        return true;
    }

    private sealed record ScanLocationAvailability(UsgsDatasetAvailability Primary,
        UsgsDatasetAvailability Intermediate, UsgsDatasetAvailability Fallback, SourceAvailability Global)
    {
        internal static ScanLocationAvailability Empty => new(new(false, 0, "not tested"),
            new(false, 0, "not tested"), new(false, 0, "not tested"), new(false, false, "not tested"));
        internal DemSourcePolicy Policy => new(Primary.ServiceAvailable && Primary.ItemCount > 0,
            Intermediate.ServiceAvailable && Intermediate.ItemCount > 0,
            Fallback.ServiceAvailable && Fallback.ItemCount > 0, Global.ServiceAvailable && Global.CoverageAvailable);
    }

    private sealed record SampledScanAvailability(ScanLocationAvailability Sources, bool HasGaps)
    {
        internal static SampledScanAvailability Empty => new(ScanLocationAvailability.Empty, false);
    }

    private static UsgsDatasetAvailability MergeUsgsScanSamples(IEnumerable<UsgsDatasetAvailability> samples)
    {
        var all = samples.ToArray();
        int covered = all.Count(s => s.ServiceAvailable && s.ItemCount > 0);
        return new(all.Any(s => s.ServiceAvailable), all.Select(s => s.ItemCount).DefaultIfEmpty().Max(),
            $"Coverage found at {covered}/{all.Length} sampled locations; other tiles are checked during Run.");
    }

    private static SourceAvailability MergeGlobalScanSamples(IEnumerable<SourceAvailability> samples)
    {
        var all = samples.ToArray();
        int covered = all.Count(s => s.ServiceAvailable && s.CoverageAvailable);
        return new(all.Any(s => s.ServiceAvailable), covered > 0,
            $"Coverage found at {covered}/{all.Length} sampled locations; other tiles are checked during Run");
    }

    private static async Task<SampledScanAvailability> ProbeScanLocationsAsync(
        IReadOnlyList<ScanLocation> locations,
        Func<ScanLocation, Task<ScanLocationAvailability>> probe,
        bool includeUsgs,
        CancellationToken cancellationToken)
    {
        List<ScanLocationAvailability> results = [];
        foreach (ScanLocation location in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteLogDetail($"Location {results.Count + 1}/{locations.Count}", location.Name);
            var result = await probe(location);
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(result);
            if (!(includeUsgs ? result.Policy.HasAny : result.Policy.UseGlobal))
                WriteLogDetail("Warning", $"No usable elevation source found at {location.Name}; continuing with the other locations.");
        }
        if (results.Count == 0) return SampledScanAvailability.Empty;
        return new(new(MergeUsgsScanSamples(results.Select(s => s.Primary)),
            MergeUsgsScanSamples(results.Select(s => s.Intermediate)),
            MergeUsgsScanSamples(results.Select(s => s.Fallback)),
            MergeGlobalScanSamples(results.Select(s => s.Global))),
            results.Any(s => !s.Policy.UseGlobal || (includeUsgs &&
                (!s.Policy.UsePrimary || !s.Policy.UseIntermediate || !s.Policy.UseFallback))));
    }

    private static Task<SampledScanAvailability> CheckScanLocationsAsync(
        HttpClient client, GeoTileMapper mapper, IReadOnlyList<ScanLocation> locations,
        bool includeUsgs, CancellationToken cancellationToken) =>
        ProbeScanLocationsAsync(locations, async location =>
        {
            GeoSampleGrid grid = location.Distant
                ? mapper.GetAreaSampleGrid(location.X + ((LoTileNormalTileSpan - 1) / 2.0),
                    location.Z + ((LoTileNormalTileSpan - 1) / 2.0), LoTileSizeMeters, LoTileSizeMeters, LoRawGridSize)
                : mapper.GetSampleGrid(new WorldTile(location.X, location.Z, new FileInfo(location.Name)));
            Task<SourceAvailability> global = TestCopernicusDatasetAsync(client, grid, cancellationToken);
            if (!includeUsgs)
                return ScanLocationAvailability.Empty with { Global = await global };
            var primary = TestUsgsDatasetAsync(client, grid, PrimaryDemDataset, cancellationToken);
            var intermediate = TestUsgsDatasetAsync(client, grid, IntermediateDemDataset, cancellationToken);
            var fallback = TestUsgsDatasetAsync(client, grid, FallbackDemDataset, cancellationToken);
            await Task.WhenAll(global, primary, intermediate, fallback);
            return new(await primary, await intermediate, await fallback, await global);
        }, includeUsgs, cancellationToken);
}
