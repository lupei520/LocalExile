# Find instructions in Game.exe that reference given addresses through RIP-relative operands (lea/mov/cmp ...).
# This is the xref lookup the headless Ghidra analysis was supposed to provide, without its analyzer pass.
# usage: findrefs.py 0x142154e14 [0x... ...]
import sys, os, pefile
from capstone import *
from capstone.x86 import *

EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "Client", "Game.exe")
targets = {int(a, 16) for a in sys.argv[1:]}
if not targets:
    print("usage: findrefs.py <addr> [addr ...]"); sys.exit(2)

pe = pefile.PE(EXE, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE, "rb").read()
text = next(s for s in pe.sections if s.Name.rstrip(b"\x00") == b".text")
start = base + text.VirtualAddress
code = data[text.PointerToRawData:text.PointerToRawData + text.SizeOfRawData]

md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
md.skipdata = True
hits = 0
# Chunked: capstone keeps an instruction cache that runs out of memory on a 40 MB section in one call.
CHUNK = 0x20000
for off in range(0, len(code), CHUNK):
    chunk = code[off:off + CHUNK]
    addr = start + off
    for ins in md.disasm(chunk, addr):
        if ins.id == 0:      # a skipdata pseudo-instruction has no operands
            continue
        try:
            ops = ins.operands
        except CsError:
            continue
        for op in ops:
            if op.type != X86_OP_MEM or op.mem.base != X86_REG_RIP:
                continue
            target = ins.address + ins.size + op.mem.disp
            if target in targets:
                print(f"0x{ins.address:x}  {ins.mnemonic} {ins.op_str}   -> 0x{target:x}")
                hits += 1
print(f"// {hits} reference(s) to {', '.join(hex(t) for t in sorted(targets))}")