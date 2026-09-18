// SCO LIDEX - bounded-memory OSM staging and GeoJSON reading.
// Copyright (C) Scott Brunner, Beast of Burden
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

using System.Text.Json;
using OSGeo.OGR;
using OSGeo.OSR;

namespace ORterr;

internal static partial class Program
{
    // Each cursor owns just its current feature. Spatial queries use the GPKG
    // R-tree; assembled fragments are indexed by source FID on disk.
    private sealed class OsmGeometryStage : IDisposable
    {
        private readonly string path;
        private readonly DataSource data;
        private readonly Dictionary<string, Layer> layers = new();
        private readonly Dictionary<string, FeatureDefn> definitions = new();
        private readonly Dictionary<string, int> counts = new();
        private int pending;
        private bool disposed;

        internal OsmGeometryStage(string path, SpatialReference projected, SpatialReference geographic)
        {
            this.path = path;
            data = Ogr.GetDriverByName("GPKG").CreateDataSource(path, [])
                ?? throw new IOException("Could not create OSM staging GeoPackage");
            try
            {
                foreach (string name in new[] { "sources", "masks", "exclusions", "vectors", "pieces" })
                {
                    Layer layer = data.CreateLayer(name, name == "vectors" ? geographic : projected,
                        wkbGeometryType.wkbUnknown, ["SPATIAL_INDEX=YES"])
                        ?? throw new IOException("Could not create OSM staging layer " + name);
                    layers.Add(name, layer);
                    counts.Add(name, 0);
                    using FieldDefn payload = new("payload", FieldType.OFTString);
                    using FieldDefn key = new("source_key", FieldType.OFTInteger64);
                    if (layer.CreateField(payload, 1) != 0 || layer.CreateField(key, 1) != 0)
                        throw new IOException("Could not create OSM staging fields");
                    definitions.Add(name, layer.GetLayerDefn());
                }
                using Layer? result = data.ExecuteSQL("CREATE INDEX pieces_source ON pieces(source_key)", null, "SQLITE");
                if (data.StartTransaction(0) != 0) throw new IOException("Could not start OSM staging transaction");
            }
            catch { Dispose(); throw; }
        }

        internal int Count(string name) => counts[name];

        internal void Add(string name, Geometry geometry, string payload = "", long key = 0)
        {
            using Feature feature = new(definitions[name]);
            feature.SetField("payload", payload);
            feature.SetField("source_key", key);
            if (feature.SetGeometry(geometry) != 0 || layers[name].CreateFeature(feature) != 0)
                throw new IOException("Could not stage OSM geometry in " + name);
            counts[name]++;
            if (++pending >= 2048) Flush();
        }

        internal void Flush()
        {
            if (data.CommitTransaction() != 0 || data.StartTransaction(0) != 0)
                throw new IOException("Could not checkpoint OSM staging data");
            pending = 0;
        }

        internal IEnumerable<(long Id, string Payload, Geometry Geometry)> Read(
            string name, Geometry? area = null, long? sourceKey = null)
        {
            Layer layer = layers[name];
            layer.SetSpatialFilter(area);
            if (layer.SetAttributeFilter(sourceKey is null ? null : $"source_key = {sourceKey.Value}") != 0)
                throw new IOException("Could not select staged OSM source pieces");
            layer.ResetReading();
            try
            {
                while (true)
                {
                    using Feature? feature = layer.GetNextFeature();
                    if (feature is null) break;
                    using Geometry? geometry = feature.GetGeometryRef();
                    if (geometry is not null)
                        yield return (feature.GetFID(), feature.GetFieldAsString("payload"), geometry);
                }
            }
            finally
            {
                layer.SetSpatialFilter(null);
                layer.SetAttributeFilter(null);
                layer.ResetReading();
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (FeatureDefn definition in definitions.Values) definition.Dispose();
            foreach (Layer layer in layers.Values) layer.Dispose();
            data.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    // Keeps only the largest individual JSON value, never the feature array.
    // Reader state survives arbitrary UTF-8, escape, and token boundaries.
    private sealed class StreamingJsonReader : IDisposable
    {
        private readonly FileStream stream;
        private byte[] buffer = new byte[65536];
        private int start, end;
        private bool final;
        private JsonReaderState state;

        internal StreamingJsonReader(string path) => stream = File.OpenRead(path);

        private bool Fill()
        {
            if (final) return false;
            int remaining = end - start;
            if (remaining == buffer.Length) Array.Resize(ref buffer, checked(buffer.Length * 2));
            buffer.AsSpan(start, remaining).CopyTo(buffer);
            start = 0;
            end = remaining;
            int count = stream.Read(buffer, end, buffer.Length - end);
            end += count;
            final = count == 0;
            return true;
        }

        internal bool Token(out JsonTokenType type, out string? name)
        {
            while (true)
            {
                Utf8JsonReader reader = new(buffer.AsSpan(start, end - start), final, state);
                if (reader.Read())
                {
                    type = reader.TokenType;
                    name = type == JsonTokenType.PropertyName ? reader.GetString() : null;
                    start += (int)reader.BytesConsumed;
                    state = reader.CurrentState;
                    return true;
                }
                start += (int)reader.BytesConsumed;
                state = reader.CurrentState;
                if (!Fill()) { type = default; name = null; return false; }
            }
        }

        internal JsonDocument? Value(bool allowEndArray = false)
        {
            while (true)
            {
                Utf8JsonReader reader = new(buffer.AsSpan(start, end - start), final, state);
                if (reader.Read())
                {
                    if (allowEndArray && reader.TokenType == JsonTokenType.EndArray)
                    {
                        start += (int)reader.BytesConsumed;
                        state = reader.CurrentState;
                        return null;
                    }
                    if (JsonDocument.TryParseValue(ref reader, out JsonDocument? value))
                    {
                        start += (int)reader.BytesConsumed;
                        state = reader.CurrentState;
                        return value;
                    }
                }
                if (!Fill()) throw new JsonException("Incomplete GeoJSON value");
            }
        }

        public void Dispose() => stream.Dispose();
    }

    private static (Dictionary<string, JsonElement> Header, int Count) ReadGeoJsonFeatures(
        string path, Action<JsonElement>? visit = null, CancellationToken cancellationToken = default)
    {
        using StreamingJsonReader reader = new(path);
        if (!reader.Token(out JsonTokenType token, out _) || token != JsonTokenType.StartObject)
            throw new JsonException("Expected GeoJSON collection");
        Dictionary<string, JsonElement> header = new(StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        int count = 0;
        bool closed = false;
        while (reader.Token(out token, out string? name))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token == JsonTokenType.EndObject) { closed = true; break; }
            if (token != JsonTokenType.PropertyName || name is null || !seen.Add(name))
                throw new JsonException("Invalid or duplicate GeoJSON collection property");
            if (name != "features")
            {
                using JsonDocument value = reader.Value()!;
                header.Add(name, value.RootElement.Clone());
                continue;
            }
            if (!reader.Token(out token, out _) || token != JsonTokenType.StartArray)
                throw new JsonException("Expected GeoJSON feature array");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using JsonDocument? feature = reader.Value(allowEndArray: true);
                if (feature is null) break;
                if (feature.RootElement.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Expected GeoJSON feature object");
                visit?.Invoke(feature.RootElement);
                count = checked(count + 1);
            }
        }
        if (!closed || !seen.Contains("features") || reader.Token(out _, out _))
            throw new JsonException("Incomplete or trailing GeoJSON collection data");
        return (header, count);
    }
}
