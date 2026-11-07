# usage: c2s_lengths.py [--check]   -- every client -> server message whose writer is exact -> the Gateway's table
# Writes src/GameServer.Core/Protocol/Instance/ClientMessageLengths.cs from Agents/docs/protocol/c2s.md (the extracted
# table; see c2s_md.py for how the doc is produced). --check verifies the file instead of writing it and exits 1 when
# it is stale or when a generated length disagrees with InstanceProtocol.KnownClientLength.
#
# The doc is a 3.29.3b extraction while the supported play client is 3.29.3c, whose instance message ids moved, so the
# ids are shifted here before they are written (Agents/docs/re/3.29.3c-client-diff.md, "Message id shift"). This script
# is the only place that shift is applied to a length table.
import os, re, sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DOC = os.path.join(REPO, "Agents", "docs", "protocol", "c2s.md")
DIFF = "Agents/docs/re/3.29.3c-client-diff.md"
PROTO = os.path.join(REPO, "src", "GameServer.Core", "Protocol", "Instance", "InstanceProtocol.cs")
OUT = os.path.join(REPO, "src", "GameServer.Core", "Protocol", "Instance", "ClientMessageLengths.cs")

# 3.29.3c inserts one instance message id at 0x2a8 and another at 0x2b0; every id from the first insertion is one
# higher than its 3.29.3b original, and every id from the second is two higher (DIFF, "Message id shift"). Corroborated
# by the ids the server already sends on 3.29.3c: gold 0x2a9 -> 0x2aa, Positioned 0x2b0 -> 0x2b2 and the leave-area ack
# 0x2cb -> 0x2cd.
FIRST_INSERTED = 0x2A8
SECOND_INSERTED = 0x2B0

# That the document is a 3.29.3b extraction is checked, not assumed: its senders are addresses in the 3.29.3b image,
# and DIFF names one of them moving (the leave-area ack's sender 0x142148690 becomes 0x14214f032). A document
# regenerated against another build no longer carries this address, and shifting its ids again would quietly register
# the wrong ones, so the shift is applied only to the extraction it was written for.
EXTRACTED_SENDER = "0x142148690"


def extraction_is_3_29_3b():
    """Whether the document still is the 3.29.3b extraction whose ids this script shifts."""
    return EXTRACTED_SENDER in open(DOC, encoding="utf-8").read()


def supported_id(extracted):
    """The 3.29.3c id of a 3.29.3b id from the extraction."""
    if extracted < FIRST_INSERTED:
        return extracted
    return extracted + 1 if extracted < SECOND_INSERTED else extracted + 2


def doc_lengths():
    """id -> whole-message length for the instance rows whose writer is exact, as extracted (3.29.3b ids)."""
    out, section = {}, ""
    for line in open(DOC, encoding="utf-8"):
        if line.startswith("## "):
            section = line[3:].strip()
            continue
        if not section.startswith("Instance connection") or not line.startswith("|"):
            continue
        parts = [p.strip() for p in line.strip().strip("|").split("|")]
        if len(parts) >= 3 and re.fullmatch(r"0x[0-9a-f]{4}", parts[0]) and re.fullmatch(r"\d+", parts[2]):
            out[int(parts[0], 16)] = int(parts[2])
    return out


def _constants():
    """Every `const` in src/, as owner-qualified and bare names.

    A declaration can wrap across lines and a file can hold several classes, so each declaration is attributed to the
    type declared last before it.
    """
    consts = {}
    for root, _, files in os.walk(os.path.join(REPO, "src")):
        for f in files:
            if not f.endswith(".cs"):
                continue
            text = open(os.path.join(root, f), encoding="utf-8").read()
            owners = [(m.start(), m.group(1))
                      for m in re.finditer(r"\b(?:class|struct|record)\s+(\w+)", text)]
            for m in re.finditer(r"const\s+(?:ushort|int|byte)\s+([^;]+);", text):
                owner = next((n for p, n in reversed(owners) if p < m.start()), None)
                for part in m.group(1).split(","):
                    kv = re.match(r"\s*(\w+)\s*=\s*(0x[0-9a-fA-F]+|\d+)\s*$", part)
                    if kv:
                        consts.setdefault(kv.group(1), int(kv.group(2), 0))
                        if owner:
                            consts.setdefault(owner + "." + kv.group(1), int(kv.group(2), 0))
    return consts


def gateway_lengths():
    """KnownClientLength's fixed lengths, as the Gateway itself states them."""
    consts = _constants()

    def value(x):
        if re.fullmatch(r"0x[0-9a-fA-F]+|\d+", x):
            return int(x, 0)
        return consts.get(x, consts.get(x.split(".")[-1]))

    text = open(PROTO, encoding="utf-8").read()
    body = text[text.index("KnownClientLength(ushort id) => id switch"):]
    body = body[:body.index("};")]
    out = {}
    for key, length in re.findall(r"([\w.]+)\s*=>\s*([\w.]+)", body):
        if key == "_":
            continue
        i, n = value(key), value(length)
        if isinstance(i, int) and isinstance(n, int):
            out[i] = n
    return out


def render(table, hand):
    lines = [
        "// <auto-generated>",
        "//   Generated by scripts/re/c2s_lengths.py from Agents/docs/protocol/c2s.md; do not edit by hand.",
        f"//   The doc is a 3.29.3b extraction, so its ids are shifted to the 3.29.3c ones the server speaks here",
        f"//   ({DIFF}, \"Message id shift\"): every extracted id from 0x2a8 is one higher, and every id from 0x2b0 two",
        "//   higher. Re-run the script (or pass --check) after the extraction or the shift changes.",
        "// </auto-generated>",
        "",
        "namespace GameServer.Core.Protocol.Instance;",
        "",
        "/// <summary>",
        "/// Every client message on the instance connection whose writer writes a fixed number of bytes, so a C→S read can",
        "/// be framed without guessing (Agents/docs/protocol/c2s.md). A message whose writer branches or takes a stream",
        "/// argument has no entry and still ends framing at its id.",
        "/// </summary>",
        "public static class ClientMessageLengths",
        "{",
        "    /// <summary>The whole message in bytes, the two-byte id included, or null when the id is not one of them.</summary>",
        "    public static int? Known(ushort id) => id switch",
        "    {",
    ]
    for i in sorted(table):
        lines.append("        %#06x => %d," % (i, table[i]))
    lines += ["        _ => null,", "    };", "", "    /// <summary>How many ids the table covers.</summary>",
              "    public const int Count = %d;" % len(table), "}"]
    shifted = sorted((i, supported_id(i)) for i in table if i >= FIRST_INSERTED)
    assert all(supported_id(i) == j for i, j in shifted), "shift is not monotonic"
    return "\n".join(lines) + "\n", shifted


def main():
    if not extraction_is_3_29_3b():
        print(f"{DOC} does not carry the 3.29.3b sender {EXTRACTED_SENDER} any more, so it was probably regenerated "
              f"against another build. Shifting its ids would register the wrong ones: fix the shift in this script "
              f"before generating the table.", file=sys.stderr)
        return 1
    extracted = doc_lengths()
    table = {supported_id(i): n for i, n in extracted.items()}
    assert len(table) == len(extracted), "two extracted ids collapse onto one"
    hand = gateway_lengths()
    bad = sorted((i, n, table[i]) for i, n in hand.items() if i in table and table[i] != n)
    text, shifted = render(table, hand)
    print(f"{DOC}: {len(extracted)} exact instance lengths"
          f" ({len(shifted)} of them shifted to their 3.29.3c id)")
    print(f"{OUT}: {len(table)} ids; KnownClientLength states {len(hand)}"
          f" ({sum(1 for i in hand if i in table)} of them covered by the table, "
          f"{sum(1 for i in hand if i not in table)} not)")
    if bad:
        for i, n, g in bad:
            print(f"  DISAGREE {i:#06x}: KnownClientLength {n}, extracted {g}", file=sys.stderr)
        print("a hand-verified length and the extraction disagree; fix the extraction or the hand table", file=sys.stderr)
        return 1
    if "--check" in sys.argv:
        have = open(OUT, encoding="utf-8").read() if os.path.exists(OUT) else ""
        if have != text:
            print(f"{OUT} is stale; run scripts/re/c2s_lengths.py", file=sys.stderr)
            return 1
        print("up to date")
        return 0
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
