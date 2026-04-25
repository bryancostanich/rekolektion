module Rekolektion.Viz.App.View.RunDialog

open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Rekolektion.Viz.App.Model

/// Avalonia Window subclass that prompts the user for the
/// macro-generation parameters defined in `Msg.RunMacroParams` and
/// returns either `Some p` (Run pressed) or `None` (Cancel / closed).
///
/// Built imperatively because the form is a one-shot modal — FuncUI
/// adds nothing here and the Window lifecycle is easier to manage
/// directly.
type RunDialog() as this =
    inherit Window()

    let tcs = TaskCompletionSource<Msg.RunMacroParams option>()
    let mutable settled = false

    let trySet (v: Msg.RunMacroParams option) =
        if not settled then
            settled <- true
            tcs.TrySetResult v |> ignore

    // --- form state controls (created in build) ---
    let cellBox = ComboBox()
    let wordsBox = NumericUpDown()
    let bitsBox = NumericUpDown()
    let muxBox = NumericUpDown()
    let writeEnableCb = CheckBox()
    let scanChainCb = CheckBox()
    let clockGatingCb = CheckBox()
    let powerGatingCb = CheckBox()
    let wlSwitchoffCb = CheckBox()
    let burnInCb = CheckBox()
    let extractedSpiceCb = CheckBox()
    let outputBox = TextBox()

    let collect () : Msg.RunMacroParams =
        let cell =
            match cellBox.SelectedItem with
            | :? string as s -> s
            | _ -> "lr"
        let toInt (n: System.Nullable<decimal>) (fallback: int) : int =
            if n.HasValue then int n.Value else fallback
        let toBool (cb: CheckBox) : bool =
            cb.IsChecked.HasValue && cb.IsChecked.Value
        {
            Cell = cell
            Words = toInt wordsBox.Value 256
            Bits = toInt bitsBox.Value 32
            Mux = toInt muxBox.Value 4
            WriteEnable = toBool writeEnableCb
            ScanChain = toBool scanChainCb
            ClockGating = toBool clockGatingCb
            PowerGating = toBool powerGatingCb
            WlSwitchoff = toBool wlSwitchoffCb
            BurnIn = toBool burnInCb
            ExtractedSpice = toBool extractedSpiceCb
            OutputPath = outputBox.Text |> Option.ofObj |> Option.defaultValue "output/macro.gds"
        }

    let labeled (label: string) (control: Control) : StackPanel =
        let sp = StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0)
        let lbl = TextBlock(Text = label, Width = 130.0, VerticalAlignment = VerticalAlignment.Center)
        sp.Children.Add lbl
        sp.Children.Add control
        sp

    let buildBody () : Control =
        cellBox.Items.Add "lr" |> ignore
        cellBox.Items.Add "foundry" |> ignore
        cellBox.Width <- 200.0

        wordsBox.Minimum <- decimal 1
        wordsBox.Maximum <- decimal 65536
        wordsBox.Increment <- decimal 1
        wordsBox.Width <- 200.0

        bitsBox.Minimum <- decimal 1
        bitsBox.Maximum <- decimal 256
        bitsBox.Increment <- decimal 1
        bitsBox.Width <- 200.0

        muxBox.Minimum <- decimal 1
        muxBox.Maximum <- decimal 32
        muxBox.Increment <- decimal 1
        muxBox.Width <- 200.0

        writeEnableCb.Content <- "WriteEnable"
        scanChainCb.Content <- "ScanChain"
        clockGatingCb.Content <- "ClockGating"
        powerGatingCb.Content <- "PowerGating"
        wlSwitchoffCb.Content <- "WlSwitchoff"
        burnInCb.Content <- "BurnIn"
        extractedSpiceCb.Content <- "ExtractedSpice"

        outputBox.Width <- 320.0

        let runBtn = Button(Content = "Run", Width = 80.0)
        runBtn.Click.Add(fun _ ->
            trySet (Some (collect ()))
            this.Close())

        let cancelBtn = Button(Content = "Cancel", Width = 80.0)
        cancelBtn.Click.Add(fun _ ->
            trySet None
            this.Close())

        let footer = StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0,
                                HorizontalAlignment = HorizontalAlignment.Right,
                                Margin = Thickness(0.0, 12.0, 0.0, 0.0))
        footer.Children.Add runBtn
        footer.Children.Add cancelBtn

        let flagsPanel = StackPanel(Orientation = Orientation.Vertical, Spacing = 4.0)
        flagsPanel.Children.Add writeEnableCb
        flagsPanel.Children.Add scanChainCb
        flagsPanel.Children.Add clockGatingCb
        flagsPanel.Children.Add powerGatingCb
        flagsPanel.Children.Add wlSwitchoffCb
        flagsPanel.Children.Add burnInCb
        flagsPanel.Children.Add extractedSpiceCb

        let outer = StackPanel(Orientation = Orientation.Vertical, Spacing = 8.0,
                               Margin = Thickness(16.0))
        outer.Children.Add (labeled "Cell" cellBox)
        outer.Children.Add (labeled "Words" wordsBox)
        outer.Children.Add (labeled "Bits" bitsBox)
        outer.Children.Add (labeled "Mux" muxBox)
        outer.Children.Add (labeled "Flags" flagsPanel)
        outer.Children.Add (labeled "Output Path" outputBox)
        outer.Children.Add footer
        outer :> Control

    do
        this.Title <- "Run macro"
        this.Width <- 520.0
        this.Height <- 540.0
        this.WindowStartupLocation <- WindowStartupLocation.CenterOwner
        this.Content <- buildBody ()
        this.Closed.Add(fun _ -> trySet None)

    member private this.Apply (initial: Msg.RunMacroParams) : unit =
        cellBox.SelectedItem <- box initial.Cell
        wordsBox.Value <- System.Nullable<decimal>(decimal initial.Words)
        bitsBox.Value <- System.Nullable<decimal>(decimal initial.Bits)
        muxBox.Value <- System.Nullable<decimal>(decimal initial.Mux)
        writeEnableCb.IsChecked <- System.Nullable<bool>(initial.WriteEnable)
        scanChainCb.IsChecked <- System.Nullable<bool>(initial.ScanChain)
        clockGatingCb.IsChecked <- System.Nullable<bool>(initial.ClockGating)
        powerGatingCb.IsChecked <- System.Nullable<bool>(initial.PowerGating)
        wlSwitchoffCb.IsChecked <- System.Nullable<bool>(initial.WlSwitchoff)
        burnInCb.IsChecked <- System.Nullable<bool>(initial.BurnIn)
        extractedSpiceCb.IsChecked <- System.Nullable<bool>(initial.ExtractedSpice)
        outputBox.Text <- initial.OutputPath

    /// Show as a modal dialog over `owner`, pre-fill with `initial`,
    /// and return when the user clicks Run / Cancel / closes the
    /// window.
    member this.ShowAsync (owner: Window) (initial: Msg.RunMacroParams) : Async<Msg.RunMacroParams option> =
        async {
            this.Apply initial
            let! _ = this.ShowDialog<obj>(owner) |> Async.AwaitTask
            return! tcs.Task |> Async.AwaitTask
        }

/// Sensible defaults for the dialog when nothing is pre-filled.
let defaultParams : Msg.RunMacroParams = {
    Cell = "lr"
    Words = 256
    Bits = 32
    Mux = 4
    WriteEnable = false
    ScanChain = false
    ClockGating = false
    PowerGating = false
    WlSwitchoff = false
    BurnIn = false
    ExtractedSpice = false
    OutputPath = "output/macro.gds"
}
