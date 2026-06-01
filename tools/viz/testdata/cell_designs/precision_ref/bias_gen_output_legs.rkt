(layout (version 1)
  (pdk sky130)
  (units (dbu_nm 1) (uu_um 1))
  (import "../primitives/pfet_01v8_W2p99_L2p0_core_botgate.rkt")
  (import "../primitives/pfet_01v8_W2p21_L2p0_core_botgate.rkt")
  (import "../primitives/pfet_01v8_W14p2_L2p0_core_botgate.rkt")
  (import "../primitives/pfet_01v8_W0p42_L3p0_core_botgate.rkt")
  (import "../primitives/pfet_01v8_W1p5_L2p0_core_botgate.rkt")
  (import "../primitives/pfet_01v8_W5p0_L2p0_core_botgate.rkt")
  (import "../primitives/pfet_01v8_W12p5_L2p0_nf2_core_botgate.rkt")
  (import "../primitives/nfet_01v8_W4p0_L1p0_core_topgate.rkt")
  (import "../primitives/nfet_01v8_W1p0_L1p0_core_topgate.rkt")
  (import "../primitives/nfet_01v8_W8p0_L1p0_core_topgate.rkt")
  (import "../primitives/nfet_01v8_W2p0_L1p0_core_topgate.rkt")
  (top bias_gen_output_legs)
  (cell bias_gen_output_legs
    (poly (layer sky130:nwell)
      (points (-354 -420) (20396 -420) (20396 -14986) (14746 -14986) (14746 -13206) (11696 -13206) (11696 -7730) (4532 -7730) (4532 -7706) (-354 -7706)))
    (sref (cell pfet_01v8_W2p99_L2p0_core_botgate) (origin 6679 -6008) (rot 90.0))
    (sref (cell pfet_01v8_W2p21_L2p0_core_botgate) (origin 10278 -6002) (rot 90.0))
    (sref (cell pfet_01v8_W14p2_L2p0_core_botgate) (origin 7379 -2806) (rot 90.0))
    (sref (cell pfet_01v8_W0p42_L3p0_core_botgate) (origin 2496 -5189))
    (sref (cell pfet_01v8_W1p5_L2p0_core_botgate) (origin 13396 -11789))
    (sref (cell pfet_01v8_W5p0_L2p0_core_botgate) (origin 13396 -7223) (rot 180.0))
    (sref (cell pfet_01v8_W12p5_L2p0_nf2_core_botgate) (origin 17596 -8088))
    (sref (cell nfet_01v8_W4p0_L1p0_core_topgate) (origin 6287 -11506) (rot -90.0))
    (sref (cell nfet_01v8_W1p0_L1p0_core_topgate) (origin 1906 -9206) (rot 90.0))
    (sref (cell nfet_01v8_W8p0_L1p0_core_topgate) (origin 7386 -9206) (rot -90.0))
    (sref (cell nfet_01v8_W2p0_L1p0_core_topgate) (origin 10086 -11507) (rot -90.0))
    (rect (layer sky130:tap) -154 -1026 20196 -606)
    (rect (layer sky130:nsdm) -279 -1151 20321 -481)
    (rect (layer sky130:licon1) -94 -901 76 -731)
    (rect (layer sky130:licon1) 246 -901 416 -731)
    (rect (layer sky130:licon1) 586 -901 756 -731)
    (rect (layer sky130:licon1) 926 -901 1096 -731)
    (rect (layer sky130:licon1) 1266 -901 1436 -731)
    (rect (layer sky130:licon1) 1606 -901 1776 -731)
    (rect (layer sky130:licon1) 1946 -901 2116 -731)
    (rect (layer sky130:licon1) 2286 -901 2456 -731)
    (rect (layer sky130:licon1) 2626 -901 2796 -731)
    (rect (layer sky130:licon1) 2966 -901 3136 -731)
    (rect (layer sky130:licon1) 3306 -901 3476 -731)
    (rect (layer sky130:licon1) 3646 -901 3816 -731)
    (rect (layer sky130:licon1) 3986 -901 4156 -731)
    (rect (layer sky130:licon1) 4326 -901 4496 -731)
    (rect (layer sky130:licon1) 4666 -901 4836 -731)
    (rect (layer sky130:licon1) 5006 -901 5176 -731)
    (rect (layer sky130:licon1) 5346 -901 5516 -731)
    (rect (layer sky130:licon1) 5686 -901 5856 -731)
    (rect (layer sky130:licon1) 6026 -901 6196 -731)
    (rect (layer sky130:licon1) 6366 -901 6536 -731)
    (rect (layer sky130:licon1) 6706 -901 6876 -731)
    (rect (layer sky130:licon1) 7046 -901 7216 -731)
    (rect (layer sky130:licon1) 7386 -901 7556 -731)
    (rect (layer sky130:licon1) 7726 -901 7896 -731)
    (rect (layer sky130:licon1) 8066 -901 8236 -731)
    (rect (layer sky130:licon1) 8406 -901 8576 -731)
    (rect (layer sky130:licon1) 8746 -901 8916 -731)
    (rect (layer sky130:licon1) 9086 -901 9256 -731)
    (rect (layer sky130:licon1) 9426 -901 9596 -731)
    (rect (layer sky130:licon1) 9766 -901 9936 -731)
    (rect (layer sky130:licon1) 10106 -901 10276 -731)
    (rect (layer sky130:licon1) 10446 -901 10616 -731)
    (rect (layer sky130:licon1) 10786 -901 10956 -731)
    (rect (layer sky130:licon1) 11126 -901 11296 -731)
    (rect (layer sky130:licon1) 11466 -901 11636 -731)
    (rect (layer sky130:licon1) 11806 -901 11976 -731)
    (rect (layer sky130:licon1) 12146 -901 12316 -731)
    (rect (layer sky130:licon1) 12486 -901 12656 -731)
    (rect (layer sky130:licon1) 12826 -901 12996 -731)
    (rect (layer sky130:licon1) 13166 -901 13336 -731)
    (rect (layer sky130:licon1) 13506 -901 13676 -731)
    (rect (layer sky130:licon1) 13846 -901 14016 -731)
    (rect (layer sky130:licon1) 14186 -901 14356 -731)
    (rect (layer sky130:licon1) 14526 -901 14696 -731)
    (rect (layer sky130:licon1) 14866 -901 15036 -731)
    (rect (layer sky130:licon1) 15206 -901 15376 -731)
    (rect (layer sky130:licon1) 15546 -901 15716 -731)
    (rect (layer sky130:licon1) 15886 -901 16056 -731)
    (rect (layer sky130:licon1) 16226 -901 16396 -731)
    (rect (layer sky130:licon1) 16566 -901 16736 -731)
    (rect (layer sky130:licon1) 16906 -901 17076 -731)
    (rect (layer sky130:licon1) 17246 -901 17416 -731)
    (rect (layer sky130:licon1) 17586 -901 17756 -731)
    (rect (layer sky130:licon1) 17926 -901 18096 -731)
    (rect (layer sky130:licon1) 18266 -901 18436 -731)
    (rect (layer sky130:licon1) 18606 -901 18776 -731)
    (rect (layer sky130:licon1) 18946 -901 19116 -731)
    (rect (layer sky130:licon1) 19286 -901 19456 -731)
    (rect (layer sky130:licon1) 19626 -901 19796 -731)
    (rect (layer sky130:licon1) 19966 -901 20136 -731)
    (rect (layer sky130:li1) -154 -981 20196 -651)
    (rect (layer sky130:met1) -154 -1181 20196 -451)
    (rect (layer sky130:mcon) -124 -951 46 -781)
    (rect (layer sky130:mcon) 236 -951 406 -781)
    (rect (layer sky130:mcon) 596 -951 766 -781)
    (rect (layer sky130:mcon) 956 -951 1126 -781)
    (rect (layer sky130:mcon) 1316 -951 1486 -781)
    (rect (layer sky130:mcon) 1676 -951 1846 -781)
    (rect (layer sky130:mcon) 2036 -951 2206 -781)
    (rect (layer sky130:mcon) 2396 -951 2566 -781)
    (rect (layer sky130:mcon) 2756 -951 2926 -781)
    (rect (layer sky130:mcon) 3116 -951 3286 -781)
    (rect (layer sky130:mcon) 3476 -951 3646 -781)
    (rect (layer sky130:mcon) 3836 -951 4006 -781)
    (rect (layer sky130:mcon) 4196 -951 4366 -781)
    (rect (layer sky130:mcon) 4556 -951 4726 -781)
    (rect (layer sky130:mcon) 4916 -951 5086 -781)
    (rect (layer sky130:mcon) 5276 -951 5446 -781)
    (rect (layer sky130:mcon) 5636 -951 5806 -781)
    (rect (layer sky130:mcon) 5996 -951 6166 -781)
    (rect (layer sky130:mcon) 6356 -951 6526 -781)
    (rect (layer sky130:mcon) 6716 -951 6886 -781)
    (rect (layer sky130:mcon) 7076 -951 7246 -781)
    (rect (layer sky130:mcon) 7436 -951 7606 -781)
    (rect (layer sky130:mcon) 7796 -951 7966 -781)
    (rect (layer sky130:mcon) 8156 -951 8326 -781)
    (rect (layer sky130:mcon) 8516 -951 8686 -781)
    (rect (layer sky130:mcon) 8876 -951 9046 -781)
    (rect (layer sky130:mcon) 9236 -951 9406 -781)
    (rect (layer sky130:mcon) 9596 -951 9766 -781)
    (rect (layer sky130:mcon) 9956 -951 10126 -781)
    (rect (layer sky130:mcon) 10316 -951 10486 -781)
    (rect (layer sky130:mcon) 10676 -951 10846 -781)
    (rect (layer sky130:mcon) 11036 -951 11206 -781)
    (rect (layer sky130:mcon) 11396 -951 11566 -781)
    (rect (layer sky130:mcon) 11756 -951 11926 -781)
    (rect (layer sky130:mcon) 12116 -951 12286 -781)
    (rect (layer sky130:mcon) 12476 -951 12646 -781)
    (rect (layer sky130:mcon) 12836 -951 13006 -781)
    (rect (layer sky130:mcon) 13196 -951 13366 -781)
    (rect (layer sky130:mcon) 13556 -951 13726 -781)
    (rect (layer sky130:mcon) 13916 -951 14086 -781)
    (rect (layer sky130:mcon) 14276 -951 14446 -781)
    (rect (layer sky130:mcon) 14636 -951 14806 -781)
    (rect (layer sky130:mcon) 14996 -951 15166 -781)
    (rect (layer sky130:mcon) 15356 -951 15526 -781)
    (rect (layer sky130:mcon) 15716 -951 15886 -781)
    (rect (layer sky130:mcon) 16076 -951 16246 -781)
    (rect (layer sky130:mcon) 16436 -951 16606 -781)
    (rect (layer sky130:mcon) 16796 -951 16966 -781)
    (rect (layer sky130:mcon) 17156 -951 17326 -781)
    (rect (layer sky130:mcon) 17516 -951 17686 -781)
    (rect (layer sky130:mcon) 17876 -951 18046 -781)
    (rect (layer sky130:mcon) 18236 -951 18406 -781)
    (rect (layer sky130:mcon) 18596 -951 18766 -781)
    (rect (layer sky130:mcon) 18956 -951 19126 -781)
    (rect (layer sky130:mcon) 19316 -951 19486 -781)
    (rect (layer sky130:mcon) 19676 -951 19846 -781)
    (rect (layer sky130:tap) 4862 -13152 11011 -12732)
    (rect (layer sky130:psdm) 4737 -13277 11136 -12607)
    (rect (layer sky130:licon1) 4922 -13027 5092 -12857)
    (rect (layer sky130:licon1) 5262 -13027 5432 -12857)
    (rect (layer sky130:licon1) 5602 -13027 5772 -12857)
    (rect (layer sky130:licon1) 5942 -13027 6112 -12857)
    (rect (layer sky130:licon1) 6282 -13027 6452 -12857)
    (rect (layer sky130:licon1) 6622 -13027 6792 -12857)
    (rect (layer sky130:licon1) 6962 -13027 7132 -12857)
    (rect (layer sky130:licon1) 7302 -13027 7472 -12857)
    (rect (layer sky130:licon1) 7642 -13027 7812 -12857)
    (rect (layer sky130:licon1) 7982 -13027 8152 -12857)
    (rect (layer sky130:licon1) 8322 -13027 8492 -12857)
    (rect (layer sky130:licon1) 8662 -13027 8832 -12857)
    (rect (layer sky130:licon1) 9002 -13027 9172 -12857)
    (rect (layer sky130:licon1) 9342 -13027 9512 -12857)
    (rect (layer sky130:licon1) 9682 -13027 9852 -12857)
    (rect (layer sky130:licon1) 10022 -13027 10192 -12857)
    (rect (layer sky130:licon1) 10362 -13027 10532 -12857)
    (rect (layer sky130:licon1) 10702 -13027 10872 -12857)
    (rect (layer sky130:li1) 4862 -13107 11011 -12777)
    (rect (layer sky130:met1) 4862 -13307 11011 -12577)
    (rect (layer sky130:mcon) 4892 -13077 5062 -12907)
    (rect (layer sky130:mcon) 5252 -13077 5422 -12907)
    (rect (layer sky130:mcon) 5612 -13077 5782 -12907)
    (rect (layer sky130:mcon) 5972 -13077 6142 -12907)
    (rect (layer sky130:mcon) 6332 -13077 6502 -12907)
    (rect (layer sky130:mcon) 6692 -13077 6862 -12907)
    (rect (layer sky130:mcon) 7052 -13077 7222 -12907)
    (rect (layer sky130:mcon) 7412 -13077 7582 -12907)
    (rect (layer sky130:mcon) 7772 -13077 7942 -12907)
    (rect (layer sky130:mcon) 8132 -13077 8302 -12907)
    (rect (layer sky130:mcon) 8492 -13077 8662 -12907)
    (rect (layer sky130:mcon) 8852 -13077 9022 -12907)
    (rect (layer sky130:mcon) 9212 -13077 9382 -12907)
    (rect (layer sky130:mcon) 9572 -13077 9742 -12907)
    (rect (layer sky130:mcon) 9932 -13077 10102 -12907)
    (rect (layer sky130:mcon) 10292 -13077 10462 -12907)
    (rect (layer sky130:mcon) 10652 -13077 10822 -12907)
    (label (layer sky130:met1_label) (text "VDD") (origin 10021 -816)
      (kind port-name))
    (label (layer sky130:met1_label) (text "GND") (origin 7936 -12942)
      (kind port-name))
    (rect (layer sky130:li1) 7114 -1746 10085 -1576
      (props (wire-id 1)))
    (rect (layer sky130:li1) 9915 -1746 10085 -811
      (props (wire-id 1)))
    (rect (layer sky130:met1) 9855 -1041 10145 -751
      (props (wire-id 1)))
    (rect (layer sky130:li1) 17511 -7993 17681 -811
      (props (wire-id 2)))
    (rect (layer sky130:li1) 9915 -981 17681 -811
      (props (wire-id 2)))
    (rect (layer sky130:li1) 8229 -6093 11608 -5923
      (props (wire-id 3)))
    (rect (layer sky130:li1) 11438 -6093 11608 -5917
      (props (wire-id 3)))
    (rect (layer sky130:li1) 11438 -6087 11608 -4498
      (props (wire-id 4)))
    (rect (layer sky130:li1) 11438 -4668 13481 -4498
      (props (wire-id 4)))
    (rect (layer sky130:li1) 13311 -4668 14704 -4498
      (props (wire-id 5)))
    (rect (layer sky130:li1) 14534 -4668 14704 -2721
      (props (wire-id 5)))
    (rect (layer sky130:li1) 7146 -8646 11591 -8476
      (props (wire-id 6)))
    (rect (layer sky130:li1) 11421 -9291 11591 -8476
      (props (wire-id 6)))
    (rect (layer sky130:li1) 1201 -9936 1371 -9121
      (props (wire-id 7)))
    (rect (layer sky130:li1) 1201 -9936 2146 -9766
      (props (wire-id 7)))
    (rect (layer sky130:li1) 6047 -10946 8492 -10776
      (props (wire-id 8)))
    (rect (layer sky130:li1) 8322 -11591 8492 -10776
      (props (wire-id 8)))
    (rect (layer sky130:li1) 9846 -12685 10016 -12067
      (props (wire-id 9)))
    (rect (layer sky130:li1) 7611 -12685 10016 -12515
      (props (wire-id 9)))
    (rect (layer sky130:met1) 7551 -12745 7841 -12455
      (props (wire-id 9)))
    (rect (layer sky130:mcon) 7611 -12685 7781 -12515
      (props (wire-id 9)))
    (rect (layer sky130:li1) 6047 -12236 7781 -12066
      (props (wire-id 10)))
    (rect (layer sky130:li1) 7611 -12685 7781 -12066
      (props (wire-id 10)))
    (rect (layer sky130:li1) 13311 -14563 13481 -12594
      (props (wire-id 11)))
    (rect (layer sky130:li1) 13311 -14563 16536 -14393
      (props (wire-id 11)))
    (rect (layer sky130:li1) 16366 -14563 18826 -14393
      (props (wire-id 12)))
    (rect (layer sky130:li1) 11121 -11592 11292 -11422
      (props (wire-id 13)))
    (rect (layer sky130:li1) 11122 -11592 11292 -10272
      (props (wire-id 13)))
    (rect (layer sky130:li1) 11122 -10442 14626 -10272
      (props (wire-id 13)))
    (rect (layer sky130:li1) 14456 -10442 14626 -7318
      (props (wire-id 13)))
    (rect (layer sky130:li1) 9846 -10947 11291 -10777
      (props (wire-id 14)))
    (rect (layer sky130:li1) 11121 -11592 11291 -10777
      (props (wire-id 14)))
    (rect (layer sky130:li1) 14456 -7488 15391 -7318
      (props (wire-id 15)))
    (rect (layer sky130:li1) 15221 -7993 15391 -7318
      (props (wire-id 15)))
    (rect (layer sky130:li1) 1201 -9291 1371 -7702
      (props (wire-id 16)))
    (rect (layer sky130:li1) 1201 -7872 9572 -7702
      (props (wire-id 16)))
    (rect (layer sky130:li1) 9402 -7872 10183 -7702
      (props (wire-id 16)))
    (rect (layer sky130:li1) 10013 -7872 10183 -7062
      (props (wire-id 16)))
    (rect (layer sky130:li1) 766 -5094 936 -3866
      (props (wire-id 17)))
    (rect (layer sky130:li1) 766 -4036 7284 -3866
      (props (wire-id 17)))
    (rect (layer sky130:li1) 4056 -4948 6584 -4778
      (props (wire-id 18)))
    (rect (layer sky130:li1) 4056 -5094 4226 -4778
      (props (wire-id 18)))
    (rect (layer sky130:met1) 11346 -9366 11666 -9046
      (props (wire-id 19)))
    (rect (layer sky130:met1) 12091 -11769 12411 -11449
      (props (wire-id 19)))
    (rect (layer sky130:met1) 11436 -10471 11576 -9136
      (props (wire-id 19)))
    (rect (layer sky130:met1) 11436 -10471 12321 -10331
      (props (wire-id 19)))
    (rect (layer sky130:met1) 12181 -11679 12321 -10331
      (props (wire-id 19)))
    (rect (layer sky130:mcon) 11421 -9291 11591 -9121
      (props (wire-id 19)))
    (rect (layer sky130:mcon) 12166 -11694 12336 -11524
      (props (wire-id 19)))
    (rect (layer sky130:met1) 7039 -4111 7359 -3791
      (props (wire-id 20)))
    (rect (layer sky130:met1) 7129 -4021 8822 -3881
      (props (wire-id 20)))
    (rect (layer sky130:met1) 8682 -4148 8822 -3881
      (props (wire-id 20)))
    (rect (layer sky130:met1) 8682 -4148 8821 -4008
      (props (wire-id 20)))
    (rect (layer sky130:met1) 8681 -7913 8821 -4008
      (props (wire-id 20)))
    (rect (layer sky130:met1) 8681 -7913 11576 -7773
      (props (wire-id 20)))
    (rect (layer sky130:met1) 11436 -9276 11576 -7773
      (props (wire-id 20)))
    (rect (layer sky130:met2) 5947 -11046 6317 -10676
      (props (wire-id 21)))
    (rect (layer sky130:met2) 6314 -7338 6684 -6968
      (props (wire-id 21)))
    (rect (layer sky130:met2) 6062 -10931 6202 -7083
      (props (wire-id 21)))
    (rect (layer sky130:met2) 6062 -7223 6569 -7083
      (props (wire-id 21)))
    (rect (layer sky130:via) 6057 -10936 6207 -10786
      (props (wire-id 21)))
    (rect (layer sky130:met1) 5972 -11021 6292 -10701
      (props (wire-id 21)))
    (rect (layer sky130:via) 6424 -7228 6574 -7078
      (props (wire-id 21)))
    (rect (layer sky130:met1) 6339 -7313 6659 -6993
      (props (wire-id 21)))
    (rect (layer sky130:mcon) 7146 -9936 7316 -9766
      (props (wire-id 22)))
    (rect (layer sky130:via) 7156 -9926 7306 -9776
      (props (wire-id 22)))
    (rect (layer sky130:met1) 7071 -10011 7391 -9691
      (props (wire-id 22)))
    (rect (layer sky130:met1) 7536 -12960 7856 -12640
      (props (wire-id 22)))
    (rect (layer sky130:via) 7621 -12875 7771 -12725
      (props (wire-id 22)))
    (rect (layer sky130:mcon) 1976 -8646 2146 -8476
      (props (wire-id 23)))
    (rect (layer sky130:via) 1986 -8636 2136 -8486
      (props (wire-id 23)))
    (rect (layer sky130:met1) 1901 -8721 2221 -8401
      (props (wire-id 23)))
    (rect (layer sky130:met2) 14356 -11794 14726 -11424
      (props (wire-id 24)))
    (rect (layer sky130:met2) 12066 -7588 12436 -7218
      (props (wire-id 24)))
    (rect (layer sky130:met2) 14471 -11679 14611 -7333
      (props (wire-id 24)))
    (rect (layer sky130:met2) 12181 -7473 14611 -7333
      (props (wire-id 24)))
    (rect (layer sky130:mcon) 14456 -11694 14626 -11524
      (props (wire-id 24)))
    (rect (layer sky130:via) 14466 -11684 14616 -11534
      (props (wire-id 24)))
    (rect (layer sky130:met1) 14381 -11769 14701 -11449
      (props (wire-id 24)))
    (rect (layer sky130:via) 12176 -7478 12326 -7328
      (props (wire-id 24)))
    (rect (layer sky130:met1) 12091 -7563 12411 -7243
      (props (wire-id 24)))
    (rect (layer sky130:met2) 9815 -1081 10185 -711
      (props (wire-id 25)))
    (rect (layer sky130:met2) 12181 -7473 12321 -826
      (props (wire-id 25)))
    (rect (layer sky130:met2) 9930 -966 12321 -826
      (props (wire-id 25)))
    (rect (layer sky130:met1) 9840 -1056 10160 -736
      (props (wire-id 25)))
    (rect (layer sky130:via) 9925 -971 10075 -821
      (props (wire-id 25)))
    (rect (layer sky130:met2) 9913 -5042 10283 -4672
      (props (wire-id 26)))
    (rect (layer sky130:met2) 10028 -4927 10168 -826
      (props (wire-id 26)))
    (rect (layer sky130:met2) 9930 -966 10168 -826
      (props (wire-id 26)))
    (rect (layer sky130:via) 10023 -4932 10173 -4782
      (props (wire-id 26)))
    (rect (layer sky130:met1) 9938 -5017 10258 -4697
      (props (wire-id 26)))
    (rect (layer sky130:met2) 3956 -5194 4326 -4824
      (props (wire-id 27)))
    (rect (layer sky130:met2) 4071 -5079 4211 -826
      (props (wire-id 27)))
    (rect (layer sky130:met2) 4071 -966 10070 -826
      (props (wire-id 27)))
    (rect (layer sky130:via) 4066 -5084 4216 -4934
      (props (wire-id 27)))
    (rect (layer sky130:met1) 3981 -5169 4301 -4849
      (props (wire-id 27)))
    (rect (layer sky130:met2) 6314 -5048 6684 -4678
      (props (wire-id 28)))
    (rect (layer sky130:met2) 6429 -4933 6569 -826
      (props (wire-id 28)))
    (rect (layer sky130:met2) 6429 -966 10070 -826
      (props (wire-id 28)))
    (rect (layer sky130:via) 6424 -4938 6574 -4788
      (props (wire-id 28)))
    (rect (layer sky130:met1) 6339 -5023 6659 -4703
      (props (wire-id 28)))
    (label (layer sky130:li1_label) (text "V_BIAS_TAIL_LOW") (origin 6132 -10861)
      (kind port-name))
    (label (layer sky130:li1_label) (text "V_BIAS_TAIL_HIGH") (origin 2061 -9851)
      (kind port-name))
    (label (layer sky130:li1_label) (text "V_BIAS_NCASC_LOW") (origin 7231 -8561)
      (kind port-name))
    (label (layer sky130:li1_label) (text "V_BIAS_NCASC_HIGH") (origin 9931 -10862)
      (kind port-name))
    (label (layer sky130:li1_label) (text "cgm_b") (origin 8314 -6008)
      (kind port-name))
    (label (layer sky130:li1_label) (text "V_Vtref_xh") (origin 2496 -5539)
      (kind port-name))
    (label (layer sky130:li1_label) (text "cgm_p_CTAT_xlat") (origin 13396 -12679)
      (kind port-name))))
