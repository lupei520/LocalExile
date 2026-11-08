// List functions that own a switch jump table, with the number of destinations: the instance S->C dispatcher is the one
// with ~0xf0 entries. Ghidra recovers those tables during analysis, so this needs the analyzed project (not -noanalysis).
// usage: -postScript FindJumpTables.java <outfile> [minDestinations]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.pcode.JumpTable;
import java.io.*;
import java.util.*;

public class FindJumpTables extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        int min = a.length > 1 ? Integer.parseInt(a[1]) : 8;
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        int seen = 0;
        FunctionIterator it = currentProgram.getFunctionManager().getFunctions(true);
        for (Function f : it) {
            JumpTable best = null;
            Address site = null;
            for (Address addr : f.getBody().getAddresses(true)) {
                Instruction ins = getInstructionAt(addr);
                if (ins == null) continue;
                JumpTable t = ins.getJumpTable();
                if (t == null) continue;
                if (best == null || t.getCases().length > best.getCases().length) {
                    best = t;
                    site = ins.getAddress();
                }
            }
            if (best == null || best.getCases().length < min) continue;
            out.println(f.getName() + " @ " + f.getEntryPoint() + " switch@" + site
                + " destinations=" + best.getCases().length);
            seen++;
        }
        out.close();
        println("wrote " + a[0] + " (" + seen + " functions with a switch of >= " + min + " destinations)");
    }
}