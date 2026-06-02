module Rekolektion.Viz.App.Tests.GuidesTests

open Xunit
open FsUnit.Xunit
open Rekolektion.Viz.App.Services
open Rekolektion.Viz.App.Services.Guides

// ─────────────────────────────────────────────────────────────────
// Pure state transitions — singleton-free so the tests don't
// have to worry about leaking state between runs. The service
// layer (`GuidesService`) gets a separate handful of tests at the
// bottom of the file.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``empty has no guides, no drag, NextId = 1`` () =
    let e = Guides.empty
    e.Guides |> should be Empty
    e.Drag   |> should equal (None : Drag option)
    e.NextId |> should equal 1

[<Fact>]
let ``startDrag records orientation, coord, MovingId`` () =
    let s = Guides.empty |> Guides.startDrag Horizontal 1234L None
    s.Drag.IsSome |> should be True
    let d = s.Drag.Value
    d.Orientation |> should equal Horizontal
    d.CoordDbu    |> should equal 1234L
    d.MovingId    |> should equal (None : int option)

[<Fact>]
let ``updateDrag changes only the coord`` () =
    let s =
        Guides.empty
        |> Guides.startDrag Vertical 100L None
        |> Guides.updateDrag 250L
    let d = s.Drag.Value
    d.Orientation |> should equal Vertical
    d.CoordDbu    |> should equal 250L

[<Fact>]
let ``updateDrag is a no-op when no drag is in flight`` () =
    let s = Guides.empty |> Guides.updateDrag 999L
    s.Drag |> should equal (None : Drag option)

[<Fact>]
let ``cancelDrag clears the in-flight drag and leaves guides intact`` () =
    let s =
        Guides.empty
        |> Guides.startDrag Horizontal 50L None
        |> Guides.commitDrag
        |> Guides.startDrag Vertical 80L None
        |> Guides.cancelDrag
    s.Drag |> should equal (None : Drag option)
    s.Guides |> List.length |> should equal 1
    s.Guides.[0].CoordDbu |> should equal 50L

[<Fact>]
let ``commitDrag creating: adds a guide with autoassigned Id`` () =
    let s =
        Guides.empty
        |> Guides.startDrag Horizontal 500L None
        |> Guides.commitDrag
    s.Guides |> List.length |> should equal 1
    let g = s.Guides.[0]
    g.Id          |> should equal 1
    g.Orientation |> should equal Horizontal
    g.CoordDbu    |> should equal 500L
    s.NextId |> should equal 2
    s.Drag   |> should equal (None : Drag option)

[<Fact>]
let ``commitDrag is a no-op when no drag in flight`` () =
    Guides.empty |> Guides.commitDrag |> should equal Guides.empty

[<Fact>]
let ``successive commits assign monotonically increasing Ids`` () =
    let s =
        Guides.empty
        |> Guides.startDrag Horizontal 10L None |> Guides.commitDrag
        |> Guides.startDrag Vertical   20L None |> Guides.commitDrag
        |> Guides.startDrag Horizontal 30L None |> Guides.commitDrag
    let ids = s.Guides |> List.map (fun g -> g.Id) |> List.sort
    ids |> should equal [ 1; 2; 3 ]
    s.NextId |> should equal 4

[<Fact>]
let ``commitDrag moving: updates the matching guide's coord`` () =
    // Seed with a horizontal guide at Y=100, then drag-move it
    // to Y=250. The id stays the same.
    let seeded =
        Guides.empty
        |> Guides.startDrag Horizontal 100L None
        |> Guides.commitDrag
    let seededId = seeded.Guides.[0].Id
    let moved =
        seeded
        |> Guides.startDrag Horizontal 250L (Some seededId)
        |> Guides.commitDrag
    moved.Guides |> List.length |> should equal 1
    moved.Guides.[0].Id       |> should equal seededId
    moved.Guides.[0].CoordDbu |> should equal 250L
    // NextId not bumped on a move.
    moved.NextId |> should equal 2

[<Fact>]
let ``commitDrag moving: unknown Id leaves the list untouched`` () =
    // Guard against the unlikely race where the guide was
    // deleted between PointerPressed and PointerReleased.
    let s =
        Guides.empty
        |> Guides.startDrag Horizontal 100L None |> Guides.commitDrag
        |> Guides.startDrag Horizontal 200L (Some 999) // 999 doesn't exist
        |> Guides.commitDrag
    s.Guides |> List.length |> should equal 1
    s.Guides.[0].CoordDbu |> should equal 100L
    s.Drag |> should equal (None : Drag option)

[<Fact>]
let ``deleteByDrag on move: removes the moved guide`` () =
    // The user grabbed a guide, dragged it onto the ruler →
    // delete. The guide whose Id was held by the drag goes
    // away; any other guides stay.
    let seeded =
        Guides.empty
        |> Guides.startDrag Horizontal 100L None |> Guides.commitDrag
        |> Guides.startDrag Vertical   200L None |> Guides.commitDrag
    let toDeleteId = seeded.Guides |> List.find (fun g -> g.Orientation = Horizontal) |> _.Id
    let s =
        seeded
        |> Guides.startDrag Horizontal 150L (Some toDeleteId)
        |> Guides.deleteByDrag
    s.Guides |> List.length |> should equal 1
    s.Guides.[0].Orientation |> should equal Vertical
    s.Drag |> should equal (None : Drag option)

[<Fact>]
let ``deleteByDrag on create: same as cancel (no guide added)`` () =
    let s =
        Guides.empty
        |> Guides.startDrag Horizontal 100L None
        |> Guides.deleteByDrag
    s.Guides |> should be Empty
    s.Drag |> should equal (None : Drag option)

[<Fact>]
let ``deleteByDrag with no drag in flight is a no-op`` () =
    let seeded =
        Guides.empty
        |> Guides.startDrag Horizontal 100L None |> Guides.commitDrag
    let s = seeded |> Guides.deleteByDrag
    s |> should equal seeded

[<Fact>]
let ``clearAll wipes guides and any in-flight drag`` () =
    let s =
        Guides.empty
        |> Guides.startDrag Horizontal 100L None |> Guides.commitDrag
        |> Guides.startDrag Vertical   200L None
        |> Guides.clearAll
    s.Guides |> should be Empty
    s.Drag   |> should equal (None : Drag option)
    // NextId is sticky — we don't reuse old Ids even after clearAll.
    s.NextId |> should equal 2

// ─────────────────────────────────────────────────────────────────
// Service layer — singleton + Changed event. Resets between
// tests to keep them order-independent.
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``GuidesService.current starts at Guides.empty after reset`` () =
    GuidesService.resetForTest()
    GuidesService.current() |> should equal Guides.empty

[<Fact>]
let ``GuidesService fires Changed on every state mutation`` () =
    GuidesService.resetForTest()
    let received = ResizeArray<State>()
    use _sub = GuidesService.onChanged.Subscribe received.Add
    // resetForTest fires once; clear what we've received so far.
    received.Clear()
    GuidesService.startDrag Horizontal 100L None
    GuidesService.updateDrag 150L
    GuidesService.commitDrag()
    // Three mutations = three fires. (No-op transitions don't
    // fire — they're filtered by the equality check in
    // `updateWith`.)
    received.Count |> should equal 3
    let last = received.[2]
    last.Guides |> List.length |> should equal 1
    last.Guides.[0].CoordDbu |> should equal 150L

[<Fact>]
let ``GuidesService skips the Changed fire when state is unchanged`` () =
    GuidesService.resetForTest()
    let fires = ref 0
    use _sub = GuidesService.onChanged.Subscribe (fun _ -> incr fires)
    // updateDrag with no drag in flight produces an equal state
    // (Guides.empty), so the service shouldn't fire.
    GuidesService.updateDrag 999L
    !fires |> should equal 0

// ─────────────────────────────────────────────────────────────────
// Programmatic API — direct add / setCoord / remove. Used by the
// HTTP command layer (and through it by the MCP guides tools).
// ─────────────────────────────────────────────────────────────────

[<Fact>]
let ``addGuide returns autoassigned id and pushes the guide`` () =
    let s, id = Guides.empty |> Guides.addGuide Horizontal 1234L
    id |> should equal 1
    s.Guides |> List.length |> should equal 1
    let g = s.Guides.[0]
    g.Id          |> should equal 1
    g.Orientation |> should equal Horizontal
    g.CoordDbu    |> should equal 1234L
    s.NextId |> should equal 2

[<Fact>]
let ``addGuide increments NextId monotonically`` () =
    let s1, id1 = Guides.empty |> Guides.addGuide Horizontal 10L
    let s2, id2 = s1            |> Guides.addGuide Vertical   20L
    let s3, id3 = s2            |> Guides.addGuide Horizontal 30L
    [ id1; id2; id3 ] |> should equal [ 1; 2; 3 ]
    s3.NextId |> should equal 4

[<Fact>]
let ``setGuideCoord on an existing id updates the coord and returns true`` () =
    let s0, id = Guides.empty |> Guides.addGuide Horizontal 100L
    let s1, found = s0 |> Guides.setGuideCoord id 250L
    found |> should be True
    s1.Guides.[0].CoordDbu |> should equal 250L
    s1.Guides.[0].Id |> should equal id

[<Fact>]
let ``setGuideCoord on an unknown id is a no-op and returns false`` () =
    let s0, _id = Guides.empty |> Guides.addGuide Horizontal 100L
    let s1, found = s0 |> Guides.setGuideCoord 9999 500L
    found |> should be False
    s1 |> should equal s0

[<Fact>]
let ``removeGuide on an existing id removes it and returns true`` () =
    let s0, id1 = Guides.empty |> Guides.addGuide Horizontal 100L
    let s1, _id2 = s0 |> Guides.addGuide Vertical 200L
    let s2, found = s1 |> Guides.removeGuide id1
    found |> should be True
    s2.Guides |> List.length |> should equal 1
    s2.Guides.[0].Orientation |> should equal Vertical

[<Fact>]
let ``removeGuide on an unknown id is a no-op and returns false`` () =
    let s0, _id = Guides.empty |> Guides.addGuide Horizontal 100L
    let s1, found = s0 |> Guides.removeGuide 9999
    found |> should be False
    s1 |> should equal s0

[<Fact>]
let ``GuidesService.addGuide fires Changed exactly once and returns the id`` () =
    GuidesService.resetForTest()
    let fires = ref 0
    use _sub = GuidesService.onChanged.Subscribe (fun _ -> incr fires)
    let id = GuidesService.addGuide Horizontal 500L
    id |> should equal 1
    !fires |> should equal 1
    (GuidesService.current()).Guides |> List.length |> should equal 1

[<Fact>]
let ``GuidesService.setGuideCoord returns the found-ness flag`` () =
    GuidesService.resetForTest()
    let id = GuidesService.addGuide Vertical 100L
    GuidesService.setGuideCoord id 250L |> should be True
    GuidesService.setGuideCoord 9999 500L |> should be False
    let s = GuidesService.current()
    s.Guides.[0].CoordDbu |> should equal 250L

[<Fact>]
let ``GuidesService.removeGuide returns the found-ness flag`` () =
    GuidesService.resetForTest()
    let id = GuidesService.addGuide Horizontal 100L
    GuidesService.removeGuide 9999 |> should be False
    GuidesService.removeGuide id   |> should be True
    (GuidesService.current()).Guides |> should be Empty
