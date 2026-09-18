// SCO LIDEX - cross-section PolyVeg contract and failure-recovery probes.
// Copyright (C) Scott Brunner, Beast of Burden
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

using System.Security.Cryptography;
using System.Text.Json;
using OSGeo.OGR;

namespace ORterr;

internal static partial class Program
{
    private sealed partial class RoutePolyVegGeodataBuilder
    {
        private static void RunSectionBoundaryProbe(RouteLayout route, GeoTileMapper mapper, string sourcePath)
        {
            string osmDirectory = GetRouteOsmDirectory(route.RouteDir);
            string output = Path.Combine(osmDirectory, "polyveg-polygons.geojson");
            using (RoutePolyVegGeodataBuilder builder = new(route, mapper, sourcePath, CancellationToken.None))
            {
                // Exercise the exact export/reparse gate that precedes output and counts.
                using (Geometry empty = new(wkbGeometryType.wkbPolygon))
                    if (PreparePolyVegGeometryJson(empty, builder.toProjected) is not null)
                        throw new InvalidDataException("Empty PolyVeg geometry was accepted");
                using (Geometry collapsed = Ogr.CreateGeometryFromJson("{\"type\":\"Polygon\",\"coordinates\":[[[-75,41],[-75,41],[-75,41],[-75,41]]]}"))
                    if (PreparePolyVegGeometryJson(collapsed, builder.toProjected) is not null)
                        throw new InvalidDataException("Collapsed PolyVeg geometry was accepted");
                Console.WriteLine(FormatSystemDiagnostics());
                Console.WriteLine(FormatProcessMemoryDiagnostics());
                using Geometry footprint = builder.exactCoverageProjected.Buffer(-20, 4)!;
                Envelope bounds = new();
                footprint.GetEnvelope(bounds);
                double midX = (bounds.MinX + bounds.MaxX) / 2;
                double midY = (bounds.MinY + bounds.MaxY) / 2;
                using Geometry upper = ProbeRectangle(midX - 400, midY - 600, midX + 400, midY + 600);
                using Geometry mask1 = ProbeRectangle(midX - 200, midY - 30, midX + 200, midY + 30);
                using Geometry mask2 = ProbeRectangle(midX - 100, midY - 40, midX + 300, midY + 20);
                PolyVegSource lowerSource = new("relation/low", "woodland", "natural=wood", 20,
                    141, 196, 108, new(), footprint.Clone());
                PolyVegSource upperSource = new("relation/high", "parkland", "leisure=park", 40,
                    206, 246, 202, new(), upper.Clone());
                builder.StageSource(lowerSource);
                builder.StageSource(upperSource);
                builder.AddValidatedExclusionParts(mask1);
                builder.AddValidatedExclusionParts(mask2);
                builder.StageExclusion(new("building/1", "building", mask1.Clone()));
                builder.StageExclusion(new("building/2", "building", mask2.Clone()));
                builder.WriteAndPromote();

                Dictionary<string, Geometry> actual = new();
                try
                {
                    ReadGeoJsonFeatures(output, feature =>
                    {
                        Geometry geometry = Ogr.CreateGeometryFromJson(feature.GetProperty("geometry").GetRawText());
                        geometry.Transform(builder.toProjected);
                        actual.Add(feature.GetProperty("id").GetString()!, geometry);
                    });
                    if (actual.Count != 2 || !actual.ContainsKey("relation/low") || !actual.ContainsKey("relation/high"))
                        throw new InvalidDataException("Section fragments lost or duplicated their logical source identity");
                    using Geometry overlap = actual["relation/low"].Intersection(actual["relation/high"]);
                    using Geometry rawMask = mask1.Union(mask2);
                    if (overlap.GetArea() > GeometryAreaToleranceSquareMetres)
                        throw new InvalidDataException("PolyVeg overlap survived across a section boundary");
                    foreach (Geometry geometry in actual.Values)
                    {
                        using Geometry conflict = geometry.Intersection(rawMask);
                        if (conflict.GetArea() > GeometryAreaToleranceSquareMetres)
                            throw new InvalidDataException("Permanent exclusion survived across a section boundary");
                    }

                    // Independent whole-area reference: the small fixture permits
                    // a global overlay to detect gaps or ordering changes at seams.
                    using Geometry normalizedMask = NormalizePolygonalOverlay(rawMask, TerrainOverlayPrecisionMetres)!;
                    using Geometry expandedMask = normalizedMask.Buffer(TerrainVegetationSeparationMetres, 4)!;
                    using Geometry separated = NormalizePolygonalOverlay(expandedMask, TerrainOverlayPrecisionMetres)!;
                    using Geometry normalizedUpper = NormalizePolygonalOverlay(upper, TerrainOverlayPrecisionMetres)!;
                    using Geometry expectedUpper = normalizedUpper.Difference(separated);
                    using Geometry upperBuffer = expectedUpper.Buffer(TerrainVegetationSeparationMetres, 4)!;
                    using Geometry upperClearance = NormalizePolygonalOverlay(upperBuffer, TerrainOverlayPrecisionMetres)!;
                    using Geometry normalizedLower = NormalizePolygonalOverlay(footprint, TerrainOverlayPrecisionMetres)!;
                    using Geometry lowerWithoutMasks = normalizedLower.Difference(separated);
                    using Geometry expectedLower = lowerWithoutMasks.Difference(upperClearance);
                    foreach (var pair in new[] { ("relation/low", expectedLower), ("relation/high", expectedUpper) })
                    {
                        using Geometry missing = pair.Item2.Difference(actual[pair.Item1]);
                        using Geometry extra = actual[pair.Item1].Difference(pair.Item2);
                        double error = missing.GetArea() + extra.GetArea();
                        if (error > 2.0)
                            throw new InvalidDataException($"Section/global reference differs by {error:F3} square metres for {pair.Item1}");
                    }
                }
                finally { foreach (Geometry geometry in actual.Values) geometry.Dispose(); }
            }

            string[] outputs = [output, Path.Combine(osmDirectory, "polyveg-exclusions.geojson"),
                Path.Combine(osmDirectory, "route-geodata.gpkg"), Path.Combine(osmDirectory, "route-geodata.json")];
            byte[][] before = outputs.Select(path => SHA256.HashData(File.ReadAllBytes(path))).ToArray();
            using (RoutePolyVegGeodataBuilder broken = new(route, mapper, sourcePath, CancellationToken.None))
            {
                using Geometry geometry = broken.BuildProjectedTileCoverage(broken.coverageTiles[0]);
                // Agriculture is expressly forbidden as a permanent mask.
                broken.StageExclusion(new("bad", "agriculture", geometry.Clone()));
                try
                {
                    broken.WriteAndPromote();
                    throw new InvalidOperationException("Invalid output was promoted");
                }
                catch (InvalidDataException) { }
            }
            using (CancellationTokenSource stop = new())
            {
                using RoutePolyVegGeodataBuilder cancelled = new(route, mapper, sourcePath, stop.Token);
                stop.Cancel();
                try { cancelled.WriteAndPromote(); throw new InvalidDataException("Cancellation was ignored"); }
                catch (OperationCanceledException) { }
            }
            for (int i = 0; i < outputs.Length; i++)
                if (!before[i].SequenceEqual(SHA256.HashData(File.ReadAllBytes(outputs[i]))))
                    throw new InvalidDataException("Failed/cancelled generation modified a previously promoted output");
            if (Directory.GetFiles(osmDirectory, "*.tmp*").Length != 0)
                throw new InvalidDataException("Failed generation left temporary output files");
            Console.WriteLine("PolyVeg section probe: PASSED (source assembly, crossing masks, draw order, global-reference agreement, failure/cancellation preservation).");
        }
    }
}
