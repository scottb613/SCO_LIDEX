// SCO LIDEX - support diagnostics.
// Copyright (C) Scott Brunner, Beast of Burden
// Part of the SCO LIDEX Terrain Builder application.
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

using System.Text;

namespace ORterr;

internal static partial class Program
{
    private static readonly object diagnosticLock = new();
    private static readonly Dictionary<string, string> diagnosticContext = new();
    private static DateTime diagnosticStartedUtc = DateTime.UtcNow;

    internal static void BeginDiagnostics(string operation, string route)
    {
        lock (diagnosticLock)
        {
            diagnosticContext.Clear();
            diagnosticStartedUtc = DateTime.UtcNow;
            diagnosticContext["Operation"] = operation;
            diagnosticContext["Route"] = route;
        }
    }

    // These are last-observed checkpoints, not a claim that a concurrent worker
    // failed on this particular feature. Keep them when exception unwinding starts.
    internal static void SetDiagnosticContext(string key, string value)
    {
        lock (diagnosticLock) diagnosticContext[key] = value;
    }

    internal static void WriteFailureDiagnostics(string reason, Exception? exception = null)
    {
        try
        {
            StringBuilder report = new();
            report.AppendLine().AppendLine("STOP / FAILURE DIAGNOSTICS").AppendLine("--------------------------");
            report.AppendLine($"  REASON: {reason}");
            report.AppendLine($"  UTC: {DateTime.UtcNow:O}");
            report.AppendLine($"  VERSION: {ReadVersionText()} | CLR: {Environment.Version} | OS: {Environment.OSVersion}");
            report.AppendLine($"  PROCESS: {Environment.ProcessId} | THREAD: {Environment.CurrentManagedThreadId} | 64-BIT: {Environment.Is64BitProcess}");
            lock (diagnosticLock)
            {
                report.AppendLine($"  ELAPSED: {DateTime.UtcNow - diagnosticStartedUtc}");
                report.AppendLine("  LAST OBSERVED CONTEXT (workers may run concurrently):");
                foreach (var item in diagnosticContext) report.AppendLine($"    {item.Key}: {item.Value}");
            }
            try { report.Append(FormatProcessMemoryDiagnostics()); }
            catch (Exception memoryError) { report.AppendLine($"  Memory diagnostics unavailable: {memoryError.Message}"); }
            if (exception is not null)
            {
                report.AppendLine($"  HRESULT: 0x{exception.HResult:X8}");
                report.AppendLine("  EXCEPTION (includes inner exceptions and stack traces):");
                report.AppendLine(exception.ToString());
                AppendExceptionData(report, exception);
            }
            else report.AppendLine("  No exception was supplied; see preceding status/error messages.");
            Console.Error.WriteLine(report.ToString());
            Console.Error.Flush();
            if (exception is not null && exception is not OperationCanceledException)
            {
                // Persist the exception first. A disconnected route drive must not
                // hold up the failure report indefinitely. No retry or route writes.
                string[] paths;
                lock (diagnosticLock)
                    paths = diagnosticContext.Where(x => x.Key == "Route" || x.Key.EndsWith("path", StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Value).Where(Path.IsPathRooted).Distinct().Take(8).ToArray();
                Task<string> probes = Task.Run(() => ProbeFailurePaths(paths));
                Console.Error.WriteLine(probes.Wait(TimeSpan.FromSeconds(3))
                    ? probes.Result
                    : "  READ-ONLY FAILURE PROBES: timed out after 3 seconds; storage may be unresponsive.");
                Console.Error.Flush();
            }
        }
        catch
        {
            // A broken output disk/writer must not hide the original failure.
            if (exception is not null) WriteStartupErrorLog(exception);
        }
    }

    private static string ProbeFailurePaths(string[] paths)
    {
        StringBuilder result = new("  READ-ONLY FAILURE PROBES (no files modified):\n");
        foreach (string path in paths)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                result.AppendLine($"    {path}: attributes={attributes}");
                if (!attributes.HasFlag(FileAttributes.Directory))
                {
                    FileInfo file = new(path);
                    result.AppendLine($"      bytes={file.Length}; modified UTC={file.LastWriteTimeUtc:O}");
                }
            }
            catch (Exception ex) { result.AppendLine($"    {path}: {ex.GetType().Name}: {ex.Message}"); }
            try
            {
                DriveInfo drive = new(Path.GetPathRoot(path)!);
                result.AppendLine($"      storage={drive.Name}; free bytes={drive.AvailableFreeSpace}; total bytes={drive.TotalSize}");
            }
            catch (Exception ex) { result.AppendLine($"      Storage probe unavailable: {ex.GetType().Name}: {ex.Message}"); }
        }
        return result.ToString();
    }

    private static void AppendExceptionData(StringBuilder report, Exception exception)
    {
        foreach (System.Collections.DictionaryEntry item in exception.Data)
            report.AppendLine($"  EXCEPTION DATA [{item.Key}]: {item.Value}");
        if (exception is AggregateException aggregate)
            foreach (Exception inner in aggregate.InnerExceptions) AppendExceptionData(report, inner);
        else if (exception.InnerException is Exception inner) AppendExceptionData(report, inner);
    }

    private static void RunFailureDiagnosticsProbe()
    {
        TextWriter previous = Console.Error;
        using StringWriter capture = new();
        try
        {
            Console.SetError(capture);
            BeginDiagnostics("Probe", "synthetic-route");
            SetDiagnosticContext("Feature", "way/1224794030");
            Exception cause;
            try { throw new IOException("synthetic write failure"); }
            catch (Exception ex) { cause = ex; }
            cause.Data["Output"] = "synthetic.geojson";
            WriteFailureDiagnostics("Run failed", new InvalidDataException("synthetic polygon failure", cause));
            WriteFailureDiagnostics("Cancellation requested");
            string result = capture.ToString();
            foreach (string expected in new[] { "synthetic-route", "way/1224794030", "IOException", "RunFailureDiagnosticsProbe", "synthetic.geojson", "Cancellation requested", "HRESULT" })
                if (!result.Contains(expected, StringComparison.Ordinal)) throw new InvalidDataException($"Missing diagnostic: {expected}");
            BeginDiagnostics("Second operation", "other-route");
            capture.GetStringBuilder().Clear();
            WriteFailureDiagnostics("Second failure");
            if (capture.ToString().Contains("way/1224794030", StringComparison.Ordinal)) throw new InvalidDataException("Stale failure context survived reset");
            string fixture = Path.GetTempFileName();
            try
            {
                File.WriteAllText(fixture, "diagnostic fixture");
                string probe = ProbeFailurePaths([fixture, fixture + ".missing"]);
                if (!probe.Contains("bytes=18", StringComparison.Ordinal) ||
                    !probe.Contains("FileNotFoundException", StringComparison.Ordinal) ||
                    File.ReadAllText(fixture) != "diagnostic fixture")
                    throw new InvalidDataException("Read-only file probes failed");
            }
            finally { File.Delete(fixture); }
        }
        finally { Console.SetError(previous); }
        Console.WriteLine("Failure diagnostics probe passed.");
    }
}
