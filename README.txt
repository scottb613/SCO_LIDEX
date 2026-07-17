SCO LIDEX Terrain Builder
=========================

SCO LIDEX is a utility for building Open Rails / MSTS route terrain from
USGS elevation services. It reads an existing route, finds the selected terrain
tiles, downloads elevation data, builds seamless 8m terrain grids, and writes
the resulting terrain back into the route.

Primary features
----------------
- Builds normal route terrain tiles from USGS 1m 3DEP elevation data.
- Samples normal terrain using the ORTS 256-post raw grid at true 8m post
  spacing, improving alignment with route/map overlays versus stretching the
  raw grid across the full nominal tile footprint.
- Falls normal route tiles back through USGS Original Product Resolution DEM
  shown as 5m~, then USGS/NED 1/3 arc-second elevation shown as 10m.
- Creates TSRE-style distant mountain lo_tiles from USGS/NED 1/3 arc-second elevation.
- Replaces existing DEMEX-style distant mountain tiles with TSRE-style lo_tiles.
- Preserves existing tile texture, water, and overlay choices unless
  "Clean Tile Wipe (Destructive)" is selected.
- Supports selection by existing route tiles, text tile list, marker file, KML
  file, or track database.
- Requires a read-only Scan before Run; the scan validates selected tiles,
  readable raw grids, decoded positions, and representative USGS service status.
- Includes Scan Override for advanced users who intentionally want to run
  without the read-only Scan step.
- Includes a Help button that opens INSTRUCTIONS.txt in a formatted, read-only
  in-app viewer.
- Prevents multiple GUI copies from running at the same time.
- Includes meter-based E/W and N/S Advanced Geo Bias controls for route
  calibration during DEM generation.
- Detects TSRE TsreGeoProjection entries in the route .trk file and uses that
  route-centered projection for DEM sampling when present.
- Includes Commit / Post Processing for quick existing-terrain offset tests
  without USGS downloads. Post Processing resamples existing terrain and can
  slightly soften fixed-grid data; rerun DEM generation with the chosen bias
  for the most accurate final mesh.
- Includes clean terrain tile templates for unsupported or clean-wipe tile rebuilds.
- Scan verifies the clean terrain template is available when
  "Clean Tile Wipe (Destructive)" is selected.
- Writes a run log with GUI settings, elapsed time, and a human-readable USGS
  data-read total to SCOLIDEX.log on the user's Desktop.
- Marks retryable failed normal terrain tiles so a later Append run can retry
  them automatically; paste-ready failed-tile lists are also provided for
  targeted Use Text File retries.
- Writes SCOLIDEX-startup-error.txt to the Desktop if the GUI fails before opening.

Important limitations
---------------------
- USGS coverage is United States focused. Routes outside USGS coverage will not
  have useful elevation data.
- Internet access is required while processing.
- Large routes can take a long time and use significant disk space.
- Always back up the route before running overwrite or clean wipe operations.
- Always back up the route before using Commit / Post Processing.
- Scan does not write route files. Run is the only operation that writes terrain.
- Scan Override bypasses preflight checks. Use it only when you already know
  the route and options are valid.
- TsreGeoProjection support is automatic and route-specific. SCO LIDEX reads
  the statement when present, but does not create or modify it.
- Normal route terrain uses 256 raw height posts at 8m spacing. This is a
  2040m stored post span inside a nominal 2048m tile.

License
-------
SCO LIDEX is distributed under the GNU General Public License, version 3 or later.
See LICENSE.txt.
