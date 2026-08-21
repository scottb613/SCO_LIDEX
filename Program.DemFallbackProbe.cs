// SCO LIDEX - Open Rails / MSTS Cloud Terrain Builder
// Focused regression checks for DEM coverage classification and vertical units.

using System;

namespace ORterr;

internal static partial class Program
{
    private static void RunDemFallbackProbe()
    {
        if (!IsDemCoverageGapFailure(AllSamplesNoDataFailure) ||
            !IsDemCoverageGapFailure(WindowOutsideRasterFailure + "; test") ||
            IsDemCoverageGapFailure("read failed"))
        {
            throw new InvalidOperationException(
                "DEM fallback probe failed coverage-gap classification.");
        }

        if (!TryGetUnitScaleToMeters(
                "US survey foot", out double usSurveyFootToMeters) ||
            Math.Abs(usSurveyFootToMeters - (1200.0 / 3937.0)) > 1e-12)
        {
            throw new InvalidOperationException(
                "DEM fallback probe failed US survey foot conversion.");
        }

        if (!TryGetUnitScaleToMeters(
                "international foot", out double internationalFootToMeters) ||
            Math.Abs(internationalFootToMeters - 0.3048) > 1e-12)
        {
            throw new InvalidOperationException(
                "DEM fallback probe failed international foot conversion.");
        }

        RasterElevationTransform feetTransform = new(
            usSurveyFootToMeters,
            0,
            usSurveyFootToMeters,
            "US survey foot");
        float[] sourceFeet = [1733.87f, 1733.87f, 1733.87f, 1733.87f];
        if (!TryBilinearSample(
                sourceFeet,
                width: 2,
                height: 2,
                xOrigin: 0,
                yOrigin: 0,
                rasterX: 0.5,
                rasterY: 0.5,
                noData: null,
                feetTransform,
                out double convertedMeters) ||
            Math.Abs(convertedMeters - 528.483) > 0.01)
        {
            throw new InvalidOperationException(
                $"DEM fallback probe failed vertical conversion: {convertedMeters:F3} m.");
        }

        RasterElevationTransform metreTransform = new(1, 0, 1, "metre");
        float[] noDataCell = [528, -999999, 528, 528];
        if (TryBilinearSample(
                noDataCell,
                width: 2,
                height: 2,
                xOrigin: 0,
                yOrigin: 0,
                rasterX: 0.5,
                rasterY: 0.5,
                noData: -999999,
                metreTransform,
                out _))
        {
            throw new InvalidOperationException(
                "DEM fallback probe accepted a NoData interpolation cell.");
        }

        float[] finiteSentinelCell = [528, 999999, 528, 528];
        if (TryBilinearSample(
                finiteSentinelCell,
                width: 2,
                height: 2,
                xOrigin: 0,
                yOrigin: 0,
                rasterX: 0.5,
                rasterY: 0.5,
                noData: null,
                metreTransform,
                out _))
        {
            throw new InvalidOperationException(
                "DEM fallback probe accepted an undeclared finite sentinel.");
        }

        float[] discontinuousCell = [528, 2529, 528, 528];
        if (TryBilinearSample(
                discontinuousCell,
                width: 2,
                height: 2,
                xOrigin: 0,
                yOrigin: 0,
                rasterX: 0.5,
                rasterY: 0.5,
                noData: null,
                metreTransform,
                out _))
        {
            throw new InvalidOperationException(
                "DEM fallback probe accepted an implausible local discontinuity.");
        }

        Console.WriteLine("DEM fallback probe passed.");
    }
}
