#r "/Users/bryancostanich/git_repos/bryan_costanich/rekolektion/tools/viz/src/Rekolektion.Viz.Core/bin/Debug/net10.0/Rekolektion.Viz.Core.dll"

open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout

let path =
    match System.Environment.GetCommandLineArgs() |> Array.tryLast with
    | Some p when System.IO.File.Exists p -> p
    | _ ->
        eprintfn "usage: dotnet fsi run_drc.fsx <path-to-rkt>"
        exit 1

eprintfn "[drc] loading %s" path
let doc, warnings = LayoutLoader.load path
for w in warnings do eprintfn "[drc] load warning: %s" w

// Build full flat + top-cell-direct, run BOTH passes the canvas
// runs (topDirect through checkWithToggles, all per-instance polys
// through checkInterInstance), then merge.
let flat = Flatten.flatten doc
let topDirect = Flatten.flattenTopCellDirect doc
let perInstance =
    Instances.enumerate doc
    |> Array.map (fun inst -> inst.Index, Flatten.flattenInstance doc inst.Index)
    |> Map.ofArray

eprintfn "[drc] flat=%d topDirect=%d instances=%d"
    flat.Length topDirect.Length (Map.count perInstance)

// Implant tags computed from the FULL flat (so a top-cell-direct
// licon sees diff polys from inside SRefs). For the top-direct
// check, build a parallel tag array keyed on bbox identity.
let tagsFull = Drc.Implant.tagAll flat
let bboxKey (p: Flatten.FlatPolygon) =
    let mutable xMin = System.Int64.MaxValue
    let mutable yMin = System.Int64.MaxValue
    let mutable xMax = System.Int64.MinValue
    let mutable yMax = System.Int64.MinValue
    for pt in p.Points do
        if pt.X < xMin then xMin <- pt.X
        if pt.X > xMax then xMax <- pt.X
        if pt.Y < yMin then yMin <- pt.Y
        if pt.Y > yMax then yMax <- pt.Y
    p.Layer, p.DataType, xMin, yMin, xMax, yMax
let tagByKey =
    let d =
        System.Collections.Generic.Dictionary<
            int * int * int64 * int64 * int64 * int64,
            Drc.Implant.ImplantTags>()
    for i in 0 .. flat.Length - 1 do
        d.[bboxKey flat.[i]] <- tagsFull.[i]
    d
let tagsFor (arr: Flatten.FlatPolygon array) =
    arr |> Array.map (fun p ->
        match tagByKey.TryGetValue (bboxKey p) with
        | true, t -> t
        | _ -> Drc.Implant.emptyTags)

let vTop = Drc.Check.checkWithToggles doc.Units topDirect (tagsFor topDirect) Set.empty
let vInter = Drc.Check.checkInterInstance doc.Units perInstance

eprintfn "[drc] topDirect violations=%d  inter-instance violations=%d"
    vTop.Length vInter.Length

let umPerDbu = float doc.Units.DbuNm * 1.0e-3
let fmtBb ((x1, y1, x2, y2): int64 * int64 * int64 * int64) =
    sprintf "[%.3f, %.3f]..[%.3f, %.3f] µm"
        (float x1 * umPerDbu) (float y1 * umPerDbu)
        (float x2 * umPerDbu) (float y2 * umPerDbu)

let report (label: string) (vs: Drc.Check.Violation array) =
    printfn ""
    printfn "=== %s — %d violations ===" label vs.Length
    let byRule =
        vs
        |> Array.groupBy (fun v -> v.Rule)
        |> Array.sortByDescending (fun (_, arr) -> arr.Length)
    for (rule, arr) in byRule do
        printfn "  %-15s  ×%d" rule arr.Length
    printfn ""
    let n = min 30 vs.Length
    if n < vs.Length then
        printfn "  first %d of %d:" n vs.Length
    for v in vs |> Array.truncate n do
        let limit = float v.LimitDbu * umPerDbu
        let meas  = float v.MeasuredDbu * umPerDbu
        printfn "    %s  measured=%.3f µm  limit=%.3f µm  A=%s%s"
            v.Rule meas limit
            (fmtBb v.BboxA)
            (match v.BboxB with
             | Some bb -> sprintf "  B=%s" (fmtBb bb)
             | None -> "")

report "TOP-CELL DIRECT (the new full-rule pass)" vTop
report "INTER-INSTANCE SPACING (existing pass)" vInter

let vFull = Drc.Check.checkWithToggles doc.Units flat tagsFull Set.empty
report "FULL FLAT (every rule × every poly, no top-direct restriction)" vFull

// Filter to the bbox the other thread quoted: x ∈ [30580, 34210]
// nm, y ∈ [-5920, -1840] nm — the box around the three new
// discharge cells where Magic reported 16 errors.
let inBox ((x1, y1, x2, y2): int64 * int64 * int64 * int64) =
    let cx = (x1 + x2) / 2L
    let cy = (y1 + y2) / 2L
    cx >= 30580L && cx <= 34210L && cy >= -5920L && cy <= -1840L
let vBox =
    vFull |> Array.filter (fun v ->
        inBox v.BboxA
        || (match v.BboxB with Some bb -> inBox bb | None -> false))
report "FILTERED TO THE THREE-DISCHARGE-CELL BBOX (matches Magic scope)" vBox
