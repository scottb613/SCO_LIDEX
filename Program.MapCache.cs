// Copyright (C) Scott Brunner, Beast of Burden
// Route-local OpenStreetMap cache manifests, AppData cache registry, and safe
// cache discovery/purge support.
// SCO LIDEX is distributed under GNU GPL v3 or later. See LICENSE.txt.

using System.Text;
using System.Text.Json;

namespace ORterr;

internal static partial class Program
{
    private const int OsmCacheSchemaVersion = 1;
    private const string RouteOsmDirectoryName = "OpenStreetMap";
    private const string RouteOsmProviderDirectoryName = "geofabrik";
    private const string RouteOsmManifestFileName = "osm-cache.json";
    private const string MapCacheRegistryFileName = "cache-registry-v1.json";

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    internal enum MapCacheKind
    {
        LegacyRegionalExtract,
        LegacyPartialDownload,
        RouteRegionalExtract,
        RoutePartialDownload,
    }

    internal sealed record MapCacheEntry(
        string Id,
        string Name,
        string Owner,
        string Location,
        int FileCount,
        long SizeBytes,
        MapCacheKind Kind,
        string ManagedRoot,
        IReadOnlyList<string> Files);

    private sealed record OsmCacheManifest(
        int SchemaVersion,
        string Provider,
        string RegionId,
        string RegionName,
        string DownloadUrl,
        string RelativePbfPath,
        long SizeBytes,
        DateTimeOffset? SourceModifiedUtc,
        DateTimeOffset DownloadedUtc,
        double MinLongitude,
        double MinLatitude,
        double MaxLongitude,
        double MaxLatitude,
        string CreatedByVersion);

    private sealed record OsmCacheRegistry(int SchemaVersion, List<OsmCacheRegistryEntry> Routes);

    private sealed record OsmCacheRegistryEntry(
        string RoutePath,
        string ManifestPath,
        string RegionId,
        string RegionName,
        DateTimeOffset LastSeenUtc);

    private static string GetMapSettingsDirectory() =>
        AppContext.GetData("SCOLIDEX.MapCacheSettingsRoot") as string
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCOLIDEX");

    private static string GetMapCacheDirectory() =>
        Path.Combine(GetMapSettingsDirectory(), "map-data");

    private static string GetMapCacheRegistryPath() =>
        Path.Combine(GetMapSettingsDirectory(), MapCacheRegistryFileName);

    private static string GetRouteOsmDirectory(string routeDir) =>
        Path.Combine(routeDir, RouteOsmDirectoryName);

    private static string GetRouteOsmProviderDirectory(string routeDir) =>
        Path.Combine(GetRouteOsmDirectory(routeDir), RouteOsmProviderDirectoryName);

    private static string GetRouteOsmManifestPath(string routeDir) =>
        Path.Combine(GetRouteOsmDirectory(routeDir), RouteOsmManifestFileName);

    private static string GetRouteGeofabrikExtractPath(string routeDir, string regionId) =>
        Path.Combine(GetRouteOsmProviderDirectory(routeDir), GetSafeRegionFileName(regionId) + ".osm.pbf");

    private static string GetLegacyGeofabrikExtractPath(string regionId) =>
        Path.Combine(GetMapCacheDirectory(), GetSafeRegionFileName(regionId) + ".osm.pbf");

    private static string GetSafeRegionFileName(string regionId) =>
        System.Text.RegularExpressions.Regex.Replace(regionId, "[^a-zA-Z0-9._-]", "-");

    private static bool IsUsableCacheFile(string path, long expectedSize = 0)
    {
        try
        {
            FileInfo file = new(path);
            return file.Exists && file.Length > 0 && (expectedSize <= 0 || file.Length == expectedSize);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadRouteOsmManifest(
        string routeDir,
        out OsmCacheManifest? manifest,
        out string? pbfPath)
    {
        manifest = null;
        pbfPath = null;
        try
        {
            string manifestPath = GetRouteOsmManifestPath(routeDir);
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            manifest = JsonSerializer.Deserialize<OsmCacheManifest>(
                File.ReadAllText(manifestPath),
                CacheJsonOptions);
            if (manifest is null ||
                manifest.SchemaVersion != OsmCacheSchemaVersion ||
                !string.Equals(manifest.Provider, "Geofabrik", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(manifest.RelativePbfPath))
            {
                manifest = null;
                return false;
            }

            string osmRoot = Path.GetFullPath(GetRouteOsmDirectory(routeDir));
            string candidate = Path.GetFullPath(Path.Combine(
                osmRoot,
                manifest.RelativePbfPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathInside(candidate, osmRoot) ||
                !candidate.EndsWith(".osm.pbf", StringComparison.OrdinalIgnoreCase) ||
                !IsUsableCacheFile(candidate, manifest.SizeBytes))
            {
                manifest = null;
                return false;
            }

            pbfPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            manifest = null;
            pbfPath = null;
            return false;
        }
    }

    private static void WriteRouteOsmManifest(string routeDir, GeofabrikRegion region, string pbfPath)
    {
        string osmRoot = Path.GetFullPath(GetRouteOsmDirectory(routeDir));
        string fullPbfPath = Path.GetFullPath(pbfPath);
        if (!IsPathInside(fullPbfPath, osmRoot))
        {
            throw new InvalidOperationException("route OSM manifest cannot reference a file outside the route OpenStreetMap cache");
        }

        Directory.CreateDirectory(osmRoot);
        FileInfo pbf = new(fullPbfPath);
        OsmCacheManifest manifest = new(
            OsmCacheSchemaVersion,
            "Geofabrik",
            region.Id,
            region.Name,
            region.PbfUrl,
            Path.GetRelativePath(osmRoot, fullPbfPath).Replace(Path.DirectorySeparatorChar, '/'),
            pbf.Length,
            region.Modified,
            new DateTimeOffset(pbf.LastWriteTimeUtc, TimeSpan.Zero),
            region.MinLon,
            region.MinLat,
            region.MaxLon,
            region.MaxLat,
            ReadVersionText());
        WriteJsonAtomically(GetRouteOsmManifestPath(routeDir), manifest);
        RegisterRouteOsmCache(routeDir, manifest);
    }

    private static void RegisterRouteOsmCache(string routeDir, OsmCacheManifest manifest)
    {
        string normalizedRoute = Path.GetFullPath(routeDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        OsmCacheRegistry registry = LoadMapCacheRegistry();
        registry.Routes.RemoveAll(entry =>
            string.Equals(
                Path.GetFullPath(entry.RoutePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                normalizedRoute,
                StringComparison.OrdinalIgnoreCase));
        registry.Routes.Add(new OsmCacheRegistryEntry(
            normalizedRoute,
            GetRouteOsmManifestPath(normalizedRoute),
            manifest.RegionId,
            manifest.RegionName,
            DateTimeOffset.UtcNow));
        SaveMapCacheRegistry(registry);
    }

    private static OsmCacheRegistry LoadMapCacheRegistry()
    {
        try
        {
            string path = GetMapCacheRegistryPath();
            if (!File.Exists(path))
            {
                return new OsmCacheRegistry(OsmCacheSchemaVersion, []);
            }

            OsmCacheRegistry? registry = JsonSerializer.Deserialize<OsmCacheRegistry>(
                File.ReadAllText(path),
                CacheJsonOptions);
            return registry is { SchemaVersion: OsmCacheSchemaVersion, Routes: not null }
                ? registry
                : new OsmCacheRegistry(OsmCacheSchemaVersion, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new OsmCacheRegistry(OsmCacheSchemaVersion, []);
        }
    }

    private static void SaveMapCacheRegistry(OsmCacheRegistry registry)
    {
        registry.Routes.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RoutePath, right.RoutePath));
        WriteJsonAtomically(GetMapCacheRegistryPath(), registry);
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"cannot determine the directory for {path}");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(value, CacheJsonOptions),
            new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    internal static IReadOnlyList<MapCacheEntry> GetKnownMapCaches(string? currentRoutePath)
    {
        List<MapCacheEntry> entries = [];
        string appDataCache = GetMapCacheDirectory();

        if (Directory.Exists(appDataCache))
        {
            foreach (string pbfPath in Directory.EnumerateFiles(appDataCache, "*.osm.pbf", SearchOption.TopDirectoryOnly))
            {
                AddSingleFileCache(
                    entries,
                    "legacy-pbf:" + Path.GetFullPath(pbfPath).ToUpperInvariant(),
                    "Legacy regional OSM extract",
                    Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(pbfPath)),
                    pbfPath,
                    MapCacheKind.LegacyRegionalExtract,
                    appDataCache);
            }

            foreach (string partialPath in Directory.EnumerateFiles(appDataCache, "*.osm.pbf.part", SearchOption.TopDirectoryOnly))
            {
                AddSingleFileCache(
                    entries,
                    "legacy-part:" + Path.GetFullPath(partialPath).ToUpperInvariant(),
                    "Incomplete legacy OSM download",
                    Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(partialPath))),
                    partialPath,
                    MapCacheKind.LegacyPartialDownload,
                    appDataCache);
            }
        }

        OsmCacheRegistry registry = LoadMapCacheRegistry();
        HashSet<string> routePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (OsmCacheRegistryEntry route in registry.Routes)
        {
            if (!string.IsNullOrWhiteSpace(route.RoutePath))
            {
                routePaths.Add(Path.GetFullPath(route.RoutePath));
            }
        }

        if (!string.IsNullOrWhiteSpace(currentRoutePath) && Directory.Exists(currentRoutePath))
        {
            routePaths.Add(Path.GetFullPath(currentRoutePath));
        }

        List<OsmCacheRegistryEntry> refreshedRoutes = [];
        foreach (string routePath in routePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(routePath))
            {
                continue;
            }

            string owner = Path.GetFileName(routePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string providerDirectory = GetRouteOsmProviderDirectory(routePath);
            string manifestPath = GetRouteOsmManifestPath(routePath);
            string? manifestPbfPath = null;
            OsmCacheManifest? manifest = null;
            if (TryReadRouteOsmManifest(routePath, out manifest, out manifestPbfPath) && manifest is not null)
            {
                refreshedRoutes.Add(new OsmCacheRegistryEntry(
                    routePath,
                    manifestPath,
                    manifest.RegionId,
                    manifest.RegionName,
                    DateTimeOffset.UtcNow));
            }

            if (Directory.Exists(providerDirectory))
            {
                bool manifestIncluded = false;
                foreach (string pbfPath in Directory.EnumerateFiles(providerDirectory, "*.osm.pbf", SearchOption.TopDirectoryOnly))
                {
                    List<string> files = [pbfPath];
                    if (manifestPbfPath is not null &&
                        string.Equals(Path.GetFullPath(pbfPath), Path.GetFullPath(manifestPbfPath), StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(manifestPath))
                    {
                        files.Add(manifestPath);
                        manifestIncluded = true;
                    }

                    AddFileGroupCache(
                        entries,
                        "route-pbf:" + Path.GetFullPath(pbfPath).ToUpperInvariant(),
                        manifest is not null ? $"Geofabrik {manifest.RegionName} OSM extract" : "Route regional OSM extract",
                        owner,
                        pbfPath,
                        MapCacheKind.RouteRegionalExtract,
                        GetRouteOsmDirectory(routePath),
                        files);
                }

                if (File.Exists(manifestPath) && !manifestIncluded)
                {
                    AddSingleFileCache(
                        entries,
                        "route-manifest:" + Path.GetFullPath(manifestPath).ToUpperInvariant(),
                        "Route OSM cache manifest",
                        owner,
                        manifestPath,
                        MapCacheKind.RouteRegionalExtract,
                        GetRouteOsmDirectory(routePath));
                }

                foreach (string partialPath in Directory.EnumerateFiles(providerDirectory, "*.osm.pbf.part", SearchOption.TopDirectoryOnly))
                {
                    AddSingleFileCache(
                        entries,
                        "route-part:" + Path.GetFullPath(partialPath).ToUpperInvariant(),
                        "Incomplete route OSM download",
                        owner,
                        partialPath,
                        MapCacheKind.RoutePartialDownload,
                        GetRouteOsmDirectory(routePath));
                }
            }

        }

        OsmCacheRegistry refreshed = new(
            OsmCacheSchemaVersion,
            refreshedRoutes
                .GroupBy(entry => entry.RoutePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList());
        SaveMapCacheRegistry(refreshed);
        return entries
            .OrderBy(entry => entry.Owner, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddSingleFileCache(
        List<MapCacheEntry> entries,
        string id,
        string name,
        string owner,
        string path,
        MapCacheKind kind,
        string managedRoot)
    {
        if (!File.Exists(path))
        {
            return;
        }

        AddFileGroupCache(entries, id, name, owner, path, kind, managedRoot, [path]);
    }

    private static void AddFileGroupCache(
        List<MapCacheEntry> entries,
        string id,
        string name,
        string owner,
        string location,
        MapCacheKind kind,
        string managedRoot,
        IReadOnlyList<string> files)
    {
        FileInfo[] existingFiles = files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .Select(path => new FileInfo(path))
            .ToArray();
        if (existingFiles.Length == 0)
        {
            return;
        }

        entries.Add(new MapCacheEntry(
            id,
            name,
            owner,
            location,
            existingFiles.Length,
            existingFiles.Sum(file => file.Length),
            kind,
            managedRoot,
            existingFiles.Select(file => file.FullName).ToArray()));
    }

    internal static void PurgeMapCaches(IReadOnlyList<MapCacheEntry> selectedEntries)
    {
        foreach (MapCacheEntry entry in selectedEntries)
        {
            string managedRoot = Path.GetFullPath(entry.ManagedRoot);
            EnsureCacheRootIsSafe(entry.Kind, managedRoot);
            foreach (string filePath in entry.Files)
            {
                string fullPath = Path.GetFullPath(filePath);
                if (!IsPathInside(fullPath, managedRoot) || !IsAllowedCacheFile(entry.Kind, fullPath))
                {
                    throw new InvalidOperationException($"refusing to purge an unrecognized cache path: {fullPath}");
                }

                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }

            RemoveEmptyManagedDirectories(entry.Kind, managedRoot);
        }

        RepairMapCacheRegistry();
    }

    private static void EnsureCacheRootIsSafe(MapCacheKind kind, string managedRoot)
    {
        string expectedAppDataRoot = Path.GetFullPath(GetMapCacheDirectory());
        if (kind is MapCacheKind.LegacyRegionalExtract or MapCacheKind.LegacyPartialDownload)
        {
            if (!string.Equals(managedRoot, expectedAppDataRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"refusing to purge an unexpected AppData cache root: {managedRoot}");
            }
            return;
        }

        string leaf = Path.GetFileName(managedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.Equals(leaf, RouteOsmDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"refusing to purge an unexpected route OSM root: {managedRoot}");
        }

        if (Directory.Exists(managedRoot) &&
            new DirectoryInfo(managedRoot).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException($"refusing to purge a linked cache directory: {managedRoot}");
        }
    }

    private static bool IsAllowedCacheFile(MapCacheKind kind, string path)
    {
        return kind switch
        {
            MapCacheKind.LegacyRegionalExtract or MapCacheKind.RouteRegionalExtract =>
                path.EndsWith(".osm.pbf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(path), RouteOsmManifestFileName, StringComparison.OrdinalIgnoreCase),
            MapCacheKind.LegacyPartialDownload or MapCacheKind.RoutePartialDownload =>
                path.EndsWith(".osm.pbf.part", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static void RemoveEmptyManagedDirectories(MapCacheKind kind, string managedRoot)
    {
        if (!Directory.Exists(managedRoot))
        {
            return;
        }

        if (kind is MapCacheKind.RouteRegionalExtract or MapCacheKind.RoutePartialDownload)
        {
            string providerDirectory = Path.Combine(managedRoot, RouteOsmProviderDirectoryName);
            if (Directory.Exists(providerDirectory) && !Directory.EnumerateFileSystemEntries(providerDirectory).Any())
            {
                Directory.Delete(providerDirectory);
            }
        }

        if (Directory.Exists(managedRoot) && !Directory.EnumerateFileSystemEntries(managedRoot).Any())
        {
            Directory.Delete(managedRoot);
        }
    }

    private static void RepairMapCacheRegistry()
    {
        OsmCacheRegistry registry = LoadMapCacheRegistry();
        registry.Routes.RemoveAll(entry =>
        {
            if (!Directory.Exists(entry.RoutePath))
            {
                return true;
            }

            string osmDirectory = GetRouteOsmDirectory(entry.RoutePath);
            bool hasOsmCache = Directory.Exists(osmDirectory) &&
                Directory.EnumerateFiles(osmDirectory, "*", SearchOption.AllDirectories).Any();
            return !hasOsmCache;
        });
        SaveMapCacheRegistry(registry);
    }

    private static bool IsPathInside(string candidatePath, string rootPath)
    {
        string candidate = Path.GetFullPath(candidatePath);
        string root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
