# DRC script for rekolektion bitcell
# Run with: magic -dnull -noconsole -rcfile $PDK/libs.tech/magic/sky130A.magicrc < scripts/run_drc.tcl
#
# Defaults to the fast geometry-only rule set. Set the env var
# RKT_DRC_FULL=1 to switch to drc(full) — sign-off rules (latch-up,
# implant-aware diff/tap.9 + licon.9, nwell.4). Slower; opt in.

set gds_file [lindex $argv 0]
if {$gds_file eq ""} {
    set gds_file "output/sky130_sram_6t_bitcell.gds"
}
set use_full 0
if {[info exists ::env(RKT_DRC_FULL)] && $::env(RKT_DRC_FULL) ne "" && $::env(RKT_DRC_FULL) ne "0"} {
    set use_full 1
}

puts "Loading GDS: $gds_file"
gds read $gds_file

# Load the top cell
set topcell [lindex [cellname list top] 0]
puts "Top cell: $topcell"
load $topcell

# Select everything and run DRC
select top cell
if {$use_full} { drc style drc(full) }
drc catchup
drc check

# Get error count
set count [drc count total]
puts ""
puts "============================================"
puts "DRC Results: $topcell"
puts "============================================"
puts "Total DRC errors: $count"
puts ""

# List all DRC errors with details
if {$count > 0} {
    puts "DRC Error Details:"
    puts "--------------------------------------------"
    set why_dict [drc listall why]
    foreach {msg boxes} $why_dict {
        puts "\nViolation: $msg"
        set box_count 0
        foreach box $boxes {
            puts "  at: $box"
            incr box_count
            if {$box_count > 20} {
                puts "  ... (truncated, too many instances)"
                break
            }
        }
    }
} else {
    puts "*** DRC CLEAN ***"
}

puts ""
puts "============================================"
quit -noprompt
