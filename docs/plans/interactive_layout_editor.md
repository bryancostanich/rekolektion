# Interactive Layout Editor (rekolektion-viz v2)

Goal: turn rekolektion-viz from a read-only viewer into a layout editor —
pick instances on the 2D canvas, drag/rotate/mirror them on a snapped
grid, see live dimension lines and DRC violations, and have an MCP-
connected agent loop watching DRC and proposing fixes.

This work lives in a full sibling clone at
`../rekolektion_v2/` (independent repo, branch `viz-v2`), so the
existing `rekolektion/` app + MCP keep working unchanged during
development. Sibling location keeps relative paths to other repos
(`../khalkulo`, etc.) identical to v1.

## Locked decisions

| | Choice |
|---|---|
| What can move | SRef instances only (no top-level paint editing) |
| DRC engine | In-process F# DRC, live during edits; magic round-trip is a manual ground-truth button |
| Edit persistence | First edit copies `foo.mag` → `foo_edited.mag` (or `_edited_2.mag` etc.) and switches the loaded path to the copy; subsequent saves overwrite the copy; Save As supported |
| Snap grid | sky130 mfg grid, 5 nm |
| FIXED_BBOX policy | Original outline drawn as dotted line; live geometry bbox drawn in a second color; `FIXED_BBOX` property + synthetic `<< checkpaint >>` rect both rewritten on save; warn if any other cell `use`s this one |
| Multi-select | Shift-click extends selection; drag any selected → whole selection translates by the same Δ; selection IS the group, no formal group/ungroup mode |
| Rotate / mirror | Space = 90° CCW, X = mirror about X-axis, Y = mirror about Y-axis. ALL THREE work on single OR multi-select; pivot = bbox-of-bboxes center of the selection, snapped to 5 nm grid; single-selection collapses to a centroid = that one instance's bbox center |

## Math: rotate / mirror around a pivot

For each selected instance with current matrix `(linear, origin)`:

```
new_linear = R · old_linear                       ← cell's own orientation
new_origin = R · (origin - centroid) + centroid   ← cell shifts around pivot
```

Where R is one of:

| Gesture | R |
|---|---|
| Rotate 90° CCW | `[[0,-1],[1,0]]` |
| Mirror about X-axis | `[[1,0],[0,-1]]` |
| Mirror about Y-axis | `[[-1,0],[0,1]]` |

With integer origins, integer R, and a grid-snapped centroid,
results stay on the mfg grid by construction. Single-selection
collapses to the same code path (centroid = that one instance's
bbox center).

## Phased implementation

### P0 — selection + drag (translate)

- `tools/viz-v2/src/Rekolektion.Viz2.App/Canvas2D/GdsCanvasControl.fs`:
  pointer-down hit-test against flattened SRef bboxes; track pressed-
  selection set; drag handler updates each selected SRef's
  `Origin.{X,Y}` by the same Δ snapped to 5 nm; pointer-release commits.
- Selection rendering: hatched fill on selected instances; ESC cancels;
  Cmd-Z undoes.
- Snap helper in `Rekolektion.Viz2.Core/Layout/Snap.fs`.
- No file writes yet — edits live in memory.

### P1 — dimension overlay

- New `Rekolektion.Viz2.Render/Skia/DimensionOverlay.fs`: for each
  selected instance, find the nearest co-layer edge of every neighbor
  within a configurable radius, draw arrow + µm label.
- Drawn in the canvas overlay layer, updates each pointer-move tick.
- Toggleable via a button + key.

### P2 — rotate + mirror

- `Canvas2D` keyboard handlers: Space / X / Y operate on the current
  selection (one or many); mutate each instance's full 6-element matrix
  per the math above; centroid drawn as a small marker during keypress.
- Live DRC + dimensions re-evaluated after each transform change (same
  path as drag-release).
- Single-select and multi-select share the same code path.

### P3 — in-process DRC

- New `Rekolektion.Viz2.Core/Drc/` module: per-layer min-spacing,
  min-width, min-enclosure for the layers that matter (li, met1–5,
  diff, poly, contacts).
- Rule numbers sourced from `src/rekolektion/tech/sky130.py` — port the
  relevant subset to F#, with a regression suite of tiny synthetic
  cells (two metal1 rects N nm apart → expect violation iff N <
  min-spacing).
- Incremental check on the moved instances' bbox-expanded neighborhood;
  violations rendered as red edges with rule names.

### P4 — safe-edit + write-back

- New `Rekolektion.Viz2.Core/Mag/Writer.fs`: round-trip the source
  `.mag`, rewriting only the `transform` lines for moved instances +
  the `FIXED_BBOX` property + the `<< checkpaint >>` rect on save.
  Preserves comments / whitespace / unrelated lines.
- New `Rekolektion.Viz2.Core/Layout/EditSession.fs`: on the first edit
  of a clean file, copy `foo.mag` → `foo_edited.mag` (or `_edited_2.mag`
  on collision), rewrite the cellname inside the file to match, retarget
  the loaded path; mark dirty.
- App: title bar `foo_edited.mag • [edited]`, Save / Save As menu items,
  close-with-unsaved prompt, undo stack.
- At save time: scan the file's directory + the configured search path
  for any `use <thisCellName>` references; if found, surface a
  "N parents reference this cell — re-check them" warning.
- Round-trip safety: a load → save (no edits) → diff test in CI must
  be a no-op. Magic comments / timestamps / order preserved.

### P5 — MCP tools for the agent loop

Add to `Rekolektion.Viz2.Mcp/Program.fs`:

| Tool | Effect |
|---|---|
| `viz2_list_instances` | `[{name, cell, originUm, bbox, transform}]` |
| `viz2_move_instance {name, dx_um, dy_um}` | `{ok, drcDelta}` |
| `viz2_rotate_instance {name, degrees}` | `{ok, drcDelta}` |
| `viz2_mirror_instance {name, axis}` | `{ok, drcDelta}` |
| `viz2_get_drc` | `[{rule, layer, p1, p2, distance}]` |
| `viz2_get_neighbors {name, radius_um}` | ranked list of nearest co-layer edges |
| `viz2_save` | writes `_edited.mag` |
| `viz2_save_as {path}` | writes to a chosen path |
| `viz2_run_magic_drc` | manual ground-truth check (P6) |

Each tool maps to a UDS endpoint on the desktop app, identical to the
existing `/open` / `/screenshot` / `/toggle/layer` pattern in
`tools/viz/src/Rekolektion.Viz.Mcp`.

Agent loop is stateless: `get_drc` → pick worst → `get_neighbors` →
`move_instance` → re-`get_drc`. Viz is the source of truth.

### P6 — manual magic ground-truth DRC

- "Run Magic DRC" button + `viz2_run_magic_drc` MCP tool: export current
  edits to a temp `.mag`, run the existing CLAUDE.md DRC heredoc, parse
  `drc listall why`, surface violations alongside in-process ones,
  marked with their source.
- Not in the live loop — only on demand.

## v2 sibling clone

Layout:

```
git_repos/bryan_costanich/
  rekolektion/                      ← unchanged, production
    tools/viz/                      ← rekolektion-viz, viz.sock
  rekolektion_v2/                   ← full clone, branch viz-v2
    tools/viz/                      ← rekolektion-viz-v2, viz-v2.sock
    docs/plans/...
```

Why a full clone (not a worktree or subdirectory):
- Identical project structure → no namespace renames, no assembly-
  name churn.
- Relative paths to sibling repos (`../khalkulo`, `../verifrog`, etc.)
  resolve identically from both copies.
- Each copy is its own independent repo — v2 work doesn't show up in
  v1's git status, no risk of cross-contamination.

Setup:

```bash
# create the full clone next to the original
git clone /Users/bryancostanich/git_repos/bryan_costanich/rekolektion \
          /Users/bryancostanich/git_repos/bryan_costanich/rekolektion_v2

# in the v2 clone: branch off and configure
cd /Users/bryancostanich/git_repos/bryan_costanich/rekolektion_v2
git checkout -b viz-v2
```

Per-instance config (changes inside the v2 clone only):
- UDS path: change the hard-coded `~/.rekolektion/viz.sock` in
  `tools/viz/src/Rekolektion.Viz.Mcp/Program.fs` and
  `tools/viz/src/Rekolektion.Viz.App/.../ScreenshotListener.fs`
  to a configurable env var defaulting to `~/.rekolektion/viz.sock`.
  v2 launchers set `REKOLEKTION_VIZ_SOCKET=$HOME/.rekolektion/viz-v2.sock`.
- MCP registration: `claude mcp add rekolektion-viz-v2 -- dotnet run
  --project /Users/bryancostanich/git_repos/bryan_costanich/rekolektion_v2/tools/viz/src/Rekolektion.Viz.Mcp --no-build`
  with `REKOLEKTION_VIZ_SOCKET` set in the env. Both v1 and v2 servers
  can run concurrently; they don't share the UDS.

The UDS env-var change is small enough to land on `main` first so
both v1 and v2 benefit.

## Risks

- **Per-layer spacing rules**: porting sky130 rule numbers is tedious
  and bug-prone. Mitigate with a small synthetic-cell regression suite.
- **Orientation-aware geometry**: every path (bbox, dimensions, DRC,
  FIXED_BBOX recompute) must handle the eight orientations. Existing
  `Mag.Transform.toSref` decomposes the matrix; `Layout.Flatten.fromSref`
  re-emits it. For interactive edits we mutate the matrix and rely on
  those paths to round-trip cleanly. Solid unit tests on `Transform` +
  `Flatten` are a prerequisite.
- **Magic round-trip safety**: writing back without losing comments /
  timestamps / order is a chronic pain in EDA tooling. CI test:
  load → save (no edits) → diff must be a no-op.
- **External `use` impact**: growing FIXED_BBOX may break other cells
  that instantiate this one. v1 surfaces a warning; auto-fixing is
  out of scope.

## Setup checklist

1. Commit current `rekolektion/main` work (mag-reader scale fix +
   layer-map additions + this plan doc) so v2 starts from a clean
   known-good base.
2. `git clone rekolektion → rekolektion_v2`, branch `viz-v2`.
3. Land the UDS-path env-var change on `main` so both copies inherit it.
4. Register MCP server in v2 clone: `rekolektion-viz-v2` with
   `REKOLEKTION_VIZ_SOCKET=$HOME/.rekolektion/viz-v2.sock`.
5. Begin P0 in `rekolektion_v2` on branch `viz-v2`.
