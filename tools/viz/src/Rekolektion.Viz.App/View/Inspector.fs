module Rekolektion.Viz.App.View.Inspector

open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Controls
open Rekolektion.Viz.Core
open Rekolektion.Viz.App.Model

let private polyDetails (model: Model.Model) (struc: string) (idx: int) : IView list =
    match model.Macro with
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
            [ TextBlock.create [ TextBlock.text (sprintf "structure: %s" struc) ] :> IView
              TextBlock.create [ TextBlock.text (sprintf "index: %d" idx) ] :> IView ]
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
            [ TextBlock.create [
                TextBlock.text (sprintf "layer: %s (%d/%d)" layerName poly.Layer poly.DataType)
                TextBlock.fontWeight Avalonia.Media.FontWeight.SemiBold ] :> IView
              TextBlock.create [ TextBlock.text (sprintf "structure: %s" struc) ] :> IView
              TextBlock.create [ TextBlock.text (sprintf "polygon #%d (%d pts)" idx poly.Points.Length) ] :> IView
              TextBlock.create [ TextBlock.text (sprintf "bbox: %.3f × %.3f µm" (xMax - xMin) (yMax - yMin)) ] :> IView
              TextBlock.create [
                TextBlock.text (sprintf "@ (%.3f, %.3f) µm" xMin yMin)
                TextBlock.foreground "#888" ] :> IView ]

let view (model: Model.Model) (_dispatch: Msg.Msg -> unit) : IView =
    let body : IView list =
        [
            yield TextBlock.create [
                TextBlock.text "Inspector"
                TextBlock.fontWeight Avalonia.Media.FontWeight.Bold
            ] :> IView
            match model.Selection with
            | None ->
                yield TextBlock.create [
                    TextBlock.text "(nothing selected)"
                    TextBlock.foreground "#888"
                ] :> IView
            | Some (struc, idx) ->
                yield! polyDetails model struc idx
        ]

    StackPanel.create [
        StackPanel.spacing 6.0
        StackPanel.margin 8.0
        StackPanel.children body
    ] :> IView
