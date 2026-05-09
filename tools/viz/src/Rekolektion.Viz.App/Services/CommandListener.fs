module Rekolektion.Viz.App.Services.CommandListener

open System.Text.Json
open Avalonia.Threading
open Rekolektion.Viz.App.Model

/// Parse a POST body JSON and dispatch the corresponding `Msg` via the
/// Elmish dispatcher. Returns a short response body for the client.
///
/// All dispatches are marshalled to the UI thread via
/// `Dispatcher.UIThread.Post` — the listener accept loop runs on a
/// thread-pool task, but FuncUI's Elmish view-render and our own
/// `uiDispatch` wrapper expect the dispatch call to land on the UI
/// thread (so view diff fires synchronously).
///
/// Endpoints (all POST):
///   /open          { "path": "..." }                     -> Msg.OpenFile
///   /toggle/layer  { "name": "met1", "visible": true }   -> Msg.ToggleLayer
///   /toggle/net    { "name": "VDD",  "visible": false }  -> Msg.ToggleNet
///   /highlight/net { "name": "CLK" }      (or null/none) -> Msg.HighlightNet
///   /tab           { "tab": "2D" | "3D" }                -> Msg.SetTab
let handle (path: string) (body: string) (dispatch: Msg.Msg -> unit) : string =
    try
        use doc = JsonDocument.Parse body
        let root = doc.RootElement
        match path with
        | "/open" ->
            let p = root.GetProperty("path").GetString()
            Dispatcher.UIThread.Post(fun () -> dispatch (Msg.OpenFile p))
            "{\"ok\":true}"
        | "/toggle/layer" ->
            let name = root.GetProperty("name").GetString()
            let visible = root.GetProperty("visible").GetBoolean()
            match Rekolektion.Viz.Core.Layout.Layer.allDrawing
                  |> List.tryFind (fun l -> l.Name = name) with
            | Some l ->
                Dispatcher.UIThread.Post(fun () ->
                    dispatch (Msg.ToggleLayer ((l.Number, l.DataType), visible)))
                "{\"ok\":true}"
            | None -> "{\"ok\":false,\"error\":\"unknown layer\"}"
        | "/toggle/net" ->
            let name = root.GetProperty("name").GetString()
            let visible = root.GetProperty("visible").GetBoolean()
            Dispatcher.UIThread.Post(fun () -> dispatch (Msg.ToggleNet (name, visible)))
            "{\"ok\":true}"
        | "/highlight/net" ->
            let net =
                match root.TryGetProperty "name" with
                | true, n when n.ValueKind = JsonValueKind.String -> Some (n.GetString())
                | _ -> None
            Dispatcher.UIThread.Post(fun () -> dispatch (Msg.HighlightNet net))
            "{\"ok\":true}"
        | "/tab" ->
            let tab =
                match root.GetProperty("tab").GetString() with
                | "3D" -> Model.Tab.View3D
                | _    -> Model.Tab.View2D
            Dispatcher.UIThread.Post(fun () -> dispatch (Msg.SetTab tab))
            "{\"ok\":true}"
        | "/select" ->
            // Replace InstanceSelection with the given indices.
            // body: { "indices": [0, 2, ...] }
            let arr = root.GetProperty("indices")
            let mutable acc = Set.empty
            for i in 0 .. arr.GetArrayLength() - 1 do
                acc <- acc.Add (arr.[i].GetInt32())
            Dispatcher.UIThread.Post(fun () ->
                dispatch (Msg.SetInstanceSelection acc))
            "{\"ok\":true}"
        | "/move" ->
            // Translate the current InstanceSelection by Δ DBU.
            // body: { "dxDbu": <int>, "dyDbu": <int> }
            let dx = root.GetProperty("dxDbu").GetInt64()
            let dy = root.GetProperty("dyDbu").GetInt64()
            Dispatcher.UIThread.Post(fun () ->
                dispatch (Msg.MoveSelectionDbu (dx, dy)))
            "{\"ok\":true}"
        | "/tighten" ->
            Dispatcher.UIThread.Post(fun () -> dispatch Msg.TightenSelection)
            "{\"ok\":true}"
        | "/dimensions" ->
            Dispatcher.UIThread.Post(fun () -> dispatch Msg.ToggleDimensions)
            "{\"ok\":true}"
        | "/drc" ->
            Dispatcher.UIThread.Post(fun () -> dispatch Msg.ToggleDrc)
            "{\"ok\":true}"
        | "/clear-selection" ->
            Dispatcher.UIThread.Post(fun () -> dispatch Msg.ClearInstanceSelection)
            "{\"ok\":true}"
        | "/close-all" ->
            Dispatcher.UIThread.Post(fun () -> dispatch Msg.CloseAllTabs)
            "{\"ok\":true}"
        | _ -> "{\"ok\":false,\"error\":\"unknown path\"}"
    with ex ->
        // Escape any embedded quotes so a thrown message containing
        // `"` doesn't yield invalid JSON. The plan's example sprintf
        // omitted this — reported as a forced deviation.
        sprintf "{\"ok\":false,\"error\":\"%s\"}" (ex.Message.Replace("\"", "\\\""))

/// GET-side query endpoints. Returns JSON. Currently only
/// `/instances`, which lists the active macro's top-level SRefs
/// with their world bboxes — used by agent-driven test loops to
/// figure out which index to select before issuing /select etc.
let handleQuery (path: string) (_dispatch: Msg.Msg -> unit) : string =
    try
        match path with
        | "/instances" ->
            match AppDispatch.currentModel with
            | None -> "{\"ok\":false,\"error\":\"no model yet\"}"
            | Some m ->
                match Model.activeMacro m with
                | None -> "{\"ok\":true,\"instances\":[]}"
                | Some mc ->
                    let entries =
                        mc.TopInstances
                        |> Array.map (fun i ->
                            let (x1, y1, x2, y2) = i.BBox
                            sprintf
                                "{\"index\":%d,\"name\":\"%s\",\"cell\":\"%s\",\"originX\":%d,\"originY\":%d,\"bbox\":[%d,%d,%d,%d]}"
                                i.Index
                                (i.Name.Replace("\"", "\\\""))
                                (i.Sref.StructureName.Replace("\"", "\\\""))
                                i.Sref.Origin.X
                                i.Sref.Origin.Y
                                x1 y1 x2 y2)
                        |> String.concat ","
                    let selected =
                        m.InstanceSelection
                        |> Set.toList
                        |> List.map string
                        |> String.concat ","
                    let dirty =
                        if mc.Dirty then "true" else "false"
                    sprintf
                        "{\"ok\":true,\"path\":\"%s\",\"originalPath\":\"%s\",\"dirty\":%s,\"selected\":[%s],\"instances\":[%s]}"
                        (mc.Path.Replace("\"", "\\\""))
                        (mc.OriginalPath.Replace("\"", "\\\""))
                        dirty
                        selected
                        entries
        | _ -> "{\"ok\":false,\"error\":\"unknown query path\"}"
    with ex ->
        sprintf "{\"ok\":false,\"error\":\"%s\"}" (ex.Message.Replace("\"", "\\\""))
