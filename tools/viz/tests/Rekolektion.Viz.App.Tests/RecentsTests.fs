module Rekolektion.Viz.App.Tests.RecentsTests

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.App.Services

// The Recents module persists to a fixed path inside ~/.rekolektion-viz.
// We can't override that path without parameterising the module, so we
// snapshot the existing file (if any), exercise save/load, and restore.
// Tests run sequentially within a class via xunit's per-collection
// serialization — the [<Collection>] attribute keeps them off the
// parallel runner so they can't trample each other.

[<Collection("Recents")>]
type RecentsTests () =

    let recentsFile =
        Path.Combine(
            Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
            ".rekolektion-viz",
            "recents.txt")

    let snapshot () : string option =
        if File.Exists recentsFile then
            Some (File.ReadAllText recentsFile)
        else None

    let restore (s: string option) =
        match s with
        | Some text -> File.WriteAllText(recentsFile, text)
        | None ->
            if File.Exists recentsFile then File.Delete recentsFile

    [<Fact>]
    member _.``save then load round-trips the list`` () =
        let snap = snapshot ()
        try
            let paths = ["/a/b.gds"; "/c/d.mag"]
            Recents.save paths
            Recents.load () |> should equal paths
        finally
            restore snap

    [<Fact>]
    member _.``save truncates to 10`` () =
        let snap = snapshot ()
        try
            let paths = [ for i in 1 .. 15 -> sprintf "/file%d.gds" i ]
            Recents.save paths
            let loaded = Recents.load ()
            loaded.Length |> should equal 10
            loaded |> List.head |> should equal "/file1.gds"
            loaded |> List.last |> should equal "/file10.gds"
        finally
            restore snap

    [<Fact>]
    member _.``load on missing file returns empty`` () =
        let snap = snapshot ()
        try
            if File.Exists recentsFile then File.Delete recentsFile
            Recents.load () |> should equal ([] : string list)
        finally
            restore snap

    [<Fact>]
    member _.``load skips blank lines`` () =
        let snap = snapshot ()
        try
            let dir = Path.GetDirectoryName recentsFile
            if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
            File.WriteAllLines(recentsFile, [| "/a.gds"; ""; "   "; "/b.gds" |])
            Recents.load () |> should equal ["/a.gds"; "/b.gds"]
        finally
            restore snap
