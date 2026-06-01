// Regenerate `tools/viz/drc/base/sky130.yaml` from `Rules.allRules`.
// Run after changing the F#-coded rule table; the
// `Bundled drc/base/sky130.yaml stays in sync with Rules.allRules`
// test will otherwise flag the bundle as stale.
//
// Usage (from any directory):
//   dotnet fsi tools/viz/scripts/dump_drc_yaml.fsx
//
// Requires the Core project to have been built at least once so
// the DLLs exist at the expected paths.

#r "../src/Rekolektion.Viz.Core/bin/Release/net10.0/Rekolektion.Viz.Core.dll"
#r "nuget: YamlDotNet, 16.2.0"

open System.IO
open Rekolektion.Viz.Core.Drc

let scriptDir = __SOURCE_DIRECTORY__
let outPath = Path.GetFullPath(Path.Combine(scriptDir, "..", "drc", "base", "sky130.yaml"))

let yaml = RulesYaml.serialize "sky130" Rules.allRules
Directory.CreateDirectory(Path.GetDirectoryName outPath) |> ignore
File.WriteAllText(outPath, yaml)
printfn "Wrote %d rules to %s (%d bytes)" Rules.allRules.Length outPath yaml.Length
