// SCO LIDEX - regional OSM selection regression probe.
// Copyright (C) Scott Brunner, Beast of Burden
// Licensed under GNU GPL v3 or later. See LICENSE.txt.
using System.Text.Json;

namespace ORterr;

internal static partial class Program
{
    private static void RunGeofabrikSelectionProbe(string[] args)
    {
        object Box(string id, double west, double south, double east, double north) => new
        {
            properties = new { id, name = id, urls = new { pbf = $"https://example.invalid/{id}.osm.pbf" } },
            geometry = new
            {
                type = "Polygon",
                coordinates = new[] { new[] {
                new[] { west, south }, new[] { east, south }, new[] { east, north },
                new[] { west, north }, new[] { west, south } } }
            }
        };
        using JsonDocument index = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            features = new[] {
            Box("north-america", -170, 10, -40, 85),
            Box("us", -130, 20, -60, 45),
            Box("new-england", -73, 42, -69, 45),
            Box("quebec", -80, 44.9, -57, 63) }
        }));
        var points = new (double Lon, double Lat)[] { (-72.1, 43.6), (-70.2, 44.8), (-71.5, 45.047) };
        string[] selected = SelectGeofabrikRegions(index.RootElement, points).Select(region => region.Id).Order().ToArray();
        if (!selected.SequenceEqual(new[] { "new-england", "quebec" }))
            throw new InvalidDataException("Border route did not select both regional sources.");
        if (SelectGeofabrikRegions(index.RootElement, points.Take(2).ToArray()).Single().Id != "new-england")
            throw new InvalidDataException("Single-region route selected unnecessary sources.");
        bool rejected = false;
        try { SelectGeofabrikRegions(index.RootElement, [(-100, 50)]); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new InvalidDataException("Uncovered point silently selected a continental source.");

        int fileArg = Array.IndexOf(args, "--index-file");
        if (fileArg >= 0 && fileArg + 1 < args.Length)
        {
            using JsonDocument realIndex = JsonDocument.Parse(File.ReadAllText(args[fileArg + 1]));
            // A conservative envelope grid from Shawn's log, not his unavailable tile inventory.
            List<(double Lon, double Lat)> envelopePoints = [];
            for (int y = 0; y <= 20; y++)
                for (int x = 0; x <= 20; x++)
                    envelopePoints.Add((-72.165940 + (1.988644 * x / 20), 43.523481 + (1.523851 * y / 20)));
            var regions = SelectGeofabrikRegions(realIndex.RootElement, envelopePoints);
            Console.WriteLine("Shawn envelope grid: " + string.Join(" + ", regions.Select(region => region.Name)));
            if (regions.Any(region => region.Id == "north-america"))
                throw new InvalidDataException("Shawn envelope still selected North America.");
        }
        Console.WriteLine("Geofabrik selection probe: PASSED (border coverage, single region, continental fallback refusal).");
    }
}
