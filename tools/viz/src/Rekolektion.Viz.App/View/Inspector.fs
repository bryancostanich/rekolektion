module Rekolektion.Viz.App.View.Inspector

open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Controls
open Avalonia.Media
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Gds.Types
// `Rkt.Types` opened after Gds.Types so `Point` resolves to the
// Rkt-flavored point Flatten now emits.
open Rekolektion.Viz.Core.Rkt.Types
open Rekolektion.Viz.App.Model

/// Inspector text rows use `SelectableTextBlock` rather than the
/// plain `TextBlock` — same rendering, but the user can drag-select
/// a range and copy via Cmd+C / Ctrl+C. FuncUI's auto-DSL exposes
/// the same property helpers (`text`, `textWrapping`, etc.) on
/// `SelectableTextBlock` because it inherits from `TextBlock`.
let private line
        (text: string)
        (extras: IAttr<SelectableTextBlock> list)
        : IView =
    SelectableTextBlock.create (
        [ SelectableTextBlock.text text
          SelectableTextBlock.textWrapping TextWrapping.Wrap ]
        @ extras) :> IView

let private polyDetails (model: Model.Model) (struc: string) (idx: int) : IView list =
    match Model.activeMacro model with
    | None -> []
    | Some m ->
        // Selection identifies a polygon in the flat array via its
        // SourceStructure + SourceIndex. Find the first match (a
        // single SRef-instanced polygon may appear many times in
        // flat space; for picking we just want any one of them so
        // the user sees the layer / footprint).
        let hit =
            m.FlatPolygons
            |> Array.tryFind (fun p -> p.SourceStructure = struc && p.SourceIndex = idx)
        match hit with
        | None ->
            [ line (sprintf "structure: %s" struc) []
              line (sprintf "index: %d" idx) [] ]
        | Some poly ->
            let layerName =
                Layout.Layer.bySky130Number poly.Layer poly.DataType
                |> Option.map (fun l -> l.Name)
                |> Option.defaultValue "(unknown)"
            let uupdb = m.Library.UserUnitsPerDbUnit
            let mutable xMin = System.Double.MaxValue
            let mutable xMax = System.Double.MinValue
            let mutable yMin = System.Double.MaxValue
            let mutable yMax = System.Double.MinValue
            for p in poly.Points do
                let x = float p.X * uupdb
                let y = float p.Y * uupdb
                if x < xMin then xMin <- x
                if x > xMax then xMax <- x
                if y < yMin then yMin <- y
                if y > yMax then yMax <- y
            // Find TextLabels in the same structure that are
            // associated with the picked polygon. SKY130 labels are
            // points placed somewhere along a signal stripe, not on
            // every connected fragment, so a tight bbox test misses
            // most of them. Two complementary passes:
            //   1. Strict: point-in-polygon with 0.05 µm grace —
            //      catches labels placed directly on the clicked
            //      polygon (e.g. label "Q" on LI1 stripe matches
            //      that LI1 polygon).
            //   2. Layer-family: labels on the same layer NUMBER
            //      (any datatype) within ~1.0 µm of the polygon
            //      bbox — catches the common case where the user
            //      clicked an LICON1 contact / via / fragment near
            //      a labeled stripe of the same routing layer.
            let strictTol = 0.05
            let nearbyRadius = 1.0
            let pointInPolyUm (qx: float) (qy: float) (poly: Point array) : bool =
                let n = poly.Length
                if n < 3 then false
                else
                    let mutable inside = false
                    let mutable j = n - 1
                    for i in 0 .. n - 1 do
                        let xi = float poly.[i].X * uupdb
                        let yi = float poly.[i].Y * uupdb
                        let xj = float poly.[j].X * uupdb
                        let yj = float poly.[j].Y * uupdb
                        let cross =
                            ((yi > qy) <> (yj > qy)) &&
                            (qx < (xj - xi) * (qy - yi) / (yj - yi) + xi)
                        if cross then inside <- not inside
                        j <- i
                    inside
            let allLabels =
                m.Library.Structures
                |> List.tryFind (fun s -> s.Name = struc)
                |> Option.map (fun s ->
                    s.Elements
                    |> List.choose (function
                        | Element.Text t -> Some t
                        | _ -> None))
                |> Option.defaultValue []
            let labelXY (t: TextLabel) =
                float t.Origin.X * uupdb, float t.Origin.Y * uupdb
            let strictHits =
                allLabels
                |> List.filter (fun t ->
                    let lx, ly = labelXY t
                    pointInPolyUm lx ly poly.Points
                    || (lx >= xMin - strictTol && lx <= xMax + strictTol
                        && ly >= yMin - strictTol && ly <= yMax + strictTol))
            let familyHits =
                allLabels
                |> List.filter (fun t ->
                    if t.Layer <> poly.Layer then false
                    else
                        let lx, ly = labelXY t
                        lx >= xMin - nearbyRadius && lx <= xMax + nearbyRadius
                        && ly >= yMin - nearbyRadius && ly <= yMax + nearbyRadius)
            // Merge passes, de-dupe by (Layer, TextType, Origin, Text).
            let matchingLabels =
                strictHits @ familyHits
                |> List.distinctBy (fun t ->
                    (t.Layer, t.TextType, t.Origin.X, t.Origin.Y, t.Text))

            let labelLines : IView list =
                matchingLabels
                |> List.map (fun t ->
                    let layerOf =
                        Layout.Layer.bySky130Number t.Layer t.TextType
                        |> Option.map (fun l -> l.Name)
                        |> Option.defaultValue (sprintf "%d/%d" t.Layer t.TextType)
                    line
                        (sprintf "label \"%s\" on %s" t.Text layerOf)
                        [ SelectableTextBlock.foreground "#a0d8ff" ])

            [ yield line
                (sprintf "layer: %s (%d/%d)" layerName poly.Layer poly.DataType)
                [ SelectableTextBlock.fontWeight FontWeight.SemiBold ]
              yield line (sprintf "structure: %s" struc) []
              yield line (sprintf "polygon #%d (%d pts)" idx poly.Points.Length) []
              yield line (sprintf "bbox: %.3f × %.3f µm" (xMax - xMin) (yMax - yMin)) []
              yield line
                (sprintf "@ (%.3f, %.3f) µm" xMin yMin)
                [ SelectableTextBlock.foreground "#888" ]
              yield! labelLines ]

let view (model: Model.Model) (_dispatch: Msg.Msg -> unit) : IView =
    let body : IView list =
        [
            yield TextBlock.create [
                TextBlock.text "Inspector"
                TextBlock.fontWeight FontWeight.Bold
            ] :> IView
            if model.Selection.IsEmpty then
                yield TextBlock.create [
                    TextBlock.text "(nothing selected)"
                    TextBlock.foreground "#888"
                ] :> IView
            elif model.Selection.Count = 1 then
                let struc, idx = model.Selection.MinimumElement
                yield! polyDetails model struc idx
            else
                yield TextBlock.create [
                    TextBlock.text (sprintf "%d polygons selected" model.Selection.Count)
                    TextBlock.foreground "#CCC"
                ] :> IView
        ]

    StackPanel.create [
        StackPanel.spacing 6.0
        StackPanel.margin 8.0
        StackPanel.children body
    ] :> IView
