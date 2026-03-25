#!/bin/bash
# Regenerate all outputs from the current bitcell design.
# Run this after any change to the bitcell generator.

set -e
cd "$(dirname "$0")/.."

echo "=== Generating bitcell GDS + SPICE ==="
python3 -c "
from rekolektion.bitcell.sky130_6t import generate_bitcell
generate_bitcell('output/sky130_sram_6t_bitcell.gds', generate_spice=True)
"

echo ""
echo "=== Generating SVG ==="
python3 -c "
from rekolektion.bitcell.sky130_6t import create_bitcell
cell = create_bitcell()
style = {
    (65,20): {'fill':'#FFD080','stroke':'#B08030','stroke-width':'0.3','fill-opacity':'0.7'},
    (65,44): {'fill':'#FFD080','stroke':'#B08030','stroke-width':'0.3','fill-opacity':'0.7'},
    (64,20): {'fill':'#A0C8FF','stroke':'#4080C0','stroke-width':'0.3','fill-opacity':'0.3'},
    (66,20): {'fill':'#FF4040','stroke':'#C00000','stroke-width':'0.3','fill-opacity':'0.7'},
    (66,44): {'fill':'#808080','stroke':'#404040','stroke-width':'0.3','fill-opacity':'0.9'},
    (67,20): {'fill':'#C080FF','stroke':'#8040C0','stroke-width':'0.3','fill-opacity':'0.6'},
    (67,44): {'fill':'#606060','stroke':'#303030','stroke-width':'0.3','fill-opacity':'0.9'},
    (68,20): {'fill':'#4090FF','stroke':'#2060C0','stroke-width':'0.3','fill-opacity':'0.5'},
    (93,44): {'fill':'#FFFF40','stroke':'#C0C000','stroke-width':'0.2','fill-opacity':'0.2'},
    (94,20): {'fill':'#FF80FF','stroke':'#C040C0','stroke-width':'0.2','fill-opacity':'0.2'},
    (235,4): {'fill':'none','stroke':'#000000','stroke-width':'0.5','stroke-dasharray':'2,1'},
}
cell.write_svg('output/bitcell_layout.svg', scaling=800, shape_style=style, background='#FFFFFF', pad='8%')
print('  output/bitcell_layout.svg')
"

echo ""
echo "=== Generating 3D (STL + GLB + in-situ) ==="
python3 scripts/gds_to_stl.py output/sky130_sram_6t_bitcell.gds output/3d/

echo ""
echo "=== Done ==="
ls -lh output/sky130_sram_6t_bitcell.gds output/sky130_sram_6t_bitcell.spice output/bitcell_layout.svg output/3d/bitcell_3d.glb output/3d/bitcell_3d_in_situ.glb 2>/dev/null
