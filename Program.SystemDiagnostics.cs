// SCO LIDEX - support diagnostics.
// Copyright (C) Scott Brunner, Beast of Burden
// Part of the SCO LIDEX Terrain Builder application.
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

// SCO LIDEX - local hardware and process-memory diagnostics for support logs.
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ORterr;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    internal static string FormatSystemDiagnostics()
    {
        string cpu = "unavailable";
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            cpu = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? cpu;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        MemoryStatus memory = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        string ram = GlobalMemoryStatusEx(ref memory)
            ? $"{memory.TotalPhysical / 1073741824.0:F1} GiB usable physical; {memory.AvailablePhysical / 1073741824.0:F1} GiB available"
            : "unavailable";
        return $"  SYSTEM{Environment.NewLine}  ------{Environment.NewLine}" +
            $"    CPU: {cpu}{Environment.NewLine}" +
            $"    LOGICAL PROCESSORS AVAILABLE: {Environment.ProcessorCount}{Environment.NewLine}" +
            $"    RAM: {ram}{Environment.NewLine}";
    }

    internal static string FormatProcessMemoryDiagnostics()
    {
        using Process process = Process.GetCurrentProcess();
        return $"  LIDEX MEMORY: current working set {process.WorkingSet64 / 1073741824.0:F2} GiB; " +
            $"peak working set since application launch {process.PeakWorkingSet64 / 1073741824.0:F2} GiB; " +
            $"current private bytes {process.PrivateMemorySize64 / 1073741824.0:F2} GiB{Environment.NewLine}";
    }
}
