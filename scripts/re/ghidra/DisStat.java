// Report how much of the program is disassembled (instruction count per block) — a sanity check for the fast path.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;

public class DisStat extends GhidraScript {
    @Override
    public void run() throws Exception {
        Listing lst = currentProgram.getListing();
        for (MemoryBlock b : currentProgram.getMemory().getBlocks()) {
            if (!b.isExecute() || !b.isInitialized()) continue;
            int n = 0;
            InstructionIterator it = lst.getInstructions(b.getStart(), true);
            while (it.hasNext()) { Instruction i = it.next(); if (!b.contains(i.getAddress())) break; n++; }
            println("block " + b.getName() + " " + b.getStart() + "-" + b.getEnd() + " instructions=" + n);
        }
    }
}