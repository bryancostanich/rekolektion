module Rekolektion.Viz.Core.Tests.MagicVsVizDrcTests

// Cross-checks the viz in-process DRC (`Drc.Check.check`) against
// Magic's authoritative `drc check` on `j_az_col.rkt`. Each side
// produces a (rule -> count) map; we report the deltas and assert
// no rule that BOTH sides recognise has a count mismatch.
//
// Requirements (skipped cleanly when absent):
//   - magic binary on PATH or at ~/.local/bin/magic
//   - PDK_ROOT pointing at a tree containing sky130B
//
// The .rkt is shipped in `testdata/cell_designs/column_readout_chain/`
// alongside its primitive import — `LayoutLoader` walks the
// `(import …)` graph automatically.

open System
open System.IO
open System.Text.RegularExpressions
open Xunit
open Xunit.Abstractions
open Rekolektion.Viz.Core
open Rekolektion.Viz.Core.Layout
open Rekolektion.Viz.Core.Rkt

let private testDataPath (rel : string) =
    let asmDir =
        System.Reflection.Assembly.GetExecutingAssembly().Location
        |> Path.GetDirectoryName
    Path.Combine(asmDir, rel)

let private rktPath () =
    testDataPath "testdata/cell_designs/column_readout_chain/j_az_col.rkt"

let private hasFixture () = File.Exists (rktPath ())

let private findMagic () : string option =
    let candidates = [
        Path.Combine (
            Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
            ".local", "bin", "magic")
        "/opt/homebrew/bin/magic"
        "/usr/local/bin/magic"
    ]
    candidates
    |> List.tryFind File.Exists
    |> Option.orElseWith (fun () ->
        // Fall back to PATH lookup
        let pathEnv = Environment.GetEnvironmentVariable "PATH"
        if isNull pathEnv then None
        else
            pathEnv.Split (Path.PathSeparator)
            |> Array.tryPick (fun dir ->
                let p = Path.Combine(dir, "magic")
                if File.Exists p then Some p else None))

let private findPdkRoot () : string option =
    let env = Environment.GetEnvironmentVariable "PDK_ROOT"
    let candidates =
        [ env
          Path.Combine(
            Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
            ".volare") ]
        |> List.choose (fun p ->
            if String.IsNullOrEmpty p then None else Some p)
    candidates
    |> List.tryFind (fun p ->
        Directory.Exists (Path.Combine(p, "sky130B", "libs.tech", "magic")))

/// One Magic violation: the rule/message text plus its bboxes.
/// Magic emits bboxes in INTERNAL units (1 magic unit = 5 nm on
/// sky130B), which the parser converts to nm so they can be
/// compared to viz bboxes (which we keep in DBU=nm).
type MagicViolation = {
    Message : string
    Bboxes  : (int64 * int64 * int64 * int64) list
}

/// Parse the TCL output we emit:
///   MSG: <free-form text>
///   BOX: <llx> <lly> <urx> <ury>   (one per box, magic internal units)
///   END
let private parseMagicViolations (raw : string) : MagicViolation list =
    let acc = System.Collections.Generic.List<MagicViolation>()
    let mutable currentMsg : string option = None
    let mutable currentBoxes : (int64 * int64 * int64 * int64) list = []
    let flush () =
        match currentMsg with
        | Some m when not currentBoxes.IsEmpty ->
            acc.Add { Message = m; Bboxes = List.rev currentBoxes }
        | _ -> ()
        currentMsg <- None
        currentBoxes <- []
    for rawLine in raw.Split('\n') do
        let line = rawLine.TrimEnd('\r').TrimEnd()
        if line.StartsWith "MSG: " then
            flush ()
            currentMsg <- Some (line.Substring 5)
        elif line.StartsWith "BOX: " then
            let parts =
                (line.Substring 5).Split(
                    [|' '; '\t'|],
                    StringSplitOptions.RemoveEmptyEntries)
            if parts.Length = 4 then
                let toNm (s : string) =
                    // Magic internal units are 5 nm/unit on sky130B
                    // ("2 Magic internal units = 1 Lambda" in the log,
                    // Lambda = 10 nm → internal = 5 nm).
                    int64 (Double.Parse (s, System.Globalization.CultureInfo.InvariantCulture) * 5.0)
                let llx = toNm parts.[0]
                let lly = toNm parts.[1]
                let urx = toNm parts.[2]
                let ury = toNm parts.[3]
                currentBoxes <- (llx, lly, urx, ury) :: currentBoxes
        elif line.StartsWith "END" then
            flush ()
    flush ()
    List.ofSeq acc

let private runMagic
        (magicBin   : string)
        (pdkRoot    : string)
        (gdsPath    : string)
        (cellName   : string) : string =
    let magicrc =
        Path.Combine(pdkRoot, "sky130B", "libs.tech", "magic", "sky130B.magicrc")
    // Sign-off-grade DRC. Mirrors `$PDK_ROOT/sky130B/libs.tech/magic/
    // run_standard_drc.py` — `drc style drc(full)` switches Magic
    // from its abbreviated interactive ruleset to the full sky130
    // sign-off deck (~hundreds of rules). `gds flatten true` makes
    // GDS reading flatten the hierarchy so cross-SRef spacing
    // violations surface. `drc euclidean on` matches sign-off's
    // diagonal-distance metric.
    //
    // The interactive `drc check` (default `drc(fast)`) skips many
    // rules including `nsdm.2` — running it against viz reports
    // tons of viz-only violations that are real silicon. Sign-off
    // deck is the apples-to-apples comparison.
    //
    // `cif scale out` returns nm-per-internal-unit (sky130: 5). We
    // emit bbox in INTERNAL units and convert client-side.
    let script =
        $"""
crashbackups stop
drc euclidean on
drc style drc(full)
drc on
snap internal
gds flatten true
gds read {gdsPath}
load {cellName}
select top cell
expand
drc catchup
set result [drc listall why]
foreach {{msg boxes}} $result {{
    if {{[llength $boxes] > 0}} {{
        puts "MSG: $msg"
        foreach b $boxes {{
            puts "BOX: [lindex $b 0] [lindex $b 1] [lindex $b 2] [lindex $b 3]"
        }}
        puts "END"
    }}
}}
puts "=== DONE ==="
quit -noprompt
"""
    let psi = System.Diagnostics.ProcessStartInfo(magicBin)
    psi.ArgumentList.Add "-dnull"
    psi.ArgumentList.Add "-noconsole"
    psi.ArgumentList.Add "-rcfile"
    psi.ArgumentList.Add magicrc
    psi.RedirectStandardInput <- true
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.Environment.["PDK_ROOT"] <- pdkRoot
    use proc = System.Diagnostics.Process.Start psi
    proc.StandardInput.Write script
    proc.StandardInput.Close()
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    if not (proc.WaitForExit (120 * 1000)) then
        proc.Kill ()
        failwithf "magic timed out after 120 s"
    stdout + "\n" + stderr

/// Compare viz DRC vs magic sign-off DRC on `rktRelPath` (relative
/// to the test DLL's `testdata/` root). Writes a structured report
/// to `out`; returns (vizOnly, magicOnly) so the caller can assert.
let private compareDrc
        (out : ITestOutputHelper)
        (rktRelPath : string) : int * int =
    let path = testDataPath rktRelPath
    if not (File.Exists path) then
        out.WriteLine (sprintf "SKIP: fixture missing at %s" path)
        0, 0
    else
    match findMagic (), findPdkRoot () with
    | None, _ ->
        out.WriteLine "SKIP: magic binary not found"
        0, 0
    | _, None ->
        out.WriteLine "SKIP: PDK_ROOT/sky130B/libs.tech/magic not found"
        0, 0
    | Some magicBin, Some pdkRoot ->

    out.WriteLine (sprintf "fixture = %s" path)
    out.WriteLine (sprintf "magic = %s" magicBin)
    out.WriteLine (sprintf "PDK_ROOT = %s" pdkRoot)

    let doc, warnings = LayoutLoader.load path
    if not warnings.IsEmpty then
        for w in warnings do
            out.WriteLine (sprintf "loader warning: %s" w)
    let flat = Flatten.flatten doc

    let units = doc.Units
    let vizViolations = Drc.Check.check Drc.Rules.defaultView units flat
    let vizByRule =
        vizViolations
        |> Array.groupBy (fun v -> v.Rule)
        |> Array.map (fun (rule, vs) -> rule, vs.Length)
        |> Map.ofArray
    out.WriteLine (sprintf "viz: %d violations across %d rules"
                    vizViolations.Length vizByRule.Count)
    vizByRule
    |> Map.iter (fun rule count ->
        out.WriteLine (sprintf "  viz[%s] = %d" rule count))

    let tmp = Path.GetTempFileName()
    let gdsPath = Path.ChangeExtension(tmp, ".gds")
    File.Delete tmp
    let lib = ToGds.toLibrary doc
    Gds.Writer.writeGds gdsPath lib

    try
        let cellName =
            match doc.TopCell with
            | Some n -> n
            | None ->
                match doc.Cells with
                | c :: _ -> c.Name
                | [] -> failwith "no cell in doc"
        let raw = runMagic magicBin pdkRoot gdsPath cellName
        let magicViolations = parseMagicViolations raw
        let magicTotal =
            magicViolations
            |> List.sumBy (fun v -> v.Bboxes.Length)
        out.WriteLine (sprintf
            "magic: %d distinct violation messages, %d total tiles"
            magicViolations.Length magicTotal)
        for mv in magicViolations |> List.truncate 30 do
            out.WriteLine (sprintf "  magic[%s] x %d" mv.Message mv.Bboxes.Length)
        if magicViolations.Length > 30 then
            out.WriteLine (sprintf "  ... %d more magic messages"
                            (magicViolations.Length - 30))

        // Pair violations by overlapping bbox (200 nm slop for
        // framing differences — magic tags the gap, viz often
        // tags the polygon corner).
        let slop = 200L
        let overlapsWithin
                ((ax1, ay1, ax2, ay2) : int64 * int64 * int64 * int64)
                ((bx1, by1, bx2, by2) : int64 * int64 * int64 * int64) : bool =
            ax1 - slop <= bx2 && ax2 + slop >= bx1
            && ay1 - slop <= by2 && ay2 + slop >= by1
        let magicMatches (vizBbox : int64 * int64 * int64 * int64) =
            magicViolations
            |> List.choose (fun mv ->
                let hits =
                    mv.Bboxes
                    |> List.filter (overlapsWithin vizBbox)
                if hits.IsEmpty then None
                else Some (mv.Message, hits))
        let vizOnly =
            vizViolations
            |> Array.filter (fun v ->
                (magicMatches v.BboxA).IsEmpty)
        let allVizBboxes =
            vizViolations
            |> Array.collect (fun v ->
                match v.BboxB with
                | Some b -> [| v.BboxA; b |]
                | None -> [| v.BboxA |])
        let magicOnly =
            magicViolations
            |> List.collect (fun mv ->
                mv.Bboxes
                |> List.choose (fun mb ->
                    let pairsAny =
                        allVizBboxes
                        |> Array.exists (overlapsWithin mb)
                    if pairsAny then None
                    else Some (mv.Message, mb)))

        out.WriteLine (sprintf "TOTAL: viz=%d, magic=%d, viz-only=%d, magic-only=%d"
                        vizViolations.Length magicTotal
                        vizOnly.Length magicOnly.Length)
        if vizOnly.Length > 0 then
            out.WriteLine "--- viz-only (viz reports, magic does not) — likely viz false positives ---"
            for v in vizOnly |> Array.truncate 50 do
                let (x1, y1, x2, y2) = v.BboxA
                out.WriteLine (sprintf
                    "  viz[%s] limit=%d measured=%d bbox=(%d,%d,%d,%d)"
                    v.Rule v.LimitDbu v.MeasuredDbu x1 y1 x2 y2)
            if vizOnly.Length > 50 then
                out.WriteLine (sprintf "  ... %d more viz-only" (vizOnly.Length - 50))
        if magicOnly.Length > 0 then
            out.WriteLine "--- magic-only (magic reports, viz does not) — likely viz misses ---"
            for (msg, (x1, y1, x2, y2)) in magicOnly |> List.truncate 50 do
                out.WriteLine (sprintf
                    "  magic[%s] bbox=(%d,%d,%d,%d)" msg x1 y1 x2 y2)
            if magicOnly.Length > 50 then
                out.WriteLine (sprintf "  ... %d more magic-only" (magicOnly.Length - 50))

        vizOnly.Length, magicOnly.Length
    finally
        if File.Exists gdsPath then File.Delete gdsPath

type MagicVsVizDrc(out : ITestOutputHelper) =

    [<Fact>]
    member _.``viz DRC matches Magic DRC on j_az_col`` () =
        let vizOnly, magicOnly =
            compareDrc out "testdata/cell_designs/column_readout_chain/j_az_col.rkt"
        let summary =
            sprintf "viz-only=%d, magic-only=%d (see test output for per-violation details)"
                vizOnly magicOnly
        Assert.True (vizOnly = 0 && magicOnly = 0, summary)

    [<Fact>]
    member _.``viz DRC matches Magic DRC on opamp_buffer_r2r`` () =
        let vizOnly, magicOnly =
            compareDrc out "testdata/cell_designs/dac/opamp_buffer_r2r/opamp_buffer_r2r.rkt"
        let summary =
            sprintf "viz-only=%d, magic-only=%d (see test output for per-violation details)"
                vizOnly magicOnly
        Assert.True (vizOnly = 0 && magicOnly = 0, summary)

    [<Fact>]
    member _.``viz DRC matches Magic DRC on bias_gen`` () =
        let vizOnly, magicOnly =
            compareDrc out "testdata/cell_designs/precision_ref/bias_gen.rkt"
        let summary =
            sprintf "viz-only=%d, magic-only=%d (see test output for per-violation details)"
                vizOnly magicOnly
        Assert.True (vizOnly = 0 && magicOnly = 0, summary)

    [<Fact>]
    member _.``viz DRC matches Magic DRC on b1_5_stage1`` () =
        let vizOnly, magicOnly =
            compareDrc out "testdata/cell_designs/column_readout_chain/b1_5_stage1.rkt"
        let summary =
            sprintf "viz-only=%d, magic-only=%d (see test output for per-violation details)"
                vizOnly magicOnly
        Assert.True (vizOnly = 0 && magicOnly = 0, summary)
