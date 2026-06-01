module Rekolektion.Viz.Core.Drc.Rules

/// SKY130 DRC rule subset for the interactive editor.
///
/// Values ported from `src/rekolektion/tech/sky130.py` plus a few
/// extras (min-area) that aren't in the Python table but are
/// commonly hit during hand-routing. Rule names are
/// Magic-compatible (`nwell.2a`, `met1.6`, `licon.5a`, ...) so
/// `Check.Violation.Rule` text matches what `magic -dnull -drc
/// listall why` emits — easier to cross-reference viz output with
/// a full Magic run.
///
/// Algorithm coverage (in `Check.fs`):
///   Width      — bbox shorter side < min on a single polygon
///   Spacing    — bbox-edge gap < min between two polygons on the
///                same layer (current), or two different layers
///                (CrossSpacing variant — used for poly.4)
///   Enclosure  — outer layer must fully contain inner with ≥ N µm
///                margin on every side (e.g., nwell encloses
///                p-diff by 0.18)
///   Endcap     — source layer must extend past reference layer
///                edge by ≥ N µm (e.g., poly extends past diff by
///                0.13 in the channel-length direction)
///   MinArea    — single polygon's bbox area < threshold (e.g.,
///                met1.6: tiny isolated fragments)
///
/// What's NOT covered:
///   * Notch rules (single-polygon U-shapes with too-small
///     internal gap — needs edge-decomposition pass)
///   * Implant-aware cross-layer interactions (diff/tap.9 needs
///     diff ∩ nsdm boolean to identify n-diff, then check vs nwell)
///   * Density / antenna / area rules beyond simple bbox area
///   * Non-Manhattan rules (x.2: 90° on local interconnect)
///
/// Magic remains authoritative for the rules above. Run full
/// Magic DRC before any commit to silicon.

/// Layer identity — (gds_layer, gds_datatype). Matches how
/// `Rekolektion.Viz.Core.Layout.Layer` keys layers everywhere
/// else in the codebase.
type LayerKey = {
    Number   : int
    DataType : int
}

let private layer n dt = { Number = n; DataType = dt }

// --- Layer constants ---------------------------------------------------
// Names + numbers cross-checked against
// `Rekolektion.Viz.Core.Layout.Layer.allDrawing` and
// `src/rekolektion/tech/sky130.py:SKY130Layers`.
let diff   = layer 65 20
let tap    = layer 65 44
let nwell  = layer 64 20
let nsdm   = layer 93 44
let psdm   = layer 94 20
let poly   = layer 66 20
let licon1 = layer 66 44
let li1    = layer 67 20
let mcon   = layer 67 44
let met1   = layer 68 20
let via    = layer 68 44
let met2   = layer 69 20
let via2   = layer 69 44
let met3   = layer 70 20
/// POLYRES — derived poly-resistor layer (xhrpoly/uhrpoly +
/// xpc, grown 80 nm). SKY130 emits this on GDS 66/13 from
/// `res_xhigh_po_*` / `res_xhigh_po_uhrpoly_*` foundry cells.
/// Used as the source layer for rpm.* and poly.9 precision-
/// resistor spacing checks (distance from resistor body to
/// n-diff / poly / general diff).
let polyres = layer 66 13

/// Filter predicate on the *inner* polygon of an Enclosure rule
/// (or analogous "subject" polygon of other rule kinds). Lets a
/// single rule apply only to a typed subset of polygons —
/// crucial for SKY130 rules that distinguish diff-contacts from
/// poly-contacts, or p-diff from n-diff.
///
/// Magic does this via implant boolean operations (e.g.
/// `licon.5a` only fires on licons that touch diff). Viz
/// approximates with bbox-overlap pre-pass results — see
/// `Implant.tagAll`.
type InnerCondition =
    /// Always applies. Default for rules with no implant typing.
    | Always
    /// Inner polygon overlaps a DIFF (65/20) polygon. Used to
    /// scope `licon.5a` / `licon.5c` to diff-contact licons.
    | OverlapsDiff
    /// Inner polygon overlaps a POLY (66/20) polygon. Used to
    /// scope `licon.8` to poly-contact licons.
    | OverlapsPoly
    /// Inner polygon overlaps a PSDM (94/20) marker — marks
    /// p-diff. Used to scope `difftap.8a` (nwell encloses p-diff).
    | PsdmOverlaps
    /// Inner polygon overlaps an NSDM (93/44) marker — marks
    /// n-diff. Used by `nsdm.5a` (nsdm encloses n-diff).
    | NsdmOverlaps
    /// Inner polygon overlaps NSDM AND does NOT overlap NWELL —
    /// marks "n-diff outside any well". Used by `difftap.9`
    /// (n-diff to nwell spacing): the rule only fires on
    /// outside-well n-diff; n-diff entirely inside an nwell is
    /// a separate geometry issue with its own rule, not a
    /// spacing case.
    | NsdmNotInNwell

/// Rule kinds. Each variant carries a Magic-compatible name (used
/// as `Violation.Rule`) plus the parameters its check algorithm
/// needs. `Check.fs` dispatches on the case.
type Rule =
    /// Single-polygon min width: bbox shorter side ≥ MinUm.
    | Width    of name: string * layer: LayerKey * minUm: float
    /// Pairwise min spacing within a single layer: bbox-edge gap
    /// ≥ MinUm between any two same-layer polygons.
    | Spacing  of name: string * layer: LayerKey * minUm: float
    /// Cross-layer min spacing: bbox-edge gap ≥ MinUm between any
    /// polygon on `layerA` (matching `condA` if provided) and any
    /// polygon on `layerB`. Used for rules like `poly.4` (poly
    /// edge to diff edge) and the implant-aware `difftap.9`
    /// (n-diff outside nwell to nwell).
    ///
    /// `condA` filters which `layerA` polygons the rule applies
    /// to. Use `Always` for rules with no type filtering.
    | CrossSpacing of
        name: string
        * layerA: LayerKey
        * layerB: LayerKey
        * minUm: float
        * condA: InnerCondition
        * condB: InnerCondition
    /// Symmetric enclosure: `outer` must contain `inner` with at
    /// least MinUm margin on every side. A violation fires for
    /// any `inner` polygon (matching `cond`) whose enclosing
    /// `outer` polygon leaves an edge with < MinUm margin. Inner
    /// polygons matching `cond` with no covering outer are
    /// reported too (zero margin).
    ///
    /// `cond` filters which inner polygons the rule applies to
    /// based on implant tags — e.g. `licon.5a` only fires on
    /// licons that overlap diff (`OverlapsDiff`), not on
    /// poly-contact licons. Use `Always` for rules that apply to
    /// every inner polygon.
    | Enclosure of
        name: string
        * outer: LayerKey
        * inner: LayerKey
        * minUm: float
        * cond: InnerCondition
    /// `source` must extend past the `reference` polygon's bbox by
    /// at least MinUm in BOTH directions of one axis (auto-picked:
    /// the axis where source is the longer dimension). Used for
    /// `poly.7` (diff extends past poly in the gate-W direction)
    /// and `poly.8` (poly extends past diff in the gate-L
    /// direction).
    | Endcap of
        name: string
        * source: LayerKey
        * reference: LayerKey
        * minUm: float
    /// Single-polygon min area: bbox area ≥ MinUm². Catches tiny
    /// isolated fragments (`met1.6`, `met2.6`, `li.6`).
    | MinArea  of name: string * layer: LayerKey * minUm2: float
    /// "Source must not cross destination boundary; if outside,
    /// must be ≥ minUm away." For each source polygon:
    ///   * Fully inside any destination polygon → skip (legal,
    ///     e.g. NSDM inside nwell marks an n-tap).
    ///   * Partially overlaps a destination polygon (crosses
    ///     the boundary) → fire at gap=0.
    ///   * Fully separate from all destinations → check gap;
    ///     fire if gap < minUm. Same as CrossSpacing for this
    ///     case.
    ///
    /// Why this isn't just CrossSpacing: CrossSpacing skips
    /// overlapping pairs because intentional crossings (poly
    /// over diff = transistor gate) are legal. BoundaryCrossing
    /// is the opposite — overlap-without-containment IS the
    /// violation.
    | BoundaryCrossing of
        name: string
        * source: LayerKey
        * destination: LayerKey
        * minUm: float
    /// "Implant ∩ active, minus well, must be ≥ minUm from
    /// well." Models Magic's SKY130 rules where the violation
    /// is keyed on the actual diffusion region — diff ∩ implant
    /// marker — rather than the bare implant marker. Avoids the
    /// false-positive class where an implant marker (e.g. a
    /// pFET's n-tap NSDM at the nwell edge) has no diff under
    /// the part that extends past the well, and so isn't
    /// actually a "n-diffusion outside well" violation.
    ///
    /// Used for `diff/tap.9` (n+ diffusion outside nwell to
    /// nwell ≥ 0.34 µm). Magic's deck implements it via the
    /// same boolean: `(NSDM ∩ DIFF) - NWELL` then spacing to
    /// NWELL.
    | ImplantOutsideWellSpacing of
        name: string
        * implant: LayerKey
        * active: LayerKey
        * well: LayerKey
        * minUm: float
    /// Asymmetric enclosure: `outer` must contain `inner` with
    /// enclosure ≥ `oneDirUm` on one axis AND ≥ `otherDirUm` on
    /// the other axis. The "one" axis is whichever (X or Y) has
    /// more slack; the rule passes if both pairs of opposing
    /// edges satisfy their axis's threshold.
    ///
    /// SKY130 uses asymmetric enclosure for many contact rules
    /// (`licon.5a`/`licon.5c`, `li.5`/`li.5_2`, `met1.5`,
    /// `via.4a`/`via.5a`, etc.) — a li1 strap on top of a licon
    /// can have 0 enclosure on its long axis if it has ≥ 0.08 µm
    /// on the short axis. Symmetric Enclosure (requiring N on
    /// every side) would falsely fire on those.
    ///
    /// Convention: `oneDirUm` is the LARGER threshold (typically
    /// the one Magic labels with the higher suffix letter — e.g.
    /// licon.5c at 0.06 vs licon.5a at 0.04, the rule asks the
    /// LONG axis ≥ 0.06 AND the SHORT axis ≥ 0.04). The check
    /// determines which axis is which by measuring actual
    /// enclosure per axis.
    | AsymEnclosure of
        name: string
        * outer: LayerKey
        * inner: LayerKey
        * oneDirUm: float
        * otherDirUm: float
        * cond: InnerCondition
    /// Enclosure check where the "inner" is actually the
    /// intersection `(inner ∩ withL)`. Magic's diff/tap.8 is the
    /// canonical case: nwell must enclose `*pdiff = diff ∩ psdm`
    /// by 0.18 µm, NOT the bare psdm. The bare psdm has a
    /// foundry-mandated implant halo extending past the diff (in
    /// SKY130 a 125 nm halo on every side), which would eat the
    /// enclosure margin and produce false positives if the rule
    /// were applied to the implant marker alone. The intersection
    /// crops the implant back to the actual silicon region.
    ///
    /// Implemented via `inner ∩ withL` Region followed by
    /// `\ shrink(outer, N)` — same pattern as `Enclosure` but
    /// with the intersection step in front.
    | EnclosureOfIntersection of
        name: string
        * outer: LayerKey
        * inner: LayerKey
        * withL: LayerKey
        * minUm: float

/// Magic-compatible name of a rule. Used by the renderer (label
/// next to the violation marker) and by toggle filters keyed on
/// rule name.
let nameOf (rule: Rule) : string =
    match rule with
    | Width (n, _, _) -> n
    | Spacing (n, _, _) -> n
    | CrossSpacing (n, _, _, _, _, _) -> n
    | Enclosure (n, _, _, _, _) -> n
    | Endcap (n, _, _, _) -> n
    | MinArea (n, _, _) -> n
    | AsymEnclosure (n, _, _, _, _, _) -> n
    | BoundaryCrossing (n, _, _, _) -> n
    | ImplantOutsideWellSpacing (n, _, _, _, _) -> n
    | EnclosureOfIntersection (n, _, _, _, _) -> n

// --- Rule table ---------------------------------------------------------
// Ordering follows `sky130.py` top-to-bottom for ease of diffing,
// with the post-py extras (min-area) grouped at the bottom by
// layer. Magic name conventions:
//   <layer>.1  = min width
//   <layer>.2  = min spacing (sometimes 2a for same-net variant)
//   <layer>.3  = also spacing in some layer families
//   <layer>.4  = enclosure / cross-layer spacing
//   <layer>.5  = enclosure (foundry contact rules use 5a/5c for
//                asymmetric one-direction vs other-direction)
//   <layer>.6  = min area
//   <layer>.7/8 = endcap (poly.7 = diff overhang, poly.8 = poly
//                 overhang)
// Where the Python constant doesn't map to a known Magic id, the
// Magic deck's actual numbering is used (sourced from
// `verify/drc.py:_KNOWN_WAIVER_RULES`).

let allRules : Rule list = [
    // --- Diffusion (active area) ---
    Width    ("difftap.1",  diff,  0.15)
    Spacing  ("difftap.3",  diff,  0.27)
    // difftap.8a — nwell encloses p-diff. p-diff identified via
    // PSDM overlap; without that filter the rule would fire on
    // every n-diff (which legitimately sits outside any nwell).
    Enclosure("difftap.8a", nwell, diff,  0.18, PsdmOverlaps)

    // --- Tap (substrate/well contacts) ---
    Width    ("difftap.2",  tap,   0.26)
    Spacing  ("difftap.3a", tap,   0.27)
    // Diff (65/20) and tap (65/44) are stored as separate polygons
    // in the GDS / .rkt but Magic treats them as one "active area"
    // for spacing — diff↔tap spacing fires under the same
    // `difftap.3` rule.  Without this CrossSpacing entry viz only
    // checks diff↔diff (rule above) and tap↔tap (`difftap.3a`),
    // missing the cross-pair (e.g., a pfet's p-diff vs the chip's
    // top p-tap strip in opamp_buffer_r2r).  Rule name matches
    // Magic's emission so MagicVsViz bbox-pairing works.
    CrossSpacing("difftap.3", diff, tap, 0.27, Always, Always)

    // --- N-well ---
    Width    ("nwell.1",    nwell, 0.84)
    Spacing  ("nwell.2a",   nwell, 1.27)
    // NWELL_TO_NWELL_SAME (0.60) — same-net relaxation, can't model
    // without net awareness; the stricter 1.27 is the safe default.
    // nwell.5 — nwell encloses the actual p-diffusion region
    // `*pdiff = diff ∩ psdm` by 0.18 µm. Matches Magic's
    //   surround *pdiff allnwell 180 absence_illegal
    // (diff/tap.8 in the sky130 deck). The pfet primitive's
    // psdm has a 125 nm halo on every side past the diff (a
    // foundry implant rule); checking the bare psdm against
    // nwell would eat the enclosure margin and false-fire one
    // per pfet (probed on tap_mux_row, where the bare-psdm
    // check fired 16x while Magic was clean). The intersection
    // crops back to the actual silicon p-diff.
    EnclosureOfIntersection("nwell.5", nwell, psdm, diff, 0.18)

    // --- Implants ---
    Width    ("nsdm.1",     nsdm,  0.38)
    Spacing  ("nsdm.2",     nsdm,  0.38)
    // nsdm.5a — nsdm encloses n-diff. n-diff identified via
    // NSDM overlap (the inner must already overlap nsdm for nsdm
    // to be considered "the" implant for that diff).
    Enclosure("nsdm.5a",    nsdm,  diff, 0.125, NsdmOverlaps)
    Width    ("psdm.1",     psdm,  0.38)
    Spacing  ("psdm.2",     psdm,  0.38)
    // psdm.5a — psdm encloses p-diff. Same logic as nsdm.5a but
    // with PSDM as the type marker.
    Enclosure("psdm.5a",    psdm,  diff, 0.125, PsdmOverlaps)

    // --- Polysilicon ---
    Width    ("poly.1a",    poly,  0.15)
    Spacing  ("poly.2",     poly,  0.21)
    Endcap   ("poly.8",     poly,  diff, 0.13)    // poly overhangs gate edge
    Endcap   ("poly.7",     diff,  poly, 0.25)    // diff overhangs source/drain
    CrossSpacing("poly.4",  poly,  diff, 0.075, Always, Always)
                                                  // poly edge to diff (non-gate)
    // --- Precision poly resistors (POLYRES = 66/13) ---
    // POLYRES is the foundry's derived layer for resistor poly
    // (xhrpoly/uhrpoly + xpc, grown). Spacing rules from a
    // resistor body to its neighbours come from Magic's deck:
    //   rpm.3 + rpm.6 + nsd.5a   525 nm to n+ diffusion
    //   rpm.3 + rpm.7            400 nm to unrelated poly
    //   poly.9                   480 nm to general diffusion
    // The 525 nm rule is keyed on n-diff (NSDM ∩ DIFF outside
    // NWELL), modelled with the NsdmNotInNwell condition on
    // the layerB filter — same machinery diff/tap.9 uses.
    // rpm.1 — minimum RPM/URPM width. The polyres layer (xhrpoly /
    // uhrpoly derived) is the precision-resistor body. Foundry
    // cells emit narrow polyres bodies for tight resistance values;
    // Magic flags any narrow side < 1.27 µm.
    Width    ("rpm.1",      polyres, 1.27)
    CrossSpacing("rpm.3-6-nsd.5a", polyres, diff, 0.525,
                 Always, NsdmNotInNwell)
    CrossSpacing("rpm.3-7",        polyres, poly, 0.400,
                 Always, Always)
    CrossSpacing("poly.9",         polyres, diff, 0.480,
                 Always, Always)
    // poly.9 also applies polyres-to-poly (Magic's *poly tile
    // class includes regular poly; the deck has both an rpm.3-7
    // entry at 0.4 µm and a poly.9 entry at 0.48 µm for the
    // same source-target pair — viz needs both to match Magic
    // when the actual gap falls between the two limits).
    CrossSpacing("poly.9",         polyres, poly, 0.480,
                 Always, Always)
    // poly.9 also applies polyres-to-polyres (Magic's *poly tile
    // class covers all poly subtypes, including the resistor
    // body itself). Needed both for DRC fidelity AND so the
    // Tighten path can find a per-layer spacing rule for the
    // 66/13 layer — without it, dragging a poly resistor
    // toward its neighbour would overshoot because tighten
    // would see no constraint on layer 66/13.
    Spacing  ("poly.9",     polyres, 0.480)
    // diff/tap.9 — n+ diffusion outside nwell to nwell ≥ 0.34.
    // Uses (NSDM ∩ DIFF) \ NWELL as the violating geometry,
    // matching Magic's deck. Bare NSDM crossing nwell (a pFET's
    // n-tap implant extending slightly past the well boundary
    // with no diff under it) correctly does NOT fire — the
    // n-diffusion itself isn't outside nwell, only the marker.
    ImplantOutsideWellSpacing("diff/tap.9", nsdm, diff, nwell, 0.34)

    // --- LICON1 (contact: diff/poly to li1) ---
    // Licon contacts are typed by what's BELOW them: diff for
    // licon-to-diff, poly for licon-to-poly. Enclosure rules below
    // are scoped accordingly — `licon.5a` (diff encloses licon)
    // only fires on diff-contact licons; `licon.8` (poly
    // encloses licon) only on poly-contacts. `li.5` (li1
    // encloses licon) fires on both because li1 always sits on
    // top of a licon regardless of what's below.
    Width    ("licon.1",    licon1, 0.17)
    Spacing  ("licon.2",    licon1, 0.17)
    // licon.5a/5c — diff overlap of licon (one direction 0.04,
    // other direction 0.06). The rule passes when both opposing
    // pairs of edges satisfy their axis's threshold.
    AsymEnclosure("licon.5a", diff, licon1, 0.06, 0.04, OverlapsDiff)
    // licon.8 — poly overlap of licon (0.05 one dir, 0.08 other)
    AsymEnclosure("licon.8",  poly, licon1, 0.08, 0.05, OverlapsPoly)
    // licon.9 (+ psdm.5a) — poly-contact licon must be ≥ 0.235 µm
    // from any P-diffusion edge. Magic's deck composes this with
    // psdm.5a (psdm encloses p-diff) so the violation message reads
    // "licon.9 + psdm.5a". Both endpoints are implant-tagged:
    //   source licon = OverlapsPoly (poly-contact, NOT diff-contact)
    //   reference diff = PsdmOverlaps (p-diff)
    // viz emits as a CrossSpacing — the bbox-pairing in MagicVsViz
    // matches Magic's composite tile by location, not message text.
    CrossSpacing("licon.9", licon1, diff, 0.235, OverlapsPoly, PsdmOverlaps)
    // li.5 — li1 encloses every licon. li1 straps are typically
    // licon-width on one axis (0 enclosure there), so the rule
    // is properly asymmetric: 0 in one direction, 0.08 in the
    // other.
    AsymEnclosure("li.5",     li1,  licon1, 0.08, 0.0,  Always)

    // --- LI1 (local interconnect) ---
    Width    ("li.1",       li1,   0.17)
    Spacing  ("li.3",       li1,   0.17)
    MinArea  ("li.6",       li1,   0.0561)        // 0.17 × 0.33 effective min

    // --- MCON (contact: li1 to met1) ---
    Width    ("mcon.1",     mcon,  0.17)
    Spacing  ("mcon.2",     mcon,  0.19)
    // met1.5 — met1 encloses every mcon. Asymmetric: 0.03 one
    // dir, 0.06 other dir. A met1 wire that's 0.17 nm wide (=
    // mcon width) on its short axis has 0 enclosure there;
    // along the wire's length it has plenty.
    AsymEnclosure("met1.5",   met1,  mcon, 0.06, 0.03, Always)

    // --- Metal 1 ---
    Width    ("met1.1",     met1,  0.14)
    Spacing  ("met1.2",     met1,  0.14)
    MinArea  ("met1.6",     met1,  0.083)         // 0.083 µm² min area

    // --- VIA (met1 to met2) ---
    Width    ("via.1",      via,   0.15)
    Spacing  ("via.2",      via,   0.17)
    // via.4a — met1 encloses via1 by 0.055 µm minimum on at least
    // one axis. Always applies.
    Enclosure("via.4a",     met1,  via, 0.055, Always)
    // via.4b — when met1 is minimum-width (0.14 µm), the OTHER
    // axis must have 0.085 µm enclosure. Encoded asymmetrically so
    // the pad emitter sizes square pads to satisfy the strict axis
    // (it takes max of the two thresholds). Pre-2026-05 this rule
    // was missing; the emitter returned 0.26 µm pads, then
    // met1.6 min-area floored them at 0.288 µm — both below the
    // 0.320 µm needed to satisfy via.4b on a min-width wire.
    AsymEnclosure("via.4b", met1, via, 0.085, 0.055, Always)
    // via.5a / via.5b — same shape for met2 around via1.
    Enclosure("via.5a",     met2,  via, 0.055, Always)
    AsymEnclosure("via.5b", met2, via, 0.085, 0.055, Always)

    // --- Metal 2 ---
    Width    ("met2.1",     met2,  0.14)
    Spacing  ("met2.2",     met2,  0.14)
    MinArea  ("met2.6",     met2,  0.0676)        // 0.0676 µm² min area

    // --- VIA2 (met2 to met3) ---
    Width    ("via2.1",     via2,  0.20)
    Spacing  ("via2.2",     via2,  0.20)
    // via2.4 — met2 encloses via2. Asymmetric: 0.04 one dir,
    // 0.085 other dir.
    AsymEnclosure("via2.4", met2, via2, 0.085, 0.04, Always)
    // via2.5 — met3 encloses via2. Asymmetric: 0.065 one dir,
    // 0.095 other dir.
    AsymEnclosure("via2.5", met3, via2, 0.095, 0.065, Always)

    // --- Metal 3 ---
    Width    ("met3.1",     met3,  0.30)
    Spacing  ("met3.2",     met3,  0.30)
    MinArea  ("met3.6",     met3,  0.240)         // 0.240 µm² min area
]

// -----------------------------------------------------------------------
// Legacy LayerRule shim. The Tighten code path
// (`Check.tightenCandidates`, `Check.maxOrthoSlackDbu`) was written
// against a flat `{ Layer; Number; DataType; MinWidthUm;
// MinSpacingUm }` record and asks per (layer, dt) for one width +
// one spacing limit. Keep the shape around as a derived view over
// `allRules` so the Tighten path doesn't have to learn the DU.
// Width and spacing are pulled from the matching DU cases when
// present; missing entries default to 0 (rule not enforced).
// -----------------------------------------------------------------------

/// Display-name lookup. Returns the layer's human name (e.g.
/// "met1") given its (gds-layer, datatype) pair, or "?" when
/// unknown. Used to print rule headers in the legacy shim.
let private displayName (key: LayerKey) : string =
    if   key = diff   then "diff"
    elif key = tap    then "tap"
    elif key = nwell  then "nwell"
    elif key = nsdm   then "nsdm"
    elif key = psdm   then "psdm"
    elif key = poly   then "poly"
    elif key = polyres then "polyres"
    elif key = licon1 then "licon1"
    elif key = li1    then "li1"
    elif key = mcon   then "mcon"
    elif key = met1   then "met1"
    elif key = via    then "via"
    elif key = met2   then "met2"
    elif key = via2   then "via2"
    elif key = met3   then "met3"
    else "?"

type LayerRule = {
    /// Display name — also used as the rule prefix when reporting
    /// violations from the legacy code path (e.g. "met1.width").
    Layer       : string
    /// SKY130 (gds-layer-number, datatype) so the runtime can
    /// match flattened polygons to their rule entry.
    Number      : int
    DataType    : int
    MinWidthUm  : float
    MinSpacingUm: float
}

let private layerRulesView : LayerRule list =
    // Group all Width + Spacing rules by their layer key, then
    // distill to one LayerRule per key with whichever widths /
    // spacings were declared.
    let byKey = System.Collections.Generic.Dictionary<LayerKey, float * float>()
    let merge (key: LayerKey) (w: float) (s: float) =
        let curW, curS =
            match byKey.TryGetValue key with
            | true, v -> v
            | _ -> 0.0, 0.0
        byKey.[key] <- (max curW w, max curS s)
    for r in allRules do
        match r with
        | Width (_, k, m)   -> merge k m  0.0
        | Spacing (_, k, m) -> merge k 0.0 m
        | _ -> ()
    [ for kv in byKey ->
        { Layer        = displayName kv.Key
          Number       = kv.Key.Number
          DataType     = kv.Key.DataType
          MinWidthUm   = fst kv.Value
          MinSpacingUm = snd kv.Value } ]

let private byKey =
    layerRulesView
    |> List.map (fun r -> (r.Number, r.DataType), r)
    |> Map.ofList

/// Look up legacy LayerRule for (gds-layer, datatype). Returns
/// None for layers with no width or spacing rule in the table.
/// Used by the Tighten code path; the new DRC overlay uses
/// `allRules` directly.
let tryFind (number: int) (dataType: int) : LayerRule option =
    Map.tryFind (number, dataType) byKey

/// Cross-layer min spacing entries derived from `allRules`. Each
/// entry says: a poly on `LayerA` must stay ≥ `MinUm` from any
/// poly on `LayerB` (when the inner-condition filters match).
/// Tighten consults this so cross-layer constraints (e.g.
/// polyres → diff at 0.48 µm) bind the move, not just same-layer
/// ones. CondA / CondB are kept for the rare implant-aware
/// cases (e.g. rpm.3-6-nsd.5a only fires against n-diff outside
/// nwell).
type CrossSpacingRule = {
    LayerA   : LayerKey
    LayerB   : LayerKey
    MinUm    : float
    CondA    : InnerCondition
    CondB    : InnerCondition
}

let allCrossSpacings : CrossSpacingRule list =
    allRules
    |> List.choose (fun r ->
        match r with
        | CrossSpacing (_, a, b, m, ca, cb) ->
            Some { LayerA = a; LayerB = b; MinUm = m
                   CondA = ca; CondB = cb }
        | _ -> None)

// --- ADR-0003 live DRC eligibility -------------------------------------
//
// A rule is live-eligible when its violation depends only on local
// geometry — clearance, width, enclosure, endcap. These can be
// re-checked on every mouse move during routing without needing the
// full cell's topology.
//
// Commit-only rules (MinArea, BoundaryCrossing, implant-outside-well
// spacing) depend on aggregates or implant booleans we don't recompute
// per-frame. They fire on RouteFinish via the full `check` entry
// point in Check.fs.

let isLiveEligible (rule: Rule) : bool =
    match rule with
    | Width _ | Spacing _ | CrossSpacing _ -> true
    | Enclosure _ | Endcap _ | AsymEnclosure _ -> true
    | MinArea _ -> false
    | BoundaryCrossing _ -> false
    | ImplantOutsideWellSpacing _ -> false
    | EnclosureOfIntersection _ -> false

let liveRules : Rule list =
    allRules |> List.filter isLiveEligible

let liveEligibleNames : Set<string> =
    liveRules |> List.map nameOf |> Set.ofList

// --- ADR-0004 RulesetView -----------------------------------------------
//
// A bundle of (rules + provenance) that flows through Drc.Check. The
// view replaces the previous direct use of `Rules.allRules` inside
// the engine, so a loaded YAML ruleset can drive DRC the same way
// the F#-coded defaults do. Provenance carries from the loader
// through to where the UI surfaces "this rule came from X".

type RulesetView = {
    Rules : Rule list
    /// Per-rule source attribution keyed by `nameOf rule`. Empty
    /// map for `defaultView` (every rule is internal). Populated by
    /// `RulesYaml.merge` and friends.
    Provenance : Map<string, string>
}

/// Internal F# default — provenance empty (every rule is "compiled
/// in"). Drc.Check.check falls back to this when no view is given.
let defaultView : RulesetView = {
    Rules = allRules
    Provenance = Map.empty
}

/// Build a view from a rules list + provenance map. Convenience for
/// `RulesYaml.merge` consumers and ad-hoc test cases.
let viewOf (rules: Rule list) (provenance: Map<string, string>) : RulesetView = {
    Rules = rules
    Provenance = provenance
}

/// Probe well-known locations for `drc/base/<pdk>.yaml`, walking
/// upward from the executing assembly so dev runs (test bin, raw
/// `dotnet run` from any subdir) and installed copies all resolve.
/// Returns the first existing path, or `None` if none of the
/// candidates exist.
let tryLocateBaseYaml (pdk: string) : string option =
    let asmDir =
        System.IO.Path.GetDirectoryName(typeof<RulesetView>.Assembly.Location)
    let leaf =
        System.IO.Path.Combine("drc", "base", sprintf "%s.yaml" pdk)
    let rec ancestors (dir: string) (depth: int) =
        if depth > 8 || System.String.IsNullOrEmpty dir then []
        else
            dir :: ancestors (System.IO.Path.GetDirectoryName dir) (depth + 1)
    ancestors asmDir 0
    |> List.map (fun d -> System.IO.Path.Combine(d, leaf))
    |> List.tryFind System.IO.File.Exists
