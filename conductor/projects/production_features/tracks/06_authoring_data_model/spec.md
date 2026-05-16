# Track 06: Authoring Data Model — Label Kind + Net Auto-Derive

## Problem

The viz tool's ratline overlay is silent for every `.rkt` file in the
project. Root cause is a two-sided contract gap, not a code regression:

1. Commit `86502f6` ("declared-net rule") added a gate in
   `Net/Ratlines.fs:382` that only counts a label as a net if its text
   appears in the document's `(nets ...)` block. The intent was to
   suppress FET device-terminal labels (`D`/`G`/`S`/`B`) inside primitive
   cells from collapsing into fake nets at the top level.
2. No build script in `scripts/build_*.py` ever populates `doc.nets`,
   and the workflow doc (`docs/workflows/rkt_primitive_workflow.md`)
   tells authors to add labels but never tells them to declare a
   `(nets ...)` block. `grep` across `cell_designs/` finds zero files
   with a `(nets ...)` block.

So the gate fires for everything. The fix isn't backfilling block
declarations — that's per-file toil with no enforcement. The fix is
giving labels an intrinsic role so the consumer can tell signal nets
from device terminals without a name-based filter and without an
external declaration.

## Design

### Label kind

`Rkt.Types.Label` gains a `Kind` property with two values:

- `NetName` — a signal or power net name. Default for hand-authored
  labels and for everything that isn't a FET device terminal.
- `DeviceTerminal` — a FET pin annotation (`D`/`G`/`S`/`B`) emitted by
  primitive generators so Magic's `port makeall` sees it during LVS,
  but explicitly NOT a net name at any composition level.

The kind is intrinsic to the label, not to its container. A custom
primitive may legitimately mix device-terminal labels (its FET pins)
with net labels (an internal bias rail, a substrate-tap connection).
Per-cell granularity would mis-classify those.

Default-on-read is `NetName`, so existing `.rkt` files without the
annotation parse correctly and behave the same as today's
non-primitive content. Only files containing device-terminal labels
need to be regenerated to carry the explicit tag.

### Source of truth for nets

`doc.Nets` (the parsed `(nets ...)` block) stops being read by
in-memory consumers. The authoritative source is the label set itself,
filtered by `Kind = NetName`. The `(nets ...)` block becomes a
writer-emitted manifest: useful as human-readable summary on disk and
for external tooling that wants a quick net list without scanning all
labels, but never trusted as primary by the viz tool or any other
in-process consumer.

This resolves the "loaded doc with empty `doc.Nets`" problem — a
fresh load of a regenerated `.rkt` immediately renders ratlines
because Ratlines walks labels, not the manifest block.

### Writer-time derivation

At serialization, the writer walks the document's labels with
`Kind = NetName`, dedupes by text, and emits the `(nets ...)` block
in the canonical place in the file. Build scripts no longer pass
`nets=[...]` explicitly; the writer derives it. Hand-authored .rkt
files get the same treatment on round-trip.

### Primitive generators

Every primitive generator that emits FET port labels tags them
`Kind = DeviceTerminal` at emit time. Concretely: wherever
`gen_nfet_hv` / `gen_pfet_hv` (and any siblings) add `D`/`G`/`S`/`B`
labels from Magic's `mos_draw` output, the emit path attaches the
kind. Hand-authored labels in non-primitive files stay `NetName` by
default — no per-file changes required for those.

### Viz consumers

`Net/Ratlines.fs` and `Net/LabelFlood.fs` rewrite their label filter
to `Kind = NetName` instead of consulting `doc.Nets`. The
`declaredNets` set and the gate comment block at `Ratlines.fs:372-381`
go away entirely. There is no name-based blacklist anywhere — neither
hardcoded `["D"; "G"; "S"; "B"]` nor a regex. The role is in the
label.

## Non-goals

- Composite primitives or hand-authored blocks that legitimately use
  `D`/`G`/`S`/`B` as net names will continue to work, because their
  labels default to `Kind = NetName`. Only labels explicitly tagged
  `DeviceTerminal` are filtered out.
- The `IsInternal` flag on `Label` (viz/debug labels skipped by
  `ToGds.fs`) is orthogonal and stays as-is. `Kind` describes the
  label's role in the netlist; `IsInternal` describes whether it
  reaches GDS at all.
- Backward compatibility for legacy `.rkt` files is preserved by
  defaulting missing kind to `NetName`. No mass migration of
  hand-authored content is required.

## Test gates

- F# round-trip: read a `.rkt` with explicit `(kind device-terminal)`,
  write it back, parse again — kind survives.
- Python round-trip: same on the Python side of `io/rkt.py`.
- Generator output: re-running `gen_pfet_hv` on
  `pfet_hv_W25p0_L0p5_nf10_core` produces a file with `Kind` tags on
  every `D`/`G`/`S`/`B` label and no other geometry changes.
- Ratline render: opening `cim_reram_drv_phaseA_srcmux.rkt` (after
  regenerating its imported primitives) with ratlines on draws
  ratlines between matching net labels and draws nothing between the
  FET device terminals — verified visually in the running viz app.
- No string blacklist: `grep -E '"D"|"G"|"S"|"B"' tools/viz/src` and
  `src/rekolektion` shows no name-based filtering for FET terminals.

## Open design decisions

These get presented to the user as decision points; the agent does
not pick unilaterally.

1. **F# representation.** A new `LabelKind = NetName | DeviceTerminal`
   discriminated union added to `Rkt.Types`, vs. repurposing the
   existing unused `Label.Class: string option` field with the strings
   `"net"` / `"device-terminal"`.
2. **Sexpr syntax for the annotation.** `(kind device-terminal)`,
   `(class device-terminal)` (matches the `Class` field name and the
   `(port ...)` precedent in the reader), or a compact form.
3. **Relationship to `IsInternal`.** Keep `Kind` and `IsInternal`
   strictly separate (role vs. export-filter, this spec's default),
   or fold the FET-terminal case into `IsInternal` and not introduce
   a new field at all.
4. **In-memory `doc.Nets` after the refactor.** Purge it (reader
   discards, writer derives), or keep it populated as a cache that
   consumers may optionally read.
5. **Regeneration verification.** Mechanism to confirm a primitive
   regeneration diff contains only `(kind ...)` additions and no
   geometric drift — diff-aware test, or before/after geometry hash,
   or visual diff in viz.
