module Rekolektion.Viz.Render.Tests.MeshPickingTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Render.Mesh

[<Fact>]
let ``encodeId then decodeId is identity for small ids`` () =
    for id in [0; 1; 42; 65535; 16777214] do
        let (r, g, b) = Picking.encodeId id
        Picking.decodeId (r, g, b) |> should equal id

[<Fact>]
let ``encodeId rejects ids that exceed 24-bit range`` () =
    (fun () -> Picking.encodeId 16777216 |> ignore) |> should throw typeof<System.ArgumentException>

[<Fact>]
let ``encodeId rejects negative ids`` () =
    (fun () -> Picking.encodeId -1 |> ignore) |> should throw typeof<System.ArgumentException>
