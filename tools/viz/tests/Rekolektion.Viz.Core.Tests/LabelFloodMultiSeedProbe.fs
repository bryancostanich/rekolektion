module Rekolektion.Viz.Core.Tests.LabelFloodMultiSeedProbe

// Temporary probe — runs LabelFlood on the user's blc_trim_dac.rkt
// macro and prints what drn_R claims. Verifies whether the
// multi-seed fix actually surfaces the wide top-cell channel.

open Xunit
open Rekolektion.Viz.Core

let private rktPath =
    "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cell_designs/bl_clamp/blc_trim_dac.rkt"

[<Fact>]
let ``probe drn_R coverage on blc_trim_dac`` () =
    if not (System.IO.File.Exists rktPath) then () else

    let doc, warnings = Layout.LayoutLoader.load rktPath
    for w in warnings do eprintfn "[probe.warn] %s" w
    let flat = Layout.Flatten.flatten doc
    eprintfn "[probe] cells=%d flatPolys=%d" doc.Cells.Length flat.Length

    let labels = Layout.Flatten.flattenLabels doc
    eprintfn "[probe] labels=%d, drn_R-label count=%d" labels.Length
        (labels |> Array.filter (fun l -> l.Text = "drn_R") |> Array.length)
    for l in labels do
        if l.Text = "drn_R" then
            eprintfn "  drn_R label at (%d,%d) layer=%d kind=%A"
                l.Origin.X l.Origin.Y l.Layer l.Kind

    let nets = Net.LabelFlood.derive doc
    eprintfn "[probe] %d nets derived: %A" nets.Count
        (nets |> Map.toList |> List.map fst)

    match Map.tryFind "drn_R" nets with
    | None ->
        eprintfn "[probe] drn_R: NOT FOUND in derived nets"
    | Some entry ->
        eprintfn "[probe] drn_R: %d polygons, %d seeds"
            entry.Polygons.Length entry.SeedPolygons.Length
        eprintfn "[probe] seeds:"
        for s in entry.SeedPolygons do
            eprintfn "  struct=%s layer=%d/%d index=%d topInst=%A"
                s.Structure s.Layer s.DataType s.Index s.TopInstanceIndex
        eprintfn "[probe] li1 polys near wire path Y~7358:"
        let claimed = System.Collections.Generic.HashSet<_>(entry.Polygons)
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
                if yMin <= 7443L && yMax >= 7273L
                   && xMin <= 8715L && xMax >= 3948L then
                    let pr : Rekolektion.Viz.Core.Sidecar.Types.PolygonRef =
                        { Structure = fp.SourceStructure
                          Layer = fp.Layer
                          DataType = fp.DataType
                          Index = fp.SourceIndex
                          TopInstanceIndex = fp.TopInstanceIndex }
                    let mark = if claimed.Contains pr then "OURS    " else "FOREIGN "
                    eprintfn "  %s bbox=(%d,%d,%d,%d) struct=%s/%d topInst=%A"
                        mark xMin yMin xMax yMax fp.SourceStructure fp.SourceIndex fp.TopInstanceIndex
