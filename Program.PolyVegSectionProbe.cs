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
            RunSectionConflictRecoveryProbe(route, mapper, sourcePath);
            string osmDirectory = GetRouteOsmDirectory(route.RouteDir);
            string output = Path.Combine(osmDirectory, "polyveg-polygons.geojson");
            using TileErrorSession retryErrors = new(route.RouteDir, append: true);
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
                if (retryErrors.HasStage("PolyVeg"))
                    throw new InvalidDataException("Successful validated promotion did not clear resolved PolyVeg tile errors");

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

        private static void RunSectionConflictRecoveryProbe(RouteLayout route, GeoTileMapper mapper, string sourcePath)
        {
            using TileErrorSession errors = new(route.RouteDir, append: true);
            using RoutePolyVegGeodataBuilder builder = new(route, mapper, sourcePath, CancellationToken.None);
            using Geometry core = builder.BuildProjectedTileCoverage(builder.coverageTiles[0]);
            PolyVegSource rejected = new("way/conflict", "woodland", "natural=wood", 20,
                141, 196, 108, new(), core, core.GetArea());
            TextWriter previous = Console.Out;
            using StringWriter diagnostics = new(System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                Console.SetOut(diagnostics);
                if (builder.TryStagePolyVegSection(0, 100, rejected, core, core, core, null,
                        new(core, core, core, core)) ||
                    builder.TryStagePolyVegSection(0, 100, rejected, core, core, null, core,
                        new(core, core, null, core)))
                    throw new InvalidDataException("Conflicting section piece was accepted");
            }
            finally { Console.SetOut(previous); }
            string log = diagnostics.ToString();
            if (builder.conflictingSectionPiecesSkippedCount != 2 || builder.stage.Count("pieces") != 0 ||
                !new[] { "permanent exclusion conflict", "visible-layer overlap", "way/conflict",
                    "category=woodland", "style=natural=wood", "Conflict area=", "Conflict bounds",
                    "world file=", "OSM working source=" }.All(log.Contains))
                throw new InvalidDataException("Section conflict omission or diagnostics are incomplete");
            if (errors.Support?.Samples.Count != 2) throw new InvalidDataException("Conflict reproduction samples were not captured");
            foreach (string samplePath in errors.Support.Samples)
            {
                using var sample = System.IO.Compression.ZipFile.OpenRead(samplePath);
                byte[] ReadBytes(string name)
                {
                    using Stream input = sample.GetEntry(name)!.Open();
                    using MemoryStream bytes = new();
                    input.CopyTo(bytes);
                    return bytes.ToArray();
                }
                using JsonDocument metadata = JsonDocument.Parse(ReadBytes("metadata.json"));
                using Geometry candidate = Geometry.CreateFromWkb(ReadBytes("candidate.wkb"));
                using Geometry mask = Geometry.CreateFromWkb(ReadBytes(sample.GetEntry("raw-exclusions.wkb") is null ? "claimed.wkb" : "raw-exclusions.wkb"));
                using Geometry conflict = candidate.Intersection(mask);
                if (metadata.RootElement.GetProperty("crs").GetString() != $"EPSG:{builder.projectedEpsg}" ||
                    Math.Abs(conflict.GetArea() - metadata.RootElement.GetProperty("conflictAreaSquareMetres").GetDouble()) > 0.000001 ||
                    sample.GetEntry("local-source.wkb") is null || sample.GetEntry("without-exclusions.wkb") is null)
                    throw new InvalidDataException("Saved geometry could not reproduce the detected conflict");
            }

            // A later valid source must still assemble, validate and promote.
            // Neither rejected candidate was staged or reserved as claimed land.
            builder.StageSource(rejected with { SourceId = "way/valid", ProjectedGeometry = core.Clone() });
            builder.WriteAndPromote();
            string osmDirectory = GetRouteOsmDirectory(route.RouteDir);
            using JsonDocument output = JsonDocument.Parse(File.ReadAllText(Path.Combine(osmDirectory, "polyveg-polygons.geojson")));
            JsonElement features = output.RootElement.GetProperty("features");
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(osmDirectory, "route-geodata.json")));
            if (features.GetArrayLength() != 1 ||
                features[0].GetProperty("properties").GetProperty("sourceId").GetString() != "way/valid" ||
                manifest.RootElement.GetProperty("diagnostics").GetProperty("conflictingSectionPiecesSkipped").GetInt32() != 2)
                throw new InvalidDataException("Valid output or conflict summary was lost after section conflicts");
            if (!errors.Has(NormalTileErrorName(builder.coverageTiles[0].X, builder.coverageTiles[0].Z), "PolyVeg"))
                throw new InvalidDataException("Conflicting tile was not persisted in the error file");
            using (TileErrorSession retry = new(route.RouteDir, append: true, readOnly: true))
                if (GetRouteTileProcessingList(route, false, false, false, true, 0).Count != 1)
                    throw new InvalidDataException("Error-only Append did not select the actual failed terrain tile");
            Console.WriteLine("PolyVeg conflict recovery probe: PASSED (both conflicts omitted, detailed warnings, later valid output promoted, manifest counts).");
        }
    }
}
