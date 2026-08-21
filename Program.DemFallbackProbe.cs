// SCO LIDEX - DEM coverage, fallback, and vertical-unit regression probe.
// Copyright (C) Scott Brunner, Beast of Burden
// Part of the SCO LIDEX Terrain Builder application.
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

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

        RasterElevationUnit standardOneMetreUnit = ResolveRasterElevationUnit(
            bandUnitName: "US survey foot",
            projectedUnitName: "US survey foot",
            projectedUnitToMeters: usSurveyFootToMeters,
            useStandardMetreElevations: true);
        if (Math.Abs(standardOneMetreUnit.UnitToMeters - 1.0) > 1e-12 ||
            standardOneMetreUnit.UnitName != "metres (standard DEM)")
        {
            throw new InvalidOperationException(
                "DEM fallback probe treated a standard 1m DEM's horizontal CRS unit as its elevation unit.");
        }

        RasterElevationUnit originalProductUnit = ResolveRasterElevationUnit(
            bandUnitName: "",
            projectedUnitName: "US survey foot",
            projectedUnitToMeters: usSurveyFootToMeters,
            useStandardMetreElevations: false);
        if (Math.Abs(originalProductUnit.UnitToMeters - usSurveyFootToMeters) > 1e-12)
        {
            throw new InvalidOperationException(
                "DEM fallback probe failed the Original Product Resolution CRS-unit fallback.");
        }

        RasterElevationUnit explicitBandUnit = ResolveRasterElevationUnit(
            bandUnitName: "US survey foot",
            projectedUnitName: "metre",
            projectedUnitToMeters: 1.0,
            useStandardMetreElevations: false);
        if (Math.Abs(explicitBandUnit.UnitToMeters - usSurveyFootToMeters) > 1e-12)
        {
            throw new InvalidOperationException(
                "DEM fallback probe ignored an explicit raster-band elevation unit.");
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
