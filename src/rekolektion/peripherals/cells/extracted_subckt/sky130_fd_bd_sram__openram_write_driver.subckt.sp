* NGSPICE file created from sky130_fd_bd_sram__openram_write_driver.ext - technology: sky130B

.subckt sky130_fd_bd_sram__openram_write_driver BR EN BL VDD DIN GND
X0 a_213_736# EN a_129_736# GND sky130_fd_pr__nfet_01v8 ad=0.07975 pd=0.84 as=0.1485 ps=1.64 w=0.55 l=0.15
X1 a_271_690# DIN GND GND sky130_fd_pr__nfet_01v8 ad=0.1044 pd=1.3 as=0.08865 ps=0.9 w=0.36 l=0.15
X2 VDD a_41_1120# a_121_1585# VDD sky130_fd_pr__pfet_01v8 ad=0.07975 pd=0.84 as=0.1485 ps=1.64 w=0.55 l=0.15
X3 a_271_690# DIN VDD VDD sky130_fd_pr__pfet_01v8 ad=0.15125 pd=1.65 as=0.07975 ps=0.84 w=0.55 l=0.15
X4 a_129_736# a_271_690# VDD VDD sky130_fd_pr__pfet_01v8 ad=0.15125 pd=1.65 as=0.07975 ps=0.84 w=0.55 l=0.15
X5 a_41_1120# EN VDD VDD sky130_fd_pr__pfet_01v8 ad=0.07975 pd=0.84 as=0.1485 ps=1.64 w=0.55 l=0.15
X6 BR a_121_1585# GND GND sky130_fd_pr__nfet_01v8 ad=0.27 pd=2.54 as=0.145 ps=1.29 w=1 l=0.15
X7 a_183_1687# a_129_736# GND GND sky130_fd_pr__nfet_01v8 ad=0.0972 pd=1.26 as=0.0522 ps=0.65 w=0.36 l=0.15
X8 GND DIN a_145_492# GND sky130_fd_pr__nfet_01v8 ad=0.08865 pd=0.9 as=0.07975 ps=0.84 w=0.55 l=0.15
X9 VDD EN a_129_736# VDD sky130_fd_pr__pfet_01v8 ad=0.07975 pd=0.84 as=0.1485 ps=1.64 w=0.55 l=0.15
X10 GND a_271_690# a_213_736# GND sky130_fd_pr__nfet_01v8 ad=0.1595 pd=1.68 as=0.07975 ps=0.84 w=0.55 l=0.15
X11 a_183_1687# a_129_736# VDD VDD sky130_fd_pr__pfet_01v8 ad=0.1485 pd=1.64 as=0.07975 ps=0.84 w=0.55 l=0.15
X12 GND a_183_1687# BL GND sky130_fd_pr__nfet_01v8 ad=0.145 pd=1.29 as=0.27 ps=2.54 w=1 l=0.15
X13 GND a_41_1120# a_121_1585# GND sky130_fd_pr__nfet_01v8 ad=0.0522 pd=0.65 as=0.0972 ps=1.26 w=0.36 l=0.15
X14 VDD DIN a_41_1120# VDD sky130_fd_pr__pfet_01v8 ad=0.07975 pd=0.84 as=0.07975 ps=0.84 w=0.55 l=0.15
X15 a_145_492# EN a_41_1120# GND sky130_fd_pr__nfet_01v8 ad=0.07975 pd=0.84 as=0.1485 ps=1.64 w=0.55 l=0.15
.ends

