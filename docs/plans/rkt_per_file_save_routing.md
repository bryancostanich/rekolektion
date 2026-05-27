# Per-file save routing in the viz App

Status: feature request, pre-decision. Open gap explicitly named in
[`docs/io/rkt.md`](../io/rkt.md) under "Open gaps (v1)":

> Save-routing per imported file isn't tracked in the App yet —
> edits to a cell defined in an imported file currently write into
> the root file on save. The cell-origin metadata exists at the
> reader layer; the App's editor just doesn't consult it yet.

## Why this exists

The `.rkt` format's `(import …)` form is the schema's answer to
multi-file projects: a macro file imports primitive files, and the
loader merges every cell into one in-memory library. The Reader
already preserves the **source path** for every cell — i.e.,
`Library.cell_index : Map<cellName, path>` tells you which file a
cell was defined in.

The App's Save path does not consult this map. When the user edits
a cell defined in an imported file (e.g., a primitive inside
`cell_designs/primitives/nfet_hv_W1p2_L1p0_core.rkt`) and saves the
open document (e.g., `cell_designs/my_block/my_block.rkt`), the
edited primitive's geometry is written back into the parent file
instead of its origin file. The next time the parent is reopened,
the original imported primitive shadows the edit — silently — and
the user's work appears to vanish.

This is a save-path bug that the **interactive layout editor**
work (`docs/plans/interactive_layout_editor.md`) explicitly depends
on. The editor's mutation model assumes that the file you edit is
the file you save into.

## Goals

- **Edits land in their source file.** A cell defined in
  `primitives/nfet_hv_W1p2_L1p0_core.rkt` and edited via the App
  saves back into that same file, not the parent that imported it.
- **Cross-file edits are explicit in the UI.** When the user is
  about to save edits that touch multiple files, the Save dialog
  enumerates the files being written so the user can see the blast
  radius before committing.
- **Save As reroots.** Save As on the *open document* (the root)
  forks the entire `Library` to a new path set, **preserving the
  import graph shape**. Edits made during the Save-As flow continue
  to land per-file in the new locations.
- **No silent file creation.** If a cell exists in the in-memory
  library but its `cell_index` source path is `None` (synthesized,
  never written), Save prompts for a target path rather than picking
  one heuristically.
- **Round-trip preservation per file.** Each saved file goes through
  `Writer.write` against its own `Document`, so comments and
  formatting in unedited regions of an imported file survive
  (mirrors the `canonical_layout_format_decisions.md` D5
  comment-preservation contract).

## Non-goals

- **Adding new schema forms.** Per-file routing is purely an App /
  Reader / Writer wiring change. `(import …)` semantics in
  `rkt.md → Imports` already cover the multi-file case.
- **Merging or splitting cells across files.** Out of scope: a cell
  belongs to exactly one source file at any time. "Move cell to a
  different file" is a separate feature.
- **Version control integration.** The App doesn't need to
  understand git, just write each file correctly. PR / commit
  flows stay manual.

## Locked decisions (to ratify)

| | Choice |
|---|---|
| Cell-to-file mapping source | Reader's existing `Library.cell_index : Map<cellName, path>` — the metadata is already there. |
| Cells with `cell_index = None` | New cells synthesized in-App. Save prompts for target path on first save; subsequent saves remember it. |
| Save dialog when ≥2 files change | Modal list of all affected files + cell counts, with checkboxes to opt out per file (defaults to all checked). |
| Save-As behavior | Reroots the entire library: parent + all imported files copy to mirror locations under the new path. UI shows the file map before commit. |
| Atomicity | Write all changed files to `.tmp` siblings, then rename. If any write fails, roll back the renames. |
| Conflict detection | Per file: mtime check at load + before save. If file changed on disk since load, prompt: Reload-and-Reapply / Overwrite / Cancel. |

## API / data flow

```fsharp
// Already exists in Reader.fs (per rkt.md → F# API):
type Library = {
    Documents: Map<string, LoadedDocument>   // path -> doc
    CellIndex: Map<string, string>           // cellName -> path
    // …
}

// Editor mutates AST in memory. To route saves:

module SaveRouter =
    /// Compute per-file diffs from the current Library state vs the
    /// last-known on-disk state. Returns one Document per file that
    /// needs writing.
    val diffByFile :
        original: Library ->
        current: Library ->
        Map<string, Document>

    /// Atomic multi-file write. Writes to .tmp siblings then renames.
    /// Returns the new on-disk Library snapshot on success.
    val saveAll :
        diffs: Map<string, Document> ->
        Result<Library, SaveError>

type SaveError =
    | MtimeConflict of path: string * onDiskMtime: DateTime * loadedMtime: DateTime
    | WriteFailure of path: string * inner: exn
    | OrphanCell of cellName: string    // no path in cell_index, no user choice provided
```

The router is a pure-ish function on `Library` pairs; the App
service layer handles the prompts and the `File.Exists` /
`File.Move` side effects.

## UX

### Single-file edits (the common case)

Save behaves exactly as today: write the open file, done. No new
dialog. The router computes `diffByFile`, sees one entry, writes it.

### Multi-file edits (the new case)

Modal on Save:

```
You are saving edits to 3 files:

  ☑ cell_designs/my_block/my_block.rkt        (1 cell changed)
  ☑ cell_designs/primitives/nfet_hv_W1p2_L1p0_core.rkt  (1 cell changed)
  ☑ cell_designs/primitives/pfet_hv_W2p0_L2p0_core.rkt  (1 cell changed)

[ Save Selected ]  [ Cancel ]
```

Unchecking a file leaves its in-memory edits intact but doesn't
write the file. Use case: experimenting in a primitive but not
ready to commit that part of the change.

### Save As

```
Save As → /path/to/new_root.rkt

Files that will be written:

  /path/to/new_root.rkt                              (was my_block.rkt)
  /path/to/primitives/nfet_hv_W1p2_L1p0_core.rkt     (was …same…)
  /path/to/primitives/pfet_hv_W2p0_L2p0_core.rkt     (was …same…)

[ Save All ]  [ Cancel ]
```

Mirroring rule: imported files copy relative-path-preserved into the
new root's directory. If the user picks a path where some imported
files already exist on disk, the dialog flags each with an
"Overwrite?" confirmation per file.

### Conflict

If `nfet_hv_W1p2_L1p0_core.rkt` was edited externally since the App
loaded it:

```
File changed on disk since load:
  cell_designs/primitives/nfet_hv_W1p2_L1p0_core.rkt

  [ Reload and Re-apply Edits ]   [ Overwrite ]   [ Cancel ]
```

`Reload and Re-apply` re-parses the on-disk version, attempts a
three-way merge of the App's pending edits against it, and if any
edit no longer applies cleanly, drops to a per-cell prompt. Stretch
goal; v1 can omit and treat reload-and-reapply as
"reparse-and-warn-on-conflict."

## Acceptance criteria

- **A1.** Open `cell_designs/my_block/my_block.rkt` (which imports
  two primitives), edit one cell from each primitive + one cell in
  the root, Save. Three files are written, each with only its own
  cell's changes; mtimes confirm no unrelated files touched.
- **A2.** After A1, close + reopen — every edit survives in its
  origin file.
- **A3.** Save As to a sibling directory: three files appear at the
  new path, import graph intact, original three untouched.
- **A4.** External edit detection: load file, edit, externally touch
  the file's mtime + content, attempt Save → conflict prompt fires.
- **A5.** Atomic-write rollback: simulate a write failure on the
  third file of a three-file save (test helper that throws once on
  a specific path); confirm the first two `.tmp` files are removed
  and the originals are untouched.
- **A6.** Orphan-cell prompt: synthesize a new cell in-App, attempt
  Save before assigning it a path → `OrphanCell` raises and the
  Save dialog prompts for a target file.
- **A7.** Comments preserved per-file: the unedited regions of every
  written file go through `Writer.write` byte-for-byte against their
  pre-edit form (modulo the per-cell AST edits the user actually
  made).

## Implementation phases

### P0 — `diffByFile` core

- New module
  `tools/viz/src/Rekolektion.Viz.Core/Rkt/SaveRouter.fs` (or under
  `Rkt/`). Pure function on `(Library, Library) -> Map<path, Document>`.
- Unit tests against synthetic library pairs.

### P1 — `saveAll` with atomic rename

- Tmp-file + rename. Roll back on any failure.
- Unit tests on the rollback path (mock filesystem or use a temp
  dir + injectable IO).

### P2 — App integration: single + multi-file Save

- Replace the App's current "write open file" Save handler with the
  router-driven path.
- Multi-file dialog. Checkboxes optional per file.

### P3 — Save As reroot

- Mirror the import graph into the new root directory. Overwrite
  confirmation per pre-existing target.

### P4 — Conflict detection + (stretch) merge

- Mtime check at load + before write. Prompt on mismatch. v1:
  reparse-and-warn. v2: three-way merge.

### P5 — Orphan-cell prompt

- `OrphanCell` from the router triggers a Save-As-for-this-cell
  modal.

## Risks

- **Filesystem races.** Two App instances open on the same file is
  the obvious bad case. v1 detection is mtime-based; not race-free.
  Document the assumption; consider lockfiles in v2 if it bites.
- **Path normalization.** Two import paths that resolve to the same
  file via different relative routes must collapse to one
  `Library.Documents` entry. The Reader already canonicalizes paths;
  verify in P0 that the router uses the canonicalized form.
- **Comment drift on per-file write.** If `Writer.write` is run
  against the *modified* AST for a file that had only one cell
  edited, comments in unmodified cells must survive. The
  `canonical_layout_format_decisions.md` D5 contract covers this;
  A7 enforces.
- **Interaction with the v2 editor's `_edited.mag` copy-on-write.**
  The interactive editor plan uses a "first edit copies foo.mag →
  foo_edited.mag" strategy for `.mag` files. This plan is purely
  about `.rkt`; the policies don't need to match, but should be
  documented side-by-side so the App's Save UX doesn't fork
  inconsistently between formats.

## Open questions

- **What about edits that affect the import graph itself**
  (adding/removing an `(import …)` line)? Out of scope for v1 — the
  router treats imports as fixed during a save. Document that the
  user must save, close, edit imports manually, reopen.
- **Should Save All-button be the default in the multi-file dialog,
  or Save Open File Only?** Default = Save All. Saving everything is
  the common case; opting out is the exception.

## Implementation status (2026-05-27)

All phases landed end-to-end.

**Core (P0, P1, P4-detection, P5-detection, projection)**:
- `tools/viz/src/Rekolektion.Viz.Core/Rkt/SaveRouter.fs`.
- `diffByFile`, `saveAll` (atomic tmp+rename + rollback),
  `detectMtimeConflicts`, `orphanCells`, plus
  `projectIntoLibrary` — projects the in-memory merged Document
  back into per-file Documents using the snapshot's CellIndex.
  Cells without a source mapping land in the root file as
  orphans.
- 10 Core tests cover the API:
  `tools/viz/tests/Rekolektion.Viz.Core.Tests/SaveRouterTests.fs`.

**App load (SR2)**:
- `LoadedMacro` gained `LibrarySnapshot: Rkt.Reader.Library option`
  and `LibraryMtimes: Map<string, DateTime>`.
- `Services/GdsLoading.load` captures both for `.rkt` files via
  `Reader.loadSingle`. Other formats stay `None` and use the
  legacy single-file save path.

**App routed save (SR3)**:
- New `tools/viz/src/Rekolektion.Viz.App/Services/RoutedSave.fs`
  with `plan`, `execute`, and `saveOrSurfaceBlockers`.
  `Backend.SaveMacro` now routes `.rkt` saves through
  `RoutedSave` and falls through to the single-file
  `EditSession.saveTo` for non-`.rkt`.
- `Msg.SaveCompleted` re-reads the saved Library and refreshes
  the snapshot + mtimes so subsequent saves use the post-write
  ground truth.

**App dialogs (SR4–SR7)**:
- `tools/viz/src/Rekolektion.Viz.App/View/SaveDialogs.fs` ships
  four modal `Window` subclasses:
  - `MultiFileSaveDialog` — list of changed files with per-file
    checkboxes; fires when ≥2 files would be written.
  - `ConflictDialog` — Reload-and-Reapply / Overwrite /
    Cancel when `detectMtimeConflicts` returns non-empty.
  - `OrphanDialog` — per-orphan target-file textbox (defaults
    to the root) when `orphanCells` returns non-empty.
  - `SaveAsRerootDialog` — preview of mapped paths when Save As
    targets a different directory and the macro has imports.
- App.fs's File-menu Save handler runs `runRoutedSave` async,
  driving the dialog chain (Conflict → Orphan → MultiFile) before
  executing the write. Save As runs `runRoutedSaveAs`, computing
  the mirror mapping for the import tree and routing through
  `SaveAsRerootDialog`.

**Test coverage**:
- 10 Core SaveRouter tests pass.
- Full F# suite (476 Core + 14 Render + 54 App + 4 MCP) green
  with all the new wiring in place.

## Files affected

- `tools/viz/src/Rekolektion.Viz.Core/Rkt/SaveRouter.fs` (new).
- `tools/viz/src/Rekolektion.Viz.App/Services/` — new
  `SaveService.fs` wrapping the router with the App's IO + dialog
  surface.
- `tools/viz/src/Rekolektion.Viz.App/View/` — multi-file Save
  dialog, Save As reroot dialog, conflict-prompt dialog.
- `tools/viz/tests/Rekolektion.Viz.Core.Tests/SaveRouterTests.fs`
  (new) — A5, A6, A7 against synthetic libraries + temp dirs.
- `tools/viz/tests/Rekolektion.Viz.App.Tests/` — A1, A2, A3, A4
  integration tests.
- `docs/io/rkt.md` — strike the save-routing bullet from "Open gaps"
  once P3 lands.
