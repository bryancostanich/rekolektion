module Rekolektion.Viz.Render.Skia.LayerPainter

open SkiaSharp
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.Core.Layout.Flatten
open Rekolektion.Viz.Render.Color

type ViewBox = {
    MinX: int64; MinY: int64
    MaxX: int64; MaxY: int64
    PixelW: int; PixelH: int
}

let private boundsOfFlat (polys: FlatPolygon array) : (int64 * int64 * int64 * int64) =
    if polys.Length = 0 then (0L, 0L, 1L, 1L)
    else
        let mutable xMin = System.Int64.MaxValue
        let mutable yMin = System.Int64.MaxValue
        let mutable xMax = System.Int64.MinValue
        let mutable yMax = System.Int64.MinValue
        // Skip non-physical / Magic-internal markers (e.g. checkpaint
        // tiles) so they don't pull the bbox out to several times the
        // cell's silicon footprint. They still RENDER if the user
        // toggles them on; this just excludes them from camera-fit.
        for p in polys do
            if not (Layout.Layer.isNonPhysical p.Layer p.DataType) then
                for pt in p.Points do
                    if pt.X < xMin then xMin <- pt.X
                    if pt.X > xMax then xMax <- pt.X
                    if pt.Y < yMin then yMin <- pt.Y
                    if pt.Y > yMax then yMax <- pt.Y
        if xMin > xMax then (0L, 0L, 1L, 1L)
        else (xMin, yMin, xMax, yMax)

let private project (vb: ViewBox) (p: Point) : SKPoint =
    let dx = float (vb.MaxX - vb.MinX) |> max 1.0
    let dy = float (vb.MaxY - vb.MinY) |> max 1.0
    let x = float (p.X - vb.MinX) / dx * float vb.PixelW
    let y = float vb.PixelH - (float (p.Y - vb.MinY) / dy * float vb.PixelH)
    SKPoint(float32 x, float32 y)

/// Paint every flattened polygon, layer-ordered by stack Z so upper
/// metal sits on top of lower metal. Honors ToggleState.Layers.
/// Iterates `flat` (post-hierarchy expansion), so a hierarchical
/// macro renders its full content (e.g. SRAM bitcell array) instead
/// of just the top cell's polygons.
///
/// `vb` defines the world-coordinate window that maps to the canvas
/// pixel rectangle. Callers compute `vb` from current pan + zoom
/// state and pass it in; for auto-fit, use `paint` (no `_vb` arg).
/// Build the set of (SourceStructure, SourceIndex) flat-polygon
/// keys that 'belong to' the highlighted net — defined as: a label
/// with the highlighted text exists in the same Structure and its
/// Origin lies inside the polygon's bbox. Empty when no nets are
/// highlighted; computation is skipped on the fast path.
/// Public so the 3D canvas can reuse the same set when shading
/// the GL mesh — keeps 2D and 3D agreement on which polygons
/// belong to a net.
///
/// `netNames` is a set; a polygon is included if ANY of the named
/// nets has a label whose origin lands inside the polygon's bbox.
/// Multi-net highlight (the new model state) flows in through this
/// set; single-net highlight is just `Set.singleton`.
let highlightedPolyKeys
        (doc: Document)
        (flat: FlatPolygon array)
        (netNames: Set<string>)
        : System.Collections.Generic.HashSet<string * int> =
    let result = System.Collections.Generic.HashSet<string * int>()
    if netNames.IsEmpty then result
    else
    // Collect every label whose text is in the highlighted set,
    // in flat (top-cell) coords. Labels authored inside a sub-cell
    // get their origin transformed through the SRef/ARef chain.
    let origins = ResizeArray<int64 * int64>()
    for l in Layout.Flatten.flattenLabels doc do
        if netNames.Contains l.Text then
            origins.Add(l.Origin.X, l.Origin.Y)
    if origins.Count = 0 then result
    else
        // For each flat polygon, mark it highlighted if any of the
        // net's flat-frame label origins falls in its bbox. No
        // SourceStructure filter — that was the old bug, since it
        // required label and polygon to live in the SAME source
        // cell, which excluded sub-cell routing whose labels are
        // placed in the parent.
        for poly in flat do
            let mutable xMin = System.Int64.MaxValue
            let mutable xMax = System.Int64.MinValue
            let mutable yMin = System.Int64.MaxValue
            let mutable yMax = System.Int64.MinValue
            for p in poly.Points do
                if p.X < xMin then xMin <- p.X
                if p.X > xMax then xMax <- p.X
                if p.Y < yMin then yMin <- p.Y
                if p.Y > yMax then yMax <- p.Y
            let mutable hit = false
            let mutable i = 0
            while (not hit) && i < origins.Count do
                let (ox, oy) = origins.[i]
                if ox >= xMin && ox <= xMax && oy >= yMin && oy <= yMax then
                    hit <- true
                i <- i + 1
            if hit then
                result.Add((poly.SourceStructure, poly.SourceIndex)) |> ignore
        result

let paintIn
        (canvas: SKCanvas)
        (vb: ViewBox)
        (doc: Document)
        (flat: FlatPolygon array)
        (toggle: Visibility.ToggleState)
        : unit =
    // Group polys by layer key for layer-ordered draw. Faster to
    // group once than to sort each polygon's draw call.
    let byLayer =
        flat
        |> Array.groupBy (fun p -> p.Layer, p.DataType)

    let zOf (key: int * int) =
        Layout.Layer.bySky130Number (fst key) (snd key)
        |> Option.map (fun l -> l.StackZ)
        |> Option.defaultValue 100.0
    let ordered = byLayer |> Array.sortBy (fun (k, _) -> zOf k)

    // When one or more nets are highlighted, polygons not in any
    // of the matching sets are dimmed so the highlighted runs pop.
    // Empty set on the fast path costs no measurable extra time.
    let highlightSet =
        if toggle.HighlightedNets.IsEmpty then
            System.Collections.Generic.HashSet()
        else
            highlightedPolyKeys doc flat toggle.HighlightedNets
    let isHighlightActive = not toggle.HighlightedNets.IsEmpty

    // When a block is isolated, only render polygons whose source
    // cell is inside the block's transitive closure (the block
    // itself plus every cell reached via SRef/ARef). Polygons
    // outside the closure are skipped entirely — semantics is
    // 'hide other blocks', per Visibility.isBlockVisible.
    let blockClosure : Set<string> option =
        toggle.IsolatedBlock
        |> Option.map (fun name -> Layout.Hierarchy.closure doc name)

    use fill = new SKPaint(Style = SKPaintStyle.Fill, IsAntialias = true)
    use stroke = new SKPaint(Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 0.5f)

    let dimColor (c: SKColor) =
        // Drop alpha to ~25% to dim non-matching polygons.
        SKColor(c.Red, c.Green, c.Blue, byte (int c.Alpha * 64 / 255))

    for (key, polys) in ordered do
        if Visibility.isLayerVisible toggle key then
            match Layout.Layer.bySky130Number (fst key) (snd key) with
            | None -> ()
            | Some layer ->
                let fillFull = SkyTheme.fillFor layer.Name
                let strokeFull = SkyTheme.strokeFor layer.Name
                let fillDim = dimColor fillFull
                let strokeDim = dimColor strokeFull
                for poly in polys do
                    let inBlock =
                        match blockClosure with
                        | Some s -> s.Contains poly.SourceStructure
                        | None   -> true
                    if poly.Points.Length >= 3 && inBlock then
                        let isMatch =
                            (not isHighlightActive)
                            || highlightSet.Contains((poly.SourceStructure, poly.SourceIndex))
                        fill.Color <- if isMatch then fillFull else fillDim
                        stroke.Color <- if isMatch then strokeFull else strokeDim
                        use path = new SKPath()
                        path.MoveTo(project vb poly.Points.[0])
                        for i in 1 .. poly.Points.Length - 1 do
                            path.LineTo(project vb poly.Points.[i])
                        path.Close()
                        canvas.DrawPath(path, fill)
                        canvas.DrawPath(path, stroke)

/// Auto-fit variant: ViewBox derived from polygon bbox.
let paint (canvas: SKCanvas) (size: int * int) (flat: FlatPolygon array) (toggle: Visibility.ToggleState) : unit =
    let (w, h) = size
    let (xmin, ymin, xmax, ymax) = boundsOfFlat flat
    let vb = { MinX = xmin; MinY = ymin; MaxX = xmax; MaxY = ymax; PixelW = w; PixelH = h }
    // No document passed — synthesize an empty one so the highlight
    // path is a no-op. Auto-fit callers (CLI / tests) don't exercise
    // net highlighting.
    paintIn canvas vb emptyDocument flat toggle

/// Compute the bbox of the flat polygons in world DBU coordinates.
let bboxOf (flat: FlatPolygon array) : (int64 * int64 * int64 * int64) =
    boundsOfFlat flat
