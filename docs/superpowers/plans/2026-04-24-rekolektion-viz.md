# Rekolektion Viz Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a native Avalonia desktop visualizer for rekolektion SRAM macros and bitcells, modeled on `Moroder.Viz`. Phase 1 ships a multi-project F# .NET 10 solution with 2D Skia + 3D Silk.NET canvases, shared layer/net/block toggle state, file open + run-macro dialog, headless render, and Unix-socket screenshot/command listeners for agent-driven iteration.

**Architecture:** Replace `rekolektion/tools/viz/Viz.fsproj` (single-project) with a multi-project solution under `rekolektion/tools/viz/`: `Rekolektion.Viz.Core` (pure data + logic), `Rekolektion.Viz.Render` (Skia + mesh rendering), `Rekolektion.Viz.App` (Avalonia GUI), `Rekolektion.Viz.Cli` (commands incl. existing `read|render|mesh`), `Rekolektion.Viz.Mcp` (stdio JSON-RPC server). Existing `Gds/`, `Mesh/`, `Render/` source dirs are ported into Core/Render.

**Tech Stack:** F# / .NET 10 / Avalonia 11.3.14 / FuncUI 1.6 / Elmish 4.3 / SkiaSharp / Silk.NET.OpenGL 2.21 / Avalonia.Headless / xunit + FsUnit.Xunit / Python 3 (sidecar emitter only)

**Spec:** `docs/superpowers/specs/2026-04-24-rekolektion-viz-design.md`

**Reference repo for ports:** `/Users/bryancostanich/Git_Repos/bryan_costanich/Moroder/src/Moroder.Viz/`

---

## Conventions

- All paths absolute under `/Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion/` unless noted.
- F# project files use explicit `<Compile Include="…" />` order (mirrors Moroder).
- Tests use **xunit + FsUnit.Xunit**, matching Moroder.Viz.Tests. Run via `dotnet test <path>`.
- Commit messages prefix with `viz:` for scannable history. Never include "Claude" anywhere in commits or trailers.
- Don't push without explicit user approval. Commit locally each task; user pushes in batches.
- The current `tools/viz/` (single `Viz.fsproj`) is being **replaced**. Existing files migrate; the old `.fsproj` is deleted at the end of the scaffold task.
- For long verbatim ports from Moroder, the plan says "port from `<path>` verbatim, change namespace to `Rekolektion.Viz.<X>`" — implementer reads the source file directly rather than the plan duplicating it.

## Pre-flight (do before starting Task 1)

- [ ] Verify clean working tree on `main` branch:
  ```bash
  cd /Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion
  git status
  git rev-parse --abbrev-ref HEAD
  ```
  Expected: `nothing to commit, working tree clean` and `main`. (One uncommitted change is currently expected: `src/rekolektion/macro_v2/write_driver_row.py` from prior work — leave it alone.)
- [ ] Verify .NET 10 SDK installed: `dotnet --version` → `10.x`.
- [ ] Verify Moroder repo accessible for ports: `ls /Users/bryancostanich/Git_Repos/bryan_costanich/Moroder/src/Moroder.Viz/HeadlessRender.fs` exists.

---

## Task 1: Solution scaffold + 5 projects + 4 test projects

**Files:**
- Delete: `tools/viz/Viz.fsproj`, `tools/viz/Program.fs`, `tools/viz/Gds/`, `tools/viz/Mesh/`, `tools/viz/Render/`
  (Wait — don't delete yet. We *move* these in subsequent tasks. For Task 1 we only create the new structure alongside.)
- Create: `tools/viz/Rekolektion.Viz.sln`
- Create: `tools/viz/src/Rekolektion.Viz.Core/Rekolektion.Viz.Core.fsproj`
- Create: `tools/viz/src/Rekolektion.Viz.Render/Rekolektion.Viz.Render.fsproj`
- Create: `tools/viz/src/Rekolektion.Viz.App/Rekolektion.Viz.App.fsproj`
- Create: `tools/viz/src/Rekolektion.Viz.Cli/Rekolektion.Viz.Cli.fsproj`
- Create: `tools/viz/src/Rekolektion.Viz.Mcp/Rekolektion.Viz.Mcp.fsproj`
- Create: `tools/viz/tests/Rekolektion.Viz.Core.Tests/Rekolektion.Viz.Core.Tests.fsproj`
- Create: `tools/viz/tests/Rekolektion.Viz.Render.Tests/Rekolektion.Viz.Render.Tests.fsproj`
- Create: `tools/viz/tests/Rekolektion.Viz.App.Tests/Rekolektion.Viz.App.Tests.fsproj`
- Create: `tools/viz/tests/Rekolektion.Viz.Mcp.Tests/Rekolektion.Viz.Mcp.Tests.fsproj`

- [ ] **Step 1: Create empty fsproj files with the exact package set from Moroder.Viz**

  `Rekolektion.Viz.Core.fsproj` — minimal, no Avalonia:
  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <TargetFramework>net10.0</TargetFramework>
      <LangVersion>latest</LangVersion>
      <OutputType>Library</OutputType>
      <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    </PropertyGroup>
    <ItemGroup>
      <!-- Compile Include lines added in subsequent tasks -->
    </ItemGroup>
  </Project>
  ```

  `Rekolektion.Viz.Render.fsproj` — adds SkiaSharp and Silk.NET:
  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <TargetFramework>net10.0</TargetFramework>
      <LangVersion>latest</LangVersion>
      <OutputType>Library</OutputType>
      <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    </PropertyGroup>
    <ItemGroup>
      <PackageReference Include="SkiaSharp" Version="2.88.8" />
      <PackageReference Include="Silk.NET.OpenGL" Version="2.21.0" />
    </ItemGroup>
    <ItemGroup>
      <ProjectReference Include="..\Rekolektion.Viz.Core\Rekolektion.Viz.Core.fsproj" />
    </ItemGroup>
  </Project>
  ```

  `Rekolektion.Viz.App.fsproj` — Avalonia + FuncUI + Elmish (mirror Moroder.Viz):
  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <TargetFramework>net10.0</TargetFramework>
      <LangVersion>latest</LangVersion>
      <OutputType>Library</OutputType>
      <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    </PropertyGroup>
    <ItemGroup>
      <PackageReference Include="Avalonia" Version="11.3.14" />
      <PackageReference Include="Avalonia.Desktop" Version="11.3.14" />
      <PackageReference Include="Avalonia.Headless" Version="11.3.14" />
      <PackageReference Include="Avalonia.Themes.Fluent" Version="11.3.14" />
      <PackageReference Include="Avalonia.FuncUI" Version="1.6.0" />
      <PackageReference Include="Avalonia.FuncUI.Elmish" Version="1.6.0" />
      <PackageReference Include="Elmish" Version="4.3.0" />
      <PackageReference Include="System.Reactive" Version="6.0.1" />
    </ItemGroup>
    <ItemGroup>
      <ProjectReference Include="..\Rekolektion.Viz.Core\Rekolektion.Viz.Core.fsproj" />
      <ProjectReference Include="..\Rekolektion.Viz.Render\Rekolektion.Viz.Render.fsproj" />
    </ItemGroup>
  </Project>
  ```

  `Rekolektion.Viz.Cli.fsproj` — exe, references Core/Render/App:
  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <TargetFramework>net10.0</TargetFramework>
      <LangVersion>latest</LangVersion>
      <OutputType>Exe</OutputType>
      <AssemblyName>rekolektion-viz</AssemblyName>
      <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    </PropertyGroup>
    <ItemGroup>
      <ProjectReference Include="..\Rekolektion.Viz.Core\Rekolektion.Viz.Core.fsproj" />
      <ProjectReference Include="..\Rekolektion.Viz.Render\Rekolektion.Viz.Render.fsproj" />
      <ProjectReference Include="..\Rekolektion.Viz.App\Rekolektion.Viz.App.fsproj" />
    </ItemGroup>
  </Project>
  ```

  `Rekolektion.Viz.Mcp.fsproj` — exe, references Core/Render/App:
  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <TargetFramework>net10.0</TargetFramework>
      <LangVersion>latest</LangVersion>
      <OutputType>Exe</OutputType>
      <AssemblyName>rekolektion-viz-mcp</AssemblyName>
      <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    </PropertyGroup>
    <ItemGroup>
      <ProjectReference Include="..\Rekolektion.Viz.Core\Rekolektion.Viz.Core.fsproj" />
      <ProjectReference Include="..\Rekolektion.Viz.Render\Rekolektion.Viz.Render.fsproj" />
      <ProjectReference Include="..\Rekolektion.Viz.App\Rekolektion.Viz.App.fsproj" />
    </ItemGroup>
  </Project>
  ```

  Each test fsproj uses xunit + FsUnit (mirror Moroder.Viz.Tests):
  ```xml
  <Project Sdk="Microsoft.NET.Sdk">
    <PropertyGroup>
      <TargetFramework>net10.0</TargetFramework>
      <LangVersion>latest</LangVersion>
      <IsPackable>false</IsPackable>
      <GenerateProgramFile>true</GenerateProgramFile>
    </PropertyGroup>
    <ItemGroup>
      <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.10.0" />
      <PackageReference Include="xunit" Version="2.9.0" />
      <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
      <PackageReference Include="FsUnit.Xunit" Version="6.0.0" />
    </ItemGroup>
    <ItemGroup>
      <ProjectReference Include="..\..\src\Rekolektion.Viz.<Subject>\Rekolektion.Viz.<Subject>.fsproj" />
    </ItemGroup>
  </Project>
  ```

  Replace `<Subject>` with `Core` / `Render` / `App` / `Mcp` for each test project. The App.Tests project additionally needs `Avalonia.Headless`:
  ```xml
  <PackageReference Include="Avalonia.Headless" Version="11.3.14" />
  ```

- [ ] **Step 2: Create solution and add all 9 projects**

  ```bash
  cd /Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion/tools/viz
  dotnet new sln -n Rekolektion.Viz
  dotnet sln add src/Rekolektion.Viz.Core/Rekolektion.Viz.Core.fsproj
  dotnet sln add src/Rekolektion.Viz.Render/Rekolektion.Viz.Render.fsproj
  dotnet sln add src/Rekolektion.Viz.App/Rekolektion.Viz.App.fsproj
  dotnet sln add src/Rekolektion.Viz.Cli/Rekolektion.Viz.Cli.fsproj
  dotnet sln add src/Rekolektion.Viz.Mcp/Rekolektion.Viz.Mcp.fsproj
  dotnet sln add tests/Rekolektion.Viz.Core.Tests/Rekolektion.Viz.Core.Tests.fsproj
  dotnet sln add tests/Rekolektion.Viz.Render.Tests/Rekolektion.Viz.Render.Tests.fsproj
  dotnet sln add tests/Rekolektion.Viz.App.Tests/Rekolektion.Viz.App.Tests.fsproj
  dotnet sln add tests/Rekolektion.Viz.Mcp.Tests/Rekolektion.Viz.Mcp.Tests.fsproj
  ```

- [ ] **Step 3: Stub one F# file per project so the build has something to compile**

  Each project (src and tests) needs at least one `.fs`. Create a placeholder `Placeholder.fs` in each with:
  ```fsharp
  module Rekolektion.Viz.<ProjectShortName>.Placeholder
  let private _placeholder = ()
  ```
  Add `<Compile Include="Placeholder.fs" />` to each fsproj. For Cli and Mcp (Exe), `Placeholder.fs` becomes:
  ```fsharp
  module Rekolektion.Viz.Cli.Placeholder
  [<EntryPoint>]
  let main _ = 0
  ```
  (Same for Mcp, with `Mcp.Placeholder`.)

- [ ] **Step 4: Build the solution — must be green**

  ```bash
  cd /Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion/tools/viz
  dotnet build Rekolektion.Viz.sln
  ```
  Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 5: Run tests — empty pass**

  ```bash
  dotnet test Rekolektion.Viz.sln
  ```
  Expected: `Passed: 0` for each test project, exit 0.

- [ ] **Step 6: Commit**

  ```bash
  cd /Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion
  git add tools/viz/Rekolektion.Viz.sln tools/viz/src/ tools/viz/tests/
  git commit -m "viz: scaffold multi-project solution + placeholder builds"
  ```

  Note: the existing `tools/viz/Viz.fsproj` and its `Gds/Mesh/Render/Program.fs` files are still present and unchanged. Subsequent tasks port their contents into the new structure, and the final scaffold task deletes the old project.

---

## Task 2: Port `Gds/Types.fs` to Core

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Core/Gds/Types.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.Core.Tests/GdsTypesTests.fs`
- Modify: `tools/viz/src/Rekolektion.Viz.Core/Rekolektion.Viz.Core.fsproj` (add Compile)
- Modify: `tools/viz/tests/Rekolektion.Viz.Core.Tests/Rekolektion.Viz.Core.Tests.fsproj` (add Compile)

- [ ] **Step 1: Read the existing types**

  Read `tools/viz/Gds/Types.fs` to learn its DU shape (Boundary/Path/SRef/ARef + Library/Structure + Point).

- [ ] **Step 2: Write a failing test asserting type construction**

  `tests/Rekolektion.Viz.Core.Tests/GdsTypesTests.fs`:
  ```fsharp
  module Rekolektion.Viz.Core.Tests.GdsTypesTests

  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Core.Gds.Types

  [<Fact>]
  let ``Point holds X Y as int64 DBU`` () =
      let p = { X = 12300L; Y = -4500L }
      p.X |> should equal 12300L
      p.Y |> should equal -4500L

  [<Fact>]
  let ``Boundary holds layer datatype and point list`` () =
      let b = { Layer = 68; DataType = 20; Points = [ { X = 0L; Y = 0L }; { X = 100L; Y = 0L } ] }
      b.Layer |> should equal 68
      b.Points |> List.length |> should equal 2

  [<Fact>]
  let ``Element DU includes all four GDS element types`` () =
      let _b: Element = Boundary { Layer = 0; DataType = 0; Points = [] }
      let _p: Element = Path { Layer = 0; DataType = 0; Width = 0; Points = [] }
      let _s: Element = SRef { StructureName = "x"; Origin = { X = 0L; Y = 0L }; Mag = 1.0; Angle = 0.0; Reflected = false }
      let _a: Element = ARef { StructureName = "x"; Origin = { X = 0L; Y = 0L }; Cols = 1; Rows = 1; ColPitch = { X = 0L; Y = 0L }; RowPitch = { X = 0L; Y = 0L }; Mag = 1.0; Angle = 0.0; Reflected = false }
      ()
  ```

- [ ] **Step 3: Run, verify FAIL with "module Types not found"**

  ```bash
  dotnet test tools/viz/tests/Rekolektion.Viz.Core.Tests/ --filter "GdsTypesTests"
  ```

- [ ] **Step 4: Create the Types module**

  `src/Rekolektion.Viz.Core/Gds/Types.fs`:
  ```fsharp
  module Rekolektion.Viz.Core.Gds.Types

  /// GDS coordinates are integer database units (DBU). Conversion to
  /// micrometers happens at display time using Library.DbUnitsPerUserUnit.
  type Point = { X: int64; Y: int64 }

  type Boundary = {
      Layer: int
      DataType: int
      Points: Point list      // closed polygon, first = last
  }

  type Path = {
      Layer: int
      DataType: int
      Width: int               // DBU
      Points: Point list
  }

  type SRef = {
      StructureName: string
      Origin: Point
      Mag: float
      Angle: float             // degrees, CCW
      Reflected: bool          // reflect about X axis before rotation
  }

  type ARef = {
      StructureName: string
      Origin: Point
      Cols: int
      Rows: int
      ColPitch: Point          // vector from origin to next column anchor
      RowPitch: Point
      Mag: float
      Angle: float
      Reflected: bool
  }

  type TextLabel = {
      Layer: int
      TextType: int
      Origin: Point
      Text: string
  }

  type Element =
      | Boundary of Boundary
      | Path of Path
      | SRef of SRef
      | ARef of ARef
      | Text of TextLabel

  type Structure = {
      Name: string
      Elements: Element list
  }

  type Library = {
      Name: string
      DbUnitsPerUserUnit: float    // user units per DB unit (e.g. 0.001 μm/DBU)
      DbUnitsInMeters: float       // meters per DB unit (e.g. 1e-9)
      Structures: Structure list
  }
  ```

- [ ] **Step 5: Add Compile to fsproj**

  Append to `Rekolektion.Viz.Core.fsproj` ItemGroup, BEFORE the Placeholder line:
  ```xml
  <Compile Include="Gds/Types.fs" />
  ```

  Append to `Rekolektion.Viz.Core.Tests.fsproj` ItemGroup, BEFORE the Placeholder line:
  ```xml
  <Compile Include="GdsTypesTests.fs" />
  ```

- [ ] **Step 6: Run tests — must pass**

  ```bash
  dotnet test tools/viz/tests/Rekolektion.Viz.Core.Tests/ --filter "GdsTypesTests"
  ```
  Expected: 3 passed.

- [ ] **Step 7: Commit**

  ```bash
  git add tools/viz/src/Rekolektion.Viz.Core/Gds/Types.fs \
          tools/viz/src/Rekolektion.Viz.Core/Rekolektion.Viz.Core.fsproj \
          tools/viz/tests/Rekolektion.Viz.Core.Tests/GdsTypesTests.fs \
          tools/viz/tests/Rekolektion.Viz.Core.Tests/Rekolektion.Viz.Core.Tests.fsproj
  git commit -m "viz: Core.Gds.Types — DBU-native GDS data types"
  ```

---

## Task 3: Port `Gds/Reader.fs` to Core (binary GDS parser)

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Core/Gds/Reader.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.Core.Tests/GdsReaderTests.fs`
- Modify: both fsprojs (add Compile)
- Use existing GDS fixture: `tools/viz/Gds/Reader.fs` reads a known fixture; we'll re-use `output/sky130_6t_lr.gds` if present, otherwise generate it via the Python CLI in the test arrange step.

- [ ] **Step 1: Read the existing parser** — `tools/viz/Gds/Reader.fs`. Note record format, big-endian, 2-byte record length + 1-byte record type + 1-byte data type.

- [ ] **Step 2: Generate or locate a fixture GDS**

  ```bash
  cd /Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion
  ls output/sky130_6t_lr.gds || \
    python3 -c "from rekolektion.bitcell.sky130_6t_lr import generate_bitcell; generate_bitcell('output/sky130_6t_lr.gds')"
  ```

  Copy that fixture into the test corpus (committed):
  ```bash
  mkdir -p tools/viz/testdata
  cp output/sky130_6t_lr.gds tools/viz/testdata/bitcell_lr.gds
  ```

- [ ] **Step 3: Write failing test that reads the fixture**

  `tests/Rekolektion.Viz.Core.Tests/GdsReaderTests.fs`:
  ```fsharp
  module Rekolektion.Viz.Core.Tests.GdsReaderTests

  open System.IO
  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Core.Gds

  let private fixturePath name =
      // Tests run from bin/Debug/net10.0/; walk up to tools/viz/testdata
      let here = System.AppContext.BaseDirectory
      Path.GetFullPath(Path.Combine(here, "../../../../../testdata", name))

  [<Fact>]
  let ``Reader.readGds parses bitcell_lr fixture`` () =
      let lib = Reader.readGds (fixturePath "bitcell_lr.gds")
      lib.Name |> should not' (equal "")
      lib.Structures |> List.isEmpty |> should equal false
      lib.DbUnitsPerUserUnit |> should be (greaterThan 0.0)

  [<Fact>]
  let ``Reader.readGds bitcell_lr has at least one boundary`` () =
      let lib = Reader.readGds (fixturePath "bitcell_lr.gds")
      let total =
          lib.Structures
          |> List.sumBy (fun s ->
              s.Elements
              |> List.filter (function Types.Boundary _ -> true | _ -> false)
              |> List.length)
      total |> should be (greaterThan 0)
  ```

- [ ] **Step 4: Run, verify FAIL**

  ```bash
  dotnet test tools/viz/tests/Rekolektion.Viz.Core.Tests/ --filter "GdsReaderTests"
  ```
  Expected: FAIL with "Reader namespace not found".

- [ ] **Step 5: Port the existing reader**

  Copy `tools/viz/Gds/Reader.fs` to `tools/viz/src/Rekolektion.Viz.Core/Gds/Reader.fs`. Change the `module Viz.Gds.Reader` line to `module Rekolektion.Viz.Core.Gds.Reader`. Change any `open Viz.Gds.Types` to `open Rekolektion.Viz.Core.Gds.Types`. Keep parsing logic byte-for-byte identical.

- [ ] **Step 6: Update fsprojs**

  `Rekolektion.Viz.Core.fsproj` Compile order:
  ```xml
  <Compile Include="Gds/Types.fs" />
  <Compile Include="Gds/Reader.fs" />
  <Compile Include="Placeholder.fs" />
  ```

  `Rekolektion.Viz.Core.Tests.fsproj`:
  ```xml
  <Compile Include="GdsTypesTests.fs" />
  <Compile Include="GdsReaderTests.fs" />
  <Compile Include="Placeholder.fs" />
  ```

  Also add the testdata file as content so it's copied to bin:
  ```xml
  <ItemGroup>
    <Content Include="..\..\testdata\bitcell_lr.gds">
      <Link>testdata\bitcell_lr.gds</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
  ```

  Update `fixturePath` accordingly — testdata is now copied next to the binaries; simplify:
  ```fsharp
  let private fixturePath name =
      Path.Combine(System.AppContext.BaseDirectory, "testdata", name)
  ```

- [ ] **Step 7: Run tests — pass**

  ```bash
  dotnet test tools/viz/tests/Rekolektion.Viz.Core.Tests/ --filter "GdsReaderTests"
  ```
  Expected: 2 passed.

- [ ] **Step 8: Commit**

  ```bash
  git add tools/viz/src/Rekolektion.Viz.Core/Gds/Reader.fs \
          tools/viz/src/Rekolektion.Viz.Core/Rekolektion.Viz.Core.fsproj \
          tools/viz/tests/Rekolektion.Viz.Core.Tests/GdsReaderTests.fs \
          tools/viz/tests/Rekolektion.Viz.Core.Tests/Rekolektion.Viz.Core.Tests.fsproj \
          tools/viz/testdata/bitcell_lr.gds
  git commit -m "viz: Core.Gds.Reader — port binary GDS parser, fixture-tested"
  ```

---

## Task 4: Core.Layout.Layer — SKY130 layer table

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Core/Layout/Layer.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.Core.Tests/LayerTests.fs`
- Modify: both fsprojs

- [ ] **Step 1: Write failing test**

  `tests/Rekolektion.Viz.Core.Tests/LayerTests.fs`:
  ```fsharp
  module Rekolektion.Viz.Core.Tests.LayerTests

  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Core.Layout

  [<Fact>]
  let ``Layer.bySky130Number returns met2 for layer 68`` () =
      match Layer.bySky130Number 68 20 with
      | Some l -> l.Name |> should equal "met2"
      | None -> failwith "expected met2"

  [<Fact>]
  let ``Layer.bySky130Number returns li1 for layer 67`` () =
      match Layer.bySky130Number 67 20 with
      | Some l -> l.Name |> should equal "li1"
      | None -> failwith "expected li1"

  [<Fact>]
  let ``Layer.allDrawing returns at least 8 entries`` () =
      Layer.allDrawing |> List.length |> should be (greaterThanOrEqualTo 8)

  [<Fact>]
  let ``Layer stack Z increases monotonically`` () =
      let zs = Layer.allDrawing |> List.sortBy (fun l -> l.StackZ) |> List.map (fun l -> l.StackZ)
      zs |> should equal (List.sort zs)
  ```

- [ ] **Step 2: Run, verify FAIL** — `Layout namespace not found`.

- [ ] **Step 3: Implement**

  `src/Rekolektion.Viz.Core/Layout/Layer.fs`:
  ```fsharp
  module Rekolektion.Viz.Core.Layout.Layer

  type ColorRgba = { R: byte; G: byte; B: byte; A: byte }

  type Layer = {
      Number   : int       // GDS layer number
      DataType : int       // GDS datatype (20 for drawing in SKY130)
      Name     : string
      Color    : ColorRgba
      StackZ   : float     // bottom of layer in 3D extrusion (μm)
      Thickness: float     // extrusion thickness (μm)
  }

  let private rgba r g b a = { R = byte r; G = byte g; B = byte b; A = byte a }

  /// SKY130 drawing layers we care about for SRAM viz. Z heights are
  /// approximate process-stack values (top-of-substrate to top-of-met5)
  /// in μm. Colors loosely follow Magic's default theme so the viz feels
  /// familiar to anyone who has used Magic.
  let allDrawing : Layer list = [
      { Number =  64; DataType = 20; Name = "nwell";   Color = rgba 0x66 0x66 0x66 0xff; StackZ = -0.50; Thickness = 0.30 }
      { Number =  65; DataType = 20; Name = "diff";    Color = rgba 0x48 0x84 0x48 0xff; StackZ = -0.20; Thickness = 0.15 }
      { Number =  66; DataType = 20; Name = "poly";    Color = rgba 0xa4 0x44 0x44 0xff; StackZ =  0.05; Thickness = 0.18 }
      { Number =  67; DataType = 20; Name = "li1";     Color = rgba 0xc8 0x84 0x44 0xff; StackZ =  0.40; Thickness = 0.10 }
      { Number =  68; DataType = 20; Name = "met1";    Color = rgba 0x48 0x88 0xaa 0xff; StackZ =  0.65; Thickness = 0.36 }
      { Number =  69; DataType = 20; Name = "met2";    Color = rgba 0x55 0xaa 0x88 0xff; StackZ =  1.20; Thickness = 0.36 }
      { Number =  70; DataType = 20; Name = "met3";    Color = rgba 0x33 0xaa 0xaa 0xff; StackZ =  1.78; Thickness = 0.85 }
      { Number =  71; DataType = 20; Name = "met4";    Color = rgba 0xaa 0x88 0xaa 0xff; StackZ =  2.78; Thickness = 0.85 }
      { Number =  72; DataType = 20; Name = "met5";    Color = rgba 0xbb 0xbb 0x66 0xff; StackZ =  3.78; Thickness = 1.26 }
      // Vias / contacts (data type 44 in SKY130)
      { Number =  66; DataType = 44; Name = "licon";   Color = rgba 0xff 0xff 0xff 0x60; StackZ =  0.23; Thickness = 0.17 }
      { Number =  67; DataType = 44; Name = "mcon";    Color = rgba 0xff 0xff 0xff 0x60; StackZ =  0.50; Thickness = 0.15 }
      { Number =  68; DataType = 44; Name = "via";     Color = rgba 0xff 0xff 0xff 0x60; StackZ =  1.01; Thickness = 0.19 }
      { Number =  69; DataType = 44; Name = "via2";    Color = rgba 0xff 0xff 0xff 0x60; StackZ =  1.56; Thickness = 0.22 }
      { Number =  70; DataType = 44; Name = "via3";    Color = rgba 0xff 0xff 0xff 0x60; StackZ =  2.63; Thickness = 0.15 }
      { Number =  71; DataType = 44; Name = "via4";    Color = rgba 0xff 0xff 0xff 0x60; StackZ =  3.63; Thickness = 0.15 }
      // Marker
      { Number =  81; DataType =  2; Name = "areaid.sc"; Color = rgba 0xff 0x00 0xff 0x40; StackZ =  4.00; Thickness = 0.05 }
  ]

  let private byKey =
      allDrawing |> List.map (fun l -> (l.Number, l.DataType), l) |> Map.ofList

  let bySky130Number (number: int) (dataType: int) : Layer option =
      Map.tryFind (number, dataType) byKey
  ```

  > **Verify Z values against the SKY130 PDK** before locking. The values above are reasonable defaults — if SKY130 process docs disagree, replace with the doc's values. Source: `~/.volare/sky130B/libs.tech/openlane/sky130_fd_sc_hd/config.tcl` or the SkyWater PDK `sky130_fd_pr` tech files.

- [ ] **Step 4: Add Compile, run tests, commit**

  ```xml
  <Compile Include="Gds/Types.fs" />
  <Compile Include="Gds/Reader.fs" />
  <Compile Include="Layout/Layer.fs" />
  <Compile Include="Placeholder.fs" />
  ```

  ```bash
  dotnet test tools/viz/tests/Rekolektion.Viz.Core.Tests/ --filter "LayerTests"
  ```
  Expected: 4 passed.

  ```bash
  git add tools/viz/src/Rekolektion.Viz.Core/Layout/Layer.fs \
          tools/viz/src/Rekolektion.Viz.Core/Rekolektion.Viz.Core.fsproj \
          tools/viz/tests/Rekolektion.Viz.Core.Tests/LayerTests.fs \
          tools/viz/tests/Rekolektion.Viz.Core.Tests/Rekolektion.Viz.Core.Tests.fsproj
  git commit -m "viz: Core.Layout.Layer — SKY130 layer table with stack Z"
  ```

---

## Task 5: Core.Sidecar — Types + Loader

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Core/Sidecar/Types.fs`
- Create: `tools/viz/src/Rekolektion.Viz.Core/Sidecar/Loader.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.Core.Tests/SidecarLoaderTests.fs`
- Create: `tools/viz/testdata/bitcell_lr.nets.json` (small hand-written fixture for now; real emitter comes in Task 28)
- Modify: both fsprojs

- [ ] **Step 1: Add System.Text.Json package to Core.fsproj**

  ```xml
  <ItemGroup>
    <PackageReference Include="System.Text.Json" Version="9.0.0" />
  </ItemGroup>
  ```

- [ ] **Step 2: Write fixture sidecar JSON**

  `tools/viz/testdata/bitcell_lr.nets.json`:
  ```json
  {
    "version": 1,
    "macro": "sky130_sram_6t_bitcell_lr",
    "nets": {
      "VPWR": {
        "class": "power",
        "polygons": [
          { "structure": "sky130_sram_6t_bitcell_lr", "layer": 68, "datatype": 20, "index": 0 }
        ]
      },
      "VGND": {
        "class": "ground",
        "polygons": [
          { "structure": "sky130_sram_6t_bitcell_lr", "layer": 68, "datatype": 20, "index": 1 }
        ]
      },
      "WL": {
        "class": "signal",
        "polygons": [
          { "structure": "sky130_sram_6t_bitcell_lr", "layer": 67, "datatype": 20, "index": 5 }
        ]
      }
    }
  }
  ```

  Wire it into the test fsproj `<Content>` block alongside the GDS.

- [ ] **Step 3: Failing test**

  `tests/Rekolektion.Viz.Core.Tests/SidecarLoaderTests.fs`:
  ```fsharp
  module Rekolektion.Viz.Core.Tests.SidecarLoaderTests

  open System.IO
  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Core.Sidecar

  let private fixturePath name =
      Path.Combine(System.AppContext.BaseDirectory, "testdata", name)

  [<Fact>]
  let ``Loader.load returns Some for valid sidecar`` () =
      match Loader.load (fixturePath "bitcell_lr.nets.json") with
      | Some sc ->
          sc.Version |> should equal 1
          sc.Macro |> should equal "sky130_sram_6t_bitcell_lr"
          sc.Nets.Count |> should be (greaterThanOrEqualTo 3)
      | None -> failwith "expected Some"

  [<Fact>]
  let ``Loader.load returns None for missing file`` () =
      Loader.load "/tmp/does-not-exist.nets.json" |> should equal (None: Types.Sidecar option)

  [<Fact>]
  let ``Sidecar exposes power class for VPWR`` () =
      match Loader.load (fixturePath "bitcell_lr.nets.json") with
      | Some sc ->
          sc.Nets.["VPWR"].Class |> should equal Types.NetClass.Power
      | None -> failwith "expected Some"
  ```

- [ ] **Step 4: Run, verify FAIL** — `Sidecar namespace not found`.

- [ ] **Step 5: Implement Types**

  `src/Rekolektion.Viz.Core/Sidecar/Types.fs`:
  ```fsharp
  module Rekolektion.Viz.Core.Sidecar.Types

  type NetClass = Power | Ground | Signal | Clock

  type PolygonRef = {
      Structure: string
      Layer    : int
      DataType : int
      Index    : int       // ordinal within structure's element list
  }

  type NetEntry = {
      Name    : string
      Class   : NetClass
      Polygons: PolygonRef list
  }

  type Sidecar = {
      Version: int        // = 1
      Macro  : string
      Nets   : Map<string, NetEntry>
  }
  ```

- [ ] **Step 6: Implement Loader**

  `src/Rekolektion.Viz.Core/Sidecar/Loader.fs`:
  ```fsharp
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
              let nets =
                  root.GetProperty("nets").EnumerateObject()
                  |> Seq.map (fun p -> p.Name, parseNetEntry p.Name p.Value)
                  |> Map.ofSeq
              Some {
                  Version = root.GetProperty("version").GetInt32()
                  Macro   = root.GetProperty("macro").GetString()
                  Nets    = nets
              }
          with _ -> None
  ```

- [ ] **Step 7: Add compiles, run tests, commit**

  Compile order in Core.fsproj:
  ```xml
  <Compile Include="Gds/Types.fs" />
  <Compile Include="Gds/Reader.fs" />
  <Compile Include="Layout/Layer.fs" />
  <Compile Include="Sidecar/Types.fs" />
  <Compile Include="Sidecar/Loader.fs" />
  <Compile Include="Placeholder.fs" />
  ```
  Tests fsproj: add `SidecarLoaderTests.fs` after `LayerTests.fs`. Add `bitcell_lr.nets.json` Content entry mirroring the GDS one.

  ```bash
  dotnet test tools/viz/tests/Rekolektion.Viz.Core.Tests/ --filter "SidecarLoaderTests"
  git add ...
  git commit -m "viz: Core.Sidecar — types + JSON loader for net→polygon map"
  ```

---

## Task 6: Core.Layout.Hierarchy — sub-block detector

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Core/Layout/Hierarchy.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.Core.Tests/HierarchyTests.fs`
- Modify: both fsprojs

- [ ] **Step 1: Failing test**

  ```fsharp
  module Rekolektion.Viz.Core.Tests.HierarchyTests

  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Core.Gds.Types
  open Rekolektion.Viz.Core.Layout

  let private mkStruct name = { Name = name; Elements = [] }

  [<Fact>]
  let ``Hierarchy.detect identifies known sram sub-blocks`` () =
      let lib = {
          Name = "test"; DbUnitsPerUserUnit = 0.001; DbUnitsInMeters = 1e-9
          Structures = [
              mkStruct "macro_v2_top"
              mkStruct "sram_array"
              mkStruct "precharge_row"
              mkStruct "column_mux"
              mkStruct "sense_amp_row"
              mkStruct "wl_driver_row"
              mkStruct "row_decoder"
              mkStruct "ctrl_logic"
              mkStruct "unrelated_thing"
          ]
      }
      let blocks = Hierarchy.detect lib
      blocks |> List.map (fun b -> b.Name) |> List.contains "sram_array" |> should equal true
      blocks |> List.map (fun b -> b.Name) |> List.contains "precharge_row" |> should equal true
      blocks |> List.length |> should be (greaterThanOrEqualTo 7)

  [<Fact>]
  let ``Hierarchy.detect classifies blocks by role`` () =
      let lib = {
          Name = "x"; DbUnitsPerUserUnit = 0.001; DbUnitsInMeters = 1e-9
          Structures = [mkStruct "sram_array"; mkStruct "row_decoder"]
      }
      let blocks = Hierarchy.detect lib
      let arr = blocks |> List.find (fun b -> b.Name = "sram_array")
      arr.Role |> should equal Hierarchy.BlockRole.Array
      let dec = blocks |> List.find (fun b -> b.Name = "row_decoder")
      dec.Role |> should equal Hierarchy.BlockRole.Decoder
  ```

- [ ] **Step 2: Run, verify FAIL.**

- [ ] **Step 3: Implement**

  `src/Rekolektion.Viz.Core/Layout/Hierarchy.fs`:
  ```fsharp
  module Rekolektion.Viz.Core.Layout.Hierarchy

  open Rekolektion.Viz.Core.Gds.Types

  type BlockRole =
      | Top
      | Array
      | Precharge
      | ColumnMux
      | SenseAmp
      | WriteDriver
      | WordlineDriver
      | Decoder
      | Control
      | Bitcell
      | Other

  type Block = {
      Name      : string
      Role      : BlockRole
      Children  : string list   // names of structures referenced via SRef/ARef
  }

  let private roleOfName (n: string) : BlockRole =
      let lower = n.ToLowerInvariant()
      if lower.Contains "sram_array"             then Array
      elif lower.Contains "precharge"            then Precharge
      elif lower.Contains "col_mux"              then ColumnMux
      elif lower.Contains "column_mux"           then ColumnMux
      elif lower.Contains "sense_amp"            then SenseAmp
      elif lower.Contains "write_driver"         then WriteDriver
      elif lower.Contains "wd_row"               then WriteDriver
      elif lower.Contains "wl_driver"            then WordlineDriver
      elif lower.Contains "decoder"              then Decoder
      elif lower.Contains "ctrl"                 then Control
      elif lower.Contains "bitcell"              then Bitcell
      elif lower.Contains "macro" && lower.Contains "top" then Top
      elif lower.EndsWith "_top"                 then Top
      else Other

  let private childrenOf (s: Structure) : string list =
      s.Elements
      |> List.choose (function
          | SRef sr -> Some sr.StructureName
          | ARef ar -> Some ar.StructureName
          | _ -> None)
      |> List.distinct

  /// Build a flat list of blocks for every structure in the library,
  /// skipping `Other` blocks unless they reference children (an "Other"
  /// with no children is almost certainly a leaf cell — bitcell, std
  /// cell instance — and isn't useful in the block tree).
  let detect (lib: Library) : Block list =
      lib.Structures
      |> List.choose (fun s ->
          let role = roleOfName s.Name
          let children = childrenOf s
          match role, children with
          | Other, [] -> None
          | _ -> Some { Name = s.Name; Role = role; Children = children })
  ```

- [ ] **Step 4: Add compile, run tests, commit**

  ```bash
  dotnet test tools/viz/tests/Rekolektion.Viz.Core.Tests/ --filter "HierarchyTests"
  git commit -m "viz: Core.Layout.Hierarchy — detect sub-blocks by structure name"
  ```

---

## Task 7: Core.Visibility — ToggleState reducer

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Core/Visibility.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.Core.Tests/VisibilityTests.fs`

- [ ] **Step 1: Failing test**

  ```fsharp
  module Rekolektion.Viz.Core.Tests.VisibilityTests

  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Core

  [<Fact>]
  let ``empty ToggleState shows everything`` () =
      let s = Visibility.empty
      Visibility.isLayerVisible s (68, 20) |> should equal true
      Visibility.isNetVisible s "BL" |> should equal true
      Visibility.isBlockVisible s "sram_array" |> should equal true

  [<Fact>]
  let ``toggling layer off hides it`` () =
      let s = Visibility.empty |> Visibility.toggleLayer (68, 20) false
      Visibility.isLayerVisible s (68, 20) |> should equal false
      Visibility.isLayerVisible s (69, 20) |> should equal true

  [<Fact>]
  let ``highlightNet sets HighlightNet and dims others`` () =
      let s = Visibility.empty |> Visibility.highlightNet (Some "BL_3")
      s.HighlightNet |> should equal (Some "BL_3")
      Visibility.isNetDimmed s "VPWR" |> should equal true
      Visibility.isNetDimmed s "BL_3" |> should equal false

  [<Fact>]
  let ``isolateBlock hides all other blocks`` () =
      let s = Visibility.empty |> Visibility.isolateBlock (Some "sram_array")
      Visibility.isBlockVisible s "sram_array" |> should equal true
      Visibility.isBlockVisible s "row_decoder" |> should equal false
  ```

- [ ] **Step 2: Run, verify FAIL.**

- [ ] **Step 3: Implement**

  `src/Rekolektion.Viz.Core/Visibility.fs`:
  ```fsharp
  module Rekolektion.Viz.Core.Visibility

  type LayerKey = int * int       // (number, datatype)

  type ToggleState = {
      Layers        : Map<LayerKey, bool>
      Nets          : Map<string, bool>
      Blocks        : Map<string, bool>
      HighlightNet  : string option       // dim everything else when set
      IsolatedBlock : string option       // when set, hide other blocks
  }

  let empty : ToggleState = {
      Layers = Map.empty
      Nets = Map.empty
      Blocks = Map.empty
      HighlightNet = None
      IsolatedBlock = None
  }

  let isLayerVisible (s: ToggleState) (key: LayerKey) : bool =
      Map.tryFind key s.Layers |> Option.defaultValue true

  let isNetVisible (s: ToggleState) (name: string) : bool =
      Map.tryFind name s.Nets |> Option.defaultValue true

  let isNetDimmed (s: ToggleState) (name: string) : bool =
      match s.HighlightNet with
      | Some h -> h <> name
      | None -> false

  let isBlockVisible (s: ToggleState) (name: string) : bool =
      let explicit = Map.tryFind name s.Blocks |> Option.defaultValue true
      let isolated =
          match s.IsolatedBlock with
          | Some iso -> iso = name
          | None -> true
      explicit && isolated

  let toggleLayer (key: LayerKey) (visible: bool) (s: ToggleState) : ToggleState =
      { s with Layers = Map.add key visible s.Layers }

  let toggleNet (name: string) (visible: bool) (s: ToggleState) : ToggleState =
      { s with Nets = Map.add name visible s.Nets }

  let toggleBlock (name: string) (visible: bool) (s: ToggleState) : ToggleState =
      { s with Blocks = Map.add name visible s.Blocks }

  let highlightNet (net: string option) (s: ToggleState) : ToggleState =
      { s with HighlightNet = net }

  let isolateBlock (block: string option) (s: ToggleState) : ToggleState =
      { s with IsolatedBlock = block }
  ```

- [ ] **Step 4: Add compile, run tests, commit**

  ```bash
  dotnet test tools/viz/tests/Rekolektion.Viz.Core.Tests/ --filter "VisibilityTests"
  git commit -m "viz: Core.Visibility — ToggleState reducer for layer/net/block"
  ```

---

## Task 8: Core.Layout.Picking — point-in-polygon + ray-vs-extruded

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Core/Layout/Picking.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.Core.Tests/PickingTests.fs`

- [ ] **Step 1: Failing test**

  ```fsharp
  module Rekolektion.Viz.Core.Tests.PickingTests

  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Core.Gds.Types
  open Rekolektion.Viz.Core.Layout

  let private square (x: int64) (y: int64) (size: int64) : Point list =
      [ { X = x;        Y = y }
        { X = x + size; Y = y }
        { X = x + size; Y = y + size }
        { X = x;        Y = y + size }
        { X = x;        Y = y } ]

  [<Fact>]
  let ``point inside square is contained`` () =
      let poly = square 0L 0L 100L
      Picking.pointInPolygon { X = 50L; Y = 50L } poly |> should equal true

  [<Fact>]
  let ``point outside square is not contained`` () =
      let poly = square 0L 0L 100L
      Picking.pointInPolygon { X = 150L; Y = 50L } poly |> should equal false

  [<Fact>]
  let ``point on edge is contained (boundary inclusive)`` () =
      let poly = square 0L 0L 100L
      Picking.pointInPolygon { X = 0L; Y = 50L } poly |> should equal true

  [<Fact>]
  let ``L-shape: concavity excluded`` () =
      // L-shape: 100x100 square with the upper-right 50x50 carved out
      let poly = [
          { X = 0L;   Y = 0L }
          { X = 100L; Y = 0L }
          { X = 100L; Y = 50L }
          { X = 50L;  Y = 50L }
          { X = 50L;  Y = 100L }
          { X = 0L;   Y = 100L }
          { X = 0L;   Y = 0L }
      ]
      Picking.pointInPolygon { X = 75L; Y = 75L } poly |> should equal false
      Picking.pointInPolygon { X = 25L; Y = 25L } poly |> should equal true
  ```

- [ ] **Step 2: Run, verify FAIL.**

- [ ] **Step 3: Implement**

  `src/Rekolektion.Viz.Core/Layout/Picking.fs`:
  ```fsharp
  module Rekolektion.Viz.Core.Layout.Picking

  open Rekolektion.Viz.Core.Gds.Types

  /// Crossing-number / even-odd rule. Boundary-inclusive: a point that
  /// lands exactly on an edge is treated as "in" so the picker doesn't
  /// have dead spots between adjacent rectangles.
  let pointInPolygon (p: Point) (poly: Point list) : bool =
      // Strip closing point if present so we don't double-count edges.
      let pts =
          match poly with
          | [] -> []
          | _ ->
              let last = List.last poly
              if last = List.head poly then poly |> List.take (List.length poly - 1)
              else poly
      let n = List.length pts
      if n < 3 then false
      else
          let arr = List.toArray pts
          let mutable inside = false
          let mutable onEdge = false
          for i in 0 .. n - 1 do
              let a = arr.[i]
              let b = arr.[(i + 1) % n]
              // Edge inclusion: collinear and within bbox of segment.
              let cross =
                  (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)
              let withinX = (min a.X b.X) <= p.X && p.X <= (max a.X b.X)
              let withinY = (min a.Y b.Y) <= p.Y && p.Y <= (max a.Y b.Y)
              if cross = 0L && withinX && withinY then
                  onEdge <- true
              // Standard ray cast (point shoots ray to +X). Use inclusive at
              // bottom, exclusive at top to avoid double-counting on y-vertices.
              if (a.Y > p.Y) <> (b.Y > p.Y) then
                  let xIntersect =
                      float (b.X - a.X) * float (p.Y - a.Y)
                          / float (b.Y - a.Y) + float a.X
                  if float p.X < xIntersect then
                      inside <- not inside
          inside || onEdge

  /// Pick the first matching boundary in a structure's element list.
  /// Returns the element index alongside so the caller can relate it
  /// to a Sidecar PolygonRef.
  let pickBoundary (point: Point) (elements: Element list) : (int * Boundary) option =
      elements
      |> List.indexed
      |> List.tryPick (fun (i, e) ->
          match e with
          | Boundary b when pointInPolygon point b.Points -> Some (i, b)
          | _ -> None)
  ```

  Note: 3D ray-vs-extruded picking is implemented in Render via GPU color ID buffer (Task 13), not here. This module covers 2D and provides a general `pointInPolygon` that the 3D picker also uses for face hit-tests after ray un-projection.

- [ ] **Step 4: Add compile, run tests, commit**

  ```bash
  dotnet test tools/viz/tests/Rekolektion.Viz.Core.Tests/ --filter "PickingTests"
  git commit -m "viz: Core.Layout.Picking — pointInPolygon + boundary picker"
  ```

---

## Task 9: Core.Net.LabelFlood — fallback net derivation from labels

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Core/Net/LabelFlood.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.Core.Tests/LabelFloodTests.fs`

- [ ] **Step 1: Failing test**

  Build a hand-crafted Library with two labeled rectangles on the same layer overlapping a third (unlabeled) rectangle. Expect: all three end up in the same net (the label propagates).

  ```fsharp
  module Rekolektion.Viz.Core.Tests.LabelFloodTests

  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Core.Gds.Types
  open Rekolektion.Viz.Core.Net

  let private rect (x: int64) (y: int64) (w: int64) (h: int64) : Point list =
      [ { X = x;     Y = y     }
        { X = x + w; Y = y     }
        { X = x + w; Y = y + h }
        { X = x;     Y = y + h }
        { X = x;     Y = y     } ]

  [<Fact>]
  let ``label on a polygon names that polygon's net`` () =
      let lib = {
          Name = "x"; DbUnitsPerUserUnit = 0.001; DbUnitsInMeters = 1e-9
          Structures = [{
              Name = "top"
              Elements = [
                  Boundary { Layer = 68; DataType = 20; Points = rect 0L 0L 100L 50L }
                  Text     { Layer = 68; TextType = 5; Origin = { X = 50L; Y = 25L }; Text = "BL" }
              ]
          }]
      }
      let nets = LabelFlood.derive lib
      nets.ContainsKey "BL" |> should equal true
      nets.["BL"].Polygons |> List.length |> should equal 1

  [<Fact>]
  let ``label on overlapping polys connects both`` () =
      let lib = {
          Name = "x"; DbUnitsPerUserUnit = 0.001; DbUnitsInMeters = 1e-9
          Structures = [{
              Name = "top"
              Elements = [
                  Boundary { Layer = 68; DataType = 20; Points = rect 0L  0L 100L 50L } // labeled
                  Boundary { Layer = 68; DataType = 20; Points = rect 80L 0L 100L 50L } // overlaps first
                  Text     { Layer = 68; TextType = 5; Origin = { X = 10L; Y = 25L }; Text = "WL" }
              ]
          }]
      }
      let nets = LabelFlood.derive lib
      nets.["WL"].Polygons |> List.length |> should equal 2
  ```

- [ ] **Step 2: Run, verify FAIL.**

- [ ] **Step 3: Implement**

  `src/Rekolektion.Viz.Core/Net/LabelFlood.fs`:
  ```fsharp
  module Rekolektion.Viz.Core.Net.LabelFlood

  open Rekolektion.Viz.Core.Gds.Types
  open Rekolektion.Viz.Core.Sidecar.Types
  open Rekolektion.Viz.Core.Layout.Picking

  /// Axis-aligned bbox of a polygon. Cheap reject before edge math.
  let private bbox (pts: Point list) : (int64 * int64 * int64 * int64) =
      let xs = pts |> List.map (fun p -> p.X)
      let ys = pts |> List.map (fun p -> p.Y)
      List.min xs, List.min ys, List.max xs, List.max ys

  let private bboxOverlap a b =
      let (ax0, ay0, ax1, ay1) = a
      let (bx0, by0, bx1, by1) = b
      not (ax1 < bx0 || bx1 < ax0 || ay1 < by0 || by1 < ay0)

  /// Two polygons on the SAME layer "touch" if their bboxes overlap and
  /// at least one vertex of either lies inside (or on the edge of) the
  /// other. This is a coarse approximation of polygon intersection that
  /// is correct for the rectilinear shapes rekolektion emits.
  let private touch (a: Point list) (b: Point list) : bool =
      bboxOverlap (bbox a) (bbox b)
      && (
          a |> List.exists (fun p -> pointInPolygon p b)
          || b |> List.exists (fun p -> pointInPolygon p a)
      )

  type private PolyEntry = {
      StructureName: string
      Index        : int
      Layer        : int
      DataType     : int
      Points       : Point list
  }

  let private flatten (lib: Library) : PolyEntry list =
      lib.Structures
      |> List.collect (fun s ->
          s.Elements
          |> List.indexed
          |> List.choose (fun (i, e) ->
              match e with
              | Boundary b ->
                  Some {
                      StructureName = s.Name
                      Index = i
                      Layer = b.Layer
                      DataType = b.DataType
                      Points = b.Points
                  }
              | _ -> None))

  let private classOfName (n: string) : NetClass =
      let upper = n.ToUpperInvariant()
      if   upper = "VPWR" || upper = "VDD"      then Power
      elif upper = "VGND" || upper = "VSS"      then Ground
      elif upper.StartsWith "CLK"               then Clock
      else Signal

  /// Build NetMap from labels. For each Text element, find the polygon
  /// on the same layer that contains the label point. Then flood-fill
  /// across same-layer touching polygons. Polygons not reached by any
  /// label are not included in the NetMap (they show as net-unknown
  /// in the inspector).
  let derive (lib: Library) : Map<string, NetEntry> =
      let polys = flatten lib

      let labels =
          lib.Structures
          |> List.collect (fun s ->
              s.Elements
              |> List.choose (function
                  | Text t when t.Text <> "" -> Some (s.Name, t)
                  | _ -> None))

      // For each label, do a layer-restricted flood from the seed polygon.
      labels
      |> List.fold (fun (acc: Map<string, NetEntry>) (_structName, t) ->
          let seed =
              polys
              |> List.tryFind (fun p -> p.Layer = t.Layer && pointInPolygon t.Origin p.Points)
          match seed with
          | None -> acc
          | Some s0 ->
              // BFS over same-layer polygons that touch.
              let sameLayer = polys |> List.filter (fun p -> p.Layer = s0.Layer && p.DataType = s0.DataType)
              let visited = System.Collections.Generic.HashSet<int>()
              let queue = System.Collections.Generic.Queue<PolyEntry>()
              queue.Enqueue s0 |> ignore
              visited.Add (s0.Index + (s0.StructureName.GetHashCode())) |> ignore
              let collected = System.Collections.Generic.List<PolyEntry>()
              while queue.Count > 0 do
                  let cur = queue.Dequeue()
                  collected.Add cur
                  for cand in sameLayer do
                      let key = cand.Index + (cand.StructureName.GetHashCode())
                      if not (visited.Contains key) && touch cur.Points cand.Points then
                          visited.Add key |> ignore
                          queue.Enqueue cand |> ignore
              let polyRefs =
                  collected
                  |> Seq.map (fun p ->
                      { Structure = p.StructureName
                        Layer = p.Layer
                        DataType = p.DataType
                        Index = p.Index })
                  |> Seq.toList
              let entry =
                  match Map.tryFind t.Text acc with
                  | Some existing ->
                      { existing with Polygons = existing.Polygons @ polyRefs |> List.distinct }
                  | None ->
                      { Name = t.Text; Class = classOfName t.Text; Polygons = polyRefs }
              Map.add t.Text entry acc) Map.empty
  ```

- [ ] **Step 4: Add compile, run tests, commit.**

  ```bash
  dotnet test tools/viz/tests/Rekolektion.Viz.Core.Tests/ --filter "LabelFloodTests"
  git commit -m "viz: Core.Net.LabelFlood — derive nets from labels (fallback)"
  ```

---

## Task 10: Render.Color.SkyTheme — Magic-style palette helpers

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Render/Color/SkyTheme.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.Render.Tests/SkyThemeTests.fs`

- [ ] **Step 1: Failing test**

  ```fsharp
  module Rekolektion.Viz.Render.Tests.SkyThemeTests

  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Render.Color

  [<Fact>]
  let ``SkyTheme.fillFor returns a Skia color`` () =
      let c = SkyTheme.fillFor "met2"
      c.A |> should be (greaterThan 0uy)

  [<Fact>]
  let ``SkyTheme.strokeFor is darker than fillFor`` () =
      let f = SkyTheme.fillFor "met2"
      let s = SkyTheme.strokeFor "met2"
      let lum (c: SkiaSharp.SKColor) = int c.Red + int c.Green + int c.Blue
      lum s |> should be (lessThan (lum f))
  ```

  Note: this test references `SkiaSharp.SKColor` — the test project needs SkiaSharp:
  ```xml
  <PackageReference Include="SkiaSharp" Version="2.88.8" />
  ```
  in `Rekolektion.Viz.Render.Tests.fsproj`.

- [ ] **Step 2: Run, verify FAIL.**

- [ ] **Step 3: Implement**

  `src/Rekolektion.Viz.Render/Color/SkyTheme.fs`:
  ```fsharp
  module Rekolektion.Viz.Render.Color.SkyTheme

  open SkiaSharp
  open Rekolektion.Viz.Core.Layout

  let private toSkColor (c: Layer.ColorRgba) =
      SKColor(c.R, c.G, c.B, c.A)

  let fillFor (layerName: string) : SKColor =
      Layer.allDrawing
      |> List.tryFind (fun l -> l.Name = layerName)
      |> Option.map (fun l -> toSkColor l.Color)
      |> Option.defaultValue (SKColor(byte 0xCC, byte 0xCC, byte 0xCC, byte 0x80))

  let strokeFor (layerName: string) : SKColor =
      let f = fillFor layerName
      let darken v = max 0uy (v - 64uy)
      SKColor(darken f.Red, darken f.Green, darken f.Blue, byte 0xff)
  ```

- [ ] **Step 4: Add compile, run tests, commit.**

---

## Task 11: Render.Skia.LayerPainter — paint a Library to an SKCanvas

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Render/Skia/LayerPainter.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.Render.Tests/LayerPainterTests.fs`

- [ ] **Step 1: Failing test (renders to RenderTargetBitmap, asserts pixel non-zero)**

  ```fsharp
  module Rekolektion.Viz.Render.Tests.LayerPainterTests

  open System.IO
  open SkiaSharp
  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Core.Gds.Types
  open Rekolektion.Viz.Core
  open Rekolektion.Viz.Render.Skia

  let private rect x y w h = [ {X=x;Y=y}; {X=x+w;Y=y}; {X=x+w;Y=y+h}; {X=x;Y=y+h}; {X=x;Y=y} ]

  let private singleBoundaryLib (layer: int) (datatype: int) =
      { Name = "x"; DbUnitsPerUserUnit = 0.001; DbUnitsInMeters = 1e-9
        Structures = [{
          Name = "top"
          Elements = [
            Boundary { Layer = layer; DataType = datatype; Points = rect 0L 0L 1000L 1000L }
          ]
        }] }

  [<Fact>]
  let ``Paint a single met2 polygon and check non-empty pixels`` () =
      let lib = singleBoundaryLib 69 20  // met2
      use surface = SKSurface.Create(SKImageInfo(200, 200))
      let canvas = surface.Canvas
      canvas.Clear(SKColors.Black)
      LayerPainter.paint canvas (200, 200) lib Visibility.empty
      use img = surface.Snapshot()
      use data = img.Encode(SKEncodedImageFormat.Png, 100)
      let bytes = data.ToArray()
      bytes.Length |> should be (greaterThan 0)
      // Sample center pixel — must be non-black (we drew there).
      use pix = img.PeekPixels()
      let centerColor = pix.GetPixelColor(100, 100)
      (centerColor.Red + centerColor.Green + centerColor.Blue) |> should be (greaterThan 0)

  [<Fact>]
  let ``Paint with met2 hidden produces black canvas`` () =
      let lib = singleBoundaryLib 69 20
      let hidden = Visibility.empty |> Visibility.toggleLayer (69, 20) false
      use surface = SKSurface.Create(SKImageInfo(50, 50))
      let canvas = surface.Canvas
      canvas.Clear(SKColors.Black)
      LayerPainter.paint canvas (50, 50) lib hidden
      use img = surface.Snapshot()
      use pix = img.PeekPixels()
      let c = pix.GetPixelColor(25, 25)
      (int c.Red + int c.Green + int c.Blue) |> should equal 0
  ```

- [ ] **Step 2: Run, verify FAIL.**

- [ ] **Step 3: Implement**

  `src/Rekolektion.Viz.Render/Skia/LayerPainter.fs`:
  ```fsharp
  module Rekolektion.Viz.Render.Skia.LayerPainter

  open SkiaSharp
  open Rekolektion.Viz.Core
  open Rekolektion.Viz.Core.Gds.Types
  open Rekolektion.Viz.Render.Color

  type ViewBox = {
      MinX: int64; MinY: int64
      MaxX: int64; MaxY: int64
      PixelW: int; PixelH: int
  }

  let private boundsOf (lib: Library) : (int64 * int64 * int64 * int64) =
      let allPts =
          lib.Structures
          |> List.collect (fun s ->
              s.Elements
              |> List.collect (function
                  | Boundary b -> b.Points
                  | Path p -> p.Points
                  | _ -> []))
      match allPts with
      | [] -> (0L, 0L, 1L, 1L)
      | _ ->
          let xs = allPts |> List.map (fun p -> p.X)
          let ys = allPts |> List.map (fun p -> p.Y)
          List.min xs, List.min ys, List.max xs, List.max ys

  let private project (vb: ViewBox) (p: Point) : SKPoint =
      let dx = float (vb.MaxX - vb.MinX) |> max 1.0
      let dy = float (vb.MaxY - vb.MinY) |> max 1.0
      let x = float (p.X - vb.MinX) / dx * float vb.PixelW
      let y = float vb.PixelH - (float (p.Y - vb.MinY) / dy * float vb.PixelH)
      SKPoint(float32 x, float32 y)

  /// Paint every boundary in the library, layer-ordered by stack Z so
  /// upper metal sits on top of lower metal. Honors ToggleState.Layers.
  /// Net-aware dimming is handled in the App layer (which annotates
  /// each polygon with its net before calling); for now this is a
  /// layer-only painter.
  let paint (canvas: SKCanvas) (size: int * int) (lib: Library) (toggle: Visibility.ToggleState) : unit =
      let (w, h) = size
      let (xmin, ymin, xmax, ymax) = boundsOf lib
      let vb = { MinX = xmin; MinY = ymin; MaxX = xmax; MaxY = ymax; PixelW = w; PixelH = h }

      // Group boundaries by layer key so each layer paints in one pass.
      let byLayer =
          lib.Structures
          |> List.collect (fun s ->
              s.Elements
              |> List.choose (function Boundary b -> Some b | _ -> None))
          |> List.groupBy (fun b -> b.Layer, b.DataType)

      // Order layers by StackZ from the SKY130 table; unknown layers go last.
      let zOf (key: int * int) =
          Layout.Layer.bySky130Number (fst key) (snd key)
          |> Option.map (fun l -> l.StackZ)
          |> Option.defaultValue 100.0
      let ordered = byLayer |> List.sortBy (fun (k, _) -> zOf k)

      use fill = new SKPaint(Style = SKPaintStyle.Fill, IsAntialias = true)
      use stroke = new SKPaint(Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeWidth = 0.5f)

      for (key, boundaries) in ordered do
          if Visibility.isLayerVisible toggle key then
              match Layout.Layer.bySky130Number (fst key) (snd key) with
              | None -> ()  // unknown layer — skip
              | Some layer ->
                  fill.Color <- SkyTheme.fillFor layer.Name
                  stroke.Color <- SkyTheme.strokeFor layer.Name
                  for b in boundaries do
                      use path = new SKPath()
                      match b.Points with
                      | [] -> ()
                      | first :: rest ->
                          path.MoveTo(project vb first)
                          for pt in rest do path.LineTo(project vb pt)
                          path.Close()
                          canvas.DrawPath(path, fill)
                          canvas.DrawPath(path, stroke)
  ```

- [ ] **Step 4: Add compile, run tests, commit.**

---

## Task 12: Render.Skia.LabelPainter — text labels that scale with zoom

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Render/Skia/LabelPainter.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.Render.Tests/LabelPainterTests.fs`

- [ ] **Step 1: Failing test**

  ```fsharp
  module Rekolektion.Viz.Render.Tests.LabelPainterTests

  open SkiaSharp
  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Core.Gds.Types
  open Rekolektion.Viz.Render.Skia

  [<Fact>]
  let ``Labels paint visible text`` () =
      let lib =
          { Name = "x"; DbUnitsPerUserUnit = 0.001; DbUnitsInMeters = 1e-9
            Structures = [{
              Name = "top"
              Elements = [
                  Text { Layer = 68; TextType = 5; Origin = { X = 100L; Y = 100L }; Text = "BL" }
              ]
            }] }
      use surface = SKSurface.Create(SKImageInfo(200, 200))
      surface.Canvas.Clear(SKColors.Black)
      LabelPainter.paint surface.Canvas (200, 200) lib
      // Grab pixels — must have white-ish text rendered somewhere
      use img = surface.Snapshot()
      use pix = img.PeekPixels()
      let mutable found = false
      for y in 0 .. 199 do
          for x in 0 .. 199 do
              let c = pix.GetPixelColor(x, y)
              if c.Red > 200uy && c.Green > 200uy && c.Blue > 200uy then found <- true
      found |> should equal true
  ```

- [ ] **Step 2: Run, verify FAIL.**

- [ ] **Step 3: Implement**

  `src/Rekolektion.Viz.Render/Skia/LabelPainter.fs`:
  ```fsharp
  module Rekolektion.Viz.Render.Skia.LabelPainter

  open SkiaSharp
  open Rekolektion.Viz.Core.Gds.Types

  let private bounds (lib: Library) =
      let allPts =
          lib.Structures
          |> List.collect (fun s ->
              s.Elements |> List.collect (function
                  | Boundary b -> b.Points
                  | Path p -> p.Points
                  | Text t -> [t.Origin]
                  | _ -> []))
      match allPts with
      | [] -> 0L, 0L, 1L, 1L
      | _ ->
          let xs = allPts |> List.map (fun p -> p.X)
          let ys = allPts |> List.map (fun p -> p.Y)
          List.min xs, List.min ys, List.max xs, List.max ys

  let paint (canvas: SKCanvas) (size: int * int) (lib: Library) : unit =
      let (w, h) = size
      let (xmin, ymin, xmax, ymax) = bounds lib
      let dx = float (xmax - xmin) |> max 1.0
      let dy = float (ymax - ymin) |> max 1.0
      use paint = new SKPaint(Color = SKColors.White, IsAntialias = true, TextSize = 11.0f, IsStroke = false)
      for s in lib.Structures do
          for el in s.Elements do
              match el with
              | Text t ->
                  let x = float (t.Origin.X - xmin) / dx * float w
                  let y = float h - (float (t.Origin.Y - ymin) / dy * float h)
                  canvas.DrawText(t.Text, float32 x, float32 y, paint)
              | _ -> ()
  ```

- [ ] **Step 4: Add compile, run tests, commit.**

---

## Task 13: Render.Mesh.Extruder — port existing mesh code

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Render/Mesh/Extruder.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.Render.Tests/ExtruderTests.fs`
- Read for porting: `tools/viz/Mesh/MeshGenerator.fs` (existing — produces STL/GLB)

- [ ] **Step 1: Read existing mesh code** to learn how it computes per-layer extrusion vertices.

- [ ] **Step 2: Failing test**

  ```fsharp
  module Rekolektion.Viz.Render.Tests.ExtruderTests

  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Core.Gds.Types
  open Rekolektion.Viz.Render.Mesh

  let private rect x y w h = [{X=x;Y=y};{X=x+w;Y=y};{X=x+w;Y=y+h};{X=x;Y=y+h};{X=x;Y=y}]

  [<Fact>]
  let ``Extruder.extrude produces 8 vertices per rectangular layer (top + bottom)`` () =
      let lib =
          { Name = "x"; DbUnitsPerUserUnit = 0.001; DbUnitsInMeters = 1e-9
            Structures = [{
                Name = "top"
                Elements = [ Boundary { Layer = 68; DataType = 20; Points = rect 0L 0L 1000L 1000L } ]
            }] }
      let mesh = Extruder.extrude lib
      // 8 verts per rect (top quad + bottom quad) × 1 polygon = 8.
      mesh.Vertices.Length |> should equal 8
      // Triangle indices: 12 triangles (2 caps + 4 sides) = 36 indices.
      mesh.Indices.Length |> should equal 36
  ```

- [ ] **Step 3: Run, verify FAIL.**

- [ ] **Step 4: Implement (port + adapt)**

  `src/Rekolektion.Viz.Render/Mesh/Extruder.fs`:
  ```fsharp
  module Rekolektion.Viz.Render.Mesh.Extruder

  open Rekolektion.Viz.Core.Gds.Types
  open Rekolektion.Viz.Core.Layout

  type Vertex = { X: float32; Y: float32; Z: float32; LayerKey: int * int }
  type ExtrudedMesh = {
      Vertices: Vertex array
      Indices : int array
  }

  /// Convert DBU→μm via library scale.
  let private dbuToUm (lib: Library) (v: int64) : float32 =
      float32 (float v * lib.DbUnitsPerUserUnit)

  /// Extrude a single rectilinear polygon at z0..z1. Returns 8 vertices
  /// (top quad then bottom quad) and 36 triangle indices (2 caps × 2
  /// triangles + 4 side faces × 2 triangles = 12 tris × 3 = 36).
  /// Non-rectangular polygons fall back to fan triangulation of the
  /// top/bottom caps; sides still come from edge pairs.
  let private extrudePolygon (lib: Library) (layer: Layer.Layer) (pts: Point list) (vertOffset: int) : Vertex array * int array =
      let stripped =
          match pts with
          | [] -> []
          | _ when List.last pts = List.head pts -> pts |> List.take (List.length pts - 1)
          | _ -> pts
      if stripped.Length < 3 then [||], [||]
      else
          let zBot = float32 layer.StackZ
          let zTop = float32 (layer.StackZ + layer.Thickness)
          let n = stripped.Length
          // Top vertices [0..n-1], then bottom [n..2n-1]
          let verts =
              [|
                  for p in stripped do
                      yield { X = dbuToUm lib p.X; Y = dbuToUm lib p.Y; Z = zTop; LayerKey = layer.Number, layer.DataType }
                  for p in stripped do
                      yield { X = dbuToUm lib p.X; Y = dbuToUm lib p.Y; Z = zBot; LayerKey = layer.Number, layer.DataType }
              |]
          // Top cap: fan triangulation
          let topIndices =
              [| for i in 1 .. n - 2 -> [| 0; i; i + 1 |] |] |> Array.concat
          // Bottom cap: fan, reversed winding
          let bottomIndices =
              [| for i in 1 .. n - 2 -> [| n; n + i + 1; n + i |] |] |> Array.concat
          // Sides: 2 triangles per edge
          let sideIndices =
              [|
                  for i in 0 .. n - 1 do
                      let i2 = (i + 1) % n
                      // Top-i, top-i+1, bot-i+1 / top-i, bot-i+1, bot-i
                      yield i; yield i2; yield n + i2
                      yield i; yield n + i2; yield n + i
              |]
          let indices = Array.concat [ topIndices; bottomIndices; sideIndices ]
          let offsetIndices = indices |> Array.map ((+) vertOffset)
          verts, offsetIndices

  /// Extrude every visible boundary in the library to a mesh suitable
  /// for upload to a GL VBO. Layer Z stack comes from
  /// Core.Layout.Layer.allDrawing.
  let extrude (lib: Library) : ExtrudedMesh =
      let allVerts = System.Collections.Generic.List<Vertex>()
      let allIdx   = System.Collections.Generic.List<int>()
      for s in lib.Structures do
          for el in s.Elements do
              match el with
              | Boundary b ->
                  match Layer.bySky130Number b.Layer b.DataType with
                  | None -> ()
                  | Some layer ->
                      let v, i = extrudePolygon lib layer b.Points allVerts.Count
                      allVerts.AddRange v
                      allIdx.AddRange i
              | _ -> ()
      { Vertices = allVerts.ToArray(); Indices = allIdx.ToArray() }
  ```

- [ ] **Step 5: Add compile, run tests, commit.**

---

## Task 14: Render.Mesh.Picking — GPU color-id pick (stub for App)

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.Render/Mesh/Picking.fs`

This task defines the *types* and helper functions for color-ID picking; the actual GPU draw lives in the 3D canvas in Task 17.

- [ ] **Step 1: Failing test**

  ```fsharp
  module Rekolektion.Viz.Render.Tests.MeshPickingTests

  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.Render.Mesh

  [<Fact>]
  let ``encodeId then decodeId is identity for small ids`` () =
      for id in [0; 1; 42; 65535; 16777215] do
          let (r, g, b) = Picking.encodeId id
          Picking.decodeId (r, g, b) |> should equal id

  [<Fact>]
  let ``encodeId rejects ids that exceed 24-bit`` () =
      (fun () -> Picking.encodeId 16777216 |> ignore) |> should throw typeof<System.ArgumentException>
  ```

- [ ] **Step 2: Run, verify FAIL.**

- [ ] **Step 3: Implement**

  `src/Rekolektion.Viz.Render/Mesh/Picking.fs`:
  ```fsharp
  module Rekolektion.Viz.Render.Mesh.Picking

  /// Encode a polygon ID into an RGB triplet for GPU color-buffer picking.
  /// IDs must fit in 24 bits; 0xFFFFFF is reserved as "background".
  let encodeId (id: int) : byte * byte * byte =
      if id < 0 || id >= 16777215 then
          raise (System.ArgumentException $"id {id} out of 24-bit range")
      byte ((id >>> 16) &&& 0xff),
      byte ((id >>>  8) &&& 0xff),
      byte ( id         &&& 0xff)

  let decodeId (rgb: byte * byte * byte) : int =
      let r, g, b = rgb
      (int r <<< 16) ||| (int g <<< 8) ||| int b

  let backgroundId : int = 16777215
  ```

- [ ] **Step 4: Add compile, run tests, commit.**

---

## Task 15: App.Model.Msg + Model + Update skeleton

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.App/Model/Msg.fs`
- Create: `tools/viz/src/Rekolektion.Viz.App/Model/Model.fs`
- Create: `tools/viz/src/Rekolektion.Viz.App/Model/Update.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.App.Tests/UpdateTests.fs`

- [ ] **Step 1: Failing test**

  ```fsharp
  module Rekolektion.Viz.App.Tests.UpdateTests

  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.App.Model
  open Rekolektion.Viz.Core

  let private stubBackend : Update.ServiceBackend = {
      OpenGds = fun _ -> async { return Error "stub" }
      RunMacro = fun _ _ -> async { return Error "stub" }
  }

  [<Fact>]
  let ``ToggleLayer updates Model.Toggle.Layers`` () =
      let init = Model.empty
      let next, _cmd = Update.update stubBackend (Msg.ToggleLayer ((68, 20), false)) init
      Visibility.isLayerVisible next.Toggle (68, 20) |> should equal false

  [<Fact>]
  let ``HighlightNet sets Model.Toggle.HighlightNet`` () =
      let next, _ = Update.update stubBackend (Msg.HighlightNet (Some "BL")) Model.empty
      next.Toggle.HighlightNet |> should equal (Some "BL")

  [<Fact>]
  let ``SetTab changes ActiveTab`` () =
      let next, _ = Update.update stubBackend (Msg.SetTab Tab.View3D) Model.empty
      next.ActiveTab |> should equal Tab.View3D
  ```

- [ ] **Step 2: Run, verify FAIL.**

- [ ] **Step 3: Implement Model**

  `src/Rekolektion.Viz.App/Model/Model.fs`:
  ```fsharp
  module Rekolektion.Viz.App.Model.Model

  open Rekolektion.Viz.Core
  open Rekolektion.Viz.Core.Gds.Types
  open Rekolektion.Viz.Core.Sidecar.Types

  type Tab = View2D | View3D

  type LoadedMacro = {
      Path     : string
      Library  : Library
      Nets     : Map<string, NetEntry>
      Blocks   : Layout.Hierarchy.Block list
      NetsFromSidecar : bool       // false → derived from labels
  }

  type RunState =
      | Idle
      | Running of pid: int * args: string list

  type Model = {
      Macro       : LoadedMacro option
      Toggle      : Visibility.ToggleState
      Selection   : (string * int) option   // (structure, element index)
      ActiveTab   : Tab
      View2D      : View2DState
      View3D      : View3DState
      Run         : RunState
      RecentFiles : string list
      LogVisible  : bool
      Log         : string list             // newest last
  }
  and View2DState = { ZoomFactor: float; OffsetX: float; OffsetY: float }
  and View3DState = { OrbitYaw: float; OrbitPitch: float; ZoomFactor: float; Ortho: bool }

  let empty : Model = {
      Macro = None
      Toggle = Visibility.empty
      Selection = None
      ActiveTab = View2D
      View2D = { ZoomFactor = 1.0; OffsetX = 0.0; OffsetY = 0.0 }
      View3D = { OrbitYaw = 30.0; OrbitPitch = -25.0; ZoomFactor = 1.0; Ortho = false }
      Run = Idle
      RecentFiles = []
      LogVisible = false
      Log = []
  }
  ```

- [ ] **Step 4: Implement Msg**

  `src/Rekolektion.Viz.App/Model/Msg.fs`:
  ```fsharp
  module Rekolektion.Viz.App.Model.Msg

  open Rekolektion.Viz.Core.Visibility

  type RunMacroParams = {
      Cell      : string         // foundry | lr
      Words     : int
      Bits      : int
      Mux       : int
      WriteEnable: bool
      ScanChain : bool
      ClockGating: bool
      PowerGating: bool
      WlSwitchoff: bool
      BurnIn    : bool
      ExtractedSpice: bool
      OutputPath: string
  }

  type Msg =
      | OpenFile         of path: string
      | LoadComplete     of Model.LoadedMacro
      | LoadFailed       of path: string * reason: string
      | ToggleLayer      of LayerKey * visible: bool
      | ToggleNet        of name: string * visible: bool
      | ToggleBlock      of name: string * visible: bool
      | HighlightNet     of net: string option
      | IsolateBlock     of block: string option
      | SetTab           of Model.Tab
      | PolygonPicked    of structure: string * index: int
      | ClearSelection
      | Pan2D            of dx: float * dy: float
      | Zoom2D           of factor: float
      | Orbit3D          of dyaw: float * dpitch: float
      | Zoom3D           of factor: float
      | RunMacroRequested of RunMacroParams
      | RunStarted       of pid: int
      | LogLine          of line: string
      | RunCompleted     of outputPath: string
      | RunFailed        of exitCode: int
      | ToggleLogPane
      | RecentFileClicked of path: string
  ```

- [ ] **Step 5: Implement Update with curried ServiceBackend**

  `src/Rekolektion.Viz.App/Model/Update.fs`:
  ```fsharp
  module Rekolektion.Viz.App.Model.Update

  open Elmish
  open Rekolektion.Viz.Core
  open Rekolektion.Viz.Core.Sidecar.Types

  /// Side-effect surface — resolved at boot and curried into update.
  /// Test code provides stubs; production wires real services.
  type ServiceBackend = {
      OpenGds : string -> Async<Result<Model.LoadedMacro, string>>
      RunMacro: Msg.RunMacroParams -> (string -> unit) -> Async<Result<string, int>>
      // ^ second arg = log-line callback for streaming stderr.
  }

  let private appendLog line model =
      let log = model.Log @ [line]
      let trimmed = if log.Length > 1000 then log |> List.skip (log.Length - 1000) else log
      { model with Log = trimmed }

  let update (backend: ServiceBackend) (msg: Msg.Msg) (model: Model.Model) : Model.Model * Cmd<Msg.Msg> =
      match msg with
      | Msg.OpenFile path ->
          let cmd =
              Cmd.OfAsync.either backend.OpenGds path
                  (function
                      | Ok m -> Msg.LoadComplete m
                      | Error r -> Msg.LoadFailed (path, r))
                  (fun ex -> Msg.LoadFailed (path, ex.Message))
          model, cmd
      | Msg.LoadComplete macro ->
          let recents =
              macro.Path :: (model.RecentFiles |> List.filter (fun p -> p <> macro.Path))
              |> List.truncate 10
          { model with Macro = Some macro; RecentFiles = recents; Selection = None }, Cmd.none
      | Msg.LoadFailed (path, reason) ->
          appendLog (sprintf "load failed: %s — %s" path reason) model, Cmd.none
      | Msg.ToggleLayer (key, vis) ->
          { model with Toggle = Visibility.toggleLayer key vis model.Toggle }, Cmd.none
      | Msg.ToggleNet (name, vis) ->
          { model with Toggle = Visibility.toggleNet name vis model.Toggle }, Cmd.none
      | Msg.ToggleBlock (name, vis) ->
          { model with Toggle = Visibility.toggleBlock name vis model.Toggle }, Cmd.none
      | Msg.HighlightNet net ->
          { model with Toggle = Visibility.highlightNet net model.Toggle }, Cmd.none
      | Msg.IsolateBlock blk ->
          { model with Toggle = Visibility.isolateBlock blk model.Toggle }, Cmd.none
      | Msg.SetTab tab -> { model with ActiveTab = tab }, Cmd.none
      | Msg.PolygonPicked (s, i) -> { model with Selection = Some (s, i) }, Cmd.none
      | Msg.ClearSelection -> { model with Selection = None }, Cmd.none
      | Msg.Pan2D (dx, dy) ->
          let v = model.View2D
          { model with View2D = { v with OffsetX = v.OffsetX + dx; OffsetY = v.OffsetY + dy } }, Cmd.none
      | Msg.Zoom2D f ->
          let v = model.View2D
          { model with View2D = { v with ZoomFactor = v.ZoomFactor * f } }, Cmd.none
      | Msg.Orbit3D (dy, dp) ->
          let v = model.View3D
          { model with View3D = { v with OrbitYaw = v.OrbitYaw + dy; OrbitPitch = v.OrbitPitch + dp } }, Cmd.none
      | Msg.Zoom3D f ->
          let v = model.View3D
          { model with View3D = { v with ZoomFactor = v.ZoomFactor * f } }, Cmd.none
      | Msg.RunMacroRequested p ->
          let cmd =
              Cmd.OfAsync.either
                  (fun () -> backend.RunMacro p (fun line -> ()))
                  ()
                  (function
                      | Ok path -> Msg.RunCompleted path
                      | Error code -> Msg.RunFailed code)
                  (fun ex -> Msg.LogLine (sprintf "run failed: %s" ex.Message))
          model, cmd
      | Msg.RunStarted pid ->
          { model with Run = Model.RunState.Running (pid, []); LogVisible = true }, Cmd.none
      | Msg.LogLine line -> appendLog line model, Cmd.none
      | Msg.RunCompleted path ->
          { model with Run = Model.RunState.Idle }, Cmd.ofMsg (Msg.OpenFile path)
      | Msg.RunFailed code ->
          let m = appendLog (sprintf "run failed (exit %d)" code) model
          { m with Run = Model.RunState.Idle }, Cmd.none
      | Msg.ToggleLogPane -> { model with LogVisible = not model.LogVisible }, Cmd.none
      | Msg.RecentFileClicked p -> model, Cmd.ofMsg (Msg.OpenFile p)
  ```

- [ ] **Step 6: Wire fsproj compile order**

  ```xml
  <Compile Include="Model/Model.fs" />
  <Compile Include="Model/Msg.fs" />
  <Compile Include="Model/Update.fs" />
  <Compile Include="Placeholder.fs" />
  ```
  (Note Msg.fs references Model.fs — Model first, then Msg, then Update.)

- [ ] **Step 7: Tests project compiles too**

  `Rekolektion.Viz.App.Tests.fsproj`:
  ```xml
  <Compile Include="UpdateTests.fs" />
  <Compile Include="Placeholder.fs" />
  ```

- [ ] **Step 8: Run tests, commit.**

  ```bash
  dotnet test tools/viz/tests/Rekolektion.Viz.App.Tests/ --filter "UpdateTests"
  git commit -m "viz: App.Model — Msg/Model/Update with ServiceBackend"
  ```

---

## Task 16: App.Services.RekolektionCli — subprocess runner

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.App/Services/RekolektionCli.fs`
- Create: `tools/viz/tests/Rekolektion.Viz.App.Tests/RekolektionCliTests.fs`

- [ ] **Step 1: Failing test (uses /bin/sh as a stand-in for the rekolektion CLI)**

  ```fsharp
  module Rekolektion.Viz.App.Tests.RekolektionCliTests

  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.App.Services

  [<Fact>]
  let ``runProcess captures stdout to log lines and exit code`` () =
      let lines = ResizeArray<string>()
      let exitCode =
          RekolektionCli.runProcess "/bin/sh" ["-c"; "echo first; echo second"] (fun l -> lines.Add l)
          |> Async.RunSynchronously
      exitCode |> should equal 0
      lines |> should contain "first"
      lines |> should contain "second"

  [<Fact>]
  let ``runProcess returns non-zero for failing process`` () =
      let exitCode =
          RekolektionCli.runProcess "/bin/sh" ["-c"; "exit 7"] (fun _ -> ())
          |> Async.RunSynchronously
      exitCode |> should equal 7
  ```

- [ ] **Step 2: Run, verify FAIL.**

- [ ] **Step 3: Implement**

  `src/Rekolektion.Viz.App/Services/RekolektionCli.fs`:
  ```fsharp
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
  ```

- [ ] **Step 4: Run tests, commit.**

---

## Task 17: App.Services.GdsLoading — open GDS + sidecar + hierarchy

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.App/Services/GdsLoading.fs`

This wires Core.Gds + Sidecar + LabelFlood + Hierarchy into one `OpenGds` function the App backend uses.

- [ ] **Step 1: Implement**

  ```fsharp
  module Rekolektion.Viz.App.Services.GdsLoading

  open System.IO
  open Rekolektion.Viz.Core
  open Rekolektion.Viz.Core.Gds
  open Rekolektion.Viz.Core.Sidecar
  open Rekolektion.Viz.Core.Net
  open Rekolektion.Viz.App.Model.Model

  /// Open a GDS file and build a fully-loaded LoadedMacro:
  /// 1. Parse GDS via Core.Gds.Reader
  /// 2. Try to load <path>.nets.json sidecar; fall back to LabelFlood
  /// 3. Detect hierarchy
  let load (path: string) : Async<Result<LoadedMacro, string>> = async {
      try
          let lib = Reader.readGds path
          let sidecarPath = Path.ChangeExtension(path, ".nets.json")
          let nets, fromSidecar =
              match Loader.load sidecarPath with
              | Some sc -> sc.Nets, true
              | None -> LabelFlood.derive lib, false
          let blocks = Layout.Hierarchy.detect lib
          return Ok {
              Path = path
              Library = lib
              Nets = nets
              Blocks = blocks
              NetsFromSidecar = fromSidecar
          }
      with ex -> return Error ex.Message
  }
  ```

- [ ] **Step 2: Add to fsproj**, build, commit.

  ```bash
  git commit -m "viz: App.Services.GdsLoading — open GDS + sidecar/labelflood + hierarchy"
  ```

---

## Task 18: App.Canvas2D.GdsCanvasControl — Skia ICustomDrawOperation

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.App/Canvas2D/GdsCanvasControl.fs`

This is an Avalonia `Control` that hosts a Skia draw operation. Reference: `Moroder/src/Moroder.Viz/DieCanvas/SkiaCustomDrawOp.fs` and `DieCanvasControl.fs` — both are templates we copy and adapt.

- [ ] **Step 1: Read Moroder's DieCanvas implementation:**
  ```bash
  cat /Users/bryancostanich/Git_Repos/bryan_costanich/Moroder/src/Moroder.Viz/DieCanvas/SkiaCustomDrawOp.fs
  cat /Users/bryancostanich/Git_Repos/bryan_costanich/Moroder/src/Moroder.Viz/DieCanvas/DieCanvasControl.fs
  ```
  Note the pattern: `ICustomDrawOperation` holds the data; `Control.Render` builds it; `AvaloniaProperty` data-binds the model into the control.

- [ ] **Step 2: Implement**

  ```fsharp
  module Rekolektion.Viz.App.Canvas2D.GdsCanvasControl

  open System
  open Avalonia
  open Avalonia.Controls
  open Avalonia.Media
  open Avalonia.Platform
  open Avalonia.Rendering.SceneGraph
  open Avalonia.Skia
  open SkiaSharp
  open Rekolektion.Viz.Core
  open Rekolektion.Viz.Core.Gds.Types
  open Rekolektion.Viz.Render.Skia

  type private SkiaDraw(bounds: Rect, lib: Library, toggle: Visibility.ToggleState) =
      interface ICustomDrawOperation with
          member _.Bounds = bounds
          member _.Equals(_: ICustomDrawOperation) = false
          member _.HitTest _ = false
          member _.Dispose() = ()
          member _.Render(context) =
              let leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()
              if not (isNull leaseFeature) then
                  use lease = leaseFeature.Lease()
                  let canvas = lease.SkCanvas
                  let w = int bounds.Width
                  let h = int bounds.Height
                  canvas.Clear(SKColors.Black)
                  LayerPainter.paint canvas (w, h) lib toggle
                  LabelPainter.paint canvas (w, h) lib

  type GdsCanvasControl() =
      inherit Control()

      static let LibraryProp =
          AvaloniaProperty.Register<GdsCanvasControl, Library option>("Library", None)
      static let ToggleProp =
          AvaloniaProperty.Register<GdsCanvasControl, Visibility.ToggleState>("Toggle", Visibility.empty)

      member this.Library
          with get() : Library option = this.GetValue(LibraryProp)
          and set(v: Library option) = this.SetValue(LibraryProp, v) |> ignore

      member this.Toggle
          with get() : Visibility.ToggleState = this.GetValue(ToggleProp)
          and set(v: Visibility.ToggleState) = this.SetValue(ToggleProp, v) |> ignore

      override this.OnPropertyChanged(e) =
          base.OnPropertyChanged e
          if e.Property = LibraryProp || e.Property = ToggleProp then
              this.InvalidateVisual()

      override this.Render(context) =
          base.Render context
          match this.Library with
          | Some lib ->
              let bounds = Rect(0.0, 0.0, this.Bounds.Width, this.Bounds.Height)
              context.Custom(SkiaDraw(bounds, lib, this.Toggle))
          | None -> ()
  ```

- [ ] **Step 3: Add compile, build, commit.**

  ```bash
  dotnet build tools/viz/Rekolektion.Viz.sln
  git commit -m "viz: App.Canvas2D — GdsCanvasControl with Skia ICustomDrawOperation"
  ```

  No unit test for this yet — App.Tests headless integration in Task 25 will exercise it.

---

## Task 19: App.Canvas3D.StackCanvasControl — Silk.NET OpenGL extruded mesh

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.App/Canvas3D/StackCanvasControl.fs`

This is the highest-risk item in the plan (per spec risks section). If it tar-pits, the fallback is a screenshot-only 3D view backed by an offscreen FBO that paints into the Skia canvas — not as nice but deliverable.

- [ ] **Step 1: Implement minimal OpenGL control**

  ```fsharp
  module Rekolektion.Viz.App.Canvas3D.StackCanvasControl

  open Avalonia
  open Avalonia.OpenGL
  open Avalonia.OpenGL.Controls
  open Silk.NET.OpenGL
  open Rekolektion.Viz.Core
  open Rekolektion.Viz.Core.Gds.Types
  open Rekolektion.Viz.Render.Mesh

  /// Avalonia OpenGlControlBase loads a GL context for us; we use
  /// Silk.NET.OpenGL.GL on top of it for typed bindings. The control
  /// owns one VBO + one shader program. Mesh changes when Library
  /// changes; toggle changes are handled by per-vertex layer visibility
  /// uniforms.
  type StackCanvasControl() =
      inherit OpenGlControlBase()

      static let LibraryProp =
          AvaloniaProperty.Register<StackCanvasControl, Library option>("Library", None)
      static let ToggleProp =
          AvaloniaProperty.Register<StackCanvasControl, Visibility.ToggleState>("Toggle", Visibility.empty)

      let mutable gl : GL option = None
      let mutable vbo : uint32 = 0u
      let mutable ebo : uint32 = 0u
      let mutable program : uint32 = 0u
      let mutable indexCount : int = 0
      let mutable yawDeg = 30.0
      let mutable pitchDeg = -25.0
      let mutable zoom = 1.0

      member this.Library
          with get() : Library option = this.GetValue(LibraryProp)
          and set(v: Library option) = this.SetValue(LibraryProp, v) |> ignore

      member this.Toggle
          with get() : Visibility.ToggleState = this.GetValue(ToggleProp)
          and set(v: Visibility.ToggleState) = this.SetValue(ToggleProp, v) |> ignore

      member this.SetCamera (yaw: float) (pitch: float) (z: float) =
          yawDeg <- yaw
          pitchDeg <- pitch
          zoom <- z
          this.RequestNextFrameRendering()

      override this.OnPropertyChanged e =
          base.OnPropertyChanged e
          if e.Property = LibraryProp || e.Property = ToggleProp then
              this.RequestNextFrameRendering()

      override this.OnOpenGlInit(gli) =
          let g = GL.GetApi(fun n -> gli.GlInterface.GetProcAddress(n))
          gl <- Some g
          vbo <- g.GenBuffer()
          ebo <- g.GenBuffer()
          // Minimal vertex shader (position only) + frag shader (per-layer color uniform)
          let vsSrc = "
              #version 330 core
              layout(location=0) in vec3 aPos;
              layout(location=1) in vec3 aColor;
              layout(location=2) in float aLayerVisible; // 0 or 1
              uniform mat4 uMVP;
              out vec3 vColor;
              out float vVis;
              void main() {
                  gl_Position = uMVP * vec4(aPos, 1.0);
                  vColor = aColor;
                  vVis = aLayerVisible;
              }
          "
          let fsSrc = "
              #version 330 core
              in vec3 vColor;
              in float vVis;
              out vec4 FragColor;
              void main() {
                  if (vVis < 0.5) discard;
                  FragColor = vec4(vColor, 1.0);
              }
          "
          let compile (src: string) (kind: GLEnum) =
              let s = g.CreateShader kind
              g.ShaderSource(s, src)
              g.CompileShader s
              s
          let vs = compile vsSrc GLEnum.VertexShader
          let fs = compile fsSrc GLEnum.FragmentShader
          program <- g.CreateProgram()
          g.AttachShader(program, vs)
          g.AttachShader(program, fs)
          g.LinkProgram program
          g.DeleteShader vs
          g.DeleteShader fs

      override this.OnOpenGlDeinit(_gli) =
          match gl with
          | Some g ->
              g.DeleteBuffer vbo
              g.DeleteBuffer ebo
              g.DeleteProgram program
          | None -> ()

      override this.OnOpenGlRender(_gli, _fb) =
          match gl, this.Library with
          | Some g, Some lib ->
              let mesh = Extruder.extrude lib
              indexCount <- mesh.Indices.Length
              // Build interleaved buffer: pos(3) + color(3) + layerVisible(1)
              let toggle = this.Toggle
              let interleaved = ResizeArray<float32>(mesh.Vertices.Length * 7)
              for v in mesh.Vertices do
                  let layerOpt = Layout.Layer.bySky130Number (fst v.LayerKey) (snd v.LayerKey)
                  let color =
                      match layerOpt with
                      | Some l -> float32 l.Color.R / 255.0f, float32 l.Color.G / 255.0f, float32 l.Color.B / 255.0f
                      | None -> 0.5f, 0.5f, 0.5f
                  let vis = if Visibility.isLayerVisible toggle v.LayerKey then 1.0f else 0.0f
                  interleaved.Add v.X; interleaved.Add v.Y; interleaved.Add v.Z
                  let r,gC,b = color
                  interleaved.Add r; interleaved.Add gC; interleaved.Add b
                  interleaved.Add vis
              let arr = interleaved.ToArray()
              g.BindBuffer(GLEnum.ArrayBuffer, vbo)
              g.BufferData(GLEnum.ArrayBuffer, ReadOnlySpan<float32>(arr), GLEnum.DynamicDraw)
              g.BindBuffer(GLEnum.ElementArrayBuffer, ebo)
              g.BufferData(GLEnum.ElementArrayBuffer, ReadOnlySpan<int>(mesh.Indices), GLEnum.DynamicDraw)

              g.ClearColor(0.0f, 0.0f, 0.0f, 1.0f)
              g.Clear(uint (GLEnum.ColorBufferBit ||| GLEnum.DepthBufferBit))
              g.Enable(GLEnum.DepthTest)

              g.UseProgram program
              // Vertex attribs
              g.EnableVertexAttribArray 0u
              g.VertexAttribPointer(0u, 3, GLEnum.Float, false, 7u * sizeof<float32> |> uint, IntPtr.Zero |> nativeint)
              g.EnableVertexAttribArray 1u
              g.VertexAttribPointer(1u, 3, GLEnum.Float, false, 7u * sizeof<float32> |> uint, (3 * sizeof<float32>) |> nativeint)
              g.EnableVertexAttribArray 2u
              g.VertexAttribPointer(2u, 1, GLEnum.Float, false, 7u * sizeof<float32> |> uint, (6 * sizeof<float32>) |> nativeint)

              // Build a simple MVP: orthographic projection looking down at the die,
              // then rotate by yaw/pitch.
              let mvp = Matrix4x4Helpers.buildOrbitMvp yawDeg pitchDeg zoom (this.Bounds.Width, this.Bounds.Height)
              let loc = g.GetUniformLocation(program, "uMVP")
              let mvpArr = Matrix4x4Helpers.toFloatArray mvp
              g.UniformMatrix4(loc, 1u, false, ReadOnlySpan<float32>(mvpArr))
              g.DrawElements(GLEnum.Triangles, uint indexCount, GLEnum.UnsignedInt, IntPtr.Zero |> nativeint)
          | _ -> ()
  ```

  Note: `Matrix4x4Helpers` is a small helper module the implementer creates alongside (orbital camera matrix). Use `System.Numerics.Matrix4x4` to compose: model identity, view = lookAt(orbit position around centroid), projection = orthographic by default.

- [ ] **Step 2: Implement `Matrix4x4Helpers.fs`** in the same Canvas3D directory:

  ```fsharp
  module Rekolektion.Viz.App.Canvas3D.Matrix4x4Helpers

  open System
  open System.Numerics

  let private deg2rad d = float32 (d * Math.PI / 180.0)

  let buildOrbitMvp (yawDeg: float) (pitchDeg: float) (zoom: float) (bounds: float * float) : Matrix4x4 =
      let w, h = bounds
      let aspect = float32 (w / max h 1.0)
      let proj = Matrix4x4.CreateOrthographic(80.0f / float32 zoom * aspect, 80.0f / float32 zoom, 0.1f, 1000.0f)
      let radius = 100.0f / float32 zoom
      let yaw = deg2rad yawDeg
      let pitch = deg2rad pitchDeg
      let camX = radius * MathF.Cos(pitch) * MathF.Sin(yaw)
      let camY = radius * MathF.Cos(pitch) * MathF.Cos(yaw)
      let camZ = radius * MathF.Sin(pitch)
      let view = Matrix4x4.CreateLookAt(Vector3(camX, camY, camZ), Vector3.Zero, Vector3.UnitZ)
      view * proj

  let toFloatArray (m: Matrix4x4) : float32 array =
      [| m.M11; m.M12; m.M13; m.M14
         m.M21; m.M22; m.M23; m.M24
         m.M31; m.M32; m.M33; m.M34
         m.M41; m.M42; m.M43; m.M44 |]
  ```

- [ ] **Step 3: Build, fix compile errors interactively. Commit when build is green.**

  ```bash
  dotnet build tools/viz/Rekolektion.Viz.sln
  git commit -m "viz: App.Canvas3D — Silk.NET extruded layer stack"
  ```

  > **If 3D context creation fails on this machine** (older GL stack, headless Linux without GL): the App will detect this in App.fs (Task 22) and disable the 3D tab with a banner. Phase 1 still ships.

---

## Task 20: App.View modules — TopBar, LeftPanel, Inspector, LogPane, RunDialog

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.App/View/TopBar.fs`
- Create: `tools/viz/src/Rekolektion.Viz.App/View/LeftPanel.fs`
- Create: `tools/viz/src/Rekolektion.Viz.App/View/Inspector.fs`
- Create: `tools/viz/src/Rekolektion.Viz.App/View/LogPane.fs`
- Create: `tools/viz/src/Rekolektion.Viz.App/View/RunDialog.fs`
- Create: `tools/viz/src/Rekolektion.Viz.App/View/AppView.fs`

These are FuncUI views — pure functions of `(Model, dispatch)` → `IView`. Implement each as a small focused file. Reference: `Moroder/src/Moroder.Viz/View/*.fs`.

- [ ] **Step 1: TopBar.fs** — file open button, recent dropdown, run-macro button, current-file label.

  ```fsharp
  module Rekolektion.Viz.App.View.TopBar

  open Avalonia.FuncUI.DSL
  open Avalonia.FuncUI.Types
  open Avalonia.Controls
  open Avalonia.Layout
  open Rekolektion.Viz.App.Model

  let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
      DockPanel.create [
          DockPanel.height 36.0
          DockPanel.background "#1a1a1a"
          DockPanel.children [
              StackPanel.create [
                  StackPanel.orientation Orientation.Horizontal
                  StackPanel.spacing 8.0
                  StackPanel.margin (8.0, 4.0, 8.0, 4.0)
                  StackPanel.children [
                      Button.create [
                          Button.content "Open…"
                          Button.onClick (fun _ ->
                              // file dialog logic — simple: emit OpenFile from a Cmd
                              ())
                      ]
                      Button.create [
                          Button.content "Run macro…"
                          // open RunDialog
                          ()
                      ]
                      TextBlock.create [
                          TextBlock.text (model.Macro |> Option.map (fun m -> m.Path) |> Option.defaultValue "(no file)")
                          TextBlock.foreground "#888"
                          TextBlock.verticalAlignment VerticalAlignment.Center
                      ]
                  ]
              ]
          ]
      ]
  ```

  > **Note on file dialogs:** Avalonia's `OpenFileDialog` requires a `Window` reference. The cleanest pattern is to expose an `IServiceBackend.PickGds : Window -> Async<string option>` and call it via Cmd; or use the FuncUI `IClassicDesktopStyleApplicationLifetime` to get the active window. See Moroder.Viz for an example pattern; replicate.

- [ ] **Step 2: LeftPanel.fs** — three sections: Layers, Nets, Blocks.

  ```fsharp
  module Rekolektion.Viz.App.View.LeftPanel

  open Avalonia.FuncUI.DSL
  open Avalonia.FuncUI.Types
  open Avalonia.Controls
  open Avalonia.Layout
  open Rekolektion.Viz.Core
  open Rekolektion.Viz.App.Model

  let private layerRow (toggle: Visibility.ToggleState) (dispatch: Msg.Msg -> unit) (layer: Layout.Layer.Layer) : IView =
      let key = layer.Number, layer.DataType
      let visible = Visibility.isLayerVisible toggle key
      StackPanel.create [
          StackPanel.orientation Orientation.Horizontal
          StackPanel.spacing 6.0
          StackPanel.children [
              Border.create [
                  Border.width 10.0
                  Border.height 10.0
                  Border.background (sprintf "#%02x%02x%02x" layer.Color.R layer.Color.G layer.Color.B)
                  Border.borderThickness 1.0
                  Border.borderBrush "#555"
              ]
              CheckBox.create [
                  CheckBox.isChecked visible
                  CheckBox.content layer.Name
                  CheckBox.onChecked (fun _ -> dispatch (Msg.ToggleLayer (key, true)))
                  CheckBox.onUnchecked (fun _ -> dispatch (Msg.ToggleLayer (key, false)))
              ]
          ]
      ]

  let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
      ScrollViewer.create [
          ScrollViewer.content (
              StackPanel.create [
                  StackPanel.spacing 4.0
                  StackPanel.margin 8.0
                  StackPanel.children [
                      TextBlock.create [ TextBlock.text "Layers"; TextBlock.fontWeight Avalonia.Media.FontWeight.Bold ]
                      yield! Layout.Layer.allDrawing |> List.map (layerRow model.Toggle dispatch)
                      Separator.create []
                      TextBlock.create [ TextBlock.text "Nets"; TextBlock.fontWeight Avalonia.Media.FontWeight.Bold ]
                      yield!
                          (match model.Macro with
                           | None -> []
                           | Some m ->
                               m.Nets
                               |> Map.toList
                               |> List.sortBy fst
                               |> List.map (fun (name, _) ->
                                   Button.create [
                                       Button.content name
                                       Button.onClick (fun _ -> dispatch (Msg.HighlightNet (Some name)))
                                   ] :> IView))
                      Separator.create []
                      TextBlock.create [ TextBlock.text "Blocks"; TextBlock.fontWeight Avalonia.Media.FontWeight.Bold ]
                      yield!
                          (match model.Macro with
                           | None -> []
                           | Some m ->
                               m.Blocks
                               |> List.map (fun b ->
                                   Button.create [
                                       Button.content b.Name
                                       Button.onClick (fun _ -> dispatch (Msg.IsolateBlock (Some b.Name)))
                                   ] :> IView))
                  ]
              ]
          )
      ]
  ```

- [ ] **Step 3: Inspector.fs** — selected polygon details.

  ```fsharp
  module Rekolektion.Viz.App.View.Inspector

  open Avalonia.FuncUI.DSL
  open Avalonia.FuncUI.Types
  open Avalonia.Controls
  open Rekolektion.Viz.App.Model

  let view (model: Model.Model) (_dispatch: Msg.Msg -> unit) : IView =
      StackPanel.create [
          StackPanel.spacing 6.0
          StackPanel.margin 8.0
          StackPanel.children [
              TextBlock.create [ TextBlock.text "Inspector"; TextBlock.fontWeight Avalonia.Media.FontWeight.Bold ]
              match model.Selection with
              | None ->
                  TextBlock.create [ TextBlock.text "(nothing selected)"; TextBlock.foreground "#888" ]
              | Some (struc, idx) ->
                  TextBlock.create [ TextBlock.text (sprintf "structure: %s" struc) ]
                  TextBlock.create [ TextBlock.text (sprintf "index: %d" idx) ]
          ]
      ]
  ```

- [ ] **Step 4: LogPane.fs** — collapsed by default, expandable strip with stderr stream.

  ```fsharp
  module Rekolektion.Viz.App.View.LogPane

  open Avalonia.FuncUI.DSL
  open Avalonia.FuncUI.Types
  open Avalonia.Controls
  open Rekolektion.Viz.App.Model

  let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
      let last = model.Log |> List.tryLast |> Option.defaultValue ""
      DockPanel.create [
          DockPanel.background "#0d0d0d"
          DockPanel.children [
              if model.LogVisible then
                  ScrollViewer.create [
                      ScrollViewer.height 160.0
                      ScrollViewer.content (
                          TextBlock.create [
                              TextBlock.text (System.String.Join("\n", model.Log))
                              TextBlock.fontFamily "Menlo,Consolas,monospace"
                              TextBlock.foreground "#aaa"
                          ]
                      )
                  ]
              else
                  Button.create [
                      Button.content (sprintf "▸ Log — last: %s" last)
                      Button.onClick (fun _ -> dispatch Msg.ToggleLogPane)
                      Button.background "#0d0d0d"
                      Button.foreground "#888"
                  ]
          ]
      ]
  ```

- [ ] **Step 5: RunDialog.fs** — modal dialog with form fields, returns RunMacroParams via dispatch.

  Implement as a separate Window, opened from TopBar. Form fields per spec (cell, words, bits, mux, all 7 feature flags, output path), Run button dispatches `Msg.RunMacroRequested`. Cancel closes. Reference Moroder's RunsPage modal patterns where applicable.

- [ ] **Step 6: AppView.fs** — composes all views into the main grid.

  ```fsharp
  module Rekolektion.Viz.App.View.AppView

  open Avalonia.Controls
  open Avalonia.FuncUI.DSL
  open Avalonia.FuncUI.Types
  open Rekolektion.Viz.App.Canvas2D
  open Rekolektion.Viz.App.Canvas3D
  open Rekolektion.Viz.App.Model

  let private canvas (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
      TabControl.create [
          TabControl.viewItems [
              TabItem.create [
                  TabItem.header "2D"
                  TabItem.content (
                      View.create<GdsCanvasControl>([
                          // FuncUI lift; bind Library and Toggle from model
                      ]))
              ]
              TabItem.create [
                  TabItem.header "3D"
                  TabItem.content (
                      View.create<StackCanvasControl>([
                      ]))
              ]
          ]
      ]

  let view (model: Model.Model) (dispatch: Msg.Msg -> unit) : IView =
      Grid.create [
          Grid.rowDefinitions "Auto,*,Auto"
          Grid.children [
              TopBar.view model dispatch
              Grid.create [
                  Grid.row 1
                  Grid.columnDefinitions "240,*,260"
                  Grid.children [
                      Border.create [ Border.column 0; Border.child (LeftPanel.view model dispatch) ]
                      Border.create [ Border.column 1; Border.child (canvas model dispatch) ]
                      Border.create [ Border.column 2; Border.child (Inspector.view model dispatch) ]
                  ]
              ]
              Border.create [ Border.row 2; Border.child (LogPane.view model dispatch) ]
          ]
      ]
  ```

  > FuncUI's `View.create<...>` lift for our two custom controls (`GdsCanvasControl`, `StackCanvasControl`) — see Moroder's `DieCanvasControl` lift pattern in `View/Canvas.fs`. The implementer should mirror it.

- [ ] **Step 7: Add all View Compile entries to fsproj, build green, commit.**

  ```bash
  git commit -m "viz: App.View — TopBar/LeftPanel/Inspector/LogPane/AppView"
  ```

---

## Task 21: App.HeadlessRender — port from Moroder

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.App/HeadlessRender.fs`

- [ ] **Step 1: Port verbatim from Moroder**

  Read `/Users/bryancostanich/Git_Repos/bryan_costanich/Moroder/src/Moroder.Viz/HeadlessRender.fs` (82 lines). Copy to `tools/viz/src/Rekolektion.Viz.App/HeadlessRender.fs`. Make these substitutions:

  - `namespace Moroder.Viz` → `namespace Rekolektion.Viz.App`
  - `MORODER_VIZ_HEADLESS` → `REKOLEKTION_VIZ_HEADLESS`
  - `MainWindow()` reference resolves to whatever Task 22 names — keep as `MainWindow()` for now; Task 22 implements it.

- [ ] **Step 2: Add compile (after Canvas3D, before App.fs), build.**

- [ ] **Step 3: Commit.**

  ```bash
  git commit -m "viz: App.HeadlessRender — port from Moroder.Viz"
  ```

---

## Task 22: App.App — Avalonia App + MainWindow + Elmish wiring

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.App/App.fs`

- [ ] **Step 1: Read Moroder's App.fs**

  ```bash
  cat /Users/bryancostanich/Git_Repos/bryan_costanich/Moroder/src/Moroder.Viz/App.fs
  ```

- [ ] **Step 2: Implement**

  Mirror Moroder's pattern: `App` inherits from `Avalonia.Application`, sets the FluentTheme, on `OnFrameworkInitializationCompleted` checks for classic-desktop lifetime, constructs `MainWindow`, attaches Elmish program with `init`, `update`, `view`, and the `ServiceBackend` wired to `GdsLoading.load` and `RekolektionCli.runProcess`.

  Also: in `OnFrameworkInitializationCompleted`, gated by `Environment.GetEnvironmentVariable "REKOLEKTION_VIZ_HEADLESS" <> "1"`, start the `ScreenshotListener` and `CommandListener` on `~/.rekolektion/viz.sock` (Tasks 23 + 24 implement them). Stale-cleanup the socket file before bind.

  Sketch:
  ```fsharp
  namespace Rekolektion.Viz.App

  open Avalonia
  open Avalonia.Controls.ApplicationLifetimes
  open Avalonia.FuncUI.Hosts
  open Avalonia.FuncUI.Elmish
  open Avalonia.Themes.Fluent
  open Elmish
  open Rekolektion.Viz.App.Model

  type MainWindow() as this =
      inherit HostWindow()
      do
          this.Title <- "rekolektion-viz"
          this.Width <- 1400.0
          this.Height <- 900.0
          let backend : Update.ServiceBackend = {
              OpenGds = Services.GdsLoading.load
              RunMacro = fun p onLog -> async {
                  let args = Services.RekolektionCli.buildMacroArgs p
                  let! exit = Services.RekolektionCli.runProcess "rekolektion" args onLog
                  if exit = 0 then return Ok p.OutputPath
                  else return Error exit
              }
          }
          let init () = Model.empty, Cmd.none
          Elmish.Program.mkProgram init (Update.update backend) View.AppView.view
          |> Program.withHost this
          |> Program.run

  type App() =
      inherit Application()
      override this.Initialize() =
          this.Styles.Add (FluentTheme())
      override this.OnFrameworkInitializationCompleted() =
          match this.ApplicationLifetime with
          | :? IClassicDesktopStyleApplicationLifetime as desktop ->
              let win = MainWindow()
              desktop.MainWindow <- win
              if System.Environment.GetEnvironmentVariable "REKOLEKTION_VIZ_HEADLESS" <> "1" then
                  let socketPath =
                      let dir = System.IO.Path.Combine(System.Environment.GetFolderPath System.Environment.SpecialFolder.UserProfile, ".rekolektion")
                      System.IO.Directory.CreateDirectory dir |> ignore
                      System.IO.Path.Combine(dir, "viz.sock")
                  // Listeners come from Tasks 23 + 24 — bind here once available.
                  ()
          | _ -> ()
          base.OnFrameworkInitializationCompleted()
  ```

- [ ] **Step 3: Add compile, build, commit.**

  ```bash
  git commit -m "viz: App.App + MainWindow — Avalonia app with Elmish"
  ```

---

## Task 23: App.Services.ScreenshotListener — port from Moroder

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.App/Services/ScreenshotListener.fs`

- [ ] **Step 1: Port verbatim from `/Users/bryancostanich/Git_Repos/bryan_costanich/Moroder/src/Moroder.Viz/Services/ScreenshotListener.fs`** (159 lines). Substitutions:
  - `module Moroder.Viz.Services.ScreenshotListener` → `module Rekolektion.Viz.App.Services.ScreenshotListener`

  No other changes — the listener is generic over the `windowProvider`.

- [ ] **Step 2: Wire into App.fs**

  Update `OnFrameworkInitializationCompleted`:
  ```fsharp
  let listener =
      ScreenshotListener.start socketPath (fun () -> Some (win :> Avalonia.Controls.TopLevel))
  desktop.Exit.Add(fun _ -> listener.Dispose())
  ```

- [ ] **Step 3: Build, commit.**

  ```bash
  git commit -m "viz: App.Services.ScreenshotListener — port from Moroder"
  ```

---

## Task 24: App.Services.CommandListener — UDS HTTP for agent-driven commands

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.App/Services/CommandListener.fs`

This is new (no Moroder equivalent). Same socket as ScreenshotListener — they cooperate, ScreenshotListener handles `GET /screenshot`, CommandListener handles all `POST` endpoints.

Simplest design: refactor ScreenshotListener into a unified `SocketServer` that dispatches by request line, with two handler modules. But keeping them separate sockets is also fine for v1 — single socket adds complexity. **Decision**: separate listener at the same `viz.sock` path is impossible (one socket = one bind). So we make the ScreenshotListener accept any HTTP request and dispatch by path:

- `GET /screenshot` → existing PNG handler
- `POST /open` / `POST /toggle/layer` etc. → CommandListener handlers

- [ ] **Step 1: Refactor ScreenshotListener to dispatch by request line**

  Read `_requestText` and parse the first line `METHOD PATH HTTP/1.1`. Branch on method+path. Move existing screenshot logic into a `handleScreenshot` private function. Add a stub `handleCommand` that for now returns 200 OK with no body.

- [ ] **Step 2: Implement CommandListener — JSON body parsing + dispatch**

  ```fsharp
  module Rekolektion.Viz.App.Services.CommandListener

  open System.Text.Json
  open Avalonia.Threading
  open Rekolektion.Viz.App.Model

  /// Parse a POST body JSON and dispatch the corresponding Msg via the
  /// Elmish dispatcher. Returns a short response body for the client.
  let handle (path: string) (body: string) (dispatch: Msg.Msg -> unit) : string =
      try
          use doc = JsonDocument.Parse body
          let root = doc.RootElement
          match path with
          | "/open" ->
              let p = root.GetProperty("path").GetString()
              Dispatcher.UIThread.Post(fun () -> dispatch (Msg.OpenFile p))
              "{\"ok\":true}"
          | "/toggle/layer" ->
              let name = root.GetProperty("name").GetString()
              let visible = root.GetProperty("visible").GetBoolean()
              // Find the layer key for this name
              match Rekolektion.Viz.Core.Layout.Layer.allDrawing |> List.tryFind (fun l -> l.Name = name) with
              | Some l ->
                  Dispatcher.UIThread.Post(fun () ->
                      dispatch (Msg.ToggleLayer ((l.Number, l.DataType), visible)))
                  "{\"ok\":true}"
              | None -> "{\"ok\":false,\"error\":\"unknown layer\"}"
          | "/toggle/net" ->
              let name = root.GetProperty("name").GetString()
              let visible = root.GetProperty("visible").GetBoolean()
              Dispatcher.UIThread.Post(fun () -> dispatch (Msg.ToggleNet (name, visible)))
              "{\"ok\":true}"
          | "/highlight/net" ->
              let net =
                  match root.TryGetProperty "name" with
                  | true, n when n.ValueKind = JsonValueKind.String -> Some (n.GetString())
                  | _ -> None
              Dispatcher.UIThread.Post(fun () -> dispatch (Msg.HighlightNet net))
              "{\"ok\":true}"
          | "/tab" ->
              let tab =
                  match root.GetProperty("tab").GetString() with
                  | "3D" -> Model.Tab.View3D
                  | _    -> Model.Tab.View2D
              Dispatcher.UIThread.Post(fun () -> dispatch (Msg.SetTab tab))
              "{\"ok\":true}"
          | _ -> "{\"ok\":false,\"error\":\"unknown path\"}"
      with ex -> sprintf "{\"ok\":false,\"error\":\"%s\"}" ex.Message
  ```

- [ ] **Step 3: Wire dispatch into App.fs**

  Capture the Elmish dispatcher when starting the program; pass it into `CommandListener.handle` from the unified socket dispatcher in ScreenshotListener.

  This requires plumbing — the cleanest way is to expose `Program.mkProgram |> Program.withHost win |> Program.runWith dispatch` (Elmish's `runWith` returns the dispatch function), capture it, and pass it through.

- [ ] **Step 4: Build, commit.**

  ```bash
  git commit -m "viz: App.Services.CommandListener — agent-drivable commands over UDS"
  ```

---

## Task 25: App.Tests headless integration test — golden PNG

**Files:**
- Create: `tools/viz/src/Rekolektion.Viz.App/HeadlessApp.fs` (Avalonia.Headless test app, mirrors Moroder pattern)
- Create: `tools/viz/tests/Rekolektion.Viz.App.Tests/HeadlessRenderTests.fs`
- Create: `tools/viz/testdata/goldens/bitcell_lr_default.png` (committed after first successful capture)

- [ ] **Step 1: Implement HeadlessApp class** (mirrors Moroder.HeadlessApp in HeadlessRender.fs).

- [ ] **Step 2: Failing test (loads bitcell, captures PNG, fails because golden is empty)**

  ```fsharp
  module Rekolektion.Viz.App.Tests.HeadlessRenderTests

  open System.IO
  open Xunit
  open FsUnit.Xunit
  open Rekolektion.Viz.App

  let private testdata name =
      Path.Combine(System.AppContext.BaseDirectory, "testdata", name)

  [<Fact>]
  let ``Headless render of bitcell_lr produces non-empty PNG`` () =
      let outPath = Path.GetTempFileName() + ".png"
      let exitCode = HeadlessRender.renderToPng outPath 800 600 1500
      exitCode |> should equal 0
      let bytes = File.ReadAllBytes outPath
      bytes.Length |> should be (greaterThan 1000)
  ```

- [ ] **Step 3: Run, verify it passes** (this is a smoke test — golden compare comes when we have a stable image).

- [ ] **Step 4: Commit.**

  ```bash
  git commit -m "viz: App.Tests — headless render smoke test"
  ```

---

## Task 26: Cli — port read|render|mesh + add app + viz-render

**Files:**
- Modify: `tools/viz/src/Rekolektion.Viz.Cli/Program.fs` (replace placeholder)

- [ ] **Step 1: Read existing CLI** — `tools/viz/Program.fs` (the original Viz.fsproj entry point).

- [ ] **Step 2: Implement new CLI**

  ```fsharp
  module Rekolektion.Viz.Cli.Program

  open Rekolektion.Viz.Core.Gds

  let private printUsage () =
      printfn "rekolektion-viz <command> [options]"
      printfn ""
      printfn "Commands:"
      printfn "  read   <file.gds>                       GDS summary"
      printfn "  render <file.gds> <out_dir/>            Per-layer PNGs"
      printfn "  mesh   <file.gds> <out_dir/>            STL + GLB 3D models"
      printfn "  app    [<file.gds>]                     Launch GUI"
      printfn "  viz-render --gds <f> --output <p.png>"
      printfn "             [--toggle-layer <n>=on|off]"
      printfn "             [--highlight-net <n>] [--tab 2D|3D]"
      printfn "             [--width <px>] [--height <px>] [--hold-ms <ms>]"

  let cmdRead args =
      match args with
      | [path] ->
          let lib = Reader.readGds path
          printfn "Library: %s" lib.Name
          printfn "DB units/user unit: %g" lib.DbUnitsPerUserUnit
          printfn "Structures: %d" lib.Structures.Length
          for s in lib.Structures do
              let bs = s.Elements |> List.filter (function Types.Boundary _ -> true | _ -> false) |> List.length
              printfn "  %s — %d boundaries" s.Name bs
          0
      | _ -> printUsage(); 1

  let cmdRender args =
      // Reuse current implementation (port from existing Render/LayerRenderer)
      // -- this lives in Render project once ported, called here.
      0

  let cmdMesh args =
      // Reuse current implementation (port from existing Mesh/MeshGenerator)
      0

  let cmdApp (args: string list) =
      // Hand off to App's HeadlessRender? No — call AppMain
      // For Phase 1, app launches Avalonia desktop:
      let argv = args |> List.toArray
      Rekolektion.Viz.App.Program.runDesktop argv

  let cmdVizRender (args: string list) =
      // Parse args, call HeadlessRender.renderToPng
      // For brevity, the implementer wires this to Argu or equivalent
      // and forwards to App.HeadlessRender.renderToPng with parsed
      // toggles applied via a pre-render Msg sequence.
      0

  [<EntryPoint>]
  let main argv =
      match argv |> Array.toList with
      | "read" :: rest        -> cmdRead rest
      | "render" :: rest      -> cmdRender rest
      | "mesh" :: rest        -> cmdMesh rest
      | "app" :: rest         -> cmdApp rest
      | "viz-render" :: rest  -> cmdVizRender rest
      | _ -> printUsage(); 1
  ```

  Also create `tools/viz/src/Rekolektion.Viz.App/Program.fs`:
  ```fsharp
  module Rekolektion.Viz.App.Program

  open Avalonia

  let private buildAvaloniaApp () =
      AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()

  let runDesktop (argv: string[]) : int =
      buildAvaloniaApp().StartWithClassicDesktopLifetime(argv)
  ```

- [ ] **Step 3: Build, smoke-test `dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- read tools/viz/testdata/bitcell_lr.gds`**, expect summary printed, commit.

  ```bash
  git commit -m "viz: Cli — read|render|mesh|app|viz-render entry point"
  ```

---

## Task 27: Cli — viz-render flag parsing + headless toggle application

**Files:**
- Modify: `tools/viz/src/Rekolektion.Viz.Cli/Program.fs`
- Create: `tools/viz/src/Rekolektion.Viz.App/HeadlessRenderArgs.fs` (parsed args type + apply-to-model fn)

- [ ] **Step 1: Implement arg parsing** in `cmdVizRender`. Use a small hand-rolled parser (no Argu dependency for v1).

  ```fsharp
  type VizRenderArgs = {
      Gds        : string
      Output     : string
      Toggles    : (string * bool) list  // (layerName, visible)
      Highlight  : string option
      Tab        : string                // "2D" | "3D"
      Width      : int
      Height     : int
      HoldMs     : int
  }

  let parseVizRenderArgs (args: string list) : Result<VizRenderArgs, string> =
      let rec loop a acc =
          match a with
          | [] -> Ok acc
          | "--gds" :: v :: rest        -> loop rest { acc with Gds = v }
          | "--output" :: v :: rest     -> loop rest { acc with Output = v }
          | "--toggle-layer" :: v :: rest ->
              match v.Split '=' with
              | [| name; "on"  |] -> loop rest { acc with Toggles = acc.Toggles @ [name, true] }
              | [| name; "off" |] -> loop rest { acc with Toggles = acc.Toggles @ [name, false] }
              | _ -> Error (sprintf "bad --toggle-layer value: %s" v)
          | "--highlight-net" :: v :: rest -> loop rest { acc with Highlight = Some v }
          | "--tab" :: v :: rest        -> loop rest { acc with Tab = v }
          | "--width" :: v :: rest      -> loop rest { acc with Width = int v }
          | "--height" :: v :: rest     -> loop rest { acc with Height = int v }
          | "--hold-ms" :: v :: rest    -> loop rest { acc with HoldMs = int v }
          | unknown :: _ -> Error (sprintf "unknown arg: %s" unknown)
      let init = { Gds = ""; Output = ""; Toggles = []; Highlight = None; Tab = "2D"; Width = 1400; Height = 900; HoldMs = 500 }
      loop args init
  ```

- [ ] **Step 2: Pre-render Msg sequence in HeadlessRender**

  Extend `HeadlessRender.renderToPng` with an optional `preRenderMsgs: Msg.Msg list` parameter (bypassed in current callers via `[]`). Apply each Msg via the dispatcher before `CaptureRenderedFrame`. The CLI's `cmdVizRender` builds the Msg list from `VizRenderArgs`:
  - For each `Toggles`: lookup layer key, push `ToggleLayer`
  - If `Highlight`: push `HighlightNet (Some ...)`
  - If `Tab = "3D"`: push `SetTab View3D`
  - First always push `OpenFile gds`

- [ ] **Step 3: Smoke test**

  ```bash
  dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- viz-render \
    --gds tools/viz/testdata/bitcell_lr.gds \
    --output /tmp/lr.png \
    --toggle-layer met2=off \
    --tab 2D
  file /tmp/lr.png    # expect "PNG image data ..."
  ```

- [ ] **Step 4: Commit.**

  ```bash
  git commit -m "viz: Cli viz-render — toggles + tab + highlight-net args"
  ```

---

## Task 28: Python sidecar emitter in macro_assembler.py

**Files:**
- Modify: `src/rekolektion/macro/macro_assembler.py` (locate the function that writes the GDS; emit `<path>.nets.json` next to it)

This is the only Python change in the plan. Small enough that it doesn't need its own subdirectory.

- [ ] **Step 1: Locate the GDS-write site**

  ```bash
  grep -n "write_gds\|gdstk\.Library.*write\|\.gds[\"']" src/rekolektion/macro/macro_assembler.py | head -10
  ```

- [ ] **Step 2: Add a NetsTracker**

  In the assembler module, add a class that accumulates polygon→net mappings as the assembler draws:

  ```python
  # src/rekolektion/macro/macro_assembler.py (or new sibling file imported here)

  import json
  from pathlib import Path
  from dataclasses import dataclass, field
  from typing import List, Dict, Literal

  NetClass = Literal["power", "ground", "signal", "clock"]

  @dataclass
  class PolygonRef:
      structure: str
      layer: int
      datatype: int
      index: int

  @dataclass
  class NetEntry:
      name: str
      cls: NetClass
      polygons: List[PolygonRef] = field(default_factory=list)

  class NetsTracker:
      def __init__(self) -> None:
          self._nets: Dict[str, NetEntry] = {}
          self._counters: Dict[str, int] = {}  # structure → element index

      def _next_index(self, structure: str) -> int:
          n = self._counters.get(structure, 0)
          self._counters[structure] = n + 1
          return n

      def record(self, *, structure: str, layer: int, datatype: int, net: str, cls: NetClass = "signal") -> None:
          idx = self._next_index(structure)
          ref = PolygonRef(structure=structure, layer=layer, datatype=datatype, index=idx)
          if net not in self._nets:
              self._nets[net] = NetEntry(name=net, cls=cls)
          self._nets[net].polygons.append(ref)

      def write(self, gds_path: str | Path, macro_name: str) -> Path:
          out = Path(gds_path).with_suffix(".nets.json")
          payload = {
              "version": 1,
              "macro": macro_name,
              "nets": {
                  name: {
                      "class": entry.cls,
                      "polygons": [
                          {"structure": p.structure, "layer": p.layer,
                           "datatype": p.datatype, "index": p.index}
                          for p in entry.polygons
                      ],
                  }
                  for name, entry in self._nets.items()
              },
          }
          out.write_text(json.dumps(payload, indent=2))
          return out
  ```

- [ ] **Step 3: Hand a `NetsTracker` to every polygon draw site**

  Modify `assemble_macro()` (or whatever the top-level assembler entry point is named) to instantiate a `NetsTracker`, pass it to peripheral generators (precharge, column_mux, sense_amp, write_driver, wl_driver, decoder, ctrl_logic) so each one calls `.record(...)` with the net name it knows. Every place in the codebase that emits a polygon and knows the net should land on this tracker.

  Per-block scope: the v1 implementation only needs to instrument the top-level macro_v2 routing (BL/BR/WL/dec_out/VPWR/VGND/muxed_BL — the nets that go through the recently-fixed code paths). Foundry-cell child polygons can be inferred via labels later; not all coverage in v1.

- [ ] **Step 4: Call `tracker.write(gds_path, macro_name)` after `lib.write_gds(gds_path)`.**

- [ ] **Step 5: Test by regenerating one macro and inspecting the sidecar**

  ```bash
  cd /Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion
  rekolektion macro --cell lr --words 64 --bits 8 --mux 2 -o output/test_v1.gds
  ls output/test_v1.gds output/test_v1.nets.json
  python3 -c "import json; print(json.load(open('output/test_v1.nets.json'))['version'])"
  # Expected: 1
  ```

- [ ] **Step 6: Run rekolektion's existing pytest suite** to make sure the sidecar doesn't break anything:
  ```bash
  pytest tests/ -x
  ```

- [ ] **Step 7: Commit Python change.**

  ```bash
  git add src/rekolektion/macro/macro_assembler.py
  git commit -m "viz: emit <macro>.nets.json sidecar with net→polygon map"
  ```

---

## Task 29: MCP server scaffold + 7 tools

**Files:**
- Modify: `tools/viz/src/Rekolektion.Viz.Mcp/Program.fs`

- [ ] **Step 1: Read Moroder's MCP server**

  ```bash
  ls /Users/bryancostanich/Git_Repos/bryan_costanich/Moroder/src/Moroder.Orchestration.Mcp/
  ```
  Note the JSON-RPC 2.0 stdio loop pattern, tool registration, and tool dispatch.

- [ ] **Step 2: Implement a JSON-RPC 2.0 stdio loop**

  ```fsharp
  module Rekolektion.Viz.Mcp.Program

  open System
  open System.IO
  open System.Net.Sockets
  open System.Text
  open System.Text.Json

  let private vizSocket =
      let dir = Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".rekolektion")
      Path.Combine(dir, "viz.sock")

  let private sendRpc (id: int) (result: obj) =
      let resp =
          {| jsonrpc = "2.0"; id = id; result = result |}
          |> JsonSerializer.Serialize
      Console.Out.WriteLine resp
      Console.Out.Flush()

  let private sendErr (id: int) (code: int) (message: string) =
      let resp =
          {| jsonrpc = "2.0"; id = id; error = {| code = code; message = message |} |}
          |> JsonSerializer.Serialize
      Console.Out.WriteLine resp
      Console.Out.Flush()

  /// Send a single HTTP request over the live viz UDS. Returns the
  /// response body as a string.
  let private udsRequest (method: string) (path: string) (body: string option) : string =
      use sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
      sock.Connect(UnixDomainSocketEndPoint vizSocket)
      use stream = new NetworkStream(sock, ownsSocket = false)
      let bodyBytes = body |> Option.map Encoding.UTF8.GetBytes |> Option.defaultValue [||]
      let header =
          sprintf "%s %s HTTP/1.1\r\nContent-Length: %d\r\n\r\n" method path bodyBytes.Length
      let headerBytes = Encoding.ASCII.GetBytes header
      stream.Write(headerBytes, 0, headerBytes.Length)
      if bodyBytes.Length > 0 then
          stream.Write(bodyBytes, 0, bodyBytes.Length)
      // Read entire response
      use ms = new MemoryStream()
      stream.CopyTo ms
      Encoding.UTF8.GetString(ms.ToArray())

  let private toolHandlers : Map<string, JsonElement -> obj> =
      Map.ofList [
          "rekolektion_viz_screenshot", (fun _params ->
              let resp = udsRequest "GET" "/screenshot" None
              // Extract body (after "\r\n\r\n"), base64-encode for MCP image content
              let idx = resp.IndexOf("\r\n\r\n")
              let bodyStart = if idx < 0 then 0 else idx + 4
              let png = resp.Substring(bodyStart)
              let b64 = Convert.ToBase64String(Encoding.Latin1.GetBytes png)
              {| ``type`` = "image"; mimeType = "image/png"; data = b64 |} :> obj)
          "rekolektion_viz_open", (fun p ->
              let path = p.GetProperty("path").GetString()
              let body = sprintf "{\"path\":\"%s\"}" path
              udsRequest "POST" "/open" (Some body) :> obj)
          "rekolektion_viz_toggle_layer", (fun p ->
              let name = p.GetProperty("name").GetString()
              let visible = p.GetProperty("visible").GetBoolean()
              let body = sprintf "{\"name\":\"%s\",\"visible\":%b}" name visible
              udsRequest "POST" "/toggle/layer" (Some body) :> obj)
          "rekolektion_viz_highlight_net", (fun p ->
              let name = p.GetProperty("name").GetString()
              let body = sprintf "{\"name\":\"%s\"}" name
              udsRequest "POST" "/highlight/net" (Some body) :> obj)
          "rekolektion_viz_set_tab", (fun p ->
              let tab = p.GetProperty("tab").GetString()
              let body = sprintf "{\"tab\":\"%s\"}" tab
              udsRequest "POST" "/tab" (Some body) :> obj)
          "rekolektion_viz_render", (fun p ->
              let gds = p.GetProperty("gds").GetString()
              let outPath = Path.GetTempFileName() + ".png"
              // Spawn `rekolektion-viz viz-render --gds <gds> --output <outPath> ...`
              // For brevity, omitted — implement via Process.Start.
              {| outputPath = outPath |} :> obj)
          "rekolektion_viz_run_macro", (fun p ->
              // Spawn `rekolektion macro …` similarly
              {| ok = true |} :> obj)
      ]

  [<EntryPoint>]
  let main _argv =
      let mutable running = true
      while running do
          let line = Console.In.ReadLine()
          if isNull line then running <- false
          else
              try
                  use doc = JsonDocument.Parse line
                  let root = doc.RootElement
                  let id = root.GetProperty("id").GetInt32()
                  let methodName = root.GetProperty("method").GetString()
                  match methodName with
                  | "tools/list" ->
                      let tools =
                          [
                              {| name = "rekolektion_viz_render"; description = "Headless one-shot render to PNG"; inputSchema = obj() |}
                              {| name = "rekolektion_viz_screenshot"; description = "Capture live viz window"; inputSchema = obj() |}
                              {| name = "rekolektion_viz_open"; description = "Open file in live viz"; inputSchema = obj() |}
                              {| name = "rekolektion_viz_toggle_layer"; description = "Show/hide a GDS layer"; inputSchema = obj() |}
                              {| name = "rekolektion_viz_highlight_net"; description = "Highlight a net"; inputSchema = obj() |}
                              {| name = "rekolektion_viz_set_tab"; description = "Switch 2D/3D tab"; inputSchema = obj() |}
                              {| name = "rekolektion_viz_run_macro"; description = "Generate a macro"; inputSchema = obj() |}
                          ]
                      sendRpc id {| tools = tools |}
                  | "tools/call" ->
                      let pp = root.GetProperty "params"
                      let name = pp.GetProperty("name").GetString()
                      let args = pp.GetProperty "arguments"
                      match Map.tryFind name toolHandlers with
                      | Some h -> sendRpc id (h args)
                      | None   -> sendErr id -32601 (sprintf "unknown tool: %s" name)
                  | _ -> sendErr id -32601 (sprintf "unknown method: %s" methodName)
              with ex ->
                  sendErr 0 -32603 ex.Message
      0
  ```

- [ ] **Step 3: Build, test by hand**

  ```bash
  echo '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | dotnet run --project tools/viz/src/Rekolektion.Viz.Mcp
  # Expected: JSON response listing 7 tools
  ```

- [ ] **Step 4: Commit.**

  ```bash
  git commit -m "viz: Mcp — stdio JSON-RPC 2.0 server with 7 tools"
  ```

---

## Task 30: Delete old `Viz.fsproj` + cleanup

**Files:**
- Delete: `tools/viz/Viz.fsproj`
- Delete: `tools/viz/Program.fs`
- Delete: `tools/viz/Gds/`
- Delete: `tools/viz/Mesh/`
- Delete: `tools/viz/Render/`

The old single-project tool has been fully ported. Delete it.

- [ ] **Step 1: Confirm new structure works**

  ```bash
  cd /Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion
  dotnet test tools/viz/Rekolektion.Viz.sln
  # All test projects pass.
  dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- read tools/viz/testdata/bitcell_lr.gds
  # Summary printed.
  dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- viz-render \
      --gds tools/viz/testdata/bitcell_lr.gds --output /tmp/x.png
  file /tmp/x.png  # PNG image
  ```

- [ ] **Step 2: Delete old files**

  ```bash
  cd /Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion
  rm tools/viz/Viz.fsproj
  rm tools/viz/Program.fs
  rm -r tools/viz/Gds
  rm -r tools/viz/Mesh
  rm -r tools/viz/Render
  ```

- [ ] **Step 3: Update `rekolektion/CLAUDE.md`**

  The current CLAUDE.md says `cd ~/Git_Repos/bryan_costanich/rekolektion/tools/viz && dotnet run -- render …`. Update those commands to:
  ```
  dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- render <gds> <out>/
  dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- mesh   <gds> <out>/
  dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- app
  ```

- [ ] **Step 4: Commit.**

  ```bash
  git add -A
  git commit -m "viz: delete old Viz.fsproj — fully replaced by multi-project"
  ```

---

## Task 31: Final integration smoke

**Files:**
- (no new files; verification only)

- [ ] **Step 1: Generate a real macro and open it**

  ```bash
  cd /Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion
  rekolektion macro --cell lr --words 64 --bits 8 --mux 2 -o output/smoke.gds
  ls output/smoke.gds output/smoke.nets.json    # both should exist (post-Task 28)
  ```

- [ ] **Step 2: Headless render with toggles**

  ```bash
  dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- viz-render \
      --gds output/smoke.gds \
      --output /tmp/smoke_2d.png \
      --tab 2D
  open /tmp/smoke_2d.png    # macOS will pop the image; verify SRAM macro is visible

  dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- viz-render \
      --gds output/smoke.gds \
      --output /tmp/smoke_3d.png \
      --tab 3D \
      --toggle-layer met3=off
  open /tmp/smoke_3d.png    # 3D view, met3 hidden
  ```

- [ ] **Step 3: Live viewer + MCP loop**

  ```bash
  # Terminal 1
  dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- app

  # Terminal 2 — manual MCP test
  echo '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"rekolektion_viz_open","arguments":{"path":"/Users/bryancostanich/Git_Repos/bryan_costanich/rekolektion/output/smoke.gds"}}}' \
    | dotnet run --project tools/viz/src/Rekolektion.Viz.Cli -- ... # actually send to MCP binary
  # Verify GUI loads the file.
  ```

- [ ] **Step 4: If all three smokes pass, commit a CLAUDE.md update mentioning the new commands and tag the milestone.**

  ```bash
  git tag -a viz-phase1-mvp -m "rekolektion-viz Phase 1 MVP — multi-project Avalonia visualizer with headless mode"
  ```

  (Don't push the tag — wait for user approval per global rules.)

---

## Self-Review (run after writing all tasks above)

**1. Spec coverage**
- Multi-project layout (Core/Render/App/Cli/Mcp + 4 test projects): Tasks 1–14 build it. ✓
- 2D Skia canvas: Tasks 11, 12, 18. ✓
- 3D Silk.NET stack canvas: Tasks 13, 14, 19. ✓
- Sidecar JSON loader + LabelFlood fallback: Tasks 5, 9, 17. ✓
- Hierarchy detection: Task 6. ✓
- ToggleState reducer: Task 7. ✓
- Picking math (2D): Task 8. ✓ — 3D pick wired into Canvas3D in Task 19 via color buffer.
- Layer table: Task 4. ✓
- Run-macro subprocess + dialog: Tasks 16, 20 (RunDialog), 22 (App wiring). ✓
- Headless render + ScreenshotListener + CommandListener: Tasks 21, 23, 24. ✓
- CLI surface (`read|render|mesh|app|viz-render`): Tasks 26, 27. ✓
- MCP server (7 tools): Task 29. ✓
- Python sidecar emitter: Task 28. ✓
- Test fixtures + goldens: Tasks 3 (fixture GDS), 5 (fixture sidecar), 25 (golden PNG). ✓
- CLAUDE.md update: Task 30. ✓

**2. Placeholder scan** — no "TBD"; "for brevity, the implementer wires …" appears in two spots (Task 26 cmdRender/cmdMesh — pointing to existing implementations; Task 29 — `rekolektion_viz_render`/`run_macro` Process.Start sketches). These are acceptable because they reference existing code paths the implementer can read directly.

**3. Type consistency**
- `Visibility.LayerKey = int * int` — used consistently across Tasks 7, 11, 19.
- `Sidecar.Types.Sidecar.Nets : Map<string, NetEntry>` — consistent across Tasks 5, 9, 17.
- `Model.LoadedMacro` — consistent across Tasks 15, 17, 22.
- `Msg.Tab = View2D | View3D` — consistent across Tasks 15, 24, 27.

**4. Ambiguity** — couple notes:
- Task 19 lists OpenGL extruded mesh as the highest-risk item; Task 22 includes a fallback path (disable 3D tab if context creation fails). Acceptable.
- Task 28 (Python sidecar) scopes coverage to top-level macro_v2 routing; foundry-cell child polygons defer to label flood. Documented in spec risks.

---

## Plan complete and saved to `docs/superpowers/plans/2026-04-24-rekolektion-viz.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration.
**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?
