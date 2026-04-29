module Rekolektion.Viz.App.Tests.UpdateTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.App.Model
open Rekolektion.Viz.Core

let private stubBackend : Update.ServiceBackend = {
    OpenGds = fun _ -> async { return Error "stub" }
    RunMacro = fun _ _ -> async { return Error 1 }
    DeriveNets = fun _ -> async { return Map.empty }
}

[<Fact>]
let ``ToggleLayer updates Model.Toggle.Layers`` () =
    let init = Model.empty
    let next, _cmd = Update.update stubBackend (Msg.ToggleLayer ((68, 20), false)) init
    Visibility.isLayerVisible next.Toggle (68, 20) |> should equal false

[<Fact>]
let ``HighlightNet sets Model.Toggle.HighlightNet`` () =
    let next, _ = Update.update stubBackend (Msg.HighlightNet (Some "BL")) Model.empty
    next.Toggle.HighlightNet |> should equal (Some "BL")

[<Fact>]
let ``SetTab changes ActiveTab`` () =
    let next, _ = Update.update stubBackend (Msg.SetTab Model.Tab.View3D) Model.empty
    next.ActiveTab |> should equal Model.Tab.View3D
