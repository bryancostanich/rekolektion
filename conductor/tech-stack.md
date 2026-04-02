# Tech Stack

## Process
- **Target process**: SkyWater SKY130 (130nm CMOS, open-source PDK)
- **PDK**: skywater-pdk via volare (`$HOME/.volare/sky130A`)

## Generator
- **Language**: Python >= 3.10
- **Layout engine**: gdstk (GDS generation)
- **Package**: `pip install -e ".[dev]"`

## Verification
- **DRC**: Magic (batch mode, scripted)
- **LVS**: netgen (batch mode)
- **SPICE**: ngspice with SKY130 PDK models

## Output Artifacts (per macro)
- GDS — flattened layout
- LEF — abstract for place-and-route
- Liberty .lib — timing model
- Verilog — behavioral model + blackbox stub
- SPICE — subcircuit netlist

## Visualization
- **Language**: F# (.NET 10)
- **2D**: SkiaSharp (GDS -> per-layer PNGs)
- **3D**: SharpGLTF (GDS -> GLB), raw binary STL
- **Location**: `tools/viz/`

## Integration
- OpenLane 1 (synthesis through routing, validated with 0 violations)
- chipIgnite OpenFrame (via khalkulo)
