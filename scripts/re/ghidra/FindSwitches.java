// Rank functions by how many computed jumps (switch dispatch) their body contains, and print each site's jump-table
// reference. The instance S->C dispatcher is one of the few functions with a very large switch.
// usage: -postScript FindSwitches.java <outfile> [topN]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class FindSwitches extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        int top = a.length > 1 ? Integer.parseInt(a[1]) : 25;
        Map<Function, Integer> counts = new HashMap<>();
        Map<Function, List<String>> sites = new HashMap<>();
        FunctionIterator it = currentProgram.getFunctionManager().getFunctions(true);
        for (Function f : it) {
            if (f.isThunk()) continue;
            int n = 0;
            List<String> where = new ArrayList<>();
            for (Address addr : f.getBody().getAddresses(true)) {
                Instruction ins = getInstructionAt(addr);
                if (ins == null) continue;
                FlowType ft = ins.getFlowType();
                if (!ft.isJump() && !ft.isCall()) continue;
                if (!ins.getFlowType().isComputed()) continue;
                n++;
                StringBuilder refs = new StringBuilder();
                for (Reference r : ins.getReferencesFrom())
                    if (r.getReferenceType().isJump() || r.getReferenceType().isCall())
                        refs.append(" ").append(r.getToAddress());
                where.add(ins.getAddress() + " " + ins + refs);
            }
            if (n > 0) { counts.put(f, n); sites.put(f, where); }
        }
        List<Function> ranked = new ArrayList<>(counts.keySet());
        ranked.sort((x, y) -> counts.get(y) - counts.get(x));
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        int shown = 0;
        for (Function f : ranked) {
            if (shown++ >= top) break;
            out.println(f.getName() + " @ " + f.getEntryPoint() + " computedJumps=" + counts.get(f));
            for (String s : sites.get(f)) out.println("    " + s);
        }
        out.close();
        println("wrote " + a[0] + " (" + ranked.size() + " functions with computed jumps)");
    }
}