# Layers panel — per-layer DRC visibility column

**Status:** spec, pre-implementation
**Owner:** Bryan
**Target:** `tools/viz/` (F# Avalonia desktop app)

## Goal

Add a third checkbox column ("D") to each row in the Layers panel of the viz
left-side panel. Toggling **D** for a layer controls whether DRC violation
tiles involving that layer are drawn on the canvas. Default all-ON, persists
across app restarts in the existing `~/.rekolektion/session.json`, and uses
the same click + swipe-paint behavior the existing layer-visibility column
uses.

This complements the existing layer-visibility column ("V") and lets the user
silence DRC noise on layers they don't care about right now without losing
either the polygon view of that layer or the DRC overlay everywhere else.

## Non-goals

- Per-rule toggle (e.g. "hide met1.2 but show met1.1"). Out of scope; the
  filter is per-layer only.
- Per-net DRC filtering. The Nets panel keeps its H/R columns unchanged.
- A global DRC on/off master at the top bar. The "D" master toggle at the
  Layers section header is sufficient; bigger surface than that isn't needed.
- Re-styling existing rows or changing color-swatch / name typography.

## UI

### Row layout

Each Layers row currently renders as:

```
[ color swatch ][ V checkbox ][ layer name ]
```

After the change:

```
[ color swatch ][ V checkbox ][ D checkbox ][ layer name ]
```

A header strip above the first row places column letters above the two
checkbox columns:

```
                  V D
Layers
[ ▓ met1 row ]
[ ▓ met2 row ]
[ ▓ poly row ]
...
```

The "Layers" section title stays where it is (top of the section); the V/D
column letters live in a thin sub-row directly above the first layer row,
right-aligned to sit above the V and D cells. The V and D letters are styled
to match the H/R legend the Nets section already uses (small font, `#bbb`
foreground).

### Click and drag-paint semantics

Each checkbox cell becomes its own click + drag-paint target. This is a
behavior change from today, where the whole row clicks the V checkbox; under
the new design, only the V cell controls V, and only the D cell controls D.
The layer name reverts to a non-clickable label. The Nets panel's `netRow`
already works this way (H and R cells each own their drag-paint); we are
making Layers consistent with that pattern.

Drag-paint, per column:

- `PointerPressed` on a V or D cell: flip that row to the OPPOSITE of its
  current state, record the flipped state as the in-flight drag target,
  record which column kind the drag is painting.
- `PointerEntered` on another row's cell of the SAME column kind while the
  left mouse button is still down: paint that row to the in-flight drag
  target.
- `PointerEntered` on a cell of the OTHER column kind: ignored. Starting a
  swipe on V does not paint D rows it passes through, and vice versa.
- `PointerReleased` anywhere: disarm the drag.

Stale-closure trap: the V cell already documents (in `layerRow`,
`tools/viz/src/Rekolektion.Viz.App/View/LeftPanel.fs`) that FuncUI reuses
`Border` instances across renders without rebinding the press lambda. The D
cell must obey the same rule — read row state via
`Services.AppDispatch.currentModel` at press time, never from a closure
capture. The Nets panel's `netCell` already does this with its `readLive`
function parameter; the layer-D handler will follow the same shape.

### Header master toggle

The Layers section header already has a master select-all button for V. A
parallel master button gets added for D with the same all/none flip behavior:
empty → full, non-empty → empty. The two master buttons stack horizontally
next to the "Layers" title, in the same DockPanel right-side position the
existing button uses.

## Data model

### `ToggleState` (`tools/viz/src/Rekolektion.Viz.Core/Visibility.fs`)

Add one map and parallel API. No change to existing fields.

```fsharp
type ToggleState = {
    Layers          : Map<LayerKey, bool>
    DrcVisibleLayers: Map<LayerKey, bool>   // NEW
    Nets            : Map<string, bool>
    Blocks          : Map<string, bool>
    HighlightedNets : Set<string>
    VisibleRatlines : Set<string>
    IsolatedBlock   : string option
    ActiveLayer     : LayerKey option
}

let isDrcVisibleForLayer (s: ToggleState) (key: LayerKey) : bool =
    Map.tryFind key s.DrcVisibleLayers |> Option.defaultValue true

let setDrcVisibleLayer (key: LayerKey) (visible: bool) (s: ToggleState) : ToggleState =
    { s with DrcVisibleLayers = Map.add key visible s.DrcVisibleLayers }

let setAllDrcVisible (keys: LayerKey seq) (visible: bool) (s: ToggleState) : ToggleState =
    let next =
        keys |> Seq.fold (fun acc k -> Map.add k visible acc) s.DrcVisibleLayers
    { s with DrcVisibleLayers = next }
```

`empty` gets the additional `DrcVisibleLayers = Map.empty` initializer (which
together with the `defaultValue true` lookup yields "all visible by default").

### Filter predicate

A `Check.Violation` (declared in
`tools/viz/src/Rekolektion.Viz.Core/Drc/Check.fs` per the existing layout)
carries 0, 1, or 2 layer fields depending on its rule kind — width rules have
one layer; spacing / overlap / enclosure rules have two; transistor and
well-area rules can be layerless. The filter rule:

```
keep_violation(v, ts) :=
    let touched = layers_of(v)
    touched.IsEmpty OR Set.exists (isDrcVisibleForLayer ts) touched
```

In other words: a violation that doesn't carry any layer association
(transistor "no bends", `well.2a`, `x.2`, etc.) is always shown — layerless
rules are immune to the toggle. A violation that DOES touch one or more
layers is hidden iff every touched layer's D is OFF. This is the **AND**
semantics from the brainstorming — a 2-layer rule like `via.5a` (met1
overlap of via1) is hidden only when BOTH met1 and via1 have D OFF.

The function `layersOf: Check.Violation -> Set<LayerKey>` is a small helper
that pulls the layer fields off whichever `Rule` discriminant the violation
came from. It lives in a new module
`tools/viz/src/Rekolektion.Viz.Core/Drc/Filter.fs` (alongside the existing
`Check.fs` / `Rules.fs` in that directory). The `Drc.Filter` module also
exposes the `keepViolation` predicate the call site uses. Keeping it
separate from `Check.fs` keeps the violation-detection code free of the
visibility-toggle concern. Tests pin `layersOf` behavior per rule kind.

## Persistence

`tools/viz/src/Rekolektion.Viz.App/Services/SessionState.fs` currently
serializes each layer to `{"n": <number>, "d": <datatype>, "v": <visible>}`.
The existing `d` field is the GDS datatype; we therefore use `drc` (not `d`)
for the new field to avoid collision.

New per-layer record shape:

```jsonc
{"n":68, "d":20, "v":true,  "drc":true},
{"n":69, "d":20, "v":false, "drc":true},
{"n":66, "d":20, "v":true,  "drc":false}
```

Backward-compat: missing `drc` field reads as `true` (matches the "default
all-ON on first run" rule and any pre-upgrade session.json files).

`State.Layers` (declared at the top of `SessionState.fs` as a list of tuples)
changes from `(int, int, bool)` — `(n, d, v)` — to `(int, int, bool, bool)`
— `(n, d, v, drc)`. `serialize`, `parse`, and `persistFromModel` each touch
this tuple shape once; the change is uniform.

## Render integration

`DrcOverlay.render` already takes a `Check.Violation array`. The filtering
happens at the call site in
`tools/viz/src/Rekolektion.Viz.App/Canvas2D/GdsCanvasControl.fs` — find the
existing `DrcOverlay.render canvas vb …` invocation (one instance). Walk
the violations array immediately above that call, drop the hidden ones
using the predicate above, pass the filtered array as the last argument
to `render`.

`DrcOverlay.fs` itself is untouched — keeping the overlay file ignorant of
the toggle state preserves its unit-testability against synthetic
`Check.Violation` arrays.

## Messages

`tools/viz/src/Rekolektion.Viz.App/Model/Msg.fs` gets two messages parallel
to the existing `ToggleLayer` and `SetAllLayers`:

```fsharp
| ToggleDrcLayer of key: LayerKey * visible: bool
| SetAllDrcVisible of visible: bool
```

`Model/Update.fs` handles each by delegating to the new `Visibility.fs`
mutators and persisting via the existing `SessionState.persistFromModel`
path. The persistFromModel call is already wired for every layer-affecting
message; the new messages slot in identically.

## Drag-paint state

The current `LeftPanel.fs` has module-level mutable state for the V drag
(`dragActive: bool`, `dragTarget: bool`, `dragVisited: Set<LayerKey>`) — UI
thread only, mutated by row handlers. The new D drag needs its own parallel
state OR a unified state that records the column kind.

Pick: unified state with a `LayerDragKind` discriminator, matching the
already-extant `NetDragKind` pattern in the same file.

```fsharp
type private LayerDragKind = | Visibility | Drc

let mutable private layerDragKind   : LayerDragKind voption = ValueNone
let mutable private layerDragTarget : bool = false
let mutable private layerDragVisited: Set<LayerKey> = Set.empty
```

The existing `dragActive: bool` collapses into `layerDragKind = ValueNone`
vs `ValueSome _`. `PointerEntered` filters on `k = thisCellKind` so painting
stays in-column. `endDragPaint` resets all three. Removing the legacy
`dragActive/dragTarget/dragVisited` triple keeps a single drag-paint state
machine per panel (Layers, Nets) instead of three (Layers-V, Layers-D, Nets).

## Tests

Pure-function tests, no Avalonia required:

- `Visibility.fs` tests
  - `isDrcVisibleForLayer` defaults true for unknown keys.
  - `setDrcVisibleLayer` adds / overwrites the right map entry.
  - `setAllDrcVisible` accumulates over the seed map (matches `setAllLayers`).
- Filter-predicate tests (against `Check.Violation` samples)
  - Width rule (1 layer): hidden when that layer's D is off.
  - Spacing rule (2 layers): visible when EITHER layer's D is on; hidden
    only when BOTH are off.
  - Layerless rule: visible regardless of `DrcVisibleLayers`.
  - Empty `DrcVisibleLayers` map: everything visible (default).
- Session JSON round-trip
  - Serialize with mixed `drc` values; parse, confirm restored.
  - Parse a legacy entry without `drc`; confirm `true` default.
- Drag-paint state machine test (no Avalonia, exercising the module-level
  state machine directly via the public Msg.* dispatch hook)
  - Press on V cell of layer A while D is in-flight: ignored.
  - Press on V cell of layer A: V state flips; drag arms with kind = V.
  - Enter V cells of B, C, D in succession: each gets painted target.
  - Enter D cells of B, C: ignored (column mismatch).
  - Release: drag disarms, subsequent enters ignored.

## Files touched

| File | Why |
|---|---|
| `tools/viz/src/Rekolektion.Viz.Core/Visibility.fs` | new `DrcVisibleLayers` field + helpers |
| `tools/viz/src/Rekolektion.Viz.Core/Drc/Filter.fs` (new) | `layersOf` + `keepViolation` predicate |
| `tools/viz/src/Rekolektion.Viz.App/Model/Msg.fs` | two new messages |
| `tools/viz/src/Rekolektion.Viz.App/Model/Update.fs` | dispatch handlers for the new messages |
| `tools/viz/src/Rekolektion.Viz.App/View/LeftPanel.fs` | row layout change, header strip, D cell, drag-paint unification |
| `tools/viz/src/Rekolektion.Viz.App/Canvas2D/GdsCanvasControl.fs` | filter `violations` before passing to `DrcOverlay.render` |
| `tools/viz/src/Rekolektion.Viz.App/Services/SessionState.fs` | tuple shape, serialize / parse / persistFromModel |
| `tools/viz/tests/Rekolektion.Viz.Core.Tests/VisibilityTests.fs` (or similar) | unit tests above |

No changes to `DrcOverlay.fs` itself, no changes to the Nets panel, no
changes to any rendering code beyond the call-site filter.

## Risks / open questions

- **Row hit-target sizing.** The current layer row uses a wrapping `Border`
  for the whole row as a click target. Splitting into per-cell handlers
  shrinks the hit area for V from "whole row" to "11 × 11 px square plus
  6 px padding". Net rows already work this way with no reported usability
  complaints, so we expect the same here. If the cells feel too small in
  practice, the fix is widening each cell's `Border.padding` rather than
  reverting to whole-row hit testing.
- **Drag-paint across panel switches.** Existing drag state is module-level
  mutable. If a user starts a Layers-V drag, switches tabs, and finishes the
  drag elsewhere, state can leak. Same risk as today for V alone; unified
  state doesn't make it worse. Out of scope to fix here, noted for tracking.
- **Color swatch ↔ D collision.** The visual rhythm of `[swatch][V][D] name`
  puts three small squares in a row. Color swatch is 10 × 10 px with a
  `#555` border; V and D indicators are 11 × 11 px with a `#888` border and
  rounded corners. The size + border distinction should be enough to read
  swatch-vs-checkbox at a glance, but if it's not, we add a 2 px extra
  spacing between swatch and V to reinforce the grouping.
