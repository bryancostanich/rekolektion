# Route Editing UX — Cross-Editor Research

**Purpose.** Seed planning for the next batch of route-editing features in
`rekolektion-viz` (F#/Avalonia SKY130 layout viewer). We already have basic
wire drawing with walk-around obstacle avoidance; this document surveys how
mature PCB and IC editors let users *modify* already-placed routes, what
patterns are worth stealing, and where chip-layout norms diverge from PCB
norms.

Scope: KiCad 8/9 (PCB), Altium Designer, Cadence Allegro, Mentor PADS /
Xpedition, Autodesk Eagle/Fusion Electronics, plus IC-side tools (Magic,
KLayout, Cadence Virtuoso Layout XL, OpenROAD GUI). Claims are cited;
anything I could not pin down to a primary or near-primary source is marked
**[unverified]**.

> Reading note: PCB editors all share a vocabulary ("push and shove",
> "walkaround", "highlight collisions", "drag/slide", "gloss"). IC editors
> use a different vocabulary ("partial select", "stretch", "chop", "split")
> and largely *do not* try to do collision resolution at edit time. The
> divergence is real and matters for our design — see §9.

---

## 1. Segment dragging

### KiCad 8/9 (PCB) — "Push and Shove" interactive router (PNS)

KiCad exposes three router modes, configured via context menu while routing
or via `E` in the Track tool (KiCad 8 PCB Editor docs):

- **Highlight Collisions.** Manual routing. Clearance violations highlight
  in green; placement blocked unless "Allow DRC Violations" is enabled.
- **Walk Around.** Active trace hugs/walks around obstacles; existing
  geometry is not displaced.
- **Shove.** Active trace walks around immovable obstacles (pads, locked
  tracks/vias) and shoves movable ones out of the way. DRC violations are
  prevented — if no legal path exists, no track is created.

Drag is split into two distinct commands (KiCad PCB Editor docs):

- **`D` — Drag (45-degree mode).** Drags the segment under the cursor while
  preserving 45° posture. If router mode is set to **Shove**, drag also
  shoves neighbouring tracks.
- **`G` — Drag Free Angle.** Splits the segment under the cursor into two
  and drags the new corner *anywhere* (free-angle).
- **`M` — Move.** Also works on track segments; behaves more like a rigid
  move than an elastic drag.

Endpoint behaviour: when dragging a segment whose endpoint terminates at a
pad/via, the endpoint stays anchored and the neighbouring 45° segments
adjust their lengths to keep the corners valid. When dragging an interior
segment, both flanking segments stretch to maintain Manhattan/45° posture.
(Documented behaviour in `kicad-doc/src/pcbnew/pcbnew_interactive_router.adoc`.)

Hover affordance: cursor changes over a track segment vs. an empty area;
the segment under cursor is the one a subsequent `D`/`G`/`M` will operate
on. The KiCad PNS source has a state machine that explicitly enters a
`DRAG_SEGMENT` state on grab (kicad-source-mirror `pcbnew/router/pns_dragger.cpp`,
referenced via DeepWiki).

### Altium Designer — multiple "conflict resolution" modes

Altium's Interactive Router exposes (Altium PCB Editor Interactive Routing
docs):

- **Walkaround Obstacles.** Hug obstacles, do not displace.
- **Push Obstacles.** Displace tracks (and optionally vias if "Allow Via
  Pushing" is on).
- **Hug And Push Obstacles.** Walkaround until clearance is exhausted,
  then push. Default for most users.
- **Stop At First Obstacle / Ignore Obstacles.** Edge cases.

Modes can be cycled mid-route with **`Shift+R`**. Dragging a placed
segment uses the same modes. Altium additionally runs **Loop Removal** —
when a re-route closes a loop and you right-click to finish, the redundant
old segments and vias are auto-deleted (Altium Modifying the Routing docs).

### Cadence Allegro — "Slide" in Etch Edit mode

Allegro's Etch Edit Application Mode (Setup → Application Mode → Etch Edit;
Allegro PCB Editor docs / FlowCAD tips PDF) treats segment dragging as a
first-class "Slide" command:

- Hover an etch segment, press and hold LMB, drag, release to commit.
- The Options panel maps Click/Drag to behaviors — setting Drag = Slide
  is the standard ergonomic configuration.
- **`Tab`** cycles segment selection under the cursor when the data tip is
  ambiguous about which segment you want (e.g. horizontal vs. vertical vs.
  odd-angle under a corner).
- Slide uses a "move-intersect" algorithm in 16.6+, producing smoother and
  more localized edits than the earlier shove-everything heuristic
  (Cadence Allegro 16.6 community blog).

### Mentor PADS / Xpedition — Push/Shove dynamic vs. delayed

Xpedition's sketch router and PADS interactive router are shape-based and
gridless. Two push/shove modes (Siemens "PCB Routing Solutions" blog):

- **Dynamic.** Push/shove applies as you drag.
- **Delayed.** Push/shove waits until the active trace reaches open space;
  produces fewer unnecessary jogs.

LMB-hold-and-drag a segment to slide it (push/shove on neighbours engages
per mode).

### Autodesk Eagle / Fusion Electronics

Eagle 8.1+ added **Push and Shove** as a routing mode toggle in the route
toolbar; DRC and locked objects are always respected (Autodesk Eagle 8.1
release blog). Default trace angle is 90°; right-click during route to
change angle constraint. Eagle's miter command rounds/chamfers a single
corner. Segment dragging itself is more rigid than KiCad/Altium —
historically Eagle relied more on `RIPUP` + re-route than on live segment
sliding **[unverified for current Fusion Electronics build]**.

---

## 2. Node (vertex / corner) manipulation

### KiCad — implicit vertex insertion via "/" posture + drag free angle

KiCad does not expose explicit "add vertex" / "delete vertex" commands on
existing tracks. Instead:

- **`/` — Switch Track Posture.** While the router is active (drawing or
  dragging), `/` toggles the *initial* segment between straight and
  diagonal, effectively flipping which side of an L-shape carries the
  diagonal. Documented in `pcbnew_interactive_router.adoc` ("Pressing /
  or selecting Switch Track Posture from the context menu toggles the
  direction of the initial track segment between straight or diagonal").
- **`G` — Drag Free Angle.** Splits a track segment in two and drops a new
  corner under the cursor. This is the canonical "add a vertex" gesture.
- Deleting a vertex: select the two adjacent segments, delete, re-route.
  No explicit "delete vertex" command. (Forum-confirmed behaviour; not in
  router doc.)

### KLayout — explicit partial editing

KLayout's **Partial mode** (toolbar button or `Edit → Partial`) is the
canonical model for IC-style vertex editing (KLayout manual `partial.html`):

- Click a vertex or edge to select it; drag-rectangle to select multiple.
- Drag a vertex to move it. Drag an edge to move the whole edge (its two
  endpoints translate together).
- **Inserting a vertex.** Click on an edge midpoint to add a vertex there,
  then drag.
- **Deleting a vertex.** Select the vertex, press `Delete`; the path
  regenerates with one fewer segment, the two adjacent segments collapse
  into one.
- **Movement constraint override:** hold `Shift` for orthogonal, `Ctrl`
  for diagonal, `Shift+Ctrl` for any-angle, regardless of the global
  setting. (KLayout manual; partial editing override docs.)

### Cadence Virtuoso Layout XL — partial select via `F4`

- **`F4`** toggles partial select mode.
- Click an edge to select it; click and drag to stretch that edge alone.
- **`S` — Stretch.** Operates on selected edges/vertices.
- **`Ctrl+S` — Split.** Splits a wire/shape at a chosen location.
- **`Shift+C` — Chop.** Cuts a rectangular hole out of a shape.
- (AnalogHub Virtuoso hotkeys; Cadence layout shortcut PDFs.)

### Magic — `stretch` command

Magic does not have a vertex model in the GUI sense; geometry is paint on
a tile plane. The `:stretch` command moves the current selection and fills
in behind it so electrical connections survive. `corner direction1
direction2` creates an L-shaped wire from the box (Magic 8.3 command
reference; opencircuitdesign.com).

### Altium / Allegro

Both support inserting a "kink" by dragging a midpoint of a segment
similar to KiCad's `G`. Allegro's slide command will automatically add
required corners to keep the trace legal when you slide into a constraint.
Altium's "Re-route by drawing over" is a common pattern: redraw the new
path and Loop Removal auto-cleans the old segments.

---

## 3. Width / layer changes mid-trace

### Width

- **KiCad.** While routing, **`W`** and **`Shift+W`** step through the
  track widths configured in Board Setup. **Important limitation:** to
  *change width mid-route on the same trace*, KiCad requires you to end
  the current route and start a new one from the endpoint — the active
  route holds a single width (KiCad PCB Editor docs; confirmed by KiCad
  GitLab issue #9825 and #20031). On already-placed segments, edit
  properties to change width per-segment; there is no "select half a
  trace and change width" gesture.
- **Altium.** Width changes are part of the routing rule stack; you can
  switch active "rule" mid-route with the `*` key on numpad
  **[unverified]**, or edit the segment's properties post-hoc.
- **Allegro.** `Slide` preserves segment width; to change width, use
  `Change Width` from the Etch Edit RMB menu or edit segment properties.
- **Virtuoso.** During an active `path` command, `F3` opens path options
  and lets you change width on the next segment. Partial-select on an
  existing path edge + stretch the parallel edges is the post-hoc analog.

### Layer change (insert via)

- **KiCad.** **`V`** during routing places a through-via at the cursor
  and switches to the next layer in the active pair. **`Ctrl+V`** places
  a microvia, **`Alt+Shift+V`** a blind/buried via. **`<`** opens a
  layer-selection dialog and places a via to the chosen target. Layer
  pair cycling: **`+`/`-`** or **`PgUp`/`PgDn`** for top/bottom (KiCad
  PCB Editor docs).
- **Altium.** Numpad `*` cycles signal layer during routing and places a
  via automatically when allowed by rules **[unverified]**.
- **Allegro.** Add Via from RMB while in Add Connect; layer dropdown in
  Options panel.
- **IC-side (Magic, KLayout, Virtuoso).** Vias are placed as cell
  instances (contact cuts), not as "insert via mid-trace" gestures.
  The user typically draws the new layer's wire, then drops a contact
  device (`getcell` / `pyxic` / PCell) at the junction. KLayout users
  often script via insertion via Python; there is no canonical "press V
  to drop a via" mid-edit.

---

## 4. Push-and-shove vs walk-around vs respect — defaults

| Editor | Default mode | Notes |
|---|---|---|
| KiCad 8/9 | **Shove** (officially recommended) | Walk Around is the second-most-common choice; Highlight Collisions is for users who want zero displacement. |
| Altium | **Hug And Push Obstacles** | Combines walkaround until clearance dies, then push. |
| Allegro | **Slide (push)** in Etch Edit mode | `Shove preferred / Hug preferred` toggle for nuance. |
| PADS / Xpedition | Push/shove with **Dynamic** vs **Delayed** sub-modes | Delayed gives cleaner topology in dense regions. |
| Eagle/Fusion | **Walkaround** historically; Push/Shove since 8.1 (opt-in). | |
| Magic / KLayout / Virtuoso | **Respect.** No automatic neighbour displacement. The user is the router. | This is the IC-world default — see §9. |
| OpenROAD GUI | Inspection-only / scripted (`draw_route_segments`). The GUI is not a manual router. (OpenROAD GUI README.) | |

Takeaway: **PCB defaults are "actively help the user"; IC defaults are
"never surprise the user."** For SKY130 layout, leaning IC-side is the
safer default (see §10).

---

## 5. Length tuning / matched routing

- **KiCad** (`pcbnew_interactive_router.adoc`):
  - **`7`** — Tune single track length; adds serpentine/meanders to reach
    a target.
  - **`8`** — Tune differential pair length.
  - **`9`** — Tune differential pair skew.
  Targets come from net classes / Board Setup.
- **Altium.** Length-tuning ("accordion") tools with min/max amplitude and
  spacing, driven by xSignal rules.
- **Allegro.** "Delay Tune" — similar UX, driven by constraint manager.
- **Virtuoso / IC.** Not a thing in this form. Length matching on-die is
  done at routing-engine time (RC-aware) or by manually drawing jogs;
  there is no "press 7 to add meanders" gesture.

**Relevance to rekolektion-viz: low.** We are not building a length-tuning
feature near-term. Worth noting only because the same shortcut surface
exists in PCB editors and we want to keep `7/8/9` free if we ever add it.

---

## 6. Selection model

### KiCad — escalation via `U`

- Click selects one segment.
- **`U` — Expand Selection.** First press expands to the nearest pad
  (segment → connected segments up to a pad boundary). Second press
  expands to all connected items across all layers (full net).
- Expansion **obeys the Selection Filter**: disable "Vias" in the filter
  and expansion will halt at vias — useful for selecting "this trace up
  to the first via" without grabbing the whole net. (KiCad PCB Editor
  docs.)

### Altium

- Single click → segment.
- Double click → whole trace up to next node.
- `Shift+click` → add to selection.
- `Tab` during interactive routing → enter properties for the live segment.
- "Select Net" / "Select Connected Copper" via right-click menu.

### Allegro

- LMB selects one segment by default; `Tab` cycles candidates under cursor.
- "Find Filter" panel scopes what can be picked (similar idea to KiCad's
  Selection Filter).

### KLayout / Virtuoso — partial vs whole

- Normal select picks the whole path/shape.
- Partial mode (`F4` in Virtuoso, `Partial` button in KLayout) lets you
  pick *one edge or vertex*. This is the IC analog of KiCad's segment
  selection.

### Pattern to steal

The **escalation via repeated keystroke** (KiCad `U`, also Sublime/VS Code
"expand selection") is a clean idiom: one key, repeated, climbs the
hierarchy segment → trace-to-pad → net → connected component. It plays
extremely well with a `SelectionFilter` panel that controls *where the
escalation stops*.

---

## 7. Undo granularity

Documentation on undo granularity is thin for most editors. What I could
verify:

- **KiCad.** Each completed router action (a finished route, a finished
  drag-and-release, a via placement) is one undo step. Mid-route key
  presses (W, V, /, layer changes) are *not* individually undoable —
  pressing `Esc` cancels the live route entirely. After a drag, **one
  `Ctrl+Z` reverts the entire drag**, not segment-by-segment. (Forum
  posts; behaviour stable through 6/7/8/9.) **[partially unverified — no
  single doc page lays this out explicitly.]**
- **Altium.** Undo is per-action; a single shove operation that moved
  several neighbouring traces is *one* undo step **[unverified — common
  user reports]**.
- **Allegro.** Each `Slide` commit is one undo step. Allegro additionally
  has "session checkpoints" via `dbdoctor`/save.
- **Magic / KLayout.** Per-command undo (Magic's `:undo`, KLayout `Ctrl+Z`).
  KLayout undo coalesces a partial-mode drag into one step.

The general convention: **one continuous user gesture = one undo step.**
Intermediate state during a drag (rubber-banding, posture flips, modifier
toggles) is not in the undo stack.

---

## 8. Keyboard-driven editing — what power users rely on

Cheat sheet of the most-cited shortcuts (cited where verified; KiCad
shortcuts are from `kicad-doc/src/pcbnew/pcbnew_interactive_router.adoc`):

### KiCad PCB (interactive router context)

| Key | Action |
|---|---|
| `X` | Start routing a new track from cursor |
| `D` | Drag segment (45° preserved; shoves if mode = Shove) |
| `G` | Drag free angle (splits segment, drops corner) |
| `M` | Move segment (rigid) |
| `/` | Switch track posture (toggle L-shape diagonal side) |
| `V` | Place through via, switch layer |
| `Ctrl+V` | Place microvia |
| `Alt+Shift+V` | Place blind/buried via |
| `<` | Select target layer + place via via dialog |
| `+` / `-` | Cycle layer in pair |
| `PgUp` / `PgDn` | Jump to F.Cu / B.Cu |
| `W` / `Shift+W` | Step through configured track widths |
| `E` | Edit routing options (mode, posture, etc.) |
| `U` | Expand selection (segment → trace → net) |
| `7` / `8` / `9` | Length-tune single / diff-pair / diff-skew |
| `Esc` | Cancel active route, no commit |

### Altium

| Key | Action |
|---|---|
| `Shift+R` | Cycle conflict-resolution mode |
| `Tab` | Edit live trace properties mid-route |
| `*` | Switch layer + auto-place via during route |
| `/` | Toggle 90/45 corner mode **[unverified for current version]** |

### Allegro

| Key | Action |
|---|---|
| `Tab` | Cycle candidate segment under cursor |
| RMB → Slide | Enter slide on selected etch |
| `F` keys | Application-mode-specific (etch edit) |

### KLayout

| Key | Action |
|---|---|
| Toolbar `Partial` | Enter partial mode |
| `Shift` (held while drag) | Force orthogonal |
| `Ctrl` (held while drag) | Force diagonal |
| `Shift+Ctrl` | Any-angle override |
| `Delete` on vertex | Remove vertex, regenerate path |

### Virtuoso Layout XL

| Key | Action |
|---|---|
| `F3` | Open options for active command (width, etc.) |
| `F4` | Toggle partial select |
| `S` | Stretch selected edge/vertex |
| `Ctrl+S` | Split |
| `Shift+C` | Chop |

### Magic

| Command | Action |
|---|---|
| `:stretch <dir> <amount>` | Stretch selection, fill in behind |
| `:move <dir> <amount>` | Move without fill |
| `:corner <dir1> <dir2>` | Generate L-shape under box |

---

## 9. What's different in IC / chip-layout editors

This is the part most relevant to rekolektion-viz, because we are an IC
viewer, not a PCB editor, and the PCB world's defaults can mislead us.

### Geometry model

- **PCB editors** treat traces as polylines with width, layer, and
  endpoints. Vias are first-class objects with a stack-up. A "trace" is
  a named entity.
- **IC editors** (Magic, KLayout, Virtuoso) treat geometry as *paint* on
  layers: rectangles or paths in a hierarchical cell. There is no
  first-class "trace" object — a "wire" is just a path or a set of
  rectangles. Connectivity is *extracted*, not stored. The closest
  analogue to a "trace" is the connected set of geometry on one net,
  recovered by extraction.

### Routing posture

- **PCB:** 45° is the norm; some editors support arbitrary angles.
- **IC:** Manhattan-only (90°) is the universal constraint. Routes live
  on a layer grid; segments are on-track for the layer's preferred
  direction (e.g., met1 horizontal, met2 vertical for SKY130). 45° wires
  exist in analog/RF but are exceptional and DRC-fragile.

### Collision resolution at edit time

- **PCB:** Push-and-shove is the default in modern editors. The router
  reactively rearranges neighbours to keep the active edit legal.
- **IC:** **No push-and-shove.** Magic, KLayout, and Virtuoso do not
  displace existing geometry when you stretch a wire. The user is
  responsible for making space. If your stretch would collide, you get
  collision (paint overlap) or a DRC violation on the next check — but
  no automatic re-routing of neighbours.

  Why? IC DRC is far stricter (min spacing, density, antenna, well-tap
  rules), the layer stack is taller (li1/met1..met5+ in SKY130), and the
  blast radius of moving a neighbour wire silently can break analog
  matching, parasitic balance, or signal integrity assumptions. The
  safer default is "do exactly what I said and tell me if it broke
  something."

### DRC integration

- **PCB:** Live DRC during routing is universal; shove engines optimise
  against the live DRC.
- **IC:** DRC is typically batch (Magic `:drc check`, KLayout DRC
  scripts, Calibre runs). Some editors offer incremental DRC for the
  active cell (Virtuoso), but it is advisory; the editor does not
  *prevent* you from drawing illegal geometry.

### Via insertion

- **PCB:** `V` mid-route is canonical.
- **IC:** Vias are PCell instances or contact cuts. The user typically:
  1. Switches to the target layer (e.g., met2).
  2. Draws the new wire.
  3. Places a via cell (e.g., M1M2_Via) at the junction.
  KLayout users sometimes script this; OpenROAD inserts vias only via
  the detailed router.

### OpenROAD GUI specifically

OpenROAD's GUI (OpenROAD docs, `src/gui/README.md`) is primarily an
**inspector**: DisplayControls for layer/net visibility, the Inspector
panel for selected-object properties, and Tcl scripting (`draw_route_segments`)
to programmatically annotate. **It is not a manual route editor.** The
flow is: run `global_route` and `detailed_route`, inspect results in the
GUI, fix issues by re-running the router with adjusted constraints.
This matters for rekolektion-viz: we are filling a gap OpenROAD does not
fill — *manual* route editing on a SKY130 layout.

---

## 10. Concrete UX patterns to steal — ranked

Ranked from "obvious win, do this first" to "interesting, evaluate later".

### Tier 1 — do these

1. **Two distinct drag modes, two keys.** Steal KiCad's `D` (preserve
   Manhattan, drag segment, neighbours stretch) and `G` (split segment,
   drop a new corner under cursor). Make them feel different. For our
   Manhattan-only world: `D` keeps 90°, `G` splits-and-drops. The
   posture flip (KiCad `/`) is the third key — toggles which side of an
   L carries the dogleg. These three together cover ~80% of real route
   edits.

2. **Hover affordance + Tab to disambiguate.** Steal Allegro's
   `Tab`-cycles-candidate-under-cursor. SKY130 layouts get dense at
   junctions; users will frequently want a specific layer's segment
   under the cursor when three are stacked. Highlight the *current*
   pickable segment in a saturated colour; `Tab` cycles.

3. **Selection escalation via repeated `U`.** KiCad's pattern is great.
   First `U` = this segment plus everything on the same layer up to the
   next via/pin. Second `U` = the whole connected net across layers.
   Pair with a Selection Filter panel so users can scope the expansion.

4. **Three editing-policy modes, user-selectable.** Default to
   **Respect** (IC-safe: never displace neighbours; show collision
   highlight in red). Offer **Walkaround** (our current behaviour for
   drawing). Offer **Shove** behind an explicit toggle. The default
   **must** be Respect — IC users will be burned by a tool that
   silently moves their carefully-placed met1 power straps.

5. **One gesture = one undo step.** Coalesce a full drag (mousedown →
   modifier presses → mouseup) into one `Ctrl+Z`. Posture flips,
   layer toggles, and width changes *during* a live drag should not
   each be undoable; canceling with `Esc` aborts cleanly.

### Tier 2 — strong adds for v1.1

6. **Partial-mode-style vertex editing.** Steal KLayout's partial mode:
   a modal toggle (toolbar button + key, e.g. `F4` like Virtuoso) that
   reveals vertices and edges as draggable handles. Click empty edge
   midpoint → adds a vertex. Click vertex + Delete → removes it, two
   adjacent segments collapse to one. This is the canonical IC idiom
   and our users will expect it.

7. **Modifier-key constraint override during drag.** KLayout's
   `Shift = orthogonal`, `Ctrl = diagonal`, both = free-angle. For us:
   Manhattan-only by default, but `Shift` could override to free-angle
   (off-grid, marked dirty in the wire's provenance). Useful for
   prototyping pre-DRC.

8. **`V`-to-insert-via with layer switching.** KiCad's `V` is the
   most-cited single shortcut in PCB-land for a reason. Bind `V` to
   "drop a SKY130 via at cursor (using YAML rules table for sizing),
   switch active layer to the next in the routing stack." Pair with
   `+`/`-` to step the layer manually without dropping a via.

9. **Width/preset cycling via `W`.** Even though KiCad's W-during-route
   has the painful "must end route to change width" limitation, the
   *shortcut surface* (W = cycle preset widths) is universal muscle
   memory for any PCB-trained user. For IC we have layer-default widths
   (met2 wider than met1 etc.); `W` cycles among configured widths for
   the current layer.

10. **Loop removal on re-draw.** Altium's killer feature: draw a new
    path that overlaps the old; on commit, the redundant old segments
    auto-delete. For our walk-around router, this means: user draws
    over a section of an existing wire, hits Enter, the old loop is
    detected and removed. Big win for "redo this section without
    deleting first."

### Tier 3 — evaluate later

11. **Slide algorithm: move-intersect** (Allegro 16.6+). When a user
    slides a segment that hits a constraint (another wire, a via, a
    cell boundary), automatically introduce corners on the flanking
    segments to keep posture. Hard to get right; defer until basic
    drag feels solid.

12. **JSONL provenance log per edit.** Already in our design notes for
    the routing feature (see `MEMORY` entry "Viz routing feature
    design"). Each drag/insert/delete writes a record; the MCP
    `tail_log` tool surfaces it. This is *better* than any PCB editor
    offers and is a natural fit for an AI-driven workflow.

---

## Editor-by-editor source citations

Primary sources used (verified):

- **KiCad 8 PCB Editor docs**, `docs.kicad.org/8.0/en/pcbnew/pcbnew.html`.
- **KiCad doc source**, `kicad-doc/src/pcbnew/pcbnew_interactive_router.adoc`
  on GitHub.
- **KiCad source (PNS)**, `kicad-source-mirror/pcbnew/router/pns_dragger.cpp`,
  `pns_router.cpp`, via DeepWiki summary and KiCad Doxygen.
- **KiCad GitLab issues** #9825 (cannot change width mid-route), #20031
  (width-change side-effect).
- **Altium Designer Technical Documentation** — "PCB Editor - Interactive
  Routing", "Modifying the Routing", "Re-routing & Rearranging Existing
  Routes".
- **Cadence community / FlowCAD** — "What's Good About Allegro PCB Editor
  New Slide Capabilities?" (community.cadence.com), "Allegro PCB Editor:
  Tips and Tricks SPB 17.2" (flowcad.de PDF).
- **Siemens Mentor blog** — "PCB Routing Solutions: New Interactive
  Routing Styles" (blogs.sw.siemens.com, 2015).
- **Autodesk Eagle blog** — "What's New in Autodesk EAGLE 8.5: Powering
  Up Your Routing"; Push and Shove announcement on Autodesk forums.
- **KLayout manual** — `klayout.de/doc/manual/partial.html`; partial
  editing override docs.
- **Magic** — opencircuitdesign.com command reference for `stretch`,
  `corner`, `move`; Magic man page.
- **Cadence Virtuoso shortcuts** — AnalogHub Cadence tricks; PSU
  CMPEN411 Virtuoso shortcut PDF; UCSB nanofab Virtuoso guides.
- **OpenROAD GUI** — `openroad.readthedocs.io/.../gui/README.html` and
  the upstream `src/gui/README.md`.

Items marked **[unverified]** were referenced in forum posts or
secondary tutorials but I could not pin them to a primary doc in this
research pass. Treat as "probably true, double-check before relying."

---

## Suggested next planning step

Take Tier 1 items (1–5) and write a short design doc that maps each to a
concrete F#/Avalonia interaction in `rekolektion-viz`:

- Which tool/mode in our existing tool palette owns each gesture.
- How the YAML rules table feeds width/via defaults (already in design).
- How the JSONL log records each edit kind so MCP `tail_log` consumers
  can replay or audit.
- Where the Selection Filter lives in the UI.

The Tier 2 items (especially partial-mode vertex editing, #6) should be
sketched but probably land in a second iteration once Tier 1 is settled.
