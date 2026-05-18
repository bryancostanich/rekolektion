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
    /// polygon on `layerA` and any polygon on `layerB`. Used for
    /// rules like `poly.4` (poly edge to diff edge).
    | CrossSpacing of
        name: string * layerA: LayerKey * layerB: LayerKey * minUm: float
    /// Asymmetric enclosure: `outer` must contain `inner` with at
    /// least MinUm margin on every side. A violation fires for any
    /// `inner` polygon whose enclosing `outer` polygon (if one
    /// exists at all) leaves an edge with < MinUm margin. Inner
    /// polygons with no covering outer at all are reported too
    /// (zero margin).
    | Enclosure of
        name: string
        * outer: LayerKey
        * inner: LayerKey
        * minUm: float
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

/// Magic-compatible name of a rule. Used by the renderer (label
/// next to the violation marker) and by toggle filters keyed on
/// rule name.
let nameOf (rule: Rule) : string =
    match rule with
    | Width (n, _, _) -> n
    | Spacing (n, _, _) -> n
    | CrossSpacing (n, _, _, _) -> n
    | Enclosure (n, _, _, _) -> n
    | Endcap (n, _, _, _) -> n
    | MinArea (n, _, _) -> n

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
    Enclosure("difftap.8a", nwell, diff,  0.18)   // nwell overlap of p-diff

    // --- Tap (substrate/well contacts) ---
    Width    ("difftap.2",  tap,   0.26)
    Spacing  ("difftap.3a", tap,   0.27)

    // --- N-well ---
    Width    ("nwell.1",    nwell, 0.84)
    Spacing  ("nwell.2a",   nwell, 1.27)
    // NWELL_TO_NWELL_SAME (0.60) — same-net relaxation, can't model
    // without net awareness; the stricter 1.27 is the safe default.
    Enclosure("nwell.5",    nwell, psdm,  0.18)   // nwell encloses psdm

    // --- Implants ---
    Width    ("nsdm.1",     nsdm,  0.38)
    Spacing  ("nsdm.2",     nsdm,  0.38)
    Enclosure("nsdm.5a",    nsdm,  diff, 0.125)   // nsdm encloses (n-)diff
    Width    ("psdm.1",     psdm,  0.38)
    Spacing  ("psdm.2",     psdm,  0.38)
    Enclosure("psdm.5a",    psdm,  diff, 0.125)   // psdm encloses (p-)diff

    // --- Polysilicon ---
    Width    ("poly.1a",    poly,  0.15)
    Spacing  ("poly.2",     poly,  0.21)
    Endcap   ("poly.8",     poly,  diff, 0.13)    // poly overhangs gate edge
    Endcap   ("poly.7",     diff,  poly, 0.25)    // diff overhangs source/drain
    CrossSpacing("poly.4",  poly,  diff, 0.075)   // poly edge to diff (non-gate)

    // --- LICON1 (contact: diff/poly to li1) ---
    Width    ("licon.1",    licon1, 0.17)
    Spacing  ("licon.2",    licon1, 0.17)
    Enclosure("licon.5a",   diff,   licon1, 0.04) // diff overlap of licon
                                                  // (one direction — symmetric
                                                  // 0.04 is conservative; the
                                                  // 0.06 other-direction
                                                  // variant needs orientation)
    Enclosure("licon.8",    poly,   licon1, 0.05) // poly overlap of licon
    Enclosure("li.5",       li1,    licon1, 0.08) // li1 encloses licon

    // --- LI1 (local interconnect) ---
    Width    ("li.1",       li1,   0.17)
    Spacing  ("li.3",       li1,   0.17)
    MinArea  ("li.6",       li1,   0.0561)        // 0.17 × 0.33 effective min

    // --- MCON (contact: li1 to met1) ---
    Width    ("mcon.1",     mcon,  0.17)
    Spacing  ("mcon.2",     mcon,  0.19)
    Enclosure("met1.5",     met1,  mcon, 0.03)    // met1 encloses mcon

    // --- Metal 1 ---
    Width    ("met1.1",     met1,  0.14)
    Spacing  ("met1.2",     met1,  0.14)
    MinArea  ("met1.6",     met1,  0.083)         // 0.083 µm² min area

    // --- VIA (met1 to met2) ---
    Width    ("via.1",      via,   0.15)
    Spacing  ("via.2",      via,   0.17)
    Enclosure("via.4a",     met1,  via,  0.055)   // met1 encloses via
    Enclosure("via.5a",     met2,  via,  0.055)   // met2 encloses via

    // --- Metal 2 ---
    Width    ("met2.1",     met2,  0.14)
    Spacing  ("met2.2",     met2,  0.14)
    MinArea  ("met2.6",     met2,  0.0676)        // 0.0676 µm² min area

    // --- VIA2 (met2 to met3) ---
    Width    ("via2.1",     via2,  0.20)
    Spacing  ("via2.2",     via2,  0.20)
    Enclosure("via2.4",     met2,  via2, 0.04)    // met2 encloses via2
    Enclosure("via2.5",     met3,  via2, 0.065)   // met3 encloses via2

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
