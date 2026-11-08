// Usage: analyzeHeadless <projdir> <proj> -process Game.exe -noanalysis -readOnly -scriptPath <this dir> -postScript Probe.java <outfile> <addr> ...
// Prints the program's image base, its address ranges, how many functions it has, and what each address holds.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import java.io.*;

public class Probe extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        out.println("program: " + currentProgram.getName());
        out.println("image base: " + currentProgram.getImageBase());
        out.println("min: " + currentProgram.getMinAddress() + " max: " + currentProgram.getMaxAddress());
        FunctionManager fm = currentProgram.getFunctionManager();
        out.println("functions: " + fm.getFunctionCount());
        for (int i = 1; i < a.length; i++) {
            Address ad = toAddr(a[i]);
            Function f = fm.getFunctionContaining(ad);
            CodeUnit cu = currentProgram.getListing().getCodeUnitContaining(ad);
            out.println(a[i] + ": function=" + (f == null ? "none" : f.getName() + "@" + f.getEntryPoint())
                + " codeUnit=" + (cu == null ? "none" : cu.getMnemonicString() + " " + cu));
        }
        // the first few functions, to see where the analyzer put them
        int shown = 0;
        for (Function f : fm.getFunctions(true)) {
            if (shown++ >= 5) break;
            out.println("first function: " + f.getName() + " @ " + f.getEntryPoint());
        }
        out.close();
    }
}
