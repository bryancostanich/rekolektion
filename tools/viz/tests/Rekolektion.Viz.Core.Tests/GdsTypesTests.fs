module Rekolektion.Viz.Core.Tests.GdsTypesTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Gds.Types

[<Fact>]
let ``Point holds X Y as int64 DBU`` () =
    let p = { X = 12300L; Y = -4500L }
    p.X |> should equal 12300L
    p.Y |> should equal -4500L

[<Fact>]
let ``Boundary holds layer datatype and point list`` () =
    let b = { Layer = 68; DataType = 20; Points = [ { X = 0L; Y = 0L }; { X = 100L; Y = 0L } ] }
    b.Layer |> should equal 68
    b.Points |> List.length |> should equal 2

[<Fact>]
let ``Element DU includes all four GDS element types`` () =
    let _b: Element = Boundary { Layer = 0; DataType = 0; Points = [] }
    let _p: Element = Path { Layer = 0; DataType = 0; Width = 0; Points = [] }
    let _s: Element = SRef { StructureName = "x"; Origin = { X = 0L; Y = 0L }; Mag = 1.0; Angle = 0.0; Reflected = false }
    let _a: Element = ARef { StructureName = "x"; Origin = { X = 0L; Y = 0L }; Cols = 1; Rows = 1; ColPitch = { X = 0L; Y = 0L }; RowPitch = { X = 0L; Y = 0L }; Mag = 1.0; Angle = 0.0; Reflected = false }
    ()
