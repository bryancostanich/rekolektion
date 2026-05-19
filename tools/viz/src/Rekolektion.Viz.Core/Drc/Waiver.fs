module Rekolektion.Viz.Core.Drc.Waiver

open Rekolektion.Viz.Core.Layout.Flatten

/// SKY130 COREID rule waiver.
///
/// The SKY130 foundry SRAM library (`sky130_fd_bd_sram`) uses
/// tighter geometry than stock rules permit — sub-min-width
/// metal, sub-min-spacing contacts, asymmetric enclosures. These
/// shapes are foundry-validated and accepted in silicon, but
/// fire stock DRC. The PDK marks SRAM cells with an
/// `areaid_core` polygon (GDS layer 81/2); inside that area, ~30
/// specific rules are silently waived by Magic's deck.
///
/// This module ports `_KNOWN_WAIVER_RULES` from
/// `src/rekolektion/verify/drc.py:_KNOWN_WAIVER_RULES` to F#
/// and exposes a single helper: given a violation's bbox and
/// the rule name, decide whether the violation falls inside any
/// COREID area AND matches a waiver-listed rule. If both are
/// true, the violation is dropped from the report.
///
/// **Known limitation** — copied from drc.py: the rule-name
/// filter is global, not spatial. A `met1.2` from a routing bug
/// outside any COREID area should NOT be waived, but if it
/// happens to fall inside a COREID polygon (e.g. user routes
/// over an SRAM macro), it is. Magic's deck is rigorous about
/// spatial scoping per-rule; we trust the COREID bbox for now.
/// Future work: spatial filtering tagged to bitcell footprints.

/// Magic rule IDs that SKY130's COREID-relaxed deck silently
/// accepts inside areaid_core polygons. Mirrors
/// `_KNOWN_WAIVER_RULES` in `verify/drc.py` — keep in sync.
let waiverRuleIds : Set<string> = Set.ofList [
    // Local interconnect
    "li.1"; "li.3"; "li.c1"; "li.6"; "li.c2"
    // Diffusion / taps / transistors
    "diff/tap.1"; "diff/tap.2"; "diff/tap.3"; "diff/tap.8"; "diff/tap.9"
    "difftap.1"; "difftap.2"; "difftap.3"; "difftap.3a"; "difftap.8a"
    // Wells
    "nwell.1"; "nwell.2a"; "nwell.7"
    "dnwell.2"; "dnwell.3"
    // Poly
    "poly.2"; "poly.4"; "poly.5"; "poly.7"; "poly.8"; "poly.11"
    "poly.1a"
    // Angles (foundry uses non-Manhattan li1 in bitcells)
    "x.2"
    // Psub/nsub contact rules tight in SRAM
    "psd.5a"; "psd.5b"; "psd.10b"
    "nsd.10b"
    "licon.5a"; "licon.5b"; "licon.5c"
    "licon.7"; "licon.8"; "licon.8a"
    "licon.9"; "licon.10"; "licon.11"; "licon.14"
    "licon.1"; "licon.2"
    "hvtp.4"
    // Foundry bitcell metal width/spacing waivers
    "met1.1"; "met1.2"; "met1.5"; "met1.6"
    "met2.1"; "met2.2"; "met2.5"; "met2.6"
    // mcon / via rules — foundry bitcell packs contacts at min
    // width and min spacing
    "mcon.1"; "mcon.2"
    "via.2"; "via.4a"; "via.5a"
    // Implant / cap rules
    "psdm.5a"; "psdm.2"
    "nsdm.5a"; "nsdm.2"
    "var.1"; "var.2"; "var.4"
]

/// AreaID core layer key — `areaid_core` (81/2). Polygons on
/// this layer mark the COREID regions inside which waivers
/// apply.
let private areaidCoreKey = 81, 2

let private bboxOf (poly: FlatPolygon) : int64 * int64 * int64 * int64 =
    let mutable xMin = System.Int64.MaxValue
    let mutable yMin = System.Int64.MaxValue
    let mutable xMax = System.Int64.MinValue
    let mutable yMax = System.Int64.MinValue
    for p in poly.Points do
        if p.X < xMin then xMin <- p.X
        if p.X > xMax then xMax <- p.X
        if p.Y < yMin then yMin <- p.Y
        if p.Y > yMax then yMax <- p.Y
    xMin, yMin, xMax, yMax

/// Collect every `areaid_core` polygon's bbox from a flat
/// polygon array. Returns an empty array when none — no waivers
/// fire and the full DRC report passes through unfiltered.
let collectCoreAreas
        (flat: FlatPolygon array)
        : (int64 * int64 * int64 * int64) array =
    flat
    |> Array.filter (fun p ->
        p.Layer = fst areaidCoreKey && p.DataType = snd areaidCoreKey)
    |> Array.map bboxOf

/// Is `bb` fully inside any of the COREID area bboxes?
let private bboxFullyInside
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        (areas: (int64 * int64 * int64 * int64) array)
        : bool =
    let mutable hit = false
    let mutable i = 0
    while not hit && i < areas.Length do
        let (ax1, ay1, ax2, ay2) = areas.[i]
        if bx1 >= ax1 && by1 >= ay1 && bx2 <= ax2 && by2 <= ay2 then
            hit <- true
        i <- i + 1
    hit

/// Decide whether a (ruleName, violationBbox) pair should be
/// waived. True when:
///   * `ruleName` appears in `waiverRuleIds`, AND
///   * `violationBbox` is fully contained by at least one COREID
///     area bbox.
/// Callers (Check.fs) drop violations the predicate accepts.
let isWaived
        (areas: (int64 * int64 * int64 * int64) array)
        (ruleName: string)
        (violationBbox: int64 * int64 * int64 * int64)
        : bool =
    if not (waiverRuleIds.Contains ruleName) then false
    elif areas.Length = 0 then false
    else bboxFullyInside violationBbox areas

// ---------------------------------------------------------------------
// Foundry-primitive cell waiver
//
// SKY130 foundry primitives (FET cells, BJT cells, resistors,
// caps) ship as pre-validated layouts whose internal geometry
// is foundry-DRC-clean by construction even when it appears to
// violate stock rules — they're packed at process minimums,
// asymmetric enclosures, sub-spec contact spacings. Magic's
// rule deck implicitly waives DRC errors that are fully
// internal to such cells.
//
// Without `areaid_core` polygons (which only mark SRAM bitcells,
// not all foundry primitives), the COREID waiver above can't
// catch these. The mechanism here is name-based: cells matching
// known foundry naming conventions are treated as opaque, and
// any violation whose bbox sits entirely inside one of their
// SRef-instance footprints is waived.
//
// Detection heuristic: cells whose name starts with one of the
// known foundry prefixes are treated as foundry primitives.
// Future enhancement: explicit `(foundry yes)` annotation in
// .rkt, or path-based detection ("imported from .../primitives/
// directory").

let private foundryPrefixes : string array = [|
    "pfet_"; "nfet_"
    "bjt_"
    "cap_"; "capacitor_"
    "res_"; "resistor_"
    "sky130_fd_"   // SKY130 PDK std cells
|]

/// Rules that Magic implicitly waives when the violation is
/// fully internal to a foundry-primitive cell. This is a SUBSET
/// of `waiverRuleIds` — foundry cells get the contact-packing /
/// spacing relaxations because the foundry pre-validated those
/// dimensions, but FUNDAMENTAL rules (min-area, well-spacing,
/// implant-vs-well interactions) are still checked everywhere.
///
/// Empirically derived from running Magic on cim_reram and
/// comparing against viz output. Magic fires met1.6, nwell.2a,
/// difftap.9 even when the geometry is inside foundry FET
/// footprints — those rules are NOT in this list. Magic
/// silently waives mcon.2, licon.2, nsdm.2, psdm.2, poly.7/8,
/// li.3 in foundry footprints — those ARE.
let private foundryWaivedRules : Set<string> = Set.ofList [
    // Contact / via packing — foundry cells pack at the foundry
    // process minimum, which is below Magic's stock spec.
    "mcon.1"; "mcon.2"
    "licon.1"; "licon.2"
    "via.2"
    // Implant spacing — foundry cells often abut implants tighter
    // than user-routing rules require.
    "nsdm.2"; "psdm.2"
    // Local interconnect — foundry cells pack li1 tighter than
    // user routing.
    "li.1"; "li.3"
    // Poly endcaps — foundry cell layout uses precise foundry-
    // measured endcap values, sometimes below stock spec.
    "poly.7"; "poly.8"
    // Enclosure rules — foundry contacts/vias use asymmetric
    // enclosures that the rule deck's "one direction" handling
    // gets right but our simpler model may not.
    "licon.5a"; "licon.5c"; "licon.8"
    "li.5"; "met1.5"; "met2.5"
    "via.4a"; "via.5a"
    // Well-overlap-of-implant — foundry cells have
    // implant patterns that overlap nwell edges in ways the
    // simpler stock rule mis-reads.
    "nwell.5"
]

/// Is this cell name a foundry primitive by our heuristic? Used
/// to decide whether to waive internal DRC violations.
let isFoundryCell (cellName: string) : bool =
    if System.String.IsNullOrEmpty cellName then false
    else
        foundryPrefixes
        |> Array.exists (fun prefix -> cellName.StartsWith prefix)

/// Compute the world bbox of every foundry-primitive cell
/// instance present in `flat`. Groups flat polygons by
/// (SourceStructure, TopInstanceIndex) — same SRef instance —
/// and returns the bbox of each group whose source cell is
/// foundry by the heuristic above.
///
/// Caller hands the result to `isFoundryWaived` per violation.
let collectFoundryFootprints
        (flat: Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon array)
        : (int64 * int64 * int64 * int64) array =
    let groups =
        System.Collections.Generic.Dictionary<
            string * int option,
            int64 * int64 * int64 * int64>()
    for p in flat do
        if isFoundryCell p.SourceStructure then
            let key = (p.SourceStructure, p.TopInstanceIndex)
            let xs = p.Points |> Array.map (fun pt -> pt.X)
            let ys = p.Points |> Array.map (fun pt -> pt.Y)
            let bb =
                Array.min xs, Array.min ys,
                Array.max xs, Array.max ys
            match groups.TryGetValue key with
            | true, (gx1, gy1, gx2, gy2) ->
                let (bx1, by1, bx2, by2) = bb
                groups.[key] <-
                    (min gx1 bx1, min gy1 by1,
                     max gx2 bx2, max gy2 by2)
            | _ ->
                groups.[key] <- bb
    groups.Values |> Array.ofSeq

/// True iff the violation bbox is fully inside at least one
/// foundry-cell-instance footprint AND every polygon
/// contributing to the violation is itself authored in a
/// foundry cell. The dual test avoids the over-suppression bug
/// where a user-cell polygon (e.g. a met1 fragment in
/// `lshift_1v8_to_3v3`) coincidentally falls inside a foundry
/// FET footprint and would be wrongly waived by bbox alone.
///
/// `contributingPolys` is the list of input polygons whose
/// bbox overlaps the violation bbox. The caller assembles it
/// from the same flat array the check ran on.
let isFoundryWaived
        (foundryFootprints: (int64 * int64 * int64 * int64) array)
        (ruleName: string)
        (violationBbox: int64 * int64 * int64 * int64)
        (contributingPolys: Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon array)
        : bool =
    if not (foundryWaivedRules.Contains ruleName) then false
    elif foundryFootprints.Length = 0 then false
    elif not (bboxFullyInside violationBbox foundryFootprints) then false
    elif contributingPolys.Length = 0 then
        // No identifiable contributing polys (e.g. a Region-
        // morphology violation where the input geometry isn't
        // tracked back to specific polys). Fall back to the
        // bbox-only check — the violation falls inside a
        // foundry footprint AND the rule is in the foundry-
        // waiver list, so most likely waive-worthy.
        true
    else
        contributingPolys
        |> Array.forall (fun p -> isFoundryCell p.SourceStructure)
