module Rekolektion.Viz.Core.Tests.MagWriterTests

open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout

let private fixturePath =
    "/Users/bryancostanich/git_repos/bryan_costanich/khalkulo/source/cim/cim_reram_4t2r_wip.mag"

let private withTmp (f: string -> unit) =
    let p = Path.Combine(Path.GetTempPath(),
                         "viz-v2-rt-" + System.Guid.NewGuid().ToString("N") + ".mag")
    try f p
    finally if File.Exists p then File.Delete p

[<Fact>]
let ``round-trip cim_reram_4t2r_wip with no edits is byte-identical`` () =
    if not (System.IO.File.Exists fixturePath) then ()
    else
        let lib, _ = Layout.LayoutLoader.load fixturePath
        withTmp (fun tmpPath ->
            Mag.Writer.writeUpdated fixturePath lib tmpPath
            let original = File.ReadAllText fixturePath
            let written  = File.ReadAllText tmpPath
            written |> should equal original)

[<Fact>]
let ``writer rewrites the transform line for a moved instance`` () =
    if not (System.IO.File.Exists fixturePath) then ()
    else
        let lib, _ = Layout.LayoutLoader.load fixturePath
        let instances = Layout.Instances.enumerate lib
        instances.Length |> should equal 2
        let pickIdx = instances.[0].Index
        let movedOrig =
            instances |> Array.find (fun i -> i.Index = pickIdx)
        let lib' =
            Layout.Instances.translateSelection
                lib (Set.singleton pickIdx) 200L 0L
        withTmp (fun tmpPath ->
            Mag.Writer.writeUpdated fixturePath lib' tmpPath
            // Re-load the written file and confirm the move
            // persisted into the transform's translation tokens.
            let lib2, _ = Layout.LayoutLoader.load tmpPath
            let inst2 = Layout.Instances.enumerate lib2
            let moved =
                inst2 |> Array.find (fun i -> i.Index = pickIdx)
            moved.Sref.Origin.X |> should equal (movedOrig.Sref.Origin.X + 200L)
            moved.Sref.Origin.Y |> should equal movedOrig.Sref.Origin.Y)

[<Fact>]
let ``writer leaves comments, timestamps, and box lines verbatim`` () =
    if not (System.IO.File.Exists fixturePath) then ()
    else
        let lib, _ = Layout.LayoutLoader.load fixturePath
        let instances = Layout.Instances.enumerate lib
        let pickIdx = instances.[0].Index
        let lib' =
            Layout.Instances.translateSelection
                lib (Set.singleton pickIdx) 200L 0L
        withTmp (fun tmpPath ->
            Mag.Writer.writeUpdated fixturePath lib' tmpPath
            let original = File.ReadAllText fixturePath
            let written  = File.ReadAllText tmpPath
            // Every non-`transform` line should match the source
            // exactly. Diff line-by-line.
            let origLines = original.Split('\n')
            let newLines  = written.Split('\n')
            origLines.Length |> should equal newLines.Length
            for i in 0 .. origLines.Length - 1 do
                let ot = origLines.[i].TrimStart()
                if not (ot.StartsWith "transform ") then
                    newLines.[i] |> should equal origLines.[i])
