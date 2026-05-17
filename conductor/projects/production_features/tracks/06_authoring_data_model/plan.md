# Track 06: Authoring Data Model — Plan

Status: `pending`

Spec: [spec.md](spec.md)
Continuation prompt: [continuation_prompt.md](continuation_prompt.md)

## Steps

Each step is its own local commit. Push only on explicit user
approval. Stop at the spec's open design decisions before writing
any code on the step that depends on them.

### 1. Audit pass — no code changes

- [ ] Read `tools/viz/src/Rekolektion.Viz.Core/Rkt/Types.fs` —
      current `Label` shape, `IsInternal`, `Class`.
- [ ] Read `tools/viz/src/Rekolektion.Viz.Core/Rkt/Reader.fs` around
      label parsing — how forms are recognized, where `Class` is
      populated.
- [ ] Read `tools/viz/src/Rekolektion.Viz.Core/Rkt/Writer.fs` —
      `synthesizeNetsBlock`, label emit, current `(nets …)` emission.
- [ ] Read `tools/viz/src/Rekolektion.Viz.Core/Net/Ratlines.fs`
      lines 348–445 — declared-net gate, anchor flow.
- [ ] Read `tools/viz/src/Rekolektion.Viz.Core/Net/LabelFlood.fs` —
      label-driven net derivation, any name filter.
- [ ] Read `src/rekolektion/io/rkt.py` — `Label` dataclass,
      `_emit_nets_block`, parser path.
- [ ] Locate every primitive generator that emits FET port labels
      (`src/rekolektion/primitives/` — start with `gen_nfet_hv`,
      `gen_pfet_hv`, follow callers).
- [ ] Report findings to user; surface the open design decisions
      from spec.md "Open design decisions" with the table format
      from `feedback_decision_table_format.md`.

### 2. F# data model + reader + writer

Gated on decisions 1–4 from spec.md.

- [ ] Add `LabelKind` (or repurposed `Class`) to `Rkt.Types`.
- [ ] Update `Reader.fs` to parse the chosen sexpr form; default to
      `NetName` when absent.
- [ ] Update `Writer.fs` to emit the kind annotation when not the
      default, and to derive `(nets …)` from labels filtered by
      `Kind = NetName` instead of reading `doc.Nets`.
- [ ] Round-trip test: parse → write → parse, `Kind` preserved.
- [ ] Commit.

### 3. Python data model + reader + writer

- [ ] Mirror the chosen representation in `Label` dataclass.
- [ ] Update Python parser + writer (auto-derive `(nets …)`).
- [ ] Round-trip test: parse → write → parse, kind preserved.
- [ ] Commit.

### 4. Primitive generators

- [ ] Add the kind tag at every FET port label emit site
      (`D`/`G`/`S`/`B`) found in step 1.
- [ ] Hand-authored labels in primitive composites stay untagged
      (default `NetName`).
- [ ] Commit.

### 5. Regenerate primitives

Gated on decision 5 (regen verification mechanism).

- [ ] Run every primitive generator in `src/rekolektion/primitives/`
      that produces files in `cell_designs/primitives/`.
- [ ] Verify diff is tag-only (no geometric drift) using the
      mechanism chosen in decision 5.
- [ ] Commit the regeneration as one mechanical commit, message
      naming the primitives regenerated.

### 6. Viz consumers

- [ ] `Net/Ratlines.fs`: remove `declaredNets` set and the gate at
      lines 382–387; remove the comment block at 372–381; filter
      labels by `Kind = NetName` directly.
- [ ] `Net/LabelFlood.fs`: same filter, no name-based blacklist.
- [ ] Update / add unit tests covering: primitive labels with
      `DeviceTerminal` kind don't produce ratline pins; net labels
      do; declared-net gate is gone.
- [ ] Build and run viz app, open
      `cell_designs/khalkulo/cim_reram_drv_phaseA_srcmux.rkt`,
      toggle ratlines, confirm visible ratlines between matching
      net labels and no spurious lines between FET terminals.
- [ ] Commit.

### 7. Workflow doc

- [ ] Update `docs/workflows/rkt_primitive_workflow.md` "Naming nets"
      section to describe the kind model.
- [ ] Note that primitive generators emit `Kind = DeviceTerminal`
      automatically; hand authors don't tag.
- [ ] Note that `(nets …)` block auto-derives at write time; no
      manual declaration step.
- [ ] Remove the D/G/S/B gotcha section's "rule: never use these as
      net names" wording — replace with the kind-based explanation.
- [ ] Commit.

### 8. Final verification

- [ ] `grep -E '"D"\|"G"\|"S"\|"B"' tools/viz/src src/rekolektion`
      confirms no name-based filter remains.
- [ ] All round-trip tests pass.
- [ ] Viz ratline render works on a freshly-regenerated file with
      no manual intervention.
- [ ] Stop. Ask user before push.

## Decisions log

Track decisions made during the work here as they happen. Format:
`YYYY-MM-DD — Decision N (spec.md): chose X because Y.`

2026-05-16 — Decision 1 (F# representation): chose A — new
`LabelKind = NetName | DeviceTerminal` DU on `Rkt.Types.Label`.
Repurposing `Class` would overload one slot with two unrelated
meanings (style hint vs. netlist role); a dedicated DU keeps the
two axes structurally separate and lets the type system enforce
the value space.

2026-05-16 — Decision 2 (sexpr syntax): chose A — `(kind device-terminal)`.
Pairs cleanly with Decision 1's separate field, avoids overloading
the existing `(class …)` form which already lives on `Port` /
`Net` with different meanings, and keeps the wire format
self-documenting.

2026-05-16 — Decision 3 (relationship to `IsInternal`): chose A —
keep separate. `IsInternal` is the GDS-export filter; folding the
two would suppress FET terminal labels from the GDS and break
Magic's `port makeall` LVS extraction. They describe orthogonal
axes (role-in-netlist vs. export-filter); divergent cases like
"named internal net" need both flags settable independently.

2026-05-16 — Decision 5 (regen verification): chose A — a
diff-aware comparator that parses old vs. new `.rkt`, ignores the
new `(kind …)` annotation, and reports element-level
discrepancies. When something does go wrong on a future
tag-only schema change, "which rect moved" is the diagnostic we
actually want; hash-only loses that signal.

2026-05-16 — Decision 4 (in-memory doc.Nets): chose C — **delete
the `(nets …)` block entirely**, not A (purge field) or B (keep
as cache) from the original spec. An audit found that no consumer
reads `Net.Voltage`, `Net.Domain`, or `Net.NetClass`; the whole
block round-trip exists to preserve metadata that has zero
observable effect. Cleaner to remove the apparatus (reader,
writer, `Document.Nets`, `Net` record / dataclass) than to ship
a dead-letter format feature. Spec.md updated to reflect.
