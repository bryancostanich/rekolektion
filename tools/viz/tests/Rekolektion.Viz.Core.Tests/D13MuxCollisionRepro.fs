module Rekolektion.Viz.Core.Tests.D13MuxCollisionRepro

// REPRO of the live "wire crossed VSS" bug 2026-05-30 on d13_mux.
//
// User routed a VDD jumper from VDD label at (28615, 4510) to VDD
// label at (40185, 4510) on li1 (67/20). The wire committed as a
// SINGLE horizontal rect (28530, 4425, 40270, 4595) crossing the
// VSS rail at (33580, 4270, 35520, 4440) — 15 nm Y overlap, same
// layer, foreign net → electrical short.
//
// The walkaround log showed `outcome="noPath"` at cursor
// (40185, 4510). When walkaround can't find a path, r.Auto stays
// empty, the commit polyline reduces to [anchor, cursor], and
// lShape decomposes that into a straight line bypassing every
// obstacle. The user expects either:
//   (a) walkaround returns a clean detour path, or
//   (b) commit refuses when walkaround fails.
//
// This probe loads d13_mux, runs shortestPath with the exact
// scenario, and asserts that EITHER a clean path is found OR the
// search returns None — but never a path that crosses VSS.

open System.IO
open Xunit
open Xunit.Abstractions
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.Core.Routing

type D13MuxCollisionRepro(out : ITestOutputHelper) =
    let macroPath =
        "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cell_designs/column_readout_chain/d13_mux.rkt"
    let hasMacro () = File.Exists macroPath

    [<Fact>]
    member _.``REPRO: VDD-to-VDD jumper across VSS rail must dodge or fail`` () =
        if not (hasMacro ()) then
            out.WriteLine "SKIP: d13_mux.rkt not available"
        else

        let doc, _ = LayoutLoader.load macroPath
        let flat = Flatten.flatten doc
        let nets = Net.LabelFlood.derive doc

        let layer : Obstacles.LayerKey = { Number = 67; DataType = 20 }
        let startNet = "VDD"
        let clearance = 85L + 170L   // li1 half-width + spacing
        let key : WalkAround.BuildKey =
            { Layer = layer; StartNet = startNet
              Clearance = clearance; FlatPolyRef = flat
              NetMapRef = nets }

        // Exact coordinates from the live log.
        let startPt : VisibilityGraph.Pt = { X = 28615L; Y = 4510L }
        let cursorPt : VisibilityGraph.Pt = { X = 40185L; Y = 4510L }

        // First — sanity. Is there a foreign obstacle in the route
        // corridor that we'd expect the walkaround to dodge?
        let netIdx = Obstacles.buildNetIndex nets
        let set = Obstacles.obstacleSet layer startNet netIdx flat
        let allObs = Obstacles.polygonsOf set
        let routeY = startPt.Y
        let routeMinX = min startPt.X cursorPt.X
        let routeMaxX = max startPt.X cursorPt.X
        let blockers =
            allObs
            |> Array.choose (fun fp ->
                let mutable xMin = System.Int64.MaxValue
                let mutable yMin = System.Int64.MaxValue
                let mutable xMax = System.Int64.MinValue
                let mutable yMax = System.Int64.MinValue
                for pt in fp.Points do
                    if pt.X < xMin then xMin <- pt.X
                    if pt.X > xMax then xMax <- pt.X
                    if pt.Y < yMin then yMin <- pt.Y
                    if pt.Y > yMax then yMax <- pt.Y
                // Foreign li1 rect overlapping route corridor in X
                // and with Y within wire+clearance of routeY?
                let yMargin = clearance + 85L
                let touchesY = yMin < routeY + yMargin && yMax > routeY - yMargin
                let touchesX = xMin < routeMaxX && xMax > routeMinX
                if touchesY && touchesX then
                    Some (sprintf "(%d,%d,%d,%d)" xMin yMin xMax yMax)
                else None)
        out.WriteLine(sprintf "obstacles in route corridor (Y±%d, X %d..%d): %d"
                        (clearance + 85L) routeMinX routeMaxX blockers.Length)
        for b in blockers |> Array.truncate 10 do
            out.WriteLine(sprintf "  blocker: %s" b)

        // Build graph + run shortestPath.
        let dummyRegion : Obstacles.Region =
            { XMin = 0L; YMin = 0L; XMax = 0L; YMax = 0L }
        let graph = WalkAround.buildGraphInRegion key dummyRegion
        out.WriteLine(sprintf "graph: %d nodes, %d obstacles"
                        graph.Nodes.Length graph.Obstacles.Length)

        // STRIP-DOWN diagnostic: live walkaround reported
        // `cursorInside: 5622` and `startInside: 5622` — both
        // endpoints inside the SAME foreign obstacle. Find it.
        let inside (pt : VisibilityGraph.Pt) (b : VisibilityGraph.Bbox) =
            pt.X > b.XMin && pt.X < b.XMax
            && pt.Y > b.YMin && pt.Y < b.YMax
        let mutable startInsideIdx = -1
        let mutable cursorInsideIdx = -1
        for i in 0 .. graph.Obstacles.Length - 1 do
            if startInsideIdx < 0 && inside startPt graph.Obstacles.[i] then
                startInsideIdx <- i
            if cursorInsideIdx < 0 && inside cursorPt graph.Obstacles.[i] then
                cursorInsideIdx <- i
        out.WriteLine(sprintf "startInside obstacle index = %d, cursorInside obstacle index = %d"
                        startInsideIdx cursorInsideIdx)
        if startInsideIdx >= 0 then
            let b = graph.Obstacles.[startInsideIdx]
            out.WriteLine(sprintf "  start-containing obstacle bbox (expanded): (%d,%d,%d,%d) — size %d × %d"
                            b.XMin b.YMin b.XMax b.YMax
                            (b.XMax - b.XMin) (b.YMax - b.YMin))
        if cursorInsideIdx >= 0 && cursorInsideIdx <> startInsideIdx then
            let b = graph.Obstacles.[cursorInsideIdx]
            out.WriteLine(sprintf "  cursor-containing obstacle bbox (expanded): (%d,%d,%d,%d) — size %d × %d"
                            b.XMin b.YMin b.XMax b.YMax
                            (b.XMax - b.XMin) (b.YMax - b.YMin))
        // What polygon does that obstacle come from? Match by bbox.
        if startInsideIdx >= 0 then
            let b = graph.Obstacles.[startInsideIdx]
            // graph.Obstacles[i] is the EXPANDED bbox; original is
            // shrunk by Clearance.
            let cl = graph.Clearance
            let originalBbox = (b.XMin + cl, b.YMin + cl, b.XMax - cl, b.YMax - cl)
            let (oxmin, oymin, oxmax, oymax) = originalBbox
            out.WriteLine(sprintf "  original (shrunk back): (%d,%d,%d,%d)"
                            oxmin oymin oxmax oymax)
            // Find the FlatPolygon whose bbox matches the original.
            let mutable matched = -1
            for k in 0 .. allObs.Length - 1 do
                let fp = allObs.[k]
                let mutable xMin = System.Int64.MaxValue
                let mutable yMin = System.Int64.MaxValue
                let mutable xMax = System.Int64.MinValue
                let mutable yMax = System.Int64.MinValue
                for pt in fp.Points do
                    if pt.X < xMin then xMin <- pt.X
                    if pt.X > xMax then xMax <- pt.X
                    if pt.Y < yMin then yMin <- pt.Y
                    if pt.Y > yMax then yMax <- pt.Y
                if matched < 0
                   && xMin = oxmin && yMin = oymin
                   && xMax = oxmax && yMax = oymax then
                    matched <- k
                    out.WriteLine(sprintf "  matched FlatPolygon: layer %d/%d, source=%s"
                                    fp.Layer fp.DataType fp.SourceStructure)

        let path =
            VisibilityGraph.shortestPath
                System.Threading.CancellationToken.None
                VisibilityGraph.NoPreference graph startPt cursorPt
        match path with
        | None ->
            out.WriteLine "shortestPath returned None — commit would emit straight line through obstacles."
        | Some nodes ->
            let dump =
                nodes
                |> List.map (fun pt -> sprintf "(%d,%d)" pt.X pt.Y)
                |> String.concat " → "
            out.WriteLine(sprintf "shortestPath returned %d-node path: %s" nodes.Length dump)

            // Decompose into segments and check each segment against
            // every obstacle. If any segment crosses a foreign li1
            // obstacle's interior (not its expanded clearance, the
            // actual silicon), the path is invalid.
            let pairs =
                nodes
                |> List.pairwise
            let mutable shorts = 0
            for (a, b) in pairs do
                let segMinX = min a.X b.X
                let segMaxX = max a.X b.X
                let segMinY = min a.Y b.Y
                let segMaxY = max a.Y b.Y
                for fp in allObs do
                    let mutable xMin = System.Int64.MaxValue
                    let mutable yMin = System.Int64.MaxValue
                    let mutable xMax = System.Int64.MinValue
                    let mutable yMax = System.Int64.MinValue
                    for pt in fp.Points do
                        if pt.X < xMin then xMin <- pt.X
                        if pt.X > xMax then xMax <- pt.X
                        if pt.Y < yMin then yMin <- pt.Y
                        if pt.Y > yMax then yMax <- pt.Y
                    // Segment interior crossing obstacle interior
                    // (strict inequality so abutments don't count).
                    if a.X = b.X then
                        // Vertical segment at x = a.X
                        if a.X > xMin && a.X < xMax
                           && segMinY < yMax && segMaxY > yMin then
                            shorts <- shorts + 1
                            out.WriteLine(sprintf
                                "  SHORT: V-seg x=%d Y[%d..%d] crosses obstacle (%d,%d,%d,%d)"
                                a.X segMinY segMaxY xMin yMin xMax yMax)
                    elif a.Y = b.Y then
                        if a.Y > yMin && a.Y < yMax
                           && segMinX < xMax && segMaxX > xMin then
                            shorts <- shorts + 1
                            out.WriteLine(sprintf
                                "  SHORT: H-seg y=%d X[%d..%d] crosses obstacle (%d,%d,%d,%d)"
                                a.Y segMinX segMaxX xMin yMin xMax yMax)
            Assert.Equal(0, shorts)

        // Whatever shortestPath returned, the user should not be
        // able to commit a wire crossing VSS. If path = None, the
        // commit path needs a different gate (a separate bug).
        ignore path

    [<Fact>]
    member _.``REPRO: after a straight-line VDD wire was committed, next walkaround finds noPath`` () =
        // SIMULATE the post-first-commit state. Live event chain
        // (06:47:38-52 on 2026-05-30):
        //   1. StartRoute at (28615, 4510), zero walkaround events
        //      logged in 2.6 s.
        //   2. RouteFinish committed [anchor, cursor] = straight
        //      horizontal li1 wire (28530, 4425, 40270, 4595).
        //   3. Second StartRoute, same coords.
        //   4. Walkaround returned `outcome=noPath, startInside=5622,
        //      cursorInside=5622`. obstacles=5625 (3 more than fresh
        //      disk = the wire body + 2 endpoint pads).
        //
        // Hypothesis: the just-committed VDD wire is classified as
        // FOREIGN by `isOurs` despite the incremental NetMap update.
        // Both endpoints land inside its expanded bbox → noPath.
        if not (hasMacro ()) then
            out.WriteLine "SKIP: d13_mux.rkt not available"
        else

        let doc, _ = LayoutLoader.load macroPath
        let layer : Obstacles.LayerKey = { Number = 67; DataType = 20 }
        let startNet = "VDD"
        let clearance = 85L + 170L

        // Step 1: simulate `RouteFinish` for the FIRST bad route.
        // The committed wire body bbox from element 93 in the live
        // geometry dump: (28530, 4425, 40270, 4595).
        let topCellName = doc.TopCell |> Option.defaultValue "TOP"
        let routeLayer =
            match Layout.Layer.bySky130Number layer.Number layer.DataType with
            | Some l -> Rkt.Types.Named(doc.Pdk, l.Name)
            | None   -> Rkt.Types.Unknown(layer.Number, layer.DataType)
        let mkRect (x1, y1, x2, y2) : Rkt.Types.Rectangle =
            { Layer = routeLayer
              X1 = x1; Y1 = y1; X2 = x2; Y2 = y2
              Net = None
              Props = [
                { Key = Routing.Wire.wireIdKey
                  Value = Rkt.Types.PvInt 1L }
              ]
              Comments = []
              SubFormComments = Map.empty }
        // Match the live commit: body + 2 endpoint pads (170×170
        // squares centred at start/cursor). Total 3 rects = matches
        // the +3 obstacle delta we observed live.
        let bodyRect = mkRect (28530L, 4425L, 40270L, 4595L)
        let startPad = mkRect (28530L, 4425L, 28700L, 4595L)
        let endPad   = mkRect (40100L, 4425L, 40270L, 4595L)
        let docCommitted =
            let updatedCells =
                doc.Cells
                |> List.map (fun c ->
                    if c.Name <> topCellName then c
                    else
                        { c with
                            Elements =
                                c.Elements
                                @ [Rkt.Types.RectEl startPad
                                   Rkt.Types.RectEl bodyRect
                                   Rkt.Types.RectEl endPad] })
            { doc with Cells = updatedCells }
        // Run dedup the same way commitRouteWith does.
        let docCommitted = Routing.Wire.dedupCoincidentRects docCommitted
        let flatCommitted = Flatten.flatten docCommitted
        out.WriteLine(sprintf "post-commit flat polys = %d" flatCommitted.Length)

        // Step 2: simulate the incremental NetMap update from
        // Update.fs:326-368. Find the newly-added rect by wireId,
        // build a PolygonRef, append to VDD's NetEntry.
        let netsFresh = Net.LabelFlood.derive doc
        let newRefs : Rekolektion.Viz.Core.Sidecar.Types.PolygonRef list =
            docCommitted.Cells
            |> List.tryFind (fun c -> c.Name = topCellName)
            |> Option.map (fun c ->
                c.Elements
                |> List.indexed
                |> List.choose (fun (i, el) ->
                    match el with
                    | Rkt.Types.RectEl r when Routing.Wire.getWireId r = Some 1 ->
                        let (num, dt) = Rkt.ToGds.layerToGds r.Layer
                        Some {
                            Structure = topCellName
                            Layer = num
                            DataType = dt
                            Index = i
                            TopInstanceIndex = None } : Rekolektion.Viz.Core.Sidecar.Types.PolygonRef option
                    | _ -> None))
            |> Option.defaultValue []
        out.WriteLine(sprintf "incremental newRefs count = %d, indices = %s"
                        newRefs.Length
                        (newRefs |> List.map (fun r -> string r.Index) |> String.concat ","))
        let nets' =
            let entry =
                match Map.tryFind startNet netsFresh with
                | Some e ->
                    { e with
                        Polygons = e.Polygons @ newRefs
                        SeedPolygons = e.SeedPolygons @ newRefs
                        DirectLabelPolys = e.DirectLabelPolys @ newRefs }
                | None ->
                    { Name = startNet
                      Class = Rekolektion.Viz.Core.Sidecar.Types.Signal
                      Polygons = newRefs
                      SeedPolygons = newRefs
                      DirectLabelPolys = newRefs }
            Map.add startNet entry netsFresh

        // Step 3: verify the new wire is classified as ours.
        let idx = Obstacles.buildNetIndex nets'
        let newFlatPoly =
            flatCommitted
            |> Array.tryFind (fun fp ->
                fp.Layer = layer.Number
                && fp.DataType = layer.DataType
                && fp.SourceStructure = topCellName
                && fp.Points.Length >= 4
                && (fp.Points
                    |> Array.exists (fun p -> p.X = 28530L && p.Y = 4425L)))
        match newFlatPoly with
        | None ->
            out.WriteLine "WARN: could not find the just-committed FlatPolygon in flatten output"
        | Some fp ->
            let claimants = Obstacles.claimantsOf idx fp
            let netOf = Obstacles.netOf idx fp
            let isOursVDD = Obstacles.isOurs idx startNet fp
            out.WriteLine(sprintf
                "committed-wire FlatPolygon: SourceIndex=%d TopInstance=%A claimants=%A netOf=%A isOurs(VDD)=%b"
                fp.SourceIndex fp.TopInstanceIndex claimants netOf isOursVDD)

        // Step 4: build obstacleSet + graph with the new state, see
        // if shortestPath returns noPath like live did.
        let set = Obstacles.obstacleSet layer startNet idx flatCommitted
        let allObs = Obstacles.polygonsOf set
        out.WriteLine(sprintf "post-commit obstacle count for layer %d/%d net %s = %d (was 5622 fresh)"
                        layer.Number layer.DataType startNet allObs.Length)

        let key : WalkAround.BuildKey =
            { Layer = layer; StartNet = startNet
              Clearance = clearance; FlatPolyRef = flatCommitted
              NetMapRef = nets' }
        let dummyRegion : Obstacles.Region =
            { XMin = 0L; YMin = 0L; XMax = 0L; YMax = 0L }
        let graph = WalkAround.buildGraphInRegion key dummyRegion
        let startPt : VisibilityGraph.Pt = { X = 28615L; Y = 4510L }
        let cursorPt : VisibilityGraph.Pt = { X = 40185L; Y = 4510L }

        let inside (pt : VisibilityGraph.Pt) (b : VisibilityGraph.Bbox) =
            pt.X > b.XMin && pt.X < b.XMax
            && pt.Y > b.YMin && pt.Y < b.YMax
        let mutable startInside = -1
        let mutable cursorInside = -1
        for i in 0 .. graph.Obstacles.Length - 1 do
            if startInside < 0 && inside startPt graph.Obstacles.[i] then
                startInside <- i
            if cursorInside < 0 && inside cursorPt graph.Obstacles.[i] then
                cursorInside <- i
        out.WriteLine(sprintf "startInside=%d cursorInside=%d  (live reported 5622/5622)"
                        startInside cursorInside)

        let path =
            VisibilityGraph.shortestPath
                System.Threading.CancellationToken.None
                VisibilityGraph.NoPreference graph startPt cursorPt
        match path with
        | None ->
            out.WriteLine "REPRODUCED: shortestPath returned None — matches live noPath outcome"
        | Some nodes ->
            out.WriteLine(sprintf
                "NOT reproduced: shortestPath returned %d-node path: %s"
                nodes.Length
                (nodes
                 |> List.map (fun pt -> sprintf "(%d,%d)" pt.X pt.Y)
                 |> String.concat " → "))
