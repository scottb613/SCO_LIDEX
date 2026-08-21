# SCO LIDEX Terrain Builder

SCO LIDEX is a Windows terrain-building utility for Open Rails and MSTS route developers. It discovers route coverage, obtains USGS or global Copernicus elevation data, builds normal terrain and optional TSRE-style distant mountains, validates the work, and writes terrain back into an existing route.

The exact LIDEX/TSRE planting handoff is recorded in [POLYVEG-GEODATA-CONTRACT-v2.txt](docsMaster/POLYVEG-GEODATA-CONTRACT-v2.txt).

> **Current version:** v1.400.

## Release Highlights

### v1.400 — What's New

- Restructures the application as a professional TSRE-inspired dark operator console for small screens. A fixed, centered sidebar carries Mode, Selection, Build Options, version, Contact, and Help while the Running Log receives the flexible workspace.
- Supports the complete interface at the 1024×700 minimum without sidebar scrollbars. Maximizing is disabled, startup stays inside the active monitor, and the log retains its useful vertical viewing area.
- Applies the charcoal/amber theme consistently to controls, disabled choices, numeric inputs, sliders, Help, Contact, confirmations, and cache cleanup. Thin amber marks available actions; strong amber leads from Scan to Run and marks Keep All as the preferred exit choice.
- Corrects the DEM data issue identified on the ETRY Kentucky route. Raster scale/offset and metre or foot-based vertical units are normalized before interpolation; NoData, invalid finite sentinels, implausible elevations, and extreme cell discontinuities fall through to the established lower-resolution source path instead of producing terrain spikes.
- Defaults Tile Radius to 2, removes the unstable Text Size and unused Mosaic Tiles controls, and arranges Route Tiles, DM Tiles, OSM/Map Tiles, HD Mesh Tiles, and HD Map Tiles in a compact two-column workflow.

### v1.300 — What's New

- Defaults **Create Route Tiles**, **Create DM Tiles**, and **Create OSM/Map Tiles** on. **Enable HD Mesh Tiles** unlocks the 4m terrain choice; otherwise terrain remains locked to **Normal - 8m Tiles**.
- Creates 2048×2048 map overlays by default; **Enable HD Map Tiles** selects 4096×4096 output.
- Scan checks the route for mixed terrain resolution only when **Create Route Tiles** is selected. Scan is read-only; approved conversion occurs during Run.
- After styled approval, Run rebuilds every mismatched tile from current elevation data in the selected resolution, including mismatches outside the original selection. Matching tiles remain under the selected Run mode.
- Uses bounded rolling seam windows for 8m, 4m, and Distant Mountains. Each completed grid updates the live counter; completed rows are merged, written, and released without retaining a full-route terrain mesh.
- Restores obsolete `_map.ace` normal-terrain materials to `terrain.ace` while preserving the separate TSRE F3 `terrain_maps` PNG overlay.

### v1.200 — What's New

- Adds non-overlapping interface sounds for application buttons, checkbox/radio selections, normal status-stage changes, successful Scan/Run completion, and failure/error/abort indications; all supplied WAV assets remain packaged.
- Retains the isolated 4m evaluation implementation, but keeps the packaged GUI fixed to **Normal - 8 m**; the disabled teaser is labeled **Testing - 4 m**.
- Adds a disabled **Create Mosaic Tiles** teaser for future development.
- Adds the full Beast of Burden SCO LIDEX title graphic, a restrained textured title plate, and a slightly taller layout without reducing the running log.
- Restores the **Route Path Browse** button and adds a clearly labeled **Recent** menu for the five most recently used valid routes, saved in `%LocalAppData%\SCOLIDEX\route-history.json`.
- Adds key-free global elevation fallback using Copernicus DEM GLO-30 Public Cloud Optimized GeoTIFFs on AWS Open Data.
- Uses the established USGS `1m` → `5m~` → `10m` sequence first, then fills only unresolved posts from `30m (global)` data.
- Uses Copernicus GLO-30 exclusively for Distant Mountains, resampling 30-meter source data onto their 128-meter low-terrain grid; DM-only Scan and Run do not query USGS.
- Identifies the source as a low-resolution digital surface model that may include vegetation, buildings, and infrastructure.
- Adds separate `30m (global)` status counters, an amber `GLOBAL - LOW RES MODE` indicator, representative-source Scan validation, and Copernicus data-read totals.
- Reports USGS 1m, 5m~, 10m, Copernicus, and Geofabrik status separately at the end of Scan; viable fallbacks enable Run with warnings while failed or uncovered sources are excluded from that Run.
- Searches OSM caches before downloading: the selected route first, then sibling and registered route caches. A covering cache is used directly in place without copying it.
- Downloads a fresh Geofabrik PBF into the selected route only when no existing route cache covers the selection. Purging a cached PBF is the user's explicit refresh mechanism.
- Stores each new Geofabrik PBF under its route's `osm_data\geofabrik` folder and writes a portable `osm_data\osm-cache.json` manifest.
- During **Create OSM/Map Tiles**, atomically writes categorized `route-geodata.gpkg`, its `route-geodata.json` manifest, final selectable `polyveg-polygons.geojson`, and `polyveg-exclusions.geojson` under the active route's `osm_data` folder.
- Exports 12 canonical rural categories as mutually exclusive visible surfaces in the same deterministic drawing order and F3 colors as the LIDEX map: woodland, scrub, heath, wetland, grassland, agriculture, orchard, parkland, golf course, cemetery, sports, and zoo. Every feature includes its exact OSM `styleId`, RGB fill, stable source identity, and useful tags. Permanent road, rail, trail, water, building, developed-land, and bare-ground masks are already carved out; TSRE does not reconstruct stacking or exclusions.
- Builds derivative coverage from the union of the route's actual 2048-meter normal terrain-tile footprints. Habitat layers are clipped to the exact route coverage; roads, railways, water, buildings, and developed context retain a 2048-meter margin.
- Streams a regional PBF once to create a compact, spatially indexed route working cut. Map and PolyVeg processing reuse that cut until the source PBF or terrain footprint changes. Visible-surface stacking runs in bounded terrain sections with a one-foot overlap halo and recombines pieces by stable source identity.
- Stores the 12 rural categories plus water, waterways, buildings, developed land, roads, railways, and bare-ground layers with declared WGS84 coordinates and spatial indexes.
- Rebuilds derivatives when the regional PBF or normal terrain-tile fingerprint changes. Terrain without an object-bearing `.w` file remains part of PolyVeg coverage. A newly downloaded PBF always refreshes the derivatives; an unchanged current package is reused.
- Reads a covering PBF from another route in place while saving the newly sliced derivatives under the active route. Derivatives are validated and atomically replace the previous set only after all outputs pass validation.
- Keeps only the small shared Geofabrik index and cross-route cache registry under `%LocalAppData%\SCOLIDEX`; large PBF data is never stored in AppData.
- Replaces the all-or-nothing exit purge with an unchecked, per-cache list covering only regional PBFs and incomplete downloads. Route derivatives, the small shared Geofabrik index, and generated TSRE map PNGs are never offered for deletion.
- Refines the standard-projection baseline to 11 meters east and 16 meters south.
- Isolates Copernicus naming, discovery, validation, and COG reads in `Program.Copernicus.cs`.
- Enables **Create OSM/Map Tiles** using anonymous Geofabrik regional OpenStreetMap PBF extracts rather than the public OSM editing API.
- Renders one 4096×4096 map overlay per selected 2048-meter normal terrain tile and writes it directly to TSRE's F3 `terrain_maps/<X*10000+Y>.png` cache.
- Overwrites a matching cached PNG without creating a map ACE or changing terrain materials, 16×16 patch UVs, TERRTEX files, or Distant Mountain tiles.
- Projects every OSM vertex through the same corrected tile-local coordinate path used by terrain sampling; the Austria acceptance route reports zero pixel error at tile corners and center.
- Loads compact selected-route geometry once, retains it for the complete run, and renders two 4096 bitmaps concurrently.
- Uses bundled GDAL and TSRE's F3 PNG naming/projection behavior; no API key, external map service, route-editor executable, or legacy MSTS runtime is required.
- Ports TSRE's OSM drawing style: warm paper background, pale land-use fills, outlined buildings, feature ordering, railway treatment, and cased motorway/primary/secondary/tertiary road colors and widths.

### v1.100 — Additional Work

- Uses the calibrated standard-projection terrain baseline of **11 meters east and 16 meters south** to match TSRE map coordinates; v1.100 introduced the correction and v1.200 refines its east/west component.
- Applies the correction at geographic sampling time to both normal terrain and Distant Mountain generation, preserving consistent tile transitions.
- Leaves route-specific `TsreGeoProjection` mapping unchanged; the correction is only the standard-projection baseline.
- Treats Advanced Geo Bias values as additional route-specific offsets on top of the corrected standard baseline.
- Adds `GEOMETRY_PLACEMENT_CORRECTION.txt` with the coordinate comparison, 256-post/8-meter grid explanation, and implementation details.
- Aligns the Windows executable file, assembly, and product metadata with version 1.100.
- Maintains the tracked and distributed `docsMaster` folder and this Markdown README for GitHub and release-page presentation.

### v1.000 — Complete Feature Set

#### Terrain Generation and Source Data

- Builds normal Open Rails/MSTS terrain from USGS 1-meter 3DEP elevation data.
- Falls back through USGS Original Product Resolution data, displayed as **5m~**, and USGS/NED 1/3 arc-second data, displayed as **10m**, when finer coverage is unavailable.
- Samples the native ORTS 256×256 height grid at true 8-meter post spacing. The 256 stored posts cover 2,040 meters inside a nominal 2,048-meter tile; the neighboring tile supplies the next boundary post.
- Uses GDAL windowed raster reads so only the needed parts of source products are read instead of downloading complete DEM files.
- Merges generated terrain in a bounded rolling window to support large routes without retaining the entire route in memory.

#### Coverage and Tile Selection

- Uses existing route terrain tiles as coverage.
- Accepts exact tile names from `ROUTE\SCOLIDEXTiles.txt`.
- Builds coverage from the route marker file, KML coordinates, or track database references.
- Provides configurable normal-terrain and Distant Mountain coverage radii.

#### Normal and Distant Mountain Terrain

- Creates or updates normal route terrain in **Append** or **Overwrite** mode.
- Creates TSRE-style Distant Mountain `lo_tiles` exclusively from Copernicus GLO-30 data, resampled onto the 128-meter low-terrain grid.
- Detects and replaces incompatible DEMEX-style distant mountain terrain when Distant Mountain generation is selected.
- Preserves existing texture, water, overlay, and patch choices during normal updates whenever the source tile layout can be patched safely.
- Includes clean normal-terrain and `lo_tile` templates for first-time creation and recovery from unsupported or damaged terrain files.
- Provides the explicitly destructive **Clean Tile Wipe** option for rebuilding a tile from a clean template.

#### Projection, Placement, and Calibration

- Supports the standard MSTS/Open Rails interrupted Goode homolosine world-tile projection.
- Detects and honors route-specific `TsreGeoProjection` entries for DEM sampling, coverage bounds, markers, KML, and Distant Mountains.
- Provides meter-based north/south and east/west **Advanced Geo Bias** controls for route-specific calibration during DEM generation.
- Includes **Commit/Post Processing** for quickly testing offsets by resampling existing normal or TSRE-style Distant Mountain height grids without another USGS download.

#### Safety, Validation, and Recovery

- Performs a read-only **Scan** before Run and validates route paths, selected tiles, raw-grid readability, decoded coordinates, templates, and only the data sources required by the selected stages.
- Provides **Scan Override** for deliberate advanced or known-good workflows.
- Marks retryable failures so Append can retry them automatically.
- Prints paste-ready failed-tile lists for targeted `SCOLIDEXTiles.txt` retries and separates unmappable failures that cannot be retried by tile name.
- Provides live totals for processed, skipped, failed, 1m, 5m~, 10m, and 30m-global work.
- Supports **Abort** and stops before the next tile or write step.

#### Application and Diagnostics

- Provides a Windows WinForms interface with a formatted, read-only Help viewer.
- Prevents multiple GUI instances from running simultaneously.
- Writes `SCOLIDEX.log` to the user's Desktop with selected settings, projection details, source usage, elapsed time, failures, and separate USGS/Copernicus data-read totals.
- Writes `SCOLIDEX-startup-error.txt` to the Desktop if startup fails before the GUI opens.
- Ships as a self-contained Windows x64 distribution with GDAL, clean terrain templates, documentation, third-party notices, and a desktop shortcut helper.

## Installation

1. Download `SCOLIDEX-v1.400-win-x64.zip` from the [v1.400 release](https://github.com/scottb613/SCO_LIDEX/releases/tag/v1.400).
2. Extract the complete archive to a writable folder.
3. Run `SCOLIDEX-win-x64\SCOLIDEX.exe`.
4. Optionally run `AddShortcutDesktop.cmd` from the extracted top-level folder.
5. Back up a route before using Run, Overwrite, Clean Tile Wipe, or Commit.

## Requirements and Limitations

- Windows 10 or newer, 64-bit.
- Internet access while requesting elevation or Geofabrik map data.
- USGS elevation coverage is United States focused.
- Large routes can require substantial processing time and disk space.
- Map overlays are compressed PNG files in the route's `terrain_maps` folder: 2048×2048 by default or 4096×4096 with **Enable HD Map Tiles**. Route OSM derivatives remain under `osm_data`; regional PBF bulk data remains under `osm_data\geofabrik`. On exit, SCO LIDEX offers only individually checked PBF and partial-download files for purge. Map PNGs and route derivatives are retained.
- Clean Tile Wipe and post-processing terrain shifts modify route files; always work from a backup.
- KML polygon filling is basic and should be considered experimental.
- SCO LIDEX reads `TsreGeoProjection` when present but does not create or modify that route setting.

## Documentation

The [`docsMaster`](docsMaster/) folder contains the complete instructions, changelog, known issues, build notes, geometry-placement technical note, sample tile list, license, third-party notices, and the PolyVeg geodata contract.

## License

SCO LIDEX is distributed under the [GNU General Public License version 3 or later](LICENSE.txt). Third-party components remain under their respective licenses.
