/// In-session guideline storage for the viewport rulers.
///
/// Two layers:
///   * `Guides` module — pure value-type state (Guide list +
///     in-flight Drag option) and the transitions between states.
///     Every interesting transition is exposed as a pure function
///     so the headless tests can exercise creation, move, commit,
///     discard, and delete without any singleton plumbing.
///   * `GuidesService` module — singleton wrapper that holds the
///     latest state and fires a `Changed` event so the canvas and
///     the ruler controls can `InvalidateVisual` on every commit
///     or in-flight tick. The whole app shares one instance —
///     guides are global to the viz session (not per-document)
///     for v1; per-document storage can come later when (and if)
///     the user wants it.
namespace Rekolektion.Viz.App.Services

module Guides =

    /// Horizontal guide = one Y world DBU, drawn across the full
    /// viewport width. Vertical = one X DBU, drawn across the
    /// full height.
    type Orientation = Horizontal | Vertical

    /// A committed guideline. `Id` is the stable handle that
    /// the canvas's hit-test returns; `CoordDbu` is the world
    /// DBU position along the perpendicular axis (Y for
    /// horizontal guides, X for vertical).
    type Guide = {
        Id          : int
        Orientation : Orientation
        CoordDbu    : int64
    }

    /// In-flight drag. `MovingId = Some` when grabbing an
    /// existing guide; `None` when creating a fresh one from
    /// the ruler.
    type Drag = {
        Orientation : Orientation
        CoordDbu    : int64
        MovingId    : int option
    }

    /// Whole guides-state snapshot. `NextId` is the autoincrement
    /// counter so committed guides get stable identities even
    /// after deletes shift the list.
    type State = {
        Guides : Guide list
        Drag   : Drag option
        NextId : int
    }

    let empty : State = { Guides = []; Drag = None; NextId = 1 }

    /// Begin a guide drag. `movingId = None` creates a new guide;
    /// `Some id` reassigns the existing guide's position to the
    /// drag's coord on commit (delete-by-drag drops it instead).
    let startDrag
            (orientation: Orientation) (coordDbu: int64)
            (movingId: int option) (s: State) : State =
        { s with Drag = Some { Orientation = orientation
                               CoordDbu    = coordDbu
                               MovingId    = movingId } }

    /// Update the in-flight drag's coord. No-op when no drag is
    /// in flight (e.g. a stray PointerMoved after Release).
    let updateDrag (coordDbu: int64) (s: State) : State =
        match s.Drag with
        | Some d -> { s with Drag = Some { d with CoordDbu = coordDbu } }
        | None -> s

    /// Discard the in-flight drag without committing or deleting.
    /// On a move-drag, the moved guide stays at its pre-drag
    /// position (which lives in `Guides`, unchanged through the
    /// drag — the in-flight Drag holds the new candidate coord,
    /// not the source position).
    let cancelDrag (s: State) : State =
        { s with Drag = None }

    /// Commit the in-flight drag.
    ///   * Creating (`MovingId = None`) → assign `NextId`, push
    ///     the new guide onto the head of the list, bump `NextId`.
    ///   * Moving (`MovingId = Some id`) → overwrite the matching
    ///     guide's `CoordDbu`. If the id isn't found (unexpected
    ///     — guide deleted between press and release?), the list
    ///     is left untouched and the drag clears.
    /// No-op when nothing is in flight.
    let commitDrag (s: State) : State =
        match s.Drag with
        | None -> s
        | Some d ->
            match d.MovingId with
            | None ->
                let g = { Id          = s.NextId
                          Orientation = d.Orientation
                          CoordDbu    = d.CoordDbu }
                { s with Guides = g :: s.Guides
                         NextId = s.NextId + 1
                         Drag   = None }
            | Some id ->
                let guides' =
                    s.Guides
                    |> List.map (fun g ->
                        if g.Id = id then { g with CoordDbu = d.CoordDbu }
                        else g)
                { s with Guides = guides'; Drag = None }

    /// Drag-off-canvas-deletes-it.
    ///   * On a move-drag: remove the guide whose id was being
    ///     moved.
    ///   * On a creating-drag: equivalent to cancel.
    let deleteByDrag (s: State) : State =
        match s.Drag with
        | None -> s
        | Some d ->
            match d.MovingId with
            | Some id ->
                { s with Guides = s.Guides |> List.filter (fun g -> g.Id <> id)
                         Drag   = None }
            | None ->
                { s with Drag = None }

    /// Remove every guide + cancel any in-flight drag. For the
    /// "Clear Guides" command (and the MCP `clear_guides` tool).
    let clearAll (s: State) : State =
        { s with Guides = []; Drag = None }

    // ─────────────────────────────────────────────────────────────
    // Programmatic / MCP-side transitions. These bypass the drag
    // flow entirely: an external caller (MCP, future scripting
    // API, etc.) doesn't have a press / move / release rhythm to
    // map onto, so they get direct create / move / remove that
    // each fires the `Changed` event exactly once.
    // ─────────────────────────────────────────────────────────────

    /// Create a guide with the next autoassigned `Id`. Returns
    /// the new state + the assigned id so the caller can refer
    /// to the guide later (e.g. for `setGuideCoord` / `removeGuide`).
    let addGuide
            (orientation: Orientation) (coordDbu: int64)
            (s: State) : State * int =
        let id = s.NextId
        let g = { Id = id; Orientation = orientation; CoordDbu = coordDbu }
        { s with Guides = g :: s.Guides; NextId = s.NextId + 1 }, id

    /// Move a guide by id. Returns the new state + whether the
    /// id was found, so callers can surface "no such guide"
    /// errors without re-scanning the list.
    let setGuideCoord
            (id: int) (coordDbu: int64) (s: State) : State * bool =
        let mutable found = false
        let guides' =
            s.Guides
            |> List.map (fun g ->
                if g.Id = id then
                    found <- true
                    { g with CoordDbu = coordDbu }
                else g)
        { s with Guides = guides' }, found

    /// Delete a guide by id. Returns the new state + whether
    /// the id was found.
    let removeGuide (id: int) (s: State) : State * bool =
        let found = s.Guides |> List.exists (fun g -> g.Id = id)
        let guides' = s.Guides |> List.filter (fun g -> g.Id <> id)
        { s with Guides = guides' }, found


/// Singleton service holding the live `Guides.State` and a
/// `Changed` event the canvas + rulers subscribe to. Mirrors
/// `ViewportSync`'s shape so subscribers can `InvalidateVisual`
/// from the same pattern.
module GuidesService =

    let private state : Guides.State ref = ref Guides.empty
    let private changed = Event<Guides.State>()

    /// Current snapshot. Cheap value read — value-type record so
    /// no aliasing concerns.
    let current () : Guides.State = !state

    /// Subscribe to state changes. Subscribers should
    /// `InvalidateVisual` themselves; the service does no
    /// rendering.
    let onChanged : IEvent<Guides.State> = changed.Publish

    let private updateWith (f: Guides.State -> Guides.State) =
        let s' = f !state
        if s' <> !state then
            state := s'
            changed.Trigger s'

    let startDrag o c m  = updateWith (Guides.startDrag o c m)
    let updateDrag c     = updateWith (Guides.updateDrag c)
    let cancelDrag ()    = updateWith Guides.cancelDrag
    let commitDrag ()    = updateWith Guides.commitDrag
    let deleteByDrag ()  = updateWith Guides.deleteByDrag
    let clearAll ()      = updateWith Guides.clearAll

    // Programmatic / MCP-side wrappers — each returns the data
    // the caller needs (assigned id, found-ness) so the HTTP
    // command layer can shape an honest response without re-
    // reading the singleton afterwards.

    let addGuide (orientation: Guides.Orientation) (coordDbu: int64) : int =
        let mutable assignedId = 0
        updateWith (fun s ->
            let s', id = Guides.addGuide orientation coordDbu s
            assignedId <- id
            s')
        assignedId

    let setGuideCoord (id: int) (coordDbu: int64) : bool =
        let mutable foundFlag = false
        updateWith (fun s ->
            let s', f = Guides.setGuideCoord id coordDbu s
            foundFlag <- f
            s')
        foundFlag

    let removeGuide (id: int) : bool =
        let mutable foundFlag = false
        updateWith (fun s ->
            let s', f = Guides.removeGuide id s
            foundFlag <- f
            s')
        foundFlag

    /// Test-only: reset the singleton to `Guides.empty`. Pure
    /// modules don't have this need; the singleton does so tests
    /// that touch the service (rare — most stick to the pure
    /// `Guides` module) don't leak state across runs.
    let resetForTest () =
        state := Guides.empty
        changed.Trigger Guides.empty
