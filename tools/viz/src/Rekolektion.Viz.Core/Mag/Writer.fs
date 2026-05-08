module Rekolektion.Viz.Core.Mag.Writer

open System
open System.IO
open Rekolektion.Viz.Core.Gds.Types

/// Round-trip a `.mag` file back to disk, substituting only the
/// `transform` line of each top-level instance with the value
/// derived from `currentLib`. Every other line — `use`, `box`,
/// `timestamp`, `<< checkpaint >>` rects, `<< properties >>`
/// strings, comments, blank lines, ordering — is preserved
/// byte-for-byte.
///
/// **Why so narrow:**
/// - `box` is the *child cell's* local bbox in its own coords; it
///   doesn't change when the SRef's transform changes.
/// - `<< checkpaint >>` and `string FIXED_BBOX` typically include
///   user-set padding; we don't want to silently shrink them on
///   save. A separate "Refresh outline" command can rewrite them
///   when the user actually wants that.
///
/// Net result: load → save (no edits) is byte-identical, and a
/// move / rotate / mirror persists exactly the change the user
/// made and nothing else.

let private transformTokens (sr: SRef) : int64 * int64 * int64 * int64 * int64 * int64 =
    let rad = sr.Angle * System.Math.PI / 180.0
    let cosA = System.Math.Cos rad
    let sinA = System.Math.Sin rad
    let mag = sr.Mag
    let a, b, c, d =
        if sr.Reflected then
            mag * cosA,  mag * sinA,
            mag * sinA, -mag * cosA
        else
            mag * cosA, -mag * sinA,
            mag * sinA,  mag * cosA
    let toI v = int64 (System.Math.Round (v: float))
    toI a, toI b, sr.Origin.X, toI c, toI d, sr.Origin.Y

let private findTop (lib: Library) : Structure =
    let referenced = System.Collections.Generic.HashSet<string>()
    for s in lib.Structures do
        for el in s.Elements do
            match el with
            | SRef sr -> referenced.Add sr.StructureName |> ignore
            | ARef ar -> referenced.Add ar.StructureName |> ignore
            | _ -> ()
    lib.Structures
    |> List.tryFind (fun s -> not (referenced.Contains s.Name))
    |> Option.defaultWith (fun () -> List.head lib.Structures)

let private topSrefs (lib: Library) : SRef array =
    let top = findTop lib
    top.Elements
    |> List.toArray
    |> Array.choose (function SRef sr -> Some sr | _ -> None)

let private trimmed (s: string) = s.Trim()

/// Rewrite the source `.mag` to `targetPath`, substituting only
/// the `transform` line of each top-level instance with the
/// current in-memory value (in the order the file's `use`
/// directives appear; one-to-one with the top-cell's SRef
/// elements). Source and target may be the same path — atomic
/// write via a sibling temp file.
let writeUpdated
        (sourcePath: string)
        (currentLib: Library)
        (targetPath: string) : unit =
    let raw = File.ReadAllText sourcePath
    // Preserve the source's newline convention. Detect by looking
    // at the first occurrence; default to LF if none found.
    let nl = if raw.Contains "\r\n" then "\r\n" else "\n"
    let lines = raw.Split([| nl |], StringSplitOptions.None)
    // Trailing newline → Split leaves an empty tail. Track so we
    // can re-emit the same trailing-newline state.
    let endsWithNewline =
        lines.Length > 0 && lines.[lines.Length - 1] = ""

    let srefs = topSrefs currentLib
    let mutable instanceIdx = -1

    let output = System.Collections.Generic.List<string>()
    let bodyLength = if endsWithNewline then lines.Length - 1 else lines.Length
    for i in 0 .. bodyLength - 1 do
        let line = lines.[i]
        let t = trimmed line
        let toks = t.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        let head = if toks.Length > 0 then toks.[0] else ""
        if head = "use" then
            instanceIdx <- instanceIdx + 1
            output.Add line
        elif head = "transform" && instanceIdx >= 0 && instanceIdx < srefs.Length then
            let (a, b, tx, c, d, ty) = transformTokens srefs.[instanceIdx]
            output.Add (sprintf "transform %d %d %d %d %d %d" a b tx c d ty)
        else
            output.Add line

    let body = String.Join(nl, output)
    let final = if endsWithNewline then body + nl else body
    let tmp = targetPath + ".tmp"
    File.WriteAllText(tmp, final)
    if File.Exists targetPath then File.Delete targetPath
    File.Move(tmp, targetPath)
