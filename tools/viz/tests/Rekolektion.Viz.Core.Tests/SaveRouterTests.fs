module Rekolektion.Viz.Core.Tests.SaveRouterTests

/// Tests for `Rkt.SaveRouter` — per-file save routing for
/// multi-file `.rkt` projects.

open System
open System.IO
open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.Core.Rkt
open Rekolektion.Viz.Core.Rkt.Types

let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), "saverouter_" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try f dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

let private writeRkt (path: string) (text: string) =
    File.WriteAllText(path, text)

// ─── diffByFile ───────────────────────────────────────────────────────

[<Fact>]
let ``diffByFile returns empty when nothing changed`` () =
    withTempDir (fun dir ->
        let p = Path.Combine(dir, "a.rkt")
        writeRkt p "(layout (version 1) (pdk sky130) (cell c))"
        let lib1 = match Reader.loadSingle p with Ok l -> l | Error e -> failwithf "%A" e
        let lib2 = match Reader.loadSingle p with Ok l -> l | Error e -> failwithf "%A" e
        SaveRouter.diffByFile lib1 lib2 |> Map.count |> should equal 0)

[<Fact>]
let ``diffByFile picks up an in-memory edit to one cell`` () =
    withTempDir (fun dir ->
        let p = Path.Combine(dir, "a.rkt")
        writeRkt p "(layout (version 1) (pdk sky130) (cell c))"
        let lib1 = match Reader.loadSingle p with Ok l -> l | Error e -> failwithf "%A" e
        // Simulate an in-App edit: replace the cell with one having
        // a new element.
        let edited =
            let ld = Map.find (Path.GetFullPath p) lib1.Documents
            let cell = { ld.Ast.Cells.[0] with
                            Comments = [ "in-app annotation" ]
                            SubFormComments = Map.empty }
            let newAst = { ld.Ast with Cells = [ cell ] }
            { lib1 with
                Documents = Map.add (Path.GetFullPath p)
                                { ld with Ast = newAst } lib1.Documents }
        let diff = SaveRouter.diffByFile lib1 edited
        diff |> Map.count |> should equal 1)

[<Fact>]
let ``diffByFile routes edits to each cell's source file`` () =
    withTempDir (fun dir ->
        let prim = Path.Combine(dir, "prim.rkt")
        let macro = Path.Combine(dir, "macro.rkt")
        writeRkt prim "(layout (version 1) (pdk sky130) (cell shared))"
        writeRkt macro
            ("(layout (version 1) (pdk sky130)\n"
             + "  (import \"prim.rkt\")\n"
             + "  (cell parent (sref (cell shared) (origin 0 0))))\n")
        let lib1 = match Reader.loadSingle macro with Ok l -> l | Error e -> failwithf "%A" e
        let primKey = Path.GetFullPath prim
        // Edit ONLY the shared cell (sourced from prim.rkt).
        let edited =
            let ld = Map.find primKey lib1.Documents
            let cell = { ld.Ast.Cells.[0] with
                            Comments = [ "edited shared cell" ]
                            SubFormComments = Map.empty }
            let newAst = { ld.Ast with Cells = [ cell ] }
            { lib1 with
                Documents = Map.add primKey { ld with Ast = newAst } lib1.Documents }
        let diff = SaveRouter.diffByFile lib1 edited
        // Only prim.rkt should appear — macro.rkt is unchanged.
        diff |> Map.count |> should equal 1
        diff |> Map.containsKey primKey |> should equal true)

// ─── saveAll ──────────────────────────────────────────────────────────

[<Fact>]
let ``saveAll writes per-file canonical .rkt to disk`` () =
    withTempDir (fun dir ->
        let p = Path.Combine(dir, "a.rkt")
        let doc =
            { emptyDocument with
                Cells = [ { Name = "c"; Meta = None; Elements = []
                            Comments = []; SubFormComments = Map.empty } ]
                TopCell = Some "c" }
        let diff = Map.ofList [ p, doc ]
        match SaveRouter.saveAll diff with
        | Error e -> failwithf "saveAll failed: %A" e
        | Ok () ->
            File.Exists p |> should equal true
            // File parses back to an equivalent AST.
            match Reader.readFile p with
            | Ok (_, ast) ->
                ast.Cells |> List.length |> should equal 1
                ast.Cells.[0].Name |> should equal "c"
            | Error e -> failwithf "reread failed: %A" e)

[<Fact>]
let ``saveAll on no diffs is a no-op`` () =
    withTempDir (fun dir ->
        match SaveRouter.saveAll Map.empty with
        | Error e -> failwithf "should be Ok: %A" e
        | Ok () -> ())

// ─── mtime conflict detection ─────────────────────────────────────────

[<Fact>]
let ``detectMtimeConflicts flags an externally-touched file`` () =
    withTempDir (fun dir ->
        let p = Path.Combine(dir, "a.rkt")
        writeRkt p "(layout (version 1) (pdk sky130) (cell c))"
        let loadedMtimes =
            Map.ofList [ p, File.GetLastWriteTimeUtc p ]
        // Sleep + touch.
        System.Threading.Thread.Sleep 50
        writeRkt p "(layout (version 1) (pdk sky130) (cell c) (cell extra))"
        let doc =
            { emptyDocument with
                Cells = [ { Name = "c"; Meta = None; Elements = []
                            Comments = []; SubFormComments = Map.empty } ] }
        let diff = Map.ofList [ p, doc ]
        let conflicts = SaveRouter.detectMtimeConflicts diff loadedMtimes
        conflicts
        |> List.exists (function
            | SaveRouter.MtimeConflict (path, _, _) -> path = p
            | _ -> false)
        |> should equal true)

[<Fact>]
let ``detectMtimeConflicts is silent when on-disk matches loaded mtime`` () =
    withTempDir (fun dir ->
        let p = Path.Combine(dir, "a.rkt")
        writeRkt p "(layout (version 1) (pdk sky130) (cell c))"
        let loadedMtimes =
            Map.ofList [ p, File.GetLastWriteTimeUtc p ]
        let doc =
            { emptyDocument with
                Cells = [ { Name = "c"; Meta = None; Elements = []
                            Comments = []; SubFormComments = Map.empty } ] }
        let diff = Map.ofList [ p, doc ]
        SaveRouter.detectMtimeConflicts diff loadedMtimes |> List.length |> should equal 0)

// ─── projectIntoLibrary ───────────────────────────────────────────────

[<Fact>]
let ``projectIntoLibrary routes an edited imported cell back to its source file`` () =
    withTempDir (fun dir ->
        let prim = Path.Combine(dir, "prim.rkt")
        let macro = Path.Combine(dir, "macro.rkt")
        writeRkt prim "(layout (version 1) (pdk sky130) (cell shared))"
        writeRkt macro
            ("(layout (version 1) (pdk sky130)\n"
             + "  (import \"prim.rkt\")\n"
             + "  (cell parent (sref (cell shared) (origin 0 0))))\n")
        let lib =
            match Reader.loadSingle macro with
            | Ok l -> l
            | Error e -> failwithf "%A" e
        let primKey = Path.GetFullPath prim
        let macroKey = Path.GetFullPath macro
        // Build the merged Document the App would carry — start from
        // the root then append the imported file's cells (mirrors
        // LayoutLoader.load).
        let merged =
            let rootDoc = (Map.find macroKey lib.Documents).Ast
            let primDoc = (Map.find primKey lib.Documents).Ast
            { rootDoc with Cells = rootDoc.Cells @ primDoc.Cells }
        // Simulate an in-App edit to the `shared` cell.
        let editedMerged =
            { merged with
                Cells =
                    merged.Cells
                    |> List.map (fun c ->
                        if c.Name = "shared"
                        then { c with Comments = [ "edited in-App" ] }
                        else c) }
        let projected =
            SaveRouter.projectIntoLibrary lib editedMerged macroKey Map.empty
        // The edit lands in prim.rkt's Document, not macro.rkt's.
        let projPrim = Map.find primKey projected.Documents
        let projMacro = Map.find macroKey projected.Documents
        projPrim.Ast.Cells |> List.length |> should equal 1
        projPrim.Ast.Cells.[0].Comments |> should equal [ "edited in-App" ]
        projMacro.Ast.Cells |> List.length |> should equal 1
        projMacro.Ast.Cells.[0].Name |> should equal "parent"
        // diffByFile against the unchanged original picks up exactly
        // one changed file (prim.rkt), not the root.
        let diff = SaveRouter.diffByFile lib projected
        diff |> Map.count |> should equal 1
        diff |> Map.containsKey primKey |> should equal true
        diff |> Map.containsKey macroKey |> should equal false)

[<Fact>]
let ``projectIntoLibrary orphans an in-App-synthesized cell into the root`` () =
    withTempDir (fun dir ->
        let prim = Path.Combine(dir, "prim.rkt")
        let macro = Path.Combine(dir, "macro.rkt")
        writeRkt prim "(layout (version 1) (pdk sky130) (cell shared))"
        writeRkt macro
            ("(layout (version 1) (pdk sky130)\n"
             + "  (import \"prim.rkt\")\n"
             + "  (cell parent))\n")
        let lib = match Reader.loadSingle macro with Ok l -> l | Error e -> failwithf "%A" e
        let macroKey = Path.GetFullPath macro
        let merged =
            let rootDoc = (Map.find macroKey lib.Documents).Ast
            let primDoc = (Map.find (Path.GetFullPath prim) lib.Documents).Ast
            { rootDoc with Cells = rootDoc.Cells @ primDoc.Cells }
        // Add a new cell with no CellIndex mapping.
        let newCell : Cell = {
            Name = "fresh"; Meta = None
            Elements = []; Comments = []; SubFormComments = Map.empty }
        let edited = { merged with Cells = merged.Cells @ [ newCell ] }
        let projected = SaveRouter.projectIntoLibrary lib edited macroKey Map.empty
        let projMacro = Map.find macroKey projected.Documents
        // The orphan landed in the root file.
        projMacro.Ast.Cells |> List.exists (fun c -> c.Name = "fresh") |> should equal true)

[<Fact>]
let ``projectIntoLibrary routes orphans via orphanAssignments override`` () =
    withTempDir (fun dir ->
        let prim = Path.Combine(dir, "prim.rkt")
        let macro = Path.Combine(dir, "macro.rkt")
        writeRkt prim "(layout (version 1) (pdk sky130) (cell shared))"
        writeRkt macro
            ("(layout (version 1) (pdk sky130)\n"
             + "  (import \"prim.rkt\")\n"
             + "  (cell parent))\n")
        let lib = match Reader.loadSingle macro with Ok l -> l | Error e -> failwithf "%A" e
        let macroKey = Path.GetFullPath macro
        let primKey = Path.GetFullPath prim
        let merged =
            let rootDoc = (Map.find macroKey lib.Documents).Ast
            let primDoc = (Map.find primKey lib.Documents).Ast
            { rootDoc with Cells = rootDoc.Cells @ primDoc.Cells }
        // Add an orphan cell — user assigns it to prim.rkt.
        let newCell : Cell = {
            Name = "fresh"; Meta = None
            Elements = []; Comments = []; SubFormComments = Map.empty }
        let edited = { merged with Cells = merged.Cells @ [ newCell ] }
        let assignments = Map.ofList [ "fresh", primKey ]
        let projected = SaveRouter.projectIntoLibrary lib edited macroKey assignments
        let projPrim = Map.find primKey projected.Documents
        let projMacro = Map.find macroKey projected.Documents
        // The orphan lands in prim.rkt (not in macro.rkt).
        projPrim.Ast.Cells
        |> List.exists (fun c -> c.Name = "fresh")
        |> should equal true
        projMacro.Ast.Cells
        |> List.exists (fun c -> c.Name = "fresh")
        |> should equal false)

[<Fact>]
let ``projectIntoLibrary ignores orphanAssignment to a non-library path`` () =
    withTempDir (fun dir ->
        let macro = Path.Combine(dir, "macro.rkt")
        writeRkt macro "(layout (version 1) (pdk sky130) (cell parent))"
        let lib = match Reader.loadSingle macro with Ok l -> l | Error e -> failwithf "%A" e
        let macroKey = Path.GetFullPath macro
        let merged = (Map.find macroKey lib.Documents).Ast
        let newCell : Cell = {
            Name = "fresh"; Meta = None
            Elements = []; Comments = []; SubFormComments = Map.empty }
        let edited = { merged with Cells = merged.Cells @ [ newCell ] }
        // User points the orphan at a file that isn't in the library.
        // Expectation: silently route to root rather than create a
        // new file (see SaveRouter docs).
        let assignments = Map.ofList [ "fresh", "/does/not/exist.rkt" ]
        let projected = SaveRouter.projectIntoLibrary lib edited macroKey assignments
        let projMacro = Map.find macroKey projected.Documents
        projMacro.Ast.Cells
        |> List.exists (fun c -> c.Name = "fresh")
        |> should equal true)

// ─── orphan cells ─────────────────────────────────────────────────────

[<Fact>]
let ``orphanCells surfaces names not in CellIndex`` () =
    withTempDir (fun dir ->
        let p = Path.Combine(dir, "a.rkt")
        writeRkt p "(layout (version 1) (pdk sky130) (cell existing))"
        let lib = match Reader.loadSingle p with Ok l -> l | Error e -> failwithf "%A" e
        // Synthesize a new cell in-memory without updating CellIndex.
        let ld = Map.find (Path.GetFullPath p) lib.Documents
        let newCell : Cell = {
            Name = "synthesized"; Meta = None; Elements = []; Comments = []
            SubFormComments = Map.empty
        }
        let newAst = { ld.Ast with Cells = ld.Ast.Cells @ [ newCell ] }
        let edited =
            { lib with
                Documents =
                    Map.add (Path.GetFullPath p) { ld with Ast = newAst } lib.Documents }
        let orphans = SaveRouter.orphanCells edited
        orphans |> should contain "synthesized")
