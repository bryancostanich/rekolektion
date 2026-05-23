# Route Editing — Feature Plan

Companion to [`route_editing_research.md`](route_editing_research.md), which
surveys how mature PCB and IC editors handle route editing. This document
takes that research and turns it into a sequenced plan for
`rekolektion-viz`.

Scope: edit operations on **already-committed** wires (segments, vertices,
widths, vias). Drawing new wires is already done.

## Guiding principles

1. **Respect is the default.** IC users will not tolerate a tool that
   silently moves their power straps. Edits never displace neighbours
   unless the user explicitly opts in to a Shove / Walkaround mode for
   that operation. (Research §4, §9, §10 Tier 1.4.)

2. **Manhattan-only.** Wires stay axis-aligned. `Shift` may unlock
   free-angle for prototyping, but the default is 90°-only and the on-grid
   snap is non-negotiable. (Research §9 geometry model.)

3. **One gesture, one undo.** A whole drag (mousedown → modifier presses
   → mouseup) coalesces into a single `Cmd+Z`. Live BG walk-around
   recomputes during the drag are not undoable; the only undoable artifact
   is the committed result. (Research §10 Tier 1.5.)

4. **Existing infrastructure carries through.** The walk-around router,
   live DRC overlay, single-flight LiveDrc dispatch, obstacle cache, and
   snapshot-paired undo are all reused. Edit operations are new producers
   into the same pipeline.

5. **Provenance log is mandatory.** Every edit emits a structured JSONL
   record (the same `viz.log` we already use) — drag from/to, vertices
   added/removed, layer switches, etc. MCP `tail_log` exposes this for
   any AI workflow on top. (Research §10 Tier 3.12.)

## v1 — what ships first

A minimum that's good enough that the user stops avoiding the tool.
Three operations and a hover affordance:

### v1.1 — Segment drag (`D`)

Pick up a committed segment by its body, drag it perpendicular to its
axis, drop it on `mouseup`. Flanking segments stretch to keep the wire
connected at its endpoints. Drag is **Respect** mode: neighbours never
move; if the dropped position would collide, the live-DRC overlay marks
red and a snap-back option appears (Esc cancels; Enter commits anyway).

Data:
- New `Msg.SegmentDragStart of WireSegmentRef * pickup: int64*int64`,
  `Msg.SegmentDragMove of int64*int64`, `Msg.SegmentDragCommit`,
  `Msg.SegmentDragCancel`.
- A live `DraftEdit` analogue of `DraftRoute` that holds the proposed
  geometry — fixed segments and the dragged segment's new position —
  without mutating the Document until commit.
- On commit: `pushUndoSnapshot`, replace the affected RectEls in the top
  cell, run the same incremental Nets update used by `commitRouteWith`.

Hit-testing:
- A wire segment hit-tests as the bbox of its RectEl(s) for that layer.
  `WireSegmentRef` resolves to one `(Cell, RectIndex)` pair (or more
  for posture-decomposed L-shapes).
- Hover affordance: segment under cursor gets a saturated outline; the
  cursor changes to a horizontal/vertical drag arrow depending on the
  segment's axis. (Research §10 Tier 1.2.)

### v1.2 — Vertex add / delete

`A` while hovering an empty edge midpoint inserts a vertex at that
position. The segment splits into two; both halves remain straight until
the user drags one of them. `Delete` on a vertex collapses its two
adjacent segments back into one (if collinear), or refuses (if doing so
would change the wire's path).

This is the IC-canonical "partial mode" workflow per Research §2 and §10
Tier 2.6.

Data:
- `Msg.VertexInsert of WireSegmentRef * int64*int64`,
  `Msg.VertexDelete of VertexRef`.
- `VertexRef = (WireId, VertexIndex)` — requires us to give committed
  wires identifiers. See §"Wire identity" below.

### v1.3 — Tab to disambiguate

At dense junctions (a met1 wire overlapping a li1 wire under an mcon),
the segment under cursor may be ambiguous. `Tab` cycles through the
candidates; the highlighted one is the target of the next D/A/Delete.
(Research §10 Tier 1.2.)

### v1 — explicitly NOT in scope

- Shove / Push routing (defer to v2).
- Width change mid-segment (defer to v2; KiCad has documented limitations
  here per Research §3).
- Layer change / via insertion via `V` (defer to v2 — needs Pads-style
  via cell instancing, larger change).
- Loop removal (Tier 2.10) — strong feature but algorithmically distinct;
  defer until segment drag feels solid.
- Length tuning / matching — not relevant for chip layout.

## Wire identity

The Document today carries RectEls in cell order with no notion of "wire".
A drag operates on a *segment* (one RectEl), but the user thinks in
*wires* (the polyline). Vertex add/delete needs to know which adjacent
segments belong to the same wire.

Two options:

| | A — Wire-aware overlay | B — On-the-fly inference |
|---|---|---|
| Storage | Add a `WireId` field to RectEl provenance; wires are first-class | Wires are reconstructed from net + connectivity at edit time |
| Migration | New field; old `.rkt` files lack it; need a fallback | None — works on any file we can open |
| Drag UX | Trivial: pick segment → all RectEls with same WireId stretch | Inference walks the same-net adjacent RectEls on each edit |
| Risk | Round-trip through `.mag` / `.rkt` writer must preserve the tag | Inference correctness on weird topology (T-junctions, branches) |
| Order | Blocks v1 | Doesn't block v1 |

**Decision: Option A.** `WireId` is added to RectEl provenance from
v1. The driver is the MCP path — an AI workflow needs first-class
addressable wires ("show me all edits on wire X", "drag wire Y by
40 nm", "delete the third segment of wire Z"). Inference-based
identity collapses under those use cases because IDs change every
time the underlying RectEls are renumbered.

Concrete consequences:
- Schema bump for `.rkt`: RectEl gains an optional `WireId` tag.
  Reader treats missing as `None`; writer round-trips when present.
- `.mag` round-trip: tag piggybacks on a Magic property; if Magic
  drops it, the round-trip degrades to inference (acceptable
  fallback, not the primary path).
- WireIds are assigned at commit-time inside `commitRouteWith` —
  monotonic per-document, never reused on undo/redo.
- MCP `get_geometry` / `get_selection` start returning `WireId` on
  each segment; new MCP verbs (`drag_wire`, `delete_wire_segment`,
  etc.) take WireId as input.

## v2 — next layer up

After v1 lands and feels solid:

1. **Three explicit modes — `Respect / Walkaround / Shove`.** Picker in
   the toolbar; per-operation override via Shift. Respect remains default.
2. **`V` to insert via + switch layer.** Requires the via-cell scaffolding
   currently sketched in the routing feature design memo.
3. **Width change via `W`.** Cycle among configured widths for the active
   layer. Requires segment-width to be a per-segment attribute (today
   it's per-draft).
4. **Loop removal.** Detect when a fresh draft overlaps an existing
   committed wire of the same net and remove the redundant section on
   commit. (Research §10 Tier 2.10.)
5. **Selection escalation via `U`.** First `U` selects segment-on-layer;
   second `U` selects net across layers. (Research §10 Tier 1.3.)

## v3 — IC-specific power features

These are not in the PCB world and exist only because we're chip-layout:

1. **Per-layer width presets driven by the YAML rules table.** No
   guessing; the table is authoritative.
2. **Auto-via at layer-change request.** When `V` fires, the
   appropriate SKY130 via cell (mcon + met1, via1 + met2, etc.) is
   instanced at the cursor with the right enclosure rules.
3. **DRC-aware drag.** During segment drag, the live DRC engine already
   runs against the proposed geometry; we add a "snap to nearest legal
   position" gesture (`Shift+drop` or similar) that resolves an
   in-flight violation by nudging to the closest DRC-clean offset.

## Implementation order

Each row is one cycle of "design decision check → implement → test → commit".

1. **`WireId` plumbing.** Add `WireId : int option` to RectEl
   provenance. Assigner inside `commitRouteWith`. Reader/writer
   round-trip. Sidecar / MCP geometry getters surface it.
2. **Wire-segment hit-test + hover affordance.** Cursor-over-segment
   visual + WireId-based resolution. No edit op yet.
3. **`Tab` to cycle candidates** at dense junctions. Standalone.
4. **`D` — segment drag** with flanking-segment stretch using WireId
   to identify the same-wire stretch group. Full operation, Respect mode.
5. **`A` — vertex insertion.**
6. **`Delete` — vertex removal.**
7. **Decision point: continue with v1 closure or jump to v2 mode picker
   (Walkaround / Shove).** Re-evaluate against user feedback.

## Snap policy

Decided:

- **Default snap = DBU** (5 nm manufacturing grid). No enforced
  track alignment.
- **Snap toggle on → regular snap grid** (configurable; defaults
  to the layer routing pitch from the rules table). Cursor /
  drag-body position rounds to that grid.
- **Endpoint exception:** the first and last anchors are always
  the exact coordinate of the snap target (pin/pad/via center).
  Endpoint coordinate always beats grid coordinate. The first
  segment off the start anchor and the last segment into the end
  anchor act as short connectors from the off-grid endpoint to
  the first on-grid corner.

Same rule applies to drag: the dragged segment's endpoints stay
tied to the flanking segments (or to pins) — only its
perpendicular position rounds. This means drawing and editing
produce the same on-grid invariant, and wires can land cleanly on
pins that don't happen to fall on the grid.

## Endpoint behavior during drag

Decided:

- **Anchors stay put.** When the user drags a segment whose
  endpoint is attached to a pin, pad, or via, the anchor does NOT
  move. The wire grows whatever extra corners it needs (an L,
  S-jog, or staircase) to bridge from the fixed anchor to the new
  position of the dragged segment.
- **`Shift`-drag overrides** to move the anchor along with the
  dragged segment. Available for the explicit "I really meant to
  reposition the attachment" case.

**Why:**
- Pin / pad coordinates are a contract with the symbol or LEF.
  Silently moving them would break LVS.
- A via is shared between two wires on different layers; moving
  it with one drag could orphan the wire on the other side.
- The corner-adding behavior matches the IC norm
  (Magic / KLayout / Virtuoso) and gives a predictable visual
  outcome — the wire deforms to reach the new position rather
  than the anchor sliding silently.

**How to apply:** During drag commit, classify each endpoint of
the dragged segment as `Mid-wire` (touches a same-wire flanking
segment) or `Anchored` (attached to a non-wire feature). For
each:

- Mid-wire: stretch the flanking segment to the new endpoint
  position. Same axis remains, only length changes.
- Anchored: insert one or two new corner segments between the
  fixed anchor coordinate and the new dragged-segment endpoint
  position. Posture (HorizontalFirst / VerticalFirst) chosen to
  match the dragged segment's axis (so the new corners stay
  visually contiguous with the existing wire).

## Open questions

All v1 architectural questions resolved. Snap to grid (DBU grid? routing grid?
   layer-specific?), or free placement clamped to DBU only? Default
   answer: snap to the smallest-pitch routing grid for the layer.
3. **Behaviour at endpoints attached to pins/vias.** Pin-attached
   endpoint must stay anchored. Via-attached endpoint can either stay
   anchored (drag stretches the flanking segments) or move the via with
   the drag. Default answer: anchor. User override via modifier.
4. **Multi-segment selection.** Out of scope for v1; v1 is one-segment
   one-operation.
