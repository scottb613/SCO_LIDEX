SCO LIDEX Terrain Builder
=========================

SCO LIDEX is a Windows terrain-building utility for Open Rails and MSTS route
developers. It discovers route coverage, obtains USGS or global Copernicus
elevation data, builds normal terrain and optional TSRE-style distant
mountains, validates the work,
and writes terrain back into an existing route.

The exact planting handoff is documented in
docsMaster\POLYVEG-GEODATA-CONTRACT-v2.txt.

Current version: v1.400.

Release highlights
==================

v1.400 - What's New
-------------------

- Restructures the application as a professional TSRE-inspired dark operator
  console for small screens. The fixed centered sidebar contains Mode,
  Selection, Build Options, version, Contact, and Help; the Running Log receives
  the flexible workspace.
- Supports the complete interface at the 1024x700 minimum without sidebar
  scrollbars. Maximizing is disabled and startup remains inside the active
  monitor's usable area.
- Applies the charcoal/amber theme to controls, disabled choices, numeric
  inputs, sliders, Help, Contact, confirmations, and cache cleanup. Thin amber
  marks available actions; strong amber leads from Scan to Run and marks Keep
  All as the preferred exit choice.
- Corrects the DEM data issue identified on the ETRY Kentucky route. Raster
  scale/offset and metre or foot-based vertical units are normalized before
  interpolation. NoData, invalid finite sentinels, implausible elevations, and
  extreme cell discontinuities use the established lower-resolution fallback
  instead of producing terrain spikes.
- Defaults Tile Radius to 2, removes the unstable Text Size and unused Mosaic
  Tiles controls, and compacts the terrain/map options into two columns.

v1.300 - What's New
-------------------

- Defaults Create Route Tiles, Create DM Tiles, and Create OSM/Map Tiles on.
  Enable HD Mesh Tiles unlocks the 4m terrain choice; otherwise terrain remains
  locked to Normal - 8m Tiles.
- Creates 2048x2048 map overlays by default. Enable HD Map Tiles selects
  4096x4096 output.
- Scan checks mixed terrain resolution only when Create Route Tiles is selected.
  Scan is read-only; approved conversion occurs during Run.
- After styled approval, Run rebuilds every mismatched tile from current
  elevation data in the selected resolution, including mismatches outside the
  original selection. Matching tiles remain under the selected Run mode.
- Uses bounded rolling seam windows for 8m, 4m, and Distant Mountains. Each
  completed grid updates the counter; completed rows are merged, written, and
  released without retaining a full-route terrain mesh.
- Restores legacy `_map.ace` normal-terrain materials to `terrain.ace` while
  preserving the separate TSRE F3 `terrain_maps` PNG overlay.

v1.200 - What's New
-------------------

- Adds non-overlapping interface sounds for buttons, checkbox/radio selections,
  normal status changes, successful Scan/Run completion, and
  failure/error/abort indications.
- Retains the isolated 4m evaluation mechanism, but keeps the packaged GUI
  fixed to Normal - 8 m; the disabled teaser is labeled Testing - 4 m.
- Adds a disabled Create Mosaic Tiles teaser for future development.
- Adds the full Beast of Burden title graphic and a slightly taller interface
  without reducing the running log.
- Restores Route Path browsing and adds a clearly labeled Recent menu saved
  as JSON under the user's local SCOLIDEX application-data folder.
- Adds key-free Copernicus DEM GLO-30 Public fallback from AWS Open Data.
- Keeps the USGS 1m, 5m~, and 10m order, then fills only unresolved posts from
  30m (global) data.
- Uses Copernicus GLO-30 exclusively for Distant Mountains, resampling the 30m
  source onto their 128m low-terrain grid. DM-only work does not query USGS.
- Labels GLO-30 as a low-resolution DSM that can include vegetation,
  buildings, and infrastructure.
- Adds a 30m (global) status row, low-resolution indicator, Scan validation,
  and separate Copernicus data-read logging without shrinking the run log.
- Reports USGS 1m, 5m~, 10m, Copernicus, and Geofabrik status separately at the
  end of Scan. Viable fallbacks enable Run with warnings; failed or uncovered
  sources are not polled during that Run.
- Searches the selected route cache first, then sibling and registered route
  caches. A covering PBF is used directly in place without copying it.
- Enables Create OSM/Map Tiles from anonymous Geofabrik OpenStreetMap regional PBF
  extracts, with resumable route-local caching and no public OSM API bulk use.
- Stores the regional PBF under osm_data\geofabrik in the route and writes an
  osm_data\osm-cache.json manifest for discovery and reuse.
- Downloads a fresh PBF into the selected route only when no covering route
  cache exists. Purging a PBF is the user's explicit refresh mechanism.
- During Create OSM/Map Tiles, writes route OSM derivatives under the active
  route's osm_data folder: categorized route-geodata.gpkg, its
  route-geodata.json manifest, final polyveg-polygons.geojson, and
  polyveg-exclusions.geojson. Twelve canonical rural categories are mutually
  exclusive and permanent exclusions are already carved out for TSRE. Exact
  OSM style IDs, F3 RGB fills, draw order, and stable source IDs are retained.
- Uses the union of the route's actual 2048-meter normal terrain-tile
  footprints.
  Habitat layers use exact route coverage; transport, water, building, and
  developed-context layers retain a 2048-meter margin.
- Stores indexed WGS84 layers for woodland, scrub, heath, grassland, wetland,
  agriculture, orchard, parkland, golf course, cemetery, sports, zoo, water,
  waterways, buildings, developed land, roads, railways, and bare ground.
- Rebuilds derivatives when the PBF or normal terrain-tile fingerprint changes.
  Terrain without an object-bearing .w file remains in PolyVeg coverage.
  A fresh PBF download always refreshes them; a current package is reused.
- Streams the regional PBF once into a compact, spatially indexed route working
  cut. Map and PolyVeg processing reuse it until the source PBF or terrain
  footprint changes. Visible surfaces are stacked in bounded terrain sections
  with a one-foot overlap halo and recombined by stable source identity.
- Reads a covering PBF from another route in place and saves the sliced
  derivatives under the active route. Validated outputs atomically replace the
  previous derivative set. The PolyVeg exclusion cache contains route-clipped,
  already-buffered road, track, trail, and water corridors plus protected
  water, building, developed, and bare-ground polygons.
- Keeps only the small Geofabrik index and cross-route cache registry under
  %LocalAppData%\SCOLIDEX; large PBF data is never stored in AppData.
- Lists only regional PBFs and partial downloads on exit. Purge boxes are
  unchecked by default. Route derivatives, the small shared Geofabrik index,
  and generated terrain_maps PNG files are never offered for deletion.
- Creates a 4096x4096 TSRE F3 overlay PNG per selected normal tile in
  terrain_maps using TSRE's X*10000+Y naming convention.
- Overwrites a matching cached PNG directly while leaving terrain .t materials,
  patch UVs, TERRTEX files, and Distant Mountain tiles unchanged.
- Uses the exact terrain projection for OSM vertices and validates map/terrain
  corner and center agreement before Run.
- Retains selected-route geometry for the complete run and renders two 4096 bitmaps concurrently.
- Requires no API key, external route editor, or MSTS runtime.
- Matches TSRE's OSM vector palette, land-use/building treatment, feature
  ordering, and cased road/railway drawing rules.

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
- Creates TSRE-style Distant Mountain lo_tiles exclusively from Copernicus
  GLO-30, resampled onto the 128-meter low-terrain grid.
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
  tiles, raw-grid readability, decoded coordinates, templates, and only the
  data sources needed by the selected production stages.
- Provides Scan Override for deliberate advanced or known-good workflows.
- Marks retryable failures so Append can retry them automatically.
- Prints paste-ready failed-tile lists for targeted SCOLIDEXTiles.txt retries
  and separates unmappable failures that cannot be retried by tile name.
- Provides live totals for processed, skipped, failed, 1m, 5m~, 10m, and
  30m-global work.
- Supports Abort and stops before the next tile or write step.

Application and diagnostics:
- Provides a Windows WinForms interface with a formatted, read-only Help viewer.
- Prevents multiple GUI instances from running simultaneously.
- Writes SCOLIDEX.log to the user's Desktop with selected settings, projection
  details, source usage, elapsed time, failures, and separate USGS/Copernicus
  data-read totals.
- Writes SCOLIDEX-startup-error.txt to the Desktop if startup fails before the
  GUI opens.
- Ships as a self-contained Windows x64 distribution with GDAL, clean terrain
  templates, documentation, third-party notices, and a desktop shortcut helper.

v1.100 - Additional work
------------------------

- Uses the calibrated standard-projection terrain baseline of 11 meters east
  and 16 meters south. v1.100 introduced the correction; v1.200 refines its
  east/west component.
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
- Maintains the tracked and distributed docsMaster folder and this Markdown README
  for GitHub and release-page presentation.

Installation
============

1. Download SCOLIDEX-v1.400-win-x64.zip from the GitHub release.
2. Extract the complete archive to a writable folder.
3. Run SCOLIDEX-win-x64\SCOLIDEX.exe.
4. Optionally run AddShortcutDesktop.cmd from the extracted top-level folder.
5. Back up a route before using Run, Overwrite, Clean Tile Wipe, or Commit.

Requirements and limitations
============================

- Windows 10 or newer, 64-bit.
- Internet access while requesting elevation or Geofabrik map data.
- USGS elevation coverage is United States focused.
- Large routes can require substantial processing time and disk space.
- Map overlays are 2048x2048 PNG files by default or 4096x4096 when HD maps are enabled.
- Clean Tile Wipe and post-processing terrain shifts modify route files; always
  work from a backup.
- KML polygon filling is basic and should be considered experimental.
- SCO LIDEX reads TsreGeoProjection when present but does not create or modify
  that route setting.

Documentation
=============

See the docsMaster folder for the complete instructions, changelog, known
issues, build notes, geometry-placement technical note, sample tile list,
license, third-party notices, and the PolyVeg geodata contract.

License
-------

SCO LIDEX is distributed under the GNU General Public License, version 3 or
later. Third-party components remain under their respective licenses.
