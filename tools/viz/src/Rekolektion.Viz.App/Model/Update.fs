module Rekolektion.Viz.App.Model.Update

open Elmish
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Sidecar.Types

/// Side-effect surface — resolved at boot and curried into update.
/// Test code provides stubs; production wires real services.
type ServiceBackend = {
    OpenGds : string -> Async<Result<Model.LoadedMacro, string>>
    RunMacro: Msg.RunMacroParams -> (string -> unit) -> Async<Result<string, int>>
    // ^ second arg = log-line callback for streaming stderr.
    DeriveNets: Rekolektion.Viz.Core.Gds.Types.Library
                  -> Async<Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry>>
}

let private appendLog (line: string) (model: Model.Model) : Model.Model =
    let log = model.Log @ [line]
    let trimmed = if log.Length > 1000 then log |> List.skip (log.Length - 1000) else log
    { model with Log = trimmed }

let update (backend: ServiceBackend) (msg: Msg.Msg) (model: Model.Model) : Model.Model * Cmd<Msg.Msg> =
    match msg with
    | Msg.OpenFile path ->
        let cmd =
            Cmd.OfAsync.either backend.OpenGds path
                (function
                    | Ok m -> Msg.LoadComplete m
                    | Error r -> Msg.LoadFailed (path, r))
                (fun ex -> Msg.LoadFailed (path, ex.Message))
        model, cmd
    | Msg.LoadComplete macro ->
        let recents =
            macro.Path :: (model.RecentFiles |> List.filter (fun p -> p <> macro.Path))
            |> List.truncate 10
        // If nets came from a sidecar, we're done. Otherwise schedule
        // a background LabelFlood — it can take 10+ s for production
        // macros, so we render the layers immediately and fill in
        // nets when ready. NetsLoaded carries the path so a stale
        // result for a previously-open file is dropped.
        let cmd =
            if macro.NetsFromSidecar then Cmd.none
            else
                Cmd.OfAsync.either
                    backend.DeriveNets macro.Library
                    (fun nets -> Msg.NetsLoaded (macro.Path, nets))
                    (fun ex -> Msg.LogLine (sprintf "net derivation failed: %s" ex.Message))
        { model with Macro = Some macro; RecentFiles = recents; Selection = None }, cmd
    | Msg.NetsLoaded (path, nets) ->
        // Drop the result if the user has loaded a different file
        // since we kicked off the derivation.
        match model.Macro with
        | Some m when m.Path = path -> { model with Macro = Some { m with Nets = nets } }, Cmd.none
        | _ -> model, Cmd.none
    | Msg.LoadFailed (path, reason) ->
        appendLog (sprintf "load failed: %s — %s" path reason) model, Cmd.none
    | Msg.ToggleLayer (key, vis) ->
        { model with Toggle = Visibility.toggleLayer key vis model.Toggle }, Cmd.none
    | Msg.ToggleNet (name, vis) ->
        { model with Toggle = Visibility.toggleNet name vis model.Toggle }, Cmd.none
    | Msg.ToggleBlock (name, vis) ->
        { model with Toggle = Visibility.toggleBlock name vis model.Toggle }, Cmd.none
    | Msg.HighlightNet net ->
        { model with Toggle = Visibility.highlightNet net model.Toggle }, Cmd.none
    | Msg.IsolateBlock blk ->
        { model with Toggle = Visibility.isolateBlock blk model.Toggle }, Cmd.none
    | Msg.SetTab tab -> { model with ActiveTab = tab }, Cmd.none
    | Msg.PolygonPicked (s, i) -> { model with Selection = Some (s, i) }, Cmd.none
    | Msg.ClearSelection -> { model with Selection = None }, Cmd.none
    | Msg.Pan2D (dx, dy) ->
        let v = model.View2D
        { model with View2D = { v with OffsetX = v.OffsetX + dx; OffsetY = v.OffsetY + dy } }, Cmd.none
    | Msg.Zoom2D f ->
        let v = model.View2D
        { model with View2D = { v with ZoomFactor = v.ZoomFactor * f } }, Cmd.none
    | Msg.Orbit3D (dy, dp) ->
        let v = model.View3D
        { model with View3D = { v with OrbitYaw = v.OrbitYaw + dy; OrbitPitch = v.OrbitPitch + dp } }, Cmd.none
    | Msg.Zoom3D f ->
        let v = model.View3D
        { model with View3D = { v with ZoomFactor = v.ZoomFactor * f } }, Cmd.none
    | Msg.RunMacroRequested p ->
        let cmd =
            // TODO(task 16+): wire log-line callback through Cmd.ofSub so streamed stderr posts LogLine msgs.
            Cmd.OfAsync.either
                (fun () -> backend.RunMacro p (fun _line -> ()))
                ()
                (function
                    | Ok path -> Msg.RunCompleted path
                    | Error code -> Msg.RunFailed code)
                (fun ex -> Msg.LogLine (sprintf "run failed: %s" ex.Message))
        model, cmd
    | Msg.RunStarted pid ->
        { model with Run = Model.RunState.Running (pid, []); LogVisible = true }, Cmd.none
    | Msg.LogLine line -> appendLog line model, Cmd.none
    | Msg.RunCompleted path ->
        { model with Run = Model.RunState.Idle }, Cmd.ofMsg (Msg.OpenFile path)
    | Msg.RunFailed code ->
        let m = appendLog (sprintf "run failed (exit %d)" code) model
        { m with Run = Model.RunState.Idle }, Cmd.none
    | Msg.ToggleLogPane -> { model with LogVisible = not model.LogVisible }, Cmd.none
    | Msg.RecentFileClicked p -> model, Cmd.ofMsg (Msg.OpenFile p)
