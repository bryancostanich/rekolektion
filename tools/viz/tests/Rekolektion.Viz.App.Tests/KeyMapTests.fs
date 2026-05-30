module Rekolektion.Viz.App.Tests.KeyMapTests

open Xunit
open FsUnit.Xunit
open Avalonia.Input
open Rekolektion.Viz.App
open Rekolektion.Viz.App.Model

// ─────────────────────────────────────────────────────────────────
// KeyMap.dispatchFor — pure window-level keymap.
//
// Match arms are order-sensitive. The crucial invariant under
// test: in Tighten mode the layer-focus keys (` 1 2 3 4 0) must
// NOT fire SetActiveLayer — number keys must reach the
// CommitTighten arm.
// ─────────────────────────────────────────────────────────────────

let private noModel : Model.Model option = None

let private modelWith (mutate: Model.Model -> Model.Model) : Model.Model option =
    Some (mutate Model.empty)

let private tightenOn : Model.Model option =
    modelWith (fun m -> { m with TightenMode = true })

let private tightenOff : Model.Model option =
    modelWith (fun m -> { m with TightenMode = false })

// ─── Layer-focus keys (Tighten mode OFF) ────────────────────────

[<Fact>]
let ``D1 without Tighten → SetActiveLayer met1`` () =
    KeyMap.dispatchFor tightenOff Key.D1 KeyModifiers.None
    |> should equal (Some (Msg.SetActiveLayer (Some (68, 20))))

[<Fact>]
let ``D2 without Tighten → SetActiveLayer met2`` () =
    KeyMap.dispatchFor tightenOff Key.D2 KeyModifiers.None
    |> should equal (Some (Msg.SetActiveLayer (Some (69, 20))))

[<Fact>]
let ``D3 without Tighten → SetActiveLayer met3`` () =
    KeyMap.dispatchFor tightenOff Key.D3 KeyModifiers.None
    |> should equal (Some (Msg.SetActiveLayer (Some (70, 20))))

[<Fact>]
let ``D4 without Tighten → SetActiveLayer met4`` () =
    KeyMap.dispatchFor tightenOff Key.D4 KeyModifiers.None
    |> should equal (Some (Msg.SetActiveLayer (Some (71, 20))))

[<Fact>]
let ``OemTilde without Tighten → SetActiveLayer li1`` () =
    KeyMap.dispatchFor tightenOff Key.OemTilde KeyModifiers.None
    |> should equal (Some (Msg.SetActiveLayer (Some (67, 20))))

[<Fact>]
let ``D0 without Tighten → SetActiveLayer None (clear focus)`` () =
    KeyMap.dispatchFor tightenOff Key.D0 KeyModifiers.None
    |> should equal (Some (Msg.SetActiveLayer None))

[<Fact>]
let ``NumPad0 without Tighten → SetActiveLayer None`` () =
    KeyMap.dispatchFor tightenOff Key.NumPad0 KeyModifiers.None
    |> should equal (Some (Msg.SetActiveLayer None))

[<Fact>]
let ``D1 with None model → SetActiveLayer met1 (default not-in-tighten)`` () =
    KeyMap.dispatchFor noModel Key.D1 KeyModifiers.None
    |> should equal (Some (Msg.SetActiveLayer (Some (68, 20))))

// ─── Number keys IN Tighten mode → CommitTighten ────────────────

[<Fact>]
let ``D1 IN Tighten → CommitTighten 1 (NOT layer focus)`` () =
    KeyMap.dispatchFor tightenOn Key.D1 KeyModifiers.None
    |> should equal (Some (Msg.CommitTighten 1))

[<Fact>]
let ``D2 IN Tighten → CommitTighten 2`` () =
    KeyMap.dispatchFor tightenOn Key.D2 KeyModifiers.None
    |> should equal (Some (Msg.CommitTighten 2))

[<Fact>]
let ``D3 IN Tighten → CommitTighten 3`` () =
    KeyMap.dispatchFor tightenOn Key.D3 KeyModifiers.None
    |> should equal (Some (Msg.CommitTighten 3))

[<Fact>]
let ``D4 IN Tighten → CommitTighten 4`` () =
    KeyMap.dispatchFor tightenOn Key.D4 KeyModifiers.None
    |> should equal (Some (Msg.CommitTighten 4))

[<Fact>]
let ``NumPad1 IN Tighten → CommitTighten 1`` () =
    KeyMap.dispatchFor tightenOn Key.NumPad1 KeyModifiers.None
    |> should equal (Some (Msg.CommitTighten 1))

[<Fact>]
let ``NumPad4 IN Tighten → CommitTighten 4`` () =
    KeyMap.dispatchFor tightenOn Key.NumPad4 KeyModifiers.None
    |> should equal (Some (Msg.CommitTighten 4))

// ─── Other keys IN Tighten mode → no dispatch (silently ignored) ─

[<Fact>]
let ``D5 IN Tighten → no dispatch (out of 1-4 range)`` () =
    KeyMap.dispatchFor tightenOn Key.D5 KeyModifiers.None
    |> should equal (None : Msg.Msg option)

[<Fact>]
let ``OemTilde IN Tighten → no layer-focus change`` () =
    KeyMap.dispatchFor tightenOn Key.OemTilde KeyModifiers.None
    |> should equal (None : Msg.Msg option)

[<Fact>]
let ``D0 IN Tighten → no layer-focus change`` () =
    KeyMap.dispatchFor tightenOn Key.D0 KeyModifiers.None
    |> should equal (None : Msg.Msg option)

[<Fact>]
let ``NumPad0 IN Tighten → no layer-focus change`` () =
    KeyMap.dispatchFor tightenOn Key.NumPad0 KeyModifiers.None
    |> should equal (None : Msg.Msg option)

// ─── Tighten-mode toggle / escape ────────────────────────────────

[<Fact>]
let ``T toggles Tighten mode (works in either state)`` () =
    KeyMap.dispatchFor tightenOff Key.T KeyModifiers.None
    |> should equal (Some Msg.ToggleTightenMode)
    KeyMap.dispatchFor tightenOn Key.T KeyModifiers.None
    |> should equal (Some Msg.ToggleTightenMode)

[<Fact>]
let ``Escape IN Tighten → ToggleTightenMode (exits mode)`` () =
    KeyMap.dispatchFor tightenOn Key.Escape KeyModifiers.None
    |> should equal (Some Msg.ToggleTightenMode)

// ─── Sanity: a few non-tighten global keys still work ───────────

[<Fact>]
let ``Space → RotateSelection90`` () =
    KeyMap.dispatchFor noModel Key.Space KeyModifiers.None
    |> should equal (Some Msg.RotateSelection90)

[<Fact>]
let ``X → MirrorSelectionX`` () =
    KeyMap.dispatchFor noModel Key.X KeyModifiers.None
    |> should equal (Some Msg.MirrorSelectionX)

[<Fact>]
let ``Cmd+Z → UndoActiveMacro`` () =
    KeyMap.dispatchFor noModel Key.Z KeyModifiers.Meta
    |> should equal (Some Msg.UndoActiveMacro)

[<Fact>]
let ``Cmd+Shift+Z → RedoActiveMacro`` () =
    KeyMap.dispatchFor noModel Key.Z (KeyModifiers.Meta ||| KeyModifiers.Shift)
    |> should equal (Some Msg.RedoActiveMacro)

[<Fact>]
let ``Delete → DeleteSelection`` () =
    KeyMap.dispatchFor noModel Key.Delete KeyModifiers.None
    |> should equal (Some Msg.DeleteSelection)

[<Fact>]
let ``Unbound key → no dispatch`` () =
    KeyMap.dispatchFor noModel Key.F1 KeyModifiers.None
    |> should equal (None : Msg.Msg option)
