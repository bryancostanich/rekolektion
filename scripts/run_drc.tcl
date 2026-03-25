# DRC script for rekolektion bitcell
# Run with: magic -dnull -noconsole -rcfile $PDK/libs.tech/magic/sky130A.magicrc < scripts/run_drc.tcl

set gds_file [lindex $argv 0]
if {$gds_file eq ""} {
    set gds_file "output/sky130_sram_6t_bitcell.gds"
}

puts "Loading GDS: $gds_file"
gds read $gds_file

# Load the top cell
set topcell [lindex [cellname list top] 0]
puts "Top cell: $topcell"
load $topcell

# Select everything and run DRC
select top cell
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
