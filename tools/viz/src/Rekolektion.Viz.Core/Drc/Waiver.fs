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
