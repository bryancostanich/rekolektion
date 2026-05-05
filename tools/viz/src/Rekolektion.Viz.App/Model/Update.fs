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
        eprintfn "[viz] OpenFile %s" path
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
        // Hide Magic-internal marker layers (255, *) by default —
        // checkpaint / error / feedback geometry on a freshly loaded
        // .mag would otherwise paint a large translucent overlay
        // over the cell. Toggleable on later from the layer panel.
        // No-op for .gds: those keys don't appear there.
        let toggle' =
            [(255, 0); (255, 1); (255, 2)]
            |> List.fold (fun t key -> Visibility.toggleLayer key false t) model.Toggle
        // Insert (or replace) by path so reopening a file just
        // refreshes its tab in place rather than duplicating it.
        let openMacros =
            let withoutExisting =
                model.OpenMacros |> List.filter (fun m -> m.Path <> macro.Path)
            withoutExisting @ [macro]
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
        let model' =
            { model with
                OpenMacros = openMacros
                ActiveMacroPath = Some macro.Path
                RecentFiles = recents
                Toggle = toggle'
                Selection = None }
        model', cmd
    | Msg.NetsLoaded (path, nets) ->
        // Update the macro in OpenMacros by path. Drops silently if
        // the user closed the tab while net derivation was in flight.
        let openMacros =
            model.OpenMacros
            |> List.map (fun m ->
                if m.Path = path then { m with Nets = nets } else m)
        { model with OpenMacros = openMacros }, Cmd.none
    | Msg.LoadFailed (path, reason) ->
        appendLog (sprintf "load failed: %s — %s" path reason) model, Cmd.none
    | Msg.SetActiveMacro path ->
        // No-op if the requested path is already active — clicking
        // the active tab shouldn't wipe the user's current selection
        // (that was masquerading as a "× clears the inspector" bug).
        if model.ActiveMacroPath = Some path then model, Cmd.none
        else
            // Only switch if the path is actually open; ignore stale
            // requests (e.g. socket-driven from outside).
            let exists = model.OpenMacros |> List.exists (fun m -> m.Path = path)
            if exists then
                { model with ActiveMacroPath = Some path; Selection = None }, Cmd.none
            else model, Cmd.none
    | Msg.CloseActiveTab ->
        match model.ActiveMacroPath with
        | Some p -> model, Cmd.ofMsg (Msg.CloseMacro p)
        | None -> model, Cmd.none
    | Msg.ReloadActiveMacro ->
        // OpenFile → LoadComplete already replaces an existing
        // entry by path, so re-issuing it for the active path
        // refreshes the tab in place.
        match model.ActiveMacroPath with
        | Some p ->
            eprintfn "[viz] Reload %s" p
            model, Cmd.ofMsg (Msg.OpenFile p)
        | None -> model, Cmd.none
    | Msg.CloseMacro path ->
        eprintfn "[viz] CloseMacro: path=%s, before=%d open" path model.OpenMacros.Length
        let remaining = model.OpenMacros |> List.filter (fun m -> m.Path <> path)
        // If the closed tab was active, fall back to the last
        // remaining tab (right-most); empty list → no active tab.
        let nextActive =
            match model.ActiveMacroPath with
            | Some p when p = path ->
                remaining |> List.tryLast |> Option.map (fun m -> m.Path)
            | other -> other
        let model' =
            { model with
                OpenMacros = remaining
                ActiveMacroPath = nextActive
                Selection = None }
        model', Cmd.none
    | Msg.ToggleLayer (key, vis) ->
        { model with Toggle = Visibility.toggleLayer key vis model.Toggle }, Cmd.none
    | Msg.FlipLayer key ->
        let cur = Visibility.isLayerVisible model.Toggle key
        { model with Toggle = Visibility.toggleLayer key (not cur) model.Toggle }, Cmd.none
    | Msg.SetAllLayers vis ->
        let keys =
            Layout.Layer.allDrawing
            |> List.map (fun l -> (l.Number, l.DataType))
        { model with Toggle = Visibility.setAllLayers keys vis model.Toggle }, Cmd.none
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
