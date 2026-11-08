# Find tables that look like the instance S->C dispatcher's case table: gather every RIP-relative operand target in
# .text (whatever the index/scale), then keep the ones whose bytes there are a dense 0x2ed table (a dominant default
# value, only small case numbers). Works without xrefs, which the 3.29.3b addresses do not give in 3.29.3c.
import os, pefile, collections
from capstone import *
from capstone.x86 import *

EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "Client", "Game.exe")
pe = pefile.PE(EXE, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE, "rb").read()
text = next(s for s in pe.sections if s.Name.rstrip(b"\x00") == b".text")

def read_va(va, n):
    for s in pe.sections:
        if s.VirtualAddress + base <= va < s.VirtualAddress + base + s.SizeOfRawData:
            off = s.PointerToRawData + (va - (s.VirtualAddress + base))
            return data[off:off + n]
    return b""

md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
code = data[text.PointerToRawData:text.PointerToRawData + text.SizeOfRawData]
start = base + text.VirtualAddress

targets = collections.Counter()
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
            if op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP:
                targets[ins.address + ins.size + op.mem.disp] += 1
                break

print(f"// {len(targets)} distinct RIP-relative targets in .text")
hits = 0
for va, reads in sorted(targets.items()):
    window = read_va(va, 0x2ED)
    if len(window) < 0x2ED:
        continue
    counts = collections.Counter(window)
    common, times = counts.most_common(1)[0]
    fraction = times / 0x2ED
    if not (0.45 <= fraction <= 0.75) or len(counts) < 40 or max(window) > 0xF4:
        continue
    print(f"table va=0x{va:x} default=0x{common:x} ({times}/0x2ed) distinct={len(counts)} reads={reads}")
    hits += 1
print(f"// {hits} table(s)")