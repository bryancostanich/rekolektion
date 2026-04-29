module Rekolektion.Viz.Render.Tests.ExtruderTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Gds.Types
open Rekolektion.Viz.Core
open Rekolektion.Viz.Render.Mesh

let private rect x y w h = [{X=x;Y=y};{X=x+w;Y=y};{X=x+w;Y=y+h};{X=x;Y=y+h};{X=x;Y=y}]

[<Fact>]
let ``Extruder.extrude produces 8 vertices per rectangular layer`` () =
    // Layer 68 dt 20 = met1 (in SKY130 PDK numbering)
    let lib =
        { Name = "x"; UserUnitsPerDbUnit = 0.001; DbUnitsInMeters = 1e-9
          Structures = [{
              Name = "top"
              Elements = [ Boundary { Layer = 68; DataType = 20; Points = rect 0L 0L 1000L 1000L } ]
          }] }
    let flat = Layout.Flatten.flatten lib
    let mesh = Extruder.extrude lib.UserUnitsPerDbUnit flat
    // 4 unique vertices in the rect (closing point stripped) x 2 (top + bottom) = 8.
    mesh.Vertices.Length |> should equal 8
    // 4-vertex polygon -> top fan (2 triangles, 6 indices) + bottom fan (6) + 4 sides x 6 = 36.
    mesh.Indices.Length |> should equal 36
