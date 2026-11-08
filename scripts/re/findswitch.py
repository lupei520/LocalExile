# Find switch jump tables: `jmp qword ptr [reg*8 + disp]` whose 8-byte entries point back into .text. A function around
# such a site is a dispatcher; the instance S->C dispatcher is one of them, and the case table it indexes sits nearby.
import os, pefile, struct
from capstone import *
from capstone.x86 import *

EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "Client", "Game.exe")
pe = pefile.PE(EXE, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE, "rb").read()
text = next(s for s in pe.sections if s.Name.rstrip(b"\x00") == b".text")
text_lo = base + text.VirtualAddress
text_hi = text_lo + text.SizeOfRawData

def read_va(va, n):
    for s in pe.sections:
        if s.VirtualAddress + base <= va < s.VirtualAddress + base + s.SizeOfRawData:
            off = s.PointerToRawData + (va - (s.VirtualAddress + base))
            return data[off:off + n]
    return b""

md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
md.skipdata = True
code = data[text.PointerToRawData:text.PointerToRawData + text.SizeOfRawData]
start = text_lo

sites = 0
CHUNK = 0x20000
for off in range(0, len(code), CHUNK):
    for ins in md.disasm(code[off:off + CHUNK], start + off):
        if ins.id == 0 or ins.mnemonic not in ("jmp", "call"):
            continue
        try:
            ops = ins.operands
        except CsError:
            continue
        if len(ops) != 1 or ops[0].type != X86_OP_MEM or ops[0].mem.scale != 8:
            continue
        table = ops[0].mem.disp if ops[0].mem.base == 0 else ins.address + ins.size + ops[0].mem.disp
        entries = read_va(table, 8 * 64)
        if len(entries) < 8 * 8:
            continue
        good = 0
        total = 0
        for i in range(0, len(entries), 8):
            value = struct.unpack("<Q", entries[i:i + 8])[0]
            if value == 0:
                continue
            total += 1
            if text_lo <= value < text_hi:
                good += 1
        if total >= 8 and good >= total * 0.8:
            print(f"switch at 0x{ins.address:x}  table=0x{table:x}  entries={total}")
            sites += 1
print(f"// {sites} switch site(s)")