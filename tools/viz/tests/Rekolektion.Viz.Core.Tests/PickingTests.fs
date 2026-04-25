module Rekolektion.Viz.Core.Tests.PickingTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Gds.Types
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
