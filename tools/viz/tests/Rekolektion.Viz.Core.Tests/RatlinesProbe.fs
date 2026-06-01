module Rekolektion.Viz.Core.Tests.RatlinesProbe

open Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.Core.Net

let private targetCell =
    "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cell_designs/dac/opamp_lowv_buffer/opamp_lowv_buffer.rkt"

[<Fact>]
let ``PROBE opamp_lowv_buffer ratlines.compute result`` () =
    if not (System.IO.File.Exists targetCell) then () else
    let doc, _ = LayoutLoader.load targetCell
    let flat = Flatten.flatten doc
    let routes = Ratlines.compute doc flat
    let dump =
        routes
        |> Array.map (fun r ->
            sprintf "  net=%s pins=%d edges=%d"
                r.Name r.Pins.Length r.Mst.Length)
        |> String.concat "\n"
    Assert.Fail(
        sprintf "flatPolys=%d ratline routes=%d\n%s"
            flat.Length routes.Length dump)

[<Fact>]
let ``PROBE opamp_lowv_buffer flat labels`` () =
    if not (System.IO.File.Exists targetCell) then () else
    let doc, _ = LayoutLoader.load targetCell
    let labels = Flatten.flattenLabels doc
    let byKindNet =
        labels
        |> Array.groupBy (fun l -> l.Kind, l.Text)
        |> Array.sortByDescending (fun (_, ls) -> ls.Length)
    let dump =
        byKindNet
        |> Array.truncate 20
        |> Array.map (fun ((kind, text), ls) ->
            sprintf "  %A %s × %d (layer=%d)" kind text ls.Length (Array.head ls).Layer)
        |> String.concat "\n"
    Assert.Fail(
        sprintf "labels=%d (showing top 20 by kind+text):\n%s"
            labels.Length dump)
