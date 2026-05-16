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

/// Canvas-side diagnostic for the routing hover hit-test. Takes
/// screen pixel (x, y) on the 3D canvas and returns a JSON string
/// with the full ray / per-layer intersection / route-detection
/// trace. Registered by the StackCanvasControl on attach. Used by
/// the `/route/diagnose-hover` UDS endpoint + the
/// `rekolektion_viz_hover_at` MCP tool so an agent can probe
/// hit-test behaviour without manually moving the cursor.
let mutable diagnoseRoutingHover : (float * float -> string) option = None

/// Synthesize a full route-slide gesture (press → release) at the
/// given screen pixels. Lets an agent test the edit pipeline end
/// to end without OS pointer events. Args:
/// (startX, startY, endX, endY) — both screen pixels on the 3D
/// canvas. Returns a JSON description of what handle was hit, what
/// adjusts were built, and what delta was committed.
let mutable simulateRouteDrag :
    (float * float * float * float -> string) option = None
