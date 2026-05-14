module Rekolektion.Viz.Core.Tests.Sky130LayerMapTests

/// Layer-map symmetry tests.  These pin the sky130 `(layer, datatype)`
/// → name table in the F# `Layout.Layer` module against the canonical
/// sky130 stream conventions (sourced from
/// `$PDK_ROOT/sky130B/libs.tech/magic/sky130B.tech` — see `calma`
/// lines) so:
///
/// 1. The F# table stays symmetric with the Python `_layer_map.py`
///    that is used by `rekolektion.primitives.sky130.gen_*` when
///    minting primitives, OR (when names diverge intentionally for
///    the misnamings noted below) the divergence is documented and
///    verified here.
///
/// 2. The psdm/nsdm GDS pairs can't silently re-swap.  The previous
///    swap caused every g5v0 FET to write its implant on the wrong
///    mask layer when serialized via `Rkt.ToGds`, producing
///    cascading false-positive Magic DRC violations.
///
/// 3. The HV implant / marker layers (hvi, hvntm, nwell_drawing, npc)
///    survive the `.rkt` → GDS round-trip.  When absent, HV FETs
///    lose their HV markers and Magic applies LV DRC rules.

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Rkt

/// Canonical sky130 stream layer assignments.  These are the ground
/// truth — if any of these regress, fix the table, not the test.
///
/// NOTE on names: `hvntm` at 78/44 and `nwell_drawing` at 125/20 are
/// the names Python `_layer_map.py` carries verbatim.  Per
/// `sky130B.tech` the sky130-canonical names are `hvtp` at 78/44 and
/// `hvntm` at 125/20 — see PR description for the follow-up rename.
let private canonicalPairs : (int * int * string) list = [
    // Wells / substrate
    64, 18, "dnwell"
    64, 20, "nwell"
    65, 20, "diff"
    65, 44, "tap"
    66, 20, "poly"
    66, 44, "licon1"          // matches Python + sky130 stream name
    67, 20, "li1"
    67, 44, "mcon"
    68, 20, "met1"
    68, 44, "via"
    69, 20, "met2"
    69, 44, "via2"
    70, 20, "met3"
    70, 44, "via3"
    71, 20, "met4"
    71, 44, "via4"
    72, 20, "met5"
    // HV markers — `_g5v0d10v5_` families fire HV DRC rules when these
    // markers are present.  Dropping them sends Magic to LV rules and
    // produces false-positive violations.
    75, 20, "hvi"
    78, 44, "hvntm"           // Python misname — actually HVTP per sky130
    81,  2, "areaid_core"     // matches Python; renamed from "areaid.sc"
    81, 53, "areaid_lowtapdensity"
    89, 44, "mimcap"
    // Implant masks — sky130 puts NSDM at 93/44 and PSDM at 94/20.
    // The PREVIOUS F# entry had these swapped — see PR description.
    93, 44, "nsdm"
    94, 20, "psdm"
    95, 20, "npc"             // Nitride Poly Cut — HV gate-silicide block
    122, 16, "cfom_drawing"
    125, 20, "nwell_drawing"  // Python misname — actually HVNTM per sky130
    235,  4, "boundary"
]

[<Fact>]
let ``every canonical sky130 pair resolves through ToGds`` () =
    for (n, d, name) in canonicalPairs do
        let pair = ToGds.layerToGds (Named ("sky130", name))
        pair |> should equal (n, d)

[<Fact>]
let ``every canonical sky130 pair round-trips through OfGds`` () =
    for (n, d, name) in canonicalPairs do
        OfGds.layerFromGds n d |> should equal (Named ("sky130", name))

[<Fact>]
let ``psdm (94 20) and nsdm (93 44) are not swapped`` () =
    // Anchor for the P0 fix.  If this test fails, the F# table has
    // re-swapped psdm/nsdm and the GDS writer is corrupting implants.
    ToGds.layerToGds (Named ("sky130", "psdm")) |> should equal (94, 20)
    ToGds.layerToGds (Named ("sky130", "nsdm")) |> should equal (93, 44)

[<Fact>]
let ``HV marker layers survive ToGds (not silently dropped)`` () =
    // Anchor for the missing-layer fix.  If any of these resolve to
    // (0, 0), HV FETs are losing their HV implant/native-threshold
    // markers when serialized and Magic will apply LV DRC rules.
    let hvMarkers = [
        "hvi"; "hvntm"; "nwell_drawing"; "npc"
    ]
    for name in hvMarkers do
        let pair = ToGds.layerToGds (Named ("sky130", name))
        pair |> should not' (equal (0, 0))
