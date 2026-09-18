// SCO LIDEX - isolated coverage and multi-location Scan regression probe.
// Copyright (C) Scott Brunner, Beast of Burden
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

namespace ORterr;

internal static partial class Program
{
    private static async Task RunScanCoverageProbeAsync(string[] args)
    {
        List<ScanLocation> terrain = [];
        int fileOption = Array.IndexOf(args, "--tile-list");
        if (fileOption >= 0)
        {
            if (fileOption + 1 >= args.Length) throw new ArgumentException("--tile-list needs a filename");
            foreach (string line in File.ReadLines(args[fileOption + 1]))
                if (line.Trim().EndsWith(".t", StringComparison.OrdinalIgnoreCase) &&
                    RouteLayout.TryDecodeTileName(line.Trim(), out TileCoordinate tile))
                    terrain.Add(new(tile.X, tile.Z));
        }
        else
        {
            for (int z = 0; z < 40; z++)
                for (int x = 0; x < 100; x++) terrain.Add(new(-5800 + x, 14900 + z));
            terrain.Add(new(-2953, 12045));
        }
        var isolated = FindIsolatedScanLocations(terrain);
        var selected = SelectScanLocations(terrain);
        if (isolated.Count != 1 || isolated[0].Name != "-1843a864.t" ||
            selected.Count != 5 || selected[0] == isolated[0] || !selected.Contains(isolated[0]) ||
            selected.Distinct().Count() != selected.Count ||
            !selected.SequenceEqual(SelectScanLocations(terrain.AsEnumerable().Reverse())))
            throw new InvalidDataException("Isolated-tile detection or deterministic sample selection failed");
        var mountainCoverage = new HashSet<LoTileCoordinate>();
        foreach (ScanLocation tile in terrain) AddLoTileCoverage(mountainCoverage, tile.X, tile.Z, 1);
        var mountainSamples = SelectScanLocations(mountainCoverage.Select(p => new ScanLocation(p.X, p.Z, true)));
        if (mountainSamples.Count != 5 || mountainSamples.Any(p => !p.Distant) ||
            !mountainSamples.Any(p => p.X > -4000) || mountainSamples[0].X > -4000)
            throw new InvalidDataException("DM-only sampling lost main or remote coverage");

        ScanLocationAvailability available = new(new(true, 1, "test"), new(true, 2, "test"),
            new(true, 3, "test"), new(true, true, "test"));
        ScanLocationAvailability uncovered = new(new(true, 0, "no coverage"), new(true, 0, "no coverage"),
            new(true, 0, "no coverage"), new(true, false, "ocean"));
        // First sample failure and later sample failure must both retain any
        // usable source found elsewhere. Use the actual selection/aggregation
        // path with deterministic services, never a live network dependency.
        foreach (int badIndex in new[] { 0, selected.Count - 1 })
        {
            int calls = 0;
            var result = await ProbeScanLocationsAsync(selected,
                _ => Task.FromResult(calls++ == badIndex ? uncovered : available), true, CancellationToken.None);
            if (calls != selected.Count || !result.Sources.Policy.UsePrimary ||
                !result.Sources.Policy.UseGlobal || !result.HasGaps ||
                !result.Sources.Global.Detail.Contains("4/5", StringComparison.Ordinal))
                throw new InvalidDataException("An uncovered sample erased valid source coverage");
        }
        foreach (var missing in new[] { uncovered, ScanLocationAvailability.Empty })
        {
            var result = await ProbeScanLocationsAsync(selected, _ => Task.FromResult(missing), true, CancellationToken.None);
            if (result.Sources.Policy.HasAny || !result.HasGaps)
                throw new InvalidDataException("Scan enabled a source when every sampled location failed");
        }
        int dmCalls = 0;
        var dmResult = await ProbeScanLocationsAsync(mountainSamples, _ => Task.FromResult(
            ScanLocationAvailability.Empty with { Global = dmCalls++ == 0 ? uncovered.Global : available.Global }),
            false, CancellationToken.None);
        if (!dmResult.Sources.Policy.UseGlobal || dmResult.Sources.Policy.UsePrimary || !dmResult.HasGaps)
            throw new InvalidDataException("DM-only coverage aggregation failed");

        if (SelectScanLocations([]).Count != 0 || SelectScanLocations([terrain[0], terrain[0]]).Count != 1 ||
            FindIsolatedScanLocations([terrain[0], isolated[0]]).Count != 0)
            throw new InvalidDataException("Empty/small/multiple-main-group handling failed");
        var longRoute = Enumerable.Range(0, 2000).Select(x => new ScanLocation(x, 0)).ToArray();
        if (FindIsolatedScanLocations(longRoute).Count != 0)
            throw new InvalidDataException("A continuous long route was incorrectly flagged as isolated");
        var emptyResult = await ProbeScanLocationsAsync([], _ => throw new InvalidOperationException("No samples expected"),
            true, CancellationToken.None);
        if (emptyResult.Sources.Policy.HasAny) throw new InvalidDataException("Empty coverage enabled Run");
        var usOnly = await ProbeScanLocationsAsync(selected,
            _ => Task.FromResult(available with { Global = uncovered.Global }), true, CancellationToken.None);
        if (!usOnly.Sources.Policy.HasAny || usOnly.Sources.Policy.UseGlobal)
            throw new InvalidDataException("A missing global source incorrectly disabled USGS terrain coverage");
        using CancellationTokenSource stop = new();
        try
        {
            await ProbeScanLocationsAsync(selected, _ => { stop.Cancel(); return Task.FromResult(available); }, true, stop.Token);
            throw new InvalidDataException("Scan ignored cancellation during a source check");
        }
        catch (OperationCanceledException) { }
        // Exercise the real HTTP source-check catch clauses without networking.
        GeoSampleGrid grid = new(new double[,] { { 7.0 } }, new double[,] { { 51.0 } }, (7.0, 51.0, 7.01, 51.01));
        foreach (bool global in new[] { false, true })
        {
            using CancellationTokenSource requestStop = new();
            using HttpClient client = new(new ScanCancelledRequestHandler(requestStop));
            try
            {
                if (global) await TestCopernicusDatasetAsync(client, grid, requestStop.Token);
                else await TestUsgsDatasetAsync(client, grid, PrimaryDemDataset, requestStop.Token);
                throw new InvalidDataException("Source HTTP cancellation was swallowed as unavailable coverage");
            }
            catch (OperationCanceledException) { }
        }
        Console.WriteLine($"Scan coverage probe: PASSED ({terrain.Count:N0} terrain tiles; outlier {isolated[0].Name}; main-first and spread-out samples).");
        Console.WriteLine("  mixed coverage, all-uncovered, service outage, DM-only, empty/small selections, deterministic ordering, and cancellation verified");
        Console.WriteLine("  tile inventory is inspected only; no route files are modified");
    }

    private sealed class ScanCancelledRequestHandler(CancellationTokenSource stop) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            stop.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(stop.Token);
        }
    }
}
