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

/// Rules that Magic implicitly waives when the violation centre
/// lies inside a foundry-primitive cell footprint, paired with a
/// per-rule expansion margin (in nm = DBU on SKY130). MUST stay
/// in sync with `src/rekolektion/verify/drc.py:_WAIVER_RULE_MARGIN_UM`
/// — `verify_drc` and the viz are two implementations of the
/// same policy, and divergence is what produces the "the F#
/// editor reports 200 fires but `verify_drc(full=True)` is clean"
/// confusion.
///
/// Width / area / overlap / enclosure rules use a 0 margin —
/// the violation is contained inside a single polygon, so a
/// centre point outside the cell footprint is always a real
/// user-routing bug. Spacing rules use a small margin (≈ rule
/// minimum × 1.5) because a spacing tile straddles two polygons
/// and its centre can sit just past the cell edge when one
/// polygon is at the boundary. Cross-cell well / implant rules
/// use a large margin so abutted-nwell, LV-vs-MV diffusion, etc.
/// boundary tiles still classify as waivers.
let private foundryWaiverMarginNm : Map<string, int64> =
    Map.ofList [
        // --- WIDTH / AREA / OVERLAP / ENCLOSURE (margin 0) -----------
        "li.1",       0L
        "li.c1",      0L
        "li.6",       0L
        "met1.1",     0L
        "met1.6",     0L
        "met2.1",     0L
        "met2.6",     0L
        "mcon.1",     0L
        "licon.1",    0L
        "poly.1a",    0L
        "diff/tap.1", 0L
        "diff/tap.2", 0L
        "diff/tap.8", 0L
        "diff/tap.9", 0L
        "nwell.1",    0L
        "dnwell.2",   0L
        "poly.4",     0L
        "poly.5",     0L
        "poly.7",     0L
        "poly.8",     0L
        "poly.11",    0L
        "licon.5a",   0L
        "licon.5b",   0L
        "licon.5c",   0L
        "licon.7",    0L
        "licon.8",    0L
        "licon.8a",   0L
        "licon.9",    0L
        "licon.10",   0L
        "licon.11",   0L
        "licon.14",   0L
        "psd.5a",     0L
        "psd.5b",     0L
        "psd.10b",    0L
        "nsd.10b",    0L
        "psdm.5a",    0L
        "hvtp.4",     0L
        "var.1",      0L
        "var.2",      0L
        "var.4",      0L
        "x.2",        0L
        "met1.5",     0L
        "met2.4",     0L
        "met2.5",     0L
        "via.4a",     0L
        "via.4b",     0L   // viz-only rule (asymmetric 0.085/0.055); Magic
                           // deck uses the simpler via.4a composite. Foundry
                           // FET cells pass the simpler check; the stricter
                           // asymmetric check that catches sub-min-width
                           // pads is still useful in user routing.
        "via.5a",     0L
        "via.5b",     0L   // same story for met2 (via.5b mirrors via.4b).
        "met4.2",     0L
        "rr1.1",      0L
        "rr1.2",      0L
        // --- SPACING rules (small per-rule margin) --------------------
        "li.3",       250L
        "li.c2",      250L
        "met1.2",     250L
        "met2.2",     250L
        "mcon.2",     250L
        "licon.2",    250L
        "poly.2",     300L
        "via.2",      250L
        "diff/tap.3", 300L
        // --- CROSS-CELL WELL / IMPLANT / SPECIAL-DIFF (large margin) -
        "nwell.2a",     1500L
        "nwell.7",      1500L
        "dnwell.3",     1500L
        "diff/tap.15a",  500L
        "diff/tap.22",   500L
        "diff/tap.23",   500L
        "diff/tap.24",   500L
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

/// True iff the violation tile centre lies inside at least one
/// foundry-cell-instance footprint, optionally expanded by the
/// rule's per-rule margin. Matches Magic's cell-scope DRC
/// semantics at instantiation: the foundry cell's internal
/// geometry is pre-validated once when the cell was designed
/// and not re-checked at every instantiation site.
///
/// Algorithm matches `src/rekolektion/verify/drc.py` line 466-489
/// EXACTLY:
///   1. Look up the rule name in `foundryWaiverMarginNm`. Rule
///      not in the table → never waive (real error).
///   2. Compute the violation's centre `(cx, cy)`.
///   3. For each footprint `(fx0, fy0, fx1, fy1)`, check if
///      `(fx0 - margin) ≤ cx ≤ (fx1 + margin)` and likewise on Y.
///      Any hit → waive.
///
/// `contributingPolys` is retained in the signature for callers
/// that already assemble it but is no longer consulted — the
/// margin-expanded centre-point test is sufficient for the
/// foundry-cell scope semantics and matches `verify_drc`'s
/// production tape-out path.
let isFoundryWaived
        (foundryFootprints: (int64 * int64 * int64 * int64) array)
        (ruleName: string)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        (_contributingPolys: Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon array)
        : bool =
    match Map.tryFind ruleName foundryWaiverMarginNm with
    | None -> false
    | Some margin ->
        if foundryFootprints.Length = 0 then false
        else
            let cx = (bx1 + bx2) / 2L
            let cy = (by1 + by2) / 2L
            foundryFootprints
            |> Array.exists (fun (fx0, fy0, fx1, fy1) ->
                (fx0 - margin) <= cx && cx <= (fx1 + margin)
                && (fy0 - margin) <= cy && cy <= (fy1 + margin))

// ---------------------------------------------------------------------
// Message-text waiver — used by the Magic-vs-viz parity test harness
// to apply the SAME foundry-waiver filter to Magic's raw output that
// `verify_drc` applies. Lets the test compare waived-viz to waived-
// Magic instead of waived-viz to raw-Magic (the old test was
// comparing across waiver states and "passed" purely on
// bbox-clustering density similarity — see git history for the
// false-equivalency analysis).
//
// Mirrors the Python implementation in
// src/rekolektion/verify/drc.py exactly:
//   - `_extract_rule_ids` (regex pulls the trailing "(...)" rule id
//     list out of Magic's free-form message, splits on "-" or "+",
//     strips any leading "N *" multiplier).
//   - `_is_waiver` (all extracted rule IDs must be in the waiver
//     table; empty-ID falls through to the message-text-list path
//     which we don't model here yet — none of our test fixtures
//     trigger it).
//   - The per-tile centre-in-expanded-footprint check, with the
//     margin being the MAX margin across all rule IDs in the
//     composite (most permissive — matches Python's `max(...)`).
// ---------------------------------------------------------------------

let private ruleIdRe =
    System.Text.RegularExpressions.Regex(@"\(([^()]+)\)\s*$")
let private splitRe =
    System.Text.RegularExpressions.Regex(@"\s*[-+]\s*")
let private mulPrefixRe =
    System.Text.RegularExpressions.Regex(@"^\s*\d+(\.\d+)?\s*\*\s*")

/// Pull the rule IDs out of a Magic DRC message — e.g.
/// "Metal1 overlap of Via1 < 6 in one direction (via.5a - via.4a)"
/// → ["via.5a"; "via.4a"]. Returns [] when no parenthesised id
/// list is present.
let extractRuleIds (msg: string) : string list =
    let m = ruleIdRe.Match msg
    if not m.Success then []
    else
        let inner = m.Groups.[1].Value.Trim()
        splitRe.Split inner
        |> Array.map (fun p -> mulPrefixRe.Replace(p, "").Trim())
        |> Array.filter (fun p -> p.Length > 0)
        |> Array.toList

/// True iff Magic's (msg, bbox) tuple should be waived under the
/// foundry-cell-internal policy. See module-top comment for the
/// exact algorithm and the Python reference it mirrors.
let waiveByMessage
        (foundryFootprints: (int64 * int64 * int64 * int64) array)
        (msg: string)
        ((bx1, by1, bx2, by2): int64 * int64 * int64 * int64)
        : bool =
    let ruleIds = extractRuleIds msg
    if ruleIds.IsEmpty then false
    elif foundryFootprints.Length = 0 then false
    else
        let margins =
            ruleIds
            |> List.map (fun rid -> Map.tryFind rid foundryWaiverMarginNm)
        if margins |> List.exists Option.isNone then false
        else
            let maxMargin =
                margins
                |> List.choose id
                |> List.max
            let cx = (bx1 + bx2) / 2L
            let cy = (by1 + by2) / 2L
            foundryFootprints
            |> Array.exists (fun (fx0, fy0, fx1, fy1) ->
                (fx0 - maxMargin) <= cx && cx <= (fx1 + maxMargin)
                && (fy0 - maxMargin) <= cy && cy <= (fy1 + maxMargin))
