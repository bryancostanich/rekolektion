namespace Rekolektion.Viz.Core.Tests

open System.IO
open Xunit
open Xunit.Abstractions
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Routing
open Rekolektion.Viz.Core.Routing.VisibilityGraph

type WalkAroundRealMacroTests(out: ITestOutputHelper) =

    let macroPath =
        "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cell_designs/bl_clamp/blc_trim_dac.rkt"

    let hasMacro () = File.Exists macroPath

    let loadDoc () =
        let doc, _w = Layout.LayoutLoader.load macroPath
        doc

    [<Fact>]
    member _.``REPRO: drn_R pin at (-393, 8023) on li1`` () =
        if not (hasMacro ()) then () else

        let doc = loadDoc ()
        let flat = Layout.Flatten.flatten doc
        let nets = Net.LabelFlood.derive doc

        out.WriteLine(sprintf "flat polys: %d" flat.Length)
        out.WriteLine(sprintf "nets count: %d" nets.Count)
        let drnR = Map.tryFind "drn_R" nets
        let drnRCount =
            match drnR with
            | Some e -> e.Polygons.Length
            | None -> -1
        out.WriteLine(sprintf "drn_R polygons claimed: %d" drnRCount)
        drnRCount |> should be (greaterThan 0)

        let layer : Obstacles.LayerKey = { Number = 67; DataType = 20 }
        let startPt : Pt = { X = -393L; Y = 8023L }
        let cursorPt : Pt = { X = -393L; Y = 6225L }
        let dxAbs = abs (cursorPt.X - startPt.X)
        let dyAbs = abs (cursorPt.Y - startPt.Y)
        let margin = max (dxAbs + dyAbs) 0L
        let region : Obstacles.Region =
            { XMin = (min startPt.X cursorPt.X) - margin
              YMin = (min startPt.Y cursorPt.Y) - margin
              XMax = (max startPt.X cursorPt.X) + margin
              YMax = (max startPt.Y cursorPt.Y) + margin }

        let netIdx = Obstacles.buildNetIndex nets
        let obstaclePolys =
            Obstacles.obstaclesInRegion layer "drn_R" netIdx region flat
        out.WriteLine(sprintf "obstacles in region: %d" obstaclePolys.Length)

        let bboxOf (fp : FlatPolygon) : int64 * int64 * int64 * int64 =
            let mutable xMin = System.Int64.MaxValue
            let mutable yMin = System.Int64.MaxValue
            let mutable xMax = System.Int64.MinValue
            let mutable yMax = System.Int64.MinValue
            for pt in fp.Points do
                if pt.X < xMin then xMin <- pt.X
                if pt.X > xMax then xMax <- pt.X
                if pt.Y < yMin then yMin <- pt.Y
                if pt.Y > yMax then yMax <- pt.Y
            xMin, yMin, xMax, yMax

        let containers =
            obstaclePolys
            |> Array.mapi (fun i p ->
                let (xMin, yMin, xMax, yMax) = bboxOf p
                let inside =
                    startPt.X > xMin && startPt.X < xMax
                    && startPt.Y > yMin && startPt.Y < yMax
                i, p, inside, (xMin, yMin, xMax, yMax))
            |> Array.filter (fun (_, _, inside, _) -> inside)

        out.WriteLine(sprintf "containers of start (%d, %d): %d"
                        startPt.X startPt.Y containers.Length)
        for (i, p, _, (x0, y0, x1, y1)) in containers do
            let net = Obstacles.netOf netIdx p
            out.WriteLine(
                sprintf "  obstacle %d: layer=%d/%d bbox=(%d,%d)..(%d,%d) net=%A src=%s/%d"
                    i p.Layer p.DataType x0 y0 x1 y1 net p.SourceStructure p.SourceIndex)

        // Also: what polygons of net drn_R contain the start?
        let drnRContaining =
            match drnR with
            | Some entry ->
                flat
                |> Array.filter (fun fp ->
                    entry.Polygons
                    |> List.exists (fun pr ->
                        pr.Structure = fp.SourceStructure
                        && pr.Layer = fp.Layer
                        && pr.DataType = fp.DataType
                        && pr.Index = fp.SourceIndex))
                |> Array.filter (fun fp ->
                    let (xMin, yMin, xMax, yMax) = bboxOf fp
                    startPt.X >= xMin && startPt.X <= xMax
                    && startPt.Y >= yMin && startPt.Y <= yMax)
            | None -> [||]
        out.WriteLine(sprintf "drn_R polygons containing the start: %d" drnRContaining.Length)
        for fp in drnRContaining do
            let (x0, y0, x1, y1) = bboxOf fp
            out.WriteLine(
                sprintf "  drnR-poly layer=%d/%d bbox=(%d,%d)..(%d,%d) src=%s/%d"
                    fp.Layer fp.DataType x0 y0 x1 y1 fp.SourceStructure fp.SourceIndex)

        containers.Length |> should equal 0
