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

/// Region-bounded graph build. Filters the obstacle universe to the
/// supplied bbox before constructing the visibility graph — the
/// whole point of ADR-0006's "continuous mode" is that the relevant
/// obstacles are the ones near (start, cursor); the visibility-graph
/// build is too costly to run against the full cell on every frame.
///
/// The caller picks the region (typically (start, cursor) bbox
/// expanded by 1-2× the manhattan distance) so detours that have to
/// leave the direct corridor can still be found.
let buildGraphInRegion (key : BuildKey) (region : Obstacles.Region) : VisibilityGraph.Prebuilt =
    let netIdx = Obstacles.buildNetIndex key.NetMapRef
    let obstacles =
        Obstacles.obstaclesInRegion key.Layer key.StartNet netIdx region key.FlatPolyRef
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

/// Outer bounding box of the macro — the region search will never
/// grow past this. Caller supplies it (typically the FlatPolygons'
/// overall bbox or the cell's library bounds).
type MacroBounds = {
    XMin : int64
    YMin : int64
    XMax : int64
    YMax : int64
}

/// Outcome of an adaptive search: the path (if found), the region
/// the successful (or final) attempt used, and how many expansions
/// happened. `Expansions = 0` means the initial region succeeded;
/// higher counts mean the search retried with a larger region.
type AdaptiveResult = {
    Path        : VisibilityGraph.Pt list option
    FinalRegion : Obstacles.Region
    Expansions  : int
}

/// Adaptive region-bounded routing. Region-bounding is an
/// optimization, but the semantic guarantee is
/// "noPath means no path exists in the full macro." This wrapper
/// preserves that: builds the graph in a margin-bounded region,
/// runs `shortestPath`; on `None`, doubles the margin and retries
/// until either a path is found, the region encloses
/// `macroBounds`, or `maxExpansions` is hit.
///
/// `initialMargin` is the padding added on each side of the
/// (start, cursor) bbox for the first attempt. The same bbox is
/// re-padded with `initialMargin * 2 ^ n` on each retry.
///
/// Region edges are clamped to `macroBounds`, so a sufficiently
/// large margin collapses to the full macro and the search runs
/// against every same-layer obstacle.
let routeAdaptive
    (key            : BuildKey)
    (start          : VisibilityGraph.Pt)
    (cursor         : VisibilityGraph.Pt)
    (initialMargin  : int64)
    (macroBounds    : MacroBounds)
    (maxExpansions  : int)
    : AdaptiveResult =
    let regionFromMargin (m : int64) : Obstacles.Region =
        { XMin = max macroBounds.XMin ((min start.X cursor.X) - m)
          YMin = max macroBounds.YMin ((min start.Y cursor.Y) - m)
          XMax = min macroBounds.XMax ((max start.X cursor.X) + m)
          YMax = min macroBounds.YMax ((max start.Y cursor.Y) + m) }
    let regionCoversMacro (r : Obstacles.Region) =
        r.XMin <= macroBounds.XMin
        && r.YMin <= macroBounds.YMin
        && r.XMax >= macroBounds.XMax
        && r.YMax >= macroBounds.YMax
    let rec loop margin attempt =
        let region = regionFromMargin margin
        let graph = buildGraphInRegion key region
        match route graph start cursor with
        | Some path ->
            { Path = Some path; FinalRegion = region; Expansions = attempt }
        | None ->
            if attempt >= maxExpansions || regionCoversMacro region then
                { Path = None; FinalRegion = region; Expansions = attempt }
            else
                loop (margin * 2L) (attempt + 1)
    loop initialMargin 0
