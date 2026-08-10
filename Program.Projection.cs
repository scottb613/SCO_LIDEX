// SCO LIDEX - Open Rails / MSTS Cloud Terrain Builder
// Copyright (C) Scott Brunner, Beast of Burden
// Route discovery, tile geography, and MSTS/Open Rails/TSRE coordinate projection.
// SCO LIDEX is distributed under GNU GPL v3 or later. See LICENSE.txt.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MaxRev.Gdal.Core;
using OSGeo.GDAL;
using OSGeo.OGR;
using OSGeo.OSR;

namespace ORterr;

internal static partial class Program
{    // Route parser and tile-name bridge. This connects route markers, world
    // files, terrain files, raw grids, RouteStart, and optional TSRE projection
    // data into one structure the builder can reason about.
    private sealed partial class RouteLayout
    {
        private RouteLayout(
            string routeDir,
            TileCoordinate? startTile,
            TsreGeoProjection? tsreProjection,
            IReadOnlyList<RouteMarker> markers,
            IReadOnlyList<WorldTile> worldTiles,
            IReadOnlyList<TerrainTile> terrainTiles)
        {
            RouteDir = routeDir;
            StartTile = startTile;
            TsreProjection = tsreProjection;
            Markers = markers;
            WorldTiles = worldTiles;
            TerrainTiles = terrainTiles;
        }

        public string RouteDir { get; }

        public TileCoordinate? StartTile { get; }

        public TsreGeoProjection? TsreProjection { get; }

        public IReadOnlyList<RouteMarker> Markers { get; }

        public IReadOnlyList<WorldTile> WorldTiles { get; }

        public IReadOnlyList<TerrainTile> TerrainTiles { get; }

        public static bool TryLoad(string routeDir, out RouteLayout? route, out string error)
        {
            route = null;

            if (string.IsNullOrWhiteSpace(routeDir) || !Directory.Exists(routeDir))
            {
                error = $"Error: route directory does not exist: {routeDir}";
                return false;
            }

            string tilesDir = Path.Combine(routeDir, "tiles");
            string worldDir = Path.Combine(routeDir, "world");

            string routeName = new DirectoryInfo(routeDir).Name;
            string trkPath = Path.Combine(routeDir, routeName + ".trk");
            if (!File.Exists(trkPath))
            {
                trkPath = Directory.GetFiles(routeDir, "*.trk").FirstOrDefault() ?? "";
            }

            if (!File.Exists(trkPath))
            {
                error = $"Error: could not find a .trk route file in {routeDir}";
                return false;
            }

            if (!Directory.Exists(tilesDir))
            {
                error = $"Error: could not find tiles folder at {tilesDir}";
                return false;
            }

            if (!Directory.Exists(worldDir))
            {
                error = $"Error: could not find world folder at {worldDir}";
                return false;
            }

            TileCoordinate? startTile;
            TsreGeoProjection? tsreProjection;
            IReadOnlyList<RouteMarker> markers;
            IReadOnlyList<WorldTile> worldTiles;
            IReadOnlyList<TerrainTile> terrainTiles;
            try
            {
                string trkText = File.ReadAllText(trkPath);
                startTile = ParseRouteStart(trkText);
                tsreProjection = ParseTsreGeoProjection(trkText);
                markers = ReadMarkers(routeDir, routeName);
                worldTiles = ReadWorldTiles(worldDir);
                terrainTiles = ReadTerrainTiles(tilesDir, worldTiles);
            }
            catch (Exception ex)
            {
                error = $"Error: failed while reading route files: {ex.Message}";
                return false;
            }

            if (terrainTiles.Count == 0)
            {
                error = $"Error: no terrain .t files found in {tilesDir}";
                return false;
            }

            if (worldTiles.Count == 0)
            {
                error = $"Error: no world .w files found in {worldDir}";
                return false;
            }

            route = new RouteLayout(routeDir, startTile, tsreProjection, markers, worldTiles, terrainTiles);
            error = "";
            return true;
        }

        private static TileCoordinate? ParseRouteStart(string trkText)
        {
            Match match = RouteStartRegex().Match(trkText);
            if (!match.Success)
            {
                return null;
            }

            return new TileCoordinate(
                int.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture));
        }

        private static TsreGeoProjection? ParseTsreGeoProjection(string trkText)
        {
            Match match = TsreGeoProjectionRegex().Match(trkText);
            if (!match.Success)
            {
                return null;
            }

            return new TsreGeoProjection(
                double.Parse(match.Groups["lat"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["lon"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture));
        }

        private static IReadOnlyList<RouteMarker> ReadMarkers(string routeDir, string routeName)
        {
            string markerPath = Path.Combine(routeDir, routeName + ".mkr");
            if (!File.Exists(markerPath))
            {
                return [];
            }

            List<RouteMarker> markers = [];
            foreach (string line in File.ReadLines(markerPath))
            {
                Match match = MarkerRegex().Match(line);
                if (!match.Success)
                {
                    continue;
                }

                markers.Add(new RouteMarker(
                    double.Parse(match.Groups["lon"].Value, CultureInfo.InvariantCulture),
                    double.Parse(match.Groups["lat"].Value, CultureInfo.InvariantCulture),
                    match.Groups["name"].Value.Trim()));
            }

            return markers;
        }

        private static IReadOnlyList<WorldTile> ReadWorldTiles(string worldDir)
        {
            List<WorldTile> tiles = [];
            foreach (FileInfo file in new DirectoryInfo(worldDir).EnumerateFiles("w*.w"))
            {
                Match match = WorldFileRegex().Match(file.Name);
                if (!match.Success)
                {
                    continue;
                }

                tiles.Add(new WorldTile(
                    int.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups["z"].Value, CultureInfo.InvariantCulture),
                    file));
            }

            return tiles;
        }

        private static IReadOnlyList<TerrainTile> ReadTerrainTiles(string tilesDir, IReadOnlyList<WorldTile> worldTiles)
        {
            List<TerrainTile> tiles = [];
            foreach (FileInfo tileFile in new DirectoryInfo(tilesDir).EnumerateFiles("*.t"))
            {
                tiles.Add(new TerrainTile(tileFile, FindRawHeightPath(tileFile), FindMatchingWorldTile(tileFile, worldTiles)));
            }

            return tiles.OrderBy(t => t.TileFile.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static WorldTile? FindMatchingWorldTile(FileInfo tileFile, IReadOnlyList<WorldTile> worldTiles)
        {
            string tileName = Path.GetFileNameWithoutExtension(tileFile.Name);
            WorldTile? decodedTile = worldTiles.FirstOrDefault(
                worldTile => string.Equals(
                    TileNameFromTileXZ(worldTile.X, worldTile.Z),
                    tileName,
                    StringComparison.OrdinalIgnoreCase));
            if (decodedTile is not null)
            {
                return decodedTile;
            }

            if (TryDecodeTileName(tileFile.Name, out TileCoordinate decodedCoordinate))
            {
                FileInfo worldFile = worldTiles.FirstOrDefault(w => w.X == decodedCoordinate.X && w.Z == decodedCoordinate.Z)?.File
                    ?? new FileInfo(Path.Combine(tileFile.DirectoryName ?? "", WorldFileName(decodedCoordinate.X, decodedCoordinate.Z)));
                return new WorldTile(decodedCoordinate.X, decodedCoordinate.Z, worldFile);
            }

            return null;
        }

        public static string TileNameFromTileXZ(int tileX, int tileZ)
        {
            const int zoom = 15;
            const string hex = "0123456789ABCDEF";
            int rectX = -16384;
            int rectZ = -16384;
            int rectW = 16384;
            int rectH = 16384;
            StringBuilder name = new("-");
            int partial = 0;

            for (int z = 0; z < zoom; z++)
            {
                bool east = tileX >= rectX + rectW;
                bool north = tileZ >= rectZ + rectH;
                partial <<= 2;
                partial += (north ? 0 : 2) + (east ^ north ? 0 : 1);
                if (z % 2 == 1)
                {
                    name.Append(hex[partial]);
                    partial = 0;
                }

                if (east)
                {
                    rectX += rectW;
                }

                if (north)
                {
                    rectZ += rectH;
                }

                rectW /= 2;
                rectH /= 2;
            }

            name.Append(hex[partial << 2]);
            return name.ToString().ToLowerInvariant();
        }

        public static bool TryDecodeTileName(string tileName, out TileCoordinate tile)
        {
            const int zoom = 15;
            const string hex = "0123456789ABCDEF";
            string key = Path.GetFileNameWithoutExtension(tileName).TrimStart('-', '_').ToUpperInvariant();
            tile = default;
            if (key.Length != 8 || key.Any(c => !Uri.IsHexDigit(c)))
            {
                return false;
            }

            int rectX = -16384;
            int rectZ = -16384;
            int rectW = 16384;
            int rectH = 16384;
            for (int level = 0; level < zoom; level++)
            {
                int hexValue = hex.IndexOf(key[level / 2]);
                int quadrant = level % 2 == 0 ? hexValue >> 2 : hexValue & 0b11;
                if (!TryDecodeTileQuadrant(quadrant, out bool east, out bool north))
                {
                    return false;
                }

                if (east)
                {
                    rectX += rectW;
                }

                if (north)
                {
                    rectZ += rectH;
                }

                rectW /= 2;
                rectH /= 2;
            }

            tile = new TileCoordinate(rectX, rectZ);
            return true;
        }

        private static bool TryDecodeTileQuadrant(int quadrant, out bool east, out bool north)
        {
            for (int eastValue = 0; eastValue <= 1; eastValue++)
            {
                for (int northValue = 0; northValue <= 1; northValue++)
                {
                    bool candidateEast = eastValue == 1;
                    bool candidateNorth = northValue == 1;
                    int candidate = (candidateNorth ? 0 : 2) + (candidateEast ^ candidateNorth ? 0 : 1);
                    if (candidate == quadrant)
                    {
                        east = candidateEast;
                        north = candidateNorth;
                        return true;
                    }
                }
            }

            east = false;
            north = false;
            return false;
        }

        private static string? FindRawHeightPath(FileInfo tileFile)
        {
            string conventionalRawPath = Path.Combine(
                tileFile.DirectoryName ?? "",
                Path.GetFileNameWithoutExtension(tileFile.Name) + "_y.raw");
            if (File.Exists(conventionalRawPath))
            {
                return conventionalRawPath;
            }

            byte[] bytes = File.ReadAllBytes(tileFile.FullName);
            string unicodeText = Encoding.Unicode.GetString(bytes).Replace("\0", "");
            Match match = RawHeightRegex().Match(unicodeText);
            if (!match.Success)
            {
                string shiftedUnicodeText = Encoding.Unicode.GetString(bytes, 1, bytes.Length - 1).Replace("\0", "");
                match = RawHeightRegex().Match(shiftedUnicodeText);
            }

            if (!match.Success)
            {
                string asciiText = Encoding.ASCII.GetString(bytes).Replace("\0", "");
                match = RawHeightRegex().Match(asciiText);
            }

            return match.Success ? Path.Combine(tileFile.DirectoryName ?? "", match.Value) : null;
        }

        [GeneratedRegex(@"RouteStart\s*\(\s*(?<x>-?\d+)\s+(?<z>-?\d+)")]
        private static partial Regex RouteStartRegex();

        [GeneratedRegex(@"TsreGeoProjection\s*\(\s*(?<lat>-?\d+(?:\.\d+)?)\s+(?<lon>-?\d+(?:\.\d+)?)\s+(?<x>-?\d+(?:\.\d+)?)\s+(?<z>-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
        private static partial Regex TsreGeoProjectionRegex();

        [GeneratedRegex(@"Marker\s*\(\s*(?<lon>-?\d+(?:\.\d+)?)\s+(?<lat>-?\d+(?:\.\d+)?)\s+(?<name>[^)]*)\)")]
        private static partial Regex MarkerRegex();

        [GeneratedRegex(@"w(?<x>[+-]\d+)(?<z>[+-]\d+)\.w", RegexOptions.IgnoreCase)]
        private static partial Regex WorldFileRegex();

        [GeneratedRegex(@"-?[0-9a-f]{8}_y\.raw", RegexOptions.IgnoreCase)]
        private static partial Regex RawHeightRegex();
    }

    private sealed record TerrainTile(FileInfo TileFile, string? RawHeightPath, WorldTile? WorldTile);

    private sealed record WorldTile(int X, int Z, FileInfo File);

    private readonly record struct TileCoordinate(int X, int Z);

    private readonly record struct LoTileCoordinate(int X, int Z);

    private sealed record RouteMarker(double Longitude, double Latitude, string Name);

    private sealed record TsreGeoProjection(double CenterLat, double CenterLon, double CenterX, double CenterZ);

    // Converts ORTS world-tile coordinates into geographic sample grids. This is
    // the heart of terrain alignment: it handles standard MSTS/Open Rails Goode
    // homolosine mapping and TSRE's optional route-centered projection.
    private sealed class GeoTileMapper
    {
        private const double EarthRadiusMeters = 6_370_997.0;
        private const double LegacyUpperLeftGoodeX = -20_013_965.0;
        private const double LegacyUpperLeftGoodeY = 8_674_008.0;
        private const double DefaultTerrainPlacementEastMeters = 16.0;
        private const double DefaultTerrainPlacementNorthMeters = -16.0;
        // TSRE's map overlay uses a slightly different IGH image origin than
        // the older ORTS WorldLatLon converter. Sampling west/north by the
        // opposite amount places generated terrain 16 m east and 16 m south.
        private const double UpperLeftGoodeX =
            LegacyUpperLeftGoodeX - DefaultTerrainPlacementEastMeters;
        private const double UpperLeftGoodeY =
            LegacyUpperLeftGoodeY - DefaultTerrainPlacementNorthMeters;
        private const double WorldTileEastWestOffset = -16_385.0;
        private const double WorldTileNorthSouthOffset = 16_385.0;
        private const double Epsilon = 0.0000000001;
        private static readonly double[] LongitudeCenters =
        [
            -1.74532925199, -1.74532925199, 0.523598775598, 0.523598775598,
            -2.79252680319, -1.0471975512, -2.79252680319, -1.0471975512,
            0.349065850399, 2.44346095279, 0.349065850399, 2.44346095279,
        ];

        private readonly int minTileX;
        private readonly int minTileZ;
        private readonly TsreGeoProjection? tsreProjection;
        private readonly double tsreStepLat;
        private readonly double tsreStepLon;

        private GeoTileMapper(
            int minTileX,
            int minTileZ,
            TsreGeoProjection? tsreProjection)
        {
            this.minTileX = minTileX;
            this.minTileZ = minTileZ;
            this.tsreProjection = tsreProjection;
            if (tsreProjection is not null)
            {
                double centerLatRad = tsreProjection.CenterLat * Math.PI / 180.0;
                tsreStepLat = 111132.92 - (559.82 * Math.Cos(2 * centerLatRad)) + (1.175 * Math.Cos(4 * centerLatRad)) - (0.0023 * Math.Cos(6 * centerLatRad));
                tsreStepLon = (111412.84 * Math.Cos(centerLatRad)) - (93.5 * Math.Cos(3 * centerLatRad));
            }
        }

        public string ProjectionName => tsreProjection is null
            ? "MSTS/Open Rails interrupted Goode homolosine world-tile projection"
            : "TSRE route-centered projection";

        public string ProjectionDetail => tsreProjection is null
            ? $"TsreGeoProjection not detected; using standard MSTS/Open Rails tile geography with the TSRE map compatibility correction: terrain east={DefaultTerrainPlacementEastMeters:F0}m, south={-DefaultTerrainPlacementNorthMeters:F0}m."
            : $"TsreGeoProjection detected: center lat={tsreProjection.CenterLat:F6}, lon={tsreProjection.CenterLon:F6}, X={tsreProjection.CenterX:F0}, Z={tsreProjection.CenterZ:F0}; meters/degree lat={tsreStepLat:F3}, lon={tsreStepLon:F3}.";

        public double MinLon { get; private set; }

        public double MinLat { get; private set; }

        public double MaxLon { get; private set; }

        public double MaxLat { get; private set; }

        public static GeoTileMapper? TryCreate(RouteLayout route)
        {
            if (route.WorldTiles.Count == 0)
            {
                return null;
            }

            List<WorldTile> coverageTiles = route.TerrainTiles
                .Select(t => t.WorldTile)
                .Where(t => t is not null)
                .Select(t => t!)
                .DistinctBy(t => (t.X, t.Z))
                .ToList();
            if (coverageTiles.Count == 0)
            {
                coverageTiles = route.WorldTiles.ToList();
            }

            int minX = coverageTiles.Min(t => t.X);
            int minZ = coverageTiles.Min(t => t.Z);
            GeoTileMapper mapper = new(minX, minZ, route.TsreProjection);
            List<(double Lon, double Lat)> corners = [];
            foreach (WorldTile tile in coverageTiles)
            {
                mapper.AddTileCorners(corners, tile.X, tile.Z, 1, 1);
            }

            mapper.SetBounds(corners);
            return mapper;
        }

        private void SetBounds(List<(double Lon, double Lat)> corners)
        {
            MinLon = corners.Min(c => c.Lon);
            MinLat = corners.Min(c => c.Lat);
            MaxLon = corners.Max(c => c.Lon);
            MaxLat = corners.Max(c => c.Lat);
        }

        public (double MinLon, double MinLat, double MaxLon, double MaxLat) GetBoundingBox(
            WorldTile tile,
            double sourceOffsetX = 0,
            double sourceOffsetZ = 0,
            double sourceScaleX = 1,
            double sourceScaleZ = 1)
        {
            double centerTileX = minTileX + ((tile.X - minTileX) * sourceScaleX) + sourceOffsetX;
            double centerTileZ = minTileZ + ((tile.Z - minTileZ) * sourceScaleZ) + sourceOffsetZ;
            List<(double Lon, double Lat)> corners = [];
            AddTileCorners(corners, centerTileX, centerTileZ, sourceScaleX, sourceScaleZ);
            return (corners.Min(c => c.Lon), corners.Min(c => c.Lat), corners.Max(c => c.Lon), corners.Max(c => c.Lat));
        }

        public GeoSampleGrid GetSampleGrid(
            WorldTile tile,
            double sourceOffsetX = 0,
            double sourceOffsetZ = 0,
            double sourceScaleX = 1,
            double sourceScaleZ = 1,
            double sourceBiasEastMeters = 0,
            double sourceBiasNorthMeters = 0)
        {
            return GetSampleGridForResolution(
                tile,
                OrtsRawGridSize,
                OrtsPostSpacingMeters,
                sourceOffsetX,
                sourceOffsetZ,
                sourceScaleX,
                sourceScaleZ,
                sourceBiasEastMeters,
                sourceBiasNorthMeters);
        }

        public GeoSampleGrid GetSampleGridForResolution(
            WorldTile tile,
            int sampleCount,
            double sampleSpacingMeters,
            double sourceOffsetX = 0,
            double sourceOffsetZ = 0,
            double sourceScaleX = 1,
            double sourceScaleZ = 1,
            double sourceBiasEastMeters = 0,
            double sourceBiasNorthMeters = 0)
        {
            double centerTileX = minTileX + ((tile.X - minTileX) * sourceScaleX) + sourceOffsetX;
            double centerTileZ = minTileZ + ((tile.Z - minTileZ) * sourceScaleZ) + sourceOffsetZ;
            double[,] longitudes = new double[sampleCount, sampleCount];
            double[,] latitudes = new double[sampleCount, sampleCount];
            double minLon = double.PositiveInfinity;
            double minLat = double.PositiveInfinity;
            double maxLon = double.NegativeInfinity;
            double maxLat = double.NegativeInfinity;
            double halfX = OrtsTileSizeMeters * sourceScaleX / 2.0;
            double halfZ = OrtsTileSizeMeters * sourceScaleZ / 2.0;
            double postSpacingX = sampleSpacingMeters * sourceScaleX;
            double postSpacingZ = sampleSpacingMeters * sourceScaleZ;

            for (int y = 0; y < sampleCount; y++)
            {
                double localZ = halfZ - (postSpacingZ * y);
                for (int x = 0; x < sampleCount; x++)
                {
                    double localX = -halfX + (postSpacingX * x);
                    (double lon, double lat) = ConvertWorldTileCoordinate(
                        centerTileX,
                        centerTileZ,
                        localX - sourceBiasEastMeters,
                        localZ - sourceBiasNorthMeters);
                    longitudes[y, x] = lon;
                    latitudes[y, x] = lat;
                    minLon = Math.Min(minLon, lon);
                    minLat = Math.Min(minLat, lat);
                    maxLon = Math.Max(maxLon, lon);
                    maxLat = Math.Max(maxLat, lat);
                }
            }

            return new GeoSampleGrid(longitudes, latitudes, (minLon, minLat, maxLon, maxLat));
        }

        public static TileCoordinate GetTileCoordinateForLonLat(double longitudeDegrees, double latitudeDegrees)
        {
            double lon = longitudeDegrees * Math.PI / 180.0;
            double lat = latitudeDegrees * Math.PI / 180.0;
            (double goodeX, double goodeY) = ForwardGoode(lat, lon);

            double tileXContinuous = ((goodeX - UpperLeftGoodeX) / OrtsTileSizeMeters) + 1.0 + WorldTileEastWestOffset;
            double tileZContinuous = WorldTileNorthSouthOffset - (((UpperLeftGoodeY - goodeY) / OrtsTileSizeMeters) + 1.0);

            return new TileCoordinate(
                (int)Math.Floor(tileXContinuous + 0.5),
                (int)Math.Floor(tileZContinuous + 0.5));
        }

        public TileCoordinate GetTileCoordinateForLonLatProjected(double longitudeDegrees, double latitudeDegrees)
        {
            if (tsreProjection is null)
            {
                return GetTileCoordinateForLonLat(longitudeDegrees, latitudeDegrees);
            }

            double line = (latitudeDegrees - tsreProjection.CenterLat) * tsreStepLat;
            double sample = (longitudeDegrees - tsreProjection.CenterLon) * tsreStepLon;
            double tileX = (sample + tsreProjection.CenterX) / OrtsTileSizeMeters;
            double tileZ = (line + tsreProjection.CenterZ) / OrtsTileSizeMeters;
            return new TileCoordinate((int)Math.Floor(tileX), (int)Math.Floor(tileZ));
        }

        // Converts a geographic OSM vertex into the exact local-meter frame used
        // by terrain sampling. Pixel X grows east and pixel Y grows south, which
        // matches TSRE's terrain-map UV assignment without a bbox approximation.
        public (double X, double Y) ProjectToTilePixel(
            WorldTile tile,
            double longitudeDegrees,
            double latitudeDegrees,
            int imageSize)
        {
            double localX;
            double localZ;
            if (tsreProjection is null)
            {
                double lon = longitudeDegrees * Math.PI / 180.0;
                double lat = latitudeDegrees * Math.PI / 180.0;
                (double goodeX, double goodeY) = ForwardGoode(lat, lon);
                double centerSample = tile.X - WorldTileEastWestOffset;
                double centerLine = WorldTileNorthSouthOffset - tile.Z;
                double centerX = UpperLeftGoodeX + ((centerSample - 1.0) * OrtsTileSizeMeters);
                double centerY = UpperLeftGoodeY - ((centerLine - 1.0) * OrtsTileSizeMeters);
                localX = goodeX - centerX;
                localZ = goodeY - centerY;
            }
            else
            {
                double sample = (longitudeDegrees - tsreProjection.CenterLon) * tsreStepLon;
                double line = (latitudeDegrees - tsreProjection.CenterLat) * tsreStepLat;
                localX = sample + tsreProjection.CenterX - (OrtsTileSizeMeters * (tile.X + 0.5));
                localZ = line + tsreProjection.CenterZ - (OrtsTileSizeMeters * (tile.Z + 0.5));
            }

            return (
                ((localX + (OrtsTileSizeMeters / 2.0)) / OrtsTileSizeMeters) * imageSize,
                (((OrtsTileSizeMeters / 2.0) - localZ) / OrtsTileSizeMeters) * imageSize);
        }

        public static GeoSampleGrid GetSampleGridForWorldArea(
            double centerTileX,
            double centerTileZ,
            double widthMeters,
            double heightMeters,
            int gridSize,
            double sampleOffsetX = 0,
            double sampleOffsetY = 0,
            double sourceBiasEastMeters = 0,
            double sourceBiasNorthMeters = 0)
        {
            double[,] longitudes = new double[gridSize, gridSize];
            double[,] latitudes = new double[gridSize, gridSize];
            double minLon = double.PositiveInfinity;
            double minLat = double.PositiveInfinity;
            double maxLon = double.NegativeInfinity;
            double maxLat = double.NegativeInfinity;
            double halfX = widthMeters / 2.0;
            double halfZ = heightMeters / 2.0;
            double sampleSpacingX = widthMeters / (gridSize - 1);
            double sampleSpacingZ = heightMeters / (gridSize - 1);
            double localOffsetX = sampleOffsetX * sampleSpacingX;
            double localOffsetZ = sampleOffsetY * sampleSpacingZ;

            for (int y = 0; y < gridSize; y++)
            {
                double localZ = halfZ - (heightMeters * y / (gridSize - 1)) + localOffsetZ;
                for (int x = 0; x < gridSize; x++)
                {
                    double localX = -halfX + (widthMeters * x / (gridSize - 1)) + localOffsetX;
                    (double lon, double lat) = ConvertGoodeWorldTileCoordinate(
                        centerTileX,
                        centerTileZ,
                        localX - sourceBiasEastMeters,
                        localZ - sourceBiasNorthMeters);
                    longitudes[y, x] = lon;
                    latitudes[y, x] = lat;
                    minLon = Math.Min(minLon, lon);
                    minLat = Math.Min(minLat, lat);
                    maxLon = Math.Max(maxLon, lon);
                    maxLat = Math.Max(maxLat, lat);
                }
            }

            return new GeoSampleGrid(longitudes, latitudes, (minLon, minLat, maxLon, maxLat));
        }

        public GeoSampleGrid GetAreaSampleGrid(
            double centerTileX,
            double centerTileZ,
            double widthMeters,
            double heightMeters,
            int gridSize,
            double sampleOffsetX = 0,
            double sampleOffsetY = 0,
            double sourceBiasEastMeters = 0,
            double sourceBiasNorthMeters = 0)
        {
            double[,] longitudes = new double[gridSize, gridSize];
            double[,] latitudes = new double[gridSize, gridSize];
            double minLon = double.PositiveInfinity;
            double minLat = double.PositiveInfinity;
            double maxLon = double.NegativeInfinity;
            double maxLat = double.NegativeInfinity;
            double halfX = widthMeters / 2.0;
            double halfZ = heightMeters / 2.0;
            double sampleSpacingX = widthMeters / (gridSize - 1);
            double sampleSpacingZ = heightMeters / (gridSize - 1);
            double localOffsetX = sampleOffsetX * sampleSpacingX;
            double localOffsetZ = sampleOffsetY * sampleSpacingZ;

            for (int y = 0; y < gridSize; y++)
            {
                double localZ = halfZ - (heightMeters * y / (gridSize - 1)) + localOffsetZ;
                for (int x = 0; x < gridSize; x++)
                {
                    double localX = -halfX + (widthMeters * x / (gridSize - 1)) + localOffsetX;
                    (double lon, double lat) = ConvertWorldTileCoordinate(
                        centerTileX,
                        centerTileZ,
                        localX - sourceBiasEastMeters,
                        localZ - sourceBiasNorthMeters);
                    longitudes[y, x] = lon;
                    latitudes[y, x] = lat;
                    minLon = Math.Min(minLon, lon);
                    minLat = Math.Min(minLat, lat);
                    maxLon = Math.Max(maxLon, lon);
                    maxLat = Math.Max(maxLat, lat);
                }
            }

            return new GeoSampleGrid(longitudes, latitudes, (minLon, minLat, maxLon, maxLat));
        }

        private void AddTileCorners(List<(double Lon, double Lat)> corners, double tileX, double tileZ, double scaleX, double scaleZ)
        {
            double halfX = OrtsTileSizeMeters * scaleX / 2.0;
            double halfZ = OrtsTileSizeMeters * scaleZ / 2.0;
            corners.Add(ConvertWorldTileCoordinate(tileX, tileZ, -halfX, -halfZ));
            corners.Add(ConvertWorldTileCoordinate(tileX, tileZ, -halfX, halfZ));
            corners.Add(ConvertWorldTileCoordinate(tileX, tileZ, halfX, -halfZ));
            corners.Add(ConvertWorldTileCoordinate(tileX, tileZ, halfX, halfZ));
        }

        private (double Lon, double Lat) ConvertWorldTileCoordinate(double tileX, double tileZ, double localX, double localZ)
        {
            return tsreProjection is null
                ? ConvertGoodeWorldTileCoordinate(tileX, tileZ, localX, localZ)
                : ConvertTsreWorldTileCoordinate(tileX, tileZ, localX, localZ);
        }

        private (double Lon, double Lat) ConvertTsreWorldTileCoordinate(double tileX, double tileZ, double localX, double localZ)
        {
            TsreGeoProjection projection = tsreProjection!;
            double sample = (OrtsTileSizeMeters * (tileX + 0.5)) + localX - projection.CenterX;
            double line = (OrtsTileSizeMeters * (tileZ + 0.5)) + localZ - projection.CenterZ;
            return (
                projection.CenterLon + (sample / tsreStepLon),
                projection.CenterLat + (line / tsreStepLat));
        }

        private static (double Lon, double Lat) ConvertGoodeWorldTileCoordinate(double tileX, double tileZ, double localX, double localZ)
        {
            double goodeSample = tileX - WorldTileEastWestOffset;
            double goodeLine = WorldTileNorthSouthOffset - tileZ;
            double goodeX = UpperLeftGoodeX + ((goodeSample - 1.0) * OrtsTileSizeMeters) + localX;
            double goodeY = UpperLeftGoodeY - ((goodeLine - 1.0) * OrtsTileSizeMeters) + localZ;

            (double lat, double lon) = InverseGoode(goodeX, goodeY);
            return (lon * 180.0 / Math.PI, lat * 180.0 / Math.PI);
        }

        private static (double Lat, double Lon) InverseGoode(double goodeX, double goodeY)
        {
            int region = GetGoodeRegion(goodeX, goodeY);
            double falseEasting = EarthRadiusMeters * LongitudeCenters[region];
            goodeX -= falseEasting;

            double latitude;
            double longitude;
            switch (region)
            {
                case 1:
                case 3:
                case 4:
                case 5:
                case 8:
                case 9:
                    latitude = goodeY / EarthRadiusMeters;
                    double temp = Math.Abs(latitude) - (Math.PI / 2.0);
                    longitude = Math.Abs(temp) > Epsilon
                        ? AdjustLongitude(LongitudeCenters[region] + (goodeX / (EarthRadiusMeters * Math.Cos(latitude))))
                        : LongitudeCenters[region];
                    break;
                default:
                    double arg = (goodeY + (0.0528035274542 * EarthRadiusMeters * Sign(goodeY))) / (1.4142135623731 * EarthRadiusMeters);
                    arg = Math.Clamp(arg, -1.0, 1.0);
                    double theta = Math.Asin(arg);
                    longitude = LongitudeCenters[region] + (goodeX / (0.900316316158 * EarthRadiusMeters * Math.Cos(theta)));
                    arg = ((2.0 * theta) + Math.Sin(2.0 * theta)) / Math.PI;
                    latitude = Math.Asin(Math.Clamp(arg, -1.0, 1.0));
                    break;
            }

            return (latitude, longitude);
        }

        private static (double X, double Y) ForwardGoode(double latitude, double longitude)
        {
            int region = GetGoodeRegionForLonLat(latitude, longitude);
            double adjustedLongitude = AdjustLongitude(longitude - LongitudeCenters[region]);

            switch (region)
            {
                case 1:
                case 3:
                case 4:
                case 5:
                case 8:
                case 9:
                    return (
                        (EarthRadiusMeters * LongitudeCenters[region]) + (EarthRadiusMeters * adjustedLongitude * Math.Cos(latitude)),
                        EarthRadiusMeters * latitude);
                default:
                    double theta = SolveMollweideTheta(latitude);
                    return (
                        (EarthRadiusMeters * LongitudeCenters[region]) +
                            (0.900316316158 * EarthRadiusMeters * adjustedLongitude * Math.Cos(theta)),
                        (1.4142135623731 * EarthRadiusMeters * Math.Sin(theta)) -
                            (0.0528035274542 * EarthRadiusMeters * Sign(latitude)));
            }
        }

        private static int GetGoodeRegionForLonLat(double latitude, double longitude)
        {
            const double transitionLatitude = 0.710987989993;
            const double minus100Degrees = -1.74532925199;
            const double minus40Degrees = -0.698131700798;
            const double minus20Degrees = -0.349065850399;
            const double plus80Degrees = 1.3962634016;

            if (latitude >= transitionLatitude)
            {
                return longitude <= minus40Degrees ? 0 : 2;
            }

            if (latitude >= 0)
            {
                return longitude <= minus40Degrees ? 1 : 3;
            }

            if (latitude >= -transitionLatitude)
            {
                if (longitude <= minus100Degrees)
                {
                    return 4;
                }

                if (longitude <= minus20Degrees)
                {
                    return 5;
                }

                return longitude <= plus80Degrees ? 8 : 9;
            }

            if (longitude <= minus100Degrees)
            {
                return 6;
            }

            if (longitude <= minus20Degrees)
            {
                return 7;
            }

            return longitude <= plus80Degrees ? 10 : 11;
        }

        private static double SolveMollweideTheta(double latitude)
        {
            double theta = latitude;
            double target = Math.PI * Math.Sin(latitude);
            for (int i = 0; i < 12; i++)
            {
                double numerator = (2.0 * theta) + Math.Sin(2.0 * theta) - target;
                double denominator = 2.0 + (2.0 * Math.Cos(2.0 * theta));
                if (Math.Abs(denominator) < Epsilon)
                {
                    break;
                }

                double delta = numerator / denominator;
                theta -= delta;
                if (Math.Abs(delta) < Epsilon)
                {
                    break;
                }
            }

            return theta;
        }

        private static int GetGoodeRegion(double goodeX, double goodeY)
        {
            if (goodeY >= EarthRadiusMeters * 0.710987989993)
            {
                return goodeX <= EarthRadiusMeters * -0.698131700798 ? 0 : 2;
            }

            if (goodeY >= 0)
            {
                return goodeX <= EarthRadiusMeters * -0.698131700798 ? 1 : 3;
            }

            if (goodeY >= EarthRadiusMeters * -0.710987989993)
            {
                if (goodeX <= EarthRadiusMeters * -1.74532925199)
                {
                    return 4;
                }

                if (goodeX <= EarthRadiusMeters * -0.349065850399)
                {
                    return 5;
                }

                return goodeX <= EarthRadiusMeters * 1.3962634016 ? 8 : 9;
            }

            if (goodeX <= EarthRadiusMeters * -1.74532925199)
            {
                return 6;
            }

            if (goodeX <= EarthRadiusMeters * -0.349065850399)
            {
                return 7;
            }

            return goodeX <= EarthRadiusMeters * 1.3962634016 ? 10 : 11;
        }

        private static double AdjustLongitude(double value)
        {
            return Math.Abs(value) > Math.PI ? value - (Sign(value) * 2.0 * Math.PI) : value;
        }

        private static double Sign(double value)
        {
            return value < 0 ? -1.0 : 1.0;
        }
    }
}
