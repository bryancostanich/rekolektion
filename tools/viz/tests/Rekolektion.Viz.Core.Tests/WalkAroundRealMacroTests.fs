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
    member _.``REPRO: vertical wire drn_R top→bottom should jog around foreign features`` () =
        if not (hasMacro ()) then () else

        let doc = loadDoc ()
        let flat = Layout.Flatten.flatten doc
        let nets = Net.LabelFlood.derive doc

        let layer : Obstacles.LayerKey = { Number = 67; DataType = 20 }
        // Both drn_R li1 labels in the macro.
        let startPt : Pt = { X = -393L; Y = 8023L }
        let cursorPt : Pt = { X = -392L; Y = 6225L }
        // Same clearance the canvas dispatch uses: wire_half_width +
        // li1 min spacing. From sky130.yaml: li1.width = 0.170 µm,
        // li1.spacing = 0.170 µm → clearance = 85 + 170 = 255 DBU.
        let clearance = 85L + 170L

        let dxAbs = abs (cursorPt.X - startPt.X)
        let dyAbs = abs (cursorPt.Y - startPt.Y)
        let margin = max (dxAbs + dyAbs) (clearance * 4L)
        let region : Obstacles.Region =
            { XMin = (min startPt.X cursorPt.X) - margin
              YMin = (min startPt.Y cursorPt.Y) - margin
              XMax = (max startPt.X cursorPt.X) + margin
              YMax = (max startPt.Y cursorPt.Y) + margin }

        let key : WalkAround.BuildKey =
            { Layer = layer
              StartNet = "drn_R"
              Clearance = clearance
              FlatPolyRef = flat
              NetMapRef = nets }
        let graph = WalkAround.buildGraphInRegion key region

        out.WriteLine(sprintf "obstacles=%d nodes=%d clearance=%d"
                        graph.Obstacles.Length graph.Nodes.Length clearance)
        // Dump every obstacle bbox so we can see what made the cut.
        // Cross-reference each expanded bbox back to its source
        // polygon so we know what we're routing around.
        let netIdxDump = Obstacles.buildNetIndex nets
        let obstaclePolys =
            Obstacles.obstaclesInRegion layer "drn_R" netIdxDump region flat
        for i in 0 .. graph.Obstacles.Length - 1 do
            let b = graph.Obstacles.[i]
            // The graph's bbox is EXPANDED by clearance. The unexpanded
            // bbox = (b.XMin + clearance, b.YMin + clearance) ..
            //        (b.XMax - clearance, b.YMax - clearance).
            let ux0 = b.XMin + clearance
            let uy0 = b.YMin + clearance
            let ux1 = b.XMax - clearance
            let uy1 = b.YMax - clearance
            // Find the matching polygon.
            let matched =
                obstaclePolys
                |> Array.tryFind (fun fp ->
                    let mutable xMin = System.Int64.MaxValue
                    let mutable yMin = System.Int64.MaxValue
                    let mutable xMax = System.Int64.MinValue
                    let mutable yMax = System.Int64.MinValue
                    for pt in fp.Points do
                        if pt.X < xMin then xMin <- pt.X
                        if pt.X > xMax then xMax <- pt.X
                        if pt.Y < yMin then yMin <- pt.Y
                        if pt.Y > yMax then yMax <- pt.Y
                    xMin = ux0 && yMin = uy0 && xMax = ux1 && yMax = uy1)
            match matched with
            | Some fp ->
                let claimants = Obstacles.claimantsOf netIdxDump fp
                out.WriteLine(
                    sprintf "  obs[%d] expanded=(%d,%d)..(%d,%d) actual=(%d,%d)..(%d,%d) layer=%d/%d net=%A src=%s/%d"
                        i b.XMin b.YMin b.XMax b.YMax
                        ux0 uy0 ux1 uy1
                        fp.Layer fp.DataType claimants
                        fp.SourceStructure fp.SourceIndex)
            | None ->
                out.WriteLine(
                    sprintf "  obs[%d] expanded=(%d,%d)..(%d,%d) [no matching poly!]"
                        i b.XMin b.YMin b.XMax b.YMax)
        // Also: every foreign-net polygon that overlaps the straight
        // path (x=-393 ± 100, y between cursor and start) — these
        // are the polygons the wire visually crosses but the search
        // didn't include as obstacles.
        let netIdx = Obstacles.buildNetIndex nets
        let pathBbox : Obstacles.Region =
            { XMin = -500L
              YMin = min startPt.Y cursorPt.Y
              XMax = -290L
              YMax = max startPt.Y cursorPt.Y }
        out.WriteLine ""
        out.WriteLine "ALL li1 polygons in a tight band along the wire (x ∈ (-700, 0)):"
        for fp in flat do
            if fp.Layer = 67 && fp.DataType = 20 then
                let mutable xMin = System.Int64.MaxValue
                let mutable yMin = System.Int64.MaxValue
                let mutable xMax = System.Int64.MinValue
                let mutable yMax = System.Int64.MinValue
                for pt in fp.Points do
                    if pt.X < xMin then xMin <- pt.X
                    if pt.X > xMax then xMax <- pt.X
                    if pt.Y < yMin then yMin <- pt.Y
                    if pt.Y > yMax then yMax <- pt.Y
                // Filter to polygons that overlap the wire's narrow
                // band horizontally AND its vertical span.
                let inBand =
                    // wire right edge ≈ x=-308. Anything whose x range
                    // is within ±400nm of -308 is close enough to
                    // possibly trigger a spacing violation.
                    xMin <= 100L && xMax >= -700L
                    && yMin <= 8200L && yMax >= 6000L
                if inBand then
                    let claimants = Obstacles.claimantsOf netIdxDump fp
                    let isMine = Set.contains "drn_R" claimants
                    out.WriteLine(
                        sprintf "  bbox=(%d,%d)..(%d,%d) mine=%b claimants=%A src=%s/%d"
                            xMin yMin xMax yMax isMine claimants
                            fp.SourceStructure fp.SourceIndex)
        out.WriteLine ""
        out.WriteLine "Foreign-net polygons inside the wire's actual path:"
        for fp in flat do
            let onRouteLayer = fp.Layer = layer.Number && fp.DataType = layer.DataType
            if onRouteLayer then
                let mutable xMin = System.Int64.MaxValue
                let mutable yMin = System.Int64.MaxValue
                let mutable xMax = System.Int64.MinValue
                let mutable yMax = System.Int64.MinValue
                for pt in fp.Points do
                    if pt.X < xMin then xMin <- pt.X
                    if pt.X > xMax then xMax <- pt.X
                    if pt.Y < yMin then yMin <- pt.Y
                    if pt.Y > yMax then yMax <- pt.Y
                let overlapsPath =
                    not (xMax < pathBbox.XMin || xMin > pathBbox.XMax
                         || yMax < pathBbox.YMin || yMin > pathBbox.YMax)
                if overlapsPath && not (Obstacles.isOurs netIdx "drn_R" fp) then
                    let claimants = Obstacles.claimantsOf netIdx fp
                    out.WriteLine(
                        sprintf "  layer=%d/%d bbox=(%d,%d)..(%d,%d) claimants=%A src=%s/%d"
                            fp.Layer fp.DataType xMin yMin xMax yMax claimants
                            fp.SourceStructure fp.SourceIndex)

        let path = WalkAround.route graph startPt cursorPt
        match path with
        | None -> out.WriteLine "PATH=None"
        | Some pts ->
            out.WriteLine(sprintf "PATH len=%d" pts.Length)
            for p in pts do
                out.WriteLine(sprintf "  (%d, %d)" p.X p.Y)

        // The straight L from (-393, 8023) to (-392, 6225) goes
        // vertically through territory containing the SIGN crossbar
        // at ~y=8000 (visible in the user's screenshot) — that's a
        // foreign-net feature on li1 that triggers a spacing
        // violation. With proper clearance, the search must
        // introduce at least one corner to detour around it.
        match path with
        | None -> failwith "expected a path, got None"
        | Some pts ->
            // The path nodes between start and goal are intermediate
            // turn corners. A straight path produces 2 nodes (start,
            // goal). A path that detours produces more.
            pts.Length |> should be (greaterThan 2)

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
