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
        | _ -> "{\"ok\":false,\"error\":\"unknown path\"}"
    with ex ->
        // Escape any embedded quotes so a thrown message containing
        // `"` doesn't yield invalid JSON. The plan's example sprintf
        // omitted this — reported as a forced deviation.
        sprintf "{\"ok\":false,\"error\":\"%s\"}" (ex.Message.Replace("\"", "\\\""))
