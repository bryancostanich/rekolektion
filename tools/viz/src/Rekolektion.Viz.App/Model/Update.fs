module Rekolektion.Viz.App.Model.Update

open Elmish
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Sidecar.Types
open Rekolektion.Viz.App.Services

/// Side-effect surface — resolved at boot and curried into update.
/// Test code provides stubs; production wires real services.
type ServiceBackend = {
    OpenGds : string -> Async<Result<Model.LoadedMacro, string>>
    RunMacro: Msg.RunMacroParams -> (string -> unit) -> Async<Result<string, int>>
    // ^ second arg = log-line callback for streaming stderr.
    DeriveNets: Rekolektion.Viz.Core.Rkt.Types.Document
                  -> Async<Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry>>
    /// Round-trip the macro through `Mag.Writer.writeUpdated`,
    /// returning the path that ended up on disk.
    SaveMacro : Model.LoadedMacro -> Async<Result<string, string>>
}

let private appendLog (line: string) (model: Model.Model) : Model.Model =
    let log = model.Log @ [line]
    let trimmed = if log.Length > 1000 then log |> List.skip (log.Length - 1000) else log
    { model with Log = trimmed }

// Label-anchor helpers (anchorMapForCell, elementBbox,
// layerNumberOf) live in `Layout.Instances` so the canvas live
// preview and the Update commit share the implementation. Same
// rule the renderer / Net.Ratlines / LabelFlood all use.
let private anchorMapForCell = Layout.Instances.anchorMapForCell
let private elementBbox = Layout.Instances.elementBbox
let private layerNumberOf = Layout.Instances.layerNumberOf

/// PolyKey carries (cell, element-index, top-instance-index) so the
/// UI can distinguish two SRef instances of the same cell. Instances.fs
/// mutation ops address an AST element by (cell, index) — modifying
/// the underlying element changes every instance visually anyway, so
/// TopInstance is dropped at the boundary.
let private polyKeyTuples
        (sel: Set<Layout.Flatten.PolyKey>)
        : Set<string * int> =
    sel |> Set.map (fun pk -> pk.Cell, pk.Index)

/// Discriminated-union case name for an Elmish Msg, used for the
/// `msg` log category so a user / agent can replay an action stream
/// without paying for full payload serialisation. Reflection cost
/// is negligible vs the dispatch + view diff that follows.
/// Switch the active tab while preserving per-tab selection state.
/// Stashes the outgoing tab's (Selection, InstanceSelection,
/// SelectedRatlines) into SavedSelections under the old path, then
/// loads the incoming tab's saved sets (empty if never selected
/// in). No-op when newPath equals the current active path.
let private switchActive
        (newPath: string option)
        (model: Model.Model)
        : Model.Model =
    if newPath = model.ActiveMacroPath then model
    else
        let saved =
            match model.ActiveMacroPath with
            | Some oldPath ->
                model.SavedSelections
                |> Map.add oldPath
                    (model.Selection,
                     model.InstanceSelection,
                     model.SelectedRatlines)
            | None -> model.SavedSelections
        let sel, instSel, ratSel =
            match newPath with
            | Some p ->
                match Map.tryFind p saved with
                | Some triple -> triple
                | None -> Set.empty, Set.empty, Set.empty
            | None -> Set.empty, Set.empty, Set.empty
        // Drop the new tab's entry from the saved map — it's live
        // now in the top-level fields, so keeping a stale copy
        // would let the next switch resurrect old state.
        let saved' =
            match newPath with
            | Some p -> Map.remove p saved
            | None -> saved
        { model with
            ActiveMacroPath = newPath
            Selection = sel
            InstanceSelection = instSel
            SelectedRatlines = ratSel
            SavedSelections = saved' }

// ADR-0002 — route-commit helpers used by RouteFinish.
let private routeLayerOf
        (key: int * int)
        (pdk: string)
        : Rekolektion.Viz.Core.Rkt.Types.Layer =
    let (num, dt) = key
    match Layout.Layer.bySky130Number num dt with
    | Some l -> Rekolektion.Viz.Core.Rkt.Types.Named(pdk, l.Name)
    | None   -> Rekolektion.Viz.Core.Rkt.Types.Unknown(num, dt)

let private rectOfDraftSegment
        (pdk: string)
        (seg: Routing.Draft.DraftSegment)
        : Rekolektion.Viz.Core.Rkt.Types.Rectangle = {
    Layer = routeLayerOf seg.Layer pdk
    X1 = seg.X1
    Y1 = seg.Y1
    X2 = seg.X2
    Y2 = seg.Y2
    Net = None
    Props = []
    Comments = []
}

/// Append a batch of rectangles to the document's top cell. The top
/// cell is `doc.TopCell` if set, otherwise the first cell in `Cells`.
/// No-op when the document has no cells.
let private appendRectsToTop
        (rects: Rekolektion.Viz.Core.Rkt.Types.Rectangle list)
        (doc: Rekolektion.Viz.Core.Rkt.Types.Document)
        : Rekolektion.Viz.Core.Rkt.Types.Document =
    if List.isEmpty rects then doc
    else
        let topName =
            doc.TopCell
            |> Option.orElseWith (fun () ->
                doc.Cells |> List.tryHead |> Option.map (fun c -> c.Name))
        match topName with
        | None -> doc
        | Some n ->
            let cells' =
                doc.Cells
                |> List.map (fun c ->
                    if c.Name <> n then c
                    else
                        let newEls =
                            rects
                            |> List.map Rekolektion.Viz.Core.Rkt.Types.RectEl
                        { c with Elements = c.Elements @ newEls })
            { doc with Cells = cells' }

/// Shared commit machinery for RouteFinish and RouteStop. Picks
/// the segment set via `getSegs` (`Draft.finishSegments` for the
/// commit-tentative path, `Draft.fixedSegments` for the stop-at-
/// last-click path), appends DRC-driven endpoint pads on the
/// active layer, pushes an undo snapshot, marks dirty, and clears
/// `DraftRoute`. Returns `(model, Cmd.none)` so the caller can
/// stitch directly into a match arm.
let private commitRouteWith
        (model: Model.Model)
        (getSegs: Routing.Draft.DraftRoute -> Routing.Draft.DraftSegment list)
        : Model.Model * Cmd<Msg.Msg> =
    match model.DraftRoute with
    | None -> model, Cmd.none
    | Some d ->
        let segs = getSegs d
        if List.isEmpty segs then
            { model with DraftRoute = None }, Cmd.none
        else
            match model.ActiveMacroPath with
            | None -> { model with DraftRoute = None }, Cmd.none
            | Some path ->
                let mutable activePath' = path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun mc ->
                        if mc.Path <> path then mc
                        else
                            let pads =
                                match
                                    Routing.Pads.endpointPadSide
                                        model.DrcView mc.Document.Units
                                        d.Layer with
                                | Some side -> Routing.Draft.endpointPads side d
                                | None -> []
                            let allSegs = pads @ segs
                            let rects =
                                allSegs
                                |> List.map
                                    (rectOfDraftSegment mc.Document.Pdk)
                            let doc' = appendRectsToTop rects mc.Document
                            let flat' = Layout.Flatten.flatten doc'
                            let inst' = Layout.Instances.enumerate doc'
                            let mc' =
                                EditSession.pushUndoSnapshot mc
                                |> fun mc'' ->
                                    { mc'' with
                                        Document = doc'
                                        FlatPolygons = flat'
                                        TopInstances = inst' }
                                |> EditSession.markDirty
                            activePath' <- mc'.Path
                            mc')
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = Some activePath'
                    DraftRoute = None }, Cmd.none

let private msgCaseName (msg: Msg.Msg) : string =
    let info, _ =
        Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(
            msg, msg.GetType())
    info.Name

let update (backend: ServiceBackend) (msg: Msg.Msg) (model: Model.Model) : Model.Model * Cmd<Msg.Msg> =
    Rekolektion.Viz.App.Services.Logger.log "msg" {| name = msgCaseName msg |}
    match msg with
    | Msg.OpenFile path ->
        eprintfn "[viz] OpenFile %s" path
        Rekolektion.Viz.App.Services.Logger.log "load" {| op = "request"; path = path |}
        let cmd =
            Cmd.OfAsync.either backend.OpenGds path
                (function
                    | Ok m -> Msg.LoadComplete m
                    | Error r -> Msg.LoadFailed (path, r))
                (fun ex -> Msg.LoadFailed (path, ex.Message))
        model, cmd
    | Msg.LoadComplete macro ->
        let recents =
            macro.OriginalPath :: (model.RecentFiles |> List.filter (fun p -> p <> macro.OriginalPath))
            |> List.truncate 10
        Rekolektion.Viz.App.Services.Recents.save recents
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
        // Also remove any open `<base>_edited*.mag` derived from
        // the same source — leaving those would create two tabs
        // that both retarget to the same edited Path on first
        // edit, masking one of them under List.map's by-path
        // mutation. Match by OriginalPath so we catch every
        // edited variant of the file we're (re)opening.
        // Replace IN PLACE: a tab found by Path is swapped with
        // the new macro at the SAME index so a Cmd+R reload
        // doesn't reorder the tab strip. New paths (no match)
        // append to the end.
        let openMacros =
            let matches (m: Model.LoadedMacro) =
                m.Path = macro.Path || m.OriginalPath = macro.Path
            if model.OpenMacros |> List.exists matches then
                model.OpenMacros
                |> List.map (fun m -> if matches m then macro else m)
            else
                model.OpenMacros @ [macro]
        // If nets came from a sidecar, we're done. Otherwise schedule
        // a background LabelFlood — it can take 10+ s for production
        // macros, so we render the layers immediately and fill in
        // nets when ready. NetsLoaded carries the path so a stale
        // result for a previously-open file is dropped.
        let cmd =
            if macro.NetsFromSidecar then Cmd.none
            else
                Cmd.OfAsync.either
                    backend.DeriveNets macro.Document
                    (fun nets -> Msg.NetsLoaded (macro.Path, nets))
                    (fun ex -> Msg.LogLine (sprintf "net derivation failed: %s" ex.Message))
        // Ratlines-on state carries across file loads, but the
        // visible-net SET is per-macro. Stale net names from the
        // previous file's nets wouldn't match anything in the new
        // macro, so nothing would render until the user toggled
        // the button twice. Refresh to the new macro's nets when
        // ratlines were on.
        let toggle' =
            if model.Toggle.VisibleRatlines.IsEmpty then toggle'
            elif macro.Nets.IsEmpty then toggle'  // wait for NetsLoaded
            else
                let newNets = macro.Nets |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                Visibility.setVisibleRatlines newNets toggle'
        // Stash the old tab's selection and load anything saved for
        // the newly-active path (typically empty on a fresh load,
        // populated when the user reloaded the same path). Doing
        // this through switchActive keeps the per-tab selection
        // contract consistent with explicit tab clicks.
        let switched =
            switchActive (Some macro.Path)
                { model with
                    OpenMacros = openMacros
                    RecentFiles = recents
                    Toggle = toggle' }
        switched, cmd
    | Msg.NetsLoaded (path, nets) ->
        // Update the macro in OpenMacros by path. Drops silently if
        // the user closed the tab while net derivation was in flight.
        Rekolektion.Viz.App.Services.Logger.log "nets.loaded"
            {| path = path
               count = nets.Count
               names = nets |> Map.toList |> List.map fst |> List.sort |}
        let openMacros =
            model.OpenMacros
            |> List.map (fun m ->
                if m.Path = path then { m with Nets = nets } else m)
        // Pair with the LoadComplete refresh: when nets arrive
        // asynchronously for the currently-active macro and
        // ratlines are on, populate VisibleRatlines now (the
        // LoadComplete refresh saw an empty Nets map and deferred).
        let toggle' =
            if model.Toggle.VisibleRatlines.IsEmpty then model.Toggle
            elif model.ActiveMacroPath <> Some path then model.Toggle
            elif nets.IsEmpty then model.Toggle
            else
                let names = nets |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                Visibility.setVisibleRatlines names model.Toggle
        { model with OpenMacros = openMacros; Toggle = toggle' }, Cmd.none
    | Msg.LoadFailed (path, reason) ->
        Rekolektion.Viz.App.Services.Logger.log "load"
            {| op = "fail"; path = path; reason = reason |}
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
            if exists then switchActive (Some path) model, Cmd.none
            else model, Cmd.none
    | Msg.CloseAllTabs ->
        { model with
            OpenMacros = []
            ActiveMacroPath = None
            Selection = Set.empty
            InstanceSelection = Set.empty
            SelectedRatlines = Set.empty
            SavedSelections = Map.empty
            RenamingPath = None }, Cmd.none
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
        // Drop any saved selection for the closed tab. switchActive
        // handles the case where the closed tab WAS the active tab
        // (current top-level selection belongs to the closed path
        // → discard, load next tab's saved selection).
        let savedAfterClose = Map.remove path model.SavedSelections
        let model' =
            if model.ActiveMacroPath = Some path then
                // Active tab closed → fully discard its selection
                // (don't stash it under the closed path) and load
                // the next tab's saved.
                switchActive nextActive
                    { model with
                        OpenMacros = remaining
                        SavedSelections = savedAfterClose
                        ActiveMacroPath = None
                        Selection = Set.empty
                        InstanceSelection = Set.empty
                        SelectedRatlines = Set.empty }
            else
                // Non-active tab closed → just drop its saved entry,
                // top-level selection (belongs to still-active tab)
                // stays put.
                { model with
                    OpenMacros = remaining
                    SavedSelections = savedAfterClose }
        model', Cmd.none
    | Msg.ToggleLayer (key, vis) ->
        let toggle' = Visibility.toggleLayer key vis model.Toggle
        Rekolektion.Viz.App.Services.SessionState.save
            { Layers =
                toggle'.Layers
                |> Map.toList
                |> List.map (fun ((n, d), v) -> (n, d, v)) }
        { model with Toggle = toggle' }, Cmd.none
    | Msg.FlipLayer key ->
        let cur = Visibility.isLayerVisible model.Toggle key
        let toggle' = Visibility.toggleLayer key (not cur) model.Toggle
        Rekolektion.Viz.App.Services.SessionState.save
            { Layers =
                toggle'.Layers
                |> Map.toList
                |> List.map (fun ((n, d), v) -> (n, d, v)) }
        { model with Toggle = toggle' }, Cmd.none
    | Msg.SetAllLayers vis ->
        let keys =
            Layout.Layer.allDrawing
            |> List.map (fun l -> (l.Number, l.DataType))
        let toggle' = Visibility.setAllLayers keys vis model.Toggle
        Rekolektion.Viz.App.Services.SessionState.save
            { Layers =
                toggle'.Layers
                |> Map.toList
                |> List.map (fun ((n, d), v) -> (n, d, v)) }
        { model with Toggle = toggle' }, Cmd.none
    | Msg.SetActiveLayer layer ->
        let toggle' = Visibility.setActiveLayer layer model.Toggle
        Rekolektion.Viz.App.Services.SessionState.save
            { Layers =
                toggle'.Layers
                |> Map.toList
                |> List.map (fun ((n, d), v) -> (n, d, v)) }
        { model with Toggle = toggle' }, Cmd.none
    | Msg.SetDrcView view ->
        Rekolektion.Viz.App.Services.Logger.log "drc.view"
            {| rules = view.Rules.Length
               provenance = view.Provenance.Count |}
        { model with DrcView = view }, Cmd.none
    | Msg.ToggleNet (name, vis) ->
        { model with Toggle = Visibility.toggleNet name vis model.Toggle }, Cmd.none
    | Msg.ToggleBlock (name, vis) ->
        { model with Toggle = Visibility.toggleBlock name vis model.Toggle }, Cmd.none
    | Msg.ToggleNetHighlight name ->
        { model with Toggle = Visibility.toggleNetHighlight name model.Toggle }, Cmd.none
    | Msg.SetHighlightedNets nets ->
        { model with Toggle = Visibility.setHighlightedNets nets model.Toggle }, Cmd.none
    | Msg.ToggleNetRatline name ->
        { model with Toggle = Visibility.toggleNetRatline name model.Toggle }, Cmd.none
    | Msg.SetVisibleRatlines nets ->
        Rekolektion.Viz.App.Services.Logger.log "ratline.setvisible"
            {| before = model.Toggle.VisibleRatlines.Count
               after = nets.Count
               sample = nets |> Seq.truncate 8 |> Seq.toList |}
        { model with Toggle = Visibility.setVisibleRatlines nets model.Toggle }, Cmd.none
    | Msg.SetSelectedRatlines nets ->
        // Log so the user can identify the just-clicked net even
        // when the visual highlight isn't conclusive.
        let newlySelected = Set.difference nets model.SelectedRatlines
        for name in newlySelected do
            Rekolektion.Viz.App.Services.Logger.log "ratline"
                {| op = "select"; net = name |}
        { model with SelectedRatlines = nets }, Cmd.none
    | Msg.IsolateBlock blk ->
        { model with Toggle = Visibility.isolateBlock blk model.Toggle }, Cmd.none
    | Msg.SetTab tab -> { model with ActiveTab = tab }, Cmd.none
    | Msg.PolygonPicked key ->
        // Replace polygon selection with the single picked element.
        // Shift-click extension goes through SetPolygonSelection so
        // the canvas can compute the new set with the modifier in
        // hand. Picking a polygon also drops any active ratline
        // selection — only one selection genre is "current" at a
        // time so the inspector and overlay stay coherent.
        { model with
            Selection = Set.singleton key
            SelectedRatlines = Set.empty }, Cmd.none
    | Msg.SetPolygonSelection sel ->
        let ratlines' =
            if sel.IsEmpty then model.SelectedRatlines
            else Set.empty
        { model with
            Selection = sel
            SelectedRatlines = ratlines' }, Cmd.none
    | Msg.ClearSelection -> { model with Selection = Set.empty }, Cmd.none
    | Msg.ToggleDimensions ->
        { model with ShowDimensions = not model.ShowDimensions }, Cmd.none
    | Msg.ToggleDrc ->
        { model with ShowDrc = not model.ShowDrc }, Cmd.none
    | Msg.ToggleGrid ->
        { model with ShowGrid = not model.ShowGrid }, Cmd.none
    | Msg.ToggleRuler ->
        { model with ShowRuler = not model.ShowRuler }, Cmd.none
    | Msg.ToggleLabels ->
        { model with ShowLabels = not model.ShowLabels }, Cmd.none
    | Msg.ToggleSnap ->
        { model with SnapEnabled = not model.SnapEnabled }, Cmd.none
    | Msg.ToggleRatlines ->
        // Master toggle: if any ratline is on, clear all; otherwise
        // turn on ratlines for every known net in the active macro.
        // Mirrors the layer panel "All / None" pattern.
        let nextSet =
            if not model.Toggle.VisibleRatlines.IsEmpty then Set.empty
            else
                match Model.activeMacro model with
                | None -> Set.empty
                | Some m -> m.Nets |> Map.toSeq |> Seq.map fst |> Set.ofSeq
        { model with Toggle = Visibility.setVisibleRatlines nextSet model.Toggle }, Cmd.none
    | Msg.RouteSlideCommit (cell, dxDbu, dyDbu, adjusts, extensions) ->
        if (dxDbu = 0L && dyDbu = 0L)
           || (List.isEmpty adjusts && List.isEmpty extensions) then
            model, Cmd.none
        else
            match Model.activeMacro model with
            | None -> model, Cmd.none
            | Some mc ->
                let bySource =
                    adjusts
                    |> List.map (fun (i, mx1x, mx1y, my1x, my1y,
                                         mx2x, mx2y, my2x, my2y) ->
                        i, (mx1x, mx1y, my1x, my1y, mx2x, mx2y, my2x, my2y))
                    |> Map.ofList
                let mutable changed = false
                let cells' =
                    mc.Document.Cells
                    |> List.map (fun c ->
                        if c.Name <> cell then c
                        else
                            let elems' =
                                c.Elements
                                |> List.mapi (fun i el ->
                                    match Map.tryFind i bySource, el with
                                    | Some (mx1x, mx1y, my1x, my1y,
                                            mx2x, mx2y, my2x, my2y),
                                      Rkt.Types.RectEl r ->
                                        changed <- true
                                        let r' =
                                            { r with
                                                X1 = r.X1 + mx1x * dxDbu + mx1y * dyDbu
                                                Y1 = r.Y1 + my1x * dxDbu + my1y * dyDbu
                                                X2 = r.X2 + mx2x * dxDbu + mx2y * dyDbu
                                                Y2 = r.Y2 + my2x * dxDbu + my2y * dyDbu }
                                        Rkt.Types.RectEl r'
                                    | _ -> el)
                            { c with Elements = elems' })
                if not changed && extensions.IsEmpty then model, Cmd.none
                else
                    // Reap stale `viz:bridge`-tagged rects ONLY when
                    // their tag value matches one of this commit's
                    // new bridges (same owning position). A drag at
                    // the top corner shouldn't wipe bridges at the
                    // bottom corner.
                    let bridgeTagOf (r: Rkt.Types.Rectangle) : string option =
                        r.Props
                        |> List.tryPick (fun p ->
                            if p.Key = "viz:bridge" then
                                match p.Value with
                                | Rkt.Types.PvString s -> Some s
                                | Rkt.Types.PvAtom s -> Some s
                                | _ -> None
                            else None)
                    let newTags =
                        extensions
                        |> List.choose bridgeTagOf
                        |> Set.ofList
                    let cells'' =
                        cells'
                        |> List.map (fun c ->
                            if c.Name <> cell then c
                            else
                                let kept =
                                    c.Elements
                                    |> List.filter (fun el ->
                                        match el with
                                        | Rkt.Types.RectEl r ->
                                            match bridgeTagOf r with
                                            | Some tag when newTags.Contains tag ->
                                                false
                                            | _ -> true
                                        | _ -> true)
                                let extEls =
                                    extensions
                                    |> List.map Rkt.Types.RectEl
                                { c with Elements = kept @ extEls })
                    let lib' = { mc.Document with Cells = cells'' }
                    let flat' = Layout.Flatten.flatten lib'
                    let inst' = Layout.Instances.enumerate lib'
                    Rekolektion.Viz.App.Services.Logger.log "route.emit"
                        {| op = "slide-commit"
                           cell = cell
                           dxDbu = dxDbu
                           dyDbu = dyDbu
                           rectsChanged = adjusts.Length
                           extensions = extensions.Length |}
                    // Track the new active path: markDirty may
                    // retarget Path from foo.rkt to foo_edited.rkt
                    // on the first edit; without updating
                    // ActiveMacroPath the active-macro lookup would
                    // then fail and the canvas would render empty.
                    let mutable activePath' = mc.Path
                    let openMacros' =
                        model.OpenMacros
                        |> List.map (fun m ->
                            if m.Path <> mc.Path then m
                            else
                                let mc' =
                                    EditSession.pushUndoSnapshot mc
                                    |> fun mc'' ->
                                        { mc'' with
                                            Document = lib'
                                            FlatPolygons = flat'
                                            TopInstances = inst' }
                                    |> EditSession.markDirty
                                activePath' <- mc'.Path
                                mc')
                    { model with
                        OpenMacros = openMacros'
                        ActiveMacroPath = Some activePath' }, Cmd.none
    | Msg.ToggleEditRoutingMode ->
        let next = not model.EditRoutingMode
        Rekolektion.Viz.App.Services.Logger.log "route.tool"
            {| op = "mode"; on = next |}
        { model with EditRoutingMode = next }, Cmd.none
    | Msg.ToggleRoutingMode ->
        let next = not model.RoutingMode
        Rekolektion.Viz.App.Services.Logger.log "route.tool"
            {| op = "wire-mode"; on = next |}
        // Turning the tool off also aborts any in-flight draft so a
        // user toggling out can't leave a half-drawn route hanging.
        { model with
            RoutingMode = next
            DraftRoute = if next then model.DraftRoute else None }, Cmd.none
    | Msg.StartRoute (layer, width, x, y) ->
        match model.ActiveMacroPath with
        | None -> model, Cmd.none
        | Some _ ->
            let draft = Routing.Draft.start layer width (x, y)
            { model with DraftRoute = Some draft }, Cmd.none
    | Msg.RouteMouseMove (x, y) ->
        match model.DraftRoute with
        | None -> model, Cmd.none
        | Some d ->
            { model with DraftRoute = Some (Routing.Draft.setCursor (x, y) d) }, Cmd.none
    | Msg.RouteFixSegment ->
        match model.DraftRoute with
        | None -> model, Cmd.none
        | Some d ->
            { model with DraftRoute = Some (Routing.Draft.fix d) }, Cmd.none
    | Msg.RouteBackspace ->
        match model.DraftRoute with
        | None -> model, Cmd.none
        | Some d ->
            { model with DraftRoute = Some (Routing.Draft.pop d) }, Cmd.none
    | Msg.RouteFlipPosture ->
        match model.DraftRoute with
        | None -> model, Cmd.none
        | Some d ->
            { model with DraftRoute = Some (Routing.Draft.flipPosture d) }, Cmd.none
    | Msg.RouteAbort ->
        { model with DraftRoute = None }, Cmd.none
    | Msg.RouteFinish ->
        commitRouteWith model Routing.Draft.finishSegments
    | Msg.RouteStop ->
        // Esc — commit only the FIXED corners (no tentative L from
        // wherever the cursor was when Esc was pressed). For an
        // in-flight route whose only segments are tentative (user
        // clicked once then hit Esc), this yields no rect to commit
        // and we just clear the draft.
        //
        // Cursor must be cleared BEFORE the commit so endpoint pads
        // land at the last fixed point, not wherever the cursor was
        // at the moment of Esc.
        let cleared =
            match model.DraftRoute with
            | Some d -> { model with DraftRoute = Some { d with Cursor = None } }
            | None -> model
        commitRouteWith cleared Routing.Draft.fixedSegments
    | Msg.ToggleTightenMode ->
        // Toggle on / off. Entering with an empty selection is
        // a no-op (nothing to compute candidates against).
        if model.TightenMode then
            { model with TightenMode = false }, Cmd.none
        elif model.InstanceSelection.IsEmpty then
            model, Cmd.none
        else
            { model with TightenMode = true }, Cmd.none
    | Msg.CommitTighten index ->
        if not model.TightenMode || model.InstanceSelection.IsEmpty then
            model, Cmd.none
        else
            match model.ActiveMacroPath with
            | None -> { model with TightenMode = false }, Cmd.none
            | Some path ->
                let mutable activePath' = path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun mc ->
                        if mc.Path <> path then mc
                        else
                            // MUST mirror the render-side
                            // computation in GdsCanvasControl
                            // exactly: same selected polys, same
                            // other polys (other-instance flatten
                            // + top-cell direct paint). If the two
                            // sides disagree on what's a neighbor,
                            // the candidate list re-derived here
                            // has different ordering than what the
                            // user saw — so the index they clicked
                            // points at the wrong candidate and
                            // the commit translates in the wrong
                            // direction.
                            let selectedPolys =
                                mc.TopInstances
                                |> Array.filter (fun i -> model.InstanceSelection.Contains i.Index)
                                |> Array.collect (fun i ->
                                    Layout.Flatten.flattenInstance mc.Document i.Index)
                            let otherInstancePolys =
                                mc.TopInstances
                                |> Array.filter (fun i -> not (model.InstanceSelection.Contains i.Index))
                                |> Array.collect (fun i ->
                                    Layout.Flatten.flattenInstance mc.Document i.Index)
                            let topCellDirectPolys =
                                Layout.Flatten.flattenTopCellDirect mc.Document
                            let otherPolys =
                                Array.append otherInstancePolys topCellDirectPolys
                            let candidates =
                                Drc.Check.tightenCandidates
                                    mc.Document.Units
                                    selectedPolys otherPolys
                            // `index` is the user-visible Slot
                            // (stable per direction: 1=R, 2=L,
                            // 3=D, 4=U), not an array position.
                            // Find the candidate with matching
                            // slot; absent direction = no-op.
                            match candidates |> Array.tryFind (fun c -> c.Slot = index) with
                            | None -> mc
                            | Some cand ->
                                let dxDbu = int64 cand.DirX * cand.SlackDbu
                                let dyDbu = int64 cand.DirY * cand.SlackDbu
                                // Use the *WithLabels variant so any
                                // top-cell label sitting inside a
                                // moved SRef's pre-move bbox travels
                                // with the SRef. Without this,
                                // Tighten was the lone SRef-move
                                // codepath that left labels behind —
                                // a label initially on the edge of
                                // its SRef's bbox could drift OUTSIDE
                                // after one Tighten step and stay
                                // permanently orphaned, since the
                                // bbox-containment heuristic has no
                                // memory of prior association.
                                let lib' =
                                    Layout.Instances.translateSelectionsWithLabels
                                        mc.Document
                                        model.InstanceSelection
                                        Set.empty
                                        dxDbu dyDbu
                                let flat' = Layout.Flatten.flatten lib'
                                let inst' = Layout.Instances.enumerate lib'
                                let mc' =
                                    EditSession.pushUndoSnapshot mc
                                    |> fun m ->
                                        { m with
                                            Document = lib'
                                            FlatPolygons = flat'
                                            TopInstances = inst' }
                                    |> EditSession.markDirty
                                activePath' <- mc'.Path
                                mc')
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = Some activePath'
                    TightenMode = false }, Cmd.none
    | Msg.RotateSelection90
    | Msg.MirrorSelectionX
    | Msg.MirrorSelectionY ->
        // Unified: rotates / mirrors BOTH the SRef selection AND
        // the polygon selection around the same pivot (union bbox
        // centroid). A mixed selection rotates as one rigid group.
        let instSel = model.InstanceSelection
        let polySel = polyKeyTuples model.Selection
        if instSel.IsEmpty && polySel.IsEmpty then model, Cmd.none
        else
            match model.ActiveMacroPath with
            | None -> model, Cmd.none
            | Some path ->
                let mutable activePath' = path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun mc ->
                        if mc.Path <> path then mc
                        else
                            let selectedInsts =
                                mc.TopInstances
                                |> Array.filter (fun i -> instSel.Contains i.Index)
                            match Layout.Instances.selectionsPivotSnapped
                                    mc.Document selectedInsts polySel with
                            | None -> mc
                            | Some pivot ->
                                let lib' =
                                    match msg with
                                    | Msg.RotateSelection90 ->
                                        mc.Document
                                        |> fun d -> Layout.Instances.rotate90Selection d instSel pivot
                                        |> fun d -> Layout.Instances.rotate90Polygons d polySel pivot
                                    | Msg.MirrorSelectionX ->
                                        mc.Document
                                        |> fun d -> Layout.Instances.mirrorXSelection d instSel pivot
                                        |> fun d -> Layout.Instances.mirrorXPolygons d polySel pivot
                                    | _ ->
                                        mc.Document
                                        |> fun d -> Layout.Instances.mirrorYSelection d instSel pivot
                                        |> fun d -> Layout.Instances.mirrorYPolygons d polySel pivot
                                let flat' = Layout.Flatten.flatten lib'
                                let inst' = Layout.Instances.enumerate lib'
                                let mc' =
                                    EditSession.pushUndoSnapshot mc
                                    |> fun m ->
                                        { m with
                                            Document = lib'
                                            FlatPolygons = flat'
                                            TopInstances = inst' }
                                    |> EditSession.markDirty
                                activePath' <- mc'.Path
                                mc')
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = Some activePath' }, Cmd.none
    | Msg.DuplicateSelection ->
        let instSel = model.InstanceSelection
        let polySel = polyKeyTuples model.Selection
        if instSel.IsEmpty && polySel.IsEmpty then model, Cmd.none
        else
            match model.ActiveMacroPath with
            | None -> model, Cmd.none
            | Some path ->
                // Snap the duplicate offset to the SKY130 mfg grid
                // so clones land on-grid even if the source's bbox
                // width doesn't divide evenly.
                let mutable nextInstSel : Set<int> = instSel
                let mutable nextPolySel : Set<Layout.Flatten.PolyKey> =
                    model.Selection
                let mutable activePath' = path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun mc ->
                        if mc.Path <> path then mc
                        else
                            // Offset = union-bbox width (over both
                            // selected SRefs AND selected polys) + a
                            // small gap so duplicates clearly sit
                            // beside the originals, not on top.
                            let selectedInsts =
                                mc.TopInstances
                                |> Array.filter (fun i ->
                                    instSel.Contains i.Index)
                            let bb =
                                Layout.Instances.selectionsBbox
                                    mc.Document selectedInsts polySel
                            let dxRaw, dyRaw =
                                match bb with
                                | Some (x1, _, x2, _) ->
                                    let w = x2 - x1
                                    // 5 % gap or 1 DBU minimum.
                                    let gap = max 1L (w / 20L)
                                    w + gap, 0L
                                | None -> 0L, 0L
                            let dx, dy =
                                Layout.Snap.snapDeltaDbu
                                    mc.Document.Units
                                    Layout.Snap.sky130MfgGridNm
                                    dxRaw dyRaw
                            let lib', instClones, polyClones =
                                Layout.Instances.duplicateSelections
                                    mc.Document instSel polySel dx dy
                            let flat' = Layout.Flatten.flatten lib'
                            let inst' = Layout.Instances.enumerate lib'
                            nextInstSel <- instClones
                            // duplicateSelections returns clones as
                            // (cell, idx) tuples; wrap each in a
                            // PolyKey (clones are direct top-cell
                            // elements so TopInstance = None).
                            nextPolySel <-
                                polyClones
                                |> Set.map (fun (c, i) ->
                                    ({ Cell = c; Index = i; TopInstance = None }
                                     : Layout.Flatten.PolyKey))
                            let mc' =
                                EditSession.pushUndoSnapshot mc
                                |> fun m ->
                                    { m with
                                        Document = lib'
                                        FlatPolygons = flat'
                                        TopInstances = inst' }
                                |> EditSession.markDirty
                            activePath' <- mc'.Path
                            mc')
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = Some activePath'
                    InstanceSelection = nextInstSel
                    Selection = nextPolySel }, Cmd.none
    | Msg.SetInstanceSelection indices ->
        let ratlines' =
            if indices.IsEmpty then model.SelectedRatlines
            else Set.empty
        { model with
            InstanceSelection = indices
            SelectedRatlines = ratlines' }, Cmd.none
    | Msg.ClearInstanceSelection ->
        { model with InstanceSelection = Set.empty }, Cmd.none
    | Msg.MoveSelectionDbu (dxDbu, dyDbu) ->
        // Unified: translates BOTH the SRef selection AND the
        // polygon selection by the same delta. The user can
        // build a mixed selection (shift-click instance + shift-
        // click polygon) and drag it as one — clicking on either
        // an SRef or a polygon dispatches this Msg via the
        // canvas, which always passes both sets through.
        let instSel = model.InstanceSelection
        let polySel = polyKeyTuples model.Selection
        if (dxDbu = 0L && dyDbu = 0L)
           || (instSel.IsEmpty && polySel.IsEmpty) then
            model, Cmd.none
        else
            match model.ActiveMacroPath with
            | None -> model, Cmd.none
            | Some path ->
                let mutable activePath' = path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun mc ->
                        if mc.Path <> path then mc
                        else
                            // SRef + poly translate (each with their
                            // anchored labels) in one composed pass
                            // via the shared Layout.Instances helper.
                            // Same code path the canvas live preview
                            // uses, so post-commit matches mid-drag.
                            let lib' =
                                Layout.Instances.translateSelectionsWithLabels
                                    mc.Document instSel polySel dxDbu dyDbu
                            let flat' = Layout.Flatten.flatten lib'
                            let inst' = Layout.Instances.enumerate lib'
                            let mc' =
                                EditSession.pushUndoSnapshot mc
                                |> fun m ->
                                    { m with
                                        Document = lib'
                                        FlatPolygons = flat'
                                        TopInstances = inst' }
                                |> EditSession.markDirty
                            activePath' <- mc'.Path
                            mc')
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = Some activePath' }, Cmd.none
    | Msg.DeleteSelection ->
        // Combine polygon Selection (cell, idx) + InstanceSelection
        // (idx in top cell). Both sets are dropped post-delete.
        // No-op when nothing's selected.
        let polySel = polyKeyTuples model.Selection
        let instSel = model.InstanceSelection
        if polySel.IsEmpty && instSel.IsEmpty then model, Cmd.none
        else
            match model.ActiveMacroPath with
            | None -> model, Cmd.none
            | Some path ->
                let updateDoc (doc: Rekolektion.Viz.Core.Rkt.Types.Document) =
                    let topName =
                        (Rekolektion.Viz.Core.Layout.Flatten.findTop doc).Name
                    // Per-cell deletion set: poly Selection grouped
                    // by cell, plus InstanceSelection lifted into
                    // the top cell's bucket.
                    let perCell =
                        let m = System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<int>>()
                        for (sname, idx) in polySel do
                            match m.TryGetValue sname with
                            | true, set -> set.Add idx |> ignore
                            | _ ->
                                let set = System.Collections.Generic.HashSet<int>()
                                set.Add idx |> ignore
                                m.[sname] <- set
                        if not instSel.IsEmpty then
                            match m.TryGetValue topName with
                            | true, set ->
                                for idx in instSel do set.Add idx |> ignore
                            | _ ->
                                let set = System.Collections.Generic.HashSet<int>()
                                for idx in instSel do set.Add idx |> ignore
                                m.[topName] <- set
                        m
                    let updatedCells =
                        doc.Cells
                        |> List.map (fun c ->
                            match perCell.TryGetValue c.Name with
                            | false, _ -> c
                            | true, deleting ->
                                // Find labels anchored to a deleted
                                // element so they go too — same
                                // anchor rule as move/resize.
                                let anchorMap = anchorMapForCell c
                                let elems' =
                                    c.Elements
                                    |> List.mapi (fun i el -> i, el)
                                    |> List.filter (fun (i, el) ->
                                        if deleting.Contains i then false
                                        else
                                            match el with
                                            | Rekolektion.Viz.Core.Rkt.Types.LabelEl _ ->
                                                match Map.tryFind i anchorMap with
                                                | Some anchorIdx ->
                                                    not (deleting.Contains anchorIdx)
                                                | None -> true
                                            | _ -> true)
                                    |> List.map snd
                                { c with Elements = elems' })
                    { doc with Cells = updatedCells }
                let mutable activePath' = path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun mc ->
                        if mc.Path <> path then mc
                        else
                            let lib' = updateDoc mc.Document
                            let flat' = Layout.Flatten.flatten lib'
                            let inst' = Layout.Instances.enumerate lib'
                            let mc' =
                                EditSession.pushUndoSnapshot mc
                                |> fun m ->
                                    { m with
                                        Document = lib'
                                        FlatPolygons = flat'
                                        TopInstances = inst' }
                                |> EditSession.markDirty
                            activePath' <- mc'.Path
                            mc')
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = Some activePath'
                    Selection = Set.empty
                    InstanceSelection = Set.empty }, Cmd.none
    | Msg.MovePolygonDbu (sname, idx, dxDbu, dyDbu) ->
        let key : Layout.Flatten.PolyKey =
            { Cell = sname; Index = idx; TopInstance = None }
        model, Cmd.ofMsg (Msg.MovePolygonsDbu (Set.singleton key, dxDbu, dyDbu))
    | Msg.MovePolygonsDbu (sel, dxDbu, dyDbu) ->
        // Mixed-selection drag: also translate any selected SRefs
        // by the same delta so a poly+instance selection moves as
        // one. The canvas's PolygonDrag commit dispatches this Msg
        // either as a singleton (one poly clicked) or as the full
        // SelectedPolygons set; the InstanceSelection passenger
        // rides along.
        let polySel = polyKeyTuples sel
        let instSel = model.InstanceSelection
        if (dxDbu = 0L && dyDbu = 0L)
           || (polySel.IsEmpty && instSel.IsEmpty) then
            model, Cmd.none
        else
            match model.ActiveMacroPath with
            | None -> model, Cmd.none
            | Some path ->
                let mutable activePath' = path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun mc ->
                        if mc.Path <> path then mc
                        else
                            let lib' =
                                Layout.Instances.translateSelectionsWithLabels
                                    mc.Document instSel polySel dxDbu dyDbu
                            let flat' = Layout.Flatten.flatten lib'
                            let inst' = Layout.Instances.enumerate lib'
                            let mc' =
                                EditSession.pushUndoSnapshot mc
                                |> fun m ->
                                    { m with
                                        Document = lib'
                                        FlatPolygons = flat'
                                        TopInstances = inst' }
                                |> EditSession.markDirty
                            activePath' <- mc'.Path
                            mc')
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = Some activePath' }, Cmd.none
    | Msg.ResizePolygonBbox (sname, idx, nxMin, nyMin, nxMax, nyMax) ->
        if nxMax <= nxMin || nyMax <= nyMin then model, Cmd.none
        else
            match model.ActiveMacroPath with
            | None -> model, Cmd.none
            | Some path ->
                let updateDoc (doc: Rekolektion.Viz.Core.Rkt.Types.Document) =
                    let updatedCells =
                        doc.Cells
                        |> List.map (fun c ->
                            if c.Name <> sname then c
                            else
                                // Pre-resize bbox of the target
                                // element. Used both for poly-point
                                // lerp and for label-origin lerp.
                                let oldBbox =
                                    if idx < 0 || idx >= c.Elements.Length then None
                                    else elementBbox c.Elements.[idx]
                                let anchorMap = anchorMapForCell c
                                let lerp (oxMin, oyMin, oxMax, oyMax) (x: int64) (y: int64) =
                                    let oldW = max 1L (oxMax - oxMin)
                                    let oldH = max 1L (oyMax - oyMin)
                                    let newW = nxMax - nxMin
                                    let newH = nyMax - nyMin
                                    nxMin + (x - oxMin) * newW / oldW,
                                    nyMin + (y - oyMin) * newH / oldH
                                let elems' =
                                    c.Elements
                                    |> List.mapi (fun i el ->
                                        if i = idx then
                                            match el, oldBbox with
                                            | Rekolektion.Viz.Core.Rkt.Types.PolyEl p, Some bb when not p.Points.IsEmpty ->
                                                let pts' =
                                                    p.Points
                                                    |> List.map (fun (pt: Rekolektion.Viz.Core.Rkt.Types.Point) ->
                                                        let nx, ny = lerp bb pt.X pt.Y
                                                        ({ X = nx; Y = ny }
                                                         : Rekolektion.Viz.Core.Rkt.Types.Point))
                                                Rekolektion.Viz.Core.Rkt.Types.PolyEl
                                                    { p with Points = pts' }
                                            | Rekolektion.Viz.Core.Rkt.Types.RectEl r, _ ->
                                                Rekolektion.Viz.Core.Rkt.Types.RectEl
                                                    { r with
                                                        X1 = nxMin; Y1 = nyMin
                                                        X2 = nxMax; Y2 = nyMax }
                                            | other, _ -> other
                                        else
                                            // Lerp the origin of any
                                            // label anchored to the
                                            // resized element so a
                                            // centered label stays
                                            // centered and an off-
                                            // center label keeps its
                                            // proportional position.
                                            match el, oldBbox, Map.tryFind i anchorMap with
                                            | Rekolektion.Viz.Core.Rkt.Types.LabelEl l, Some bb, Some anchorIdx
                                                    when anchorIdx = idx ->
                                                let nx, ny = lerp bb l.Origin.X l.Origin.Y
                                                Rekolektion.Viz.Core.Rkt.Types.LabelEl
                                                    { l with
                                                        Origin =
                                                            ({ X = nx; Y = ny }
                                                             : Rekolektion.Viz.Core.Rkt.Types.Point) }
                                            | _ -> el)
                                { c with Elements = elems' })
                    { doc with Cells = updatedCells }
                let mutable activePath' = path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun mc ->
                        if mc.Path <> path then mc
                        else
                            let lib' = updateDoc mc.Document
                            let flat' = Layout.Flatten.flatten lib'
                            let inst' = Layout.Instances.enumerate lib'
                            let mc' =
                                EditSession.pushUndoSnapshot mc
                                |> fun m ->
                                    { m with
                                        Document = lib'
                                        FlatPolygons = flat'
                                        TopInstances = inst' }
                                |> EditSession.markDirty
                            activePath' <- mc'.Path
                            mc')
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = Some activePath' }, Cmd.none
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
    | Msg.UndoActiveMacro ->
        match Model.activeMacro model with
        | None -> model, Cmd.none
        | Some mc ->
            match mc.UndoStack with
            | [] -> model, Cmd.none
            | prevLib :: rest ->
                let flat' = Layout.Flatten.flatten prevLib
                let inst' = Layout.Instances.enumerate prevLib
                let stillDirty = not (List.isEmpty rest)
                // When the stack drains we're back at the load
                // state — also revert the in-memory Path from
                // `<base>_edited.<ext>` back to the original so
                // the tab name no longer says "edited" and a
                // following Save would write to the original file
                // again. (If the user explicitly renamed the tab
                // away from the auto-suggested `_edited` path,
                // that rename stays — we only revert the
                // automatic retarget, not user intent.)
                let pathRestored =
                    if stillDirty then mc.Path
                    elif EditSession.isAutoSuggestedEditedPath mc.OriginalPath mc.Path then
                        mc.OriginalPath
                    else mc.Path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun m ->
                        if m.Path <> mc.Path then m
                        else
                            { m with
                                Document = prevLib
                                FlatPolygons = flat'
                                TopInstances = inst'
                                UndoStack = rest
                                // Push the CURRENT (pre-undo)
                                // document onto the redo stack so
                                // Cmd+Shift+Z can put it back.
                                RedoStack = mc.Document :: mc.RedoStack
                                Dirty = stillDirty
                                Path = pathRestored })
                let activePath' =
                    if model.ActiveMacroPath = Some mc.Path then Some pathRestored
                    else model.ActiveMacroPath
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = activePath' }, Cmd.none
    | Msg.RedoActiveMacro ->
        match Model.activeMacro model with
        | None -> model, Cmd.none
        | Some mc ->
            match mc.RedoStack with
            | [] -> model, Cmd.none
            | nextLib :: rest ->
                let flat' = Layout.Flatten.flatten nextLib
                let inst' = Layout.Instances.enumerate nextLib
                // Re-applying a redone edit makes the doc dirty
                // again. If the user undid all the way back to the
                // load state (Path got restored to original), redo
                // also re-applies the auto-suggested edited path.
                let editedPath =
                    if mc.Path = mc.OriginalPath then
                        EditSession.suggestEditedPath mc.OriginalPath
                    else mc.Path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun m ->
                        if m.Path <> mc.Path then m
                        else
                            { m with
                                Document = nextLib
                                FlatPolygons = flat'
                                TopInstances = inst'
                                // Current doc goes back on the undo
                                // stack so a follow-up Cmd+Z works.
                                UndoStack = mc.Document :: mc.UndoStack
                                RedoStack = rest
                                Dirty = true
                                Path = editedPath })
                let activePath' =
                    if model.ActiveMacroPath = Some mc.Path then Some editedPath
                    else model.ActiveMacroPath
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = activePath' }, Cmd.none
    | Msg.SaveActiveMacro ->
        match Model.activeMacro model with
        | None -> model, Cmd.none
        | Some mc ->
            let cmd =
                Cmd.OfAsync.either
                    backend.SaveMacro mc
                    (function
                        | Ok p -> Msg.SaveCompleted p
                        | Error r -> Msg.SaveFailed r)
                    (fun ex -> Msg.SaveFailed ex.Message)
            model, cmd
    | Msg.SaveActiveMacroAs target ->
        match Model.activeMacro model with
        | None -> model, Cmd.none
        | Some mc ->
            // SaveAs retargets the macro's Path to the chosen path
            // first, then runs the same async save. The Path
            // retarget makes the writer read the *current* file
            // (mc.Path holds the latest saved-or-edit-copy state)
            // and write to `target`. After completion the active
            // path snaps to `target` via SaveCompleted.
            let openMacros' =
                model.OpenMacros
                |> List.map (fun m ->
                    if m.Path = mc.Path then { m with Path = target }
                    else m)
            let mc' = { mc with Path = target }
            let activePath' = Some target
            let cmd =
                Cmd.OfAsync.either
                    backend.SaveMacro mc'
                    (function
                        | Ok p -> Msg.SaveCompleted p
                        | Error r -> Msg.SaveFailed r)
                    (fun ex -> Msg.SaveFailed ex.Message)
            { model with
                OpenMacros = openMacros'
                ActiveMacroPath = activePath' }, cmd
    | Msg.BeginRenameTab path ->
        { model with RenamingPath = Some path }, Cmd.none
    | Msg.CancelRenameTab ->
        { model with RenamingPath = None }, Cmd.none
    | Msg.CommitRenameTab (oldPath, newName) ->
        // Guard against stale commits: Esc clears RenamingPath
        // before TextBox.LostFocus fires its own commit. Without
        // this check, the LostFocus dispatch would undo Esc.
        if model.RenamingPath <> Some oldPath then model, Cmd.none
        else
        let trimmed = newName.Trim()
        if trimmed = "" then
            // Empty name → cancel.
            { model with RenamingPath = None }, Cmd.none
        elif trimmed.Contains "/" || trimmed.Contains "\\" then
            // No path separators in a tab rename; user can use
            // SaveAs for a directory move.
            appendLog "rename: name may not contain path separators"
                { model with RenamingPath = None }, Cmd.none
        else
            let dir = System.IO.Path.GetDirectoryName oldPath
            // Preserve the source's extension. The old code hardcoded
            // `.mag` here from the days when Magic was the only
            // supported layout format — renaming a `.rkt` tab would
            // append `.mag` and produce `.rkt.mag` doubles on save.
            // User-typed extensions are honored when they match the
            // source format; cross-format renames are rejected
            // (saveTo would error out anyway, and it's almost always
            // a typo rather than an intentional format-convert).
            let srcExt =
                (System.IO.Path.GetExtension oldPath).ToLowerInvariant()
            let typedExt =
                (System.IO.Path.GetExtension trimmed).ToLowerInvariant()
            if typedExt <> "" && typedExt <> srcExt then
                appendLog
                    (sprintf
                        "rename: extension %s doesn't match source %s — use Save As to convert formats"
                        typedExt srcExt)
                    { model with RenamingPath = None }, Cmd.none
            else
            let withExt =
                if typedExt = "" then trimmed + srcExt
                else trimmed
            let newPath = System.IO.Path.Combine(dir, withExt)
            if newPath = oldPath then
                { model with RenamingPath = None }, Cmd.none
            elif System.IO.File.Exists newPath then
                appendLog (sprintf "rename: target %s already exists" newPath)
                    { model with RenamingPath = None }, Cmd.none
            else
                // If the source exists on disk, do a real move;
                // otherwise the macro hasn't been saved yet and
                // we just retarget the in-memory Path.
                try
                    if System.IO.File.Exists oldPath then
                        System.IO.File.Move(oldPath, newPath)
                with ex ->
                    eprintfn "[viz] rename move failed: %s" ex.Message
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun m ->
                        if m.Path = oldPath then
                            // OriginalPath stays pinned at the
                            // original source so a later
                            // round-trip read still finds it. If
                            // the user renamed the original (rare),
                            // OriginalPath also retargets so the
                            // round-trip read works.
                            let newOriginal =
                                if m.OriginalPath = oldPath then newPath
                                else m.OriginalPath
                            { m with Path = newPath; OriginalPath = newOriginal }
                        else m)
                let activePath' =
                    match model.ActiveMacroPath with
                    | Some p when p = oldPath -> Some newPath
                    | other -> other
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = activePath'
                    RenamingPath = None }, Cmd.none
    | Msg.SaveCompleted writtenPath ->
        Rekolektion.Viz.App.Services.Logger.log "save"
            {| op = "ok"; path = writtenPath |}
        // Update the active macro: Path moves to the saved file
        // (no-op when already pointing there), Dirty clears.
        let openMacros' =
            model.OpenMacros
            |> List.map (fun mc ->
                if mc.Path = writtenPath
                   || (model.ActiveMacroPath = Some mc.Path
                       && mc.Path <> writtenPath) then
                    { mc with Path = writtenPath; Dirty = false }
                else mc)
        let activePath' =
            if model.ActiveMacroPath.IsSome then Some writtenPath
            else None
        // Push the saved path to Recents. First-time saves of an
        // opened file write to `<base>_edited.mag`; subsequent
        // saves stay at that path. Save As writes to a fresh
        // user-chosen path. Either way the resulting file is a
        // new artifact the user will want to reopen, so it joins
        // RecentFiles alongside the originals.
        let recents' =
            writtenPath :: (model.RecentFiles |> List.filter (fun p -> p <> writtenPath))
            |> List.truncate 10
        Rekolektion.Viz.App.Services.Recents.save recents'
        appendLog (sprintf "saved %s" writtenPath)
            { model with
                OpenMacros = openMacros'
                ActiveMacroPath = activePath'
                RecentFiles = recents' }, Cmd.none
    | Msg.SaveFailed reason ->
        Rekolektion.Viz.App.Services.Logger.log "save"
            {| op = "fail"; reason = reason |}
        appendLog (sprintf "save failed: %s" reason) model, Cmd.none
