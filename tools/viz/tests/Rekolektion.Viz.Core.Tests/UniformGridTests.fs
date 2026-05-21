module Rekolektion.Viz.Core.Tests.UniformGridTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Spatial

let private bb (x0, y0, x1, y1) : UniformGrid.Bbox =
    (int64 x0, int64 y0, int64 x1, int64 y1)

[<Fact>]
let ``empty index returns no hits for any query`` () =
    let idx = UniformGrid.build 100L [||]
    UniformGrid.queryBboxArray idx (bb (0, 0, 1000, 1000))
    |> should be Empty

[<Fact>]
let ``single bbox is returned by an overlapping query`` () =
    let idx = UniformGrid.build 100L [| bb (50, 50, 150, 150) |]
    UniformGrid.queryBboxArray idx (bb (100, 100, 200, 200))
    |> should equal [| 0 |]

[<Fact>]
let ``bbox far outside the query rectangle is NOT returned`` () =
    let idx = UniformGrid.build 100L [| bb (0, 0, 50, 50) |]
    UniformGrid.queryBboxArray idx (bb (1000, 1000, 2000, 2000))
    |> should be Empty

[<Fact>]
let ``query returns only the indices whose cells overlap the query`` () =
    let bboxes = [|
        bb (0, 0, 50, 50)            // cell (0,0)
        bb (500, 500, 600, 600)      // cell (5,5)
        bb (250, 250, 300, 300)      // cell (2,2)
    |]
    let idx = UniformGrid.build 100L bboxes
    let hits = UniformGrid.queryBboxArray idx (bb (0, 0, 100, 100))
    hits |> Array.sort |> should equal [| 0 |]

[<Fact>]
let ``bbox spanning multiple cells is found from any of them`` () =
    // Bbox covers cells (0,0), (0,1), (1,0), (1,1).
    let idx = UniformGrid.build 100L [| bb (50, 50, 150, 150) |]
    // Each individual cell of the four should still find it.
    UniformGrid.queryBboxArray idx (bb (0, 0, 99, 99))      |> should equal [| 0 |]
    UniformGrid.queryBboxArray idx (bb (100, 0, 199, 99))   |> should equal [| 0 |]
    UniformGrid.queryBboxArray idx (bb (0, 100, 99, 199))   |> should equal [| 0 |]
    UniformGrid.queryBboxArray idx (bb (100, 100, 199, 199))|> should equal [| 0 |]

[<Fact>]
let ``bbox spanning multiple cells is deduplicated in a single query`` () =
    // Query bbox also spans all 4 cells the indexed bbox lives in;
    // de-dup via internal HashSet should keep it to one hit.
    let idx = UniformGrid.build 100L [| bb (50, 50, 150, 150) |]
    UniformGrid.queryBboxArray idx (bb (0, 0, 200, 200))
    |> should equal [| 0 |]

[<Fact>]
let ``queryBbox callback fires once per matching index even on overlap`` () =
    let idx = UniformGrid.build 100L [| bb (50, 50, 150, 150) |]
    let hits = System.Collections.Generic.List<int>()
    UniformGrid.queryBbox idx (bb (0, 0, 200, 200)) (fun i -> hits.Add i)
    hits.Count |> should equal 1
    hits.[0]   |> should equal 0

[<Fact>]
let ``suggestCellSize returns a reasonable size for a typical cell load`` () =
    // Synthetic 1000 rects spread in a 100 µm × 100 µm window.
    // Target ~16 polys per cell → side ≈ sqrt(100k * 100k * 100k / (1000/16))
    // — give or take. Just check it's inside the clamps and finite.
    let bboxes =
        [|
            for i in 0 .. 999 do
                let x = (i % 100) * 1000
                let y = (i / 100) * 1000
                yield bb (x, y, x + 500, y + 500)
        |]
    let cs = UniformGrid.suggestCellSize bboxes
    cs |> should be (greaterThanOrEqualTo 100L)
    cs |> should be (lessThanOrEqualTo 10000L)

[<Fact>]
let ``suggestCellSize returns the default for an empty input`` () =
    UniformGrid.suggestCellSize [||] |> should equal 1000L

[<Fact>]
let ``index correctly returns multiple overlapping bboxes`` () =
    let bboxes = [|
        bb (0, 0, 100, 100)
        bb (50, 50, 150, 150)
        bb (200, 200, 300, 300)
    |]
    let idx = UniformGrid.build 100L bboxes
    let hits = UniformGrid.queryBboxArray idx (bb (75, 75, 125, 125))
    Array.sort hits |> should equal [| 0; 1 |]
