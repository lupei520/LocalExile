# Find switch tables the binary builds in two steps: `mov reg, [table + idx*8]` then `jmp/call reg`. The table's 8-byte
# entries must point into .text. Also catches `jmp [table + idx*8]`. Chunked with skipdata so a data byte never stops the
# scan of a chunk.
import os, pefile, struct, sys
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

seen = {}
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
            if op.type != X86_OP_MEM or op.mem.index == 0 or op.mem.scale != 8:
                continue
            table = op.mem.disp if op.mem.base == 0 else (ins.address + ins.size + op.mem.disp if op.mem.base == X86_REG_RIP else None)
            if table is None:
                continue
            seen.setdefault(table, []).append((ins.address, ins.mnemonic, ins.op_str))
            break

hits = 0
for table, sites in sorted(seen.items()):
    entries = read_va(table, 8 * 256)
    if len(entries) < 64:
        continue
    total = good = 0
    for i in range(0, len(entries) - 8, 8):
        v = struct.unpack("<Q", entries[i:i + 8])[0]
        if v == 0:
            break
        total += 1
        if text_lo <= v < text_hi:
            good += 1
    if total >= 8 and good >= total * 0.8:
        site = sites[0]
        print(f"table=0x{table:x} entries={total} site=0x{site[0]:x} {site[1]} {site[2]}")
        hits += 1
print(f"// {hits} switch table(s) among {len(seen)} indexed qword tables")