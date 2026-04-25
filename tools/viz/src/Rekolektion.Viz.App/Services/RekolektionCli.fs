module Rekolektion.Viz.App.Services.RekolektionCli

open System.Diagnostics

/// Spawn a process; pipe stdout AND stderr; invoke the callback for
/// every line. Returns exit code. Used by the Run-macro flow to drive
/// the existing Python `rekolektion` CLI as a subprocess.
let runProcess (exe: string) (args: string list) (onLine: string -> unit) : Async<int> = async {
    let psi = ProcessStartInfo(exe)
    for a in args do psi.ArgumentList.Add a
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError  <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow  <- true
    use proc = new Process(StartInfo = psi)
    proc.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then onLine e.Data)
    proc.ErrorDataReceived.Add (fun e -> if not (isNull e.Data) then onLine e.Data)
    proc.Start() |> ignore
    proc.BeginOutputReadLine()
    proc.BeginErrorReadLine()
    do! proc.WaitForExitAsync() |> Async.AwaitTask
    return proc.ExitCode
}

/// Build the args list for `rekolektion macro …` from a RunMacroParams.
let buildMacroArgs (p: Rekolektion.Viz.App.Model.Msg.RunMacroParams) : string list =
    [
        yield "macro"
        yield "--cell"; yield p.Cell
        yield "--words"; yield string p.Words
        yield "--bits"; yield string p.Bits
        yield "--mux"; yield string p.Mux
        if p.WriteEnable    then yield "--write-enable"
        if p.ScanChain      then yield "--scan-chain"
        if p.ClockGating    then yield "--clock-gating"
        if p.PowerGating    then yield "--power-gating"
        if p.WlSwitchoff    then yield "--wl-switchoff"
        if p.BurnIn         then yield "--burn-in"
        if p.ExtractedSpice then yield "--extracted-spice"
        yield "-o"; yield p.OutputPath
    ]
