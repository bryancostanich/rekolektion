module Rekolektion.Viz.App.Services.AppDispatch

open Rekolektion.Viz.App.Model

/// Shared dispatcher + ambient model snapshots used by code that
/// can't reach the FuncUI tree directly — native-menu items,
/// out-of-tree services (CommandListener, ScreenshotListener),
/// the headless render harness. Wired up by App.fs's
/// `Subscriptions.syncDispatch`; AppView updates the snapshot on
/// every render tick.
let mutable current : (Msg.Msg -> unit) option = None

let send (msg: Msg.Msg) : unit =
    match current with
    | Some d -> d msg
    | None   -> ()

/// Latest known active-macro path. Read by `File → Save As...`
/// and any other handler that needs to root a file picker at the
/// current working file.
let mutable currentActivePath : string option = None

/// Latest model snapshot — published by AppView on every render.
/// Read by out-of-tree query endpoints (CommandListener
/// `/instances`) so they can serve the current state without
/// piping the model through an extra channel. None until the
/// first render fires.
let mutable currentModel : Model.Model option = None
