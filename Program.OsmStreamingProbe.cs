// SCO LIDEX - OSM memory, streaming, and spatial-stage regression probes.
// Copyright (C) Scott Brunner, Beast of Burden
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OSGeo.OGR;
using OSGeo.OSR;

namespace ORterr;

internal static partial class Program
{
    private static void RunOsmStreamingProbe()
    {
        string root = Path.Combine(Path.GetTempPath(), "SCOLIDEX-osm-streaming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string jsonPath = Path.Combine(root, "features.geojson");
            // Match the reported failing exclusion count and exercise buffers
            // larger than 64 KB, escaped quotes, and multibyte UTF-8 boundaries.
            const int featureCount = 1_050_849;
            using (FileStream stream = File.Create(jsonPath))
            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "FeatureCollection");
                writer.WriteStartArray("features");
                for (int i = 0; i < featureCount; i++)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", i);
                    writer.WriteString("label", i == 31 ? new string('x', 160_000) + "雪é\\\"" : "route \"feature\" 雪é");
                    writer.WriteStartObject("geometry");
                    writer.WriteString("type", "Polygon");
                    writer.WritePropertyName("coordinates");
                    writer.WriteRawValue("[[[0,0],[1,0],[1,1],[0,0]]]");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    if (i % 128 == 0) writer.Flush();
                }
                writer.WriteEndArray();
                writer.WriteString("afterFeatures", "header fields after the array are legal");
                writer.WriteEndObject();
            }
            GC.Collect();
            long baseline = Process.GetCurrentProcess().WorkingSet64;
            long peak = baseline;
            int visited = 0;
            var (header, count) = ReadGeoJsonFeatures(jsonPath, feature =>
            {
                if (feature.GetProperty("id").GetInt32() != visited++)
                    throw new InvalidDataException("Streaming reader skipped or duplicated a feature");
                if (visited == 32 && !feature.GetProperty("label").GetString()!.EndsWith("雪é\\\"", StringComparison.Ordinal))
                    throw new InvalidDataException("Streaming reader corrupted a split UTF-8/escaped value");
                if (visited % 10000 == 0) peak = Math.Max(peak, Process.GetCurrentProcess().WorkingSet64);
            });
            if (count != featureCount || visited != featureCount || !header.ContainsKey("afterFeatures"))
                throw new InvalidDataException("Streaming collection count/header is incorrect");
            if (peak - baseline > 256L * 1024 * 1024)
                throw new InvalidDataException("Streaming reader memory grew beyond the 256 MiB regression budget");
            Console.WriteLine($"Streaming GeoJSON: {count:N0} features, {new FileInfo(jsonPath).Length:N0} bytes; sampled working-set growth {FormatByteCount(peak - baseline)}.");

            foreach (string invalid in new[]
            {
                "{\"features\":[{\"id\":1}", "{\"features\":[{}]}",
                "{\"features\":[{}]} garbage", "{\"features\":[{},]}",
                "{\"features\":[1]}", "{\"features\":[],\"features\":[]}"
            })
            {
                // The second value is valid: verify the negative harness too.
                File.WriteAllText(jsonPath, invalid);
                bool rejected = false;
                try { ReadGeoJsonFeatures(jsonPath); }
                catch (JsonException) { rejected = true; }
                if (rejected == (invalid == "{\"features\":[{}]}"))
                    throw new InvalidDataException("Streaming malformed-input check failed");
            }
            File.WriteAllText(jsonPath, "{\"features\":[{}]}");
            using (CancellationTokenSource stop = new())
            {
                stop.Cancel();
                try
                {
                    ReadGeoJsonFeatures(jsonPath, cancellationToken: stop.Token);
                    throw new InvalidDataException("Streaming cancellation was ignored");
                }
                catch (OperationCanceledException) { }
            }

            ConfigureOsmRuntime();
            using SpatialReference srs = new("");
            srs.ImportFromEPSG(3857);
            string stagePath = Path.Combine(root, "stage.gpkg");
            using (OsmGeometryStage stage = new(stagePath, srs, srs))
            {
                for (int i = 0; i < 100_000; i++)
                {
                    using Geometry polygon = ProbeRectangle(i * 10, 0, i * 10 + 2, 2);
                    stage.Add("masks", polygon);
                    if (i % 10000 == 0) stage.Add("pieces", polygon, key: i / 10000);
                }
                stage.Flush();
                using Geometry area = ProbeRectangle(99, -1, 123, 3);
                if (stage.Count("masks") != 100_000 || stage.Read("masks", area).Count() != 3 ||
                    stage.Read("pieces", sourceKey: 3).Count() != 1 ||
                    stage.Read("pieces", sourceKey: 999).Any())
                    throw new InvalidDataException("Spatial or source-piece stage selection failed");
                // Early iterator disposal must release/reset the cursor.
                if (!stage.Read("masks", area).Take(1).Any() || stage.Read("masks", area).Count() != 3)
                    throw new InvalidDataException("Stage cursor was not reset after early termination");
            }
            if (Directory.GetFiles(root, "stage.gpkg*").Length != 0)
                throw new InvalidDataException("Temporary OSM stage was not cleaned up");
            Console.WriteLine("OSM streaming probe: PASSED (million-feature reader, malformed input, cancellation, 100,000 staged masks, spatial/source indexes, cleanup).");
        }
        finally
        {
            // root is a newly generated directory under the OS temp directory.
            Directory.Delete(root, recursive: true);
        }
    }

    private static Geometry ProbeRectangle(double minX, double minY, double maxX, double maxY)
    {
        using Geometry ring = new(wkbGeometryType.wkbLinearRing);
        ring.AddPoint_2D(minX, minY);
        ring.AddPoint_2D(maxX, minY);
        ring.AddPoint_2D(maxX, maxY);
        ring.AddPoint_2D(minX, maxY);
        ring.AddPoint_2D(minX, minY);
        Geometry polygon = new(wkbGeometryType.wkbPolygon);
        polygon.AddGeometry(ring);
        return polygon;
    }

    private static void RunMapWindowProbe(RouteLayout route, GeoTileMapper mapper, string cachePath, string sourcePath)
    {
        using var allData = OSGeo.GDAL.Gdal.OpenEx(cachePath,
            (uint)(OSGeo.GDAL.GdalConst.OF_VECTOR | OSGeo.GDAL.GdalConst.OF_READONLY), null, null, null);
        List<OsmPrimitive> all = LoadOsmGeometry(allData, route, mapper, route.TerrainTiles,
            sourcePath, false, CancellationToken.None, buildDerivative: false);
        Parallel.ForEach(route.TerrainTiles, new ParallelOptions { MaxDegreeOfParallelism = 2 }, tile =>
        {
            using var tileData = OSGeo.GDAL.Gdal.OpenEx(cachePath,
                (uint)(OSGeo.GDAL.GdalConst.OF_VECTOR | OSGeo.GDAL.GdalConst.OF_READONLY), null, null, null);
            List<OsmPrimitive> local = LoadOsmGeometry(tileData, route, mapper, [tile],
                sourcePath, false, CancellationToken.None, buildDerivative: false);
            using Bitmap expected = RenderMapBitmap(all, mapper, tile.WorldTile!, CancellationToken.None, out _);
            using Bitmap actual = RenderMapBitmap(local, mapper, tile.WorldTile!, CancellationToken.None, out _);
            using MemoryStream expectedPng = new();
            using MemoryStream actualPng = new();
            expected.Save(expectedPng, ImageFormat.Png);
            actual.Save(actualPng, ImageFormat.Png);
            if (!SHA256.HashData(expectedPng.ToArray()).SequenceEqual(SHA256.HashData(actualPng.ToArray())))
                throw new InvalidDataException("Per-tile geometry changed a rendered map compared with route-wide geometry");
        });
        Console.WriteLine("Map window probe: PASSED (concurrent per-tile PNGs match route-wide reference).");
    }
}
