module Rekolektion.Viz.App.Services.CommandListener

open System.Text.Json
open Avalonia.Threading
open Rekolektion.Viz.App.Model
open Rekolektion.Viz.Core.Rkt.Types

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
            // Backward-compat shape: {"name": "BL"} replaces the
            // highlighted-set with a single net; {"name": null} or
            // missing clears it. Multi-net callers should use
            // /highlight/nets with an array.
            let next =
                match root.TryGetProperty "name" with
                | true, n when n.ValueKind = JsonValueKind.String ->
                    Set.singleton (n.GetString())
                | _ -> Set.empty
            Dispatcher.UIThread.Post(fun () -> dispatch (Msg.SetHighlightedNets next))
            "{\"ok\":true}"
        | "/highlight/nets" ->
            // {"names": ["BL", "WL"]} — replace the highlighted-set
            // wholesale. Empty array clears it.
            let arr = root.GetProperty("names")
            let mutable acc = Set.empty
            for i in 0 .. arr.GetArrayLength() - 1 do
                acc <- acc.Add (arr.[i].GetString())
            Dispatcher.UIThread.Post(fun () -> dispatch (Msg.SetHighlightedNets acc))
            "{\"ok\":true}"
        | "/tab" ->
            let tab =
                match root.GetProperty("tab").GetString() with
                | "3D" -> Model.Tab.View3D
                | _    -> Model.Tab.View2D
            Dispatcher.UIThread.Post(fun () -> dispatch (Msg.SetTab tab))
            "{\"ok\":true}"
        | "/active-macro" ->
            // Switch the active macro-tab to the one whose path matches.
            // body: { "path": "<absolute path>" }
            let p = root.GetProperty("path").GetString()
            Dispatcher.UIThread.Post(fun () -> dispatch (Msg.SetActiveMacro p))
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
            // Toggle Tighten mode for the agent test loop. To
            // commit a specific candidate, follow with
            // /tighten/commit { "index": N }.
            Dispatcher.UIThread.Post(fun () -> dispatch Msg.ToggleTightenMode)
            "{\"ok\":true}"
        | "/tighten/commit" ->
            let i = root.GetProperty("index").GetInt32()
            Dispatcher.UIThread.Post(fun () -> dispatch (Msg.CommitTighten i))
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

/// GET-side query endpoints. Returns JSON. Used by agent-driven
/// test loops + the MCP server to inspect the live UI state.
///
/// Endpoints:
///   /instances    — Active macro's top-level SRefs (incl. selection).
///   /macros       — Open macro tabs + which is active.
///   /selection    — Current InstanceSelection AND polygon Selection
///                   with enough metadata for an agent to reason about
///                   what's picked (cell name, bbox, etc).
let private esc (s: string) : string = s.Replace("\"", "\\\"")

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
                                (esc i.Name)
                                (esc i.Sref.Cell)
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
                        (esc mc.Path)
                        (esc mc.OriginalPath)
                        dirty
                        selected
                        entries
        | "/macros" ->
            match AppDispatch.currentModel with
            | None -> "{\"ok\":false,\"error\":\"no model yet\"}"
            | Some m ->
                let active =
                    match m.ActiveMacroPath with
                    | Some p -> sprintf "\"%s\"" (esc p)
                    | None   -> "null"
                let macros =
                    m.OpenMacros
                    |> List.map (fun mc ->
                        sprintf
                            "{\"path\":\"%s\",\"originalPath\":\"%s\",\"dirty\":%s}"
                            (esc mc.Path)
                            (esc mc.OriginalPath)
                            (if mc.Dirty then "true" else "false"))
                    |> String.concat ","
                sprintf "{\"ok\":true,\"active\":%s,\"macros\":[%s]}" active macros
        | "/selection" ->
            match AppDispatch.currentModel with
            | None -> "{\"ok\":false,\"error\":\"no model yet\"}"
            | Some m ->
                match Model.activeMacro m with
                | None ->
                    "{\"ok\":true,\"activePath\":null,\"instances\":[],\"polygons\":[]}"
                | Some mc ->
                    let uupdb = float mc.Document.Units.DbuNm * 1.0e-3
                    let umOf (n: int64) = float n * uupdb
                    // Selected SRefs: include cell, origin, and bbox
                    // in µm so the agent doesn't have to know DbuNm.
                    let instItems =
                        m.InstanceSelection
                        |> Set.toList
                        |> List.choose (fun idx ->
                            mc.TopInstances
                            |> Array.tryFind (fun i -> i.Index = idx))
                        |> List.map (fun i ->
                            let (x1, y1, x2, y2) = i.BBox
                            sprintf
                                "{\"index\":%d,\"cell\":\"%s\",\"originXUm\":%.3f,\"originYUm\":%.3f,\"bboxUm\":[%.3f,%.3f,%.3f,%.3f]}"
                                i.Index
                                (esc i.Sref.Cell)
                                (umOf i.Sref.Origin.X) (umOf i.Sref.Origin.Y)
                                (umOf x1) (umOf y1) (umOf x2) (umOf y2))
                        |> String.concat ","
                    // Selected polygons: cell + element index + layer
                    // pair + bbox in µm. Skips non-poly elements that
                    // shouldn't be in the selection set anyway.
                    let cellByName =
                        mc.Document.Cells
                        |> List.map (fun c -> c.Name, c)
                        |> Map.ofList
                    let polyItems =
                        m.Selection
                        |> Set.toList
                        |> List.choose (fun (sname, eidx) ->
                            match Map.tryFind sname cellByName with
                            | None -> None
                            | Some c when eidx < 0 || eidx >= c.Elements.Length ->
                                None
                            | Some c ->
                                let el = c.Elements.[eidx]
                                let bbox =
                                    match el with
                                    | PolyEl p when not p.Points.IsEmpty ->
                                        let xs = p.Points |> List.map (fun pt -> pt.X)
                                        let ys = p.Points |> List.map (fun pt -> pt.Y)
                                        Some (List.min xs, List.min ys,
                                              List.max xs, List.max ys)
                                    | RectEl r ->
                                        let xMin = min r.X1 r.X2
                                        let xMax = max r.X1 r.X2
                                        let yMin = min r.Y1 r.Y2
                                        let yMax = max r.Y1 r.Y2
                                        Some (xMin, yMin, xMax, yMax)
                                    | PathEl p when not p.Points.IsEmpty ->
                                        let xs = p.Points |> List.map (fun pt -> pt.X)
                                        let ys = p.Points |> List.map (fun pt -> pt.Y)
                                        Some (List.min xs, List.min ys,
                                              List.max xs, List.max ys)
                                    | _ -> None
                                let kind, layerN, layerD =
                                    match el with
                                    | PolyEl p ->
                                        let n, d =
                                            Rekolektion.Viz.Core.Rkt.ToGds.layerToGds p.Layer
                                        "poly", n, d
                                    | RectEl r ->
                                        let n, d =
                                            Rekolektion.Viz.Core.Rkt.ToGds.layerToGds r.Layer
                                        "rect", n, d
                                    | PathEl p ->
                                        let n, d =
                                            Rekolektion.Viz.Core.Rkt.ToGds.layerToGds p.Layer
                                        "path", n, d
                                    | _ -> "other", -1, -1
                                match bbox with
                                | None -> None
                                | Some (x1, y1, x2, y2) ->
                                    Some (sprintf
                                        "{\"cell\":\"%s\",\"index\":%d,\"kind\":\"%s\",\"layer\":%d,\"datatype\":%d,\"bboxUm\":[%.3f,%.3f,%.3f,%.3f]}"
                                        (esc sname) eidx kind layerN layerD
                                        (umOf x1) (umOf y1) (umOf x2) (umOf y2)))
                        |> String.concat ","
                    sprintf
                        "{\"ok\":true,\"activePath\":\"%s\",\"instances\":[%s],\"polygons\":[%s]}"
                        (esc mc.Path) instItems polyItems
        | _ -> "{\"ok\":false,\"error\":\"unknown query path\"}"
    with ex ->
        sprintf "{\"ok\":false,\"error\":\"%s\"}" (esc ex.Message)
