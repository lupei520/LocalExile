"""Which client build `Client/Game.exe` is, and where that build's tables are.

The tools under `scripts/re/` used to carry one build's addresses as constants. Run against another build they read
whatever lives at the old address and print it as an answer: with the 3.29.3c client, `0x142154e14` holds the bytes
`10 49 8d 8d 10 c6 00 00 e8 0f 5b 8b fe 48 8b c8` (code), and `dispatch_ids.py` printed a full, meaningless mapping
from it. Every address is handed out from here now, and only after the region's own bytes show that this is where the
table lives, so a build that cannot be verified is refused instead of read.

The JavaScript tools (`client-control.js`, `walkmaps.js`, `nullifier.js`) detect the build from their own byte
signatures and fail closed already; folding their tables and this one into a single committed artifact is the next
step, not something this module claims.
"""
import os

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE = os.path.join(REPO, "Client", "Game.exe")

# One entry per client build the tools know: name -> this build's addressed tables. `instance_dispatch` is the
# instance message dispatcher's case table, indexed by id - 0x0d (Agents/docs/re/3.29.3c-client-diff.md, "Message id
# shift"; Agents/docs/protocol/address-index.md holds the rest of each build's addresses).
BUILDS = (
    ("3.29.3b", {"instance_dispatch": 0x142154E14}),
    ("3.29.3c", {"instance_dispatch": 0x14215B784}),
)

TABLE_INDEX_BASE = 0x0D
TABLE_ENTRIES = 0x2ED
# A case table is one repeated "no case" filler byte plus a case number per handled id - 3.29.3c's is 481 of 749
# bytes of 0xfd with 268 cases. Code, or any other region, has no such filler, which is what makes this check
# meaningful on a build the table was not written for.
MIN_FILLER_SHARE = 0.5
MIN_CASES, MAX_CASES = 50, 700


class UnsupportedClient(Exception):
    """`Client/Game.exe` is absent, or is not a build whose tables can be located."""


_loaded = None


def _read(va, length):
    import pefile
    pe = pefile.PE(EXE, fast_load=True)
    data = open(EXE, "rb").read()
    off = pe.get_offset_from_rva(va - pe.OPTIONAL_HEADER.ImageBase)
    return data[off:off + length]


def looks_like_case_table(table):
    """Whether these bytes look like a dispatcher case table rather than code or padding."""
    if len(table) < TABLE_ENTRIES:
        return False
    filler = max(set(table), key=table.count)
    cases = sum(1 for b in table if b != filler)
    return table.count(filler) / TABLE_ENTRIES >= MIN_FILLER_SHARE and MIN_CASES <= cases <= MAX_CASES


def load():
    """The build `Client/Game.exe` is, as (name, {table: address}). Raises UnsupportedClient."""
    global _loaded
    if _loaded is not None:
        return _loaded
    if not os.path.exists(EXE):
        raise UnsupportedClient(f"{EXE} not found; the tools under scripts/re/ read the client image")
    try:
        matches = [(name, tables) for name, tables in BUILDS
                   if looks_like_case_table(_read(tables["instance_dispatch"], TABLE_ENTRIES))]
    except Exception as e:
        raise UnsupportedClient(f"cannot read {EXE}: {e}") from e
    found = [name for name, _ in matches]
    if len(matches) != 1:
        raise UnsupportedClient(
            f"{EXE} is not a client this tool knows: expected one dispatcher case table, found {found or 'none'} at "
            + ", ".join(f"0x{t['instance_dispatch']:x} ({n})" for n, t in BUILDS)
            + "; refusing to read addresses from it")
    _loaded = matches[0]
    return _loaded


def build():
    """The name of the client build, e.g. '3.29.3c'."""
    return load()[0]


def dispatch_cases():
    """Instance message id -> its switch case, for every id the client handles."""
    name, tables = load()
    table = _read(tables["instance_dispatch"], TABLE_ENTRIES)
    filler = max(set(table), key=table.count)
    return {i + TABLE_INDEX_BASE: c for i, c in enumerate(table) if c != filler}
