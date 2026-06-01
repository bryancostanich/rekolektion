(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (import "../primitives/nfet_01v8_W0p42_L0p15_core_topgate.rkt")
  (import "../primitives/pfet_01v8_W0p42_L0p15_core_botgate.rkt")
  (top tap_mux_pg_core)
  (cell tap_mux_pg_core
    (rect (layer sky130:nwell) 10 790 23600 1890)
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 555 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 2055 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 3555 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 5055 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 6555 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 8055 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 9555 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 11055 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 12555 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 14055 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 15555 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 17055 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 18555 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 20055 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 21555 0))
    (sref (cell nfet_01v8_W0p42_L0p15_core_topgate) (origin 23055 0))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 555 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 2055 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 3555 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 5055 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 6555 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 8055 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 9555 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 11055 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 12555 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 14055 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 15555 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 17055 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 18555 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 20055 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 21555 1320))
    (sref (cell pfet_01v8_W0p42_L0p15_core_botgate) (origin 23055 1320))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 335 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_0") (origin 555 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_0") (origin 775 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 335 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_0_b") (origin 555 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_0") (origin 775 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 1835 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_1") (origin 2055 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_1") (origin 2275 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 1835 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_1_b") (origin 2055 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_1") (origin 2275 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 3335 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_2") (origin 3555 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_2") (origin 3775 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 3335 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_2_b") (origin 3555 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_2") (origin 3775 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 4835 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_3") (origin 5055 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_3") (origin 5275 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 4835 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_3_b") (origin 5055 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_3") (origin 5275 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 6335 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_4") (origin 6555 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_4") (origin 6775 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 6335 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_4_b") (origin 6555 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_4") (origin 6775 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 7835 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_5") (origin 8055 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_5") (origin 8275 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 7835 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_5_b") (origin 8055 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_5") (origin 8275 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 9335 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_6") (origin 9555 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_6") (origin 9775 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 9335 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_6_b") (origin 9555 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_6") (origin 9775 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 10835 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_7") (origin 11055 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_7") (origin 11275 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 10835 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_7_b") (origin 11055 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_7") (origin 11275 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 12335 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_8") (origin 12555 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_8") (origin 12775 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 12335 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_8_b") (origin 12555 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_8") (origin 12775 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 13835 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_9") (origin 14055 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_9") (origin 14275 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 13835 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_9_b") (origin 14055 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_9") (origin 14275 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 15335 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_10") (origin 15555 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_10") (origin 15775 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 15335 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_10_b") (origin 15555 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_10") (origin 15775 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 16835 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_11") (origin 17055 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_11") (origin 17275 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 16835 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_11_b") (origin 17055 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_11") (origin 17275 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 18335 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_12") (origin 18555 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_12") (origin 18775 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 18335 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_12_b") (origin 18555 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_12") (origin 18775 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 19835 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_13") (origin 20055 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_13") (origin 20275 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 19835 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_13_b") (origin 20055 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_13") (origin 20275 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 21335 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_14") (origin 21555 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_14") (origin 21775 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 21335 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_14_b") (origin 21555 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_14") (origin 21775 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 22835 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_15") (origin 23055 330)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_15") (origin 23275 -155)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_OUT") (origin 22835 1500)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "dec_15_b") (origin 23055 970)
      (kind port-name))
    (label (layer sky130:li1_pin) (text "V_TAP_15") (origin 23275 1500)
      (kind port-name))
    (rect (layer sky130:met1) 265 1226 405 1570
      (props (wire-id 1)))
    (rect (layer sky130:met1) 130 1226 405 1366
      (props (wire-id 1)))
    (rect (layer sky130:met1) 130 -66 270 1366
      (props (wire-id 1)))
    (rect (layer sky130:met1) 130 -66 405 74
      (props (wire-id 1)))
    (rect (layer sky130:met1) 265 -225 405 74
      (props (wire-id 1)))
    (rect (layer sky130:met1) 705 1226 845 1570
      (props (wire-id 2)))
    (rect (layer sky130:met1) 705 1226 980 1366
      (props (wire-id 2)))
    (rect (layer sky130:met1) 840 -66 980 1366
      (props (wire-id 2)))
    (rect (layer sky130:met1) 705 -66 980 74
      (props (wire-id 2)))
    (rect (layer sky130:met1) 705 -225 845 74
      (props (wire-id 2)))
    (rect (layer sky130:met1) 1765 1226 1905 1570
      (props (wire-id 3)))
    (rect (layer sky130:met1) 1630 1226 1905 1366
      (props (wire-id 3)))
    (rect (layer sky130:met1) 1630 -66 1770 1366
      (props (wire-id 3)))
    (rect (layer sky130:met1) 1630 -66 1905 74
      (props (wire-id 3)))
    (rect (layer sky130:met1) 1765 -225 1905 74
      (props (wire-id 3)))
    (rect (layer sky130:met1) 2205 1226 2345 1570
      (props (wire-id 4)))
    (rect (layer sky130:met1) 2205 1226 2480 1366
      (props (wire-id 4)))
    (rect (layer sky130:met1) 2340 -66 2480 1366
      (props (wire-id 4)))
    (rect (layer sky130:met1) 2205 -66 2480 74
      (props (wire-id 4)))
    (rect (layer sky130:met1) 2205 -225 2345 74
      (props (wire-id 4)))
    (rect (layer sky130:met1) 3265 1226 3405 1570
      (props (wire-id 5)))
    (rect (layer sky130:met1) 3130 1226 3405 1366
      (props (wire-id 5)))
    (rect (layer sky130:met1) 3130 -66 3270 1366
      (props (wire-id 5)))
    (rect (layer sky130:met1) 3130 -66 3405 74
      (props (wire-id 5)))
    (rect (layer sky130:met1) 3265 -225 3405 74
      (props (wire-id 5)))
    (rect (layer sky130:met1) 3705 1226 3845 1570
      (props (wire-id 6)))
    (rect (layer sky130:met1) 3705 1226 3980 1366
      (props (wire-id 6)))
    (rect (layer sky130:met1) 3840 -66 3980 1366
      (props (wire-id 6)))
    (rect (layer sky130:met1) 3705 -66 3980 74
      (props (wire-id 6)))
    (rect (layer sky130:met1) 3705 -225 3845 74
      (props (wire-id 6)))
    (rect (layer sky130:met1) 4765 1226 4905 1570
      (props (wire-id 7)))
    (rect (layer sky130:met1) 4630 1226 4905 1366
      (props (wire-id 7)))
    (rect (layer sky130:met1) 4630 -66 4770 1366
      (props (wire-id 7)))
    (rect (layer sky130:met1) 4630 -66 4905 74
      (props (wire-id 7)))
    (rect (layer sky130:met1) 4765 -225 4905 74
      (props (wire-id 7)))
    (rect (layer sky130:met1) 5205 1226 5345 1570
      (props (wire-id 8)))
    (rect (layer sky130:met1) 5205 1226 5480 1366
      (props (wire-id 8)))
    (rect (layer sky130:met1) 5340 -66 5480 1366
      (props (wire-id 8)))
    (rect (layer sky130:met1) 5205 -66 5480 74
      (props (wire-id 8)))
    (rect (layer sky130:met1) 5205 -225 5345 74
      (props (wire-id 8)))
    (rect (layer sky130:met1) 6265 1226 6405 1570
      (props (wire-id 9)))
    (rect (layer sky130:met1) 6130 1226 6405 1366
      (props (wire-id 9)))
    (rect (layer sky130:met1) 6130 -66 6270 1366
      (props (wire-id 9)))
    (rect (layer sky130:met1) 6130 -66 6405 74
      (props (wire-id 9)))
    (rect (layer sky130:met1) 6265 -225 6405 74
      (props (wire-id 9)))
    (rect (layer sky130:met1) 6705 1226 6845 1570
      (props (wire-id 10)))
    (rect (layer sky130:met1) 6705 1226 6980 1366
      (props (wire-id 10)))
    (rect (layer sky130:met1) 6840 -66 6980 1366
      (props (wire-id 10)))
    (rect (layer sky130:met1) 6705 -66 6980 74
      (props (wire-id 10)))
    (rect (layer sky130:met1) 6705 -225 6845 74
      (props (wire-id 10)))
    (rect (layer sky130:met1) 7765 1226 7905 1570
      (props (wire-id 11)))
    (rect (layer sky130:met1) 7630 1226 7905 1366
      (props (wire-id 11)))
    (rect (layer sky130:met1) 7630 -66 7770 1366
      (props (wire-id 11)))
    (rect (layer sky130:met1) 7630 -66 7905 74
      (props (wire-id 11)))
    (rect (layer sky130:met1) 7765 -225 7905 74
      (props (wire-id 11)))
    (rect (layer sky130:met1) 8205 1226 8345 1570
      (props (wire-id 12)))
    (rect (layer sky130:met1) 8205 1226 8480 1366
      (props (wire-id 12)))
    (rect (layer sky130:met1) 8340 -66 8480 1366
      (props (wire-id 12)))
    (rect (layer sky130:met1) 8205 -66 8480 74
      (props (wire-id 12)))
    (rect (layer sky130:met1) 8205 -225 8345 74
      (props (wire-id 12)))
    (rect (layer sky130:met1) 9265 1226 9405 1570
      (props (wire-id 13)))
    (rect (layer sky130:met1) 9130 1226 9405 1366
      (props (wire-id 13)))
    (rect (layer sky130:met1) 9130 -66 9270 1366
      (props (wire-id 13)))
    (rect (layer sky130:met1) 9130 -66 9405 74
      (props (wire-id 13)))
    (rect (layer sky130:met1) 9265 -225 9405 74
      (props (wire-id 13)))
    (rect (layer sky130:met1) 9705 1226 9845 1570
      (props (wire-id 14)))
    (rect (layer sky130:met1) 9705 1226 9980 1366
      (props (wire-id 14)))
    (rect (layer sky130:met1) 9840 -66 9980 1366
      (props (wire-id 14)))
    (rect (layer sky130:met1) 9705 -66 9980 74
      (props (wire-id 14)))
    (rect (layer sky130:met1) 9705 -225 9845 74
      (props (wire-id 14)))
    (rect (layer sky130:met1) 10765 1226 10905 1570
      (props (wire-id 15)))
    (rect (layer sky130:met1) 10630 1226 10905 1366
      (props (wire-id 15)))
    (rect (layer sky130:met1) 10630 -66 10770 1366
      (props (wire-id 15)))
    (rect (layer sky130:met1) 10630 -66 10905 74
      (props (wire-id 15)))
    (rect (layer sky130:met1) 10765 -225 10905 74
      (props (wire-id 15)))
    (rect (layer sky130:met1) 11205 1226 11345 1570
      (props (wire-id 16)))
    (rect (layer sky130:met1) 11205 1226 11480 1366
      (props (wire-id 16)))
    (rect (layer sky130:met1) 11340 -66 11480 1366
      (props (wire-id 16)))
    (rect (layer sky130:met1) 11205 -66 11480 74
      (props (wire-id 16)))
    (rect (layer sky130:met1) 11205 -225 11345 74
      (props (wire-id 16)))
    (rect (layer sky130:met1) 12265 1226 12405 1570
      (props (wire-id 17)))
    (rect (layer sky130:met1) 12130 1226 12405 1366
      (props (wire-id 17)))
    (rect (layer sky130:met1) 12130 -66 12270 1366
      (props (wire-id 17)))
    (rect (layer sky130:met1) 12130 -66 12405 74
      (props (wire-id 17)))
    (rect (layer sky130:met1) 12265 -225 12405 74
      (props (wire-id 17)))
    (rect (layer sky130:met1) 12705 1226 12845 1570
      (props (wire-id 18)))
    (rect (layer sky130:met1) 12705 1226 12980 1366
      (props (wire-id 18)))
    (rect (layer sky130:met1) 12840 -66 12980 1366
      (props (wire-id 18)))
    (rect (layer sky130:met1) 12705 -66 12980 74
      (props (wire-id 18)))
    (rect (layer sky130:met1) 12705 -225 12845 74
      (props (wire-id 18)))
    (rect (layer sky130:met1) 13765 1226 13905 1570
      (props (wire-id 19)))
    (rect (layer sky130:met1) 13630 1226 13905 1366
      (props (wire-id 19)))
    (rect (layer sky130:met1) 13630 -66 13770 1366
      (props (wire-id 19)))
    (rect (layer sky130:met1) 13630 -66 13905 74
      (props (wire-id 19)))
    (rect (layer sky130:met1) 13765 -225 13905 74
      (props (wire-id 19)))
    (rect (layer sky130:met1) 14205 1226 14345 1570
      (props (wire-id 20)))
    (rect (layer sky130:met1) 14205 1226 14480 1366
      (props (wire-id 20)))
    (rect (layer sky130:met1) 14340 -66 14480 1366
      (props (wire-id 20)))
    (rect (layer sky130:met1) 14205 -66 14480 74
      (props (wire-id 20)))
    (rect (layer sky130:met1) 14205 -225 14345 74
      (props (wire-id 20)))
    (rect (layer sky130:met1) 15265 1226 15405 1570
      (props (wire-id 21)))
    (rect (layer sky130:met1) 15130 1226 15405 1366
      (props (wire-id 21)))
    (rect (layer sky130:met1) 15130 -66 15270 1366
      (props (wire-id 21)))
    (rect (layer sky130:met1) 15130 -66 15405 74
      (props (wire-id 21)))
    (rect (layer sky130:met1) 15265 -225 15405 74
      (props (wire-id 21)))
    (rect (layer sky130:met1) 15705 1226 15845 1570
      (props (wire-id 22)))
    (rect (layer sky130:met1) 15705 1226 15980 1366
      (props (wire-id 22)))
    (rect (layer sky130:met1) 15840 -66 15980 1366
      (props (wire-id 22)))
    (rect (layer sky130:met1) 15705 -66 15980 74
      (props (wire-id 22)))
    (rect (layer sky130:met1) 15705 -225 15845 74
      (props (wire-id 22)))
    (rect (layer sky130:met1) 16765 1226 16905 1570
      (props (wire-id 23)))
    (rect (layer sky130:met1) 16630 1226 16905 1366
      (props (wire-id 23)))
    (rect (layer sky130:met1) 16630 -66 16770 1366
      (props (wire-id 23)))
    (rect (layer sky130:met1) 16630 -66 16905 74
      (props (wire-id 23)))
    (rect (layer sky130:met1) 16765 -225 16905 74
      (props (wire-id 23)))
    (rect (layer sky130:met1) 17205 1226 17345 1570
      (props (wire-id 24)))
    (rect (layer sky130:met1) 17205 1226 17480 1366
      (props (wire-id 24)))
    (rect (layer sky130:met1) 17340 -66 17480 1366
      (props (wire-id 24)))
    (rect (layer sky130:met1) 17205 -66 17480 74
      (props (wire-id 24)))
    (rect (layer sky130:met1) 17205 -225 17345 74
      (props (wire-id 24)))
    (rect (layer sky130:met1) 18265 1226 18405 1570
      (props (wire-id 25)))
    (rect (layer sky130:met1) 18130 1226 18405 1366
      (props (wire-id 25)))
    (rect (layer sky130:met1) 18130 -66 18270 1366
      (props (wire-id 25)))
    (rect (layer sky130:met1) 18130 -66 18405 74
      (props (wire-id 25)))
    (rect (layer sky130:met1) 18265 -225 18405 74
      (props (wire-id 25)))
    (rect (layer sky130:met1) 18705 1226 18845 1570
      (props (wire-id 26)))
    (rect (layer sky130:met1) 18705 1226 18980 1366
      (props (wire-id 26)))
    (rect (layer sky130:met1) 18840 -66 18980 1366
      (props (wire-id 26)))
    (rect (layer sky130:met1) 18705 -66 18980 74
      (props (wire-id 26)))
    (rect (layer sky130:met1) 18705 -225 18845 74
      (props (wire-id 26)))
    (rect (layer sky130:met1) 19765 1226 19905 1570
      (props (wire-id 27)))
    (rect (layer sky130:met1) 19630 1226 19905 1366
      (props (wire-id 27)))
    (rect (layer sky130:met1) 19630 -66 19770 1366
      (props (wire-id 27)))
    (rect (layer sky130:met1) 19630 -66 19905 74
      (props (wire-id 27)))
    (rect (layer sky130:met1) 19765 -225 19905 74
      (props (wire-id 27)))
    (rect (layer sky130:met1) 20205 1226 20345 1570
      (props (wire-id 28)))
    (rect (layer sky130:met1) 20205 1226 20480 1366
      (props (wire-id 28)))
    (rect (layer sky130:met1) 20340 -66 20480 1366
      (props (wire-id 28)))
    (rect (layer sky130:met1) 20205 -66 20480 74
      (props (wire-id 28)))
    (rect (layer sky130:met1) 20205 -225 20345 74
      (props (wire-id 28)))
    (rect (layer sky130:met1) 21265 1226 21405 1570
      (props (wire-id 29)))
    (rect (layer sky130:met1) 21130 1226 21405 1366
      (props (wire-id 29)))
    (rect (layer sky130:met1) 21130 -66 21270 1366
      (props (wire-id 29)))
    (rect (layer sky130:met1) 21130 -66 21405 74
      (props (wire-id 29)))
    (rect (layer sky130:met1) 21265 -225 21405 74
      (props (wire-id 29)))
    (rect (layer sky130:met1) 21705 1226 21845 1570
      (props (wire-id 30)))
    (rect (layer sky130:met1) 21705 1226 21980 1366
      (props (wire-id 30)))
    (rect (layer sky130:met1) 21840 -66 21980 1366
      (props (wire-id 30)))
    (rect (layer sky130:met1) 21705 -66 21980 74
      (props (wire-id 30)))
    (rect (layer sky130:met1) 21705 -225 21845 74
      (props (wire-id 30)))
    (rect (layer sky130:met1) 22765 1226 22905 1570
      (props (wire-id 31)))
    (rect (layer sky130:met1) 22630 1226 22905 1366
      (props (wire-id 31)))
    (rect (layer sky130:met1) 22630 -66 22770 1366
      (props (wire-id 31)))
    (rect (layer sky130:met1) 22630 -66 22905 74
      (props (wire-id 31)))
    (rect (layer sky130:met1) 22765 -225 22905 74
      (props (wire-id 31)))
    (rect (layer sky130:met1) 23205 1226 23345 1570
      (props (wire-id 32)))
    (rect (layer sky130:met1) 23205 1226 23480 1366
      (props (wire-id 32)))
    (rect (layer sky130:met1) 23340 -66 23480 1366
      (props (wire-id 32)))
    (rect (layer sky130:met1) 23205 -66 23480 74
      (props (wire-id 32)))
    (rect (layer sky130:met1) 23205 -225 23345 74
      (props (wire-id 32)))
    (rect (layer sky130:met2) 153 581 22905 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 153 -225 293 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 153 -225 405 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 260 -230 410 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 1653 -225 1793 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 1653 -225 1905 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 1760 -230 1910 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 3153 -225 3293 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 3153 -225 3405 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 3260 -230 3410 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 4653 -225 4793 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 4653 -225 4905 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 4760 -230 4910 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 6153 -225 6293 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 6153 -225 6405 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 6260 -230 6410 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 7653 -225 7793 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 7653 -225 7905 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 7760 -230 7910 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 9153 -225 9293 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 9153 -225 9405 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 9260 -230 9410 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 10653 -225 10793 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 10653 -225 10905 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 10760 -230 10910 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 12153 -225 12293 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 12153 -225 12405 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 12260 -230 12410 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 13653 -225 13793 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 13653 -225 13905 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 13760 -230 13910 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 15153 -225 15293 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 15153 -225 15405 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 15260 -230 15410 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 16653 -225 16793 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 16653 -225 16905 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 16760 -230 16910 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 18153 -225 18293 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 18153 -225 18405 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 18260 -230 18410 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 19653 -225 19793 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 19653 -225 19905 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 19760 -230 19910 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 21153 -225 21293 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 21153 -225 21405 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 21260 -230 21410 -80
      (props (wire-id 33)))
    (rect (layer sky130:met2) 22653 -225 22793 721
      (props (wire-id 33)))
    (rect (layer sky130:met2) 22653 -225 22905 -85
      (props (wire-id 33)))
    (rect (layer sky130:via) 22760 -230 22910 -80
      (props (wire-id 33)))))
