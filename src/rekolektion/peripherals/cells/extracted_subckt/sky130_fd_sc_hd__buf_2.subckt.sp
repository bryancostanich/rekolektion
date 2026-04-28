* NGSPICE file created from sky130_fd_sc_hd__buf_2.ext - technology: sky130B

.subckt sky130_fd_sc_hd__buf_2 A VGND VNB VPB VPWR X
X0 VPWR a_27_47# X VPB sky130_fd_pr__pfet_01v8_hvt ad=0.265 pd=2.53 as=0.135 ps=1.27 w=1 l=0.15
X1 X a_27_47# VPWR VPB sky130_fd_pr__pfet_01v8_hvt ad=0.135 pd=1.27 as=0.149 ps=1.325 w=1 l=0.15
X2 VPWR A a_27_47# VPB sky130_fd_pr__pfet_01v8_hvt ad=0.149 pd=1.325 as=0.1664 ps=1.8 w=0.64 l=0.15
X3 X a_27_47# VGND VNB sky130_fd_pr__nfet_01v8 ad=0.08775 pd=0.92 as=0.097 ps=0.975 w=0.65 l=0.15
X4 VGND a_27_47# X VNB sky130_fd_pr__nfet_01v8 ad=0.17225 pd=1.83 as=0.08775 ps=0.92 w=0.65 l=0.15
X5 VGND A a_27_47# VNB sky130_fd_pr__nfet_01v8 ad=0.097 pd=0.975 as=0.1092 ps=1.36 w=0.42 l=0.15
.ends

