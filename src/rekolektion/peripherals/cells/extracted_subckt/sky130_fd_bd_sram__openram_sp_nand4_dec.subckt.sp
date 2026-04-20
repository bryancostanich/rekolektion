* NGSPICE file created from sky130_fd_bd_sram__openram_sp_nand4_dec.ext - technology: sky130B

.subckt sky130_fd_bd_sram__openram_sp_nand4_dec A B C D Z GND VDD
X0 Z C VDD VDD sky130_fd_pr__pfet_01v8 ad=0.336 pd=2.84 as=0.196 ps=1.47 w=1.12 l=0.15
X1 a_92_117# D a_92_27# GND sky130_fd_pr__nfet_01v8 ad=0.0777 pd=0.95 as=0.222 ps=2.08 w=0.74 l=0.15
X2 VDD D Z VDD sky130_fd_pr__pfet_01v8 ad=0.196 pd=1.47 as=0.336 ps=2.84 w=1.12 l=0.15
X3 Z A VDD VDD sky130_fd_pr__pfet_01v8 ad=0.3416 pd=2.85 as=0.1932 ps=1.465 w=1.12 l=0.15
X4 GND A a_92_117# GND sky130_fd_pr__nfet_01v8 ad=0.2109 pd=2.05 as=0.0777 ps=0.95 w=0.74 l=0.15
X5 a_600_162# C Z GND sky130_fd_pr__nfet_01v8 ad=0.0777 pd=0.95 as=0.1961 ps=2.01 w=0.74 l=0.15
X6 VDD B Z VDD sky130_fd_pr__pfet_01v8 ad=0.1932 pd=1.465 as=0.336 ps=2.84 w=1.12 l=0.15
X7 a_92_27# B a_600_162# GND sky130_fd_pr__nfet_01v8 ad=0.2035 pd=2.03 as=0.0777 ps=0.95 w=0.74 l=0.15
.ends

