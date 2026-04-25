module Rekolektion.Viz.Core.Sidecar.Loader

open System.IO
open System.Text.Json
open Rekolektion.Viz.Core.Sidecar.Types

let private classOfString (s: string) : NetClass =
    match s.ToLowerInvariant() with
    | "power"  -> Power
    | "ground" -> Ground
    | "clock"  -> Clock
    | _        -> Signal

let private parsePolyRef (el: JsonElement) : PolygonRef =
    { Structure = el.GetProperty("structure").GetString()
      Layer     = el.GetProperty("layer").GetInt32()
      DataType  = el.GetProperty("datatype").GetInt32()
      Index     = el.GetProperty("index").GetInt32() }

let private parseNetEntry (name: string) (el: JsonElement) : NetEntry =
    { Name = name
      Class = classOfString (el.GetProperty("class").GetString())
      Polygons =
          el.GetProperty("polygons").EnumerateArray()
          |> Seq.map parsePolyRef
          |> Seq.toList }

let load (path: string) : Sidecar option =
    if not (File.Exists path) then None
    else
        try
            let json = File.ReadAllText path
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            let version = root.GetProperty("version").GetInt32()
            if version <> 1 then None
            else
                let nets =
                    root.GetProperty("nets").EnumerateObject()
                    |> Seq.map (fun p -> p.Name, parseNetEntry p.Name p.Value)
                    |> Map.ofSeq
                Some {
                    Version = version
                    Macro   = root.GetProperty("macro").GetString()
                    Nets    = nets
                }
        with
        | :? System.Text.Json.JsonException
        | :? System.Collections.Generic.KeyNotFoundException
        | :? System.InvalidOperationException
        | :? System.FormatException -> None
