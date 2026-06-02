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
    /// "Every source polygon must be fully contained inside some
    /// destination polygon."  No margin — pure containment.
    /// Differs from `BoundaryCrossing` which only fires when the
    /// destination layer is PRESENT and the source partially
    /// crosses its edge; `MustBeInside` ALSO fires when the
    /// destination layer is absent entirely around the source.
    ///
    /// Used for KLayout's `*.not(*)` style containment rules:
    /// `ct.4` (mcon must be covered by li1), `via.4a_a` (via1
    /// must be enclosed by m1), `m1.4`, `m2.4_a`. Each emits
    /// one violation per failing source EDGE (matching KLayout's
    /// edge-pair output) — for a fully-uncovered square source,
    /// 4 violations.
    | MustBeInside of
        name: string
        * source: LayerKey
        * destination: LayerKey
    /// Size-filtered, edge-counting variant of `MustBeInside`.
    /// Only fires on source polygons whose bbox is a square of
    /// EXACTLY `sizeUm` × `sizeUm` (matches KLayout deck's
    /// `source.squares.drc(width == N)` filter).  Emits one
    /// violation per failing source EDGE (4 per uncovered
    /// square).
    ///
    /// Used for `via.4a_a` (0.15 µm via must be enclosed by m1)
    /// and the cousin rules that combine a fixed-size shape
    /// filter with per-edge emission.
    | MustBeInsideEdgewise of
        name: string
        * source: LayerKey
        * destination: LayerKey
        * sizeUm: float

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
    | MustBeInside (n, _, _) -> n
    | MustBeInsideEdgewise (n, _, _, _) -> n

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
    //
    // Naming follows the SKY130 deck (both Magic .tech and KLayout
    // .drc): `nsdm.1` is SPACING, `nsdm.2` is WIDTH. Same for
    // psdm.1 / psdm.2.  An earlier F# Magic ruleset had these
    // swapped (Width/Spacing reversed); corrected 2026-06-02 as
    // part of Track 02 follow-up (autonomous_2026-06-01.md).
    Spacing  ("nsdm.1",     nsdm,  0.38)
    Width    ("nsdm.2",     nsdm,  0.38)
    // nsdm.5a — nsdm encloses n-diff. n-diff identified via
    // NSDM overlap (the inner must already overlap nsdm for nsdm
    // to be considered "the" implant for that diff).
    Enclosure("nsdm.5a",    nsdm,  diff, 0.125, NsdmOverlaps)
    Spacing  ("psdm.1",     psdm,  0.38)
    Width    ("psdm.2",     psdm,  0.38)
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
    // rpm.1 — INTENTIONALLY OMITTED from the viz rule set.
    //
    // sky130A.tech defines rpm.1 with a CIF-style block-layer
    // subtraction:
    //   templayer rpm_block *psd,*mvpsd
    //   templayer rpm_generate
    //     and-not rpm_block
    //   cifwidth rpm_generate 1270 "rpm.1"
    // i.e. the rule operates on RPM minus PSDM. Every foundry
    // res_xhigh_po / res_high_po primitive has psdm fully covering
    // its RPM region, so Magic checks an empty region inside the
    // resistor body and the rule silently waives. The viz Width
    // schema doesn't yet model `and-not` terms, so a literal
    //   Width("rpm.1", polyres, 1.27)
    // emits a false positive on every foundry poly-resistor
    // instance. Magic-based verify_drc still enforces rpm.1
    // correctly via the foundry tech file.
    //
    // Previously suppressed via a hand-edited `disabled: true` on
    // the bundled YAML; that diverged from the F# table and broke
    // the YAML-stays-in-sync / round-trip tests. Removing the rule
    // from `allRules` here keeps both sides honest.
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
    // li.c1 is the COREID-core variant — 0.14 µm relaxation inside
    // SRAM bitcell scope. Magic external fires BOTH li.1 (peri)
    // and li.c1 (core) on geometry outside any COREID marker; the
    // foundry-cell waiver pipeline drops li.c1 inside COREID where
    // it's intentional. Added 2026-06-02 as part of Track 02
    // follow-up.
    Width    ("li.c1",      li1,   0.14)
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
    // Cross-layer containment depends on the destination layer's
    // full polygon set, which the per-frame routing path doesn't
    // have available cheaply.  Commit-time only — matches the
    // BoundaryCrossing classification.
    | MustBeInside _ -> false
    | MustBeInsideEdgewise _ -> false

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

// --- Track 02 (silicon_correct) — Magic-compat alias --------------------
//
// The entire body of this `Rules` module IS the Magic-flavored
// ruleset, frozen alongside Magic itself (Magic isn't changing from
// here out, so neither is this).
//
// The nested `Magic` submodule re-exposes the public values under a
// symmetric name so new code can read `Rules.Magic.allRules`
// alongside `Rules.Klayout.allRules` without ambiguity. Existing
// call sites that say `Rules.allRules` continue to work and continue
// to get Magic semantics — preserving the no-caller-churn guarantee
// in the Track 02 plan.
//
// See: silicon_correct/tracks/02_drc_klayout_primary/plan.md §3.3.

/// Magic-compat alias for `Rules` exports. Frozen.
module Magic =
    /// The full Magic-tuned rule list. Same value as `Rules.allRules`
    /// (and `Rules.defaultView.Rules`); re-exposed here so call sites
    /// can be explicit about which compat target they want.
    let allRules : Rule list = allRules
    let allCrossSpacings : CrossSpacingRule list = allCrossSpacings
    let liveRules : Rule list = liveRules
    let liveEligibleNames : Set<string> = liveEligibleNames
    let defaultView : RulesetView = defaultView
    let tryFind = tryFind


/// KLayout-compat ruleset.
///
/// **Phase 3 state (silicon_correct/Track 02): empty placeholder.**
/// The corpus harness in Phase 4 populates each rule one at a time,
/// gated by `F#-Klayout ≡ external-KLayout` equivalence on the
/// per-rule status table at `docs/internals/drc_rule_equivalency.md`.
///
/// Until a rule lands here with green equivalency, callers asking for
/// `compat = Klayout` get an empty rule list. The Python
/// `verify_drc(compat="klayout")` path (Track 02 Phase 2) routes the
/// same callers through the KLayout external binary in the meantime,
/// so coverage isn't lost — it's just slower than the in-viz path.
///
/// Rule names match KLayout deck IDs verbatim (`m1.1`, `poly.2`,
/// `MR_thkox.CON.1`, ...). Translation to Magic-equivalent names
/// (`met1.1`, etc.) happens only on the Python `verify_drc_klayout`
/// boundary for waiver-list lookup. Inside the F# engine the deck
/// name is the source of truth.
///
/// Co-located with `Magic` in this file because F# does not allow a
/// top-level module to share its name with a namespace — splitting
/// `Klayout` into its own file would force a much larger refactor
/// of the existing `Rules` module. We can re-evaluate the split in
/// Phase 4 if this module grows large enough to warrant it.
module Klayout =
    /// KLayout-flavored rule list. Populated rule-by-rule via the
    /// Phase 4 corpus harness — each entry below traces back to a
    /// `viol_<rule>_<variant>.rkt` cell that proves F#-Klayout ≡
    /// ext-KLayout for the rule.  See
    /// `docs/internals/drc_rule_equivalency.md`.
    let allRules : Rule list = [
        // --- Metal 1 (proved against tests/drc_corpus/) ---
        Width   ("m1.1", met1, 0.14)    // min m1 width 0.14 µm
        Spacing ("m1.2", met1, 0.14)    // min m1 spacing 0.14 µm
        MinArea ("m1.6", met1, 0.083)   // min m1 area 0.083 µm²
        // --- Metal 2 ---
        Width   ("m2.1", met2, 0.14)
        Spacing ("m2.2", met2, 0.14)
        MinArea ("m2.6", met2, 0.0676)  // 0.0676 µm² min area
        // --- Metal 3 ---
        Width   ("m3.1", met3, 0.30)
        Spacing ("m3.2", met3, 0.30)
        MinArea ("m3.6", met3, 0.240)   // 0.240 µm² min area
        // --- Local interconnect (li1) ---
        Width   ("li.1", li1,  0.17)
        Spacing ("li.3", li1,  0.17)
        MinArea ("li.6", li1,  0.0561)  // 0.17 × 0.33 effective min
        // --- mcon (li1↔met1 contact) ---
        Width   ("ct.1_a", mcon, 0.17)  // min mcon width
        Spacing ("ct.2",   mcon, 0.19)  // min mcon spacing
        // --- via1 (met1↔met2) ---
        Width   ("via.1a_a", via, 0.15) // min via1 width
        Spacing ("via.2",    via, 0.17) // min via1 spacing
        // --- Polysilicon ---
        Width   ("poly.1a", poly, 0.15) // min poly width
        Spacing ("poly.2",  poly, 0.21) // min poly spacing
        // --- N-well ---
        Width   ("nwell.1",  nwell, 0.84) // min nwell width
        Spacing ("nwell.2a", nwell, 1.27) // min nwell spacing
        // --- Implants (nsdm / psdm) ---
        //
        // KLayout deck names these OPPOSITE of F# Magic — deck has
        // nsdm.1=Spacing nsdm.2=Width; F# Magic has the labels
        // swapped (pre-existing).  We follow the deck so the
        // KLayout-diagonal comparison passes.
        Spacing ("nsdm.1", nsdm, 0.38)  // min nsdm spacing
        Width   ("nsdm.2", nsdm, 0.38)  // min nsdm width
        Spacing ("psdm.1", psdm, 0.38)
        Width   ("psdm.2", psdm, 0.38)
        // --- Cross-layer containment (`X must be inside Y`) ---
        //
        // `MustBeInside` rule kind — emits 1 violation per
        // uncovered source polygon, matching KLayout deck's
        // polygon-style `.not().output()` emission.
        //
        // KLayout's edge-style containment cousins (`via.4a_a`,
        // which uses `.drc(width == 0.15).not().output()`) need
        // a size-filtered + edge-counting variant — deferred.
        // The asymmetric `via.5a` (0.085 alt-enclosure) and
        // `m2.5` (0.085 alt) need AsymEnclosure edge-counting —
        // deferred until per-side gap computation lands.
        MustBeInside ("ct.4",   mcon, li1)   // mcon must be covered by li1
        MustBeInside ("m1.4",   mcon, met1)  // mcon must be enclosed by m1
        MustBeInside ("m2.4_a", via,  met2)  // via1 must be enclosed by m2
        // --- Symmetric Enclosure (sub-min margin) ---
        //
        // Edge-counting under Compat.Klayout: emits 4 violations
        // per under-enclosed inner — matches KLayout deck's
        // per-edge output. The post-pass clustering skips these
        // via nonClusterableRules.
        Enclosure ("via.4a", met1, via, 0.055, Always)
        Enclosure ("m2.4",   met2, via, 0.055, Always)
        // --- Asymmetric Enclosure (alt-direction margin) ---
        //
        // KLayout deck emits these polygon-style (`via_interact`
        // output of failing inners — one per inner).  F#
        // AsymEnclosure already emits polygon-style; no compat
        // branch needed.  Naming follows the deck (KLayout's
        // `via.5a` is m1 alt-enclosure, NOT m2.5a — KLayout deck
        // labels asymmetric via1-met1 as via.5a and via1-met2 as
        // m2.5; F# Magic uses different labels for the same
        // checks).
        AsymEnclosure ("via.5a", met1, via,  0.085, 0.055, Always)
        AsymEnclosure ("m2.5",   met2, via,  0.085, 0.055, Always)
        AsymEnclosure ("m1.5",   met1, mcon, 0.06,  0.03,  Always)
        // --- Size-filtered edge-style containment ---
        //
        // KLayout deck pattern:
        //   rectVIA.squares.drc(width == 0.15).not(m1).output("via.4a_a")
        // Only fires on 0.15 µm via1 squares not enclosed by m1.
        // 4 emit per uncovered matching square.
        MustBeInsideEdgewise ("via.4a_a", via, met1, 0.15)
    ]

    /// Cross-layer spacing entries derived from `allRules`. Same
    /// shape as `Rules.allCrossSpacings` but scoped to the KLayout-
    /// compat rule list — empty until Phase 4 populates `allRules`.
    let allCrossSpacings : CrossSpacingRule list =
        allRules
        |> List.choose (fun r ->
            match r with
            | CrossSpacing (_, a, b, m, ca, cb) ->
                Some { LayerA = a; LayerB = b; MinUm = m
                       CondA = ca; CondB = cb }
            | _ -> None)

    /// Live-eligible subset of the KLayout rule list (per-frame
    /// routing feedback). Phase-4 work that lands a non-`MinArea` /
    /// `BoundaryCrossing` rule here automatically picks it up via
    /// `isLiveEligible`.
    let liveRules : Rule list =
        allRules |> List.filter isLiveEligible

    let liveEligibleNames : Set<string> =
        liveRules |> List.map nameOf |> Set.ofList

    /// KLayout-compat default view. Provenance empty — every rule
    /// (when any exists) is internal F# code. `RulesYaml.merge`
    /// consumers can build a per-loaded-YAML view on top of this
    /// base; same pattern as Magic.
    let defaultView : RulesetView = {
        Rules = allRules
        Provenance = Map.empty
    }

    /// Layer → (width, spacing) lookup. Width and Spacing rules for
    /// the same layer MUST merge into one entry; Map.ofList would
    /// otherwise keep only the last inserted half and drop the other
    /// (returning MinWidthUm=0 for layers that have only a Spacing
    /// rule entered after the Width rule, or vice-versa).
    let private byKey =
        let merge (k: int * int) (w: float option) (s: float option)
                  (acc: Map<int * int, float option * float option>) =
            let prev =
                match Map.tryFind k acc with
                | Some (pw, ps) -> (pw, ps)
                | None -> (None, None)
            let pw, ps = prev
            let merged = (Option.orElse pw w, Option.orElse ps s)
            Map.add k merged acc
        allRules
        |> List.fold (fun acc r ->
            match r with
            | Width (_, l, w) ->
                merge (l.Number, l.DataType) (Some w) None acc
            | Spacing (_, l, s) ->
                merge (l.Number, l.DataType) None (Some s) acc
            | _ -> acc) Map.empty

    /// LayerRule lookup — Magic's `Rules.tryFind` analog. Returns
    /// `None` for any layer until Phase 4 populates Width/Spacing
    /// rules. Callers that depend on a hit (e.g. the Tighten path)
    /// must select `compat = Magic` until the relevant rules land.
    let tryFind (number: int) (dataType: int) : LayerRule option =
        match Map.tryFind (number, dataType) byKey with
        | Some (w, s) ->
            // Use the same Magic-side display name format
            // (`"layer{N}_{D}"`) as the fallback in displayName.
            let display = sprintf "layer%d_%d" number dataType
            Some { Layer = display
                   Number = number
                   DataType = dataType
                   MinWidthUm = defaultArg w 0.0
                   MinSpacingUm = defaultArg s 0.0 }
        | None -> None


/// Track 02 (silicon_correct) — compat dispatcher.
///
/// One choice point for callers that don't already carry a
/// `RulesetView`. The engine entry points (`Drc.Check.check`,
/// `runLive`, etc.) still take a view explicitly so call sites that
/// already build a custom view (e.g. RulesYaml-loaded) continue to
/// work unchanged.
let viewFor (compat: Compat.Compat) : RulesetView =
    match compat with
    | Compat.Magic   -> Magic.defaultView
    | Compat.Klayout -> Klayout.defaultView


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
