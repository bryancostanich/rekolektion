module Rekolektion.Viz.App.Services.Recents

open System
open System.IO

let private dir =
    Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
        ".rekolektion-viz")

let private file = Path.Combine(dir, "recents.txt")

let load () : string list =
    try
        if File.Exists file then
            File.ReadAllLines file
            |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
            |> Array.toList
        else []
    with _ -> []

let save (paths: string list) : unit =
    try
        if not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore
        File.WriteAllLines(file, paths |> List.truncate 10)
    with _ -> ()

/// Subscribers notified whenever the recent-files list changes.
/// AppView publishes the latest list on every render; we only fire
/// the callbacks when the list is actually different so menu rebuild
/// stays cheap.
let mutable private subscribers : (string list -> unit) list = []
let mutable private lastPublished : string list = []

let subscribe (f: string list -> unit) : unit =
    subscribers <- f :: subscribers
    f lastPublished

let publish (paths: string list) : unit =
    if paths <> lastPublished then
        lastPublished <- paths
        for f in subscribers do
            try f paths with _ -> ()
