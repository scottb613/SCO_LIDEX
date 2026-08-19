// SCO LIDEX - Open Rails / MSTS Cloud Terrain Builder
// Copyright (C) Scott Brunner, Beast of Burden
// Route, marker, KML, text-file, and track-database terrain selection.
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
{
    private static void EnsureMarkerCoverageTiles(
        string routeDir,
        int terrainRadius,
        bool hd4mOutput)
    {
        if (string.IsNullOrWhiteSpace(routeDir) || !Directory.Exists(routeDir))
        {
            throw new DirectoryNotFoundException($"route directory does not exist: {routeDir}");
        }

        string routeName = new DirectoryInfo(routeDir).Name;
        string markerPath = Path.Combine(routeDir, routeName + ".mkr");
        if (!File.Exists(markerPath))
        {
            throw new FileNotFoundException($"could not find route marker file: {markerPath}");
        }

        terrainRadius = Math.Max(0, terrainRadius);
        List<RouteMarker> markers = ReadCoverageMarkers(markerPath);
        if (markers.Count == 0)
        {
            throw new InvalidOperationException($"marker file does not contain any Marker entries: {markerPath}");
        }

        HashSet<TileCoordinate> coverage = BuildMarkerNormalTileCoverage(markers, terrainRadius);
        EnsureNormalCoverageTiles(routeDir, coverage, $"Marker coverage: {markers.Count:N0} markers, radius {Math.Max(0, terrainRadius):N0}", hd4mOutput);
    }

    private static void EnsureTrackDatabaseCoverageTiles(
        string routeDir,
        int terrainRadius,
        bool hd4mOutput)
    {
        HashSet<TileCoordinate> trackTiles = ReadTrackDatabaseTileCoordinates(routeDir);
        if (trackTiles.Count == 0)
        {
            throw new InvalidOperationException("track database does not contain any readable TileX/TileZ references.");
        }

        HashSet<TileCoordinate> coverage = ExpandNormalTileCoverage(trackTiles, terrainRadius);
        EnsureNormalCoverageTiles(routeDir, coverage, $"Track database coverage: {trackTiles.Count:N0} track tile(s), radius {Math.Max(0, terrainRadius):N0}", hd4mOutput);
    }

    private static void EnsureKmlCoverageTiles(
        string routeDir,
        int terrainRadius,
        bool hd4mOutput)
    {
        HashSet<TileCoordinate> kmlTiles = ReadKmlTileCoordinates(routeDir);
        if (kmlTiles.Count == 0)
        {
            throw new InvalidOperationException("KML file did not produce any terrain tile coordinates.");
        }

        HashSet<TileCoordinate> coverage = ExpandNormalTileCoverage(kmlTiles, terrainRadius);
        EnsureNormalCoverageTiles(routeDir, coverage, $"KML coverage: {kmlTiles.Count:N0} tile(s), radius {Math.Max(0, terrainRadius):N0}", hd4mOutput);
    }

    private static void EnsureTextFileCoverageTiles(
        string routeDir,
        bool hd4mOutput)
    {
        HashSet<TileCoordinate> textTiles = ReadTextFileTileCoordinates(routeDir);
        if (textTiles.Count == 0)
        {
            throw new InvalidOperationException("SCOLIDEXTiles.txt did not contain any readable terrain tile names.");
        }

        EnsureNormalCoverageTiles(routeDir, textTiles, $"Text file coverage: {textTiles.Count:N0} exact tile(s)", hd4mOutput);
    }

    private static void EnsureNormalCoverageTiles(
        string routeDir,
        HashSet<TileCoordinate> coverage,
        string description,
        bool hd4mOutput)
    {
        FileInfo templateTile = FindTerrainTileTemplate(routeDir)
            ?? throw new FileNotFoundException("could not find a terrain .t template in generated-tiles or the route tiles folder");

        string tilesDir = Path.Combine(routeDir, "tiles");
        string worldDir = Path.Combine(routeDir, "world");
        Directory.CreateDirectory(tilesDir);
        Directory.CreateDirectory(worldDir);

        int createdTerrainTiles = 0;
        int createdRawGrids = 0;
        int createdWorldFiles = 0;
        foreach (TileCoordinate tile in coverage.OrderBy(t => t.X).ThenBy(t => t.Z))
        {
            string tileBaseName = RouteLayout.TileNameFromTileXZ(tile.X, tile.Z);
            string tilePath = Path.Combine(tilesDir, tileBaseName + ".t");
            string rawPath = Path.Combine(tilesDir, tileBaseName + "_y.raw");
            string worldPath = Path.Combine(worldDir, WorldFileName(tile.X, tile.Z));

            EnsureExactFileNameCasing(tilePath);
            EnsureExactFileNameCasing(rawPath);
            if (!File.Exists(tilePath))
            {
                byte[] tileBytes = CreateTerrainTileFromTemplate(templateTile, tileBaseName);
                PatchTerrainResolutionMetadata(
                    tileBytes,
                    hd4mOutput ? ExperimentalRawGridSize : OrtsRawGridSize,
                    hd4mOutput
                        ? (float)ExperimentalPostSpacingMeters
                        : (float)OrtsPostSpacingMeters);
                File.WriteAllBytes(tilePath, tileBytes);
                createdTerrainTiles++;
            }

            if (!File.Exists(rawPath))
            {
                File.WriteAllBytes(
                    rawPath,
                    CreateEmptyRawGridBytes(
                        hd4mOutput ? ExperimentalRawGridSize : OrtsRawGridSize));
                createdRawGrids++;
            }

            if (!File.Exists(worldPath))
            {
                File.WriteAllText(worldPath, "SIMISA@@@@@@@@@@JINX0w0t______\r\n\r\nTr_Worldfile (\r\n)\r\n", Encoding.ASCII);
                createdWorldFiles++;
            }
        }

        Console.WriteLine(
            $"{description}, {coverage.Count:N0} total normal tiles. " +
            $"Created {createdTerrainTiles:N0} .t, {createdRawGrids:N0} raw, {createdWorldFiles:N0} world files.");

        int indexedTerrainTiles = WriteTsreNormalTerrainIndex(routeDir, tilesDir);
        Console.WriteLine($"Normal terrain: rebuilt TSRE terrain index with {indexedTerrainTiles:N0} tile(s).");
    }

    private static int WriteTsreNormalTerrainIndex(string routeDir, string tilesDir)
    {
        List<TileCoordinate> terrainTiles = EnumerateTsreNormalTerrainTiles(tilesDir).ToList();
        if (terrainTiles.Count == 0)
        {
            return 0;
        }

        string tdDir = Path.Combine(routeDir, "td");
        Directory.CreateDirectory(tdDir);

        TsreTerrainQuadTree quadTree = new(targetLevel: 1, indexFileName: "td_idx.dat", tileFileExtension: ".td");
        foreach (TileCoordinate tile in terrainTiles)
        {
            quadTree.AddTile(tile.X, tile.Z);
        }

        quadTree.Save(tdDir);
        return terrainTiles.Count;
    }

    private static IEnumerable<TileCoordinate> EnumerateTsreNormalTerrainTiles(string tilesDir)
    {
        if (!Directory.Exists(tilesDir))
        {
            yield break;
        }

        foreach (FileInfo tileFile in new DirectoryInfo(tilesDir).EnumerateFiles("-*.t"))
        {
            if (RouteLayout.TryDecodeTileName(tileFile.Name, out TileCoordinate tile))
            {
                yield return tile;
            }
        }
    }

    private static IReadOnlyList<TerrainTile> GetRouteTileProcessingList(
        RouteLayout route,
        bool markerCoverage,
        bool trackDatabaseCoverage,
        bool kmlCoverage,
        bool textFileCoverage,
        int terrainRadius)
    {
        IEnumerable<TerrainTile> tiles = route.TerrainTiles;
        HashSet<TileCoordinate>? coverage = null;
        string? coverageName = null;
        GeoTileMapper? mapper = route.TsreProjection is null ? null : GeoTileMapper.TryCreate(route);
        if (markerCoverage)
        {
            coverage = BuildMarkerNormalTileCoverage(route.Markers, terrainRadius, mapper);
            coverageName = "Marker";
        }
        else if (trackDatabaseCoverage)
        {
            HashSet<TileCoordinate> trackTiles = ReadTrackDatabaseTileCoordinates(route.RouteDir);
            coverage = ExpandNormalTileCoverage(trackTiles, terrainRadius);
            coverageName = "Track database";
            Console.WriteLine($"Track database source: {trackTiles.Count:N0} track tile coordinate(s).");
        }
        else if (kmlCoverage)
        {
            HashSet<TileCoordinate> kmlTiles = ReadKmlTileCoordinates(route.RouteDir, mapper);
            coverage = ExpandNormalTileCoverage(kmlTiles, terrainRadius);
            coverageName = "KML";
            Console.WriteLine($"KML source: {kmlTiles.Count:N0} tile coordinate(s).");
        }
        else if (textFileCoverage)
        {
            HashSet<TileCoordinate> textTiles = ReadTextFileTileCoordinates(route.RouteDir);
            coverage = textTiles;
            coverageName = "Text file";
            Console.WriteLine($"Text file source: {textTiles.Count:N0} tile coordinate(s).");
        }

        if (coverage is not null)
        {
            tiles = tiles.Where(tile =>
                tile.WorldTile is not null &&
                coverage.Contains(new TileCoordinate(tile.WorldTile.X, tile.WorldTile.Z)));
            Console.WriteLine($"{coverageName} terrain processing filter: {coverage.Count:N0} covered normal tile coordinate(s).");
        }

        return tiles
            .OrderBy(t => t.WorldTile?.Z ?? int.MaxValue)
            .ThenBy(t => t.WorldTile?.X ?? int.MaxValue)
            .ThenBy(t => t.TileFile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HashSet<TileCoordinate> BuildMarkerNormalTileCoverage(IEnumerable<RouteMarker> markers, int terrainRadius, GeoTileMapper? mapper = null)
    {
        HashSet<TileCoordinate> centerTiles = [];
        foreach (RouteMarker marker in markers)
        {
            centerTiles.Add(mapper?.GetTileCoordinateForLonLatProjected(marker.Longitude, marker.Latitude)
                ?? GeoTileMapper.GetTileCoordinateForLonLat(marker.Longitude, marker.Latitude));
        }

        return ExpandNormalTileCoverage(centerTiles, terrainRadius);
    }

    private static HashSet<TileCoordinate> ExpandNormalTileCoverage(IEnumerable<TileCoordinate> centerTiles, int terrainRadius)
    {
        terrainRadius = Math.Max(0, terrainRadius);
        HashSet<TileCoordinate> coverage = [];
        foreach (TileCoordinate center in centerTiles)
        {
            for (int dz = -terrainRadius; dz <= terrainRadius; dz++)
            {
                for (int dx = -terrainRadius; dx <= terrainRadius; dx++)
                {
                    coverage.Add(new TileCoordinate(center.X + dx, center.Z + dz));
                }
            }
        }

        return coverage;
    }

    private static HashSet<TileCoordinate> ReadTrackDatabaseTileCoordinates(string routeDir)
    {
        string tdbPath = FindRouteDataFile(routeDir, ".tdb");
        if (string.IsNullOrWhiteSpace(tdbPath) || !File.Exists(tdbPath))
        {
            throw new FileNotFoundException($"could not find a .tdb track database file in {routeDir}");
        }

        string text = File.ReadAllText(tdbPath);
        HashSet<TileCoordinate> tiles = [];

        foreach (Match match in TrackVectorSectionRegex().Matches(text))
        {
            AddTrackTileCandidate(tiles, match.Groups["tileX"].Value, match.Groups["tileZ"].Value);
            AddTrackTileCandidate(tiles, match.Groups["worldX"].Value, match.Groups["worldZ"].Value);
        }

        foreach (Match match in TrackUidRegex().Matches(text))
        {
            AddTrackTileCandidate(tiles, match.Groups["tileX"].Value, match.Groups["tileZ"].Value);
            AddTrackTileCandidate(tiles, match.Groups["worldX"].Value, match.Groups["worldZ"].Value);
        }

        foreach (Match match in TrackItemRDataRegex().Matches(text))
        {
            AddTrackTileCandidate(tiles, match.Groups["tileX"].Value, match.Groups["tileZ"].Value);
        }

        Console.WriteLine($"Track database: read {tiles.Count:N0} tile coordinate(s) from {Path.GetFileName(tdbPath)}.");
        return tiles;
    }

    private static void AddTrackTileCandidate(HashSet<TileCoordinate> tiles, string tileXText, string tileZText)
    {
        if (!int.TryParse(tileXText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tileX) ||
            !int.TryParse(tileZText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tileZ))
        {
            return;
        }

        if (tileX == 0 && tileZ == 0)
        {
            return;
        }

        tiles.Add(new TileCoordinate(tileX, tileZ));
    }

    private static string FindRouteDataFile(string routeDir, string extension)
    {
        string routeName = new DirectoryInfo(routeDir).Name;
        string preferredPath = Path.Combine(routeDir, routeName + extension);
        if (File.Exists(preferredPath))
        {
            return preferredPath;
        }

        return Directory.GetFiles(routeDir, "*" + extension).FirstOrDefault() ?? "";
    }

    private static HashSet<TileCoordinate> ReadTextFileTileCoordinates(string routeDir)
    {
        string tileListPath = Path.Combine(routeDir, "SCOLIDEXTiles.txt");
        if (!File.Exists(tileListPath))
        {
            string legacyTileListPath = Path.Combine(routeDir, "SCOtopoTiles.txt");
            if (!File.Exists(legacyTileListPath))
            {
                throw new FileNotFoundException($"could not find SCOLIDEXTiles.txt in {routeDir}");
            }

            tileListPath = legacyTileListPath;
        }

        HashSet<TileCoordinate> tiles = [];
        int lineNumber = 0;
        foreach (string rawLine in File.ReadLines(tileListPath))
        {
            lineNumber++;
            string line = rawLine.Split('#')[0].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParseTextTileLine(line, out TileCoordinate tile))
            {
                tiles.Add(tile);
                continue;
            }

            if (line.StartsWith('_'))
            {
                Console.WriteLine($"Text file: ignored unsupported underscore tile line {lineNumber}: {rawLine}");
            }
            else
            {
                Console.WriteLine($"Text file: ignored unreadable tile line {lineNumber}: {rawLine}");
            }
        }

        Console.WriteLine($"Text file: read {tiles.Count:N0} tile coordinate(s) from {Path.GetFileName(tileListPath)}.");
        return tiles;
    }

    private static bool TryParseTextTileLine(string line, out TileCoordinate tile)
    {
        tile = default;
        string value = line.Trim().Trim('"');
        if (value.EndsWith(".t", StringComparison.OrdinalIgnoreCase))
        {
            value = Path.GetFileNameWithoutExtension(value);
        }

        if (RouteLayout.TryDecodeTileName(value, out tile))
        {
            return true;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
        {
            tile = new TileCoordinate(x, z);
            return true;
        }

        return false;
    }

    private static HashSet<TileCoordinate> ReadKmlTileCoordinates(string routeDir, GeoTileMapper? mapper = null)
    {
        string routeName = new DirectoryInfo(routeDir).Name;
        string kmlPath = Path.Combine(routeDir, routeName + ".kml");

        if (!File.Exists(kmlPath))
        {
            throw new FileNotFoundException($"could not find route KML file: {kmlPath}");
        }

        XDocument doc = XDocument.Load(kmlPath);
        HashSet<TileCoordinate> tiles = [];
        foreach (XElement coordinates in doc.Descendants().Where(e => e.Name.LocalName.Equals("coordinates", StringComparison.OrdinalIgnoreCase)))
        {
            List<(double Lon, double Lat)> points = ParseKmlCoordinateText(coordinates.Value).ToList();
            if (points.Count == 0)
            {
                continue;
            }

            AddKmlPointTile(tiles, points[0], mapper);
            for (int i = 1; i < points.Count; i++)
            {
                AddKmlLineTiles(tiles, points[i - 1], points[i], mapper);
            }
        }

        Console.WriteLine($"KML: read {tiles.Count:N0} tile coordinate(s) from {Path.GetFileName(kmlPath)}.");
        return tiles;
    }

    private static IEnumerable<(double Lon, double Lat)> ParseKmlCoordinateText(string text)
    {
        foreach (string tuple in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = tuple.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat))
            {
                yield return (lon, lat);
            }
        }
    }

    private static void AddKmlPointTile(HashSet<TileCoordinate> tiles, (double Lon, double Lat) point, GeoTileMapper? mapper = null)
    {
        tiles.Add(mapper?.GetTileCoordinateForLonLatProjected(point.Lon, point.Lat)
            ?? GeoTileMapper.GetTileCoordinateForLonLat(point.Lon, point.Lat));
    }

    private static void AddKmlLineTiles(HashSet<TileCoordinate> tiles, (double Lon, double Lat) start, (double Lon, double Lat) end, GeoTileMapper? mapper = null)
    {
        double maxDelta = Math.Max(Math.Abs(end.Lon - start.Lon), Math.Abs(end.Lat - start.Lat));
        int steps = Math.Max(1, (int)Math.Ceiling(maxDelta / 0.002));
        for (int step = 0; step <= steps; step++)
        {
            double t = step / (double)steps;
            AddKmlPointTile(tiles, (
                start.Lon + ((end.Lon - start.Lon) * t),
                start.Lat + ((end.Lat - start.Lat) * t)),
                mapper);
        }
    }

    private static List<RouteMarker> ReadCoverageMarkers(string markerPath)
    {
        List<RouteMarker> markers = [];
        foreach (string line in File.ReadLines(markerPath))
        {
            Match match = CoverageMarkerRegex().Match(line);
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

    private static FileInfo? FindTerrainTileTemplate(string routeDir)
    {
        FileInfo? generatedTemplate = FindGeneratedTerrainTileTemplate();
        if (generatedTemplate is not null)
        {
            return generatedTemplate;
        }

        DirectoryInfo routeTiles = new(Path.Combine(routeDir, "tiles"));
        return routeTiles.Exists
            ? routeTiles.EnumerateFiles("*.t").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;
    }

    private static byte[] CreateTerrainTileFromTemplate(FileInfo templateTile, string tileBaseName)
    {
        string templateBaseName = Path.GetFileNameWithoutExtension(templateTile.Name);
        if (templateBaseName.Length != tileBaseName.Length)
        {
            throw new InvalidOperationException($"template tile name {templateBaseName} cannot be rewritten as {tileBaseName}");
        }

        byte[] bytes = File.ReadAllBytes(templateTile.FullName);
        ReplaceBytesInPlace(bytes, Encoding.ASCII.GetBytes(templateBaseName), Encoding.ASCII.GetBytes(tileBaseName));
        ReplaceBytesInPlace(bytes, Encoding.Unicode.GetBytes(templateBaseName), Encoding.Unicode.GetBytes(tileBaseName));
        return bytes;
    }

    private static void ReplaceBytesInPlace(byte[] haystack, byte[] needle, byte[] replacement)
    {
        if (needle.Length == 0 || needle.Length != replacement.Length)
        {
            return;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            Buffer.BlockCopy(replacement, 0, haystack, i, replacement.Length);
            i += needle.Length - 1;
        }
    }

    private static byte[] CreateEmptyRawGridBytes()
    {
        return CreateEmptyRawGridBytes(OrtsRawGridSize);
    }

    private static void MarkTerrainTileForAppendRetry(
        TerrainTile tile,
        int gridSize = OrtsRawGridSize)
    {
        string rawPath = tile.RawHeightPath
            ?? Path.Combine(
                tile.TileFile.DirectoryName ?? "",
                Path.GetFileNameWithoutExtension(tile.TileFile.Name).ToLowerInvariant() + "_y.raw");

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(rawPath) ?? ".");
            EnsureExactFileNameCasing(rawPath);
            File.WriteAllBytes(rawPath, CreateEmptyRawGridBytes(gridSize));
            Console.WriteLine("  -> Marked tile raw grid as missing so a later Append will retry it.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  -> Could not mark tile for Append retry: {ex.Message}");
        }
    }

    private static string WorldFileName(int tileX, int tileZ)
    {
        return string.Create(CultureInfo.InvariantCulture, $"w{tileX:+000000;-000000}{tileZ:+000000;-000000}.w");
    }

    // TSRE-style distant mountains are normal route tiles grouped into 16x16
    // coverage blocks. They use lower-resolution DEM data but are edge-merged
    // the same way as normal tiles before the TSRE low-terrain index is rebuilt.
}
