// SCO LIDEX - Copernicus DEM GLO-30 discovery, validation, and COG reading.
// Copyright (C) Scott Brunner, Beast of Burden
// Part of the SCO LIDEX Terrain Builder application.
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MaxRev.Gdal.Core;
using OSGeo.GDAL;

namespace ORterr;

internal static partial class Program
{
    private const string GlobalDemLabel = "30m (global)";
    private const string GlobalDemDisplayName = "Copernicus DEM GLO-30 Public";
    private const string CopernicusBaseUrl = "https://copernicus-dem-30m.s3.amazonaws.com";
    private static long copernicusDataBytesRead;

    internal static void ResetCopernicusDataCounter()
    {
        Interlocked.Exchange(ref copernicusDataBytesRead, 0);
    }

    internal static long GetCopernicusDataBytesRead()
    {
        return Interlocked.Read(ref copernicusDataBytesRead);
    }

    internal static string FormatCopernicusDataBytesRead()
    {
        return FormatByteCount(GetCopernicusDataBytesRead());
    }

    private static void AddCopernicusDataBytes(long byteCount)
    {
        if (byteCount > 0)
        {
            Interlocked.Add(ref copernicusDataBytesRead, byteCount);
        }
    }

    // Read-only developer/support diagnostic. It exercises the same tile-name,
    // anonymous AWS, GDAL, and sampling path used during a terrain run.
    private static Task RunCopernicusProbeAsync(string[] args)
    {
        double latitude = ParseDoubleOption(args, "--latitude", 40.75);
        double longitude = ParseDoubleOption(args, "--longitude", -75.25);
        const double offset = 0.0002;
        double[,] longitudes =
        {
            { longitude - offset, longitude + offset },
            { longitude - offset, longitude + offset },
        };
        double[,] latitudes =
        {
            { latitude + offset, latitude + offset },
            { latitude - offset, latitude - offset },
        };
        GeoSampleGrid sampleGrid = new(
            longitudes,
            latitudes,
            (longitude - offset, latitude - offset, longitude + offset, latitude + offset));

        Console.WriteLine("SCO LIDEX Copernicus GLO-30 read-only probe");
        Console.WriteLine($"Point: lon {longitude:F6}, lat {latitude:F6}");
        Console.WriteLine("Access: anonymous AWS Open Data COG");
        GdalBase.ConfigureAll();
        Gdal.SetConfigOption("GDAL_HTTP_UNSAFESSL", "YES");
        ResetCopernicusDataCounter();
        List<string> failures = [];
        List<DemWindow> windows = ReadCopernicusDemWindows(sampleGrid, failures);
        short[,] merged = CreateMissingHeightGrid(2, 2);
        int samples = MergeWindows(windows, merged);
        if (samples == 0)
        {
            Console.WriteLine("Probe result: FAILED");
            foreach (string failure in failures.Take(6))
            {
                Console.WriteLine("  " + failure);
            }

            return Task.CompletedTask;
        }

        RawGridStats stats = RawGrid.GetStats(merged);
        Console.WriteLine(
            $"Probe result: PASSED; valid={stats.ValidCount:N0}, missing={stats.MissingCount:N0}, " +
            $"min={stats.MinHeight}, max={stats.MaxHeight}, data read={FormatCopernicusDataBytesRead()}.");
        return Task.CompletedTask;
    }

    private static List<DemWindow> ReadCopernicusDemWindows(
        GeoSampleGrid sampleGrid,
        List<string> failures)
    {
        List<DemWindow> windows = [];
        bool firstProduct = true;
        foreach (CopernicusCogTile tile in GetCopernicusCogTiles(sampleGrid))
        {
            Dataset? dataset = null;
            if (!firstProduct)
            {
                Console.WriteLine();
            }
            firstProduct = false;
            WriteLogDetail("DEM product", $"{GlobalDemLabel} ({GlobalDemDisplayName}) | {tile.Name}.tif", 4);
            try
            {
                dataset = Gdal.Open("/vsicurl/" + tile.Url, Access.GA_ReadOnly);
            }
            catch (Exception ex)
            {
                failures.Add($"{tile.Name}: open failed: {ex.Message}");
                continue;
            }

            if (dataset is null)
            {
                failures.Add($"{tile.Name}: unavailable (land COG not published or request failed)");
                continue;
            }

            using (dataset)
            {
                try
                {
                    bool readOk = TryReadDatasetSampleGrid(
                        dataset,
                        sampleGrid,
                        fillMissing: false,
                        useStandardMetreElevations: true,
                        AddCopernicusDataBytes,
                        out short[,] heights,
                        out int missing,
                        out string failure);
                    if (!readOk)
                    {
                        failures.Add($"{tile.Name}: {failure}");
                        continue;
                    }

                    int totalSamples = heights.GetLength(0) * heights.GetLength(1);
                    int valid = totalSamples - missing;
                    windows.Add(new DemWindow(tile.Name + ".tif", heights, valid));
                    WriteLogDetail("Contribution", $"{valid:N0} / {totalSamples:N0} samples", 6);
                }
                catch (Exception ex)
                {
                    failures.Add($"{tile.Name}: read failed: {ex.Message}");
                }
            }
        }

        return windows;
    }

    private static async Task<SourceAvailability> TestCopernicusDatasetAsync(
        HttpClient client,
        GeoSampleGrid sampleGrid,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CopernicusCogTile> candidates = GetCopernicusCogTiles(sampleGrid);
        if (candidates.Count == 0)
        {
            Console.WriteLine($"{GlobalDemLabel} ({GlobalDemDisplayName}): unavailable for the representative bbox.");
            return new SourceAvailability(true, false, "no land COG candidate for the representative bbox");
        }

        List<string> failures = [];
        foreach (CopernicusCogTile representative in candidates)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Head, representative.Url);
                using HttpResponseMessage response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine(
                        $"{GlobalDemLabel} ({GlobalDemDisplayName}): active, anonymous AWS COG available " +
                        $"for representative bbox ({representative.Name}).");
                    return new SourceAvailability(true, true, $"anonymous AWS COG available ({representative.Name})");
                }

                failures.Add($"{representative.Name}: {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException or InvalidOperationException)
            {
                failures.Add($"{representative.Name}: {ex.Message}");
            }
        }

        bool noCoverage = failures.All(failure => failure.Contains("404", StringComparison.OrdinalIgnoreCase));
        if (noCoverage)
        {
            Console.WriteLine(
                $"{GlobalDemLabel} ({GlobalDemDisplayName}): no land COG published for representative bbox; " +
                "this may be ocean-only coverage.");
        }
        else
        {
            Console.WriteLine(
                $"{GlobalDemLabel} ({GlobalDemDisplayName}): FAILED ({string.Join(" | ", failures.Take(3))}).");
        }

        return noCoverage
            ? new SourceAvailability(true, false, "no land COG published; coverage may be ocean-only")
            : new SourceAvailability(false, false, string.Join(" | ", failures.Take(3)));
    }

    private static IReadOnlyList<CopernicusCogTile> GetCopernicusCogTiles(GeoSampleGrid sampleGrid)
    {
        HashSet<(int Latitude, int Longitude)> coordinates = [];
        int height = sampleGrid.Latitudes.GetLength(0);
        int width = sampleGrid.Latitudes.GetLength(1);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double latitude = sampleGrid.Latitudes[y, x];
                double longitude = NormalizeLongitude(sampleGrid.Longitudes[y, x]);
                if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude < -90 || latitude >= 90)
                {
                    continue;
                }

                coordinates.Add(((int)Math.Floor(latitude), (int)Math.Floor(longitude)));
            }
        }

        return coordinates
            .OrderBy(item => item.Latitude)
            .ThenBy(item => item.Longitude)
            .Select(item => CreateCopernicusCogTile(item.Latitude, item.Longitude))
            .ToArray();
    }

    private static CopernicusCogTile CreateCopernicusCogTile(int southLatitude, int westLongitude)
    {
        string latitude = string.Create(
            CultureInfo.InvariantCulture,
            $"{(southLatitude < 0 ? 'S' : 'N')}{Math.Abs(southLatitude):00}_00");
        string longitude = string.Create(
            CultureInfo.InvariantCulture,
            $"{(westLongitude < 0 ? 'W' : 'E')}{Math.Abs(westLongitude):000}_00");
        string name = $"Copernicus_DSM_COG_10_{latitude}_{longitude}_DEM";
        return new CopernicusCogTile(name, $"{CopernicusBaseUrl}/{name}/{name}.tif");
    }

    private static double NormalizeLongitude(double longitude)
    {
        double normalized = ((longitude + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        return normalized == 180.0 ? -180.0 : normalized;
    }

    private sealed record CopernicusCogTile(string Name, string Url);
}
