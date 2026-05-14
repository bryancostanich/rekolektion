module Rekolektion.Viz.Core.Mag.Reader

open System
open System.IO
open Rekolektion.Viz.Core.Mag.Types

/// Single-pass line-based parser for one .mag file. Magic's format
/// is very regular: a small set of directives (magic, tech,
/// magscale, timestamp), optional `<< layer >>` mode-switches that
/// gate subsequent `rect` / `rlabel` lines, and `use ... transform
/// ... box ...` triples for subcell instances. We don't validate
/// every directive — anything we don't recognize is logged and
/// skipped so unknown extensions don't break the parse.

/// Parser state machine. `Layer` is the active draw layer set by
/// the most recent `<< name >>` directive. `Properties = true`
/// means we're inside `<< properties >>`, where `string` lines
/// carry per-cell metadata (we extract FIXED_BBOX). `End = true`
/// after `<< end >>`.
type private State = {
    Layer       : string option
    Properties  : bool
    End         : bool
    PendingUse  : string option
    Rects       : ResizeArray<MagRect>
    Labels      : ResizeArray<MagLabel>
    Instances   : ResizeArray<MagInstance>
    BBox        : (int64 * int64 * int64 * int64) option
    MagscaleNum : int
    MagscaleDen : int
    Tech        : string
    Warnings    : ResizeArray<string>
}

let private freshState () : State = {
    Layer = None
    Properties = false
    End = false
    PendingUse = None
    Rects = ResizeArray()
    Labels = ResizeArray()
    Instances = ResizeArray()
    BBox = None
    MagscaleNum = 1
    MagscaleDen = 1
    Tech = "sky130A"
    Warnings = ResizeArray()
}

let private tryParseInt (s: string) : int64 option =
    match Int64.TryParse s with
    | true, v -> Some v
    | _ -> None

let private tryParseFloat (s: string) : float option =
    match Double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
    | true, v -> Some v
    | _ -> None

let private parseLine (st: State) (rawLine: string) : State =
    let line = rawLine.Trim()
    if line = "" then st
    elif line.StartsWith "#" then st
    elif line.StartsWith "<<" && line.EndsWith ">>" then
        let inner = line.Substring(2, line.Length - 4).Trim()
        match inner with
        | "end" -> { st with End = true; Layer = None; Properties = false }
        | "properties" -> { st with Layer = None; Properties = true }
        | layer -> { st with Layer = Some layer; Properties = false }
    else
        // Tokenize on whitespace — Magic uses spaces / tabs.
        let toks =
            line.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
        if toks.Length = 0 then st
        else
            match toks.[0], st.Properties with
            | "magic", _ -> st
            | "tech", _ when toks.Length >= 2 -> { st with Tech = toks.[1] }
            | "timestamp", _ -> st
            | "magscale", _ when toks.Length >= 3 ->
                let num = match tryParseInt toks.[1] with Some v -> int v | None -> 1
                let den = match tryParseInt toks.[2] with Some v -> int v | None -> 1
                { st with MagscaleNum = num; MagscaleDen = max 1 den }
            | "rect", false when toks.Length >= 5 && st.Layer.IsSome ->
                match tryParseInt toks.[1], tryParseInt toks.[2],
                      tryParseInt toks.[3], tryParseInt toks.[4] with
                | Some x1, Some y1, Some x2, Some y2 ->
                    st.Rects.Add({
                        Layer = st.Layer.Value
                        X1 = min x1 x2; Y1 = min y1 y2
                        X2 = max x1 x2; Y2 = max y1 y2 })
                    st
                | _ -> st
            | "rlabel", false when toks.Length >= 7 ->
                // rlabel <layer> <x1> <y1> <x2> <y2> <pos> "text"
                // The layer name precedes coordinates here, NOT the
                // active << layer >> mode (Magic lets a label live
                // on a different layer than the surrounding paint).
                let layer = toks.[1]
                match tryParseInt toks.[2], tryParseInt toks.[3],
                      tryParseInt toks.[4], tryParseInt toks.[5] with
                | Some x1, Some y1, Some x2, Some y2 ->
                    // Text is the remainder after token 6; may have
                    // spaces and embedded quotes. Re-join everything
                    // from index 7 onward.
                    let text =
                        if toks.Length > 7 then
                            String.Join(" ", toks.[7 ..])
                        else ""
                    let cleaned = text.Trim('"', ' ', '\t')
                    st.Labels.Add({
                        Layer = layer
                        X1 = min x1 x2; Y1 = min y1 y2
                        X2 = max x1 x2; Y2 = max y1 y2
                        Text = cleaned })
                    st
                | _ -> st
            | "flabel", false ->
                // flabel ... — full-form label; same idea as rlabel
                // but with extra fields (font, size, rotation). We
                // ignore for now, matching most viewers.
                st
            | "use", false when toks.Length >= 2 ->
                let cell = toks.[1]
                let inst =
                    if toks.Length >= 3 then Some toks.[2] else None
                // Stash the cell name; the next `transform` line is
                // the instance's transform. Magic always emits
                // `use → transform → box` together.
                st.Instances.Add({
                    CellName = cell
                    InstanceName = inst
                    A = 1.0; B = 0.0; C = 0.0; D = 1.0
                    Tx = 0.0; Ty = 0.0
                    Box = None })
                { st with PendingUse = Some cell }
            | "transform", false when toks.Length >= 7 && st.Instances.Count > 0 ->
                match tryParseFloat toks.[1], tryParseFloat toks.[2],
                      tryParseFloat toks.[3], tryParseFloat toks.[4],
                      tryParseFloat toks.[5], tryParseFloat toks.[6] with
                | Some a, Some b, Some c, Some d, Some e, Some f ->
                    // Magic's on-disk format is `a11 a12 a13 a21 a22 a23`,
                    // i.e. a 2×3 affine where (a13, a23) is the
                    // translation. So tokens 1..6 = (A, B, Tx, C, D, Ty)
                    // in our row-major naming.
                    let lastIdx = st.Instances.Count - 1
                    let inst = st.Instances.[lastIdx]
                    st.Instances.[lastIdx] <-
                        { inst with A = a; B = b; Tx = c; C = d; D = e; Ty = f }
                    st
                | _ -> st
            | "box", false when toks.Length >= 5 && st.Instances.Count > 0 ->
                match tryParseInt toks.[1], tryParseInt toks.[2],
                      tryParseInt toks.[3], tryParseInt toks.[4] with
                | Some x1, Some y1, Some x2, Some y2 ->
                    let lastIdx = st.Instances.Count - 1
                    let inst = st.Instances.[lastIdx]
                    st.Instances.[lastIdx] <-
                        { inst with Box = Some (min x1 x2, min y1 y2, max x1 x2, max y1 y2) }
                    st
                | _ -> st
            | "string", true when toks.Length >= 2 && toks.[1] = "FIXED_BBOX" ->
                // properties: string FIXED_BBOX x1 y1 x2 y2
                if toks.Length >= 6 then
                    match tryParseInt toks.[2], tryParseInt toks.[3],
                          tryParseInt toks.[4], tryParseInt toks.[5] with
                    | Some x1, Some y1, Some x2, Some y2 ->
                        { st with BBox = Some (min x1 x2, min y1 y2, max x1 x2, max y1 y2) }
                    | _ -> st
                else st
            | "string", true -> st        // other property strings — ignore
            | _ -> st

let private cellNameFromPath (path: string) : string =
    Path.GetFileNameWithoutExtension path

/// Read one `.mag` file from disk and return a parsed `MagCell`.
/// Does NOT recursively load subcell references — see
/// `loadWithSubcells` for that.
///
/// Tries `FileStream` with `FileShare.ReadWrite` first. If that
/// fails — Magic / `magicdnull` holds files open with `O_EXLOCK`,
/// which the .NET runtime can't bypass via FileShare flags — falls
/// back to shelling out to `/bin/cat`, which takes no lock on macOS
/// and Linux. The writer never goes through this path; it targets
/// a fresh file via `Mag.Writer.writeUpdated`, so read-side
/// concurrency is safe.
let private readLinesShared (path: string) : string[] =
    try
        use stream =
            new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite ||| FileShare.Delete)
        use reader = new StreamReader(stream)
        let buf = System.Collections.Generic.List<string>()
        let mutable line = reader.ReadLine()
        while not (isNull line) do
            buf.Add line
            line <- reader.ReadLine()
        buf.ToArray()
    with :? IOException ->
        let psi =
            System.Diagnostics.ProcessStartInfo(
                FileName = "/bin/cat",
                Arguments = sprintf "\"%s\"" (path.Replace("\"", "\\\"")),
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true)
        use proc = System.Diagnostics.Process.Start psi
        let text = proc.StandardOutput.ReadToEnd()
        proc.WaitForExit()
        if proc.ExitCode <> 0 then
            raise (IOException(
                sprintf "cat fallback failed for %s (exit %d)" path proc.ExitCode))
        text.Split('\n')

let read (path: string) : MagCell =
    let lines = readLinesShared path
    let st0 = freshState ()
    let st = (st0, lines) ||> Array.fold parseLine
    {
        Name = cellNameFromPath path
        Tech = st.Tech
        MagscaleNum = st.MagscaleNum
        MagscaleDenom = st.MagscaleDen
        Rects = List.ofSeq st.Rects
        Instances = List.ofSeq st.Instances
        Labels = List.ofSeq st.Labels
        BBox = st.BBox
    }
