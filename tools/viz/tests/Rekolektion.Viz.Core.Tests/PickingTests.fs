module Rekolektion.Viz.Core.Tests.PickingTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout

let private square (x: int64) (y: int64) (size: int64) : Point list =
    [ { X = x;        Y = y }
      { X = x + size; Y = y }
      { X = x + size; Y = y + size }
      { X = x;        Y = y + size }
      { X = x;        Y = y } ]

[<Fact>]
let ``point inside square is contained`` () =
    let poly = square 0L 0L 100L
    Picking.pointInPolygon { X = 50L; Y = 50L } poly |> should equal true

[<Fact>]
let ``point outside square is not contained`` () =
    let poly = square 0L 0L 100L
    Picking.pointInPolygon { X = 150L; Y = 50L } poly |> should equal false

[<Fact>]
let ``point on edge is contained (boundary inclusive)`` () =
    let poly = square 0L 0L 100L
    Picking.pointInPolygon { X = 0L; Y = 50L } poly |> should equal true

[<Fact>]
let ``L-shape concavity excluded`` () =
    // L-shape: 100x100 square with the upper-right 50x50 carved out
    let poly = [
        { X = 0L;   Y = 0L }
        { X = 100L; Y = 0L }
        { X = 100L; Y = 50L }
        { X = 50L;  Y = 50L }
        { X = 50L;  Y = 100L }
        { X = 0L;   Y = 100L }
        { X = 0L;   Y = 0L }
    ]
    Picking.pointInPolygon { X = 75L; Y = 75L } poly |> should equal false
    Picking.pointInPolygon { X = 25L; Y = 25L } poly |> should equal true

// ─────────────────────────────────────────────────────────────────
// pickBoundaryFiltered — routing-layer-preempt-instance-pick helper
// used by GdsCanvasControl to let a wire on top of an SRef be
// picked instead of the SRef's bbox.  Filter predicate is supplied
// by the caller (canvas uses Routing.ViaStack.isRoutingLayer); the
// helper itself is layer-agnostic.
// ─────────────────────────────────────────────────────────────────

let private rect (x1, y1, x2, y2) (layerName: string) : Element =
    RectEl {
        Layer = Named ("sky130", layerName)
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2
        Net = None
        Props = []
        Comments = []
        SubFormComments = Map.empty
    }

// met1 = (68, 20), met2 = (69, 20), li1 = (67, 20) — all routing
// per Routing.ViaStack.isRoutingLayer.
// nwell = (64, 20), diff = (65, 20) — NOT routing layers.

let private isRoutingFilter (layer: Layer) : bool =
    Rekolektion.Viz.Core.Routing.ViaStack.isRoutingLayer
        (Rekolektion.Viz.Core.Rkt.ToGds.layerToGds layer)

[<Fact>]
let ``pickBoundaryFiltered returns None on empty element list`` () =
    Picking.pickBoundaryFiltered { X = 0L; Y = 0L } isRoutingFilter []
    |> should equal (None : (int * Poly) option)

[<Fact>]
let ``pickBoundaryFiltered picks the only routing-layer rect at point`` () =
    let elements = [
        rect (0L, 0L, 100L, 100L) "met1"
    ]
    let result =
        Picking.pickBoundaryFiltered { X = 50L; Y = 50L } isRoutingFilter elements
    result |> Option.map fst |> should equal (Some 0)

[<Fact>]
let ``pickBoundaryFiltered skips non-routing-layer rects`` () =
    // nwell rect at point — filter rejects it.
    let elements = [
        rect (0L, 0L, 100L, 100L) "nwell"
    ]
    let result =
        Picking.pickBoundaryFiltered { X = 50L; Y = 50L } isRoutingFilter elements
    result |> should equal (None : (int * Poly) option)

[<Fact>]
let ``pickBoundaryFiltered picks ONLY routing rect when both at point`` () =
    // nwell rect + met1 rect overlap at (50, 50).  Routing filter
    // skips the nwell, returns the met1.  This is the canvas's
    // wire-preempt-cell-selection case in isolation: the nwell
    // would normally be "underneath" the met1 wire.
    let elements = [
        rect (0L, 0L, 200L, 200L) "nwell"    // index 0
        rect (40L, 40L, 60L, 60L) "met1"     // index 1 — smaller, routing
    ]
    let result =
        Picking.pickBoundaryFiltered { X = 50L; Y = 50L } isRoutingFilter elements
    result |> Option.map fst |> should equal (Some 1)

[<Fact>]
let ``pickBoundaryFiltered picks SMALLEST-area routing rect when multiple`` () =
    // A wide met1 rail and a narrow met1 stub overlap at (50, 50).
    // The narrow stub is smaller area — picked.
    let elements = [
        rect (0L, 0L, 1000L, 1000L) "met1"   // index 0 — wide rail
        rect (40L, 40L, 60L, 60L) "met1"     // index 1 — narrow stub
    ]
    let result =
        Picking.pickBoundaryFiltered { X = 50L; Y = 50L } isRoutingFilter elements
    result |> Option.map fst |> should equal (Some 1)

[<Fact>]
let ``pickBoundaryFiltered returns None when point misses all routing rects`` () =
    let elements = [
        rect (0L, 0L, 100L, 100L) "met1"
        rect (200L, 200L, 300L, 300L) "met2"
    ]
    let result =
        Picking.pickBoundaryFiltered { X = 150L; Y = 150L } isRoutingFilter elements
    result |> should equal (None : (int * Poly) option)

[<Fact>]
let ``pickBoundaryFiltered with always-true filter behaves like broad pick`` () =
    let elements = [
        rect (0L, 0L, 100L, 100L) "nwell"
    ]
    let result =
        Picking.pickBoundaryFiltered
            { X = 50L; Y = 50L } (fun _ -> true) elements
    result |> Option.map fst |> should equal (Some 0)

[<Fact>]
let ``pickBoundaryFiltered considers met1 met2 li1 as routing`` () =
    // Sanity: every routing-layer name we claim to support actually
    // passes the filter through the Routing.ViaStack check.
    let elements = [
        rect (0L, 0L, 100L, 100L) "li1"
        rect (200L, 0L, 300L, 100L) "met1"
        rect (400L, 0L, 500L, 100L) "met2"
        rect (600L, 0L, 700L, 100L) "met3"
        rect (800L, 0L, 900L, 100L) "met4"
        rect (1000L, 0L, 1100L, 100L) "met5"
    ]
    [ 50L; 250L; 450L; 650L; 850L; 1050L ]
    |> List.iteri (fun expected x ->
        let result =
            Picking.pickBoundaryFiltered { X = x; Y = 50L } isRoutingFilter elements
        result |> Option.map fst |> should equal (Some expected))
