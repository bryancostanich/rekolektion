module Rekolektion.Viz.Core.Tests.D13MuxRouteShapeTests

// User report 2026-05-31: drawing a VDD wire from slice 2 → slice 3
// on d13_mux.rkt produces the wrong path shape.
//
// Cursor: (28615,4510) → (40185,4510) on li1. The VSS rail at
// (32745, 4190, 34585, 4520) blocks the direct corridor.
//
// CURRENT BAD path (over-the-top):
//   (28615,4510) → (32489,4510) → (32489,4776) → (40185,4776) → (40185,4510)
// Stays at Y=4776 (elevated) from X=32489 all the way to X=40185 —
// the entire 7696 nm rest-of-route — and only drops back to start-Y
// at the cursor. Obstacle ends at X=34585; staying elevated past
// it is wasted elevation.
//
// EXPECTED hug-the-obstacle path:
//   (28615,4510) → (32489,4510) → (32489,4776) → (~34800,4776)
//                → (~34800,4510) → (40185,4510)
// Drops back to start-Y as soon as past the obstacle's right edge
// (plus clearance), then continues east along the original corridor.
//
// Manhattan length is identical for both shapes — this is purely a
// tie-break in VisibilityGraph.shortestPath. Current tie-break
// happens to pick over-the-top; the user wants hug.

open System.IO
open Xunit
open Xunit.Abstractions
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.Core.Routing

type D13MuxRouteShape(out : ITestOutputHelper) =
    let macroPath =
        "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cell_designs/column_readout_chain/d13_mux.rkt"
    let hasMacro () = File.Exists macroPath

    // VSS rail (the obstacle) li1 footprint between slice 2 and 3,
    // taken from the live obstacle dump in D13MuxCollisionRepro:
    let obstacleXMin = 32745L
    let obstacleXMax = 34585L

    // li1 clearance: half wire-width + spacing = 85 + 170 = 255 nm.
    let clearance = 255L

    [<Fact>]
    member _.``VDD slice 2 → slice 3 must hug the VSS obstacle, not stay elevated to the cursor`` () =
        if not (hasMacro ()) then
            out.WriteLine "SKIP: d13_mux.rkt not available"
        else

        let doc, _ = LayoutLoader.load macroPath
        let flat = Flatten.flatten doc
        let nets = Net.LabelFlood.derive doc

        let layer : Obstacles.LayerKey = { Number = 67; DataType = 20 }
        let startNet = "VDD"
        let key : WalkAround.BuildKey =
            { Layer = layer; StartNet = startNet
              Clearance = clearance; FlatPolyRef = flat
              NetMapRef = nets }

        let startPt  : VisibilityGraph.Pt = { X = 28615L; Y = 4510L }
        let cursorPt : VisibilityGraph.Pt = { X = 40185L; Y = 4510L }
        let startY = startPt.Y

        let dummyRegion : Obstacles.Region =
            { XMin = 0L; YMin = 0L; XMax = 0L; YMax = 0L }
        let graph = WalkAround.buildGraphInRegion key dummyRegion
        let path =
            VisibilityGraph.shortestPath
                System.Threading.CancellationToken.None
                VisibilityGraph.NoPreference graph startPt cursorPt

        match path with
        | None -> failwith "shortestPath returned None — expected a 5-node hug path"
        | Some nodes ->
            let dump =
                nodes
                |> List.map (fun pt -> sprintf "(%d,%d)" pt.X pt.Y)
                |> String.concat " → "
            out.WriteLine(sprintf "path: %s" dump)

            // "Hug" invariant: the elevated detour must terminate
            // WELL BEFORE the cursor. The bug shape stayed at
            // elevated Y all the way to the cursor.X (carrying the
            // dodge offset across the entire rest-of-route). The
            // hug shape drops to corridor Y soon after clearing the
            // obstacle stack, then continues east at corridor Y.
            //
            // We don't pin the exact drop X — multiple stacked
            // obstacles in the rail region (VSS pieces, implants)
            // mean the "earliest clear drop" is several µm past
            // the named obstacle's right edge. Just assert the
            // elevated segment doesn't extend all the way to the
            // cursor.
            ignore obstacleXMin
            ignore obstacleXMax
            ignore clearance
            let hugMargin = 2000L   // elevated must drop ≥2 µm before cursor X
            let elevatedReachesCursor =
                nodes
                |> List.pairwise
                |> List.exists (fun (a, b) ->
                    a.Y = b.Y && a.Y <> startY
                    && max a.X b.X > cursorPt.X - hugMargin)
            if elevatedReachesCursor then
                out.WriteLine
                    "FAIL: elevated segment carries the dodge offset to the cursor."
            elevatedReachesCursor |> Assert.False

            // Symmetric: elevated must NOT start at the cursor end
            // either (would imply the route went elevated only at
            // the right side rather than hugging the obstacle).
            let elevatedStartsAtCursor =
                nodes
                |> List.pairwise
                |> List.exists (fun (a, b) ->
                    a.Y = b.Y && a.Y <> startY
                    && min a.X b.X < startPt.X + hugMargin)
            elevatedStartsAtCursor |> Assert.False
