# Rekolektion Viz — Design Spec

**Date:** 2026-04-24
**Status:** Approved (ready for implementation plan)
**Reference:** `Moroder/src/Moroder.Viz/` (architectural template)

## Goals

A native desktop visualizer for rekolektion's SRAM macros and bitcells, modeled on `Moroder.Viz` but stripped of the orchestration layer. Three primary use cases:

1. **Cell viewer** — open an existing bitcell `.gds` (current `tools/viz/` already produces these via `read | render | mesh`; this just gives them a real interactive surface).
2. **Macro viewer** — open a composed macro (`sram_v2_*.gds`) and see the bitcell array, precharge, column mux, sense amps, write drivers, decoders, and control logic together. Toggle GDS layers, sub-blocks, and individual nets to debug routing/labeling/geometry issues — the kind of work the recent `macro_v2` commits target (full-width rail shorts, label drops on wrong layer, met2/met3 swaps, BL/BR adapter jogs, multi-predecoder pred_out rails).
3. **Run a macro and view output** — invoke the existing `rekolektion macro …` CLI from the app, stream its stderr to a log pane, auto-load the resulting GDS+sidecar when it exits cleanly.

The app must support **headless rendering and screenshot capture** so an agent (Claude) can iterate on macro debugging without a human in the loop.

## Non-Goals

- No daemon, SSE, or run history. Single-process per launch. (Moroder's `Moroder.Orchestration.*` is intentionally not part of this.)
- No synthesis / placement / routing pipeline. The viewer never modifies a GDS, only reads.
- No P&R or DRC integration. DRC stays in `scripts/run_drc.sh` + Magic.
- No multi-instance support in v1. One viewer per launch, one socket.

## Stack

Same choices as `Moroder.Viz`:

- **F# on .NET 10**
- **Avalonia 11.3** + **FuncUI 1.6** + **Elmish 4.3** for the GUI shell
- **SkiaSharp** via `ICustomDrawOperation` for the 2D canvas
- **Silk.NET.OpenGL** on `OpenGlControlBase` for the 3D canvas
- **Avalonia.Headless** + **Skia** for headless render mode
- Hand-rolled HTTP/1.1 over Unix domain socket for the screenshot/command listener (~150 lines, no Kestrel — copy the Moroder shape)

No Python embedding. The app shells out to the existing `rekolektion` Python CLI as a subprocess.

## Architecture

### Project Layout

Replaces the current single-project `tools/viz/Viz.fsproj`. The existing `Gds/`, `Mesh/`, `Render/` source directories are folded into the new structure near-verbatim.

```
rekolektion/tools/viz/
  Rekolektion.Viz.sln
  src/
    Rekolektion.Viz.Core/          # Pure data + logic. No UI deps.
      Gds/Reader.fs                  # GDS binary parser (port from current Gds/)
      Gds/Types.fs                   # Library, Structure, Boundary, Path,
                                     # SRef, ARef, Point — DBU-native
      Sidecar/Loader.fs              # <macro>.nets.json → NetMap
      Sidecar/Types.fs               # NetEntry, PolygonRef
      Net/LabelFlood.fs              # Fallback: build NetMap from GDS labels +
                                     # same-layer geometric connectivity
      Layout/Layer.fs                # Layer (number/datatype/name/color),
                                     # SKY130 layer table
      Layout/Hierarchy.fs            # Sub-block detector — matches GDS structure
                                     # names: precharge_row, column_mux,
                                     # sense_amp_row, wl_driver_row,
                                     # row_decoder, ctrl_logic, sram_array, etc.
      Layout/Picking.fs              # 2D point-in-polygon, 3D ray-vs-extruded-stack
      Visibility.fs                  # ToggleState — Map<LayerKey,bool>,
                                     # Map<NetName,bool>, Map<BlockName,bool>,
                                     # HighlightNet option, derived predicates

    Rekolektion.Viz.Render/        # Rendering only. No Avalonia deps.
      Skia/LayerPainter.fs           # Layer-ordered Skia paths, fill+stroke,
                                     # opacity per layer, pick buffer
      Skia/LabelPainter.fs           # Text rendering with size-stable zoom
      Mesh/Extruder.fs               # Per-layer 2.5D extrusion (port from
                                     # current Mesh/), stack-Z lookup table
      Mesh/Picking.fs                # GPU pick via color-id buffer
      Color/SkyTheme.fs              # SKY130 layer color palette (Magic-style)

    Rekolektion.Viz.App/           # Avalonia + FuncUI + Elmish.
      Model/                         # Model, Msg, Update curried on
                                     # ServiceBackend record (Moroder pattern)
      Services/RekolektionCli.fs     # Subprocess runner: spawns
                                     # `rekolektion macro …`, streams stderr
      Services/ScreenshotListener.fs # Port from Moroder.Viz — UDS HTTP server,
                                     # GET /screenshot → PNG via RenderTargetBitmap
      Services/CommandListener.fs    # NEW — same socket, accepts POST commands
                                     # so agents can drive the running viz
      Services/FileWatcher.fs        # Auto-reload on GDS change (optional)
      View/                          # AppView, TopBar, LeftPanel
                                     # (LayerTree + NetList + BlockTree),
                                     # CanvasTabs (2D | 3D), LogPane,
                                     # RunDialog, RecentFilesMenu, Inspector
      Canvas2D/GdsCanvasControl.fs   # Avalonia Control + Skia
                                     # ICustomDrawOperation
      Canvas3D/StackCanvasControl.fs # Avalonia OpenGlControlBase + Silk.NET.GL
      HeadlessRender.fs              # Port from Moroder.Viz — Avalonia.Headless
                                     # one-shot PNG capture
      App.fs                         # Avalonia App, MainWindow construction
      Program.fs                     # Entry point — calls App.fs

    Rekolektion.Viz.Cli/           # Headless and CLI entry points.
      Program.fs                     # Commands:
                                     #   read   — GDS summary (existing)
                                     #   render — per-layer PNGs (existing)
                                     #   mesh   — STL+GLB 3D models (existing)
                                     #   app    — launches GUI
                                     #   viz-render
                                     #     --gds <file>
                                     #     [--toggle-layer <name>=on|off]…
                                     #     [--toggle-net <name>=on|off]…
                                     #     [--highlight-net <name>]
                                     #     [--tab 2D|3D]
                                     #     [--width 1400] [--height 900]
                                     #     --output <path.png>
                                     # One-shot headless render → PNG → exit.

    Rekolektion.Viz.Mcp/           # NEW — stdio JSON-RPC 2.0 MCP server.
      Program.fs                     # Mirrors Moroder.Orchestration.Mcp pattern.
                                     # Tools (7):
                                     #   rekolektion_viz_render        (headless one-shot)
                                     #   rekolektion_viz_screenshot    (live process)
                                     #   rekolektion_viz_open
                                     #   rekolektion_viz_toggle_layer
                                     #   rekolektion_viz_highlight_net
                                     #   rekolektion_viz_set_tab
                                     #   rekolektion_viz_run_macro

  tests/
    Rekolektion.Viz.Core.Tests/      # Pure tests: GDS round-trip, sidecar load,
                                     # label-flood, picking math, hierarchy
                                     # detection, ToggleState reducer.
    Rekolektion.Viz.Render.Tests/    # Skia paint → RenderTargetBitmap → PNG hash
                                     # against goldens. Mesh extruder fixture
                                     # tests.
    Rekolektion.Viz.App.Tests/       # Update/Msg with stub ServiceBackend.
                                     # Headless integration tests via
                                     # HeadlessApp + golden PNGs.
    Rekolektion.Viz.Mcp.Tests/       # Spawn MCP, send JSON-RPC, assert.

  testdata/                          # Committed fixture corpus:
    bitcell_lr.gds + .nets.json      # single LR bitcell
    bitcell_foundry.gds + .nets.json # single foundry bitcell
    array_4x4.gds + .nets.json       # small array with hierarchy
    macro_64x8mux2.gds + .nets.json  # full small macro
    goldens/                         # PNG goldens for headless render tests
```

### Dependency Graph

```
Core   ←  Render  ←  App
  ↑          ↑
  └──────────┴──── Cli
  └──── Mcp (calls App via headless render or socket)

Tests:
  Core.Tests   → Core
  Render.Tests → Core, Render
  App.Tests    → Core, Render, App (headless)
  Mcp.Tests    → Mcp (subprocess)
```

`Core` and `Render` never import Avalonia. `App.Tests` is the only path that pulls Avalonia into the test runner, isolated to headless integration tests.

## Data Model

### Core types

```fsharp
// Layer.fs
type Layer = {
    Number   : int           // GDS layer number
    DataType : int           // GDS datatype
    Name     : string        // "met2", "li1", "poly", …
    Color    : ColorRgba
    StackZ   : float         // 3D extrusion height (μm)
    Thickness: float         // 3D extrusion thickness (μm)
}

// Sidecar/Types.fs — schema of <macro>.nets.json
type SidecarV1 = {
    Version  : int           // = 1
    Macro    : string        // top-level structure name
    Nets     : Map<string, NetEntry>
}
and NetEntry = {
    Name     : string        // "BL_3", "VPWR", "dec_out_3", "muxed_BL_0"
    Class    : NetClass      // Power | Ground | Signal | Clock
    Polygons : PolygonRef list
}
and PolygonRef = {
    Structure : string       // e.g. "macro_v2_top.sram_array.bitcell[7][3]"
    Layer     : int          // GDS layer/datatype
    DataType  : int
    Index     : int          // ordinal within structure's element list
}

// Visibility.fs
type ToggleState = {
    Layers       : Map<LayerKey, bool>     // LayerKey = (number, datatype)
    Nets         : Map<string, bool>
    Blocks       : Map<string, bool>
    HighlightNet : string option           // dim everything else
    HighlightBlock: string option
}
```

The sidecar JSON is small — for a 64×8 macro, ~17 nets × maybe 200 polygon refs each ≈ 50 KB.

### App model

```fsharp
type Model = {
    Macro       : LoadedMacro option       // GDS + NetMap + Hierarchy
    Toggle      : ToggleState
    Selection   : PickedPolygon option
    ActiveTab   : Tab                      // View2D | View3D — UI label "2D" / "3D"
    View2D      : View2DState              // pan, zoom
    View3D      : View3DState              // camera orbit, ortho/persp
    Run         : RunState                 // Idle | Running of {pid; logLines}
    RecentFiles : string list
    LogVisible  : bool
}
```

The `Update` function is curried on a `ServiceBackend` record of side-effect thunks (resolved once at boot), exactly mirroring `Moroder.Viz`. This is what lets `App.Tests` swap in stubs.

## Data Flow

### Open existing macro

```
User picks file.gds
  → Core.Gds.Reader  → Library
  → Core.Sidecar.Loader (file.nets.json)  ──┐
       ↓ if missing                          │
       Core.Net.LabelFlood ─────────────────►├──► NetMap
                                              ↓
  → Core.Layout.Hierarchy.detect (matches structure names)
  → Render builds Skia draw lists per layer
       + 3D extruded mesh
  → Msg.LoadComplete → both canvases redraw
```

### Run macro

```
User opens RunDialog → fills params → Run
  → Msg.RunMacroRequested
  → Update returns Cmd: Services.RekolektionCli.spawn
       cmd: rekolektion macro --cell <c> --words <w> --bits <b>
                              --mux <m> [feature flags]
                              -o <auto-named output path>
       stderr lines → Msg.LogLine
       on exit 0  → Msg.RunCompleted output_path
       on exit ≠0 → Msg.RunFailed exit_code
  → RunCompleted triggers the Open flow with the new file
```

### Layer / net / block toggle

```
User clicks layer checkbox / net row / block in left panel
  → Msg.ToggleLayer / ToggleNet / ToggleBlock / HighlightNet
  → Update mutates ToggleState (single source of truth)
  → InvalidateVisual on both Canvas2D and Canvas3D
  → Painters consult Visibility predicates per polygon
       2D: skip paint if hidden, dim non-highlighted nets to 15% opacity
       3D: skip mesh chunk if hidden, emissive boost on highlighted net
```

The 2D and 3D canvases never own state — they're functions of `(Library, NetMap, Hierarchy, ToggleState, ViewTransform)`. Toggling a met2 layer in the left panel updates `ToggleState` once; both canvases re-render from the same data. Identical to `Moroder.Viz`'s `DieCanvasControl` reading `Geometry option` as a single property.

### Picking

Click → renderer returns polygon-id → `Msg.PolygonPicked` → inspector panel right side shows layer/datatype/bbox/net/source-structure path. Same `Msg` shape from both canvases. 2D pick uses Skia pick buffer; 3D pick uses GPU color-id render-to-texture.

## UI Layout

```
┌───────────────────────────────────────────────────────────────────┐
│ TopBar: [Open…] [Recent ▾] [Run macro…] · current-file.gds        │
├──────────┬──────────────────────────────────────┬─────────────────┤
│ Layers   │  ┌ 2D ┐ 3D                           │ Inspector       │
│ ☑ met5   │  │                                   │                 │
│ ☑ met4   │  │                                   │ layer: met2     │
│ ☐ met3   │  │      [GDS canvas]                 │ net: BL_3       │
│ ☑ met2   │  │   pan / zoom / click-pick         │ bbox: …         │
│ ☑ met1   │  │                                   │ structure: …    │
│ ☑ li1    │  │                                   │                 │
│ ☑ poly   │  │                                   │ [Highlight net] │
│ ☑ diff   │  │                                   │ [Isolate block] │
│ ─────    │  │                                   │ [Find via stack]│
│ Nets     │  │                                   │                 │
│ ▶ BL_3   │  │                                   │                 │
│ · VPWR   │  │                                   │                 │
│ · WL_15  │  │                                   │                 │
│ ─────    │  │                                   │                 │
│ Blocks   │  │                                   │                 │
│ ▾ macro  │  │                                   │                 │
│   ▸ arr  │  │                                   │                 │
│   ▸ ctrl │  │                                   │                 │
├──────────┴──────────────────────────────────────┴─────────────────┤
│ ▸ Log (collapsed) — last: macro_v2: floorplan: 80×42 μm           │
└───────────────────────────────────────────────────────────────────┘
```

- **TopBar**: file open, recent files dropdown, "Run macro…" button, current file name + DBU/extent stats.
- **LeftPanel**: three sections, all in one scrollable column.
  - **Layers**: checkbox per GDS layer with color swatch. Order: top-down stack (met5 → diff → nwell). "labels" is a layer-like toggle for label visibility.
  - **Nets**: filterable list, grouped by class (power/signal/clock). Click = highlight. Shift-click = isolate. Search box at top.
  - **Blocks**: tree of structures detected by `Hierarchy`. Click = isolate. Checkbox = toggle.
- **Center**: tab strip (`2D` default, `3D`), then canvas. Toolbar at top-right of canvas: zoom in/out, fit, ortho/perspective (3D only).
- **Inspector**: read-only details for the selected polygon, plus action buttons.
- **Log**: collapsed strip at bottom; click to expand into a 200px-tall pane streaming subprocess stderr.

Toggle state is shared between tabs — switching from 2D to 3D preserves which layers/nets/blocks are visible.

## Net Data Sources

**Primary — generator sidecar JSON.** rekolektion's `macro_assembler.py` knows every wire's net at draw time. We add a small change: as the assembler emits polygons, it accumulates a `nets: dict[str, list[PolygonRef]]` and dumps it next to the GDS:

```json
{
  "version": 1,
  "macro": "sram_v2_weight_512x32mux8",
  "nets": {
    "BL_3":   { "class": "signal", "polygons": [
                  { "structure": "sram_array.bitcell[0][3]",
                    "layer": 68, "datatype": 20, "index": 4 },
                  …]},
    "VPWR":   { "class": "power",  "polygons": [ … ] },
    …
  }
}
```

The sidecar is generated for every `rekolektion macro` invocation that goes through `macro_assembler.py`. Cell-only generators (`bitcell/sky130_6t_lr.py`, etc.) get a similar sidecar as a follow-up.

**Fallback — label flood.** For GDS files generated before the sidecar lands, or hand-edited macros, `Core.Net.LabelFlood` derives nets from labels: for each labeled point, find polygons on the same layer that contain or touch the label position, then flood-fill across same-layer overlap. Works for ~80% of nets in practice; flags unrooted polygons as net-unknown. Logs a warning.

## Headless Mode & Agent-Driven Iteration

This is what makes the app self-debuggable without a human in the loop. Two surfaces, both ported from `Moroder.Viz`:

### Live-process listener

When the GUI is running, `Services/ScreenshotListener` binds a Unix domain socket at `~/.rekolektion/viz.sock`. It serves:

- `GET /screenshot` — returns a PNG of the current MainWindow render via `RenderTargetBitmap`. Same pixels the user sees.

`Services/CommandListener` extends the same socket with `POST` endpoints accepting JSON bodies:

- `POST /open` — `{ "path": "..." }` — load a GDS into the running window.
- `POST /toggle/layer` — `{ "name": "met3", "visible": false }`.
- `POST /toggle/net` — same shape.
- `POST /highlight/net` — `{ "name": "BL_3" }` (or `null` to clear).
- `POST /tab` — `{ "tab": "2D" | "3D" }`.
- `POST /select/at` — `{ "x": 12.4, "y": 8.1, "units": "um" }`.

All commands marshal to the UI thread via `Dispatcher.UIThread.InvokeAsync`, dispatch the corresponding Elmish `Msg`, then return 200. A `?then=screenshot=true` query suffix on any POST returns the post-action PNG inline (saves a round trip).

### Headless one-shot

`HeadlessRender.fs` (ported from Moroder) boots Avalonia under `Avalonia.Headless` with `UseSkia()` and `UseHeadlessDrawing=false`, constructs `MainWindow`, applies a sequence of toggles, pumps the dispatcher for `holdMs`, captures via `CaptureRenderedFrame()`, writes PNG, exits. No socket, no display, no human. Invoked via `rekolektion-viz viz-render --gds … --output …`.

### MCP server

`Rekolektion.Viz.Mcp` is a stdio JSON-RPC 2.0 server mirroring `Moroder.Orchestration.Mcp`. Seven tools:

| Tool | Backed by | Purpose |
|---|---|---|
| `rekolektion_viz_render` | Headless one-shot | Open file, apply toggles, capture PNG, return as MCP image content. No live process needed. |
| `rekolektion_viz_screenshot` | Live socket `GET /screenshot` | Capture current state of running viewer. |
| `rekolektion_viz_open` | Live socket `POST /open` | Load file in running viewer. |
| `rekolektion_viz_toggle_layer` | Live socket `POST /toggle/layer` | |
| `rekolektion_viz_highlight_net` | Live socket `POST /highlight/net` | |
| `rekolektion_viz_set_tab` | Live socket `POST /tab` | |
| `rekolektion_viz_run_macro` | Subprocess (same as GUI dialog) | Generate a new macro, return path to GDS+sidecar, optionally open it in the running viewer. |

Closed-loop iteration without a human:
1. Agent calls `rekolektion_viz_render { gds: …, toggles: { met3: off }, highlight: "BL_3", tab: "3D" }` — gets a PNG back, no GUI.
2. Or with the GUI open: `rekolektion_viz_open` → `rekolektion_viz_toggle_layer` → `rekolektion_viz_screenshot`.

## CLI Surface

```
rekolektion-viz read   <file.gds>                    # GDS summary (existing)
rekolektion-viz render <file.gds> <out_dir/>         # Per-layer PNGs (existing)
rekolektion-viz mesh   <file.gds> <out_dir/>         # STL + GLB (existing)
rekolektion-viz app    [<file.gds>]                  # Launch GUI, optionally
                                                     # opening a file
rekolektion-viz viz-render
    --gds <file>
    [--toggle-layer <name>=on|off]…
    [--toggle-net <name>=on|off]…
    [--highlight-net <name>]
    [--tab 2D|3D]
    [--width <px>]   (default 1400)
    [--height <px>]  (default 900)
    [--hold-ms <ms>] (default 500)
    --output <path.png>                              # Headless one-shot
```

Existing `read | render | mesh` commands preserve their args byte-for-byte so the entries in `rekolektion/CLAUDE.md` and any scripts keep working. New commands are additive.

## Error Handling

- **GDS parse error** — `Msg.LoadFailed { path; byteOffset; reason }` → toast + log entry. Existing macro stays loaded.
- **Sidecar missing** — silent fall-back to `LabelFlood`, log a warning ("`<file>.nets.json` not found, deriving nets from labels — some segments may show as net-unknown").
- **Sidecar/GDS mismatch** — log a warning per inconsistency, prefer sidecar where it exists.
- **Subprocess (`rekolektion macro …`) fails** — exit code + last 50 stderr lines surfaced in the run dialog; full stream remains in the log pane.
- **3D context creation fails** (older GL stack) — 3D tab disabled with explanation banner; 2D works as normal.
- **Socket bind fails** (stale lock from previous run) — same recovery as Moroder: `File.Delete socketPath` before bind, retry once. Log if still failing.
- **MCP tool call against a non-running viewer** — for live-only tools, return JSON-RPC error with `code = -32001` and message "viz process not running; use `rekolektion_viz_render` for headless one-shot".

## Testing

| Project | Strategy |
|---------|----------|
| `Core.Tests` | Pure F#, no Avalonia. GDS round-trip on fixture files. Sidecar JSON load + schema validation. `LabelFlood` correctness on hand-crafted fixtures. Picking math (point-in-polygon, ray-vs-extruded). `ToggleState` reducer. `Hierarchy` detection from structure-name patterns. |
| `Render.Tests` | Skia paint to `RenderTargetBitmap` against fixture GDS, assert on PNG hash for known visual states. Mesh extruder: vertex count + bbox match per fixture. |
| `App.Tests` | Update/Msg tests with stub `ServiceBackend` (no Avalonia process). Headless integration tests boot `HeadlessApp`, load a fixture GDS, capture PNG, diff against goldens — same path Moroder.Viz.Tests use today. |
| `Mcp.Tests` | Spawn the MCP binary, send JSON-RPC requests over stdio, assert response shape. Mirrors Moroder's MCP test pattern. |

Fixture corpus (committed under `tools/viz/testdata/`):

- `bitcell_lr.gds` + `.nets.json` — LR bitcell (smallest case)
- `bitcell_foundry.gds` + `.nets.json` — foundry bitcell
- `array_4x4.gds` + `.nets.json` — small array exercising hierarchy
- `macro_64x8mux2.gds` + `.nets.json` — full small macro
- `goldens/*.png` — PNG goldens for a handful of common toggle states (all-on, met3-only, BL_3-highlighted, sram_array-isolated, 3D-tab default)

Goldens regenerate via `dotnet test ... --filter UpdateGoldens` (gated by env var) when intentional visual changes land.

## Phasing

### Phase 1 (MVP — ships everything functional)

1. Multi-project scaffold + `.sln` + project references; CI builds.
2. Port `Gds/`, `Mesh/`, `Render/` from current `tools/viz/` into `Core` + `Render`.
3. Core: `Sidecar.Loader`, `LabelFlood`, `Hierarchy`, `Visibility`, `Picking`, `Layer` table.
4. App scaffold: Avalonia + FuncUI + Elmish wiring, MainWindow, TopBar, LeftPanel skeleton, LogPane, RunDialog.
5. 2D canvas: `GdsCanvasControl` with Skia ICustomDrawOperation, layer paint, label paint, pan/zoom, pick.
6. 3D canvas: `StackCanvasControl` on `OpenGlControlBase` + Silk.NET, extruded layer mesh, orbit/ortho, pick via color buffer.
7. Toggles: layer/net/block + highlight, fully wired to both canvases.
8. RunDialog → `RekolektionCli` subprocess → stderr stream → auto-load result.
9. `HeadlessRender` + `ScreenshotListener` + `CommandListener` (UDS HTTP).
10. `Cli` project — preserves existing `read | render | mesh`, adds `app`, `viz-render`.
11. `Mcp` project — 7 tools.
12. Python: small change in `src/rekolektion/macro/macro_assembler.py` to emit `<macro>.nets.json` alongside the GDS.
13. Test corpus + goldens.

### Phase 2 (polish, post-MVP)

- Probe markers — drop a 2D pin, see corresponding via stack lit in 3D.
- Multi-tabs (multiple GDS files open at once).
- Screenshot export from the menu.
- Block hierarchy auto-detection if structure-name matching proves unreliable across cell variants.
- File watcher → auto-reload on `.gds` change.
- Sidecar emission for cell-only generators (currently scoped only to `macro_assembler.py`).

## Risks & Open Questions

1. **3D inside Avalonia, on Apple Silicon.** Moroder.Viz is 2D-only — we don't have a worked example of `OpenGlControlBase` + Silk.NET in the existing codebase. Mitigation: spike this in week 1; if it's a tar pit, fall back to a separate native window for 3D (still launched from the F# app process). Worst case, Phase 1 ships 2D + headless and 3D moves to Phase 1.5.
2. **Sidecar source of truth.** The `macro_assembler` change is small but the rekolektion codebase has multiple paths that draw polygons (peripheral generators, raw GDS imports for foundry cells). Some polygons may not have a known net at draw time. The label-flood fallback handles this, but coverage will be uneven on first ship. Acceptable for MVP.
3. **Label flood correctness.** Real labels can land on `li1` vs `met1` vs `met2`, and Magic's LVS extraction is the actual reference. If LabelFlood diverges, we may want to add an option to source the NetMap from a Magic `.ext` extraction file instead. Out of MVP scope; folder structure (`Core/Net/`) is left open for additional sources.
4. **Coordinate system.** GDS is integer DBU + DBUperμm. All `Core` types stay DBU-native; conversion to μm happens only at the inspector display layer. Z-stack heights are in μm. This matches the existing `tools/viz/Gds/Types.fs` conventions.
5. **Socket cleanup on crash.** `~/.rekolektion/viz.sock` and `<...>.lock` need stale-cleanup on bind, like Moroder. Lifted directly.
6. **MCP server discovery.** Like `moroder-mcp`, the MCP server walks up from CWD looking for a marker (rekolektion's `pyproject.toml` works as the project root). Multi-instance is out of v1 — one socket, one viewer.
