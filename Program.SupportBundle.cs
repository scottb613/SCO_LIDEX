// SCO LIDEX - bounded diagnostic samples and transferable support archive.
// Copyright (C) Scott Brunner, Beast of Burden
// Licensed under GNU GPL v3 or later. See LICENSE.txt.
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OSGeo.OGR;

namespace ORterr;

internal static partial class Program
{
    private const long SupportZipLimitBytes = 100_000_000;
    private static SupportCapture? latestSupportCapture;

    internal static void BeginSupportCapture(string routeDirectory) => latestSupportCapture = new(routeDirectory);

    private sealed class SupportCapture(string routeDirectory)
    {
        internal readonly string RouteDirectory = Path.GetFullPath(routeDirectory);
        internal readonly string DirectoryPath = Path.Combine(routeDirectory, "osm_data", "diagnostics",
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N")[..8]);
        internal readonly List<string> Samples = [];
        internal int OmittedSamples;
        internal bool Completed;
        internal bool HadErrors;
        private long geometryBytes;
        private int attempted;

        internal string? Capture(Func<object> metadata, IReadOnlyDictionary<string, Geometry?> geometries)
        {
            int sequence = ++attempted;
            if (Samples.Count >= 8 || geometryBytes >= 64_000_000)
            {
                OmittedSamples++;
                WriteOsmLogEntry($"WARNING: Geometry sample {sequence} omitted: support sample allowance reached. Issue details remain in the log.");
                return null;
            }
            long bytes = geometries.Values.Where(g => g is not null).Sum(g => (long)g!.WkbSize());
            // Check native sizes before allocating or serializing any geometry.
            if (Samples.Count >= 8 || bytes > 16_000_000 || geometryBytes + bytes > 64_000_000)
            {
                OmittedSamples++;
                WriteOsmLogEntry($"WARNING: Geometry sample {sequence} omitted by support limits (8 samples; 16 MB per sample; 64 MB total geometry). All issue details remain in the log.");
                return null;
            }
            string path = Path.Combine(DirectoryPath, $"conflict-{sequence:D3}.zip");
            try
            {
                string metadataJson = JsonSerializer.Serialize(metadata(), CacheJsonOptions);
                if (Encoding.UTF8.GetByteCount(metadataJson) > 64_000)
                {
                    OmittedSamples++;
                    WriteOsmLogEntry($"WARNING: Geometry sample {sequence} omitted: metadata exceeds 64 KB; original issue remains logged.");
                    return null;
                }
                Directory.CreateDirectory(DirectoryPath);
                using (FileStream output = new(path + ".tmp", FileMode.CreateNew, FileAccess.Write))
                {
                    using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        WriteZipText(zip, "metadata.json", metadataJson);
                        foreach ((string name, Geometry? geometry) in geometries)
                        {
                            if (geometry is null) continue;
                            byte[] wkb = new byte[checked((int)geometry.WkbSize())];
                            if (geometry.ExportToWkb(wkb) != 0) throw new InvalidDataException("Could not export " + name);
                            using Stream entry = zip.CreateEntry(name + ".wkb", CompressionLevel.Fastest).Open();
                            entry.Write(wkb);
                        }
                    }
                    output.Flush(true);
                }
                File.Move(path + ".tmp", path);
                geometryBytes += bytes;
                Samples.Add(path);
                WriteOsmLogEntry($"Reproduction sample: {path} ({new FileInfo(path).Length:N0} bytes). Included in the support ZIP.");
                return path;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                OmittedSamples++;
                WriteOsmLogEntry($"WARNING: Could not save geometry sample {path}: {ex.Message}. Processing continues; original issue remains logged.");
                try { File.Delete(path + ".tmp"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                return null;
            }
        }
    }

    private static void WriteZipText(ZipArchive zip, string name, string value)
    {
        using Stream entry = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        using StreamWriter writer = new(entry, new UTF8Encoding(false));
        writer.Write(value);
    }

    internal static bool NeedsSupportZip(string routeDirectory, bool operationError) => operationError ||
        latestSupportCapture is { HadErrors: true } capture &&
        string.Equals(capture.RouteDirectory, Path.GetFullPath(routeDirectory), StringComparison.OrdinalIgnoreCase);

    internal static string CreateSupportZip(string routeDirectory, string logPath)
    {
        string outputPath = Path.Combine(Path.GetDirectoryName(logPath)!, "SCOLIDEX-diagnostics.zip");
        SupportCapture? capture = latestSupportCapture is { } current &&
            string.Equals(current.RouteDirectory, Path.GetFullPath(routeDirectory), StringComparison.OrdinalIgnoreCase) ? current : null;
        List<string> omissions = [];
        string temporary = outputPath + ".tmp";
        try
        {
            using (FileStream output = new(temporary, FileMode.Create, FileAccess.Write))
            {
                using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    AddBoundedSupportFile(zip, logPath, "SCOLIDEX.log", 24_000_000, omissions);
                    AddBoundedSupportFile(zip, Path.Combine(routeDirectory, TileErrorFileName), TileErrorFileName, 2_000_000, omissions);
                    AddBoundedSupportFile(zip, Path.Combine(routeDirectory, "SCOLIDEXTiles.txt"), "SCOLIDEXTiles.txt", 1_000_000, omissions);
                    if (capture is not null)
                    {
                        long sampleBytes = 0;
                        foreach (string sample in capture.Samples)
                        {
                            using FileStream input = File.OpenRead(sample);
                            if (input.Length > 17_000_000 || sampleBytes + input.Length > 65_000_000)
                            {
                                omissions.Add($"{Path.GetFileName(sample)}: omitted because archived samples exceed their size allowance.");
                                continue;
                            }
                            sampleBytes += input.Length;
                            using Stream entry = zip.CreateEntry("geometry/" + Path.GetFileName(sample), CompressionLevel.NoCompression).Open();
                            input.CopyTo(entry);
                        }
                        if (capture.OmittedSamples > 0)
                            omissions.Add($"{capture.OmittedSamples} geometry sample(s) were omitted by size/count limits or capture failures; see SCOLIDEX.log.");
                    }
                    WriteZipText(zip, "README.txt",
                        $"SCO LIDEX {ReadVersionText()} support archive\nUTC: {DateTimeOffset.UtcNow:O}\n" +
                        "Hard archive limit: 100,000,000 bytes (100 MB). Original route and log files are unchanged.\n" +
                        "Geometry samples contain exact WKB coordinates in the UTM CRS declared in metadata.json, overlay inputs/intermediates and the detected conflict.\n" +
                        "Large PBFs, route GeoPackages, terrain grids, maps and scenery assets are excluded.\n" +
                        "Limits: 8 geometry samples, 16 MB geometry per sample, 64 MB total geometry; log 24 MB; error list 2 MB; user list 1 MB.\n\n" +
                        (omissions.Count == 0 ? "No available inputs were truncated or omitted by these limits.\n" : string.Join("\n", omissions)));
                }
                output.Flush(true);
                if (output.Length > SupportZipLimitBytes) throw new InvalidDataException("Support ZIP exceeded the 100 MB limit; previous ZIP retained.");
            }
            File.Move(temporary, outputPath, overwrite: true);
            return outputPath;
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void AddBoundedSupportFile(ZipArchive zip, string path, string name, int limit, List<string> omissions)
    {
        if (!File.Exists(path)) { omissions.Add($"{name}: file was unavailable."); return; }
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using Stream entry = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        byte[] buffer = new byte[64 * 1024];
        void CopyBytes(long remaining)
        {
            while (remaining > 0)
            {
                int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) break;
                entry.Write(buffer, 0, count);
                remaining -= count;
            }
        }
        if (input.Length <= limit) { CopyBytes(input.Length); return; }
        byte[] marker = Encoding.UTF8.GetBytes("\n\n# SUPPORT COPY TRUNCATED: middle omitted; original file unchanged.\n\n");
        int head = Math.Min(1_000_000, limit / 4);
        CopyBytes(head);
        entry.Write(marker);
        int tail = limit - head - marker.Length;
        input.Seek(-tail, SeekOrigin.End);
        CopyBytes(tail);
        omissions.Add($"{name}: middle omitted; included beginning and end only ({limit:N0} of {input.Length:N0} bytes). Original file unchanged.");
    }

    private static void RunSupportBundleProbe()
    {
        string root = Path.Combine(Path.GetTempPath(), "SCOLIDEX-support-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using TileErrorSession session = new(root, append: true);
            if (NeedsSupportZip(root, operationError: false)) throw new InvalidDataException("Clean run requested a support ZIP");
            string log = Path.Combine(root, "SCOLIDEX.log");
            using (FileStream stream = File.Create(log))
            {
                stream.SetLength(25_000_000);
                stream.Write(Encoding.UTF8.GetBytes("START OF LOG\n"));
                stream.Seek(-12, SeekOrigin.End);
                stream.Write(Encoding.UTF8.GetBytes("END OF LOG\n"));
            }
            byte[] HashLog()
            {
                using FileStream input = File.OpenRead(log);
                return System.Security.Cryptography.SHA256.HashData(input);
            }
            byte[] before = HashLog();
            RecordTileIssue(NormalTileErrorName(-5829, 14910), "PolyVeg", "support fixture");
            if (!NeedsSupportZip(root, operationError: false)) throw new InvalidDataException("Errored run did not request a support ZIP");
            string zipPath = CreateSupportZip(root, log);
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                using StreamReader readme = new(zip.GetEntry("README.txt")!.Open());
                if (!readme.ReadToEnd().Contains("middle omitted") || zip.GetEntry("SCOLIDEX.log")?.Length != 24_000_000 ||
                    zip.GetEntry(TileErrorFileName) is null || new FileInfo(zipPath).Length > SupportZipLimitBytes)
                    throw new InvalidDataException("Support ZIP size, omission report or included files are incorrect");
            }
            if (!before.SequenceEqual(HashLog())) throw new InvalidDataException("Support ZIP changed the original Desktop-style log");
            ConfigureOsmRuntime();
            using Geometry geometry = Geometry.CreateFromWkt("POLYGON ((0 0, 10 0, 10 10, 0 10, 0 0))");
            for (int i = 0; i < 10; i++) session.Support!.Capture(() => new { sample = i }, new Dictionary<string, Geometry?> { ["sample"] = geometry });
            if (session.Support!.Samples.Count != 8 || session.Support.OmittedSamples != 2)
                throw new InvalidDataException("Geometry capture limits were not enforced");
            CreateSupportZip(root, log);
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                using StreamReader readme = new(zip.GetEntry("README.txt")!.Open());
                if (zip.Entries.Count(e => e.FullName.StartsWith("geometry/")) != 8 || !readme.ReadToEnd().Contains("2 geometry sample(s)"))
                    throw new InvalidDataException("Support ZIP lost geometry samples or omission counts");
            }
            Console.WriteLine("Support bundle probe: PASSED (errors-only trigger, retained original log, bounded/truncated copies, geometry count limits, omission report, 100 MB archive cap).");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
