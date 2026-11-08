# Locate the instance S->C dispatcher's case table from the code that reads it: a *byte* load indexed by a register off a
# RIP-relative base (`movzx eax, byte ptr [rax + rip + disp]`). For every such site the displacement's target is a table;
# a candidate is printed when the bytes there look like a case table (one dominant default value, only small case
# numbers). This replaces xrefs, which the documented 3.29.3b addresses do not survive in 3.29.3c.
import os, pefile, collections
from capstone import *
from capstone.x86 import *

EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "Client", "Game.exe")
pe = pefile.PE(EXE, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE, "rb").read()
text = next(s for s in pe.sections if s.Name.rstrip(b"\x00") == b".text")
rdata = next(s for s in pe.sections if s.Name.rstrip(b"\x00") == b".rdata")

def read_va(va, n):
    for s in pe.sections:
        start = s.PointerToRawData
        if s.VirtualAddress + base <= va < s.VirtualAddress + base + s.SizeOfRawData:
            off = start + (va - (s.VirtualAddress + base))
            return data[off:off + n]
    return b""

md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
code = data[text.PointerToRawData:text.PointerToRawData + text.SizeOfRawData]
start = base + text.VirtualAddress

seen = collections.Counter()
CHUNK = 0x20000
for off in range(0, len(code), CHUNK):
    for ins in md.disasm(code[off:off + CHUNK], start + off):
        if ins.id == 0:
            continue
        try:
            ops = ins.operands
        except CsError:
            continue
        for op in ops:
            if op.type != X86_OP_MEM or op.mem.base != X86_REG_RIP or op.mem.index == 0:
                continue
            if op.mem.scale != 1 or op.size != 1:      # a byte table indexed by one register
                continue
            target = ins.address + ins.size + op.mem.disp
            seen[target] += 1
            break

candidates = 0
for va in sorted(seen):
    window = read_va(va, 0x2ED)
    if len(window) < 0x2ED:
        continue
    counts = collections.Counter(window)
    common, times = counts.most_common(1)[0]
    top = max(window)
    if times < 0x2ED * 0.4 or top > 0xF4:
        continue
    print(f"table candidate va=0x{va:x} default=0x{common:x} ({times}/0x2ed) distinct={len(counts)} max=0x{top:x} reads={seen[va]}")
    candidates += 1
print(f"// {candidates} table candidate(s) among {len(seen)} indexed byte-table reads")