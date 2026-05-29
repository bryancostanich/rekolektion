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

// ─── plan must root the projection at OriginalPath, not mc.Path ─────────
//
// markDirty flips mc.Path from foo.rkt to foo_edited.rkt on first
// edit.  The snapshot's CellIndex still maps cells to foo.rkt, so
// using mc.Path as rootKey adds foo_edited.rkt to perPath as an
// empty bucket — diffByFile then flags it as a new file and the
// writer dutifully writes a 67-byte header-only stub there next to
// the real save.  Verified on style_b_v2.rkt:
// style_b_v2_edited.rkt and style_b_v2_edited_2.rkt both showed up
// as empty files; this test pins the fix.

[<Fact>]
let ``plan keys diffs by OriginalPath even after markDirty renamed mc.Path`` () =
    withTempDir (fun dir ->
        let p = Path.Combine(dir, "src.rkt")
        writeRkt p "(layout (version 1) (pdk sky130) (cell only))"
        let mc = loadMacro p
        // User edits a cell — mark dirty would normally retarget Path
        // here; simulate that explicitly so the test owns the state.
        let edited =
            mc.Document.Cells
            |> List.map (fun c ->
                if c.Name = "only" then { c with Comments = [ "edit" ] }
                else c)
        let editedDoc = { mc.Document with Cells = edited }
        let renamed =
            { mc with
                Document = editedDoc
                Path = Path.Combine(dir, "src_edited.rkt")  // post-markDirty
                Dirty = true }
        match RoutedSave.plan renamed Map.empty with
        | None -> failwith "expected Some plan (LibrarySnapshot is set)"
        | Some plan ->
            // The diff must NOT contain src_edited.rkt — the cell
            // belongs at src.rkt per CellIndex.
            plan.Diffs
            |> Map.containsKey (Path.Combine(dir, "src_edited.rkt"))
            |> should equal false
            // It MUST contain src.rkt (where the cell really lives).
            plan.Diffs
            |> Map.containsKey (Path.GetFullPath p)
            |> should equal true)

// ─── saveAll refuses to materialise empty-cells documents at new paths ──

[<Fact>]
let ``saveAll skips writing an empty-cells doc to a path that doesn't exist`` () =
    withTempDir (fun dir ->
        let ghost = Path.Combine(dir, "ghost.rkt")
        let emptyDoc =
            { Types.emptyDocument with
                Pdk = "sky130"
                Cells = [] }
        let diffs = Map.ofList [ ghost, emptyDoc ]
        match SaveRouter.saveAll diffs with
        | Ok () ->
            File.Exists ghost |> should equal false
        | Error e -> failwithf "saveAll failed: %A" e)

[<Fact>]
let ``saveAll DOES write an empty-cells doc when the path already exists`` () =
    withTempDir (fun dir ->
        let real = Path.Combine(dir, "real.rkt")
        // Pre-existing file the user is legitimately clearing.
        writeRkt real "(layout (version 1) (pdk sky130) (cell stale))"
        let emptyDoc =
            { Types.emptyDocument with
                Pdk = "sky130"
                Cells = [] }
        let diffs = Map.ofList [ real, emptyDoc ]
        match SaveRouter.saveAll diffs with
        | Ok () ->
            File.Exists real |> should equal true
            (File.ReadAllText real).Contains "(cell stale)" |> should equal false
        | Error e -> failwithf "saveAll failed: %A" e)
