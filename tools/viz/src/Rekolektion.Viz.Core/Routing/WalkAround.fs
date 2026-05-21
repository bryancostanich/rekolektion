/// Walk-around route generator (ADR-0006).
///
/// Pure entry point that ties `Obstacles` + `VisibilityGraph`
/// together. Given:
///   - the cell's flat polygons + net map,
///   - the active routing layer + start net,
///   - the wire clearance for that layer (half-width + spacing,
///     derived from the live DRC view by the caller),
///   - the start and cursor world points,
/// returns the walk-around path as a list of fixed Pt nodes from
/// start to cursor. When the path is the trivial straight-L through
/// clear space, the returned list has exactly two endpoints — the
/// caller's existing L-shape renderer handles the corner.
///
/// Returns `None` when the obstacle field has no path between start
/// and cursor (cursor inside a foreign-net polygon, fully enclosed
/// region, etc.). The caller paints the route up to its last clear
/// reachable point.
///
/// Caching policy: this module is stateless. The caller is expected
/// to cache the `VisibilityGraph.Prebuilt` across mouse moves and
/// rebuild only when (FlatPolygons identity, Macro.Nets identity,
/// layer, startNet) changes. See `Routing.LiveDrc` for the dispatch
/// pattern; the same scheme transfers here.
module Rekolektion.Viz.Core.Routing.WalkAround

open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Core.Sidecar.Types
open Rekolektion.Viz.Core.Routing

/// Inputs that bound a graph build. The caller compares these
/// against the cached `Prebuilt`'s key to decide whether to rebuild.
type BuildKey = {
    Layer        : Obstacles.LayerKey
    StartNet     : string
    Clearance    : int64
    /// Reference-equality token for `FlatPolygons`. Identity flip
    /// = geometry changed = rebuild.
    FlatPolyRef  : FlatPolygon array
    /// Reference-equality token for the net map.
    NetMapRef    : Map<string, NetEntry>
}

/// Build the obstacle set + visibility graph for a route. Returns
/// the prebuilt graph; cached by the caller. O(O · log O) for the
/// obstacle filter, O(O² · O) for the corner-pair visibility test
/// inside `VisibilityGraph.build`.
let buildGraph (key : BuildKey) : VisibilityGraph.Prebuilt =
    let netIdx = Obstacles.buildNetIndex key.NetMapRef
    let obstacles = Obstacles.obstaclesFor key.Layer key.StartNet netIdx key.FlatPolyRef
    VisibilityGraph.build key.Clearance obstacles

/// Run the walk-around. `graph` is a cached `Prebuilt` matching
/// the current `BuildKey`; `start` and `cursor` are world DBU
/// points.
///
/// Returns the path as Pt list, OR `None` if no path exists.
/// A None result means the cursor is unreachable from start under
/// the current obstacle field; the caller paints the route up to
/// the last clear node it has and lets the live-DRC overlay flag
/// the gap.
let route
    (graph  : VisibilityGraph.Prebuilt)
    (start  : VisibilityGraph.Pt)
    (cursor : VisibilityGraph.Pt) : VisibilityGraph.Pt list option =
    VisibilityGraph.shortestPath graph start cursor
