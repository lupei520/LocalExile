// Decompile the function at an address on a program imported with -noanalysis: disassemble its body by following flow,
// create the function, then decompile it (and, with :N, its callees to depth N).
// usage: -postScript DecompAt.java <outfile> <addr|addr:depth> ...
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.app.cmd.disassemble.DisassembleCommand;
import ghidra.app.cmd.function.CreateFunctionCmd;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import java.io.*;
import java.util.*;

public class DecompAt extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        Set<Long> seen = new HashSet<>();
        for (int i = 1; i < a.length; i++) {
            String[] p = a[i].split(":");
            Address ad = toAddr(p[0]);
            int depth = p.length > 1 ? Integer.parseInt(p[1]) : 0;
            Function f = getFunctionContaining(ad);
            if (f == null) {
                if (getInstructionAt(ad) == null) {
                    DisassembleCommand d = new DisassembleCommand(ad, null, true);
                    d.applyTo(currentProgram, monitor);
                }
                new CreateFunctionCmd(ad).applyTo(currentProgram, monitor);
                f = getFunctionContaining(ad);
            }
            if (f == null) { out.println("// no function at " + p[0]); continue; }
            dump(di, f, depth, seen, out);
        }
        out.close();
    }

    private void dump(DecompInterface di, Function f, int depth, Set<Long> seen, PrintWriter out) throws Exception {
        if (!seen.add(f.getEntryPoint().getOffset())) return;
        DecompileResults r = di.decompileFunction(f, 60, monitor);
        out.println("// ===== " + f.getName() + " @ " + f.getEntryPoint() + " =====");
        out.println(r.decompileCompleted() ? r.getDecompiledFunction().getC() : "// decompile failed: " + r.getErrorMessage());
        out.println();
        if (depth > 0)
            for (Function callee : callees(f)) dump(di, callee, depth - 1, seen, out);
    }

    private Set<Function> callees(Function f) {
        Set<Function> out = new LinkedHashSet<>();
        for (Address a : f.getBody().getAddresses(true)) {
            ghidra.program.model.symbol.Reference[] rs = getReferencesFrom(a);
            for (ghidra.program.model.symbol.Reference r : rs)
                if (r.getReferenceType().isCall()) {
                    Function t = getFunctionAt(r.getToAddress());
                    if (t != null) out.add(t);
                }
        }
        return out;
    }
}