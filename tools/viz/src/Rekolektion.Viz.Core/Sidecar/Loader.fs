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

/// Load a `<gds>.nets.json` sidecar.
///
/// Returns:
///   `Ok None`            — file does not exist (legitimate absence;
///                          caller should fall back to LabelFlood).
///   `Ok (Some sidecar)`  — loaded and parsed successfully.
///   `Error msg`          — file exists but is malformed, missing
///                          required fields, or carries an
///                          unsupported `version`. Caller should
///                          surface the error to the user; treating
///                          a corrupt sidecar as a missing one masks
///                          real bugs in the Python emitter.
let load (path: string) : Result<Sidecar option, string> =
    if not (File.Exists path) then Ok None
    else
        try
            let json = File.ReadAllText path
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            let version = root.GetProperty("version").GetInt32()
            if version <> 1 then
                Error (sprintf "unsupported sidecar version %d (expected 1)" version)
            else
                let nets =
                    root.GetProperty("nets").EnumerateObject()
                    |> Seq.map (fun p -> p.Name, parseNetEntry p.Name p.Value)
                    |> Map.ofSeq
                Ok (Some {
                    Version = version
                    Macro   = root.GetProperty("macro").GetString()
                    Nets    = nets
                })
        with
        | :? System.Text.Json.JsonException as ex ->
            Error (sprintf "malformed JSON: %s" ex.Message)
        | :? System.Collections.Generic.KeyNotFoundException as ex ->
            Error (sprintf "missing required field: %s" ex.Message)
        | :? System.InvalidOperationException as ex ->
            Error (sprintf "wrong field type: %s" ex.Message)
        | :? System.FormatException as ex ->
            Error (sprintf "value out of range: %s" ex.Message)
