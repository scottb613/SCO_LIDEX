// SCO LIDEX - persistent, stage-aware tile error and Append retry list.
// Copyright (C) Scott Brunner, Beast of Burden
// Licensed under GNU GPL v3 or later. See LICENSE.txt.
using System.Text;
using System.Text.Json;

namespace ORterr;

internal static partial class Program
{
    private const string TileErrorFileName = "SCOLIDEXErrors.txt";
    private static readonly AsyncLocal<TileErrorSession?> tileErrorSession = new();

    private sealed class TileErrorSession : IDisposable
    {
        private readonly TileErrorSession? previous;
        private readonly object gate = new();
        private readonly Dictionary<string, Dictionary<string, TileIssue>> errors = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<TileCoordinate> appendTiles;
        internal readonly string RouteDirectory;
        internal readonly bool Append;
        private readonly bool readOnly;
        internal readonly SupportCapture? Support;
        internal string FilePath => Path.Combine(RouteDirectory, TileErrorFileName);

        internal TileErrorSession(string routeDirectory, bool append, bool readOnly = false)
        {
            this.readOnly = readOnly;
            RouteDirectory = Path.GetFullPath(routeDirectory);
            if (!readOnly)
            {
                if (latestSupportCapture is not { Completed: false } capture ||
                    !string.Equals(capture.RouteDirectory, RouteDirectory, StringComparison.OrdinalIgnoreCase))
                    BeginSupportCapture(RouteDirectory);
                Support = latestSupportCapture;
            }
            Append = append;
            previous = tileErrorSession.Value;
            foreach (string raw in File.Exists(FilePath) ? File.ReadLines(FilePath) : [])
            {
                string[] parts = raw.Split('#', 2);
                string name = parts[0].Trim();
                if (name.Length == 0) continue;
                name = Path.GetFileName(name).ToLowerInvariant();
                Dictionary<string, TileIssue>? issues = null;
                if (parts.Length == 2)
                {
                    try { issues = JsonSerializer.Deserialize<Dictionary<string, TileIssue>>(parts[1]); }
                    catch (JsonException) { }
                }
                if (!errors.TryGetValue(name, out var existing)) errors[name] = existing = new();
                foreach (var issue in issues ?? new() { ["Terrain"] = new(parts.Length == 2 ? parts[1].Trim() : "Previously recorded tile error", DateTimeOffset.UtcNow) })
                    existing[issue.Key] = issue.Value;
            }
            appendTiles = errors.Where(e => e.Value.Keys.Any(stage => stage != "DM"))
                .Select(e => TryParseTextTileLine(e.Key, out var tile) ? (TileCoordinate?)tile : null)
                .Where(t => t.HasValue).Select(t => t!.Value).Where(t => t.X != 0 || t.Z != 0).ToHashSet();
            tileErrorSession.Value = this;
            if (!readOnly && !File.Exists(FilePath)) Save();
            WriteLogDetail("Tile error file", $"{FilePath} | {errors.Count:N0} outstanding tile(s)");
        }

        internal HashSet<TileCoordinate> AppendTiles() => Append ? new(appendTiles) : [];

        internal bool Has(string name, string stage)
        {
            lock (gate) return errors.TryGetValue(Path.GetFileName(name).ToLowerInvariant(), out var issues) && issues.ContainsKey(stage);
        }

        internal bool HasStage(string stage)
        {
            lock (gate) return errors.Values.Any(issues => issues.ContainsKey(stage));
        }

        internal HashSet<TileCoordinate> DmTiles()
        {
            lock (gate) return errors.Where(e => e.Value.ContainsKey("DM"))
                .Select(e => TryParseTextTileLine(e.Key, out var tile) ? (TileCoordinate?)tile : null)
                .Where(t => t.HasValue).Select(t => t!.Value).ToHashSet();
        }

        internal void Failed(string name, string stage, string reason)
        {
            if (readOnly) return;
            lock (gate)
            {
                if (Support is not null) Support.HadErrors = true;
                name = Path.GetFileName(name).ToLowerInvariant();
                if (!errors.TryGetValue(name, out var issues)) errors[name] = issues = new();
                issues[stage] = new(reason, DateTimeOffset.UtcNow);
                Save();
            }
        }

        internal void Succeeded(string name, string stage)
        {
            if (readOnly) return;
            lock (gate)
            {
                name = Path.GetFileName(name).ToLowerInvariant();
                if (!errors.TryGetValue(name, out var issues) || !issues.Remove(stage)) return;
                if (issues.Count == 0) errors.Remove(name);
                Save();
            }
        }

        private void Save()
        {
            string temporary = FilePath + ".tmp";
            try
            {
                using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true);
                    writer.WriteLine("# SCO LIDEX automatic tile errors. Tile names before #; outstanding stages/details after #.");
                    writer.WriteLine("# Append merges this file with SCOLIDEXTiles.txt. Entries clear only after the failed stage succeeds.");
                    foreach (var item in errors.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
                        writer.WriteLine($"{item.Key} # {JsonSerializer.Serialize(item.Value)}");
                    writer.Flush();
                    stream.Flush(true);
                }
                File.Move(temporary, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"WARNING: Could not update tile error file {FilePath}: {ex.Message}. Errors remain in the run log.");
            }
        }

        public void Dispose()
        {
            if (!readOnly) lock (gate)
            {
                Save();
                WriteLogDetail("Outstanding tile errors", $"{errors.Count:N0} tile(s) in {FilePath}; Append retries enabled stages");
            }
            tileErrorSession.Value = previous;
            if (Support is not null) Support.Completed = true;
        }
    }

    private sealed record TileIssue(string Reason, DateTimeOffset Utc);

    private static TileErrorSession? TileErrorsFor(string routeDirectory) =>
        tileErrorSession.Value is { } session && string.Equals(session.RouteDirectory,
            Path.GetFullPath(routeDirectory), StringComparison.OrdinalIgnoreCase) ? session : null;

    private static void RecordTileIssue(string name, string stage, string reason) =>
        tileErrorSession.Value?.Failed(name, stage, reason);

    private static void ResolveTileIssue(string name, string stage) =>
        tileErrorSession.Value?.Succeeded(name, stage);

    private static bool RetryTileIssue(string name, string stage) =>
        tileErrorSession.Value is { Append: true } session && session.Has(name, stage);

    private static string NormalTileErrorName(int x, int z) => RouteLayout.TileNameFromTileXZ(x, z) + ".t";

    private static void WriteTileWithErrorTracking(string name, string stage, Action write)
    {
        try { write(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordTileIssue(name, stage, ex.ToString());
            throw;
        }
        ResolveTileIssue(name, stage);
    }

    private static T ReadTileWithErrorTracking<T>(string name, string stage, Func<T> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordTileIssue(name, stage, ex.ToString());
            throw;
        }
    }

    private static HashSet<TileCoordinate> ReadAppendTileCoordinates(string routeDirectory)
    {
        TileErrorSession? session = TileErrorsFor(routeDirectory);
        if (session is not { Append: true }) return [];
        HashSet<TileCoordinate> coordinates = session.AppendTiles();
        if (File.Exists(Path.Combine(routeDirectory, "SCOLIDEXTiles.txt")) ||
            File.Exists(Path.Combine(routeDirectory, "SCOtopoTiles.txt")))
            coordinates.UnionWith(ReadUserTileCoordinates(routeDirectory));
        coordinates.Remove(new TileCoordinate(0, 0));
        return coordinates;
    }

    private static void RunTileErrorProbe()
    {
        string root = Path.Combine(Path.GetTempPath(), "SCOLIDEX-tile-errors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string first = NormalTileErrorName(-5829, 14910);
            string second = NormalTileErrorName(-5828, 14910);
            string dm = NormalTileErrorName(-5840, 14912);
            string userPath = Path.Combine(root, "SCOLIDEXTiles.txt");
            string userText = $"# User-owned selection\n{first}\n{first}\n";
            File.WriteAllText(userPath, userText);
            using (TileErrorSession session = new(root, append: true))
            {
                RecordTileIssue(first, "Terrain", "DEM unavailable");
                RecordTileIssue(first, "PolyVeg", "way/150864927 exclusion conflict");
                RecordTileIssue(dm, "DM", "lo_tile DEM unavailable");
                Parallel.For(0, 40, index => RecordTileIssue(second, index % 2 == 0 ? "Maps" : "Terrain", $"error {index}\ncontinued"));
                if (!RetryTileIssue(first, "Terrain") || !session.Has(second, "Maps"))
                    throw new InvalidDataException("Retry stage not retained");
                // Simulate an interrupted write: its error must survive reload.
                try { WriteTileWithErrorTracking(first, "Terrain", () => throw new IOException("write failed")); }
                catch (IOException) { }
                WriteTileWithErrorTracking(first, "Terrain", () => { });
                if (session.Has(first, "Terrain") || !session.Has(first, "PolyVeg"))
                    throw new InvalidDataException("Successful terrain cleared another stage");
                if (File.ReadLines(session.FilePath).Count(line => !line.StartsWith('#')) != 3)
                    throw new InvalidDataException("Concurrent errors duplicated or lost tile entries");
            }
            using (TileErrorSession session = new(root, append: true))
            {
                HashSet<TileCoordinate> merged = ReadTextFileTileCoordinates(root);
                if (merged.Count != 2 || !merged.Contains(new(-5829, 14910)) || !merged.Contains(new(-5828, 14910)))
                    throw new InvalidDataException("Append did not merge/deduplicate both files");
                if (!session.DmTiles().SetEquals([new TileCoordinate(-5840, 14912)]) || merged.Contains(new(-5840, 14912)))
                    throw new InvalidDataException("DM-only retry leaked into normal terrain selection");
                if (!session.Has(first, "PolyVeg") || !session.Has(second, "Terrain") || !session.Has(second, "Maps"))
                    throw new InvalidDataException("Outstanding stages lost across restart");
                byte[] before = File.ReadAllBytes(session.FilePath);
                using (TileErrorSession scan = new(root, append: true, readOnly: true))
                {
                    RecordTileIssue(first, "Maps", "scan must not write");
                    ResolveTileIssue(first, "PolyVeg");
                }
                if (!before.SequenceEqual(File.ReadAllBytes(session.FilePath)))
                    throw new InvalidDataException("Scan modified the error file");
            }
            using (TileErrorSession overwrite = new(root, append: false))
                if (ReadTextFileTileCoordinates(root).Count != 1 || RetryTileIssue(first, "PolyVeg"))
                    throw new InvalidDataException("Overwrite unexpectedly included automatic errors");
            if (File.ReadAllText(userPath) != userText) throw new InvalidDataException("User tile file was modified");
            File.Delete(userPath);
            using (TileErrorSession errorsOnly = new(root, append: true))
            {
                if (ReadTextFileTileCoordinates(root).Count != 2)
                    throw new InvalidDataException("Error-only Append selection failed");
                ResolveTileIssue(first, "PolyVeg");
                ResolveTileIssue(second, "Terrain");
                ResolveTileIssue(second, "Maps");
                ResolveTileIssue(dm, "DM");
            }
            if (File.ReadLines(Path.Combine(root, TileErrorFileName)).Any(line => !line.StartsWith('#')))
                throw new InvalidDataException("Resolved tiles remained in the retry list");
            string scanRoot = Path.Combine(root, "scan-only");
            Directory.CreateDirectory(scanRoot);
            using (TileErrorSession scan = new(scanRoot, append: true, readOnly: true))
                RecordTileIssue(first, "Terrain", "must not create a scan file");
            if (File.Exists(Path.Combine(scanRoot, TileErrorFileName)))
                throw new InvalidDataException("Read-only Scan created an error file");
            string dmRoot = Path.Combine(root, "dm-only");
            Directory.CreateDirectory(dmRoot);
            using (TileErrorSession dmOnly = new(dmRoot, append: true)) RecordTileIssue(dm, "DM", "test DM failure");
            using (TileErrorSession retry = new(dmRoot, append: true))
            {
                EnsureTextFileCoverageTiles(dmRoot, hd4mOutput: false);
                if (ReadTextFileTileCoordinates(dmRoot).Count != 0 || retry.DmTiles().Count != 1 ||
                    Directory.Exists(Path.Combine(dmRoot, "tiles")))
                    throw new InvalidDataException("DM-only error selection created normal terrain or lost DM retry");
            }
            Console.WriteLine("Tile error probe: PASSED (concurrent updates, restart, stage-specific success, merged/error-only Append, Overwrite, read-only Scan, user-file preservation).");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
