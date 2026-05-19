"""Build cell_designs/khalkulo/cim_reram_drv_phaseA.rkt — thin wrapper
around the per-array srcmux. One wrapper per array (D16).

The wrapper SRefs the srcmux at the origin and re-exposes each port
as a top-cell label so Magic's port_makeall promotes them to the
wrapper subckt's pin list. (Magic's hierarchical port-promotion is
flaky — see CLAUDE.md note — so every wrapper port needs its own
top-cell label on the right metal.)
"""
from pathlib import Path
from rekolektion.io import rkt

# Label coordinates land on existing srcmux metal at each port's net.
# Picked from the srcmux build script's known geometry:
#   SRC_RAIL bridge      : met2 at y=12150..12400, x=21805..36640
#   VDDA1   bridge       : met2 at y=-12500..-12170, x=21805..36640
#   VDD     strap        : met2 at y=3630..3770,    x=25130..33500
#   VSS     top horiz    : met1 at y=12505..12950,  x=23230..34565
#   pulse_req_1v8 li1    : li1  at y=-11176..-3495, x=23515..23685
#   pulse_kind_form_1v8  : met1 at nf.B pin (26443, -3545), 320×320 patch
#   pulse_kind_setrr_1v8 : met1 at ns.B pin (26430, -11056), 320×320 patch
doc = rkt.Document(
    imports=[
        rkt.Import(path="./cim_reram_drv_phaseA_srcmux.rkt"),
    ],
    cells=[
        rkt.Cell(
            name='cim_reram_drv_phaseA',
            elements=[
                rkt.SRef(cell='cim_reram_drv_phaseA_srcmux', origin=(0, 0)),
                # Power ports
                rkt.port_label(layer=rkt.named("sky130", "met2_label"),
                               text="VDDA1", origin=(29000, -12335)),
                rkt.port_label(layer=rkt.named("sky130", "met2_label"),
                               text="VDD",   origin=(29000, 3700)),
                rkt.port_label(layer=rkt.named("sky130", "met1_label"),
                               text="VSS",   origin=(29000, 12727)),
                # SRC_RAIL — per-array output
                rkt.port_label(layer=rkt.named("sky130", "met2_label"),
                               text="SRC_RAIL", origin=(29000, 12275)),
                # Control inputs (1.8 V)
                rkt.port_label(layer=rkt.named("sky130", "li1_label"),
                               text="pulse_req_1v8", origin=(23700, -7000)),
                rkt.port_label(layer=rkt.named("sky130", "met1_label"),
                               text="pulse_kind_form_1v8",
                               origin=(26443, -3545)),
                rkt.port_label(layer=rkt.named("sky130", "met1_label"),
                               text="pulse_kind_setrr_1v8",
                               origin=(26430, -11056)),
            ],
        ),
    ],
    top_cell='cim_reram_drv_phaseA',
)

out = Path("cell_designs/khalkulo/cim_reram_drv_phaseA.rkt")
out.write_text(rkt.write(doc))
print(f"wrote {out}")
