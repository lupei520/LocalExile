# Find candidate dispatcher case tables in Game.exe: a dense byte table (0x2ed entries) where one value dominates (the
# default case) and every value is a small case number. The instance S->C dispatcher indexes such a table by (id - 0xd).
# Printed VAs are candidates; findrefs.py then shows which one code actually reads.
import os, pefile, collections

EXE = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "Client", "Game.exe")
LEN = 0x2ED          # the 3.29.3b table's length
MAX_CASE = 0xF4      # no case number above this was seen in the 3.29.3b table

pe = pefile.PE(EXE, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
data = open(EXE, "rb").read()
rdata = next(s for s in pe.sections if s.Name.rstrip(b"\x00") == b".rdata")
start = rdata.PointerToRawData
blob = data[start:start + rdata.SizeOfRawData]
va0 = base + rdata.VirtualAddress

found = 0
for off in range(0, len(blob) - LEN, 1):
    window = blob[off:off + LEN]
    top = max(window)
    if top > MAX_CASE:
        continue
    counts = collections.Counter(window)
    common, times = counts.most_common(1)[0]
    if times < LEN * 0.5 or len(counts) < 2:
        continue
    va = va0 + off
    print(f"candidate va=0x{va:x} default=0x{common:x} ({times}/{LEN}) distinct={len(counts)} max=0x{top:x}")
    found += 1
print(f"// {found} candidate(s)")