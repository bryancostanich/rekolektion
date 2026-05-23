/// Wire-as-first-class-thing helpers.
///
/// Wires don't exist in the on-disk schema — a wire is a set of
/// RectEls that share a `WireId`, stored as a `Property` with key
/// `"wire-id"` on each RectEl. The Property round-trips through the
/// `.rkt` reader/writer for free; this module exists so the rest of
/// the app addresses wires through a typed API instead of poking
/// magic strings into the Properties list.
///
/// Why first-class WireId at all: the MCP surface needs addressable
/// wires. Verbs like "drag wire 42 by 40 nm" or "delete the third
/// segment of wire 17" collapse under inference-based identity
/// because inferred IDs change every time RectEls get renumbered.
/// First-class IDs survive undo/redo, file round-trip, and
/// re-derive cycles. See `tools/viz/docs/route_editing_plan.md`.
module Rekolektion.Viz.Core.Routing.Wire

open Rekolektion.Viz.Core.Rkt.Types

/// Property key under which a wire's identifier lives. Reader /
/// writer already handle Properties; nothing else changes to make
/// the field round-trip.
[<Literal>]
let wireIdKey = "wire-id"

/// Read the WireId from a rectangle, if any. Returns `None` for
/// rectangles authored before WireIds existed or for one-off
/// geometry that isn't part of a wire (e.g., a hand-drawn rect
/// that the user didn't route).
let getWireId (r : Rectangle) : int option =
    r.Props
    |> List.tryPick (fun p ->
        if p.Key = wireIdKey then
            match p.Value with
            | PvInt n -> Some (int n)
            | _ -> None
        else None)

/// Stamp a WireId onto a rectangle. Replaces any existing wire-id
/// property; preserves all other Props. Caller assigns IDs (this
/// module doesn't allocate).
let setWireId (id : int) (r : Rectangle) : Rectangle =
    let stripped =
        r.Props |> List.filter (fun p -> p.Key <> wireIdKey)
    let next =
        { Key = wireIdKey; Value = PvInt (int64 id) }
    { r with Props = stripped @ [ next ] }

/// Highest WireId currently in use across every RectEl in every
/// cell of `doc`. Returns `0` when no wired rectangle exists, so
/// the natural "next id" is `maxWireId doc + 1`.
let maxWireId (doc : Document) : int =
    let mutable hi = 0
    for c in doc.Cells do
        for el in c.Elements do
            match el with
            | RectEl r ->
                match getWireId r with
                | Some n when n > hi -> hi <- n
                | _ -> ()
            | _ -> ()
    hi

/// Next available WireId for `doc`. Monotonic — never reuses an
/// id, even after deletes. Cheap O(elements) scan; commits are
/// infrequent enough that caching the counter on the Document
/// isn't worth the bookkeeping.
let nextWireId (doc : Document) : int =
    maxWireId doc + 1

/// All rectangles in `doc` belonging to wire `id`, in document
/// order. Empty when no rectangle carries that id. Used by
/// segment-drag and vertex-edit operations to find the wire's
/// full set of segments.
let segmentsOf (id : int) (doc : Document) : (Cell * int * Rectangle) list =
    [ for c in doc.Cells do
          for idx, el in List.indexed c.Elements do
              match el with
              | RectEl r when getWireId r = Some id -> yield (c, idx, r)
              | _ -> () ]
