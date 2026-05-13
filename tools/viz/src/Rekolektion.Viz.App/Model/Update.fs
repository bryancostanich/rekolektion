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
    DeriveNets: Rekolektion.Viz.Core.Gds.Types.Library
                  -> Async<Map<string, Rekolektion.Viz.Core.Sidecar.Types.NetEntry>>
    /// Round-trip the macro through `Mag.Writer.writeUpdated`,
    /// returning the path that ended up on disk.
    SaveMacro : Model.LoadedMacro -> Async<Result<string, string>>
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
                    backend.DeriveNets macro.Library
                    (fun nets -> Msg.NetsLoaded (macro.Path, nets))
                    (fun ex -> Msg.LogLine (sprintf "net derivation failed: %s" ex.Message))
        let model' =
            { model with
                OpenMacros = openMacros
                ActiveMacroPath = Some macro.Path
                RecentFiles = recents
                Toggle = toggle'
                Selection = Set.empty
                InstanceSelection = Set.empty }
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
                { model with
                    ActiveMacroPath = Some path
                    Selection = Set.empty
                    InstanceSelection = Set.empty }, Cmd.none
            else model, Cmd.none
    | Msg.CloseAllTabs ->
        { model with
            OpenMacros = []
            ActiveMacroPath = None
            Selection = Set.empty
            InstanceSelection = Set.empty
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
        let model' =
            { model with
                OpenMacros = remaining
                ActiveMacroPath = nextActive
                Selection = Set.empty
                InstanceSelection = Set.empty }
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
    | Msg.PolygonPicked (s, i) ->
        // Replace polygon selection with the single picked element.
        // Shift-click extension goes through SetPolygonSelection so
        // the canvas can compute the new set with the modifier in
        // hand.
        { model with Selection = Set.singleton (s, i) }, Cmd.none
    | Msg.SetPolygonSelection sel ->
        { model with Selection = sel }, Cmd.none
    | Msg.ClearSelection -> { model with Selection = Set.empty }, Cmd.none
    | Msg.ToggleDimensions ->
        { model with ShowDimensions = not model.ShowDimensions }, Cmd.none
    | Msg.ToggleDrc ->
        { model with ShowDrc = not model.ShowDrc }, Cmd.none
    | Msg.ToggleRatlines ->
        { model with ShowRatlines = not model.ShowRatlines }, Cmd.none
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
                            let selectedPolys =
                                mc.TopInstances
                                |> Array.filter (fun i -> model.InstanceSelection.Contains i.Index)
                                |> Array.collect (fun i ->
                                    Layout.Flatten.flattenInstance (Rkt.OfGds.fromLibrary mc.Library) i.Index)
                            let otherPolys =
                                mc.TopInstances
                                |> Array.filter (fun i -> not (model.InstanceSelection.Contains i.Index))
                                |> Array.collect (fun i ->
                                    Layout.Flatten.flattenInstance (Rkt.OfGds.fromLibrary mc.Library) i.Index)
                            let candidates =
                                Drc.Check.tightenCandidates
                                    mc.Library selectedPolys otherPolys
                            // index is 1-based per the user-
                            // visible numbered labels.
                            let i0 = index - 1
                            if i0 < 0 || i0 >= candidates.Length then mc
                            else
                                let cand = candidates.[i0]
                                let dxDbu = int64 cand.DirX * cand.SlackDbu
                                let dyDbu = int64 cand.DirY * cand.SlackDbu
                                let lib' =
                                    Layout.Instances.translateSelection
                                        mc.Library model.InstanceSelection dxDbu dyDbu
                                let flat' = Layout.Flatten.flatten (Rkt.OfGds.fromLibrary lib')
                                let inst' = Layout.Instances.enumerate lib'
                                let mc' =
                                    EditSession.pushUndoSnapshot mc
                                    |> fun m ->
                                        { m with
                                            Library = lib'
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
        if model.InstanceSelection.IsEmpty then model, Cmd.none
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
                            let selected =
                                mc.TopInstances
                                |> Array.filter (fun i ->
                                    model.InstanceSelection.Contains i.Index)
                            match Layout.Instances.selectionPivotSnapped
                                    mc.Library selected with
                            | None -> mc
                            | Some pivot ->
                                let lib' =
                                    match msg with
                                    | Msg.RotateSelection90 ->
                                        Layout.Instances.rotate90Selection
                                            mc.Library model.InstanceSelection pivot
                                    | Msg.MirrorSelectionX ->
                                        Layout.Instances.mirrorXSelection
                                            mc.Library model.InstanceSelection pivot
                                    | _ ->
                                        Layout.Instances.mirrorYSelection
                                            mc.Library model.InstanceSelection pivot
                                let flat' = Layout.Flatten.flatten (Rkt.OfGds.fromLibrary lib')
                                let inst' = Layout.Instances.enumerate lib'
                                let mc' =
                                    EditSession.pushUndoSnapshot mc
                                    |> fun m ->
                                        { m with
                                            Library = lib'
                                            FlatPolygons = flat'
                                            TopInstances = inst' }
                                    |> EditSession.markDirty
                                activePath' <- mc'.Path
                                mc')
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = Some activePath' }, Cmd.none
    | Msg.DuplicateSelection ->
        if model.InstanceSelection.IsEmpty then model, Cmd.none
        else
            match model.ActiveMacroPath with
            | None -> model, Cmd.none
            | Some path ->
                // Snap the duplicate offset to the SKY130 mfg grid
                // so clones land on-grid even if the source's bbox
                // width doesn't divide evenly.
                let mutable nextSelection : Set<int> = model.InstanceSelection
                let mutable activePath' = path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun mc ->
                        if mc.Path <> path then mc
                        else
                            // Offset = bbox-of-bboxes width + a
                            // small gap so duplicates clearly sit
                            // beside the originals, not on top.
                            let selected =
                                mc.TopInstances
                                |> Array.filter (fun i ->
                                    model.InstanceSelection.Contains i.Index)
                            let bb = Layout.Instances.selectionBbox selected
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
                                    (Layout.Snap.unitsOfLibrary mc.Library)
                                    Layout.Snap.sky130MfgGridNm
                                    dxRaw dyRaw
                            let lib', clones =
                                Layout.Instances.duplicateSelection
                                    mc.Library model.InstanceSelection dx dy
                            let flat' = Layout.Flatten.flatten (Rkt.OfGds.fromLibrary lib')
                            let inst' = Layout.Instances.enumerate lib'
                            nextSelection <- clones
                            let mc' =
                                EditSession.pushUndoSnapshot mc
                                |> fun m ->
                                    { m with
                                        Library = lib'
                                        FlatPolygons = flat'
                                        TopInstances = inst' }
                                |> EditSession.markDirty
                            activePath' <- mc'.Path
                            mc')
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = Some activePath'
                    InstanceSelection = nextSelection }, Cmd.none
    | Msg.SetInstanceSelection indices ->
        { model with InstanceSelection = indices }, Cmd.none
    | Msg.ClearInstanceSelection ->
        { model with InstanceSelection = Set.empty }, Cmd.none
    | Msg.MoveSelectionDbu (dxDbu, dyDbu) ->
        // No-op when nothing selected or the snapped delta is zero
        // — avoids a pointless re-flatten on a sub-grid drag.
        if model.InstanceSelection.IsEmpty || (dxDbu = 0L && dyDbu = 0L) then
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
                                Layout.Instances.translateSelection
                                    mc.Library model.InstanceSelection dxDbu dyDbu
                            let flat' = Layout.Flatten.flatten (Rkt.OfGds.fromLibrary lib')
                            let inst' = Layout.Instances.enumerate lib'
                            let mc' =
                                EditSession.pushUndoSnapshot mc
                                |> fun m ->
                                    { m with
                                        Library = lib'
                                        FlatPolygons = flat'
                                        TopInstances = inst' }
                                |> EditSession.markDirty
                            activePath' <- mc'.Path
                            mc')
                { model with
                    OpenMacros = openMacros'
                    ActiveMacroPath = Some activePath' }, Cmd.none
    | Msg.MovePolygonDbu (sname, idx, dxDbu, dyDbu) ->
        model, Cmd.ofMsg (Msg.MovePolygonsDbu (Set.singleton (sname, idx), dxDbu, dyDbu))
    | Msg.MovePolygonsDbu (sel, dxDbu, dyDbu) ->
        if (dxDbu = 0L && dyDbu = 0L) || sel.IsEmpty then model, Cmd.none
        else
            match model.ActiveMacroPath with
            | None -> model, Cmd.none
            | Some path ->
                // Group target indices by structure so we only walk
                // each structure's element list once.
                let perStruct =
                    sel
                    |> Set.toList
                    |> List.groupBy fst
                    |> List.map (fun (s, items) -> s, items |> List.map snd |> Set.ofList)
                    |> Map.ofList
                let translatePoly (pts: Rekolektion.Viz.Core.Gds.Types.Point list) =
                    pts
                    |> List.map (fun (p: Rekolektion.Viz.Core.Gds.Types.Point) ->
                        ({ X = p.X + dxDbu; Y = p.Y + dyDbu } : Rekolektion.Viz.Core.Gds.Types.Point))
                let updateLib (lib: Rekolektion.Viz.Core.Gds.Types.Library) =
                    let updated =
                        lib.Structures
                        |> List.map (fun s ->
                            match Map.tryFind s.Name perStruct with
                            | None -> s
                            | Some indices ->
                                let elems' =
                                    s.Elements
                                    |> List.mapi (fun i el ->
                                        if not (indices.Contains i) then el
                                        else
                                            match el with
                                            | Rekolektion.Viz.Core.Gds.Types.Boundary b ->
                                                Rekolektion.Viz.Core.Gds.Types.Boundary
                                                    { b with Points = translatePoly b.Points }
                                            | Rekolektion.Viz.Core.Gds.Types.Path p ->
                                                Rekolektion.Viz.Core.Gds.Types.Path
                                                    { p with Points = translatePoly p.Points }
                                            | other -> other)
                                { s with Elements = elems' })
                    { lib with Structures = updated }
                let mutable activePath' = path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun mc ->
                        if mc.Path <> path then mc
                        else
                            let lib' = updateLib mc.Library
                            let flat' = Layout.Flatten.flatten (Rkt.OfGds.fromLibrary lib')
                            let inst' = Layout.Instances.enumerate lib'
                            let mc' =
                                EditSession.pushUndoSnapshot mc
                                |> fun m ->
                                    { m with
                                        Library = lib'
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
                let flat' = Layout.Flatten.flatten (Rkt.OfGds.fromLibrary prevLib)
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
                    elif mc.Path = EditSession.suggestEditedPathFor mc.OriginalPath then
                        mc.OriginalPath
                    else mc.Path
                let openMacros' =
                    model.OpenMacros
                    |> List.map (fun m ->
                        if m.Path <> mc.Path then m
                        else
                            { m with
                                Library = prevLib
                                FlatPolygons = flat'
                                TopInstances = inst'
                                UndoStack = rest
                                Dirty = stillDirty
                                Path = pathRestored })
                let activePath' =
                    if model.ActiveMacroPath = Some mc.Path then Some pathRestored
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
            let withExt =
                if trimmed.EndsWith ".mag" then trimmed
                else trimmed + ".mag"
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
        appendLog (sprintf "saved %s" writtenPath)
            { model with
                OpenMacros = openMacros'
                ActiveMacroPath = activePath' }, Cmd.none
    | Msg.SaveFailed reason ->
        appendLog (sprintf "save failed: %s" reason) model, Cmd.none
