module Rekolektion.Viz.App.Tests.TabSwitchRatlinesTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.App.Model
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout

// ─────────────────────────────────────────────────────────────────
// Per-tab VisibleRatlines preservation across tab switches.
//
// Bug report (2026-05-30): when a new file is opened via MCP and
// the user switches back to the prior tab, the curated visible-
// ratlines subset gets stomped to "all on" because:
//   (1) LoadComplete overrode Toggle.VisibleRatlines to the new
//       macro's all-nets BEFORE switchActive stashed the outgoing
//       tab's true state.
//   (2) switchActive's saved-quartet didn't include
//       VisibleRatlines, so even if (1) had been fixed there was
//       nothing to restore from.
// ─────────────────────────────────────────────────────────────────

let private stubBackend : Update.ServiceBackend = {
    OpenGds = fun _ -> async { return Error "stub" }
    RunMacro = fun _ _ -> async { return Error 1 }
    DeriveNets = fun _ -> async { return Map.empty }
    SaveMacro = fun _ -> async { return Error "stub" }
    PersistSession = fun _ -> ()
}

let private mkMacro (path: string) (netNames: string seq) : Model.LoadedMacro =
    let doc =
        { emptyDocument with
            Cells = [
                { Name = "TOP"; Meta = None; Elements = []
                  Comments = []; SubFormComments = Map.empty }
            ]
            TopCell = Some "TOP" }
    let nets =
        netNames
        |> Seq.map (fun n ->
            n, ({ Name = n
                  Class = Rekolektion.Viz.Core.Sidecar.Types.Signal
                  Polygons = []
                  SeedPolygons = []
                  DirectLabelPolys = [] }
                : Rekolektion.Viz.Core.Sidecar.Types.NetEntry))
        |> Map.ofSeq
    { Path = path
      Document = doc
      FlatPolygons = [||]
      TopInstances = [||]
      Nets = nets
      Blocks = []
      NetsFromSidecar = true
      SidecarError = None
      OriginalPath = path
      Dirty = false
      UndoStack = []
      RedoStack = []
      LibrarySnapshot = None
      LibraryMtimes = Map.empty }

let private twoTabModel () : Model.Model =
    let macroA = mkMacro "/tmp/A.gds" ["VDD"; "VSS"; "CLK"]
    let macroB = mkMacro "/tmp/B.gds" ["OUT"; "VDD"; "BIAS"]
    { Model.empty with
        OpenMacros = [macroA; macroB]
        ActiveMacroPath = Some macroA.Path }

let private setVisibleRatlines (nets: string seq) (model: Model.Model) : Model.Model =
    { model with
        Toggle =
            Visibility.setVisibleRatlines
                (Set.ofSeq nets) model.Toggle }

// ─── direct SetActiveMacro (tab click / MCP set-active) ──────────

[<Fact>]
let ``SetActiveMacro: switching tabs preserves outgoing VisibleRatlines`` () =
    let model =
        twoTabModel ()
        |> setVisibleRatlines ["VDD"; "CLK"]   // A's curated subset
    // Switch to B.
    let m1, _ = Update.update stubBackend (Msg.SetActiveMacro "/tmp/B.gds") model
    m1.ActiveMacroPath |> should equal (Some "/tmp/B.gds")
    // Curate B's subset.
    let m1' = m1 |> setVisibleRatlines ["BIAS"]
    // Switch back to A.
    let m2, _ = Update.update stubBackend (Msg.SetActiveMacro "/tmp/A.gds") m1'
    m2.ActiveMacroPath |> should equal (Some "/tmp/A.gds")
    // A's curated subset must be restored.
    m2.Toggle.VisibleRatlines |> should equal (Set.ofList ["VDD"; "CLK"])

[<Fact>]
let ``SetActiveMacro: B's saved subset survives multiple A↔B round trips`` () =
    let model =
        twoTabModel ()
        |> setVisibleRatlines ["VDD"]   // A
    let m1, _ = Update.update stubBackend (Msg.SetActiveMacro "/tmp/B.gds") model
    let m1' = m1 |> setVisibleRatlines ["OUT"; "BIAS"]   // B
    let m2, _ = Update.update stubBackend (Msg.SetActiveMacro "/tmp/A.gds") m1'
    let m3, _ = Update.update stubBackend (Msg.SetActiveMacro "/tmp/B.gds") m2
    m3.Toggle.VisibleRatlines |> should equal (Set.ofList ["OUT"; "BIAS"])

[<Fact>]
let ``SetActiveMacro: tab with no saved entry defaults to current Toggle`` () =
    // First-time switch from A to B (B never had ratlines curated).
    // B's Toggle.VisibleRatlines stays at whatever model.Toggle had
    // (no override applied — that's LoadComplete's job).
    let model = twoTabModel ()
    let m1, _ = Update.update stubBackend (Msg.SetActiveMacro "/tmp/B.gds") model
    // Since no saved entry for B, Toggle.VisibleRatlines unchanged.
    m1.Toggle.VisibleRatlines |> should equal (Set.empty : Set<string>)

[<Fact>]
let ``SetActiveMacro: empty visible-ratlines stashes and restores correctly`` () =
    // User on A has ratlines OFF.  Switch to B (anything).  Switch
    // back to A.  A still has ratlines off.
    let model = twoTabModel ()   // A active, no ratlines
    let m1, _ = Update.update stubBackend (Msg.SetActiveMacro "/tmp/B.gds") model
    let m1' = m1 |> setVisibleRatlines ["OUT"]
    let m2, _ = Update.update stubBackend (Msg.SetActiveMacro "/tmp/A.gds") m1'
    m2.Toggle.VisibleRatlines |> should equal (Set.empty : Set<string>)

[<Fact>]
let ``SetActiveMacro: clicking the active tab is a no-op`` () =
    let model =
        twoTabModel ()
        |> setVisibleRatlines ["VDD"]
    let m1, _ = Update.update stubBackend (Msg.SetActiveMacro "/tmp/A.gds") model
    // Same state — no stash, no restore.
    m1.Toggle.VisibleRatlines |> should equal (Set.ofList ["VDD"])
    m1.SavedSelections |> should equal model.SavedSelections

// ─── LoadComplete (MCP-driven new-file open) ─────────────────────

[<Fact>]
let ``LoadComplete: incoming new tab gets carry-on ratlines, outgoing preserves curated`` () =
    let model =
        twoTabModel ()
        |> setVisibleRatlines ["VDD"; "CLK"]   // A curated
    // Simulate opening a brand-new file C (never seen before).
    let macroC = mkMacro "/tmp/C.gds" ["NET1"; "NET2"; "NET3"]
    let m1, _ = Update.update stubBackend (Msg.LoadComplete macroC) model
    m1.ActiveMacroPath |> should equal (Some "/tmp/C.gds")
    // C inherits ratlines-on as "all of C's nets" (carry-on).
    m1.Toggle.VisibleRatlines
    |> should equal (Set.ofList ["NET1"; "NET2"; "NET3"])
    // Switch back to A → A's curated subset comes back.
    let m2, _ = Update.update stubBackend (Msg.SetActiveMacro "/tmp/A.gds") m1
    m2.Toggle.VisibleRatlines |> should equal (Set.ofList ["VDD"; "CLK"])

[<Fact>]
let ``LoadComplete: re-opening a tab restores its saved VisibleRatlines`` () =
    // Open A, curate, switch to B (LoadComplete-driven), switch
    // back to A via LoadComplete-driven reopen.  A's curated subset
    // should win over the carry-on override.
    let model =
        twoTabModel ()
        |> setVisibleRatlines ["VDD"]
    // Switch to B (lightweight).
    let m1, _ = Update.update stubBackend (Msg.SetActiveMacro "/tmp/B.gds") model
    let m1' = m1 |> setVisibleRatlines ["OUT"]
    // Re-open A via LoadComplete (simulates Cmd+R reload or MCP
    // open of an already-open path).  A's saved subset must win.
    let macroA' = mkMacro "/tmp/A.gds" ["VDD"; "VSS"; "CLK"]
    let m2, _ = Update.update stubBackend (Msg.LoadComplete macroA') m1'
    m2.ActiveMacroPath |> should equal (Some "/tmp/A.gds")
    m2.Toggle.VisibleRatlines |> should equal (Set.ofList ["VDD"])

[<Fact>]
let ``LoadComplete: with ratlines off, override does not turn them on`` () =
    let model = twoTabModel ()   // no ratlines anywhere
    let macroC = mkMacro "/tmp/C.gds" ["NET1"; "NET2"]
    let m1, _ = Update.update stubBackend (Msg.LoadComplete macroC) model
    // Carry-on only applies when prior VisibleRatlines was non-empty.
    m1.Toggle.VisibleRatlines |> should equal (Set.empty : Set<string>)
