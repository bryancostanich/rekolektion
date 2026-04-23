* NGSPICE file created from sky130_fd_bd_sram__openram_sense_amp.ext - technology: sky130B

.subckt sky130_fd_bd_sram__openram_sense_amp BR BL EN VDD DOUT GND
X0 a_154_1298# a_96_1689# VDD VDD sky130_fd_pr__pfet_01v8 ad=0.3402 pd=3.06 as=0.1827 ps=1.55 w=1.26 l=0.15
X1 GND EN a_184_1689# GND sky130_fd_pr__nfet_01v8 ad=0.1885 pd=1.88 as=0.1885 ps=1.88 w=0.65 l=0.15
X2 a_154_1298# a_96_1689# a_184_1689# GND sky130_fd_pr__nfet_01v8 ad=0.1885 pd=1.88 as=0.09425 ps=0.94 w=0.65 l=0.15
X3 GND a_154_1298# DOUT GND sky130_fd_pr__nfet_01v8 ad=0.182 pd=1.86 as=0.1885 ps=1.88 w=0.65 l=0.15
X4 BL EN a_96_1689# VDD sky130_fd_pr__pfet_01v8 ad=0.54 pd=4.54 as=0.54 ps=4.54 w=2 l=0.15
X5 a_154_1298# EN BR VDD sky130_fd_pr__pfet_01v8 ad=0.54 pd=4.54 as=0.54 ps=4.54 w=2 l=0.15
X6 VDD a_154_1298# a_96_1689# VDD sky130_fd_pr__pfet_01v8 ad=0.1827 pd=1.55 as=0.3402 ps=3.06 w=1.26 l=0.15
X7 a_184_1689# a_154_1298# a_96_1689# GND sky130_fd_pr__nfet_01v8 ad=0.09425 pd=0.94 as=0.1885 ps=1.88 w=0.65 l=0.15
X8 VDD a_154_1298# DOUT VDD sky130_fd_pr__pfet_01v8 ad=0.3402 pd=3.06 as=0.3654 ps=3.1 w=1.26 l=0.15
.ends

