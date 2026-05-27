module Rekolektion.Viz.App.Tests.RoutedSaveTests

/// Direct unit tests for `Services.RoutedSave.reloadAndReapply` —
/// the three-way merge that backs the ConflictDialog's
/// Reload-and-Reapply branch.

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.App.Model.Model
open Rekolektion.Viz.App.Services

let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), "rs_reapply_" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try f dir
    finally try Directory.Delete(dir, true) with _ -> ()

let private writeRkt (path: string) (text: string) = File.WriteAllText(path, text)

let private loadMacro (path: string) : LoadedMacro =
    let doc, _warnings = Layout.LayoutLoader.load path
    let lib =
        match Reader.loadSingle path with
        | Ok l -> Some l
        | Error _ -> None
    let mtimes =
        match lib with
        | Some l ->
            l.Documents
            |> Map.toSeq
            |> Seq.map (fun (p, _) -> p, File.GetLastWriteTimeUtc p)
            |> Map.ofSeq
        | None -> Map.empty
    {
        Path = path
        Document = doc
        FlatPolygons = Layout.Flatten.flatten doc
        TopInstances = Layout.Instances.enumerate doc
        Nets = Map.empty
        Blocks = Layout.Hierarchy.detect doc
        NetsFromSidecar = false
        SidecarError = None
        OriginalPath = path
        Dirty = false
        UndoStack = []
        RedoStack = []
        LibrarySnapshot = lib
        LibraryMtimes = mtimes
    }

[<Fact>]
let ``reloadAndReapply re-applies a user edit when the on-disk version is unchanged`` () =
    withTempDir (fun dir ->
        let p = Path.Combine(dir, "macro.rkt")
        writeRkt p "(layout (version 1) (pdk sky130) (cell parent))"
        let mc = loadMacro p
        // User edits the `parent` cell in-memory (add a comment).
        let editedCells =
            mc.Document.Cells
            |> List.map (fun c ->
                if c.Name = "parent" then { c with Comments = [ "user-edit" ] }
                else c)
        let edited = { mc with Document = { mc.Document with Cells = editedCells } }
        match RoutedSave.reloadAndReapply edited with
        | Error e -> failwithf "reapply failed: %s" e
        | Ok r ->
            r.ConflictingCells |> should equal ([] : string list)
            let merged = r.Macro.Document
            merged.Cells
            |> List.find (fun c -> c.Name = "parent")
            |> fun c -> c.Comments |> should equal [ "user-edit" ])

[<Fact>]
let ``reloadAndReapply surfaces a per-cell conflict when both sides edited`` () =
    withTempDir (fun dir ->
        let p = Path.Combine(dir, "macro.rkt")
        writeRkt p "(layout (version 1) (pdk sky130) (cell parent))"
        let mc = loadMacro p
        // User edits in-memory.
        let userEdited =
            mc.Document.Cells
            |> List.map (fun c ->
                if c.Name = "parent" then { c with Comments = [ "user" ] }
                else c)
        let editedMc = { mc with Document = { mc.Document with Cells = userEdited } }
        // External edit: rewrite file with a different comment on `parent`.
        System.Threading.Thread.Sleep 50
        writeRkt p "; on-disk header\n(layout (version 1) (pdk sky130)\n  ; on-disk\n  (cell parent))\n"
        match RoutedSave.reloadAndReapply editedMc with
        | Error e -> failwithf "reapply failed: %s" e
        | Ok r ->
            // The cell appears in the conflict list because both
            // sides touched it.
            r.ConflictingCells |> should contain "parent"
            // v1 policy: user's edit wins on conflict.
            r.Macro.Document.Cells
            |> List.find (fun c -> c.Name = "parent")
            |> fun c -> c.Comments |> should equal [ "user" ])

[<Fact>]
let ``reloadAndReapply errors out when LibrarySnapshot is None`` () =
    withTempDir (fun dir ->
        let p = Path.Combine(dir, "macro.rkt")
        writeRkt p "(layout (version 1) (pdk sky130) (cell c))"
        let mc = { loadMacro p with LibrarySnapshot = None }
        match RoutedSave.reloadAndReapply mc with
        | Error msg -> msg |> should haveSubstring "no library snapshot"
        | Ok _ -> failwith "expected error")
