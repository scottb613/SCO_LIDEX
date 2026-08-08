SCO LIDEX Terrain Builder
=========================

SCO LIDEX is a Windows terrain-building utility for Open Rails and MSTS route
developers. It discovers route coverage, obtains USGS elevation data, builds
normal terrain and optional TSRE-style distant mountains, validates the work,
and writes terrain back into an existing route.

Release highlights
==================

v1.000 - Complete feature set
-----------------------------

Terrain generation and source data:
- Builds normal Open Rails/MSTS terrain from USGS 1-meter 3DEP elevation data.
- Falls back through USGS Original Product Resolution data, displayed as 5m~,
  and USGS/NED 1/3 arc-second data, displayed as 10m, when finer coverage is
  unavailable.
- Samples the native ORTS 256x256 height grid at true 8-meter post spacing.
  The 256 stored posts cover 2,040 meters inside a nominal 2,048-meter tile;
  the neighboring tile supplies the next boundary post.
- Uses GDAL windowed raster reads so only the needed parts of source products
  are read instead of downloading complete DEM files.
- Merges generated terrain in a bounded rolling window to support large routes
  without retaining the entire route in memory.

Coverage and tile selection:
- Uses existing route terrain tiles as coverage.
- Accepts exact tile names from ROUTE\SCOLIDEXTiles.txt.
- Builds coverage from the route marker file, KML coordinates, or track
  database references.
- Provides configurable normal-terrain and Distant Mountain coverage radii.

Normal and Distant Mountain terrain:
- Creates or updates normal route terrain in Append or Overwrite mode.
- Creates TSRE-style Distant Mountain lo_tiles from 10-meter elevation data.
- Detects and replaces incompatible DEMEX-style distant mountain terrain when
  Distant Mountain generation is selected.
- Preserves existing texture, water, overlay, and patch choices during normal
  updates whenever the source tile layout can be patched safely.
- Includes clean normal-terrain and lo_tile templates for first-time creation
  and recovery from unsupported or damaged terrain files.
- Provides the explicitly destructive Clean Tile Wipe option for rebuilding a
  tile from a clean template.

Projection, placement, and calibration:
- Supports the standard MSTS/Open Rails interrupted Goode homolosine world-tile
  projection.
- Detects and honors route-specific TsreGeoProjection entries for DEM sampling,
  coverage bounds, markers, KML, and Distant Mountains.
- Provides meter-based north/south and east/west Advanced Geo Bias controls for
  route-specific calibration during DEM generation.
- Includes Commit/Post Processing for quickly testing offsets by resampling
  existing normal or TSRE-style Distant Mountain height grids without another
  USGS download.

Safety, validation, and recovery:
- Performs a read-only Scan before Run and validates route paths, selected
  tiles, raw-grid readability, decoded coordinates, templates, and a
  representative USGS service request.
- Provides Scan Override for deliberate advanced or known-good workflows.
- Marks retryable failures so Append can retry them automatically.
- Prints paste-ready failed-tile lists for targeted SCOLIDEXTiles.txt retries
  and separates unmappable failures that cannot be retried by tile name.
- Provides live totals for processed, skipped, failed, 1m, 5m~, and 10m work.
- Supports Abort and stops before the next tile or write step.

Application and diagnostics:
- Provides a Windows WinForms interface with a formatted, read-only Help viewer.
- Prevents multiple GUI instances from running simultaneously.
- Writes SCOLIDEX.log to the user's Desktop with selected settings, projection
  details, source usage, elapsed time, failures, and an estimated USGS data-read
  total.
- Writes SCOLIDEX-startup-error.txt to the Desktop if startup fails before the
  GUI opens.
- Ships as a self-contained Windows x64 distribution with GDAL, clean terrain
  templates, documentation, third-party notices, and a desktop shortcut helper.

v1.100 - Additional work
------------------------

- Corrects standard-projection terrain placement by 16 meters east and
  16 meters south to match the TSRE map-coordinate convention confirmed across
  multiple test routes.
- Applies the correction at geographic sampling time to both normal terrain and
  Distant Mountain generation, preserving consistent tile transitions.
- Leaves route-specific TsreGeoProjection mapping unchanged; the correction is
  only the standard-projection baseline.
- Treats Advanced Geo Bias values as additional route-specific offsets on top
  of the corrected standard baseline.
- Adds GEOMETRY_PLACEMENT_CORRECTION.txt with the coordinate comparison,
  256-post/8-meter grid explanation, and implementation details.
- Aligns the Windows executable file, assembly, and product metadata with
  version 1.100.
- Restores a tracked and distributed docs folder and adds this Markdown README
  for GitHub and release-page presentation.

Installation
============

1. Download SCOLIDEX-v1.100-win-x64.zip from the GitHub release.
2. Extract the complete archive to a writable folder.
3. Run SCOLIDEX-win-x64\SCOLIDEX.exe.
4. Optionally run AddShortcutDesktop.cmd from the extracted top-level folder.
5. Back up a route before using Run, Overwrite, Clean Tile Wipe, or Commit.

Requirements and limitations
============================

- Windows 10 or newer, 64-bit.
- Internet access while requesting elevation data.
- USGS elevation coverage is United States focused.
- Large routes can require substantial processing time and disk space.
- Clean Tile Wipe and post-processing terrain shifts modify route files; always
  work from a backup.
- KML polygon filling is basic and should be considered experimental.
- SCO LIDEX reads TsreGeoProjection when present but does not create or modify
  that route setting.

Documentation
=============

See the docs folder for the complete instructions, changelog, known issues,
build notes, geometry-placement technical note, sample tile list, license, and
third-party notices.

License
-------

SCO LIDEX is distributed under the GNU General Public License, version 3 or
later. Third-party components remain under their respective licenses.
