module Rekolektion.Viz.Core.Net.Ratlines

open Rekolektion.Viz.Core.Rkt.Types

/// One pin of a net on a specific top-level instance — the
/// world-DBU centroid of every label of that net that descends
/// from that instance, anchored to the polygon that the label
/// sits on. Used as a ratline endpoint in 2D and 3D.
type Pin = {
    /// Top-cell element index. None when the label was authored
    /// directly in the top cell (rare but possible).
    TopInstanceIndex : int option
    /// Centroid X/Y of the contributing polygons (each label finds
    /// its containing poly and contributes that poly's bbox center;
    /// labels with no containing poly fall back to the label
    /// origin). Stays in flat-DBU world coords.
    Position         : Point
    /// Z height in micrometers at the top of the contributing
    /// polygon's layer (StackZ + Thickness). Averaged when multiple
    /// contributing polys span layers. The 3D canvas uses this
    /// directly so endpoints land on the metal stack instead of
    /// floating at a fixed height. The 2D canvas ignores Z.
    ZUm              : float
    /// Number of labels contributing to this pin's centroid.
    /// Useful for tie-breaking or pruning trivially-short hops.
    LabelCount       : int
}

/// One edge of a net's spanning tree. `From` and `To` are indices
/// into `NetRoute.Pins`. Order is arbitrary — edges are undirected.
type NetEdge = {
    From : int
    To   : int
}

/// One ratline endpoint set for a net. `Pins` has at least one
/// entry — nets that don't show up at all are dropped before
/// reaching this type. A net with `Pins.Length < 2` has nothing
/// to connect across the layout and is rendered as a no-op.
///
/// `Mst` is the rectilinear minimum-spanning-tree over `Pins`, with
/// exactly `Pins.Length - 1` edges (or empty when there's a single
/// pin). Pre-computed at `compute` time so the renderer is purely
/// visual.
///
/// `IsPower` flags nets whose name matches a power/ground pattern
/// (`VDD`, `VSS`, `VPWR`, `GND`, …). Power nets typically have so
/// many pins that even an MST is a hairball, so the "all on" master
/// toggle excludes them by default — see `Visibility.HidePowerRatlines`.
type NetRoute = {
    Name    : string
    Pins    : Pin array
    Mst     : NetEdge array
    IsPower : bool
}

/// Manhattan (rectilinear) distance between two pins. Matches how
/// signals actually route on chip, and gives MST edges that hint at
/// "this net wants to span this much wire."
let private manhattan (a: Pin) (b: Pin) : int64 =
    abs (a.Position.X - b.Position.X)
    + abs (a.Position.Y - b.Position.Y)

/// Rectilinear MST via Prim's. O(N²) time, O(N) space. Plenty fast
/// for signal nets (tens of pins) and acceptable for power nets
/// (thousands of pins — ~10M ops, sub-second).
let mstOf (pins: Pin array) : NetEdge array =
    let n = pins.Length
    if n < 2 then [||] else
    let inTree = Array.zeroCreate<bool> n
    let minCost = Array.create n System.Int64.MaxValue
    let parent = Array.create n -1
    inTree.[0] <- true
    for i in 1 .. n - 1 do
        minCost.[i] <- manhattan pins.[0] pins.[i]
        parent.[i] <- 0
    let edges = ResizeArray<NetEdge>()
    for _ in 1 .. n - 1 do
        // Find the unvisited node with the smallest min-cost-into-tree.
        let mutable best = -1
        let mutable bestCost = System.Int64.MaxValue
        for i in 0 .. n - 1 do
            if not inTree.[i] && minCost.[i] < bestCost then
                best <- i
                bestCost <- minCost.[i]
        if best >= 0 then
            inTree.[best] <- true
            edges.Add({ From = parent.[best]; To = best })
            // Relax: every unvisited node may now have a shorter
            // path through `best`.
            for i in 0 .. n - 1 do
                if not inTree.[i] then
                    let cost = manhattan pins.[best] pins.[i]
                    if cost < minCost.[i] then
                        minCost.[i] <- cost
                        parent.[i] <- best
    edges.ToArray()

/// Heuristic: does this net name look like a power or ground rail?
/// Matches the SKY130 conventions (`VPWR`, `VGND`, `VPB`, `VNB`)
/// plus the common cross-vendor names (`VDD`, `VSS`, `GND`, `VCC`,
/// `VEE`) and mixed-signal split variants (`VDDA`, `VSSD`, …).
///
/// Case-insensitive. False on borderline cases — better to under-
/// classify (user sees the net) than misclassify a signal as power.
let isLikelyPowerNet (name: string) : bool =
    if System.String.IsNullOrWhiteSpace name then false else
    let upper = name.ToUpperInvariant()
    let isExact =
        match upper with
        | "VDD" | "VSS" | "GND" | "VCC" | "VEE"
        | "VPWR" | "VGND" | "VPB" | "VNB"
        | "AVDD" | "AVSS" | "DVDD" | "DVSS"
        | "VBAT" | "VREF" -> true
        | _ -> false
    if isExact then true else
    // Common prefix patterns: VDD_*, VSS_*, GND_*, VPWR_*, VGND_*.
    let prefixes = [| "VDD"; "VSS"; "GND"; "VPWR"; "VGND"; "VCC"; "VEE" |]
    prefixes
    |> Array.exists (fun p ->
        upper.StartsWith p
        && upper.Length > p.Length
        && (let c = upper.[p.Length] in c = '_' || c = ':' || c = '/'))

/// Per-flat-poly bbox + layer-Z + identity. Computed once and
/// indexed by layer-number so per-label lookup walks only
/// same-layer-number candidates instead of every flat poly. The
/// label/poly datatypes usually differ in SKY130 (e.g. met1 polys
/// = 68/20, met1 labels = 68/5), so the index key is layer NUMBER
/// only.
///
/// `PolyId` is the (SourceStructure, SourceIndex) pair of the
/// originating poly — used downstream as the per-anchor pin
/// discriminator so two labels on the same physical poly collapse.
type private FlatPolyAnchor = {
    /// Index into the `flat` array passed to `compute`. Lets us
    /// distinguish two SRefs of the same source polygon as
    /// different physical anchors, AND key the connected-components
    /// union-find on the same anchor identity.
    FlatIdx : int
    XMin    : int64
    YMin    : int64
    XMax    : int64
    YMax    : int64
    /// Z in micrometers at the top of this layer.
    TopZUm  : float
}

let private buildPolyIndex
        (flat: Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon array)
        : System.Collections.Generic.Dictionary<int, ResizeArray<FlatPolyAnchor>> =
    let dict = System.Collections.Generic.Dictionary<int, ResizeArray<FlatPolyAnchor>>()
    for i in 0 .. flat.Length - 1 do
        let poly = flat.[i]
        if poly.Points.Length > 0 then
            let topZ =
                Rekolektion.Viz.Core.Layout.Layer.bySky130Number poly.Layer poly.DataType
                |> Option.map (fun l -> l.StackZ + l.Thickness)
                |> Option.defaultValue 0.0
            let mutable xMin = System.Int64.MaxValue
            let mutable yMin = System.Int64.MaxValue
            let mutable xMax = System.Int64.MinValue
            let mutable yMax = System.Int64.MinValue
            for p in poly.Points do
                if p.X < xMin then xMin <- p.X
                if p.X > xMax then xMax <- p.X
                if p.Y < yMin then yMin <- p.Y
                if p.Y > yMax then yMax <- p.Y
            let anchor =
                { FlatIdx = i
                  XMin = xMin; YMin = yMin
                  XMax = xMax; YMax = yMax
                  TopZUm = topZ }
            match dict.TryGetValue poly.Layer with
            | true, list -> list.Add anchor
            | _ ->
                let list = ResizeArray<FlatPolyAnchor>()
                list.Add anchor
                dict.[poly.Layer] <- list
    dict

/// SKY130 via stacks: each entry says "a polygon on `viaKey` is a
/// contact that electrically bridges its bbox-overlapping
/// neighbours on `lowerKeys` (below) to its bbox-overlapping
/// neighbours on `upperKey` (above)." Used by `flatPolyComponents`
/// to chain components across routing layers. Datatypes are
/// drawing (20) / contact (44); the `_label` / `_pin` aux datatypes
/// don't carry geometry and stay out.
let private viaStacks : ((int * int) * (int * int) list * (int * int)) array =
    [|
        // licon1 contacts diff OR poly below; li1 above.
        ((66, 44), [ (65, 20); (66, 20) ], (67, 20))
        ((67, 44), [ (67, 20) ],            (68, 20))   // mcon
        ((68, 44), [ (68, 20) ],            (69, 20))   // via
        ((69, 44), [ (69, 20) ],            (70, 20))   // via2
        ((70, 44), [ (70, 20) ],            (71, 20))   // via3
        ((71, 44), [ (71, 20) ],            (72, 20))   // via4
    |]

/// Union-find over flat polys. Two polys are in the same component
/// iff their bboxes overlap (touching counts) AND they share a
/// strict (number, datatype) key, OR they're bridged by a via-
/// stack contact polygon that overlaps both. Returns the
/// `flatIdx -> componentId` map.
///
/// Same-layer key is strict — (66, 20) "poly" and (66, 44) "licon1"
/// share number 66 but are different layers; the union between
/// them happens via the licon1 entry in `viaStacks`, NOT via the
/// number alone. Bbox-overlap is a coarse "do these touch"
/// approximation; for rectilinear designs (typical SKY130) it's
/// accurate, for non-rectilinear it can over-connect.
let private flatPolyComponents
        (flat: Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon array)
        : int array =
    let n = flat.Length
    let parent = Array.init n id
    let rec find (i: int) : int =
        if parent.[i] = i then i
        else
            let r = find parent.[i]
            parent.[i] <- r
            r
    let union (a: int) (b: int) : unit =
        let ra = find a
        let rb = find b
        if ra <> rb then parent.[ra] <- rb
    // Pre-compute bboxes once.
    let bb = Array.zeroCreate<struct (int64 * int64 * int64 * int64)> n
    for i in 0 .. n - 1 do
        let poly = flat.[i]
        if poly.Points.Length > 0 then
            let mutable xMin = System.Int64.MaxValue
            let mutable yMin = System.Int64.MaxValue
            let mutable xMax = System.Int64.MinValue
            let mutable yMax = System.Int64.MinValue
            for p in poly.Points do
                if p.X < xMin then xMin <- p.X
                if p.X > xMax then xMax <- p.X
                if p.Y < yMin then yMin <- p.Y
                if p.Y > yMax then yMax <- p.Y
            bb.[i] <- struct (xMin, yMin, xMax, yMax)
        else
            bb.[i] <- struct (0L, 0L, -1L, -1L)
    // Group polys by strict (number, datatype) so we only compare
    // pairs that COULD share a component.
    let byKey = System.Collections.Generic.Dictionary<int * int, ResizeArray<int>>()
    for i in 0 .. n - 1 do
        let key = flat.[i].Layer, flat.[i].DataType
        match byKey.TryGetValue key with
        | true, list -> list.Add i
        | _ ->
            let list = ResizeArray<int>()
            list.Add i
            byKey.[key] <- list
    let overlaps (i: int) (j: int) =
        let struct (axMin, ayMin, axMax, ayMax) = bb.[i]
        let struct (bxMin, byMin, bxMax, byMax) = bb.[j]
        axMax >= axMin && bxMax >= bxMin
        && not (axMax < bxMin || bxMax < axMin
                || ayMax < byMin || byMax < ayMin)
    // Same-key bbox-touching: routing layers connect within
    // themselves.
    for KeyValue (_, ids) in byKey do
        for a in 0 .. ids.Count - 1 do
            for b in a + 1 .. ids.Count - 1 do
                if overlaps ids.[a] ids.[b] then
                    union ids.[a] ids.[b]
    // Via-stack bridges: each contact polygon unions with every
    // overlapping poly on its adjacent routing layer(s). A
    // contact whose bbox crosses both a lower and upper poly
    // electrically connects them.
    for (viaKey, lowerKeys, upperKey) in viaStacks do
        match byKey.TryGetValue viaKey with
        | false, _ -> ()
        | true, viaIds ->
            let upperIds =
                match byKey.TryGetValue upperKey with
                | true, list -> list
                | _ -> null
            let lowerLists =
                lowerKeys
                |> List.choose (fun lk ->
                    match byKey.TryGetValue lk with
                    | true, list -> Some list
                    | _ -> None)
            for vi in viaIds do
                if not (isNull upperIds) then
                    for ui in upperIds do
                        if overlaps vi ui then union vi ui
                for lowerList in lowerLists do
                    for li in lowerList do
                        if overlaps vi li then union vi li
    // Flatten union-find: every poly's parent is its root.
    Array.init n (fun i -> find i)

/// Find the smallest containing-bbox poly on the same layer-number
/// for a label origin. "Smallest" so a label sitting on a met1
/// stripe inside a wider areaid box anchors to the stripe, not the
/// box. Returns None when no same-layer poly contains the label.
let private anchorForLabel
        (idx: System.Collections.Generic.Dictionary<int, ResizeArray<FlatPolyAnchor>>)
        (label: Rekolektion.Viz.Core.Layout.Flatten.FlatLabel)
        : FlatPolyAnchor option =
    match idx.TryGetValue label.Layer with
    | false, _ -> None
    | true, list ->
        let mutable best : FlatPolyAnchor voption = ValueNone
        let mutable bestArea = System.Int64.MaxValue
        for a in list do
            if label.Origin.X >= a.XMin && label.Origin.X <= a.XMax
               && label.Origin.Y >= a.YMin && label.Origin.Y <= a.YMax then
                let area = (a.XMax - a.XMin) * (a.YMax - a.YMin)
                if area < bestArea then
                    bestArea <- area
                    best <- ValueSome a
        match best with
        | ValueNone -> None
        | ValueSome a -> Some a

/// Pin grouping discriminator. Labels inside an SRef collapse
/// per-instance — the SRef is a black box from the parent's
/// perspective, so multiple internal labels on the same net share
/// one outside-the-box pin. Labels that live directly in the top
/// cell collapse per anchoring polygon (flat-index identity —
/// distinguishing two SRefs of the same source poly as different
/// physical anchors). Top-cell labels with no containing poly
/// fall back to per-label uniqueness via a synthetic ID.
type private PinKey =
    | InInstance of int
    | OnPoly     of flatIdx: int
    | Unanchored of int     // monotonic counter

/// Compute per-net per-pin centroids from the labels reachable
/// through the hierarchy. Each label is anchored to the smallest
/// same-layer polygon that contains its origin: the pin X/Y is the
/// bbox center of that polygon, the pin Z is the top of that
/// polygon's layer in micrometers. Labels with no containing poly
/// fall back to the label's own origin and a Z of 0 (substrate
/// plane — visually obvious "no anchor found").
///
/// Grouping: per `PinKey` (see comment above). One pin per
/// (net, instance) for SRef-internal labels; one pin per
/// (net, anchor-poly) for top-cell labels. Result: a hand-laid
/// top-cell with multiple labeled stripes of the same net gets one
/// pin per stripe and ratlines between them — the case that
/// previously collapsed every parent-paint label to a single pin
/// and rendered nothing.
let compute
        (doc: Document)
        (flat: Rekolektion.Viz.Core.Layout.Flatten.FlatPolygon array)
        : NetRoute array =
    let tagged = Rekolektion.Viz.Core.Layout.Flatten.flattenLabelsTagged doc
    let polyIdx = buildPolyIndex flat
    // Components: which flat polys are physically connected (same-
    // layer touching). Used downstream to collapse pins whose
    // anchors flood-fill to each other into ONE representative pin —
    // i.e., ratline edges only span unconnected components, so a
    // pair the user has wired up disappears from the ratline view.
    let polyComp = flatPolyComponents flat
    // Stage 1: each (net, pinKey) gets a "logical pin" — one pin
    // per label-group within an SRef OR per anchor polygon for
    // top-cell labels. Same shape as before; the per-component
    // collapse happens in stage 2.
    // Accumulator: (net, pinKey) -> (sumX, sumY, sumZ, count,
    // topInstance, anchor-flat-idx-option). Anchor flat-idx is
    // captured so stage 2 can look up the component.
    let acc =
        System.Collections.Generic.Dictionary<
            string * PinKey,
            int64 * int64 * float * int * int option * int option>()
    let mutable unanchoredCounter = 0
    // Net-name filter: only labels with `Kind = NetName` contribute
    // ratline pins. `DeviceTerminal` labels (D / G / S / B emitted
    // by the FET generator's `port makeall` path) are device-pin
    // annotations, not nets — treating them as nets would collapse
    // every device's gate into one fake "G" net with ratlines
    // between every FET. The role lives on the label itself; no
    // external `(nets …)` block declaration is required (none
    // exists — the block was removed per track 06 Decision 4).
    for (topIdx, label) in tagged do
        if label.Text <> "" && label.Kind = NetName then
            let (anchorX, anchorY, anchorZ, anchorFlat) =
                match anchorForLabel polyIdx label with
                | Some a ->
                    let cx = (a.XMin + a.XMax) / 2L
                    let cy = (a.YMin + a.YMax) / 2L
                    cx, cy, a.TopZUm, Some a.FlatIdx
                | None ->
                    label.Origin.X, label.Origin.Y, 0.0, None
            let pinKey =
                match topIdx with
                | Some k -> InInstance k
                | None ->
                    match anchorFlat with
                    | Some fi -> OnPoly fi
                    | None ->
                        let n = unanchoredCounter
                        unanchoredCounter <- n + 1
                        Unanchored n
            let key = (label.Text, pinKey)
            match acc.TryGetValue key with
            | true, (sx, sy, sz, n, ti, af) ->
                acc.[key] <- (sx + anchorX, sy + anchorY, sz + anchorZ,
                              n + 1, ti, af)
            | _ ->
                acc.[key] <- (anchorX, anchorY, anchorZ, 1, topIdx, anchorFlat)
    // Stage 1 → logical pin records, paired with their (net,
    // anchor-component-id-or-unique-tag).
    let mutable uniqueCompTag = -1
    let nextUniqueTag () =
        uniqueCompTag <- uniqueCompTag - 1
        uniqueCompTag
    let logicalPins =
        acc
        |> Seq.map (fun kv ->
            let (name, _) = kv.Key
            let (sx, sy, sz, n, ti, anchorFlat) = kv.Value
            let pin : Pin = {
                TopInstanceIndex = ti
                Position = { X = sx / int64 n; Y = sy / int64 n }
                ZUm = sz / float n
                LabelCount = n
            }
            // Component bucket: real component id when we have an
            // anchor flat idx; unique negative tag for unanchored
            // pins so they never merge with anything.
            let compId =
                match anchorFlat with
                | Some fi when fi >= 0 && fi < polyComp.Length -> polyComp.[fi]
                | _ -> nextUniqueTag ()
            name, compId, pin)
        |> Seq.toArray
    // Stage 2: per net, group logical pins by component id. Each
    // group collapses into ONE final Pin (centroid + Z averaged
    // across the contributing logical pins, total label count
    // summed). The MST then runs over these per-component reps —
    // a net with everything routed together has 1 component, 0
    // ratline edges; two unconnected components → 1 edge.
    logicalPins
    |> Array.groupBy (fun (name, _, _) -> name)
    |> Array.map (fun (name, group) ->
        let byComp =
            group
            |> Array.groupBy (fun (_, compId, _) -> compId)
        let reps =
            byComp
            |> Array.map (fun (_, members) ->
                let pins = members |> Array.map (fun (_, _, p) -> p)
                let n = pins.Length
                if n = 1 then pins.[0]
                else
                    let mutable sx = 0L
                    let mutable sy = 0L
                    let mutable sz = 0.0
                    let mutable labelCount = 0
                    let mutable ti : int option = None
                    for p in pins do
                        sx <- sx + p.Position.X
                        sy <- sy + p.Position.Y
                        sz <- sz + p.ZUm
                        labelCount <- labelCount + p.LabelCount
                        // First non-None instance wins; multi-
                        // instance components lose the tag.
                        if ti.IsNone then ti <- p.TopInstanceIndex
                    { TopInstanceIndex = ti
                      Position = { X = sx / int64 n; Y = sy / int64 n }
                      ZUm = sz / float n
                      LabelCount = labelCount })
        { Name = name
          Pins = reps
          Mst = mstOf reps
          IsPower = isLikelyPowerNet name })
    |> Array.filter (fun route -> route.Pins.Length >= 2)
