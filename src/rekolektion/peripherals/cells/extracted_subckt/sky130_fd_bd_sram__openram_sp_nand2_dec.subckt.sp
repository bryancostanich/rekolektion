* NGSPICE file created from sky130_fd_bd_sram__openram_sp_nand2_dec.ext - technology: sky130B

.subckt sky130_fd_bd_sram__openram_sp_nand2_dec A B Z GND VDD
X0 VDD B Z VDD sky130_fd_pr__pfet_01v8 ad=0.168 pd=1.42 as=0.3024 ps=2.78 w=1.12 l=0.15
X1 a_174_144# B GND GND sky130_fd_pr__nfet_01v8 ad=0.0777 pd=0.95 as=0.222 ps=2.08 w=0.74 l=0.15
X2 Z A VDD VDD sky130_fd_pr__pfet_01v8 ad=0.3752 pd=2.91 as=0.168 ps=1.42 w=1.12 l=0.15
X3 Z A a_174_144# GND sky130_fd_pr__nfet_01v8 ad=0.2701 pd=2.21 as=0.0777 ps=0.95 w=0.74 l=0.15
.ends

