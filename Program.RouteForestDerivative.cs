// Copyright (C) Scott Brunner, Beast of Burden
// Route-local OSM woodland derivative used by TSRE GenX.
// SCO LIDEX is distributed under GNU GPL v3 or later. See LICENSE.txt.

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

            string derivativePath = Path.Combine(GetRouteOsmDirectory(routeDirectory), "forest-polygons.geojson");
            string geopackagePath = Path.Combine(GetRouteOsmDirectory(routeDirectory), "route-geodata.gpkg");
            string geodataManifestPath = Path.Combine(GetRouteOsmDirectory(routeDirectory), "route-geodata.json");
            const string fingerprint = "PROBE-COVERAGE";
            FileInfo pbf = new(pbfPath);
            File.WriteAllText(
                derivativePath,
                JsonSerializer.Serialize(new
                {
                    type = "FeatureCollection",
                    schemaVersion = 1,
                    source = new
                    {
                        pbfPath = pbf.FullName,
                        pbfSizeBytes = pbf.Length,
                        pbfModifiedUtc = pbf.LastWriteTimeUtc,
                    },
                    routeCoverage = new { worldTileFingerprint = fingerprint },
                    features = Array.Empty<object>(),
                }));

            if (!RouteForestDerivativeBuilder.IsCurrentForProbe(derivativePath, pbfPath, fingerprint))
            {
                throw new InvalidOperationException("matching route derivative was not recognized as current");
            }

            File.AppendAllText(pbfPath, "fresh");
            if (RouteForestDerivativeBuilder.IsCurrentForProbe(derivativePath, pbfPath, fingerprint))
            {
                throw new InvalidOperationException("changed bulk source did not invalidate the route derivative");
            }

            WriteRouteOsmManifest(routeDirectory, region with { SizeBytes = new FileInfo(pbfPath).Length }, pbfPath);
            RouteForestDerivativeBuilder.WriteGeoPackageDriverProbe(geopackagePath);
            File.WriteAllText(geodataManifestPath, "{}");
            IReadOnlyList<MapCacheEntry> caches = GetKnownMapCaches(routeDirectory);
            if (caches.Count != 1 || caches.SelectMany(cache => cache.Files).Any(path =>
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(derivativePath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(geopackagePath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFullPath(path), Path.GetFullPath(geodataManifestPath), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("exit cache discovery included route derivative data");
            }

            PurgeMapCaches(caches);
            if (File.Exists(pbfPath) || File.Exists(GetRouteOsmManifestPath(routeDirectory)) ||
                !File.Exists(derivativePath) || !File.Exists(geopackagePath) || !File.Exists(geodataManifestPath))
            {
                throw new InvalidOperationException("bulk purge did not preserve the route derivative");
            }

            RouteForestDerivativeBuilder.RunExtractionProbe(Path.Combine(probeRoot, "extraction"));

            Console.WriteLine("Route OSM lifecycle probe: PASSED");
            Console.WriteLine("  changed/new bulk source invalidates the route derivative");
            Console.WriteLine("  exit purge includes only PBF bulk data and preserves route derivatives");
            Console.WriteLine("  categorized GeoPackage, manifest, and TSRE forest cache validate atomically");
        }
        finally
        {
            if (Directory.Exists(probeRoot))
            {
                Directory.Delete(probeRoot, true);
            }
        }
    }

    private sealed class RouteForestDerivativeBuilder : IDisposable
    {
        private const int SchemaVersion = 1;
        private const double CoverageBufferMetres = 2048.0;

        private static readonly HashSet<string> ExcludedNatural = new(StringComparer.OrdinalIgnoreCase)
        {
            "water", "bay", "strait", "beach", "sand", "bare_rock", "scree",
        };

        private static readonly HashSet<string> ExcludedLanduse = new(StringComparer.OrdinalIgnoreCase)
        {
            "reservoir", "basin", "residential", "commercial", "retail", "industrial",
            "construction", "brownfield", "greenfield", "landfill", "quarry", "farmland",
            "farmyard", "farm", "orchard", "vineyard", "meadow", "allotments",
            "plant_nursery", "greenhouse_horticulture", "recreation_ground", "cemetery",
            "garages", "military", "railway",
        };

        private static readonly HashSet<string> ExcludedLeisure = new(StringComparer.OrdinalIgnoreCase)
        {
            "pitch", "golf_course", "playground", "sports_centre", "stadium",
        };

        private static readonly HashSet<string> ExcludedAmenity = new(StringComparer.OrdinalIgnoreCase)
        {
            "parking", "school", "college", "university", "hospital",
        };

        private sealed record WoodlandSource(
            string Id,
            Dictionary<string, string> Properties,
            Geometry ProjectedGeometry);

        private sealed record RouteVectorFeature(
            string LayerName,
            Dictionary<string, string> Properties,
            Geometry Geometry);

        private static readonly string[] RouteLayerNames =
        [
            "habitat_woodland", "habitat_scrub", "habitat_heath", "habitat_grassland",
            "habitat_wetland", "water_polygons", "waterways", "buildings",
            "developed_land", "agriculture", "roads", "railways", "bare_ground",
        ];

        private static readonly string[] PreservedFields =
        [
            "osm_type", "osm_id", "name", "natural", "landuse", "water", "waterway",
            "wetland", "leaf_type", "leaf_cycle", "wood", "species", "building", "amenity",
            "leisure", "aeroway", "military", "man_made", "highway", "railway", "service",
            "surface", "tracktype", "bridge", "tunnel", "layer", "width", "lanes", "gauge",
            "other_tags",
        ];

        private readonly string routeDirectory;
        private readonly string pbfPath;
        private readonly string outputPath;
        private readonly string coverageFingerprint;
        private readonly IReadOnlyList<WorldTile> worldTiles;
        private readonly Geometry exactCoverageGeographic;
        private readonly Geometry bufferedCoverageGeographic;
        private readonly CoordinateTransformation toProjected;
        private readonly CoordinateTransformation toGeographic;
        private readonly Geometry exclusionPolygons = new(wkbGeometryType.wkbMultiPolygon);
        private readonly List<WoodlandSource> woodlandSources = [];
        private readonly List<RouteVectorFeature> routeVectorFeatures = [];
        private readonly CancellationToken cancellationToken;
        private bool promoted;
        private int woodlandFeatureCount;
        private int exclusionPolygonFeatureCount;
        private int exclusionLineFeatureCount;
        private int invalidGeometryRepairedCount;
        private int invalidGeometrySkippedCount;

        private RouteForestDerivativeBuilder(
            RouteLayout route,
            GeoTileMapper mapper,
            string sourcePbfPath,
            CancellationToken token)
        {
            routeDirectory = route.RouteDir;
            pbfPath = Path.GetFullPath(sourcePbfPath);
            outputPath = Path.Combine(GetRouteOsmDirectory(route.RouteDir), "forest-polygons.geojson");
            worldTiles = route.WorldTiles
                .DistinctBy(tile => (tile.X, tile.Z))
                .OrderBy(tile => tile.Z)
                .ThenBy(tile => tile.X)
                .ToArray();
            coverageFingerprint = GetWorldTileFingerprint(worldTiles);
            cancellationToken = token;

            exactCoverageGeographic = BuildRouteCoverage(mapper, worldTiles);
            Envelope envelope = new();
            exactCoverageGeographic.GetEnvelope(envelope);

            using SpatialReference geographic = CreateSpatialReference(4326);
            int utmZone = Math.Clamp((int)Math.Floor((((envelope.MinX + envelope.MaxX) * 0.5) + 180.0) / 6.0) + 1, 1, 60);
            int projectedEpsg = ((envelope.MinY + envelope.MaxY) * 0.5) >= 0.0 ? 32600 + utmZone : 32700 + utmZone;
            using SpatialReference projected = CreateSpatialReference(projectedEpsg);
            toProjected = new CoordinateTransformation(geographic, projected);
            toGeographic = new CoordinateTransformation(projected, geographic);

            using Geometry projectedCoverage = exactCoverageGeographic.Clone();
            projectedCoverage.Transform(toProjected);
            bufferedCoverageGeographic = projectedCoverage.Buffer(CoverageBufferMetres, 8)
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

        internal static RouteForestDerivativeBuilder? TryCreate(
            RouteLayout route,
            GeoTileMapper mapper,
            string pbfPath,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            if (route.WorldTiles.Count == 0)
            {
                Console.WriteLine("Route OSM derivative skipped: route has no world-tile coverage.");
                return null;
            }

            string outputPath = Path.Combine(GetRouteOsmDirectory(route.RouteDir), "forest-polygons.geojson");
            string geopackagePath = Path.Combine(GetRouteOsmDirectory(route.RouteDir), "route-geodata.gpkg");
            string manifestPath = Path.Combine(GetRouteOsmDirectory(route.RouteDir), "route-geodata.json");
            string fingerprint = GetWorldTileFingerprint(route.WorldTiles);
            if (!forceRefresh &&
                IsForestDerivativeCurrent(outputPath, pbfPath, fingerprint) &&
                IsRouteGeodataManifestCurrent(manifestPath, geopackagePath, pbfPath, fingerprint))
            {
                Console.WriteLine($"Route OSM derivative is current: {outputPath}");
                return null;
            }

            Console.WriteLine(forceRefresh
                ? "Fresh bulk OSM download detected; rebuilding the route OSM derivative."
                : "Route OSM derivative is missing or stale; rebuilding it for the active route.");
            Console.WriteLine($"Route OSM derivative output: {outputPath}");
            return new RouteForestDerivativeBuilder(route, mapper, pbfPath, cancellationToken);
        }

        internal void Collect(Feature feature, Geometry geometry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Geometry? repairedGeometry = geometry.IsValid() ? null : geometry.MakeValid([]);
            if (repairedGeometry is not null && !repairedGeometry.IsEmpty())
            {
                invalidGeometryRepairedCount++;
            }
            else if (!geometry.IsValid())
            {
                invalidGeometrySkippedCount++;
                return;
            }
            Geometry sourceGeometry = repairedGeometry ?? geometry;
            if (!sourceGeometry.Intersects(bufferedCoverageGeographic))
            {
                return;
            }

            string layerName = feature.GetDefnRef().GetName();
            if (string.Equals(layerName, "multipolygons", StringComparison.OrdinalIgnoreCase))
            {
                bool woodland = IsWoodland(feature);
                bool excluded = IsExcludedPolygon(feature);
                if (!woodland && !excluded)
                {
                    return;
                }

                if (woodland)
                {
                    using Geometry? clipped = sourceGeometry.Intersection(exactCoverageGeographic);
                    if (clipped is not null && !clipped.IsEmpty())
                    {
                        clipped.Transform(toProjected);
                        woodlandSources.Add(new WoodlandSource(
                            NormalizedField(feature, "osm_id"),
                            WoodlandProperties(feature),
                            clipped.Clone()));
                        woodlandFeatureCount++;
                    }
                }

                CollectPolygonLayers(feature, sourceGeometry);

                if (excluded)
                {
                    using Geometry? clipped = sourceGeometry.Intersection(bufferedCoverageGeographic);
                    if (clipped is not null && !clipped.IsEmpty())
                    {
                        clipped.Transform(toProjected);
                        AddPolygonParts(clipped, exclusionPolygons);
                        exclusionPolygonFeatureCount++;
                    }
                }
            }
            else if (string.Equals(layerName, "lines", StringComparison.OrdinalIgnoreCase))
            {
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

                AddPolygonParts(buffered, exclusionPolygons);
                exclusionLineFeatureCount++;
            }
        }

        internal void WriteAndPromote()
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"Route OSM sources: {woodlandFeatureCount:N0} woodland, {exclusionPolygonFeatureCount:N0} polygon masks, {exclusionLineFeatureCount:N0} line masks.");
            Console.WriteLine($"Building the route woodland exclusion union from {exclusionPolygons.GetGeometryCount():N0} polygon part(s)...");
            using Geometry? exclusionMask = exclusionPolygons.GetGeometryCount() == 0
                ? null
                : exclusionPolygons.UnionCascaded();

            string osmDirectory = GetRouteOsmDirectory(routeDirectory);
            Directory.CreateDirectory(osmDirectory);
            string temporaryPath = outputPath + ".tmp";
            string geopackagePath = Path.Combine(osmDirectory, "route-geodata.gpkg");
            string temporaryGeopackagePath = Path.Combine(osmDirectory, "route-geodata.tmp.gpkg");
            string manifestPath = Path.Combine(osmDirectory, "route-geodata.json");
            string temporaryManifestPath = manifestPath + ".tmp";
            int writtenFeatures = 0;
            int writtenParts = 0;
            int writtenHoles = 0;
            try
            {
                using (FileStream stream = new(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
                {
                    WriteHeader(writer);
                    foreach (WoodlandSource source in woodlandSources)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using Geometry? plantable = exclusionMask is null
                            ? source.ProjectedGeometry.Clone()
                            : source.ProjectedGeometry.Difference(exclusionMask);
                        if (plantable is null || plantable.IsEmpty())
                        {
                            continue;
                        }

                        double originalArea = source.ProjectedGeometry.GetArea();
                        double plantableArea = plantable.GetArea();
                        plantable.Transform(toGeographic);
                        CountPolygonParts(plantable, ref writtenParts, ref writtenHoles);
                        WriteFeature(writer, source, plantable, originalArea, plantableArea);
                        writtenFeatures++;
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.Flush();
                    stream.Flush(true);
                }
                ValidateForestDerivative(temporaryPath, coverageFingerprint, writtenFeatures);

                Dictionary<string, int> layerCounts = WriteGeoPackage(temporaryGeopackagePath);
                WriteRouteGeodataManifest(temporaryManifestPath, layerCounts);
                ValidateGeoPackage(temporaryGeopackagePath, layerCounts);
                ValidateRouteGeodataManifest(temporaryManifestPath, layerCounts);
                PromoteDerivativeSet(
                    (temporaryPath, outputPath),
                    (temporaryGeopackagePath, geopackagePath),
                    (temporaryManifestPath, manifestPath));
                promoted = true;
            }
            finally
            {
                foreach (string path in new[] { temporaryPath, temporaryGeopackagePath, temporaryManifestPath })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }

            Console.WriteLine($"Route OSM derivative complete: {writtenFeatures:N0} feature(s), {writtenParts:N0} polygon part(s), {writtenHoles:N0} inner-ring hole(s).");
            Console.WriteLine($"Saved route-local TSRE vector coverage: {outputPath}");
            Console.WriteLine($"Saved reusable categorized route coverage: {geopackagePath}");
            Console.WriteLine($"Saved route geodata manifest: {manifestPath}");
        }

        public void Dispose()
        {
            foreach (WoodlandSource source in woodlandSources)
            {
                source.ProjectedGeometry.Dispose();
            }

            foreach (RouteVectorFeature feature in routeVectorFeatures)
            {
                feature.Geometry.Dispose();
            }

            exclusionPolygons.Dispose();
            exactCoverageGeographic.Dispose();
            bufferedCoverageGeographic.Dispose();
            toProjected.Dispose();
            toGeographic.Dispose();
            if (!promoted)
            {
                string temporaryPath = outputPath + ".tmp";
                foreach (string path in new[]
                {
                    temporaryPath,
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

        private void WriteHeader(Utf8JsonWriter writer)
        {
            FileInfo pbf = new(pbfPath);
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteStartObject("source");
            writer.WriteString("pbfRelativePath", Path.GetRelativePath(
                GetRouteOsmDirectory(routeDirectory), pbf.FullName).Replace('\\', '/'));
            writer.WriteNumber("pbfSizeBytes", pbf.Length);
            writer.WriteString("pbfModifiedUtc", pbf.LastWriteTimeUtc);
            writer.WriteEndObject();
            writer.WriteStartObject("routeCoverage");
            writer.WriteNumber("worldTileCount", worldTiles.Count);
            writer.WriteString("worldTileFingerprint", coverageFingerprint);
            writer.WriteNumber("bufferMetres", CoverageBufferMetres);
            writer.WriteString("crs", "EPSG:4326");
            writer.WriteEndObject();
            writer.WriteStartArray("features");
        }

        private static void WriteFeature(
            Utf8JsonWriter writer,
            WoodlandSource source,
            Geometry geometry,
            double originalArea,
            double plantableArea)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "Feature");
            if (!string.IsNullOrWhiteSpace(source.Id))
            {
                writer.WriteString("id", source.Id);
            }

            writer.WriteStartObject("properties");
            foreach ((string name, string value) in source.Properties)
            {
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
            if (natural == "wood" || landuse is "forest" or "wood") layers.Add("habitat_woodland");
            if (natural == "scrub") layers.Add("habitat_scrub");
            if (natural == "heath") layers.Add("habitat_heath");
            if (natural == "grassland" || landuse is "grass" or "meadow" or "pasture") layers.Add("habitat_grassland");
            if (natural == "wetland") layers.Add("habitat_wetland");
            if (natural is "water" or "bay" or "strait" || landuse is "reservoir" or "basin" || !string.IsNullOrEmpty(NormalizedField(feature, "water"))) layers.Add("water_polygons");

            string building = NormalizedField(feature, "building");
            if (!string.IsNullOrEmpty(building) && building is not "no" and not "false") layers.Add("buildings");
            if (IsDevelopedLand(feature)) layers.Add("developed_land");
            if (IsAgriculture(feature)) layers.Add("agriculture");
            if (natural is "beach" or "sand" or "bare_rock" or "scree") layers.Add("bare_ground");
            if (layers.Count == 0)
            {
                return;
            }

            bool exact = layers.Any(layer => layer.StartsWith("habitat_", StringComparison.Ordinal) || layer is "agriculture" or "bare_ground");
            using Geometry? clippedExact = exact ? geometry.Intersection(exactCoverageGeographic) : null;
            using Geometry? clippedBuffered = layers.Any(layer => !layer.StartsWith("habitat_", StringComparison.Ordinal) && layer is not "agriculture" and not "bare_ground")
                ? geometry.Intersection(bufferedCoverageGeographic)
                : null;
            Dictionary<string, string> properties = RouteProperties(feature, "multipolygon");
            foreach (string layer in layers.Distinct(StringComparer.Ordinal))
            {
                bool useExact = layer.StartsWith("habitat_", StringComparison.Ordinal) || layer is "agriculture" or "bare_ground";
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

        private void WriteRouteGeodataManifest(string path, IReadOnlyDictionary<string, int> layerCounts)
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
                sourcePbf = new
                {
                    relativePath = Path.GetRelativePath(GetRouteOsmDirectory(routeDirectory), pbf.FullName).Replace('\\', '/'),
                    sizeBytes = pbf.Length,
                    modifiedUtc = pbf.LastWriteTimeUtc,
                    sha256 = pbfSha256,
                },
                coverage = new
                {
                    worldTileCount = worldTiles.Count,
                    worldTileFingerprint = coverageFingerprint,
                    bufferMetres = CoverageBufferMetres,
                    crs = "EPSG:4326",
                },
                output = new
                {
                    geopackage = "route-geodata.gpkg",
                    compatibilityForestCache = "forest-polygons.geojson",
                },
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

        private static void ValidateRouteGeodataManifest(string path, IReadOnlyDictionary<string, int> expectedCounts)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
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
                string recordedPath = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(manifestPath)!,
                    source.GetProperty("relativePath").GetString()!.Replace('/', Path.DirectorySeparatorChar)));
                return root.GetProperty("schemaVersion").GetInt32() == SchemaVersion &&
                    string.Equals(recordedPath, pbf.FullName, StringComparison.OrdinalIgnoreCase) &&
                    source.GetProperty("sizeBytes").GetInt64() == pbf.Length &&
                    source.GetProperty("modifiedUtc").GetDateTime().ToUniversalTime() == pbf.LastWriteTimeUtc &&
                    string.Equals(coverage.GetProperty("worldTileFingerprint").GetString(), fingerprint, StringComparison.OrdinalIgnoreCase);
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
            string leisure = NormalizedField(feature, "leisure");
            return leisure is "recreation_ground" or "pitch" or "golf_course" or "playground" or "sports_centre" or "stadium" or "cemetery";
        }

        private static bool IsAgriculture(Feature feature) =>
            NormalizedField(feature, "landuse") is "farmland" or "farmyard" or "farm" or "orchard" or "vineyard" or "meadow" or "allotments" or "plant_nursery" or "greenhouse_horticulture";

        private static bool IsForestDerivativeCurrent(string outputPath, string sourcePbfPath, string fingerprint)
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
                    string.Equals(coverage.GetProperty("worldTileFingerprint").GetString(), fingerprint, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                return false;
            }
        }

        internal static bool IsCurrentForProbe(string outputPath, string sourcePbfPath, string fingerprint) =>
            IsForestDerivativeCurrent(outputPath, sourcePbfPath, fingerprint);

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
            File.WriteAllBytes(Path.Combine(tilesDirectory, RouteLayout.TileNameFromTileXZ(tileX, tileZ) + ".t"), [0]);
            File.WriteAllText(Path.Combine(worldDirectory, WorldFileName(tileX, tileZ)), "SIMISA@@@@@@@@@@JINX0w0t______");
            if (!RouteLayout.TryLoad(routeDirectory, out RouteLayout? route, out string error) || route is null)
            {
                throw new InvalidOperationException("could not construct derivative probe route: " + error);
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

            using DataSource source = Ogr.Open(sourcePath, 0)
                ?? throw new InvalidDataException("synthetic OSM source could not be reopened");
            using RouteForestDerivativeBuilder builder = new(route, mapper, sourcePath, CancellationToken.None);
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
            string forestPath = Path.Combine(osmDirectory, "forest-polygons.geojson");
            string geopackagePath = Path.Combine(osmDirectory, "route-geodata.gpkg");
            string manifestPath = Path.Combine(osmDirectory, "route-geodata.json");
            if (!File.Exists(forestPath) || !File.Exists(geopackagePath) || !File.Exists(manifestPath))
            {
                throw new InvalidDataException("synthetic extraction did not promote all route derivatives");
            }
            using JsonDocument forest = JsonDocument.Parse(File.ReadAllText(forestPath));
            if (forest.RootElement.GetProperty("features").GetArrayLength() != 1)
            {
                throw new InvalidDataException("synthetic forest derivative feature count is incorrect");
            }
            using DataSource package = Ogr.Open(geopackagePath, 0)
                ?? throw new InvalidDataException("synthetic route GeoPackage could not be reopened");
            using Layer woodland = package.GetLayerByName("habitat_woodland")
                ?? throw new InvalidDataException("synthetic woodland layer is missing");
            using Layer roads = package.GetLayerByName("roads")
                ?? throw new InvalidDataException("synthetic roads layer is missing");
            if (woodland.GetFeatureCount(1) != 1 || roads.GetFeatureCount(1) != 1)
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
                foreach (string name in new[] { "osm_id", "name", "natural", "landuse", "other_tags" })
                {
                    using FieldDefn field = new(name, FieldType.OFTString);
                    polygons.CreateField(field, 1);
                }
                using Geometry ring = new(wkbGeometryType.wkbLinearRing);
                ring.AddPoint_2D(centerLon - lonSpan, centerLat - latSpan);
                ring.AddPoint_2D(centerLon + lonSpan, centerLat - latSpan);
                ring.AddPoint_2D(centerLon + lonSpan, centerLat + latSpan);
                ring.AddPoint_2D(centerLon - lonSpan, centerLat + latSpan);
                ring.AddPoint_2D(centerLon - lonSpan, centerLat - latSpan);
                using Geometry polygon = new(wkbGeometryType.wkbPolygon);
                polygon.AddGeometry(ring);
                using Geometry multipolygon = new(wkbGeometryType.wkbMultiPolygon);
                multipolygon.AddGeometry(polygon);
                using Feature feature = new(polygons.GetLayerDefn());
                feature.SetField("osm_id", "relation/1");
                feature.SetField("name", "Probe Wood");
                feature.SetField("natural", "wood");
                feature.SetGeometry(multipolygon);
                if (polygons.CreateFeature(feature) != 0) throw new InvalidOperationException("could not create synthetic woodland");
            }
            using (Layer lines = dataSource.CreateLayer("lines", geographic, wkbGeometryType.wkbMultiLineString, [])!)
            {
                foreach (string name in new[] { "osm_id", "name", "highway", "other_tags" })
                {
                    using FieldDefn field = new(name, FieldType.OFTString);
                    lines.CreateField(field, 1);
                }
                using Geometry line = new(wkbGeometryType.wkbLineString);
                line.AddPoint_2D(centerLon - lonSpan, centerLat);
                line.AddPoint_2D(centerLon + lonSpan, centerLat);
                using Geometry multiline = new(wkbGeometryType.wkbMultiLineString);
                multiline.AddGeometry(line);
                using Feature feature = new(lines.GetLayerDefn());
                feature.SetField("osm_id", "way/2");
                feature.SetField("name", "Probe Road");
                feature.SetField("highway", "residential");
                feature.SetGeometry(multiline);
                if (lines.CreateFeature(feature) != 0) throw new InvalidOperationException("could not create synthetic road");
            }
        }

        private static void ValidateForestDerivative(string path, string expectedFingerprint, int expectedFeatures)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (!string.Equals(root.GetProperty("type").GetString(), "FeatureCollection", StringComparison.Ordinal) ||
                root.GetProperty("schemaVersion").GetInt32() != SchemaVersion ||
                !string.Equals(root.GetProperty("routeCoverage").GetProperty("worldTileFingerprint").GetString(), expectedFingerprint, StringComparison.OrdinalIgnoreCase) ||
                root.GetProperty("features").GetArrayLength() != expectedFeatures)
            {
                throw new InvalidDataException("route OSM derivative failed validation");
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

        private static string GetWorldTileFingerprint(IEnumerable<WorldTile> tiles)
        {
            string text = string.Join('\n', tiles
                .DistinctBy(tile => (tile.X, tile.Z))
                .OrderBy(tile => tile.Z)
                .ThenBy(tile => tile.X)
                .Select(tile => $"{tile.X},{tile.Z}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }

        private static bool IsWoodland(Feature feature)
        {
            string natural = NormalizedField(feature, "natural");
            string landuse = NormalizedField(feature, "landuse");
            return natural == "wood" || landuse is "forest" or "wood";
        }

        private static Dictionary<string, string> WoodlandProperties(Feature feature)
        {
            Dictionary<string, string> properties = new(StringComparer.OrdinalIgnoreCase);
            foreach (string name in new[] { "name", "natural", "landuse", "leaf_type", "leaf_cycle", "wood", "species", "other_tags" })
            {
                string value = RawField(feature, name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    properties[name] = value;
                }
            }

            return properties;
        }

        private static bool IsExcludedPolygon(Feature feature)
        {
            string building = NormalizedField(feature, "building");
            if (!string.IsNullOrEmpty(building) && building is not "no" and not "false") return true;
            if (ExcludedNatural.Contains(NormalizedField(feature, "natural"))) return true;
            if (ExcludedLanduse.Contains(NormalizedField(feature, "landuse"))) return true;
            if (ExcludedLeisure.Contains(NormalizedField(feature, "leisure"))) return true;
            if (ExcludedAmenity.Contains(NormalizedField(feature, "amenity"))) return true;
            if (!string.IsNullOrEmpty(NormalizedField(feature, "aeroway"))) return true;
            if (!string.IsNullOrEmpty(NormalizedField(feature, "military"))) return true;
            return NormalizedField(feature, "man_made") is "wastewater_plant" or "water_works";
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
            int index = feature.GetFieldIndex(name);
            return index < 0 || !feature.IsFieldSetAndNotNull(index)
                ? ""
                : (feature.GetFieldAsString(index) ?? "").Trim();
        }

        private static string NormalizedField(Feature feature, string name) =>
            RawField(feature, name).ToLowerInvariant();

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
    }
}
