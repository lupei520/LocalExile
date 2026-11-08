// Direct reference lookup without a full analysis: linearly disassemble the executable blocks (which creates operand
// references), then report every reference to the given addresses with the function containing it, when one exists.
// Much faster than the analyzer pass on this 79 MB image, and enough to find "who reads this table / vtable / string".
// usage: -postScript RefsFast.java <outfile> <addr> [addr...]
import ghidra.app.script.GhidraScript;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class RefsFast extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        // Disassemble every executable block in one pass, following flow. Existing instructions are skipped by the command.
        for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
            if (!block.isExecute() || !block.isInitialized()) continue;
            Address start = block.getStart();
            if (getInstructionAt(start) != null) continue;
            AddressSet set = new AddressSet(block.getStart(), block.getEnd());
            DisassembleCommand cmd = new DisassembleCommand(start, set, true);
            cmd.applyTo(currentProgram, monitor);
        }

        for (int i = 1; i < a.length; i++) {
            Address target = toAddr(a[i]);
            out.println("## refs to " + target);
            ReferenceIterator it = currentProgram.getReferenceManager().getReferencesTo(target);
            int n = 0;
            while (it.hasNext()) {
                Reference r = it.next();
                Address from = r.getFromAddress();
                Function f = getFunctionContaining(from);
                String name = f == null ? "-" : f.getName() + "@" + f.getEntryPoint();
                out.println("  " + from + "  " + r.getReferenceType() + "  " + name);
                n++;
            }
            if (n == 0) out.println("  (none)");
        }
        out.close();
    }
}