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

// Prebuilt graph cache. Keyed on (ObstacleSet reference identity,
// clearance). VisibilityGraph.build is O(N²·N) on obstacle count —
// 14s on a 744-obstacle macro per measurement. The region-clipped
// approach in the prior implementation rebuilt every cursor frame
// because the region changed; the graph was never reused. Caching
// the FULL obstacle graph trades one slow build for fast reuse
// across every subsequent draft frame on the same geometry.
// Invalidation contract: see `tools/viz/docs/routing_caches.md`.
let private graphCache : System.Collections.Concurrent.ConcurrentDictionary<obj * int64, VisibilityGraph.Prebuilt> =
    System.Collections.Concurrent.ConcurrentDictionary<obj * int64, VisibilityGraph.Prebuilt>(HashIdentity.Structural)

/// Build the visibility graph for the picked (Layer, StartNet)
/// against EVERY obstacle in the macro. First call is slow on
/// dense macros (~15s on 744 obstacles); subsequent calls for the
/// same (obstacleSet, clearance) hit the cache and are O(1). The
/// `region` argument is accepted for backwards-compat but no longer
/// clips the obstacle set — region clipping at build time was the
/// reason the graph kept rebuilding per cursor frame and never
/// produced a stable result for the user during interactive
/// routing. The search side (`VisibilityGraph.shortestPath`)
/// handles the start/cursor positions per query without needing a
/// new graph.
let buildGraphInRegion (key : BuildKey) (_region : Obstacles.Region) : VisibilityGraph.Prebuilt =
    let netIdx = Obstacles.buildNetIndex key.NetMapRef
    let set =
        Obstacles.obstacleSet
            key.Layer key.StartNet netIdx key.FlatPolyRef
    let cacheKey = box set, key.Clearance
    match graphCache.TryGetValue(cacheKey) with
    | true, g -> g
    | _ ->
        let fullObs = Obstacles.polygonsOf set
        let g = VisibilityGraph.build key.Clearance fullObs
        // Trim if cache grows; reference-identity keys mean once
        // an ObstacleSet is collected the entry is unreachable
        // anyway, but explicit bound keeps memory predictable.
        if graphCache.Count >= 4 then graphCache.Clear()
        graphCache.[cacheKey] <- g
        g

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
    (ct        : System.Threading.CancellationToken)
    (preferred : VisibilityGraph.PreferredPosture)
    (graph  : VisibilityGraph.Prebuilt)
    (start  : VisibilityGraph.Pt)
    (cursor : VisibilityGraph.Pt) : VisibilityGraph.Pt list option =
    VisibilityGraph.shortestPath ct preferred graph start cursor

/// Outer bounding box of the macro — the region search will never
/// grow past this. Caller supplies it (typically the FlatPolygons'
/// overall bbox or the cell's library bounds).
type MacroBounds = {
    XMin : int64
    YMin : int64
    XMax : int64
    YMax : int64
}

/// Reference-identity cache of FlatPolygons → bbox. The polygon
/// array doesn't change between cursor moves on the same macro, but
/// the canvas was rescanning every dispatch (O(polygons · points)).
/// Trimmed when it grows so a long session doesn't pin every macro.
/// Invalidation contract: see `tools/viz/docs/routing_caches.md`.
let private macroBoundsCache : System.Collections.Concurrent.ConcurrentDictionary<obj, MacroBounds> =
    System.Collections.Concurrent.ConcurrentDictionary<obj, MacroBounds>(HashIdentity.Reference)

/// Bounding box of every point in `flat`. Returns `None` when the
/// array is empty so the caller picks its own fallback (start/cursor
/// bbox is the canvas convention). Result is memoised by array
/// reference identity — pass the SAME array instance to hit cache.
let macroBoundsOf (flat : FlatPolygon array) : MacroBounds option =
    if flat.Length = 0 then None
    else
        let key = box flat
        match macroBoundsCache.TryGetValue(key) with
        | true, b -> Some b
        | _ ->
            let mutable xMin = System.Int64.MaxValue
            let mutable yMin = System.Int64.MaxValue
            let mutable xMax = System.Int64.MinValue
            let mutable yMax = System.Int64.MinValue
            for fp in flat do
                for pt in fp.Points do
                    if pt.X < xMin then xMin <- pt.X
                    if pt.X > xMax then xMax <- pt.X
                    if pt.Y < yMin then yMin <- pt.Y
                    if pt.Y > yMax then yMax <- pt.Y
            let b = { XMin = xMin; YMin = yMin; XMax = xMax; YMax = yMax }
            if macroBoundsCache.Count >= 4 then macroBoundsCache.Clear()
            macroBoundsCache.[key] <- b
            Some b

/// Outcome of an adaptive search: the path (if found), the region
/// the successful (or final) attempt used, the visibility graph
/// that was built for that region, and how many expansions happened.
/// `Expansions = 0` means the initial region succeeded; higher
/// counts mean the search retried with a larger region.
///
/// `Graph` is returned so callers that need to inspect the obstacle
/// universe (containment checks, logging) reuse the work instead of
/// calling `buildGraphInRegion` a second time. The graph matches
/// `FinalRegion` — i.e., for noPath outcomes the graph is the one
/// from the LAST attempted region (the most-expanded one).
type AdaptiveResult = {
    Path        : VisibilityGraph.Pt list option
    FinalRegion : Obstacles.Region
    Graph       : VisibilityGraph.Prebuilt
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
    (ct             : System.Threading.CancellationToken)
    (preferred      : VisibilityGraph.PreferredPosture)
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
        // Check cancellation between expansions. Graph build is
        // memoised so this is cheap on the warm cache, but a stale
        // search that's about to retry with a bigger region should
        // bail before paying that cost.
        ct.ThrowIfCancellationRequested()
        let region = regionFromMargin margin
        let graph = buildGraphInRegion key region
        match route ct preferred graph start cursor with
        | Some path ->
            { Path = Some path; FinalRegion = region; Graph = graph; Expansions = attempt }
        | None ->
            if attempt >= maxExpansions || regionCoversMacro region then
                { Path = None; FinalRegion = region; Graph = graph; Expansions = attempt }
            else
                loop (margin * 2L) (attempt + 1)
    loop initialMargin 0
