// SCO LIDEX - route OSM geodata and final PolyVeg surfaces for TSRE GenX.
// Copyright (C) Scott Brunner, Beast of Burden
// Part of the SCO LIDEX Terrain Builder application.
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OSGeo.OGR;
using OSGeo.OSR;

namespace ORterr;

internal static partial class Program
{
    private static void RunRouteOsmLifecycleProbe()
    {
        string probeRoot = Path.Combine(Path.GetTempPath(), "SCOLIDEX-route-osm-lifecycle-" + Guid.NewGuid().ToString("N"));
        string routeDirectory = Path.Combine(probeRoot, "Routes", "ProbeRoute");
        string settingsDirectory = Path.Combine(probeRoot, "settings");
        Directory.CreateDirectory(routeDirectory);
        AppContext.SetData("SCOLIDEX.MapCacheSettingsRoot", settingsDirectory);
        try
        {
            string pbfPath = GetRouteGeofabrikExtractPath(routeDirectory, "probe-region");
            Directory.CreateDirectory(Path.GetDirectoryName(pbfPath)!);
            File.WriteAllBytes(pbfPath, [1, 2, 3, 4]);
            GeofabrikRegion region = new(
                "probe-region", "Probe Region", "https://example.invalid/probe.osm.pbf",
                4, DateTimeOffset.UtcNow, -80, 35, -70, 45);
            WriteRouteOsmManifest(routeDirectory, region, pbfPath);

            string derivativePath = Path.Combine(GetRouteOsmDirectory(routeDirectory), "polyveg-polygons.geojson");
            string exclusionsPath = Path.Combine(GetRouteOsmDirectory(routeDirectory), "polyveg-exclusions.geojson");
            string geopackagePath = Path.Combine(GetRouteOsmDirectory(routeDirectory), "route-geodata.gpkg");
            string geodataManifestPath = Path.Combine(GetRouteOsmDirectory(routeDirectory), "route-geodata.json");
            const string fingerprint = "PROBE-COVERAGE";
            FileInfo pbf = new(pbfPath);
            File.WriteAllText(
                derivativePath,
                JsonSerializer.Serialize(new
                {
                    type = "FeatureCollection",
                    schemaVersion = 2,
                    generatorRevision = 5,
                    source = new
                    {
                        pbfPath = pbf.FullName,
                        pbfSizeBytes = pbf.Length,
                        pbfModifiedUtc = pbf.LastWriteTimeUtc,
                    },
                    routeCoverage = new { terrainTileFingerprint = fingerprint },
                    features = Array.Empty<object>(),
                }));
            File.Copy(derivativePath, exclusionsPath);

            if (!RoutePolyVegGeodataBuilder.IsCurrentForProbe(derivativePath, pbfPath, fingerprint) ||
                !RoutePolyVegGeodataBuilder.IsCurrentForProbe(exclusionsPath, pbfPath, fingerprint))
            {
                throw new InvalidOperationException("matching route derivative was not recognized as current");
            }

            File.AppendAllText(pbfPath, "fresh");
            if (RoutePolyVegGeodataBuilder.IsCurrentForProbe(derivativePath, pbfPath, fingerprint) ||
                RoutePolyVegGeodataBuilder.IsCurrentForProbe(exclusionsPath, pbfPath, fingerprint))
            {
                throw new InvalidOperationException("changed bulk source did not invalidate the route derivative");
            }

            WriteRouteOsmManifest(routeDirectory, region with { SizeBytes = new FileInfo(pbfPath).Length }, pbfPath);
            RoutePolyVegGeodataBuilder.WriteGeoPackageDriverProbe(geopackagePath);
            File.WriteAllText(geodataManifestPath, "{}");
            IReadOnlyList<MapCacheEntry> caches = GetKnownMapCaches(routeDirectory);
            if (caches.Count != 1 || caches.SelectMany(cache => cache.Files).Any(path =>
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(derivativePath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(exclusionsPath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(geopackagePath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(geodataManifestPath), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("exit cache discovery included route derivative data");
            }

            PurgeMapCaches(caches);
            if (File.Exists(pbfPath) || File.Exists(GetRouteOsmManifestPath(routeDirectory)) ||
                !File.Exists(derivativePath) || !File.Exists(exclusionsPath) ||
                !File.Exists(geopackagePath) || !File.Exists(geodataManifestPath))
            {
                throw new InvalidOperationException("bulk purge did not preserve the route derivative");
            }

            RunMapPolygonHoleProbe();
            RoutePolyVegGeodataBuilder.RunExtractionProbe(Path.Combine(probeRoot, "extraction"));

            Console.WriteLine("Route OSM lifecycle probe: PASSED");
            Console.WriteLine("  changed/new bulk source invalidates the route derivative");
            Console.WriteLine("  exit purge includes only PBF bulk data and preserves route derivatives");
            Console.WriteLine("  GeoPackage, manifest, PolyVeg surfaces, and exclusions validate atomically");
            Console.WriteLine("  map polygons preserve inner island rings as unpainted holes");
        }
        finally
        {
            if (Directory.Exists(probeRoot))
            {
                Directory.Delete(probeRoot, true);
            }
        }
    }

    private sealed class RoutePolyVegGeodataBuilder : IDisposable
    {
        private const int SchemaVersion = 2;
        private const int GeneratorRevision = 5;
        private const double CoverageBufferMetres = 2048.0;
        private const double GeometryAreaToleranceSquareMetres = 0.01;
        private const double OverlayPrecisionMetres = 0.01;
        private const double TerrainVegetationSeparationMetres = 0.3048;
        private const double TerrainOverlayPrecisionMetres = 0.10;

        private static readonly HashSet<string> ExcludedLanduse = new(StringComparer.OrdinalIgnoreCase)
        {
            "reservoir", "basin", "residential", "commercial", "retail", "industrial",
            "construction", "brownfield", "greenfield", "landfill", "quarry",
            "garages", "military", "railway",
        };

        private static readonly HashSet<string> ExcludedAmenity = new(StringComparer.OrdinalIgnoreCase)
        {
            "parking", "school", "college", "university", "hospital",
        };

        private sealed record PolyVegSource(
            string SourceId,
            string Category,
            string StyleId,
            int DrawOrder,
            int FillRed,
            int FillGreen,
            int FillBlue,
            Dictionary<string, string> Properties,
            Geometry ProjectedGeometry);

        private sealed record RouteVectorFeature(
            string LayerName,
            Dictionary<string, string> Properties,
            Geometry Geometry);

        private sealed record PolyVegExclusion(
            string Id,
            string Kind,
            Geometry ProjectedGeometry);

        private static readonly string[] RouteLayerNames =
        [
            "habitat_woodland", "habitat_scrub", "habitat_heath", "habitat_grassland",
            "habitat_wetland", "water_polygons", "waterways", "buildings",
            "developed_land", "agriculture", "orchard", "parkland", "golf_course",
            "cemetery", "sports", "zoo", "roads", "railways", "bare_ground",
        ];

        private static readonly string[] PolyVegCategoryNames =
        [
            "woodland", "scrub", "heath", "grassland", "wetland", "agriculture",
            "orchard", "parkland", "golf_course", "cemetery", "sports", "zoo",
        ];

        private static readonly string[] PreservedFields =
        [
            "osm_type", "osm_id", "osm_way_id", "name", "natural", "landuse", "water", "waterway",
            "wetland", "leaf_type", "leaf_cycle", "wood", "species", "building", "amenity",
            "leisure", "aeroway", "military", "man_made", "highway", "railway", "service",
            "surface", "tracktype", "bridge", "tunnel", "layer", "width", "lanes", "gauge",
            "tourism", "other_tags",
        ];

        private readonly string routeDirectory;
        private readonly string pbfPath;
        private readonly string outputPath;
        private readonly string coverageFingerprint;
        private readonly IReadOnlyList<WorldTile> coverageTiles;
        private readonly GeoTileMapper mapper;
        private readonly Geometry exactCoverageGeographic;
        private readonly Geometry exactCoverageProjected;
        private readonly Geometry bufferedCoverageGeographic;
        private readonly CoordinateTransformation toProjected;
        private readonly CoordinateTransformation toGeographic;
        private readonly Geometry exclusionPolygons = new(wkbGeometryType.wkbMultiPolygon);
        private readonly List<PolyVegSource> polyVegSources = [];
        private readonly List<RouteVectorFeature> routeVectorFeatures = [];
        private readonly List<PolyVegExclusion> polyVegExclusions = [];
        private readonly CancellationToken cancellationToken;
        private bool promoted;
        private int polyVegSourceCount;
        private int exclusionPolygonFeatureCount;
        private int exclusionLineFeatureCount;
        private int invalidGeometryRepairedCount;
        private int invalidGeometrySkippedCount;

        private RoutePolyVegGeodataBuilder(
            RouteLayout route,
            GeoTileMapper mapper,
            string sourcePbfPath,
            CancellationToken token)
        {
            routeDirectory = route.RouteDir;
            this.mapper = mapper;
            pbfPath = Path.GetFullPath(sourcePbfPath);
            outputPath = Path.Combine(GetRouteOsmDirectory(route.RouteDir), "polyveg-polygons.geojson");
            coverageTiles = GetRouteCoverageTiles(route);
            coverageFingerprint = GetRouteCoverageFingerprint(route);
            cancellationToken = token;

            exactCoverageGeographic = BuildRouteCoverage(mapper, coverageTiles);
            Envelope envelope = new();
            exactCoverageGeographic.GetEnvelope(envelope);

            using SpatialReference geographic = CreateSpatialReference(4326);
            int utmZone = Math.Clamp((int)Math.Floor((((envelope.MinX + envelope.MaxX) * 0.5) + 180.0) / 6.0) + 1, 1, 60);
            int projectedEpsg = ((envelope.MinY + envelope.MaxY) * 0.5) >= 0.0 ? 32600 + utmZone : 32700 + utmZone;
            using SpatialReference projected = CreateSpatialReference(projectedEpsg);
            toProjected = new CoordinateTransformation(geographic, projected);
            toGeographic = new CoordinateTransformation(projected, geographic);

            exactCoverageProjected = exactCoverageGeographic.Clone();
            exactCoverageProjected.Transform(toProjected);
            bufferedCoverageGeographic = exactCoverageProjected.Buffer(CoverageBufferMetres, 8)
                ?? throw new InvalidOperationException("GDAL could not buffer route OSM coverage");
            bufferedCoverageGeographic.Transform(toGeographic);
            bufferedCoverageGeographic.GetEnvelope(envelope);
            MinLongitude = envelope.MinX;
            MinLatitude = envelope.MinY;
            MaxLongitude = envelope.MaxX;
            MaxLatitude = envelope.MaxY;
        }

        internal double MinLongitude { get; }

        internal double MinLatitude { get; }

        internal double MaxLongitude { get; }

        internal double MaxLatitude { get; }

        internal static RoutePolyVegGeodataBuilder? TryCreate(
            RouteLayout route,
            GeoTileMapper mapper,
            string pbfPath,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<WorldTile> coverageTiles = GetRouteCoverageTiles(route);
            if (coverageTiles.Count == 0)
            {
                WriteOsmLogEntry("PolyVeg skipped: route has no decodable terrain-tile coverage.");
                return null;
            }

            string outputPath = Path.Combine(GetRouteOsmDirectory(route.RouteDir), "polyveg-polygons.geojson");
            string exclusionsPath = Path.Combine(GetRouteOsmDirectory(route.RouteDir), "polyveg-exclusions.geojson");
            string geopackagePath = Path.Combine(GetRouteOsmDirectory(route.RouteDir), "route-geodata.gpkg");
            string manifestPath = Path.Combine(GetRouteOsmDirectory(route.RouteDir), "route-geodata.json");
            string fingerprint = GetRouteCoverageFingerprint(route);
            if (!forceRefresh &&
                IsPolyVegDerivativeCurrent(outputPath, pbfPath, fingerprint) &&
                IsPolyVegDerivativeCurrent(exclusionsPath, pbfPath, fingerprint) &&
                IsRouteGeodataManifestCurrent(manifestPath, geopackagePath, pbfPath, fingerprint))
            {
                WriteOsmLogEntry("PolyVeg contract is current; rebuild not required.");
                return null;
            }

            WriteOsmLogEntry(forceRefresh
                ? "Fresh bulk OSM download detected; rebuilding the route OSM derivative."
                : "Route OSM derivative is missing or stale; rebuilding it for the active route.");
            WriteOsmLogEntry($"PolyVeg output: {outputPath}");
            WriteOsmLogEntry(
                $"Route coverage: {coverageTiles.Count:N0} terrain tiles; " +
                $"{route.WorldTiles.Count:N0} object-bearing world files.");
            return new RoutePolyVegGeodataBuilder(route, mapper, pbfPath, cancellationToken);
        }

        internal void Collect(Feature feature, Geometry geometry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string layerName = feature.GetDefnRef().GetName();
            if (string.Equals(layerName, "points", StringComparison.OrdinalIgnoreCase)) return;
            Envelope envelope = new();
            geometry.GetEnvelope(envelope);
            if (envelope.MaxX < MinLongitude || envelope.MinX > MaxLongitude ||
                envelope.MaxY < MinLatitude || envelope.MinY > MaxLatitude ||
                !geometry.Intersects(bufferedCoverageGeographic))
            {
                return;
            }

            bool valid = geometry.IsValid();
            using Geometry? repairedGeometry = valid ? null : geometry.MakeValid([]);
            if (repairedGeometry is not null && !repairedGeometry.IsEmpty())
            {
                invalidGeometryRepairedCount++;
            }
            else if (!valid)
            {
                invalidGeometrySkippedCount++;
                return;
            }
            Geometry sourceGeometry = repairedGeometry ?? geometry;
            if (string.Equals(layerName, "multipolygons", StringComparison.OrdinalIgnoreCase))
            {
                PolyVegClassification? category = GetPolyVegClassification(feature);
                string? exclusionKind = PermanentPolygonExclusionKind(feature);
                if (category is null && exclusionKind is null)
                {
                    return;
                }

                if (category is not null)
                {
                    using Geometry? clipped = sourceGeometry.Intersection(exactCoverageGeographic);
                    if (clipped is not null && !clipped.IsEmpty())
                    {
                        clipped.Transform(toProjected);
                        Geometry? normalized = ExtractGeometryFamily(clipped, polygons: true);
                        if (normalized is not null && normalized.GetArea() > GeometryAreaToleranceSquareMetres)
                        {
                            polyVegSources.Add(new PolyVegSource(
                                StablePolygonSourceId(feature),
                                category.Category,
                                category.StyleId,
                                category.DrawOrder,
                                category.FillRed,
                                category.FillGreen,
                                category.FillBlue,
                                PolyVegProperties(feature),
                                normalized));
                            polyVegSourceCount++;
                        }
                    }
                }

                CollectPolygonLayers(feature, sourceGeometry);

                if (exclusionKind is not null)
                {
                    using Geometry? clipped = sourceGeometry.Intersection(bufferedCoverageGeographic);
                    if (clipped is not null && !clipped.IsEmpty())
                    {
                        clipped.Transform(toProjected);
                        if (AddValidatedExclusionParts(clipped))
                            exclusionPolygonFeatureCount++;
                    }

                    using Geometry? routeClipped = sourceGeometry.Intersection(exactCoverageGeographic);
                    if (routeClipped is not null && !routeClipped.IsEmpty())
                    {
                        routeClipped.Transform(toProjected);
                        Geometry? normalized = ExtractGeometryFamily(routeClipped, polygons: true);
                        if (normalized is not null)
                        {
                            polyVegExclusions.Add(new PolyVegExclusion(
                                ExclusionId(feature, exclusionKind!), exclusionKind!, normalized));
                        }
                    }
                }
            }
            else if (string.Equals(layerName, "lines", StringComparison.OrdinalIgnoreCase))
            {
                CollectTreeRowSource(feature, sourceGeometry);
                CollectLineLayers(feature, sourceGeometry);
                double bufferMetres = ExclusionLineBufferMetres(feature);
                if (bufferMetres <= 0.0)
                {
                    return;
                }

                using Geometry? clipped = sourceGeometry.Intersection(bufferedCoverageGeographic);
                if (clipped is null || clipped.IsEmpty())
                {
                    return;
                }

                clipped.Transform(toProjected);
                using Geometry? buffered = clipped.Buffer(bufferMetres, 4);
                if (buffered is null || buffered.IsEmpty())
                {
                    return;
                }

                if (AddValidatedExclusionParts(buffered))
                    exclusionLineFeatureCount++;

                using Geometry? routeClipped = buffered.Intersection(exactCoverageProjected);
                if (routeClipped is not null && !routeClipped.IsEmpty())
                {
                    Geometry? normalized = ExtractGeometryFamily(routeClipped, polygons: true);
                    string? kind = LineExclusionKind(feature);
                    if (normalized is not null && kind is not null)
                    {
                        polyVegExclusions.Add(new PolyVegExclusion(
                            ExclusionId(feature, kind), kind, normalized));
                    }
                }
            }
        }

        internal void WriteAndPromote()
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteOsmLogSubsection("PERMANENT EXCLUSIONS");
            WriteOsmLogEntry(
                $"Sources: {polyVegSourceCount:N0} PolyVeg; {exclusionPolygonFeatureCount:N0} polygon masks; {exclusionLineFeatureCount:N0} line masks.");
            using Geometry? exclusionMask = BuildPermanentExclusionUnionWithStatus();

            string osmDirectory = GetRouteOsmDirectory(routeDirectory);
            Directory.CreateDirectory(osmDirectory);
            string temporaryPath = outputPath + ".tmp";
            string exclusionsPath = Path.Combine(osmDirectory, "polyveg-exclusions.geojson");
            string temporaryExclusionsPath = exclusionsPath + ".tmp";
            string geopackagePath = Path.Combine(osmDirectory, "route-geodata.gpkg");
            string temporaryGeopackagePath = Path.Combine(osmDirectory, "route-geodata.tmp.gpkg");
            string manifestPath = Path.Combine(osmDirectory, "route-geodata.json");
            string temporaryManifestPath = manifestPath + ".tmp";
            string obsoleteForestPath = Path.Combine(osmDirectory, "forest-polygons.geojson");
            Dictionary<string, int> categoryCounts = PolyVegCategoryNames.ToDictionary(
                category => category, _ => 0, StringComparer.Ordinal);
            int writtenFeatures;
            int writtenParts;
            int writtenHoles;
            try
            {
                WriteOsmLogSubsection("POLYVEG SURFACE BUILD");
                using (ProcessingHeartbeat stage = new("Building final PolyVeg surfaces"))
                {
                    (writtenFeatures, writtenParts, writtenHoles) = WritePolyVegPolygons(
                        temporaryPath, exclusionMask, categoryCounts);
                    stage.Complete();
                }
                WriteOsmLogSubsection("POLYVEG VALIDATION");
                using (ProcessingHeartbeat stage = new("Validating final PolyVeg surfaces"))
                {
                    ValidatePolyVegPolygons(
                        temporaryPath, coverageFingerprint, writtenFeatures, categoryCounts,
                        toProjected);
                    stage.Complete();
                }

                int writtenExclusions;
                WriteOsmLogSubsection("POLYVEG EXCLUSIONS");
                using (ProcessingHeartbeat stage = new("Writing PolyVeg exclusions"))
                {
                    writtenExclusions = WritePolyVegExclusions(temporaryExclusionsPath);
                    stage.Complete();
                }
                using (ProcessingHeartbeat stage = new("Validating PolyVeg exclusions"))
                {
                    ValidatePolyVegExclusions(
                        temporaryExclusionsPath, coverageFingerprint, writtenExclusions);
                    stage.Complete();
                }

                Dictionary<string, int> layerCounts;
                WriteOsmLogSubsection("ROUTE GEODATA HANDOFF");
                using (ProcessingHeartbeat stage = new("Writing categorized route GeoPackage"))
                {
                    layerCounts = WriteGeoPackage(temporaryGeopackagePath);
                    ValidateGeoPackage(temporaryGeopackagePath, layerCounts);
                    stage.Complete();
                }

                using (ProcessingHeartbeat stage = new("Writing manifest and promoting the atomic geodata set"))
                {
                    WriteRouteGeodataManifest(
                        temporaryManifestPath, layerCounts, categoryCounts, writtenFeatures, writtenExclusions);
                    ValidateRouteGeodataManifest(
                        temporaryManifestPath, layerCounts, categoryCounts, writtenFeatures, writtenExclusions);
                    PromoteDerivativeSet(
                        (temporaryPath, outputPath),
                        (temporaryExclusionsPath, exclusionsPath),
                        (temporaryGeopackagePath, geopackagePath),
                        (temporaryManifestPath, manifestPath));
                    RemoveObsoleteDerivative(obsoleteForestPath);
                    stage.Complete();
                }
                promoted = true;
            }
            finally
            {
                foreach (string path in new[]
                {
                    temporaryPath, temporaryExclusionsPath,
                    temporaryGeopackagePath, temporaryManifestPath,
                })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }

            WriteOsmLogSection("POLYVEG RESULTS");
            WriteOsmLogEntry(
                $"Complete: {writtenFeatures:N0} features; {writtenParts:N0} polygon parts; {writtenHoles:N0} holes.");
            WriteOsmLogEntry($"Surfaces: {outputPath}");
            WriteOsmLogEntry($"Exclusions: {exclusionsPath}");
            WriteOsmLogEntry($"GeoPackage: {geopackagePath}");
            WriteOsmLogEntry($"Manifest: {manifestPath}");
        }

        private Geometry? BuildPermanentExclusionUnionWithStatus()
        {
            using ProcessingHeartbeat stage = new(
                $"Combining {exclusionPolygons.GetGeometryCount():N0} permanent exclusion polygons");
            Geometry? result = BuildPermanentExclusionUnion();
            stage.Complete();
            return result;
        }

        private Geometry? BuildPermanentExclusionUnion()
        {
            if (exclusionPolygons.GetGeometryCount() == 0) return null;
            Geometry? union = null;
            try
            {
                union = exclusionPolygons.UnionCascaded();
            }
            catch (Exception firstFailure)
            {
                WriteOsmLogEntry(
                    "Normalizing permanent-exclusion topology: " + firstFailure.Message,
                    indent: 4);
                union = exclusionPolygons.Buffer(0.0, 8);
                if (union is null || union.IsEmpty())
                {
                    union?.Dispose();
                    throw new InvalidDataException(
                        "permanent exclusion topology normalization produced no polygon geometry",
                        firstFailure);
                }
            }

            using (union)
            {
                Geometry? normalized = NormalizePolygonalOverlay(
                    union,
                    TerrainOverlayPrecisionMetres);
                if (normalized is null || normalized.IsEmpty())
                {
                    normalized?.Dispose();
                    throw new InvalidDataException(
                        "permanent exclusion union produced no usable polygon geometry");
                }
                return normalized;
            }
        }

        public void Dispose()
        {
            foreach (PolyVegSource source in polyVegSources)
            {
                source.ProjectedGeometry.Dispose();
            }

            foreach (RouteVectorFeature feature in routeVectorFeatures)
            {
                feature.Geometry.Dispose();
            }

            foreach (PolyVegExclusion exclusion in polyVegExclusions)
            {
                exclusion.ProjectedGeometry.Dispose();
            }

            exclusionPolygons.Dispose();
            exactCoverageGeographic.Dispose();
            exactCoverageProjected.Dispose();
            bufferedCoverageGeographic.Dispose();
            toProjected.Dispose();
            toGeographic.Dispose();
            if (!promoted)
            {
                string temporaryPath = outputPath + ".tmp";
                foreach (string path in new[]
                {
                    temporaryPath,
                    Path.Combine(GetRouteOsmDirectory(routeDirectory), "polyveg-exclusions.geojson.tmp"),
                    Path.Combine(GetRouteOsmDirectory(routeDirectory), "route-geodata.tmp.gpkg"),
                    Path.Combine(GetRouteOsmDirectory(routeDirectory), "route-geodata.json.tmp"),
                })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }
        }

        private void WriteHeader(Utf8JsonWriter writer, double bufferMetres = CoverageBufferMetres)
        {
            FileInfo pbf = new(pbfPath);
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteNumber("generatorRevision", GeneratorRevision);
            writer.WriteStartObject("crs");
            writer.WriteString("type", "name");
            writer.WriteStartObject("properties");
            writer.WriteString("name", "EPSG:4326");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteStartObject("source");
            writer.WriteString("pbfRelativePath", Path.GetRelativePath(
                GetRouteOsmDirectory(routeDirectory), pbf.FullName).Replace('\\', '/'));
            writer.WriteNumber("pbfSizeBytes", pbf.Length);
            writer.WriteString("pbfModifiedUtc", pbf.LastWriteTimeUtc);
            writer.WriteEndObject();
            writer.WriteStartObject("routeCoverage");
            writer.WriteNumber("terrainTileCount", coverageTiles.Count);
            writer.WriteString("terrainTileFingerprint", coverageFingerprint);
            writer.WriteNumber("bufferMetres", bufferMetres);
            writer.WriteString("crs", "EPSG:4326");
            writer.WriteEndObject();
            writer.WriteStartArray("features");
        }

        private (int Features, int Parts, int Holes) WritePolyVegPolygons(
            string path,
            Geometry? exclusionMask,
            IDictionary<string, int> categoryCounts)
        {
            int written = 0;
            int parts = 0;
            int holes = 0;
            PolyVegSource[] orderedSources = polyVegSources
                .OrderByDescending(source => source.DrawOrder)
                .ThenByDescending(source => source.SourceId, StringComparer.Ordinal)
                .ThenByDescending(source => source.Category, StringComparer.Ordinal)
                .ToArray();
            Geometry?[] normalizedSources = new Geometry?[orderedSources.Length];
            Envelope[] sourceEnvelopes = new Envelope[orderedSources.Length];
            Geometry?[] visibleBySource = new Geometry?[orderedSources.Length];
            try
            {
                WriteOsmLogSubsection("SOURCE NORMALIZATION");
                ProcessingCheckpoints normalizationProgress = new(orderedSources.Length);
                for (int sourceIndex = 0; sourceIndex < orderedSources.Length; sourceIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    normalizedSources[sourceIndex] = NormalizePolygonalOverlay(
                        orderedSources[sourceIndex].ProjectedGeometry,
                        TerrainOverlayPrecisionMetres);
                    sourceEnvelopes[sourceIndex] = new Envelope();
                    normalizedSources[sourceIndex]?.GetEnvelope(sourceEnvelopes[sourceIndex]);
                    normalizationProgress.Report(sourceIndex + 1, "PolyVeg sources normalized");
                }

                WriteOsmLogSubsection("TERRAIN-SECTION STACKING");
                WriteOsmLogEntry(
                    $"Processing {coverageTiles.Count:N0} terrain sections with a one-foot overlap halo.",
                    indent: 4);
                ProcessingCheckpoints tileProgress = new(coverageTiles.Count);
                for (int tileIndex = 0; tileIndex < coverageTiles.Count; tileIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using Geometry core = BuildProjectedTileCoverage(coverageTiles[tileIndex]);
                    using Geometry context = core.Buffer(TerrainVegetationSeparationMetres * 2.0, 4)
                        ?? throw new InvalidDataException("could not create terrain-section overlap context");
                    Envelope contextEnvelope = new();
                    context.GetEnvelope(contextEnvelope);
                    Geometry? separatedLocalExclusions = null;
                    Geometry? rawLocalExclusions = null;
                    Geometry? claimedLocal = null;
                    try
                    {
                        if (exclusionMask is not null)
                        {
                            rawLocalExclusions = exclusionMask.Intersection(context);
                            if (rawLocalExclusions is not null && !rawLocalExclusions.IsEmpty())
                            {
                                using Geometry? expanded = rawLocalExclusions.Buffer(
                                    TerrainVegetationSeparationMetres,
                                    4);
                                if (expanded is not null && !expanded.IsEmpty())
                                {
                                    separatedLocalExclusions = NormalizePolygonalOverlay(
                                        expanded,
                                        TerrainOverlayPrecisionMetres);
                                }
                            }
                        }

                        for (int sourceIndex = 0; sourceIndex < orderedSources.Length; sourceIndex++)
                        {
                            Geometry? normalizedSource = normalizedSources[sourceIndex];
                            if (normalizedSource is null || normalizedSource.IsEmpty() ||
                                !EnvelopesIntersect(sourceEnvelopes[sourceIndex], contextEnvelope))
                            {
                                continue;
                            }

                            using Geometry? localSource = normalizedSource.Intersection(context);
                            if (localSource is null || localSource.IsEmpty()) continue;
                            using Geometry? withoutExclusions = separatedLocalExclusions is null
                                ? localSource.Clone()
                                : DifferenceWithTopologyRetry(
                                    localSource,
                                    separatedLocalExclusions,
                                    $"local permanent exclusions for {orderedSources[sourceIndex].SourceId}");
                            if (withoutExclusions is null || withoutExclusions.IsEmpty()) continue;
                            using Geometry? visible = claimedLocal is null || claimedLocal.IsEmpty()
                                ? withoutExclusions.Clone()
                                : DifferenceWithTopologyRetry(
                                    withoutExclusions,
                                    claimedLocal,
                                    $"local visible-layer stacking for {orderedSources[sourceIndex].SourceId}");
                            if (visible is null || visible.IsEmpty() ||
                                visible.GetArea() <= GeometryAreaToleranceSquareMetres) continue;
                            if (claimedLocal is not null)
                            {
                                using Geometry? overlapConflict = visible.Intersection(claimedLocal);
                                if (overlapConflict is not null &&
                                    overlapConflict.GetArea() > GeometryAreaToleranceSquareMetres)
                                {
                                    throw new InvalidDataException(
                                        $"terrain section {coverageTiles[tileIndex].X},{coverageTiles[tileIndex].Z} " +
                                        $"retained a visible-layer overlap for {orderedSources[sourceIndex].SourceId}");
                                }
                            }

                            using Geometry? coreVisible = visible.Intersection(core);
                            if (coreVisible is not null && !coreVisible.IsEmpty() &&
                                coreVisible.GetArea() > GeometryAreaToleranceSquareMetres)
                            {
                                if (rawLocalExclusions is not null)
                                {
                                    using Geometry? exclusionConflict = coreVisible.Intersection(
                                        rawLocalExclusions);
                                    if (exclusionConflict is not null &&
                                        exclusionConflict.GetArea() > GeometryAreaToleranceSquareMetres)
                                    {
                                        throw new InvalidDataException(
                                            $"terrain section {coverageTiles[tileIndex].X},{coverageTiles[tileIndex].Z} " +
                                            $"retained an exclusion conflict for {orderedSources[sourceIndex].SourceId}");
                                    }
                                }
                                visibleBySource[sourceIndex] ??= new Geometry(wkbGeometryType.wkbMultiPolygon);
                                AddPolygonParts(coreVisible, visibleBySource[sourceIndex]!);
                            }

                            using Geometry? expandedVisible = visible.Buffer(
                                TerrainVegetationSeparationMetres,
                                4);
                            if (expandedVisible is null || expandedVisible.IsEmpty())
                                throw new InvalidDataException(
                                    $"could not create local vegetation clearance for {orderedSources[sourceIndex].SourceId}");
                            using Geometry? visibleClearance = NormalizePolygonalOverlay(
                                expandedVisible,
                                TerrainOverlayPrecisionMetres);
                            if (visibleClearance is null || visibleClearance.IsEmpty())
                                throw new InvalidDataException(
                                    $"local vegetation clearance produced no geometry for {orderedSources[sourceIndex].SourceId}");
                            Geometry nextClaimed = claimedLocal is null
                                ? visibleClearance.Clone()
                                : UnionWithTopologyRetry(
                                    claimedLocal,
                                    visibleClearance,
                                    $"local visible-layer clearance for {orderedSources[sourceIndex].SourceId}");
                            claimedLocal?.Dispose();
                            claimedLocal = nextClaimed;
                        }
                    }
                    finally
                    {
                        separatedLocalExclusions?.Dispose();
                        rawLocalExclusions?.Dispose();
                        claimedLocal?.Dispose();
                    }
                    tileProgress.Report(tileIndex + 1, "Terrain sections processed");
                }

                WriteOsmLogSubsection("POLYVEG FEATURE ASSEMBLY");
                ProcessingCheckpoints assemblyProgress = new(orderedSources.Length);
                using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
                WriteHeader(writer, bufferMetres: 0.0);
                for (int sourceIndex = 0; sourceIndex < orderedSources.Length; sourceIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PolyVegSource source = orderedSources[sourceIndex];
                    Geometry? accumulated = visibleBySource[sourceIndex];
                    if (accumulated is null || accumulated.IsEmpty())
                    {
                        assemblyProgress.Report(sourceIndex + 1, "PolyVeg features assembled");
                        continue;
                    }
                    using Geometry? polygonal = AssembleVisiblePolygonParts(accumulated);
                    if (polygonal is null || polygonal.IsEmpty() ||
                        polygonal.GetArea() <= GeometryAreaToleranceSquareMetres)
                    {
                        assemblyProgress.Report(sourceIndex + 1, "PolyVeg features assembled");
                        continue;
                    }

                    double originalArea = source.ProjectedGeometry.GetArea();
                    double plantableArea = polygonal.GetArea();
                    using Geometry geographic = polygonal.Clone();
                    geographic.Transform(toGeographic);
                    CountPolygonParts(geographic, ref parts, ref holes);
                    WritePolyVegFeature(writer, source, geographic, originalArea, plantableArea);
                    categoryCounts[source.Category]++;
                    written++;
                    assemblyProgress.Report(sourceIndex + 1, "PolyVeg features assembled");
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
                stream.Flush(true);
            }
            finally
            {
                foreach (Geometry? geometry in normalizedSources) geometry?.Dispose();
                foreach (Geometry? geometry in visibleBySource) geometry?.Dispose();
            }
            return (written, parts, holes);
        }

        private Geometry BuildProjectedTileCoverage(WorldTile tile)
        {
            GeoSampleGrid corners = mapper.GetAreaSampleGrid(
                tile.X, tile.Z, OrtsTileSizeMeters, OrtsTileSizeMeters, 2);
            using Geometry ring = new(wkbGeometryType.wkbLinearRing);
            ring.AddPoint_2D(corners.Longitudes[1, 0], corners.Latitudes[1, 0]);
            ring.AddPoint_2D(corners.Longitudes[1, 1], corners.Latitudes[1, 1]);
            ring.AddPoint_2D(corners.Longitudes[0, 1], corners.Latitudes[0, 1]);
            ring.AddPoint_2D(corners.Longitudes[0, 0], corners.Latitudes[0, 0]);
            ring.AddPoint_2D(corners.Longitudes[1, 0], corners.Latitudes[1, 0]);
            Geometry polygon = new(wkbGeometryType.wkbPolygon);
            polygon.AddGeometry(ring);
            polygon.Transform(toProjected);
            return polygon;
        }

        private static Geometry? AssembleVisiblePolygonParts(Geometry accumulated)
        {
            Geometry? assembled = accumulated.GetGeometryCount() == 1
                ? accumulated.GetGeometryRef(0).Clone()
                : accumulated.UnionCascaded();
            if (assembled is null || assembled.IsEmpty())
            {
                assembled?.Dispose();
                return null;
            }
            if (assembled.IsValid()) return assembled;

            using (assembled)
            {
                Geometry? repaired = assembled.MakeValid([]);
                if (repaired is null || repaired.IsEmpty())
                {
                    repaired?.Dispose();
                    return null;
                }
                Geometry? polygonal = ExtractGeometryFamily(repaired, polygons: true);
                repaired.Dispose();
                return polygonal;
            }
        }

        private static bool EnvelopesIntersect(Envelope left, Envelope right)
        {
            return left.MaxX >= right.MinX && left.MinX <= right.MaxX &&
                left.MaxY >= right.MinY && left.MinY <= right.MaxY;
        }

        private int WritePolyVegExclusions(string path)
        {
            int written = 0;
            ProcessingCheckpoints progress = new(polyVegExclusions.Count);
            using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true });
            WriteHeader(writer, bufferMetres: 0.0);
            foreach (PolyVegExclusion exclusion in polyVegExclusions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Geometry geometry = exclusion.ProjectedGeometry.Clone();
                geometry.Transform(toGeographic);
                writer.WriteStartObject();
                writer.WriteString("type", "Feature");
                writer.WriteString("id", exclusion.Id);
                writer.WriteStartObject("properties");
                writer.WriteString("kind", exclusion.Kind);
                writer.WriteEndObject();
                writer.WritePropertyName("geometry");
                using JsonDocument geometryJson = JsonDocument.Parse(geometry.ExportToJson(null));
                geometryJson.RootElement.WriteTo(writer);
                writer.WriteEndObject();
                written++;
                progress.Report(written, "PolyVeg exclusions written");
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            stream.Flush(true);
            return written;
        }

        private static void WritePolyVegFeature(
            Utf8JsonWriter writer,
            PolyVegSource source,
            Geometry geometry,
            double originalArea,
            double plantableArea)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "Feature");
            writer.WriteString("id", source.SourceId);

            writer.WriteStartObject("properties");
            writer.WriteString("category", source.Category);
            writer.WriteString("styleId", source.StyleId);
            writer.WriteNumber("drawOrder", source.DrawOrder);
            writer.WriteString("sourceId", source.SourceId);
            writer.WriteString("fillColor", $"#{source.FillRed:X2}{source.FillGreen:X2}{source.FillBlue:X2}");
            writer.WriteStartArray("fillRgb");
            writer.WriteNumberValue(source.FillRed);
            writer.WriteNumberValue(source.FillGreen);
            writer.WriteNumberValue(source.FillBlue);
            writer.WriteEndArray();
            writer.WriteString("natural", source.Properties.GetValueOrDefault("natural", ""));
            writer.WriteString("landuse", source.Properties.GetValueOrDefault("landuse", ""));
            foreach ((string name, string value) in source.Properties)
            {
                if (name is "natural" or "landuse") continue;
                writer.WriteString(name, value);
            }

            writer.WriteNumber("originalAreaSquareMetres", originalArea);
            writer.WriteNumber("plantableAreaSquareMetres", plantableArea);
            writer.WriteNumber("excludedAreaSquareMetres", Math.Max(0.0, originalArea - plantableArea));
            writer.WriteEndObject();
            writer.WritePropertyName("geometry");
            using JsonDocument geometryJson = JsonDocument.Parse(geometry.ExportToJson(null));
            geometryJson.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        private void CollectPolygonLayers(Feature feature, Geometry geometry)
        {
            string natural = NormalizedField(feature, "natural");
            string landuse = NormalizedField(feature, "landuse");
            List<string> layers = [];
            PolyVegClassification? polyVeg = GetPolyVegClassification(feature);
            if (polyVeg is not null)
            {
                layers.Add(polyVeg.Category switch
                {
                    "woodland" => "habitat_woodland",
                    "scrub" => "habitat_scrub",
                    "heath" => "habitat_heath",
                    "grassland" => "habitat_grassland",
                    "wetland" => "habitat_wetland",
                    _ => polyVeg.Category,
                });
            }
            if (natural is "water" or "bay" or "strait" || landuse is "reservoir" or "basin" || !string.IsNullOrEmpty(NormalizedField(feature, "water"))) layers.Add("water_polygons");

            string building = NormalizedField(feature, "building");
            if (!string.IsNullOrEmpty(building) && building is not "no" and not "false") layers.Add("buildings");
            if (IsDevelopedLand(feature)) layers.Add("developed_land");
            if (natural is "beach" or "sand" or "bare_rock" or "scree") layers.Add("bare_ground");
            if (layers.Count == 0)
            {
                return;
            }

            bool exact = layers.Any(layer => layer.StartsWith("habitat_", StringComparison.Ordinal) ||
                PolyVegCategoryNames.Contains(layer, StringComparer.Ordinal) || layer == "bare_ground");
            using Geometry? clippedExact = exact ? geometry.Intersection(exactCoverageGeographic) : null;
            using Geometry? clippedBuffered = layers.Any(layer => !layer.StartsWith("habitat_", StringComparison.Ordinal) &&
                !PolyVegCategoryNames.Contains(layer, StringComparer.Ordinal) && layer != "bare_ground")
                ? geometry.Intersection(bufferedCoverageGeographic)
                : null;
            Dictionary<string, string> properties = RouteProperties(feature, "multipolygon");
            foreach (string layer in layers.Distinct(StringComparer.Ordinal))
            {
                bool useExact = layer.StartsWith("habitat_", StringComparison.Ordinal) ||
                    PolyVegCategoryNames.Contains(layer, StringComparer.Ordinal) || layer == "bare_ground";
                Geometry? clipped = useExact ? clippedExact : clippedBuffered;
                if (clipped is not null && !clipped.IsEmpty())
                {
                    Geometry? normalized = ExtractGeometryFamily(clipped, polygons: true);
                    if (normalized is not null)
                    {
                        routeVectorFeatures.Add(new RouteVectorFeature(layer, properties, normalized));
                    }
                }
            }
        }

        private void CollectLineLayers(Feature feature, Geometry geometry)
        {
            List<string> layers = [];
            if (!string.IsNullOrEmpty(NormalizedField(feature, "waterway"))) layers.Add("waterways");
            if (!string.IsNullOrEmpty(NormalizedField(feature, "highway"))) layers.Add("roads");
            if (!string.IsNullOrEmpty(NormalizedField(feature, "railway"))) layers.Add("railways");
            if (layers.Count == 0)
            {
                return;
            }

            using Geometry? clipped = geometry.Intersection(bufferedCoverageGeographic);
            if (clipped is null || clipped.IsEmpty())
            {
                return;
            }

            Dictionary<string, string> properties = RouteProperties(feature, "way");
            foreach (string layer in layers)
            {
                Geometry? normalized = ExtractGeometryFamily(clipped, polygons: false);
                if (normalized is not null)
                {
                    routeVectorFeatures.Add(new RouteVectorFeature(layer, properties, normalized));
                }
            }
        }

        private void CollectTreeRowSource(Feature feature, Geometry geometry)
        {
            if (!string.Equals(NormalizedField(feature, "natural"), "tree_row", StringComparison.Ordinal))
            {
                return;
            }

            using Geometry? clippedLine = geometry.Intersection(bufferedCoverageGeographic);
            if (clippedLine is null || clippedLine.IsEmpty())
            {
                return;
            }

            clippedLine.Transform(toProjected);
            using Geometry? buffered = clippedLine.Buffer(TreeRowWidthMetres * 0.5, 8);
            if (buffered is null || buffered.IsEmpty())
            {
                return;
            }

            using Geometry? routeClipped = buffered.Intersection(exactCoverageProjected);
            if (routeClipped is null || routeClipped.IsEmpty())
            {
                return;
            }

            Geometry? normalized = NormalizePolygonalOverlay(
                routeClipped,
                TerrainOverlayPrecisionMetres);
            if (normalized is null || normalized.IsEmpty() ||
                normalized.GetArea() <= GeometryAreaToleranceSquareMetres)
            {
                normalized?.Dispose();
                return;
            }

            string sourceId = StableLineSourceId(feature);
            Dictionary<string, string> polyVegProperties = PolyVegProperties(feature, "way");
            polyVegProperties["derivedGeometry"] = "line_buffer";
            polyVegProperties["derivedWidthMetres"] = TreeRowWidthMetres.ToString(
                "0.###", System.Globalization.CultureInfo.InvariantCulture);
            polyVegSources.Add(new PolyVegSource(
                sourceId,
                "woodland",
                "natural=tree_row",
                TsreDrawOrder(6),
                NaturalWoodRed,
                NaturalWoodGreen,
                NaturalWoodBlue,
                polyVegProperties,
                normalized));
            polyVegSourceCount++;

            Geometry geographic = normalized.Clone();
            geographic.Transform(toGeographic);
            routeVectorFeatures.Add(new RouteVectorFeature(
                "habitat_woodland",
                RouteProperties(feature, "way"),
                geographic));
        }

        private Dictionary<string, int> WriteGeoPackage(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            OSGeo.OGR.Driver driver = Ogr.GetDriverByName("GPKG")
                ?? throw new InvalidOperationException("bundled GDAL GeoPackage driver is unavailable");
            Dictionary<string, int> counts = RouteLayerNames.ToDictionary(name => name, _ => 0, StringComparer.Ordinal);
            using SpatialReference geographic = CreateSpatialReference(4326);
            using DataSource dataSource = driver.CreateDataSource(path, [])
                ?? throw new InvalidOperationException("GDAL could not create the route GeoPackage");
            Dictionary<string, Layer> layers = new(StringComparer.Ordinal);
            ProcessingCheckpoints writtenProgress = new(routeVectorFeatures.Count);
            int totalWritten = 0;
            try
            {
                foreach (string layerName in RouteLayerNames)
                {
                    bool lineLayer = layerName is "waterways" or "roads" or "railways";
                    Layer layer = dataSource.CreateLayer(
                        layerName,
                        geographic,
                        lineLayer ? wkbGeometryType.wkbMultiLineString : wkbGeometryType.wkbMultiPolygon,
                        ["SPATIAL_INDEX=YES"])
                        ?? throw new InvalidOperationException($"GDAL could not create GeoPackage layer {layerName}");
                    foreach (string fieldName in PreservedFields)
                    {
                        using FieldDefn field = new(fieldName, FieldType.OFTString);
                        field.SetWidth(fieldName == "other_tags" ? 0 : 254);
                        if (layer.CreateField(field, 1) != 0)
                        {
                            throw new InvalidOperationException($"GDAL could not create {layerName}.{fieldName}");
                        }
                    }
                    layers.Add(layerName, layer);
                }

                foreach (RouteVectorFeature source in routeVectorFeatures)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Layer layer = layers[source.LayerName];
                    using Feature output = new(layer.GetLayerDefn());
                    foreach ((string name, string value) in source.Properties)
                    {
                        output.SetField(name, value);
                    }
                    output.SetGeometry(source.Geometry);
                    if (layer.CreateFeature(output) != 0)
                    {
                        throw new InvalidOperationException($"GDAL could not write a {source.LayerName} route feature");
                    }
                    counts[source.LayerName]++;
                    totalWritten++;
                    writtenProgress.Report(totalWritten, "GeoPackage features written");
                }

                foreach (Layer layer in layers.Values)
                {
                    layer.SyncToDisk();
                }
            }
            finally
            {
                foreach (Layer layer in layers.Values)
                {
                    layer.Dispose();
                }
            }

            return counts;
        }

        private void WriteRouteGeodataManifest(
            string path,
            IReadOnlyDictionary<string, int> layerCounts,
            IReadOnlyDictionary<string, int> categoryCounts,
            int polyVegFeatureCount,
            int exclusionFeatureCount)
        {
            FileInfo pbf = new(pbfPath);
            string pbfSha256;
            using (FileStream stream = new(pbfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
            {
                pbfSha256 = Convert.ToHexString(SHA256.HashData(stream));
            }

            var manifest = new
            {
                schemaVersion = SchemaVersion,
                generatorRevision = GeneratorRevision,
                sourcePbf = new
                {
                    relativePath = Path.GetRelativePath(GetRouteOsmDirectory(routeDirectory), pbf.FullName).Replace('\\', '/'),
                    sizeBytes = pbf.Length,
                    modifiedUtc = pbf.LastWriteTimeUtc,
                    sha256 = pbfSha256,
                },
                coverage = new
                {
                    terrainTileCount = coverageTiles.Count,
                    terrainTileFingerprint = coverageFingerprint,
                    bufferMetres = CoverageBufferMetres,
                    crs = "EPSG:4326",
                },
                output = new
                {
                    geopackage = "route-geodata.gpkg",
                    polyVegPolygons = new { file = "polyveg-polygons.geojson", features = polyVegFeatureCount },
                    polyVegExclusions = new { file = "polyveg-exclusions.geojson", features = exclusionFeatureCount },
                },
                polyVegCategories = categoryCounts.ToDictionary(
                    pair => pair.Key, pair => new { features = pair.Value }),
                layers = layerCounts.ToDictionary(pair => pair.Key, pair => new { features = pair.Value }),
                diagnostics = new
                {
                    invalidGeometryRepaired = invalidGeometryRepairedCount,
                    invalidGeometrySkipped = invalidGeometrySkippedCount,
                    incompleteRelationsSkipped = 0,
                },
            };
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, CacheJsonOptions), new UTF8Encoding(false));
            using FileStream flushed = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            flushed.Flush(true);
        }

        private static void ValidateGeoPackage(string path, IReadOnlyDictionary<string, int> expectedCounts)
        {
            using DataSource dataSource = Ogr.Open(path, 0)
                ?? throw new InvalidDataException("route GeoPackage could not be reopened");
            foreach ((string layerName, int expectedCount) in expectedCounts)
            {
                using Layer layer = dataSource.GetLayerByName(layerName)
                    ?? throw new InvalidDataException($"route GeoPackage is missing layer {layerName}");
                if (layer.GetFeatureCount(1) != expectedCount)
                {
                    throw new InvalidDataException($"route GeoPackage layer {layerName} failed feature-count validation");
                }
                SpatialReference? spatialReference = layer.GetSpatialRef();
                using (spatialReference)
                {
                    if (spatialReference is null)
                    {
                        throw new InvalidDataException($"route GeoPackage layer {layerName} has no declared CRS");
                    }
                }
            }
        }

        private static void ValidateRouteGeodataManifest(
            string path,
            IReadOnlyDictionary<string, int> expectedCounts,
            IReadOnlyDictionary<string, int> expectedCategoryCounts,
            int expectedPolyVegFeatures,
            int expectedExclusionFeatures)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement output = document.RootElement.GetProperty("output");
            if (output.GetProperty("polyVegPolygons").GetProperty("features").GetInt32() != expectedPolyVegFeatures ||
                output.GetProperty("polyVegExclusions").GetProperty("features").GetInt32() != expectedExclusionFeatures)
                throw new InvalidDataException("route geodata manifest PolyVeg output counts failed");
            JsonElement categories = document.RootElement.GetProperty("polyVegCategories");
            foreach ((string name, int count) in expectedCategoryCounts)
            {
                if (categories.GetProperty(name).GetProperty("features").GetInt32() != count)
                    throw new InvalidDataException($"route geodata manifest PolyVeg count failed for {name}");
            }
            JsonElement layers = document.RootElement.GetProperty("layers");
            foreach ((string name, int count) in expectedCounts)
            {
                if (layers.GetProperty(name).GetProperty("features").GetInt32() != count)
                {
                    throw new InvalidDataException($"route geodata manifest count failed for {name}");
                }
            }
        }

        private static void PromoteDerivativeSet(params (string TemporaryPath, string FinalPath)[] files)
        {
            List<(string FinalPath, string BackupPath)> backups = [];
            List<string> promoted = [];
            try
            {
                foreach ((string _, string finalPath) in files)
                {
                    string backupPath = finalPath + ".previous";
                    if (File.Exists(backupPath) && !File.Exists(finalPath))
                    {
                        File.Move(backupPath, finalPath);
                    }
                    else if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                    if (File.Exists(finalPath))
                    {
                        File.Move(finalPath, backupPath);
                        backups.Add((finalPath, backupPath));
                    }
                }
                foreach ((string temporaryPath, string finalPath) in files)
                {
                    File.Move(temporaryPath, finalPath);
                    promoted.Add(finalPath);
                }
            }
            catch
            {
                foreach (string finalPath in promoted)
                {
                    if (File.Exists(finalPath)) File.Delete(finalPath);
                }
                foreach ((string finalPath, string backupPath) in backups)
                {
                    if (File.Exists(backupPath)) File.Move(backupPath, finalPath, true);
                }
                throw;
            }

            foreach ((string _, string backupPath) in backups)
            {
                try
                {
                    File.Delete(backupPath);
                }
                catch (IOException)
                {
                    // The new validated set is already live. A harmless backup
                    // may be cleaned on the next rebuild rather than rolling
                    // back a successful atomic promotion.
                }
                catch (UnauthorizedAccessException)
                {
                    // See the IOException case above.
                }
            }
        }

        private static bool IsRouteGeodataManifestCurrent(string manifestPath, string geopackagePath, string sourcePbfPath, string fingerprint)
        {
            try
            {
                if (!File.Exists(manifestPath) || !File.Exists(geopackagePath) || !File.Exists(sourcePbfPath)) return false;
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                JsonElement root = document.RootElement;
                FileInfo pbf = new(sourcePbfPath);
                JsonElement source = root.GetProperty("sourcePbf");
                JsonElement coverage = root.GetProperty("coverage");
                JsonElement categories = root.GetProperty("polyVegCategories");
                string recordedPath = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(manifestPath)!,
                    source.GetProperty("relativePath").GetString()!.Replace('/', Path.DirectorySeparatorChar)));
                return root.GetProperty("schemaVersion").GetInt32() == SchemaVersion &&
                    root.GetProperty("generatorRevision").GetInt32() == GeneratorRevision &&
                    PolyVegCategoryNames.All(category => categories.TryGetProperty(category, out _)) &&
                    string.Equals(recordedPath, pbf.FullName, StringComparison.OrdinalIgnoreCase) &&
                    source.GetProperty("sizeBytes").GetInt64() == pbf.Length &&
                    source.GetProperty("modifiedUtc").GetDateTime().ToUniversalTime() == pbf.LastWriteTimeUtc &&
                    string.Equals(coverage.GetProperty("terrainTileFingerprint").GetString(), fingerprint, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                return false;
            }
        }

        private static Dictionary<string, string> RouteProperties(Feature feature, string osmType)
        {
            Dictionary<string, string> properties = new(StringComparer.Ordinal);
            properties["osm_type"] = osmType;
            foreach (string name in PreservedFields.Where(name => name != "osm_type"))
            {
                string value = RawField(feature, name);
                if (!string.IsNullOrWhiteSpace(value)) properties[name] = value;
            }
            return properties;
        }

        private static Geometry? ExtractGeometryFamily(Geometry geometry, bool polygons)
        {
            Geometry destination = new(polygons ? wkbGeometryType.wkbMultiPolygon : wkbGeometryType.wkbMultiLineString);
            AddGeometryFamilyParts(geometry, destination, polygons);
            if (destination.GetGeometryCount() == 0)
            {
                destination.Dispose();
                return null;
            }
            return destination;
        }

        private static void AddGeometryFamilyParts(Geometry geometry, Geometry destination, bool polygons)
        {
            wkbGeometryType type = geometry.GetGeometryType();
            bool matches = polygons
                ? type is wkbGeometryType.wkbPolygon or wkbGeometryType.wkbPolygon25D
                : type is wkbGeometryType.wkbLineString or wkbGeometryType.wkbLineString25D;
            if (matches)
            {
                destination.AddGeometry(geometry);
                return;
            }
            for (int index = 0; index < geometry.GetGeometryCount(); index++)
            {
                using Geometry child = geometry.GetGeometryRef(index);
                AddGeometryFamilyParts(child, destination, polygons);
            }
        }

        private static bool IsDevelopedLand(Feature feature)
        {
            string landuse = NormalizedField(feature, "landuse");
            if (landuse is "residential" or "commercial" or "retail" or "industrial" or "construction" or "brownfield" or "greenfield" or "landfill" or "quarry" or "garages" or "military" or "railway") return true;
            if (!string.IsNullOrEmpty(NormalizedField(feature, "military")) || !string.IsNullOrEmpty(NormalizedField(feature, "aeroway"))) return true;
            string amenity = NormalizedField(feature, "amenity");
            if (amenity is "school" or "college" or "university" or "hospital" or "parking") return true;
            return false;
        }

        private static bool IsPolyVegDerivativeCurrent(string outputPath, string sourcePbfPath, string fingerprint)
        {
            try
            {
                if (!File.Exists(outputPath) || !File.Exists(sourcePbfPath))
                {
                    return false;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outputPath));
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("schemaVersion", out JsonElement schema) || schema.GetInt32() != SchemaVersion ||
                    !root.TryGetProperty("generatorRevision", out JsonElement revision) || revision.GetInt32() != GeneratorRevision ||
                    !root.TryGetProperty("source", out JsonElement source) ||
                    !root.TryGetProperty("routeCoverage", out JsonElement coverage))
                {
                    return false;
                }

                FileInfo pbf = new(sourcePbfPath);
                string recordedPath = source.TryGetProperty("pbfRelativePath", out JsonElement relativePath)
                    ? Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(outputPath)!,
                        relativePath.GetString()!.Replace('/', Path.DirectorySeparatorChar)))
                    : source.GetProperty("pbfPath").GetString()!;
                return string.Equals(recordedPath, pbf.FullName, StringComparison.OrdinalIgnoreCase) &&
                    source.GetProperty("pbfSizeBytes").GetInt64() == pbf.Length &&
                    source.GetProperty("pbfModifiedUtc").GetDateTime().ToUniversalTime() == pbf.LastWriteTimeUtc &&
                    string.Equals(coverage.GetProperty("terrainTileFingerprint").GetString(), fingerprint, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                return false;
            }
        }

        internal static bool IsCurrentForProbe(string outputPath, string sourcePbfPath, string fingerprint) =>
            IsPolyVegDerivativeCurrent(outputPath, sourcePbfPath, fingerprint);

        internal static void WriteGeoPackageDriverProbe(string path)
        {
            ConfigureOsmRuntime();
            OSGeo.OGR.Driver driver = Ogr.GetDriverByName("GPKG")
                ?? throw new InvalidOperationException("bundled GDAL GeoPackage driver is unavailable");
            using SpatialReference geographic = CreateSpatialReference(4326);
            using (DataSource dataSource = driver.CreateDataSource(path, [])
                ?? throw new InvalidOperationException("GDAL could not create a probe GeoPackage"))
            using (Layer layer = dataSource.CreateLayer("habitat_woodland", geographic, wkbGeometryType.wkbMultiPolygon, ["SPATIAL_INDEX=YES"])
                ?? throw new InvalidOperationException("GDAL could not create a probe GeoPackage layer"))
            {
                using FieldDefn field = new("osm_id", FieldType.OFTString);
                if (layer.CreateField(field, 1) != 0) throw new InvalidOperationException("GDAL could not create a probe field");
                using Geometry ring = new(wkbGeometryType.wkbLinearRing);
                ring.AddPoint_2D(-75.0, 40.0);
                ring.AddPoint_2D(-74.9, 40.0);
                ring.AddPoint_2D(-74.9, 40.1);
                ring.AddPoint_2D(-75.0, 40.1);
                ring.AddPoint_2D(-75.0, 40.0);
                using Geometry polygon = new(wkbGeometryType.wkbPolygon);
                polygon.AddGeometry(ring);
                using Geometry multipolygon = new(wkbGeometryType.wkbMultiPolygon);
                multipolygon.AddGeometry(polygon);
                using Feature feature = new(layer.GetLayerDefn());
                feature.SetField("osm_id", "probe");
                feature.SetGeometry(multipolygon);
                if (layer.CreateFeature(feature) != 0) throw new InvalidOperationException("GDAL could not create a probe feature");
                layer.SyncToDisk();
            }

            using DataSource reopened = Ogr.Open(path, 0)
                ?? throw new InvalidDataException("probe GeoPackage could not be reopened");
            using Layer reopenedLayer = reopened.GetLayerByName("habitat_woodland")
                ?? throw new InvalidDataException("probe GeoPackage layer was not retained");
            if (reopenedLayer.GetFeatureCount(1) != 1)
            {
                throw new InvalidDataException("probe GeoPackage feature was not retained");
            }
        }

        internal static void RunExtractionProbe(string root)
        {
            ConfigureOsmRuntime();
            string routeDirectory = Path.Combine(root, "ProbeRoute");
            string tilesDirectory = Path.Combine(routeDirectory, "tiles");
            string worldDirectory = Path.Combine(routeDirectory, "world");
            Directory.CreateDirectory(tilesDirectory);
            Directory.CreateDirectory(worldDirectory);
            File.WriteAllText(Path.Combine(routeDirectory, "ProbeRoute.trk"), "Tr_RouteFile ( )");
            const int tileX = -11020;
            const int tileZ = 14359;
            const int terrainOnlyTileX = tileX + 1;
            File.WriteAllBytes(Path.Combine(tilesDirectory, RouteLayout.TileNameFromTileXZ(tileX, tileZ) + ".t"), [0]);
            File.WriteAllBytes(Path.Combine(tilesDirectory, RouteLayout.TileNameFromTileXZ(terrainOnlyTileX, tileZ) + ".t"), [0]);
            File.WriteAllText(Path.Combine(worldDirectory, WorldFileName(tileX, tileZ)), "SIMISA@@@@@@@@@@JINX0w0t______");
            if (!RouteLayout.TryLoad(routeDirectory, out RouteLayout? route, out string error) || route is null)
            {
                throw new InvalidOperationException("could not construct derivative probe route: " + error);
            }
            IReadOnlyList<WorldTile> routeCoverage = GetRouteCoverageTiles(route);
            if (route.WorldTiles.Count != 1 || route.TerrainTiles.Count != 2 || routeCoverage.Count != 2 ||
                !routeCoverage.Any(tile => tile.X == terrainOnlyTileX && tile.Z == tileZ))
            {
                throw new InvalidOperationException("terrain tile without a world file was omitted from route coverage");
            }
            GeoTileMapper mapper = GeoTileMapper.TryCreate(route)
                ?? throw new InvalidOperationException("could not construct derivative probe projection");
            WorldTile worldTile = route.WorldTiles.Single();
            (double minLon, double minLat, double maxLon, double maxLat) = mapper.GetBoundingBox(worldTile);
            double centerLon = (minLon + maxLon) * 0.5;
            double centerLat = (minLat + maxLat) * 0.5;
            double lonSpan = (maxLon - minLon) * 0.2;
            double latSpan = (maxLat - minLat) * 0.2;
            string sourcePath = Path.Combine(root, "synthetic-source.gpkg");
            CreateSyntheticOsmSource(sourcePath, centerLon, centerLat, lonSpan, latSpan);

            string compactSourcePath = BuildRouteOsmWorkingCacheFromSources(
                route, mapper, [sourcePath], CancellationToken.None);
            if (!string.Equals(
                FindCurrentRouteOsmWorkingCache(route, sourcePath),
                compactSourcePath,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("current compact route OSM cache was not reusable");
            }
            using JsonDocument compactManifest = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(GetRouteOsmDirectory(routeDirectory), RouteOsmWorkingManifestFileName)));
            if (compactManifest.RootElement.GetProperty("SchemaVersion").GetInt32() != 4 ||
                !string.Equals(
                    compactManifest.RootElement.GetProperty("TerrainTileFingerprint").GetString(),
                    GetRouteCoverageFingerprint(route),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("compact route OSM cache manifest is incorrect");
            }

            using DataSource source = Ogr.Open(compactSourcePath, 0)
                ?? throw new InvalidDataException("synthetic compact OSM source could not be reopened");
            using RoutePolyVegGeodataBuilder builder = new(route, mapper, sourcePath, CancellationToken.None);
            for (int layerIndex = 0; layerIndex < source.GetLayerCount(); layerIndex++)
            {
                using Layer layer = source.GetLayerByIndex(layerIndex);
                layer.ResetReading();
                while (true)
                {
                    using Feature? feature = layer.GetNextFeature();
                    if (feature is null) break;
                    using Geometry? geometry = feature.GetGeometryRef();
                    if (geometry is not null) builder.Collect(feature, geometry);
                }
            }
            builder.WriteAndPromote();

            string osmDirectory = GetRouteOsmDirectory(routeDirectory);
            string polyVegPath = Path.Combine(osmDirectory, "polyveg-polygons.geojson");
            string forestPath = Path.Combine(osmDirectory, "forest-polygons.geojson");
            string exclusionsPath = Path.Combine(osmDirectory, "polyveg-exclusions.geojson");
            string geopackagePath = Path.Combine(osmDirectory, "route-geodata.gpkg");
            string manifestPath = Path.Combine(osmDirectory, "route-geodata.json");
            if (!File.Exists(polyVegPath) || File.Exists(forestPath) || !File.Exists(exclusionsPath) ||
                !File.Exists(geopackagePath) || !File.Exists(manifestPath))
            {
                throw new InvalidDataException("synthetic extraction did not promote all route derivatives");
            }
            using JsonDocument polyVeg = JsonDocument.Parse(File.ReadAllText(polyVegPath));
            JsonElement routeCoverageMetadata = polyVeg.RootElement.GetProperty("routeCoverage");
            if (routeCoverageMetadata.GetProperty("terrainTileCount").GetInt32() != 2 ||
                !string.Equals(
                    routeCoverageMetadata.GetProperty("terrainTileFingerprint").GetString(),
                    GetRouteCoverageFingerprint(route),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("synthetic PolyVeg output did not retain complete terrain-tile coverage");
            }
            JsonElement polyVegFeatures = polyVeg.RootElement.GetProperty("features");
            HashSet<string> categories = polyVegFeatures.EnumerateArray()
                .Select(feature => feature.GetProperty("properties").GetProperty("category").GetString() ?? "")
                .ToHashSet(StringComparer.Ordinal);
            bool retainedTreeRow = polyVegFeatures.EnumerateArray().Any(feature =>
            {
                JsonElement properties = feature.GetProperty("properties");
                return properties.GetProperty("sourceId").GetString() == "way/9" &&
                    properties.GetProperty("category").GetString() == "woodland" &&
                    properties.GetProperty("styleId").GetString() == "natural=tree_row" &&
                    properties.GetProperty("drawOrder").GetInt32() == TsreDrawOrder(6) &&
                    properties.GetProperty("fillColor").GetString() == "#8DC46C" &&
                    properties.GetProperty("natural").GetString() == "tree_row" &&
                    properties.GetProperty("derivedWidthMetres").GetString() == "10";
            });
            if (polyVegFeatures.GetArrayLength() != PolyVegCategoryNames.Length + 1 ||
                !retainedTreeRow ||
                !PolyVegCategoryNames.All(categories.Contains))
            {
                throw new InvalidDataException("synthetic PolyVeg category output is incorrect");
            }
            using (DataSource polyVegDataSource = Ogr.Open(polyVegPath, 0)
                ?? throw new InvalidDataException("synthetic PolyVeg output could not be reopened"))
            using (Layer polyVegLayer = polyVegDataSource.GetLayerByIndex(0)
                ?? throw new InvalidDataException("synthetic PolyVeg output has no layer"))
            {
                bool retainedHole = false;
                while (true)
                {
                    using Feature? feature = polyVegLayer.GetNextFeature();
                    if (feature is null) break;
                    using Geometry? geometry = feature.GetGeometryRef();
                    retainedHole |= geometry is not null && ContainsPolygonHole(geometry);
                }
                if (!retainedHole)
                    throw new InvalidDataException("synthetic PolyVeg exclusion hole was not retained");
            }
            using JsonDocument exclusions = JsonDocument.Parse(File.ReadAllText(exclusionsPath));
            JsonElement exclusionFeatures = exclusions.RootElement.GetProperty("features");
            HashSet<string> exclusionKinds = exclusionFeatures.EnumerateArray()
                .Select(feature => feature.GetProperty("properties").GetProperty("kind").GetString() ?? "")
                .ToHashSet(StringComparer.Ordinal);
            if (exclusionFeatures.GetArrayLength() != 7 ||
                !new[] { "road", "track", "trail", "water", "building", "urban" }
                    .All(exclusionKinds.Contains))
            {
                throw new InvalidDataException("synthetic PolyVeg exclusion derivative is incorrect");
            }
            using DataSource package = Ogr.Open(geopackagePath, 0)
                ?? throw new InvalidDataException("synthetic route GeoPackage could not be reopened");
            using Layer woodland = package.GetLayerByName("habitat_woodland")
                ?? throw new InvalidDataException("synthetic woodland layer is missing");
            using Layer roads = package.GetLayerByName("roads")
                ?? throw new InvalidDataException("synthetic roads layer is missing");
            if (woodland.GetFeatureCount(1) != 2 || roads.GetFeatureCount(1) != 2)
            {
                throw new InvalidDataException("synthetic categorized layer counts are incorrect");
            }
        }

        private static void CreateSyntheticOsmSource(
            string path,
            double centerLon,
            double centerLat,
            double lonSpan,
            double latSpan)
        {
            OSGeo.OGR.Driver driver = Ogr.GetDriverByName("GPKG")!;
            using SpatialReference geographic = CreateSpatialReference(4326);
            using DataSource dataSource = driver.CreateDataSource(path, [])!;
            using (Layer polygons = dataSource.CreateLayer("multipolygons", geographic, wkbGeometryType.wkbMultiPolygon, [])!)
            {
                foreach (string name in new[] { "osm_id", "name", "natural", "landuse", "leisure", "tourism", "building", "other_tags" })
                {
                    using FieldDefn field = new(name, FieldType.OFTString);
                    polygons.CreateField(field, 1);
                }
                void AddPolygon(string id, string fieldName, string value,
                                double offsetX, double offsetY, double scale)
                {
                    using Geometry ring = new(wkbGeometryType.wkbLinearRing);
                    double x = centerLon + offsetX * lonSpan;
                    double y = centerLat + offsetY * latSpan;
                    double dx = lonSpan * scale;
                    double dy = latSpan * scale;
                    ring.AddPoint_2D(x - dx, y - dy);
                    ring.AddPoint_2D(x + dx, y - dy);
                    ring.AddPoint_2D(x + dx, y + dy);
                    ring.AddPoint_2D(x - dx, y + dy);
                    ring.AddPoint_2D(x - dx, y - dy);
                    using Geometry polygon = new(wkbGeometryType.wkbPolygon);
                    polygon.AddGeometry(ring);
                    using Geometry multipolygon = new(wkbGeometryType.wkbMultiPolygon);
                    multipolygon.AddGeometry(polygon);
                    using Feature feature = new(polygons.GetLayerDefn());
                    feature.SetField("osm_id", id);
                    feature.SetField(fieldName, value);
                    feature.SetGeometry(multipolygon);
                    if (polygons.CreateFeature(feature) != 0)
                        throw new InvalidOperationException("could not create synthetic polygon");
                }

                AddPolygon("relation/1", "natural", "wood", -0.75, -0.65, 0.09);
                AddPolygon("relation/2", "natural", "scrub", -0.45, -0.65, 0.09);
                AddPolygon("relation/3", "natural", "heath", -0.15, -0.65, 0.09);
                AddPolygon("relation/4", "natural", "grassland", 0.15, -0.65, 0.09);
                AddPolygon("relation/5", "natural", "wetland", 0.45, -0.65, 0.09);
                AddPolygon("relation/6", "landuse", "farmland", 0.75, -0.65, 0.09);
                AddPolygon("relation/10", "landuse", "orchard", -0.75, 0.00, 0.09);
                AddPolygon("relation/11", "leisure", "park", -0.45, 0.00, 0.09);
                AddPolygon("relation/12", "leisure", "golf_course", -0.15, 0.00, 0.09);
                AddPolygon("relation/13", "landuse", "cemetery", 0.15, 0.00, 0.09);
                AddPolygon("relation/14", "leisure", "pitch", 0.45, 0.00, 0.09);
                AddPolygon("relation/15", "tourism", "zoo", 0.75, 0.00, 0.09);
                AddPolygon("relation/7", "natural", "water", 0.00, 0.00, 0.10);
                AddPolygon("relation/8", "building", "yes", -0.75, -0.65, 0.03);
                AddPolygon("relation/9", "landuse", "residential", 0.75, -0.65, 0.03);
                AddPolygon("relation/99", "natural", "wood", 100.0, 100.0, 0.09);
            }
            using (Layer lines = dataSource.CreateLayer("lines", geographic, wkbGeometryType.wkbMultiLineString, [])!)
            {
                foreach (string name in new[] { "osm_id", "name", "natural", "highway", "railway", "waterway", "other_tags" })
                {
                    using FieldDefn field = new(name, FieldType.OFTString);
                    lines.CreateField(field, 1);
                }
                void AddLine(string id, string fieldName, string value, double offset)
                {
                    using Geometry line = new(wkbGeometryType.wkbLineString);
                    line.AddPoint_2D(centerLon - lonSpan, centerLat + offset * latSpan);
                    line.AddPoint_2D(centerLon + lonSpan, centerLat + offset * latSpan);
                    using Geometry multiline = new(wkbGeometryType.wkbMultiLineString);
                    multiline.AddGeometry(line);
                    using Feature feature = new(lines.GetLayerDefn());
                    feature.SetField("osm_id", id);
                    feature.SetField(fieldName, value);
                    feature.SetGeometry(multiline);
                    if (lines.CreateFeature(feature) != 0)
                        throw new InvalidOperationException("could not create synthetic line");
                }

                AddLine("way/2", "highway", "residential", -0.45);
                AddLine("way/6", "highway", "path", -0.15);
                AddLine("way/7", "railway", "rail", 0.15);
                AddLine("way/8", "waterway", "stream", 0.45);
                AddLine("way/9", "other_tags", "\"natural\"=>\"tree_row\"", 0.65);
            }
        }

        private static void ValidatePolyVegPolygons(
            string path,
            string expectedFingerprint,
            int expectedFeatures,
            IReadOnlyDictionary<string, int> expectedCategoryCounts,
            CoordinateTransformation toProjected)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (!string.Equals(root.GetProperty("type").GetString(), "FeatureCollection", StringComparison.Ordinal) ||
                root.GetProperty("schemaVersion").GetInt32() != SchemaVersion ||
                root.GetProperty("generatorRevision").GetInt32() != GeneratorRevision ||
                !string.Equals(root.GetProperty("routeCoverage").GetProperty("terrainTileFingerprint").GetString(), expectedFingerprint, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(root.GetProperty("routeCoverage").GetProperty("crs").GetString(), "EPSG:4326", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(root.GetProperty("crs").GetProperty("properties").GetProperty("name").GetString(), "EPSG:4326", StringComparison.OrdinalIgnoreCase) ||
                root.GetProperty("features").GetArrayLength() != expectedFeatures)
            {
                throw new InvalidDataException("PolyVeg polygon derivative failed collection validation");
            }

            Dictionary<string, int> actualCategoryCounts = PolyVegCategoryNames.ToDictionary(
                category => category, _ => 0, StringComparer.Ordinal);
            ProcessingCheckpoints propertyProgress = new(expectedFeatures);
            int propertiesChecked = 0;
            foreach (JsonElement feature in root.GetProperty("features").EnumerateArray())
            {
                JsonElement properties = feature.GetProperty("properties");
                string category = properties.GetProperty("category").GetString() ?? "";
                string sourceId = properties.GetProperty("sourceId").GetString() ?? "";
                string styleId = properties.GetProperty("styleId").GetString() ?? "";
                if (!actualCategoryCounts.ContainsKey(category) || string.IsNullOrWhiteSpace(sourceId) ||
                    string.IsNullOrWhiteSpace(styleId) || !styleId.Contains('=') ||
                    !properties.TryGetProperty("drawOrder", out JsonElement drawOrder) ||
                    drawOrder.ValueKind != JsonValueKind.Number ||
                    !properties.TryGetProperty("fillColor", out JsonElement fillColor) ||
                    fillColor.GetString()?.Length != 7 ||
                    !properties.TryGetProperty("fillRgb", out JsonElement fillRgb) ||
                    fillRgb.ValueKind != JsonValueKind.Array || fillRgb.GetArrayLength() != 3 ||
                    feature.GetProperty("geometry").GetProperty("type").GetString() is not ("Polygon" or "MultiPolygon"))
                {
                    throw new InvalidDataException("PolyVeg polygon feature failed required-property or geometry validation");
                }
                actualCategoryCounts[category]++;
                propertiesChecked++;
                propertyProgress.Report(propertiesChecked, "PolyVeg properties validated");
            }
            foreach ((string category, int expectedCount) in expectedCategoryCounts)
            {
                if (actualCategoryCounts[category] != expectedCount)
                    throw new InvalidDataException($"PolyVeg polygon category count failed for {category}");
            }

            using DataSource dataSource = Ogr.Open(path, 0)
                ?? throw new InvalidDataException("PolyVeg polygon GeoJSON could not be reopened by GDAL");
            using Layer layer = dataSource.GetLayerByIndex(0)
                ?? throw new InvalidDataException("PolyVeg polygon GeoJSON has no feature layer");
            ProcessingCheckpoints geometryProgress = new(expectedFeatures);
            int geometriesChecked = 0;
            layer.ResetReading();
            while (true)
            {
                using Feature? feature = layer.GetNextFeature();
                if (feature is null) break;
                using Geometry? sourceGeometry = feature.GetGeometryRef();
                if (sourceGeometry is null || sourceGeometry.IsEmpty())
                    throw new InvalidDataException("PolyVeg polygon contains empty geometry");
                using Geometry projected = sourceGeometry.Clone();
                projected.Transform(toProjected);
                double area = projected.GetArea();
                if (area <= GeometryAreaToleranceSquareMetres)
                    throw new InvalidDataException("PolyVeg polygon contains zero-area geometry");
                geometriesChecked++;
                geometryProgress.Report(geometriesChecked, "PolyVeg geometries validated");
            }
            WriteOsmLogEntry(
                "Exclusion and overlap topology validated within bounded terrain sections.",
                indent: 4);
        }

        private static bool ContainsPolygonHole(Geometry geometry)
        {
            if (geometry.GetGeometryType() is wkbGeometryType.wkbPolygon or wkbGeometryType.wkbPolygon25D)
                return geometry.GetGeometryCount() > 1;
            for (int index = 0; index < geometry.GetGeometryCount(); index++)
            {
                using Geometry child = geometry.GetGeometryRef(index);
                if (ContainsPolygonHole(child)) return true;
            }
            return false;
        }

        private static void RemoveObsoleteDerivative(string path)
        {
            if (!File.Exists(path)) return;
            File.Delete(path);
            string backupPath = path + ".previous";
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }

        private static void ValidatePolyVegExclusions(
            string path,
            string expectedFingerprint,
            int expectedFeatures)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            JsonElement features = root.GetProperty("features");
            if (!string.Equals(root.GetProperty("type").GetString(), "FeatureCollection", StringComparison.Ordinal) ||
                root.GetProperty("schemaVersion").GetInt32() != SchemaVersion ||
                root.GetProperty("generatorRevision").GetInt32() != GeneratorRevision ||
                !string.Equals(root.GetProperty("routeCoverage").GetProperty("terrainTileFingerprint").GetString(), expectedFingerprint, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(root.GetProperty("routeCoverage").GetProperty("crs").GetString(), "EPSG:4326", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(root.GetProperty("crs").GetProperty("properties").GetProperty("name").GetString(), "EPSG:4326", StringComparison.OrdinalIgnoreCase) ||
                features.GetArrayLength() != expectedFeatures)
            {
                throw new InvalidDataException("PolyVeg exclusion derivative failed validation");
            }

            ProcessingCheckpoints progress = new(expectedFeatures);
            int checkedFeatures = 0;
            foreach (JsonElement feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("id", out JsonElement id) ||
                    string.IsNullOrWhiteSpace(id.GetString()) ||
                    !feature.GetProperty("properties").TryGetProperty("kind", out JsonElement kind) ||
                    string.IsNullOrWhiteSpace(kind.GetString()) ||
                    string.Equals(kind.GetString(), "agriculture", StringComparison.Ordinal) ||
                    feature.GetProperty("geometry").GetProperty("type").GetString() is not ("Polygon" or "MultiPolygon"))
                {
                    throw new InvalidDataException("PolyVeg exclusion feature failed schema validation");
                }
                checkedFeatures++;
                progress.Report(checkedFeatures, "PolyVeg exclusions validated");
            }
        }

        private static Geometry BuildRouteCoverage(GeoTileMapper mapper, IReadOnlyList<WorldTile> tiles)
        {
            using Geometry polygons = new(wkbGeometryType.wkbMultiPolygon);
            foreach (WorldTile tile in tiles)
            {
                GeoSampleGrid corners = mapper.GetAreaSampleGrid(tile.X, tile.Z, OrtsTileSizeMeters, OrtsTileSizeMeters, 2);
                using Geometry ring = new(wkbGeometryType.wkbLinearRing);
                ring.AddPoint_2D(corners.Longitudes[1, 0], corners.Latitudes[1, 0]);
                ring.AddPoint_2D(corners.Longitudes[1, 1], corners.Latitudes[1, 1]);
                ring.AddPoint_2D(corners.Longitudes[0, 1], corners.Latitudes[0, 1]);
                ring.AddPoint_2D(corners.Longitudes[0, 0], corners.Latitudes[0, 0]);
                ring.AddPoint_2D(corners.Longitudes[1, 0], corners.Latitudes[1, 0]);
                using Geometry polygon = new(wkbGeometryType.wkbPolygon);
                polygon.AddGeometry(ring);
                polygons.AddGeometry(polygon);
            }

            return polygons.UnionCascaded()
                ?? throw new InvalidOperationException("GDAL could not union route world-tile coverage");
        }

        private static SpatialReference CreateSpatialReference(int epsg)
        {
            SpatialReference spatialReference = new("");
            spatialReference.ImportFromEPSG(epsg);
            spatialReference.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
            return spatialReference;
        }

        internal static IReadOnlyList<WorldTile> GetRouteCoverageTiles(RouteLayout route)
        {
            WorldTile[] terrainTiles = route.TerrainTiles
                .Select(tile => tile.WorldTile)
                .Where(tile => tile is not null)
                .Select(tile => tile!)
                .DistinctBy(tile => (tile.X, tile.Z))
                .OrderBy(tile => tile.Z)
                .ThenBy(tile => tile.X)
                .ToArray();

            // Standard terrain names encode their world coordinate even when
            // no object-bearing .w file exists. Retain the older world-file
            // fallback only for routes whose terrain names cannot be decoded.
            return terrainTiles.Length > 0
                ? terrainTiles
                : route.WorldTiles
                    .DistinctBy(tile => (tile.X, tile.Z))
                    .OrderBy(tile => tile.Z)
                    .ThenBy(tile => tile.X)
                    .ToArray();
        }

        internal static string GetRouteCoverageFingerprint(RouteLayout route)
        {
            return GetTileCoverageFingerprint(GetRouteCoverageTiles(route));
        }

        private static string GetTileCoverageFingerprint(IEnumerable<WorldTile> tiles)
        {
            string text = string.Join('\n', tiles
                .DistinctBy(tile => (tile.X, tile.Z))
                .OrderBy(tile => tile.Z)
                .ThenBy(tile => tile.X)
                .Select(tile => $"{tile.X},{tile.Z}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        private static Dictionary<string, string> PolyVegProperties(
            Feature feature,
            string osmType = "multipolygon")
        {
            Dictionary<string, string> properties = RouteProperties(feature, osmType);
            properties.Remove("osm_id");
            return properties;
        }

        private static string StablePolygonSourceId(Feature feature)
        {
            string relationId = RawField(feature, "osm_id").Trim();
            if (!string.IsNullOrWhiteSpace(relationId))
                return relationId.Contains('/') ? relationId : $"relation/{relationId}";
            string wayId = RawField(feature, "osm_way_id").Trim();
            if (!string.IsNullOrWhiteSpace(wayId))
                return wayId.Contains('/') ? wayId : $"way/{wayId}";
            throw new InvalidDataException("plantable OSM polygon lacks a stable osm_id/osm_way_id");
        }

        private static string StableLineSourceId(Feature feature)
        {
            string wayId = RawField(feature, "osm_id").Trim();
            if (!string.IsNullOrWhiteSpace(wayId))
                return wayId.Contains('/') ? wayId : $"way/{wayId}";
            wayId = RawField(feature, "osm_way_id").Trim();
            if (!string.IsNullOrWhiteSpace(wayId))
                return wayId.Contains('/') ? wayId : $"way/{wayId}";
            throw new InvalidDataException("plantable OSM tree row lacks a stable osm_id/osm_way_id");
        }

        private static string? PermanentPolygonExclusionKind(Feature feature)
        {
            string building = NormalizedField(feature, "building");
            if (!string.IsNullOrEmpty(building) && building is not "no" and not "false") return "building";

            string natural = NormalizedField(feature, "natural");
            string landuse = NormalizedField(feature, "landuse");
            if (natural is "water" or "bay" or "strait" ||
                landuse is "reservoir" or "basin" ||
                !string.IsNullOrEmpty(NormalizedField(feature, "water"))) return "water";
            if (natural is "beach" or "sand" or "bare_rock" or "scree" || landuse == "quarry") return "bare_ground";
            if (ExcludedLanduse.Contains(landuse) ||
                ExcludedAmenity.Contains(NormalizedField(feature, "amenity")) ||
                !string.IsNullOrEmpty(NormalizedField(feature, "aeroway")) ||
                !string.IsNullOrEmpty(NormalizedField(feature, "military")) ||
                NormalizedField(feature, "man_made") is "wastewater_plant" or "water_works") return "urban";
            return null;
        }

        private static string? LineExclusionKind(Feature feature)
        {
            string highway = NormalizedField(feature, "highway");
            if (!string.IsNullOrEmpty(highway))
            {
                return highway switch
                {
                    "track" => "track",
                    "footway" or "path" or "cycleway" or "bridleway" or "steps" => "trail",
                    "motorway" or "motorway_link" or "trunk" or "trunk_link" or
                    "primary" or "primary_link" or "secondary" or "secondary_link" or
                    "tertiary" or "tertiary_link" or "residential" or "unclassified" or
                    "service" or "living_street" => "road",
                    _ => null,
                };
            }

            string railway = NormalizedField(feature, "railway");
            if (!string.IsNullOrEmpty(railway) && railway is not "abandoned" and not "disused")
            {
                return "track";
            }

            return NormalizedField(feature, "waterway") is
                "river" or "canal" or "stream" or "ditch" or "drain" ? "water" : null;
        }

        private static string ExclusionId(Feature feature, string kind)
        {
            string id = RawField(feature, "osm_id");
            if (!string.IsNullOrWhiteSpace(id)) return id;
            return $"{kind}/{feature.GetFID()}";
        }

        private static double ExclusionLineBufferMetres(Feature feature)
        {
            string highway = NormalizedField(feature, "highway");
            if (!string.IsNullOrEmpty(highway))
            {
                return highway switch
                {
                    "motorway" or "motorway_link" or "trunk" or "trunk_link" => 15.0,
                    "primary" or "primary_link" or "secondary" or "secondary_link" => 10.0,
                    "tertiary" or "tertiary_link" => 8.0,
                    "residential" or "unclassified" or "service" or "living_street" => 6.0,
                    "track" => 4.0,
                    "footway" or "path" or "cycleway" or "bridleway" or "steps" => 2.0,
                    _ => 0.0,
                };
            }

            string railway = NormalizedField(feature, "railway");
            if (!string.IsNullOrEmpty(railway) && railway is not "abandoned" and not "disused")
            {
                return railway == "rail" ? 8.0 : 5.0;
            }

            return NormalizedField(feature, "waterway") switch
            {
                "river" => 15.0,
                "canal" => 10.0,
                "stream" or "ditch" or "drain" => 5.0,
                _ => 0.0,
            };
        }

        private static string RawField(Feature feature, string name)
        {
            string value = string.Equals(name, "natural", StringComparison.OrdinalIgnoreCase)
                ? GetOgrTag(feature, name)
                : GetOgrField(feature, name);
            return value.Trim();
        }

        private static string NormalizedField(Feature feature, string name) =>
            RawField(feature, name).ToLowerInvariant();

        private bool AddValidatedExclusionParts(Geometry geometry)
        {
            bool added = false;
            AddValidatedExclusionPartsRecursive(geometry, ref added);
            return added;
        }

        private void AddValidatedExclusionPartsRecursive(Geometry geometry, ref bool added)
        {
            wkbGeometryType type = geometry.GetGeometryType();
            if (type is wkbGeometryType.wkbPolygon or wkbGeometryType.wkbPolygon25D)
            {
                using Geometry? snapped = SnapPolygonToGrid(geometry, OverlayPrecisionMetres);
                if (snapped is null || snapped.IsEmpty())
                {
                    invalidGeometrySkippedCount++;
                    return;
                }
                if (snapped.IsValid())
                {
                    AddPolygonParts(snapped, exclusionPolygons);
                    added = true;
                    return;
                }

                using Geometry? repaired = snapped.MakeValid([]);
                if (repaired is null || repaired.IsEmpty() || !repaired.IsValid())
                {
                    invalidGeometrySkippedCount++;
                    return;
                }
                invalidGeometryRepairedCount++;
                AddPolygonParts(repaired, exclusionPolygons);
                added = true;
                return;
            }

            for (int index = 0; index < geometry.GetGeometryCount(); index++)
            {
                using Geometry child = geometry.GetGeometryRef(index);
                AddValidatedExclusionPartsRecursive(child, ref added);
            }
        }

        private static Geometry? SnapPolygonToGrid(Geometry polygon, double gridSize)
        {
            Geometry snapped = new(wkbGeometryType.wkbPolygon);
            for (int ringIndex = 0; ringIndex < polygon.GetGeometryCount(); ringIndex++)
            {
                using Geometry sourceRing = polygon.GetGeometryRef(ringIndex);
                List<(double X, double Y)> points = [];
                for (int pointIndex = 0; pointIndex < sourceRing.GetPointCount(); pointIndex++)
                {
                    double x = Math.Round(sourceRing.GetX(pointIndex) / gridSize) * gridSize;
                    double y = Math.Round(sourceRing.GetY(pointIndex) / gridSize) * gridSize;
                    if (points.Count == 0 || points[^1] != (x, y)) points.Add((x, y));
                }
                if (points.Count > 1 && points[^1] == points[0]) points.RemoveAt(points.Count - 1);
                if (points.Distinct().Count() < 3) continue;

                using Geometry ring = new(wkbGeometryType.wkbLinearRing);
                foreach ((double x, double y) in points) ring.AddPoint_2D(x, y);
                ring.AddPoint_2D(points[0].X, points[0].Y);
                snapped.AddGeometry(ring);
            }
            if (snapped.GetGeometryCount() == 0)
            {
                snapped.Dispose();
                return null;
            }
            return snapped;
        }

        private static Geometry? NormalizePolygonalOverlay(Geometry geometry, double gridSize)
        {
            Geometry snappedParts = new(wkbGeometryType.wkbMultiPolygon);
            AddSnappedPolygonParts(geometry, snappedParts, gridSize);
            if (snappedParts.GetGeometryCount() == 0)
            {
                snappedParts.Dispose();
                return null;
            }

            if (snappedParts.IsValid()) return snappedParts;

            using (snappedParts)
            using (Geometry? repaired = snappedParts.MakeValid([]))
            {
                if (repaired is null || repaired.IsEmpty()) return null;
                Geometry? polygonal = ExtractGeometryFamily(repaired, polygons: true);
                if (polygonal is null || polygonal.IsEmpty())
                {
                    polygonal?.Dispose();
                    return null;
                }
                if (polygonal.IsValid()) return polygonal;

                using (polygonal)
                {
                    Geometry? normalized = polygonal.Buffer(0.0, 8);
                    if (normalized is null || normalized.IsEmpty())
                    {
                        normalized?.Dispose();
                        return null;
                    }
                    return normalized;
                }
            }
        }

        private static void AddSnappedPolygonParts(
            Geometry geometry,
            Geometry destination,
            double gridSize)
        {
            wkbGeometryType type = geometry.GetGeometryType();
            if (type is wkbGeometryType.wkbPolygon or wkbGeometryType.wkbPolygon25D)
            {
                using Geometry? snapped = SnapPolygonToGrid(geometry, gridSize);
                if (snapped is not null && !snapped.IsEmpty()) destination.AddGeometry(snapped);
                return;
            }

            for (int index = 0; index < geometry.GetGeometryCount(); index++)
            {
                using Geometry child = geometry.GetGeometryRef(index);
                AddSnappedPolygonParts(child, destination, gridSize);
            }
        }

        private static Geometry? DifferenceWithTopologyRetry(
            Geometry left,
            Geometry right,
            string operationDescription)
        {
            try
            {
                return left.Difference(right);
            }
            catch (Exception firstFailure)
            {
                WriteOsmLogEntry(
                    $"Topology retry: {operationDescription}: {firstFailure.Message}",
                    indent: 4);
                using Geometry? normalizedLeft = NormalizePolygonalOverlay(
                    left,
                    TerrainOverlayPrecisionMetres);
                using Geometry? normalizedRight = NormalizePolygonalOverlay(
                    right,
                    TerrainOverlayPrecisionMetres);
                if (normalizedLeft is null || normalizedRight is null)
                    throw new InvalidDataException(
                        $"could not normalize geometry during {operationDescription}",
                        firstFailure);
                try
                {
                    return normalizedLeft.Difference(normalizedRight);
                }
                catch (Exception retryFailure)
                {
                    throw new InvalidDataException(
                        $"topology operation failed during {operationDescription} after precision normalization: " +
                        retryFailure.Message,
                        new AggregateException(firstFailure, retryFailure));
                }
            }
        }

        private static Geometry UnionWithTopologyRetry(
            Geometry left,
            Geometry right,
            string operationDescription)
        {
            try
            {
                return left.Union(right)
                    ?? throw new InvalidOperationException("GDAL returned no union geometry");
            }
            catch (Exception firstFailure)
            {
                WriteOsmLogEntry(
                    $"Topology retry: {operationDescription}: {firstFailure.Message}",
                    indent: 4);
                using Geometry combined = new(wkbGeometryType.wkbMultiPolygon);
                AddPolygonParts(left, combined);
                AddPolygonParts(right, combined);
                Geometry? normalized = combined.Buffer(0.0, 8);
                if (normalized is null || normalized.IsEmpty())
                {
                    normalized?.Dispose();
                    throw new InvalidDataException(
                        $"topology union failed during {operationDescription}",
                        firstFailure);
                }
                return normalized;
            }
        }

        private static void AddPolygonParts(Geometry geometry, Geometry destination)
        {
            wkbGeometryType type = geometry.GetGeometryType();
            if (type is wkbGeometryType.wkbPolygon or wkbGeometryType.wkbPolygon25D)
            {
                destination.AddGeometry(geometry);
                return;
            }

            for (int index = 0; index < geometry.GetGeometryCount(); index++)
            {
                using Geometry child = geometry.GetGeometryRef(index);
                AddPolygonParts(child, destination);
            }
        }

        private static void CountPolygonParts(Geometry geometry, ref int polygons, ref int holes)
        {
            wkbGeometryType type = geometry.GetGeometryType();
            if (type is wkbGeometryType.wkbPolygon or wkbGeometryType.wkbPolygon25D)
            {
                polygons++;
                holes += Math.Max(0, geometry.GetGeometryCount() - 1);
                return;
            }

            for (int index = 0; index < geometry.GetGeometryCount(); index++)
            {
                using Geometry child = geometry.GetGeometryRef(index);
                CountPolygonParts(child, ref polygons, ref holes);
            }
        }

        private static int CountPolygonParts(Geometry geometry)
        {
            int polygons = 0;
            int holes = 0;
            CountPolygonParts(geometry, ref polygons, ref holes);
            return polygons;
        }
    }
}
