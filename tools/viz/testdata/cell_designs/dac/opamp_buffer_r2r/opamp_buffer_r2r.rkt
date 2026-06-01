; opamp_buffer_r2r — rail-to-rail buffer for DAC mid-range taps.
; T21 Phase 5.3 — .rkt port (placement-review state).
; Schematic: source/cell_designs/dac/spice/opamp_buffer_r2r.spice (16 devices).
(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (import "../../primitives/nfet_01v8_W10p0_L0p5_nf2_core_topgate.rkt")
  (import "../../primitives/nfet_01v8_W10p0_L1p0_nf2_core_topgate.rkt")
  (import "../../primitives/nfet_01v8_W1p0_L2p0_core_topgate.rkt")
  (import "../../primitives/nfet_01v8_W4p0_L1p0_core_topgate.rkt")
  (import "../../primitives/nfet_01v8_W8p0_L2p0_core_topgate.rkt")
  (import "../../primitives/pfet_01v8_lvt_W10p0_L0p5_nf2_core_botgate.rkt")
  (import "../../primitives/pfet_01v8_lvt_W10p0_L1p0_nf4_core_botgate.rkt")
  (import "../../primitives/pfet_01v8_lvt_W2p5_L2p0_core_botgate.rkt")
  (import "../../primitives/pfet_01v8_lvt_W4p0_L1p0_core_botgate.rkt")
  (import "../../primitives/pfet_01v8_lvt_W8p0_L1p0_core_botgate.rkt")
  (import "../../primitives/res_xhigh_po_W0p35_L2p6_core.rkt")
  (top opamp_buffer_r2r)
  (cell opamp_buffer_r2r
    (sref (cell pfet_01v8_lvt_W4p0_L1p0_core_botgate) (origin 510 -2092))
    (sref (cell pfet_01v8_lvt_W4p0_L1p0_core_botgate) (origin 2810 -2092))
    (sref (cell pfet_01v8_lvt_W10p0_L1p0_nf4_core_botgate) (origin 9028 -4910) (rot -90.0))
    (sref (cell pfet_01v8_lvt_W8p0_L1p0_core_botgate) (origin 8028 -710) (rot -90.0))
    (sref (cell pfet_01v8_lvt_W8p0_L1p0_core_botgate) (origin 18210 -4192))
    (sref (cell pfet_01v8_lvt_W10p0_L0p5_nf2_core_botgate) (origin 15810 -5192))
    (sref (cell pfet_01v8_lvt_W2p5_L2p0_core_botgate) (origin 1293 -5910) (rot 90.0))
    (sref (cell nfet_01v8_W8p0_L2p0_core_topgate) (origin 15010 -12500) (rot 90.0))
    (sref (cell nfet_01v8_W8p0_L2p0_core_topgate) (origin 15010 -16001) (rot 90.0))
    (sref (cell nfet_01v8_W10p0_L1p0_nf2_core_topgate) (origin 4891 -13500) (rot -90.0))
    (sref (cell nfet_01v8_W4p0_L1p0_core_topgate) (origin 1991 -10600) (rot -90.0))
    (sref (cell nfet_01v8_W4p0_L1p0_core_topgate) (origin 6791 -10601) (rot -90.0))
    (sref (cell nfet_01v8_W10p0_L0p5_nf2_core_topgate) (origin 4890 -16500) (rot -90.0))
    (sref (cell nfet_01v8_W1p0_L2p0_core_topgate) (origin 11100 -9990) (rot 180.0))
    (sref (cell res_xhigh_po_W0p35_L2p6_core) (origin 3300 -9100) (rot -90.0))
    (sref (cell res_xhigh_po_W0p35_L2p6_core) (origin 10400 -8500) (rot -90.0))
    (rect (layer sky130:tap) 0 290 19105 710)
    (rect (layer sky130:nsdm) -125 165 19230 835)
    (rect (layer sky130:li1) 0 290 19105 710)
    (rect (layer sky130:met1) -490 290 19363 1210)
    (rect (layer sky130:tap) 0 -18065 18730 -17645)
    (rect (layer sky130:psdm) -125 -18190 18855 -17520)
    (rect (layer sky130:li1) 0 -18065 18730 -17645)
    (rect (layer sky130:met1) -500 -18565 19318 -17645)
    (rect (layer sky130:mcon) 80 415 250 585)
    (rect (layer sky130:mcon) 80 -17940 250 -17770)
    (rect (layer sky130:mcon) 760 415 930 585)
    (rect (layer sky130:mcon) 760 -17940 930 -17770)
    (rect (layer sky130:mcon) 1440 415 1610 585)
    (rect (layer sky130:mcon) 1440 -17940 1610 -17770)
    (rect (layer sky130:mcon) 2120 415 2290 585)
    (rect (layer sky130:mcon) 2120 -17940 2290 -17770)
    (rect (layer sky130:mcon) 2800 415 2970 585)
    (rect (layer sky130:mcon) 2800 -17940 2970 -17770)
    (rect (layer sky130:mcon) 3480 415 3650 585)
    (rect (layer sky130:mcon) 3480 -17940 3650 -17770)
    (rect (layer sky130:mcon) 4160 415 4330 585)
    (rect (layer sky130:mcon) 4160 -17940 4330 -17770)
    (rect (layer sky130:mcon) 4840 415 5010 585)
    (rect (layer sky130:mcon) 4840 -17940 5010 -17770)
    (rect (layer sky130:mcon) 5520 415 5690 585)
    (rect (layer sky130:mcon) 5520 -17940 5690 -17770)
    (rect (layer sky130:mcon) 6200 415 6370 585)
    (rect (layer sky130:mcon) 6200 -17940 6370 -17770)
    (rect (layer sky130:mcon) 6880 415 7050 585)
    (rect (layer sky130:mcon) 6880 -17940 7050 -17770)
    (rect (layer sky130:mcon) 7560 415 7730 585)
    (rect (layer sky130:mcon) 7560 -17940 7730 -17770)
    (rect (layer sky130:mcon) 8240 415 8410 585)
    (rect (layer sky130:mcon) 8240 -17940 8410 -17770)
    (rect (layer sky130:mcon) 8920 415 9090 585)
    (rect (layer sky130:mcon) 8920 -17940 9090 -17770)
    (rect (layer sky130:mcon) 9600 415 9770 585)
    (rect (layer sky130:mcon) 9600 -17940 9770 -17770)
    (rect (layer sky130:mcon) 10280 415 10450 585)
    (rect (layer sky130:mcon) 10280 -17940 10450 -17770)
    (rect (layer sky130:mcon) 10960 415 11130 585)
    (rect (layer sky130:mcon) 10960 -17940 11130 -17770)
    (rect (layer sky130:mcon) 11640 415 11810 585)
    (rect (layer sky130:mcon) 11640 -17940 11810 -17770)
    (rect (layer sky130:mcon) 12320 415 12490 585)
    (rect (layer sky130:mcon) 12320 -17940 12490 -17770)
    (rect (layer sky130:mcon) 13000 415 13170 585)
    (rect (layer sky130:mcon) 13000 -17940 13170 -17770)
    (rect (layer sky130:mcon) 13680 415 13850 585)
    (rect (layer sky130:mcon) 13680 -17940 13850 -17770)
    (rect (layer sky130:mcon) 14360 415 14530 585)
    (rect (layer sky130:mcon) 14360 -17940 14530 -17770)
    (rect (layer sky130:mcon) 15040 415 15210 585)
    (rect (layer sky130:mcon) 15040 -17940 15210 -17770)
    (rect (layer sky130:mcon) 15720 415 15890 585)
    (rect (layer sky130:mcon) 15720 -17940 15890 -17770)
    (rect (layer sky130:mcon) 16400 415 16570 585)
    (rect (layer sky130:mcon) 16400 -17940 16570 -17770)
    (rect (layer sky130:mcon) 17080 415 17250 585)
    (rect (layer sky130:mcon) 17080 -17940 17250 -17770)
    (rect (layer sky130:mcon) 17760 415 17930 585)
    (rect (layer sky130:mcon) 17760 -17940 17930 -17770)
    (rect (layer sky130:mcon) 18440 415 18610 585)
    (rect (layer sky130:mcon) 18440 -17940 18610 -17770)
    (poly (layer sky130:nwell)
      (points (19310 898) (-490 898) (-490 -7910) (14310 -7910) (14310 -10810) (19310 -10810)))
    (label (layer sky130:met1_label) (text "VDD") (origin 10100 1010)
      (kind port-name))
    (label (layer sky130:met1_label) (text "VDD") (origin 10100 1010))
    (label (layer sky130:met1_label) (text "VSS") (origin 10090 -18365)
      (kind port-name))
    (label (layer sky130:met1_label) (text "VSS") (origin 10090 -18365))
    (label (layer sky130:li1_label) (text "net_d1n") (origin 1836 -9955)
      (internal #t))
    (label (layer sky130:li1_label) (text "VOUT") (origin 4111 -10600))
    (label (layer sky130:li1_label) (text "VOUT") (origin 4111 -10600)
      (kind port-name))
    (label (layer sky130:li1_label) (text "net_tail_n") (origin 1836 -11245)
      (internal #t))
    (label (layer sky130:li1_label) (text "net_ota_n") (origin 6636 -9956)
      (internal #t))
    (label (layer sky130:li1_label) (text "VIN_P") (origin 8911 -10601))
    (label (layer sky130:li1_label) (text "VIN_P") (origin 8911 -10601)
      (kind port-name))
    (label (layer sky130:li1_label) (text "net_tail_n") (origin 6636 -11246)
      (internal #t))
    (label (layer sky130:li1_label) (text "net_d1p") (origin 15165 -13645)
      (internal #t))
    (label (layer sky130:li1_label) (text "net_d1p") (origin 10890 -12500)
      (internal #t))
    (label (layer sky130:li1_label) (text "net_ota_p") (origin 15165 -17146)
      (internal #t))
    (label (layer sky130:li1_label) (text "net_d1p") (origin 10890 -16001)
      (internal #t))
    (label (layer sky130:li1_label) (text "net_tail_n") (origin 4736 -12210)
      (internal #t))
    (label (layer sky130:li1_label) (text "VBIAS_N") (origin 10011 -14145))
    (label (layer sky130:li1_label) (text "VBIAS_N") (origin 10011 -14145)
      (kind port-name))
    (label (layer sky130:li1_label) (text "VSS") (origin 4736 -13500))
    (label (layer sky130:li1_label) (text "net_d1p") (origin 8208 -65)
      (internal #t))
    (label (layer sky130:li1_label) (text "VOUT") (origin 3888 -710))
    (label (layer sky130:li1_label) (text "net_tail_p") (origin 8208 -1355)
      (internal #t))
    (label (layer sky130:nwell_label) (text "VDD") (origin 7461 -5705))
    (label (layer sky130:li1_label) (text "net_ota_p") (origin 17565 -4012)
      (internal #t))
    (label (layer sky130:li1_label) (text "VIN_P") (origin 18210 -8332))
    (label (layer sky130:nwell_label) (text "VDD") (origin 17323 -8301))
    (label (layer sky130:li1_label) (text "net_d1n") (origin -135 -1912)
      (internal #t))
    (label (layer sky130:li1_label) (text "net_d1n") (origin 510 -4232)
      (internal #t))
    (label (layer sky130:li1_label) (text "VDD") (origin 1155 -1912))
    (label (layer sky130:nwell_label) (text "VDD") (origin 178 -6736))
    (label (layer sky130:li1_label) (text "net_ota_n") (origin 2165 -1912)
      (internal #t))
    (label (layer sky130:li1_label) (text "net_d1n") (origin 2810 -4232)
      (internal #t))
    (label (layer sky130:li1_label) (text "VDD") (origin 3455 -1912))
    (label (layer sky130:nwell_label) (text "VDD") (origin 2405 -6736))
    (label (layer sky130:li1_label) (text "net_tail_p") (origin 9208 -2330)
      (internal #t))
    (label (layer sky130:li1_label) (text "net_tail_p") (origin 9208 -4910)
      (internal #t))
    (label (layer sky130:li1_label) (text "VBIAS_P") (origin 3888 -2975))
    (label (layer sky130:li1_label) (text "VBIAS_P") (origin 3888 -2975)
      (kind port-name))
    (label (layer sky130:li1_label) (text "VBIAS_P") (origin 3888 -4265))
    (label (layer sky130:li1_label) (text "VBIAS_P") (origin 3888 -5555))
    (label (layer sky130:li1_label) (text "VBIAS_P") (origin 3888 -6845))
    (label (layer sky130:li1_label) (text "VDD") (origin 9208 -3620))
    (label (layer sky130:li1_label) (text "VDD") (origin 9208 -6200))
    (label (layer sky130:nwell_label) (text "VDD") (origin 8420 -3426))
    (label (layer sky130:li1_label) (text "VOUT") (origin 15020 -5012))
    (label (layer sky130:li1_label) (text "net_ota_n") (origin 15415 -10332)
      (internal #t))
    (label (layer sky130:li1_label) (text "net_ota_n") (origin 16205 -10332)
      (internal #t))
    (label (layer sky130:li1_label) (text "VDD") (origin 15810 -5012))
    (label (layer sky130:nwell_label) (text "VDD") (origin 5055 -5474))
    (label (layer sky130:li1_label) (text "VOUT") (origin 4735 -15710))
    (label (layer sky130:li1_label) (text "net_ota_p") (origin 10010 -16105)
      (internal #t))
    (label (layer sky130:li1_label) (text "net_ota_p") (origin 10010 -16895)
      (internal #t))
    (label (layer sky130:li1_label) (text "VSS") (origin 4735 -16500))
    (label (layer sky130:li1_label) (text "VOUT") (origin 12245 -9835))
    (label (layer sky130:li1_label) (text "VBIAS_N") (origin 11100 -10610))
    (label (layer sky130:li1_label) (text "VSS") (origin 9955 -9835))
    (label (layer sky130:li1_label) (text "VOUT") (origin 1113 -7055))
    (label (layer sky130:li1_label) (text "VBIAS_P") (origin 2683 -5910))
    (label (layer sky130:li1_label) (text "VDD") (origin 1113 -4765))
    (label (layer sky130:nwell_label) (text "VDD") (origin 927 -4172))
    (label (layer sky130:li1_label) (text "net_ota_n") (origin 6505 -9100)
      (internal #t))
    (label (layer sky130:li1_label) (text "cc_n") (origin 95 -9100))
    (label (layer sky130:li1_label) (text "cc_n") (origin 95 -9100)
      (kind port-name))
    (label (layer sky130:li1_label) (text "net_ota_p") (origin 13605 -8500)
      (internal #t))
    (label (layer sky130:li1_label) (text "cc_p") (origin 7195 -8500))
    (label (layer sky130:li1_label) (text "cc_p") (origin 7195 -8500)
      (kind port-name))
    (rect (layer sky130:li1) 9925 -16190 10540 -16020
      (props (wire-id 1)))
    (rect (layer sky130:li1) 10370 -17342 10540 -16020
      (props (wire-id 1)))
    (rect (layer sky130:li1) 10370 -17342 15250 -17172
      (props (wire-id 1)))
    (rect (layer sky130:li1) 15080 -17342 15250 -17061
      (props (wire-id 1)))
    (rect (layer sky130:li1) 10805 -16086 10975 -12415
      (props (wire-id 2)))
    (rect (layer sky130:li1) 10805 -13730 15250 -13560
      (props (wire-id 3)))
    (rect (layer sky130:li1) 11146 -11440 11316 -11159
      (props (wire-id 4)))
    (rect (layer sky130:li1) 11015 -11329 11316 -11159
      (props (wire-id 4)))
    (rect (layer sky130:li1) 11015 -11329 11185 -10525
      (props (wire-id 4)))
    (rect (layer sky130:li1) 1751 -12295 1921 -11160
      (props (wire-id 5)))
    (rect (layer sky130:li1) 6551 -12295 6721 -11161
      (props (wire-id 6)))
    (rect (layer sky130:li1) 425 -4317 2895 -4147
      (props (wire-id 7)))
    (rect (layer sky130:li1) 2598 -5995 3973 -5825
      (props (wire-id 8)))
    (rect (layer sky130:li1) 9123 -2415 9293 -1270
      (props (wire-id 9)))
    (rect (layer sky130:li1) 16120 -10417 16685 -10247
      (props (wire-id 11)))
    (rect (layer sky130:li1) 16515 -10417 16685 -4927
      (props (wire-id 11)))
    (rect (layer sky130:li1) 6551 -10041 6721 -9012
      (props (wire-id 12)))
    (rect (layer sky130:li1) 6373 -9182 6721 -9012
      (props (wire-id 12)))
    (rect (layer sky130:li1) -220 -4317 -50 -1827
      (props (wire-id 13)))
    (rect (layer sky130:li1) -220 -4317 595 -4147
      (props (wire-id 13)))
    (rect (layer sky130:li1) 14348 -10417 15500 -10247
      (props (wire-id 14)))
    (rect (layer sky130:li1) 14348 -10417 14518 -8974
      (props (wire-id 14)))
    (rect (layer sky130:li1) 6551 -9144 14518 -8974
      (props (wire-id 14)))
    (rect (layer sky130:li1) 6551 -10041 6721 -8974
      (props (wire-id 14)))
    (rect (layer sky130:met1) 10940 -10770 11260 -10450
      (props (wire-id 15)))
    (rect (layer sky130:met1) 9941 -10680 11170 -10540
      (props (wire-id 15)))
    (rect (layer sky130:met1) 9941 -14215 10081 -10540
      (props (wire-id 15)))
    (rect (layer sky130:mcon) 9926 -14230 10096 -14060
      (props (wire-id 15)))
    (rect (layer sky130:li1) -497 -4317 595 -4147
      (props (wire-id 16)))
    (rect (layer sky130:li1) -497 -10040 -327 -4147
      (props (wire-id 16)))
    (rect (layer sky130:li1) -497 -10040 1921 -9870
      (props (wire-id 16)))
    (rect (layer sky130:li1) 1028 -8754 1198 -6970
      (props (wire-id 17)))
    (rect (layer sky130:li1) 1028 -8754 4196 -8584
      (props (wire-id 17)))
    (rect (layer sky130:li1) 4026 -10685 4196 -8584
      (props (wire-id 17)))
    (rect (layer sky130:met1) 18140 -10727 18280 -8262
      (props (wire-id 18)))
    (rect (layer sky130:met1) 14294 -10727 18280 -10587
      (props (wire-id 18)))
    (rect (layer sky130:met1) 14294 -10727 14434 -8948
      (props (wire-id 18)))
    (rect (layer sky130:met1) 8841 -9088 14434 -8948
      (props (wire-id 18)))
    (rect (layer sky130:met1) 8841 -10671 8981 -8948
      (props (wire-id 18)))
    (rect (layer sky130:mcon) 18125 -8417 18295 -8247
      (props (wire-id 18)))
    (rect (layer sky130:mcon) 8826 -10686 8996 -10516
      (props (wire-id 18)))
    (rect (layer sky130:met2) 3703 -895 4073 -525
      (props (wire-id 19)))
    (rect (layer sky130:met2) 928 -7240 1298 -6870
      (props (wire-id 19)))
    (rect (layer sky130:met2) 1043 -780 3958 -640
      (props (wire-id 19)))
    (rect (layer sky130:met2) 1043 -7125 1183 -640
      (props (wire-id 19)))
    (rect (layer sky130:mcon) 3803 -795 3973 -625
      (props (wire-id 19)))
    (rect (layer sky130:via) 3813 -785 3963 -635
      (props (wire-id 19)))
    (rect (layer sky130:via) 1038 -7130 1188 -6980
      (props (wire-id 19)))
    (rect (layer sky130:met1) 953 -7215 1273 -6895
      (props (wire-id 19)))
    (rect (layer sky130:met2) 4550 -15895 4920 -15525
      (props (wire-id 20)))
    (rect (layer sky130:met2) 1043 -15780 1183 -6985
      (props (wire-id 20)))
    (rect (layer sky130:met2) 1043 -15780 4805 -15640
      (props (wire-id 20)))
    (rect (layer sky130:via) 4660 -15785 4810 -15635
      (props (wire-id 20)))
    (rect (layer sky130:mcon) 12160 -9920 12330 -9750
      (props (wire-id 21)))
    (rect (layer sky130:via) 12170 -9910 12320 -9760
      (props (wire-id 21)))
    (rect (layer sky130:mcon) 4026 -10685 4196 -10515
      (props (wire-id 21)))
    (rect (layer sky130:via) 4036 -10675 4186 -10525
      (props (wire-id 21)))
    (rect (layer sky130:met2) 14835 -5197 15205 -4827
      (props (wire-id 22)))
    (rect (layer sky130:met2) 12175 -9905 15090 -9765
      (props (wire-id 22)))
    (rect (layer sky130:met2) 14950 -9905 15090 -4942
      (props (wire-id 22)))
    (rect (layer sky130:met1) 12085 -9995 12405 -9675
      (props (wire-id 22)))
    (rect (layer sky130:via) 14945 -5087 15095 -4937
      (props (wire-id 22)))
    (rect (layer sky130:met2) 9023 -3805 9393 -3435
      (props (wire-id 23)))
    (rect (layer sky130:met2) 9251 565 9621 935
      (props (wire-id 23)))
    (rect (layer sky130:met2) 9138 -3690 9506 -3550
      (props (wire-id 23)))
    (rect (layer sky130:met2) 9366 -3690 9506 820
      (props (wire-id 23)))
    (rect (layer sky130:via) 9133 -3695 9283 -3545
      (props (wire-id 23)))
    (rect (layer sky130:via) 9361 675 9511 825
      (props (wire-id 23)))
    (rect (layer sky130:met1) 995 -2072 1315 -1752
      (props (wire-id 24)))
    (rect (layer sky130:met1) 953 -4925 1273 -4605
      (props (wire-id 24)))
    (rect (layer sky130:met1) 1085 -3974 1225 -1842
      (props (wire-id 24)))
    (rect (layer sky130:met1) 1085 -3974 1416 -3834
      (props (wire-id 24)))
    (rect (layer sky130:met1) 1276 -4835 1416 -3834
      (props (wire-id 24)))
    (rect (layer sky130:met1) 3295 -2072 3615 -1752
      (props (wire-id 24)))
    (rect (layer sky130:met1) 1043 -4786 3505 -4646
      (props (wire-id 24)))
    (rect (layer sky130:met1) 3365 -4786 3505 -4648
      (props (wire-id 24)))
    (rect (layer sky130:met1) 3365 -4788 3633 -4648
      (props (wire-id 24)))
    (rect (layer sky130:met1) 3493 -4788 3633 -1842
      (props (wire-id 24)))
    (rect (layer sky130:met1) 3385 -1982 3633 -1842
      (props (wire-id 24)))
    (rect (layer sky130:met1) 3385 -1982 3525 820
      (props (wire-id 24)))
    (rect (layer sky130:met2) 4551 -13685 4921 -13315
      (props (wire-id 25)))
    (rect (layer sky130:met2) 4550 -16685 4920 -16315
      (props (wire-id 25)))
    (rect (layer sky130:met2) 4666 -13570 5721 -13430
      (props (wire-id 25)))
    (rect (layer sky130:met2) 5581 -16570 5721 -13430
      (props (wire-id 25)))
    (rect (layer sky130:met2) 4665 -16570 5721 -16430
      (props (wire-id 25)))
    (rect (layer sky130:via) 4661 -13575 4811 -13425
      (props (wire-id 25)))
    (rect (layer sky130:via) 4660 -16575 4810 -16425
      (props (wire-id 25)))
    (rect (layer sky130:met2) 9224 -18290 9594 -17920
      (props (wire-id 26)))
    (rect (layer sky130:met2) 4665 -18175 4805 -16430
      (props (wire-id 26)))
    (rect (layer sky130:met2) 4665 -18175 9479 -18035
      (props (wire-id 26)))
    (rect (layer sky130:met1) 4575 -16660 4895 -16340
      (props (wire-id 26)))
    (rect (layer sky130:via) 9334 -18180 9484 -18030
      (props (wire-id 26)))
    (rect (layer sky130:met2) 9825 -16290 10195 -15920
      (props (wire-id 27)))
    (rect (layer sky130:met2) 17380 -4197 17750 -3827
      (props (wire-id 27)))
    (rect (layer sky130:met2) 9940 -16175 17635 -16035
      (props (wire-id 27)))
    (rect (layer sky130:met2) 17495 -16175 17635 -3942
      (props (wire-id 27)))
    (rect (layer sky130:via) 9935 -16180 10085 -16030
      (props (wire-id 27)))
    (rect (layer sky130:mcon) 17480 -4097 17650 -3927
      (props (wire-id 27)))
    (rect (layer sky130:via) 17490 -4087 17640 -3937
      (props (wire-id 27)))
    (rect (layer sky130:met2) 15230 -10517 15600 -10147
      (props (wire-id 28)))
    (rect (layer sky130:met2) 1980 -2097 2350 -1727
      (props (wire-id 28)))
    (rect (layer sky130:met2) 4701 -10402 15485 -10262
      (props (wire-id 28)))
    (rect (layer sky130:met2) 4701 -11756 4841 -10262
      (props (wire-id 28)))
    (rect (layer sky130:met2) 2095 -11756 4841 -11616
      (props (wire-id 28)))
    (rect (layer sky130:met2) 2095 -11756 2235 -1842
      (props (wire-id 28)))
    (rect (layer sky130:via) 15340 -10407 15490 -10257
      (props (wire-id 28)))
    (rect (layer sky130:via) 2090 -1987 2240 -1837
      (props (wire-id 28)))
    (rect (layer sky130:met2) 14980 -13830 15350 -13460
      (props (wire-id 29)))
    (rect (layer sky130:met2) 8023 -250 8393 120
      (props (wire-id 29)))
    (rect (layer sky130:met2) 15095 -13715 15235 -10658
      (props (wire-id 29)))
    (rect (layer sky130:met2) 15095 -10798 16454 -10658
      (props (wire-id 29)))
    (rect (layer sky130:met2) 16314 -10798 16454 -1736
      (props (wire-id 29)))
    (rect (layer sky130:met2) 10602 -1876 16454 -1736
      (props (wire-id 29)))
    (rect (layer sky130:met2) 10602 -4519 10742 -1736
      (props (wire-id 29)))
    (rect (layer sky130:met2) 8395 -4519 10742 -4379
      (props (wire-id 29)))
    (rect (layer sky130:met2) 8395 -4519 8535 5
      (props (wire-id 29)))
    (rect (layer sky130:met2) 8138 -135 8535 5
      (props (wire-id 29)))
    (rect (layer sky130:mcon) 15080 -13730 15250 -13560
      (props (wire-id 30)))
    (rect (layer sky130:via) 15090 -13720 15240 -13570
      (props (wire-id 30)))
    (rect (layer sky130:mcon) 8123 -150 8293 20
      (props (wire-id 30)))
    (rect (layer sky130:via) 8133 -140 8283 10
      (props (wire-id 30)))
    (rect (layer sky130:li1) 15725 -5097 15895 835
      (props (wire-id 31)))
    (rect (layer sky130:li1) 9351 665 15895 835
      (props (wire-id 31)))
    (rect (layer sky130:mcon) 9351 665 9521 835
      (props (wire-id 31)))
    (rect (layer sky130:li1) 9123 -3705 9293 -3564
      (props (wire-id 32)))
    (rect (layer sky130:li1) 9123 -3709 14718 -3539
      (props (wire-id 32)))
    (rect (layer sky130:li1) 14548 -3709 14718 835
      (props (wire-id 32)))
    (rect (layer sky130:li1) 9123 -3734 9293 -3539
      (props (wire-id 32)))
    (rect (layer sky130:met3) 17321 -4256 17809 -3768
      (props (wire-id 33)))
    (rect (layer sky130:via2) 17465 -4112 17665 -3912
      (props (wire-id 33)))
    (rect (layer sky130:met3) 12456 -8744 12944 -8256
      (props (wire-id 34)))
    (rect (layer sky130:met3) 12550 -8650 12850 -3862
      (props (wire-id 34)))
    (rect (layer sky130:met3) 12550 -4162 17715 -3862
      (props (wire-id 34)))
    (rect (layer sky130:met2) 12515 -8685 12885 -8315
      (props (wire-id 34)))
    (rect (layer sky130:mcon) 12615 -8585 12785 -8415
      (props (wire-id 34)))
    (rect (layer sky130:via) 12625 -8575 12775 -8425
      (props (wire-id 34)))
    (rect (layer sky130:via2) 12600 -8600 12800 -8400
      (props (wire-id 34)))
    (rect (layer sky130:met3) 9711 -10079 10199 -9591
      (props (wire-id 35)))
    (rect (layer sky130:met3) 9805 -18255 10105 -9685
      (props (wire-id 35)))
    (rect (layer sky130:met3) 9165 -18349 9653 -17861
      (props (wire-id 35)))
    (rect (layer sky130:met3) 9259 -18255 10105 -17955
      (props (wire-id 35)))
    (rect (layer sky130:met2) 9770 -10020 10140 -9650
      (props (wire-id 35)))
    (rect (layer sky130:mcon) 9870 -9920 10040 -9750
      (props (wire-id 35)))
    (rect (layer sky130:via) 9880 -9910 10030 -9760
      (props (wire-id 35)))
    (rect (layer sky130:via2) 9855 -9935 10055 -9735
      (props (wire-id 35)))
    (rect (layer sky130:via2) 9309 -18205 9509 -18005
      (props (wire-id 35)))
    (rect (layer sky130:met2) 12060 -10020 12430 -9650
      (props (wire-id 36)))
    (rect (layer sky130:met2) 3926 -10785 4296 -10415
      (props (wire-id 36)))
    (rect (layer sky130:met2) 11246 -9905 12315 -9765
      (props (wire-id 36)))
    (rect (layer sky130:met2) 11246 -9905 11386 -8413
      (props (wire-id 36)))
    (rect (layer sky130:met2) 4041 -8553 11386 -8413
      (props (wire-id 36)))
    (rect (layer sky130:met2) 4041 -10670 4181 -8413
      (props (wire-id 36)))
    (rect (layer sky130:met1) 3951 -10760 4271 -10440
      (props (wire-id 36)))))
