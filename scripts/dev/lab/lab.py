"""Run a combat-lab scenario on the local client, collect the damage ledger and check it.

    python scripts/dev/lab/lab.py --list
    python scripts/dev/lab/lab.py --dry-run -s fireball-vs-rhoa
    python scripts/dev/lab/lab.py run -s scripts/dev/lab/scenarios/fireball-vs-rhoa.json
    python scripts/dev/lab/lab.py run -s fireball-vs-rhoa --session lab --json
    python scripts/dev/lab/lab.py --selftest

A scenario is JSON (the schema is in Agents/docs/dev/combat-lab.md); its steps run through the native client bridge
(`scripts/dev/client_control.py`, Agents/docs/dev/client-control.md) as chat commands, and what the Gateway recorded is
read back from the ledger `/damage` names (`logs/damage-<character>.jsonl`). Nothing here touches a save, starts a
server, or drives the mouse.

The interpreter matters: the runner spawns `<python> scripts/dev/client_control.py --session <s> --stdio` and answers
that child's requests, and the child needs `frida` and a running client. `--python` / `LAB_PYTHON` pick it, default
`sys.executable`. `--dry-run` spawns nothing, so it works without frida and without a client.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
DEV = ROOT / "scripts" / "dev"
if str(DEV) not in sys.path:
    sys.path.insert(0, str(DEV))

SCENARIOS = HERE / "scenarios"
LOGS = ROOT / "logs"
CONTROL = DEV / "client_control.py"
LIVE = DEV / "live.ps1"

VERSION = 1

# The one line /damage answers with: "damage 3 record(s) -> logs/damage-X.jsonl (2 hit, 1 critical, 260 taken)". The
# colon and the parenthetical are both optional, so a plain "damage: 3 record(s) -> <path>" also parses.
DAMAGE_LINE = re.compile(r"damage:?\s+(\d+)\s+record\(s\)\s*->\s*(.+?)\s*(?:\(|$)")
# One /mobs entry: "0x1a2b Rhoa (12, 34)". The label may itself contain a comma, so the position's last parenthesis
# is what ends an entry; everything between the id and it is the label.
MOB_ENTRY = re.compile(r"0x(?P<id>[0-9a-fA-F]+)\s+(?P<label>.*?)\s*\((?P<pos>[-\d][^()]*)\)")

# One step's own wall-clock wait after its action, unless the step sets "settle". A live client needs a frame between a
# command's effect and the next read; the runner is not a test, so a live wait here is allowed (tests never use it).
DEFAULT_SETTLE = 0.4
RETRY_SETTLE = 0.5
# A `collect` that names a filter but no count sends this count. `/damage [count] [filter]` is the documented shape,
# and the Gateway rejects a bare `/damage <filter>` as of this writing, so the runner never sends one; the ledger's own
# capacity is the ceiling, so nothing is dropped by the choice.
DEFAULT_DAMAGE_COUNT = 256

VALUE_FIELDS = ("at", "kind", "skill", "skillId", "targetId", "target", "level", "rarity", "raw", "taken", "types",
                "crit", "outcome", "lifeBefore", "lifeAfter", "distance")
# `critical` is what the task's expectation list calls it; the ledger's own field is `crit`.
FIELD_ALIASES = {"critical": "crit", "monster": "target"}
NUMERIC_FIELDS = ("at", "skillId", "targetId", "level", "raw", "taken", "lifeBefore", "lifeAfter", "distance")
COUNT_KEYS = ("hits", "count", "records")
NUMERIC_KEYS = {f"{field}_{op}": (field, op) for field in NUMERIC_FIELDS
                for op in ("min", "max", "sum", "avg")}
# What a record says. `raw` is the resolved damage the formula must predict; `taken` is the life that hit removed, so
# `taken` is `raw` clamped by the target's remaining life. These two checks state that, and catch a ledger that broke
# it (see Agents/docs/dev/combat-lab.md, "The ledger's fields").
INVARIANTS = ("taken_le_raw", "life_arithmetic", "overkill_clamped")


class LabError(RuntimeError):
    """A session, argument or bridge problem: exit code 2, not an expectation failure."""


def configure_console():
    """Make this process able to print anything at all.

    This machine's console is a Chinese code page and its streams raise on a character it cannot encode, so a report
    holding a mangled byte or another script would kill the run at its last line and take the exit code with it. Every
    stream this process prints to is asked to replace what it cannot encode. Guarded: a stream without `reconfigure`
    (a pipe wrapped by a test, an old build) is left alone, and `printable` below covers the rest."""
    for stream in (sys.stdout, sys.stderr):
        reconfigure = getattr(stream, "reconfigure", None)
        if reconfigure is None:
            continue
        try:
            reconfigure(errors="replace")
        except (ValueError, OSError, LookupError):
            pass


def printable(text, stream=None):
    """`text` as one this stream can encode. Anything the stream's code page lacks becomes a replacement character
    instead of an exception, so a tool that reports chat lines and non-ASCII character names can always print."""
    stream = sys.stdout if stream is None else stream
    encoding = getattr(stream, "encoding", None) or "utf-8"
    try:
        return str(text).encode(encoding, "replace").decode(encoding, "replace")
    except (LookupError, UnicodeError):
        return str(text).encode("ascii", "replace").decode("ascii")


def say(text, stream=None):
    """Print `text` so that this console's code page cannot turn the print itself into a failure."""
    stream = sys.stdout if stream is None else stream
    try:
        print(printable(text, stream), file=stream)
    except (UnicodeError, OSError, ValueError):  # nothing left to try: a report is not worth a crash
        pass


# The receipt of a cast that was actually built starts with "cast " (GameModule.UseSkill). Everything else the Gateway
# can answer to /cast is a refusal: no such gem, no such monster, several monsters, or the command's own argument
# checks (Rules.CombatLab). A refused cast never reaches the skill pipeline, so it must not be reported as ok — a
# silent refusal is what turns a dead character or an ambiguous target into four steps of "0 records".
CAST_REFUSALS = ("no gem of yours", "no living monster", "matches", "usage:", "takes no third argument", "/cast ")


def cast_refusal(replies):
    """The reason a `/cast` never happened, or None when a receipt says it did. None also when the answer is not
    recognised at all: an empty ledger is the expectation's business, not this function's."""
    for line in replies or []:
        text = str(line).strip()
        if text.startswith("cast "):
            return None
        lowered = text.lower()
        if any(marker in lowered for marker in CAST_REFUSALS):
            return text
    return None


def parse_state(replies):
    """The JSON of a `/state` reply: its last `STATE ` line plus the `STATE+ ` lines after it (the campaign bridge's
    own rule, [campaign automation](campaign-automation.md)). None when there is no such line or it does not parse."""
    lines = [str(line) for line in replies or []]
    start = None
    for index, line in enumerate(lines):
        if line.startswith("STATE "):
            start = index
    if start is None:
        return None
    parts = [lines[start][len("STATE "):]]
    for line in lines[start + 1:]:
        if line.startswith("STATE+ "):
            parts.append(line[len("STATE+ "):])
        elif line.startswith("STATE"):
            break
    try:
        value = json.loads("".join(parts))
    except ValueError:
        return None
    return value if isinstance(value, dict) else None


def refusals(records):
    """The refused records of `records`: a skill use the server refused *before* any hit (kind "refused", the reason in
    `outcome`, `raw` and `taken` 0). A ledger holding only these means the skill never fired, which is a different
    finding from a hit that dealt no damage."""
    return [record for record in records or [] if str(record.get("kind", "")).lower() == "refused"]


def refusal_summary(records):
    """The refusal reason(s) in `records` as one line, or None when nothing was refused:
    "1 refused: SplittingSteel - no projectile recipe/metadata". The reason is the record's `outcome`."""
    refused = refusals(records)
    if not refused:
        return None
    counts = {}
    for record in refused:
        key = (str(record.get("skill") or "?"), str(record.get("outcome") or "").strip() or "no reason given")
        counts[key] = counts.get(key, 0) + 1
    return "; ".join(f"{count} refused: {skill} - {reason}" for (skill, reason), count in counts.items())


# A `$name` a step uses to mean the monster a `spawn` step placed. Two of the same variety cannot be told apart by
# label (`/cast <skill> Rhoa` is refused when it matches both), so the id a spawn learned is the only unambiguous way
# to name one; these references are what carries it from the spawn to the cast.
REFERENCE = re.compile(r"\$([A-Za-z_][A-Za-z0-9_]*)")


def spawn_refs(steps):
    """{name: spawn ordinal} for a scenario's `spawn` steps: every spawn is `spawn<N>` (its Nth spawn step, 1-based),
    and one that sets `"ref": "primary"` also answers to that name. Used to render a reference in a dry run, where no
    id exists yet."""
    names, ordinal = {}, 0
    for number, raw in enumerate(steps or [], 1):
        step = normalize_step(raw, number)
        if step["verb"] != "spawn":
            continue
        ordinal += 1
        names[f"spawn{ordinal}"] = ordinal
        named = step_field(step, "ref")
        if named:
            names[str(named).lstrip("$")] = ordinal
    return names


def resolve_references(text, refs, allow_plain=False, dry_run_refs=None):
    """`text` with every `$name` replaced by the id that reference holds.

    `refs` is {name: {"id": …}}. A name it does not hold is a step error naming the reference and the spawns that do
    exist — a literal `$primary` must never reach the Gateway. Exceptions: `allow_plain` leaves an unknown `$name`
    alone (the `command` verb sends free text, where `$trade` is a chat channel), and `dry_run_refs` renders the
    placeholder a dry run prints instead of an id."""
    refs = refs or {}

    def replace(match):
        name = match.group(1)
        if name in refs:
            return str(refs[name]["id"])
        if dry_run_refs is not None:
            ordinal = dry_run_refs.get(name)
            return (f"${name} (the monster spawn {ordinal} learns)" if ordinal
                    else f"${name} (no spawn step defines this reference)")
        if allow_plain:
            return match.group(0)
        known = ", ".join(f"${key} {value['id']}" for key, value in refs.items()) or "none"
        raise LabError(f"${name} is not a monster this run spawned; it spawned: {known}")

    return REFERENCE.sub(replace, str(text))


def ledger_for(character):
    """`logs/damage-<character>.jsonl` for a character's name, made file-safe the way the Gateway's own writer does
    (invalid file-name characters and spaces become '_'). None without a name."""
    if not character:
        return None
    safe = "".join("_" if ch in '<>:"/\\|?*' or ch.isspace() else ch for ch in character)
    return f"logs/damage-{safe or 'Exile'}.jsonl"


# --- small shared helpers ------------------------------------------------------------------------------------------

def now_stamp():
    return datetime.datetime.now().strftime("%Y%m%d-%H%M%S")


def one_line(text, width=200):
    """Any text as one log-safe line, cut to `width`."""
    text = " ".join(str(text).split())
    return text if len(text) <= width else text[:width] + " ..."


def as_number(value):
    """`value` as a float when it is a number, else None (a bool is not a number here)."""
    if isinstance(value, bool) or value is None:
        return None
    if isinstance(value, (int, float)):
        return float(value)
    return None


def load_records(path, root=ROOT):
    """The JSONL ledger at `path` (absolute, or relative to the repository root) as a list of dicts, oldest first.
    Raises LabError for a path that cannot be read; a bad line stops the read with its line number."""
    file = Path(path)
    if not file.is_absolute():
        file = Path(root) / file
    try:
        text = file.read_text(encoding="utf-8-sig")
    except OSError as error:
        raise LabError(f"cannot read the damage ledger {file}: {error}") from error
    records = []
    for number, line in enumerate(text.splitlines(), 1):
        line = line.strip()
        if not line:
            continue
        try:
            record = json.loads(line)
        except ValueError as error:
            raise LabError(f"{file}: line {number} is not JSON: {error}") from error
        if not isinstance(record, dict):
            raise LabError(f"{file}: line {number} is not a JSON object")
        records.append(record)
    return records


def reply_path(replies):
    """The ledger path a /damage reply names, and the record count it claims; (None, None) when it names neither."""
    for line in replies or []:
        match = DAMAGE_LINE.search(str(line))
        if match:
            return match.group(2).strip(), int(match.group(1))
    return None, None


def parse_mobs(replies):
    """The monsters in a `/mobs` reply as {id: label}. The line lists them separated by ", "; a label can contain a
    comma, so the position's last parenthesis is what ends an entry ("No monsters." gives {})."""
    found = {}
    for line in replies or []:
        for match in MOB_ENTRY.finditer(str(line)):
            found[int(match.group("id"), 16)] = match.group("label").strip()
    return found


# --- scenario steps -----------------------------------------------------------------------------------------------

def normalize_step(step, index):
    """One scenario step as {"verb", "argument", "fields", "index"}: either {"verb": …, …} or the compact
    {"<verb>": argument}. Raises LabError for anything else, naming the step's 1-based number."""
    if not isinstance(step, dict) or not step:
        raise LabError(f"step {index}: must be an object with a verb")
    step = dict(step)
    if "verb" in step:
        verb = step.pop("verb")
        argument = step.get("argument")
        if not isinstance(verb, str) or not verb:
            raise LabError(f"step {index}: verb must be a name")
    else:
        verb, argument = next(iter(step.items()))
        step = {key: value for key, value in step.items() if key != verb}
    verb = verb.strip().lower()
    if not verb:
        raise LabError(f"step {index}: empty verb")
    return {"verb": verb, "argument": argument, "fields": step, "index": index}


def step_field(step, name, default=None):
    """A step's own field, wherever the author put it: the compact form's argument object ({"cast": {"skill": …}}),
    the argument itself ({"goto": "1_1_2"}), or the step's own keys (the long {"verb": …, "area": …} form)."""
    argument = step["argument"]
    if isinstance(argument, dict) and name in argument:
        return argument[name]
    if name in step["fields"]:
        return step["fields"][name]
    if isinstance(argument, dict):
        return default
    return argument if name == "argument" else default


def resolve_expectation(spec, refs):
    """A `$name` in an expectation means the monster a spawn step placed: `targetId: "$primary"` compares the ledger's
    object id, `target: "$primary"` its label. Any other value is left exactly as written."""
    resolved = {}
    for key, value in spec.items():
        if isinstance(value, str) and value.startswith("$") and key in ("target", "targetId"):
            name = value.lstrip("$")
            if name in (refs or {}):
                resolved[key] = refs[name]["numeric"] if key == "targetId" else refs[name]["label"]
                continue
            known = ", ".join(f"${key_} {entry['id']}" for key_, entry in (refs or {}).items()) or "none"
            raise LabError(f"${name} is not a monster this run spawned; it spawned: {known}")
        resolved[key] = value
    return resolved


def step_references(step):
    """The `$name`s a step's own arguments mention, read before they are resolved (the resolved command no longer holds
    them). Used to report which monster each step acted on."""
    texts = []
    for name in ("target", "monster", "text"):
        value = step_field(step, name)
        if isinstance(value, str):
            texts.append(value)
    if isinstance(step["argument"], str):
        texts.append(step["argument"])
    return {name for text in texts for name in REFERENCE.findall(text)}


def step_command(step, refs=None, dry_run_refs=None):
    """The slash command a step sends as it would be typed, or None for a step that sends none (settle, expect).
    This is the single place the step schema becomes Gateway commands, so --dry-run shows exactly what runs.

    `refs` resolves the `$name` references a scenario uses for the monsters its spawn steps placed; `dry_run_refs`
    renders them as placeholders when no id exists yet (a dry run)."""
    verb = step["verb"]

    if verb == "command":
        text = step_field(step, "text", step["argument"] if isinstance(step["argument"], str) else None)
        if not isinstance(text, str) or not text:
            raise LabError(f"step {step['index']}: command needs its text")
        text = text if text.startswith("/") else "/" + text
        # Free text: a known reference is filled in (so `/kill $primary` works), an unknown `$name` is left alone
        # because it may be chat (`$trade`). A monster argument is strict instead; see `cast` and `tp`.
        return resolve_references(text, refs, allow_plain=True, dry_run_refs=dry_run_refs)

    if verb == "goto":
        area = step_field(step, "area", step["argument"] if isinstance(step["argument"], str) else None)
        if not isinstance(area, str) or not area:
            raise LabError(f"step {step['index']}: goto needs an area")
        seed = step_field(step, "seed")
        return f"/goto {area}" + (f" {seed}" if seed is not None else "")

    if verb == "spawn":
        monster = step_field(step, "monster", step["argument"] if isinstance(step["argument"], str) else None)
        if not isinstance(monster, str) or not monster:
            raise LabError(f"step {step['index']}: spawn needs a monster")
        level = step_field(step, "level")
        text = f"/spawn_mob {monster}" + (f"@{level}" if level is not None else "")
        if step_field(step, "friendly"):
            text += " friendly"
        if step_field(step, "rarity"):
            text += f" {step_field(step, 'rarity')}"
        if step_field(step, "name"):
            text += f" {step_field(step, 'name')}"
        return text

    if verb == "cast":
        skill = step_field(step, "skill", step["argument"] if isinstance(step["argument"], str) else None)
        if not isinstance(skill, str) or not skill:
            raise LabError(f"step {step['index']}: cast needs a skill")
        # The contract's own order: a named target, else a cell, else the character's own cell. A monster argument
        # resolves its references strictly: a literal `$primary` must never reach the Gateway.
        target = step_field(step, "target") or step_field(step, "monster")
        if target not in (None, ""):
            target = resolve_references(target, refs, dry_run_refs=dry_run_refs)
            return f"/cast {skill} {target}"
        x, y = step_field(step, "x"), step_field(step, "y")
        if x is not None and y is not None:
            return f"/cast {skill} {x} {y}"
        return f"/cast {skill}"

    if verb == "settle":
        seconds = step_field(step, "seconds", step["argument"] if step["argument"] is not None else DEFAULT_SETTLE)
        if as_number(seconds) is None or seconds < 0:
            raise LabError(f"step {step['index']}: settle needs nonnegative seconds")
        return None

    if verb == "damage":
        if isinstance(step["argument"], bool):
            return "/damage clear" if step["argument"] else "/damage"
        return "/damage clear" if step_field(step, "clear", False) else "/damage"

    if verb == "collect":
        collect = step["argument"] if isinstance(step["argument"], dict) else step["fields"]
        if not collect and isinstance(step["argument"], str):
            collect = {"filter": step["argument"]}
        count, wanted = collect.get("count"), collect.get("filter")
        if count is None and wanted:
            count = DEFAULT_DAMAGE_COUNT
        text = "/damage"
        if count is not None:
            text += f" {count}"
        if wanted:
            text += f" {wanted}"
        return text

    if verb == "expect":
        return None

    if verb == "tp":
        target = step_field(step, "target", step["argument"])
        if isinstance(target, str) and re.fullmatch(r"[+-]?\d+\s+[+-]?\d+", target.strip()):
            return "tp " + target.strip()          # a cell: the bridge's own tp request, as the runner sends it
        if target is not None and not isinstance(target, str):
            target = str(target)
        if target:
            # `/tp` takes a decimal or 0x id as readily as a name (Rules.TeleportTargets), so a reference resolves.
            return f"/tp {resolve_references(target, refs, dry_run_refs=dry_run_refs)}"
        x, y = step_field(step, "x"), step_field(step, "y")
        if x is None or y is None:
            raise LabError(f"step {step['index']}: tp needs a target, or x and y")
        return f"/tp {x} {y}"

    raise LabError(f"step {step['index']}: unknown verb {verb!r}")


# --- the bridge client --------------------------------------------------------------------------------------------

class LabClient:
    """The persistent JSON-lines bridge: one `client_control.py --session <s> --stdio` child, one request per line, one
    result line back. Every request and reply is timed in milliseconds."""

    def __init__(self, python, session, control=CONTROL, root=ROOT, process=None):
        self.python, self.session, self.control, self.root = str(python), session, Path(control), Path(root)
        self.process = process
        self._spawn = process is None
        self.log = []

    def start(self):
        if self.process is None:
            command = [self.python, str(self.control), "--session", self.session, "--stdio"]
            # The bridge's stdio pipe is the Windows locale encoding, not UTF-8, and a real character's name (which
            # `character:play` and the ledger's file name both take) is not ASCII. PYTHONUTF8 makes the child use
            # UTF-8 on its pipes whatever the locale is; requests go out ASCII-escaped as well (see execute).
            env = {**os.environ, "PYTHONIOENCODING": "utf-8", "PYTHONUTF8": "1"}
            try:
                self.process = subprocess.Popen(command, cwd=str(self.root), stdin=subprocess.PIPE,
                                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                                encoding="utf-8", errors="replace", env=env, bufsize=1)
            except OSError as error:
                raise LabError(f"cannot start {self.python}: {error}") from error
        return self

    def execute(self, request):
        """One request, one result. Raises LabError when the bridge or the client refuses it (the result's `ok` is
        false), so the caller's retry sees a plain failure."""
        self.start()
        request = dict(request)
        request.setdefault("command", "game:status")
        request.setdefault("wait", 1.5)
        started = time.monotonic()
        try:
            # ensure_ascii (the default) escapes a non-ASCII name such as a character's to \uXXXX, so the request line
            # is pure ASCII and survives the bridge's locale-encoded stdin whatever that locale is.
            line = json.dumps(request)
            self.process.stdin.write(line + "\n")
            self.process.stdin.flush()
        except (OSError, ValueError) as error:
            raise LabError(f"the bridge's stdin closed: {error}") from error
        line = self.process.stdout.readline()
        elapsed = round((time.monotonic() - started) * 1000)
        if not line:
            code = self.process.poll()
            detail = (self.process.stderr.read() or "").strip().splitlines()
            raise LabError(f"the bridge exited (code {code}) without a reply"
                           + (f": {one_line(detail[-1])}" if detail else ""))
        try:
            result = json.loads(line)
        except ValueError as error:
            raise LabError(f"the bridge answered something that is not JSON: {error}") from error
        if not isinstance(result, dict):
            raise LabError("the bridge answered something that is not a JSON object")
        if not result.get("ok", False):
            raise LabError(one_line(result.get("error") or result))
        self.log.append({"at": datetime.datetime.now().isoformat(timespec="milliseconds"), "request": request,
                         "elapsedMs": elapsed, "result": result.get("result")})
        return result.get("result")

    def command(self, text, wait=1.5):
        """A slash command; its reply lines (the chat lines the Gateway sent this client)."""
        result = self.execute({"command": "command", "text": text, "wait": wait})
        replies = None if not isinstance(result, dict) else result.get("replies")
        if replies is None:
            raise LabError("no Gateway replies: the servers must be started by this checkout's live.ps1 "
                           "(logs/live-gateway.log)")
        return list(replies)

    def records(self, path):
        """The ledger a /damage reply named. Overridden by the self-test's fake client."""
        return load_records(path)

    def close(self):
        if self._spawn and self.process is not None:
            try:
                self.process.stdin.close()
            except OSError:
                pass
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.process.kill()


# --- expectations -------------------------------------------------------------------------------------------------

def matches(record, checks):
    """True when `record` satisfies every `field: value` check (a list means any of them)."""
    for field, wanted in checks.items():
        if field not in record:
            return False
        if isinstance(wanted, list):
            if record[field] not in wanted:
                return False
        elif record[field] != wanted:
            return False
    return True


def describe_record(record, field=None):
    """A record named the way a failure message needs it: what it was, what it was against, and the value that set the
    bound ("incoming MonsterAttack -> 吴沃蓝 taken=0 (blocked)")."""
    target = record.get("target")
    if not target and isinstance(record.get("targetId"), int):
        target = f"0x{record['targetId']:x}"
    text = f"{record.get('kind', '?')} {record.get('skill', '?')} -> {target or '?'}"
    if field is not None and field in record:
        text += f" {field}={record[field]}"
    if record.get("outcome"):
        text += f" ({record['outcome']})"
    return text


def numeric_metric(records, field, op):
    """`field`'s min/max/sum/avg over `records`, or None when no record has it."""
    values = [value for value in (as_number(record.get(field)) for record in records) if value is not None]
    if not values:
        return None
    if op == "sum":
        return sum(values)
    if op == "avg":
        return sum(values) / len(values)
    if op == "min":
        return min(values)
    return max(values)


def evaluate_expectation(records, spec, where="expect"):
    """One expectation against `records`, as a report dict: the checks, the measured values and the verdict.

    Keys: `hits`/`count`/`records` (a count), every ledger field name (an exact value, or a list of them),
    `<numeric field>_min|_max|_sum|_avg`, `taken_le_raw`, and `note`. An expectation that names a value or a bound no
    record has fails: it is a statement about the records that were collected."""
    if not isinstance(spec, dict) or not spec:
        raise LabError(f"{where} needs an object of checks")
    spec = {key: value for key, value in spec.items() if key != "note"}
    if not spec:
        raise LabError(f"{where} needs at least one check besides `note`")

    count_checks, value_checks, numeric_checks, flags = {}, {}, {}, {}
    for key, wanted in spec.items():
        name = FIELD_ALIASES.get(key, key)
        if name in COUNT_KEYS:
            count_checks[key] = wanted
        elif name in NUMERIC_KEYS:
            count_checks.setdefault("_numeric", {})[key] = wanted
        elif name in INVARIANTS:
            flags[name] = wanted
        elif name in VALUE_FIELDS:
            if wanted is not None:                # `"critical": null` means "do not check it"
                value_checks[name] = wanted
        else:
            raise LabError(f"{where}: unknown check {key!r} (values: {', '.join(VALUE_FIELDS)}, critical; "
                           f"counts: {', '.join(COUNT_KEYS)}; bounds: <field>_min|_max|_sum|_avg, or "
                           f"<field>: {{min: …, max: …}})")
    numeric_checks = count_checks.pop("_numeric", {})
    if not (count_checks or value_checks or numeric_checks or flags):
        raise LabError(f"{where}: nothing to check (every key was `null` or only `note`)")

    checks, failures = [], []
    # Which records each kind of check reads (Agents/docs/dev/combat-lab.md, "Which records a check reads"):
    #   * every value check is judged on its own ("at least one collected record carries this value");
    #   * those checks together select the subset that the count checks and the bounds measure — "the two outgoing
    #     hits on that monster, and what they took" — so a blocked incoming hit with taken 0 cannot dominate a bound
    #     that names `kind: outgoing`;
    #   * with no value check there is nothing to select, so counts, bounds and invariants read everything collected.
    subset = [record for record in records if matches(record, value_checks)]
    considered = subset if value_checks else records

    for key, wanted in count_checks.items():
        actual = len(considered)
        checks.append({"check": key, "wanted": wanted, "actual": actual})
        if actual != wanted:
            failures.append(f"{key}: wanted {wanted}, got {actual}")

    for key, wanted in value_checks.items():
        accepted = wanted if isinstance(wanted, list) else [wanted]
        present = [record for record in records if record.get(key) in accepted]
        actual = [record.get(key) for record in records if key in record]
        checks.append({"check": key, "wanted": wanted, "actual": actual,
                       "records": len(present)})
        if not present:
            failures.append(f"{key}: wanted {wanted}, but no record has it (the records have {actual})")

    for key, wanted in numeric_checks.items():
        field, op = NUMERIC_KEYS[key]
        actual = numeric_metric(considered, field, op)
        if isinstance(wanted, dict):
            low, high = as_number(wanted.get("min")), as_number(wanted.get("max"))
            ok = actual is not None and (low is None or actual >= low) and (high is None or actual <= high)
            text = f"{low if low is not None else '-'}..{high if high is not None else '-'}"
        else:
            bound = as_number(wanted)
            if bound is None:
                raise LabError(f"{where}: {key} needs a number")
            ok = actual is not None and (actual <= bound if op == "max" else actual >= bound)
            text = f"{'<=' if op == 'max' else '>='} {bound}"
        checks.append({"check": key, "wanted": text, "actual": actual, "records": len(considered)})
        if not ok:
            # Name the records the bound read, and for a min/max the one that set the value: "got 0.0" alone leaves
            # the reader hunting the ledger for which hit it was.
            detail = f"over {len(considered)} record(s)" + (
                " matching " + ", ".join(f"{name}={value}" for name, value in value_checks.items())
                if value_checks else " (no value check, so the whole collected set)")
            if op in ("min", "max"):
                ranked = [record for record in considered if as_number(record.get(field)) is not None]
                if ranked:
                    worst = (min if op == "min" else max)(ranked, key=lambda record: as_number(record[field]))
                    detail += f"; {op} set by {describe_record(worst, field)}"
            failures.append(f"{key}: wanted {text}, got {actual if actual is not None else 'no records'} "
                            f"({detail})")

    for name, wanted in flags.items():
        over = [record for record in considered
                if (as_number(record.get("taken")) or 0) > (as_number(record.get("raw")) or 0)]
        broken_life = [record for record in considered
                       if as_number(record.get("lifeBefore")) is not None
                       and as_number(record.get("lifeAfter")) is not None
                       and as_number(record.get("taken")) is not None
                       and abs((as_number(record["lifeBefore"]) - as_number(record["lifeAfter"]))
                               - as_number(record["taken"])) > 0.5]
        # A hit overkills when the resolved damage is at least the life that was left; the ledger must then have
        # clamped `taken` to exactly that life (raw > taken is what an overkill looks like).
        unclamped = []
        for record in considered:
            raw, before, taken = (as_number(record.get("raw")), as_number(record.get("lifeBefore")),
                                  as_number(record.get("taken")))
            if None in (raw, before, taken):
                continue
            if raw >= before and taken != before:
                unclamped.append(record)
            if raw < before and taken != raw:
                unclamped.append(record)
        if name == "taken_le_raw":
            actual, bad = not over, over
            reason = (f"{len(over)} record(s) took more than raw (first: raw {over[0].get('raw')} "
                      f"taken {over[0].get('taken')})") if over else ""
        elif name == "life_arithmetic":
            actual, bad = not broken_life, broken_life
            reason = f"{len(broken_life)} record(s) with lifeAfter != lifeBefore - taken" if broken_life else ""
        else:  # overkill_clamped: overkill => taken == lifeBefore; no overkill => taken == raw
            actual, bad = not unclamped, unclamped
            reason = (f"{len(unclamped)} record(s) where taken is neither raw nor the target's remaining life "
                      f"(first: raw {unclamped[0].get('raw')} before {unclamped[0].get('lifeBefore')} "
                      f"taken {unclamped[0].get('taken')})") if unclamped else ""
        checks.append({"check": name, "wanted": wanted, "actual": actual})
        if bool(wanted) and bad:
            failures.append(f"{name}: {reason}")

    if failures and records and len(refusals(records)) == len(records):
        # Nothing here is a damage result: the ledger holds only refusals, so a `kind: outgoing` that failed is the
        # skill never having fired, not a formula that produced the wrong number.
        failures.append(f"every record is a refusal ({refusal_summary(records)}): the skill never fired, so there is "
                        "no damage result to compare with a formula")
    return {"checks": checks, "records": len(subset), "passed": not failures, "failures": failures,
            "note": spec.get("note")}

# --- the runner ---------------------------------------------------------------------------------------------------

class Runner:
    """Walks a scenario's steps against one session, logging every request and reply and collecting the ledger records
    each `collect`/`damage` step names. The client is injectable (the self-test uses a fake)."""

    def __init__(self, scenario, client=None, out_dir=LOGS, session=None, clock=time.monotonic, sleep=time.sleep,
                 dry_run=False, character=None, cleanup=None, allow_dead=None):
        self.scenario = scenario
        self.name = scenario.get("name") or "scenario"
        self.session = session or scenario.get("session") or "default"
        self.character = character or scenario.get("character")
        # A scenario may keep its monsters ("cleanup": false) and may run on a dead character ("allowDead": true).
        self.cleanup_monsters = scenario.get("cleanup", True) if cleanup is None else cleanup
        self.allow_dead = bool(scenario.get("allowDead", False)) if allow_dead is None else bool(allow_dead)
        self.client = client
        self.out_dir = Path(out_dir)
        self.clock, self.sleep, self.dry_run = clock, sleep, dry_run
        self.records = []
        self.spawned = []          # every monster id a spawn step learned, for the end-of-run cleanup
        self.refs = {}             # "primary"/"spawn1" -> {"id", "label", "step"}, what a `$name` resolves to
        self.last_cast = None      # the last cast step: the collect after it explains its own empty ledger
        self.report = {"lab": VERSION, "scenario": self.name, "description": scenario.get("description", ""),
                       "session": self.session, "character": self.character,
                       "started": datetime.datetime.now().isoformat(timespec="seconds"),
                       "dryRun": bool(dry_run), "steps": [], "records": [], "expectations": []}

    # -- before and after the steps -------------------------------------------------------------------------------
    def preflight(self):
        """Read the character's own state before anything is cast.

        A dead character's skill use is dropped silently: `/cast` still answers its receipt and no record appears, so
        the only symptom four steps later is "0 records". This is a session problem, not an expectation, so it raises
        LabError (exit 2) unless the scenario says `"allowDead": true`. A `/state` the runner cannot read is a loud
        warning and not a stop: the run may still be fine."""
        try:
            state = parse_state(self.client.command("/state", wait=2.0))
        except Exception as error:
            self.report["preflight"] = {"checked": False, "error": one_line(str(error))}
            say(f"lab: warning: /state did not answer ({error}); cannot tell whether the character is alive", sys.stderr)
            return None
        if state is None:
            self.report["preflight"] = {"checked": False, "note": "no parseable STATE line"}
            say("lab: warning: /state gave no parseable STATE line; cannot tell whether the character is alive",
                sys.stderr)
            return None
        life = as_number(state.get("life"))
        dead = bool(state.get("dead"))
        self.report["preflight"] = {"checked": True, "life": state.get("life"), "dead": state.get("dead"),
                                    "area": state.get("area"), "character": state.get("character"),
                                    "allowDead": self.allow_dead}
        if dead or (life is not None and life <= 0):
            if not self.allow_dead:
                raise LabError(f"the character is dead (life {state.get('life')}, dead {state.get('dead')}): heal or "
                               "revive it first, because nothing casts while dead and a dropped cast leaves no record "
                               "(set \"allowDead\": true in the scenario to run anyway)")
            say("lab: warning: the character is dead and this scenario allows it; casts will not produce records",
                sys.stderr)
        return state

    def cleanup(self):
        """Take away what this run spawned.

        A monster left standing attacks the character between runs, and once several of the same variety are alive a
        later `/cast <skill> <name>` is refused as ambiguous — that is how repeated runs accumulate monsters and can
        kill a level-100 character with its own test subjects. `/despawn all` would also remove monsters this run did
        not place, so each id a spawn step learned is despawned by id. A failure is a warning, not a step error."""
        if not self.spawned:
            self.report["cleanup"] = {"monsters": [], "results": []}
            return
        if not self.cleanup_monsters:
            self.report["cleanup"] = {"monsters": list(self.spawned), "skipped": True}
            say(f"lab: warning: {len(self.spawned)} spawned monster(s) were left standing (cleanup is off): "
                f"{', '.join(self.spawned)}", sys.stderr)
            return
        results = []
        for monster in self.spawned:
            try:
                replies = self.client.command(f"/despawn {monster}", wait=0.5)
                results.append({"id": monster, "ok": True, "replies": [one_line(reply) for reply in replies]})
            except Exception as error:
                results.append({"id": monster, "ok": False, "error": one_line(str(error))})
        failed = [entry["id"] for entry in results if not entry["ok"]]
        self.report["cleanup"] = {"monsters": list(self.spawned), "results": results, "failed": failed}
        if failed:
            say(f"lab: warning: these spawned monsters could not be despawned: {', '.join(failed)} - they stay in the "
                "area and will attack the next run", sys.stderr)

    # -- one step ------------------------------------------------------------------------------------------------
    def run_step(self, step):
        entry = {"index": step["index"], "verb": step["verb"], "command": None, "attempts": [],
                 "records": 0, "expectations": []}
        at = datetime.datetime.now().isoformat(timespec="milliseconds")
        started = self.clock()
        try:
            entry["command"] = step_command(step, self.refs, dry_run_refs=spawn_refs(self.scenario.get("steps") or [])
                                            if self.dry_run else None)
        except LabError as error:  # a step that cannot even be built is one failed, timed attempt
            entry["attempts"].append({"attempt": 1, "at": at, "ok": False,
                                      "elapsedMs": round((self.clock() - started) * 1000), "error": str(error)})
            entry["error"] = str(error)
            return entry
        if self.dry_run:
            entry["attempts"].append({"attempt": 0, "at": at, "dryRun": True, "command": entry["command"]})
            return entry

        once = step["fields"].get("retries", 1) == 0 or step["fields"].get("retry") is False
        for attempt in range(1 if once else 2):
            if attempt:
                self.sleep(RETRY_SETTLE)  # a short settle, then one more try
            at = datetime.datetime.now().isoformat(timespec="milliseconds")
            started = self.clock()
            try:
                outcome = self.perform(step)
                entry["attempts"].append({"attempt": attempt + 1, "at": at, "ok": True,
                                          "elapsedMs": round((self.clock() - started) * 1000), **outcome})
                break
            except Exception as error:  # a failed step retries once; the run keeps going either way
                entry["attempts"].append({"attempt": attempt + 1, "at": at, "ok": False,
                                          "elapsedMs": round((self.clock() - started) * 1000),
                                          "error": one_line(f"{type(error).__name__}: {error}")})
        if not entry["attempts"][-1].get("ok"):
            entry["error"] = entry["attempts"][-1].get("error", "step failed")
        return entry

    def wait_area(self, area, before=None, timeout=60.0):
        """Wait for the instance a `/goto` made, not merely for the area id.

        A `/goto` into an area with a seed forces a new instance while the id stays the same; the client is loading
        meanwhile, and a chat command sent then fails with "Chat requires an active area". The bridge's `area:wait`
        with `from` waits for exactly that (its `arrived`: the instance left state 3 and returned, or the id changed).
        An older bridge without the request gets the same rule polled here."""
        request = {"command": "area:wait", "timeout": timeout}
        if area:
            request["area"] = area
        if before:
            request["from"] = before
        try:
            return self.client.execute(request)
        except LabError as error:
            if "Unknown command" not in str(error):
                raise
        previous = (before or {}).get("area")
        started, left = self.clock(), False
        while self.clock() - started < timeout:
            status = self.client.execute({"command": "game:status"})
            entered = status.get("screen", "game") == "game" and status.get("instanceState") == 3
            current = status.get("area")
            if not entered:
                left = True
            else:
                changed = left or (current is not None and previous is not None and current != previous)
                if (area is None or current is None or current == area) and (before is None or changed):
                    return status
            self.sleep(0.3)
        raise LabError(f"the area {area or 'change'} was not entered within {timeout:g} s")

    def perform(self, step):
        """The step's own work; returns the attempt's fields. Raises on failure (the retry catches it)."""
        verb, fields = step["verb"], step["fields"]
        command = step_command(step, self.refs)

        if verb == "settle":
            seconds = fields.get("seconds", step["argument"] if step["argument"] is not None else DEFAULT_SETTLE)
            self.sleep(float(seconds))
            return {"sent": None, "replies": [], "slept": float(seconds)}

        if verb == "expect":
            spec = step["argument"] if isinstance(step["argument"], dict) else fields
            return {"sent": None, "replies": [],
                    "expectation": evaluate_expectation(self.records, resolve_expectation(spec, self.refs),
                                                        where=f"step {step['index']}")}

        if command is None:
            raise LabError(f"step {step['index']}: {verb} sends no command")

        if verb == "collect":
            return self.collect(step, command)

        # A /goto's wait needs the status before the travel, so take it before the command goes out.
        before = None
        if verb == "goto":
            try:
                before = self.client.execute({"command": "game:status"})
            except LabError:
                before = None

        replies = self.client.command(command, wait=fields.get("wait", 1.5))
        outcome = {"sent": command, "replies": [one_line(reply) for reply in replies]}
        # Which `$name`s this step used and what they resolved to: the report then says which monster it acted on.
        used = {name: self.refs[name]["id"] for name in step_references(step) if name in self.refs}
        if used:
            outcome["resolved"] = used

        if verb == "goto":
            area = step_field(step, "area", step["argument"] if isinstance(step["argument"], str) else None)
            outcome["arrived"] = self.wait_area(area, before, float(step_field(step, "timeout", 60.0)))

        if verb == "cast":
            # A refusal is not a cast: the run must not report ok (the receipt text is the reason). Recorded either
            # way, so the collect that follows can explain an empty ledger.
            refused = cast_refusal(replies)
            skill = step_field(step, "skill", step["argument"] if isinstance(step["argument"], str) else None)
            self.last_cast = {"step": step["index"], "skill": skill, "refused": refused}
            if refused:
                outcome["refused"] = refused
                if not step_field(step, "allowRefused", False):
                    raise LabError(f"the cast was refused, so nothing was used: {refused}")

        if verb == "spawn":
            # /spawn_mob answers with nothing useful; the new monster's own id and label come from /mobs, and the
            # caller's `/mobs` before the spawn says which entry is new.
            mobs = parse_mobs(self.client.command("/mobs"))
            outcome["replies"].extend(one_line(reply) for reply in mobs.values())
            before_mobs = step["fields"].get("_before") or set()
            outcome["mobs"] = {f"0x{key:x}": label for key, label in mobs.items()}
            outcome["spawned"] = [f"0x{key:x}" for key in mobs if key not in before_mobs]
            if not outcome["spawned"]:
                raise LabError(f"{command!r} added no monster to /mobs (monsters: "
                               f"{outcome['mobs'] or 'none'}, before: "
                               f"{[f'0x{key:x}' for key in sorted(before_mobs)] or 'none'})")
            self.spawned.extend(outcome["spawned"])
            # Name what was placed, so a later step can say `$primary` (or `$spawn<N>`) instead of a label that two
            # monsters of the same variety share.
            ordinal = step.get("spawnOrdinal") or len(self.spawned)
            names = [f"spawn{ordinal}"]
            named = step_field(step, "ref")
            if named:
                names.append(str(named).lstrip("$"))
            for monster in outcome["spawned"]:
                entry = {"id": monster, "numeric": int(monster, 16), "label": outcome["mobs"].get(monster),
                         "step": step["index"]}
                for name in names:
                    self.refs[name] = entry
            outcome["references"] = {name: self.refs[name]["id"] for name in names}
        if step["fields"].get("settle"):
            self.sleep(float(step["fields"]["settle"]))
        return outcome

    def collect(self, step, command):
        """`/damage`, then read the ledger its reply names and keep the records.

        The path in that reply travels back through the Gateway log, so it can carry a mangled character (the log is
        decoded best-effort) or lag the file. It is therefore a hint, not the truth: the runner falls back to the
        ledger the character's own name gives, and if neither file exists the step fails naming both candidates and
        the reply line instead of a bare Errno."""
        fields = step["fields"]
        replies = self.client.command(command, wait=fields.get("wait", 1.5))
        outcome = {"sent": command, "replies": [one_line(reply) for reply in replies]}
        named, claimed = reply_path(replies)
        if named is None:
            raise LabError(f"{command!r} named no ledger file (expected 'damage: N record(s) -> "
                           f"logs/damage-<character>.jsonl'): {outcome['replies']}")
        fallback = ledger_for(self.character)
        candidates = [named] + ([fallback] if fallback and fallback != named else [])
        records, tried, path = None, [], named
        for candidate in candidates:
            try:
                records, path = self.client.records(candidate), candidate
                break
            except LabError as error:
                tried.append(f"{candidate} ({error})")
        if records is None:
            raise LabError("cannot read the damage ledger: " + "; ".join(tried)
                           + " | the reply said: " + " / ".join(outcome["replies"]))
        # What the ledger itself holds, before this step's own filter: a refusal the filter drops is still the answer.
        file_records = records
        notes = []
        if path != named:
            notes.append(f"the reply's path {named!r} did not exist; read {path!r} instead")
        # `collect`'s own count/filter, however the step wrote them: the object form, the compact argument, or the
        # fields of the long form. `filter` is the Gateway's substring filter on skill or target, applied here too so
        # an expectation sees exactly the records it names.
        spec = step["argument"] if isinstance(step["argument"], dict) else step["fields"]
        if not spec and isinstance(step["argument"], str):
            spec = {"filter": step["argument"]}
        wanted = spec.get("filter")
        if wanted:
            text = str(wanted).lower()
            records = [record for record in records
                       if text in str(record.get("skill", "")).lower()
                       or text in str(record.get("target", "")).lower()]
        # The reply says how many records it wrote; trust the file, but honour a count the step asked for.
        count = spec.get("count")
        if count is not None and len(records) > int(count):
            records = records[-int(count):]
        kept_refusals = refusals(records)
        if kept_refusals and len(kept_refusals) == len(records):
            # Every record is a refusal: the skill never fired. Saying so here is the whole point of the kind.
            notes.append(f"{refusal_summary(records)} (no outgoing hit)")
        if not records and self.last_cast:
            # An empty collect right after a cast: the ledger's own refusal is the answer when there is one, and the
            # three usual guesses only when there is not.
            if refusals(file_records):
                notes.append(f"the cast at step {self.last_cast['step']} ({self.last_cast['skill']}) produced no "
                             f"record, and the ledger holds {refusal_summary(file_records)}: the skill never fired")
            else:
                notes.append(f"the cast at step {self.last_cast['step']} ({self.last_cast['skill']}) produced no "
                             "record"
                             + (f"; it was refused: {self.last_cast['refused']}" if self.last_cast["refused"] else "")
                             + "; check the refusal text, the character's life (a dead character's cast is dropped), "
                               "or the skill's reach")
        if notes:
            outcome["note"] = "; ".join(notes)
            outcome["replies"].extend(one_line(note) for note in notes)
        if kept_refusals:
            outcome["refusals"] = [{"skill": record.get("skill"), "reason": record.get("outcome"),
                                    "target": record.get("target")} for record in kept_refusals]
        outcome.update({"recordsPath": str(path), "claimed": claimed, "records": records})
        return outcome

    # -- whole scenario ------------------------------------------------------------------------------------------
    def run(self):
        spawn_ordinal = 0
        for number, raw in enumerate(self.scenario.get("steps") or [], 1):
            step = normalize_step(raw, number)
            if step["verb"] == "spawn":
                # The Nth spawn step is `$spawn<N>`, whatever happens to the steps before it.
                spawn_ordinal += 1
                step["spawnOrdinal"] = spawn_ordinal
                if not self.dry_run and self.client is not None:
                    step["fields"]["_before"] = set(parse_mobs(self.client.command("/mobs")).keys())
            entry = self.run_step(step)
            self.report["steps"].append(entry)
            self._apply(entry)
        if not self.dry_run and self.client is not None:
            self.cleanup()   # a monster left standing poisons the next run and can kill the character
        self.report["spawned"] = {name: dict(value) for name, value in self.refs.items()}
        self.report["finished"] = datetime.datetime.now().isoformat(timespec="seconds")
        self.report["summary"] = self.summary()
        return self.report

    def _apply(self, entry):
        """Fold a step's last attempt into the run: the records it collected and the expectations it checked."""
        attempt = (entry["attempts"] or [{}])[-1]
        if attempt.get("recordsPath") and attempt.get("records") is not None:
            self.records.extend(attempt["records"])
            self.report["records"].extend(attempt["records"])
            entry["records"] = len(attempt["records"])
        # The step's own note and the refusals it kept are what a reader looks for first, so they belong on the step.
        if attempt.get("note"):
            entry["note"] = attempt["note"]
        if attempt.get("refusals"):
            entry["refusals"] = attempt["refusals"]
        if attempt.get("resolved"):
            entry["resolved"] = attempt["resolved"]
        if attempt.get("references"):
            entry["references"] = attempt["references"]
        if attempt.get("expectation"):
            self.report["expectations"].append({"step": entry["index"], **attempt["expectation"]})
            entry["expectations"] = [attempt["expectation"]["passed"]]

    def summary(self):
        expectations = self.report["expectations"]
        failed = [expectation for expectation in expectations if not expectation["passed"]]
        errors = [step for step in self.report["steps"] if step.get("error")]
        summary = {"steps": len(self.report["steps"]), "records": len(self.records),
                   "expectations": len(expectations), "expectationsPassed": len(expectations) - len(failed),
                   "expectationsFailed": len(failed), "stepErrors": len(errors)}
        refused = refusals(self.records)
        if refused:
            summary["refusals"] = len(refused)
            summary["refusedReasons"] = refusal_summary(self.records)
            if len(refused) == len(self.records):
                # The whole ledger is refusals: the skill never fired, so there is no damage result to compare with
                # a formula at all. Named in the summary so a failed `kind: outgoing` is not read as a formula bug.
                summary["onlyRefusals"] = True
                summary["finding"] = (f"{summary['refusedReasons']} (no outgoing hit): the skill never fired, which is "
                                      "not a damage result")
        return summary

    def exit_code(self):
        """0 when every expectation passed and no step failed; 1 otherwise. 2 is reserved for session and argument
        errors, which are raised before a run starts."""
        summary = self.report.get("summary") or self.summary()
        return 0 if not summary["expectationsFailed"] and not summary["stepErrors"] else 1

    def write_report(self, path=None):
        path = Path(path) if path else self.out_dir / f"lab-{now_stamp()}.json"
        if not path.is_absolute():
            path = ROOT / path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(self.report, indent=2, ensure_ascii=True), encoding="utf-8")
        return path


# --- console output -----------------------------------------------------------------------------------------------

def console_table(report):
    """The short human table of a finished run."""
    lines = [f"lab {report.get('scenario', 'scenario')}  session {report.get('session')}  "
             f"{report.get('started')}" + ("  (dry run)" if report.get("dryRun") else "")]
    if report.get("description"):
        lines.append("  " + one_line(report["description"], 160))
    if report.get("character"):
        lines.append("  character " + str(report["character"]))
    width = max([len(str(step["index"])) for step in report.get("steps", [])] or [1])
    for step in report.get("steps", []):
        attempt = (step.get("attempts") or [{}])[-1]
        if attempt.get("dryRun"):
            mark, took = "-", ""
        else:
            mark = "ok" if attempt.get("ok") else "FAIL"
            took = f"{attempt.get('elapsedMs', '')} ms"
        text = step.get("command") or f"({step['verb']})"
        detail = ""
        if step["verb"] == "expect":
            failed = [e for e in report["expectations"] if e["step"] == step["index"] and not e["passed"]]
            passed = [e for e in report["expectations"] if e["step"] == step["index"] and e["passed"]]
            detail = "passed" if passed and not failed else ("failed: " + "; ".join(
                reason for expectation in failed for reason in expectation["failures"]) if failed
                else "not evaluated")
        elif step.get("records"):
            detail = f"{step['records']} record(s)"
        elif step.get("error"):
            detail = one_line(step["error"], 120)
        if step.get("refusals") and "refused:" not in detail:
            detail = (detail + "  " if detail else "") + one_line(
                "refused: " + "; ".join(f"{entry['skill']} - {entry['reason']}" for entry in step["refusals"]), 90)
        lines.append(f"  {step['index']:>{width}}  {step['verb']:<8} {text:<44} {mark:<4} {took:<9} {detail}")
    summary = report.get("summary") or {}
    lines.append(f"  summary: {summary.get('steps', 0)} step(s), {summary.get('records', 0)} record(s), "
                 f"{summary.get('expectationsPassed', 0)}/{summary.get('expectations', 0)} expectation(s) passed, "
                 f"{summary.get('stepErrors', 0)} step error(s)")
    if summary.get("finding"):
        lines.append("  " + summary["finding"])
    elif summary.get("refusedReasons"):
        lines.append("  refused: " + summary["refusedReasons"])
    for expectation in report.get("expectations", []):
        if not expectation["passed"]:
            lines.append(f"  expectation (step {expectation['step']}) FAILED: "
                         + "; ".join(expectation["failures"]))
    return "\n".join(lines)


def listing(directory=SCENARIOS):
    """`--list`: one line per scenario file, with its step count and description."""
    lines = []
    for path in scenario_files(directory):
        try:
            scenario = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as error:
            lines.append(f"{path.stem:<28} (unreadable: {one_line(error, 80)})")
            continue
        steps = len(scenario.get("steps") or [])
        lines.append(f"{path.stem:<28} {steps:>3} step(s)  {one_line(scenario.get('description', ''), 90)}")
    return "\n".join(lines) or f"(no scenario files in {directory})"


def scenario_files(directory=SCENARIOS):
    return sorted(Path(directory).glob("*.json"))


def find_scenario(name, directory=SCENARIOS):
    """A scenario given as a path, a bare file name, or a stem in the scenarios directory."""
    candidate = Path(name)
    for path in (candidate, Path(directory) / candidate, Path(directory) / f"{name}.json"):
        if path.is_file():
            return path
    raise LabError(f"no scenario {name!r} (looked for a file, and in {directory})")


def dry_run_plan(scenario, session, cleanup=None, allow_dead=None):
    """The exact commands a scenario would send, in order, without touching a client. `cleanup`/`allow_dead` are the
    command line's overrides; None means the scenario's own setting."""
    steps = scenario.get("steps") or []
    placeholders = spawn_refs(steps)   # a dry run cannot learn an id from /mobs, so a reference prints as a promise
    ordinal = 0
    lines = [f"session {session}: {scenario.get('name')} ({len(steps)} steps)"]
    dead_ok = bool(scenario.get("allowDead", False)) if allow_dead is None else bool(allow_dead)
    if dead_ok:
        lines.append("  (preflight: skipped, this run allows a dead character)")
    else:
        lines.append("  (preflight: /state; the run stops with exit 2 if the character is dead)")
    for number, raw in enumerate(steps, 1):
        step = normalize_step(raw, number)
        if step["verb"] == "spawn":
            ordinal += 1
        command = step_command(step, dry_run_refs=placeholders)
        fields = step["fields"]
        note = []
        if step["verb"] == "settle" and command is None:
            seconds = fields.get("seconds", step["argument"] if step["argument"] is not None else DEFAULT_SETTLE)
            command = f"(wait {seconds} s)"
            note.append("wall clock, live client only")
        if step["verb"] == "spawn":
            note.append("then /mobs, to learn the new monster's id and label")
            if ordinal:
                named = step_field(step, "ref")
                note.append(f"answers to ${str(named).lstrip('$')} and $spawn{ordinal} once its id is known"
                            if named else f"answers to $spawn{ordinal} once its id is known")
        if step["verb"] == "cast":
            note.append("a receipt that does not start with 'cast ' fails the step as a refusal")
        if step["verb"] == "collect":
            note.append("then read the jsonl the reply names and keep its records")
        if step["verb"] == "expect":
            spec = step["argument"] if isinstance(step["argument"], dict) else fields
            note.append("assert " + one_line(json.dumps(spec, ensure_ascii=True), 110))
        if fields.get("retries", 1) == 0 or fields.get("retry") is False:
            note.append("no retry")
        if fields.get("note"):
            note.append(str(fields["note"]))
        lines.append(f"  {number:>3}  {command if command is not None else '(no command)':<44} "
                     f"{step['verb']:<8} " + "; ".join(note))
    keep = bool(scenario.get("cleanup", True)) if cleanup is None else bool(cleanup)
    if keep:
        lines.append("  post  /despawn <id> for every monster a spawn step placed")
    else:
        lines.append("  post  (cleanup: off, the spawned monsters are left standing)")
    lines.append("  (nothing was sent: --dry-run starts no bridge and no client)")
    return "\n".join(lines)


# --- self-test ----------------------------------------------------------------------------------------------------

# The synthetic run the self-test drives: a real /damage reply line and a two-line ledger, so the reply parser, the
# ledger reader and every expectation check are exercised with no client and no frida.
SYNTHETIC_REPLY = ["damage 3 record(s) -> logs/damage-TestWitch.jsonl (2 hit, 1 critical, 230 taken)"]
SYNTHETIC_LEDGER = [
    {"at": 1.25, "kind": "outgoing", "skill": "Fireball", "skillId": 14, "targetId": 4101, "target": "Rhoa",
     "level": 4, "rarity": "normal", "raw": 120, "taken": 120, "types": "fire:120", "crit": False,
     "outcome": "hit", "lifeBefore": 200, "lifeAfter": 80, "distance": 12.5},
    {"at": 2.5, "kind": "outgoing", "skill": "Fireball", "skillId": 14, "targetId": 4101, "target": "Rhoa",
     "level": 4, "rarity": "normal", "raw": 260, "taken": 80, "types": "fire:260", "crit": True,
     "outcome": "killed", "lifeBefore": 80, "lifeAfter": 0, "distance": 12.5},
    {"at": 3.0, "kind": "incoming", "skill": "MonsterAttack", "skillId": 257, "targetId": 1, "target": "TestWitch",
     "level": 5, "rarity": "normal", "raw": 30, "taken": 30, "types": "physical:30", "crit": False,
     "outcome": "hit", "lifeBefore": 300, "lifeAfter": 270, "distance": 3.0},
]


SYNTHETIC_REFUSED = [
    {"at": 4.0, "kind": "refused", "skill": "SplittingSteel", "skillId": 611, "targetId": 4101,
     "target": "Rhoa Mare@80", "level": 80, "rarity": "normal", "raw": 0, "taken": 0, "types": "", "crit": False,
     "outcome": "no projectile recipe/metadata", "lifeBefore": 4000, "lifeAfter": 4000, "distance": 3.0}
]
SYNTHETIC_REFUSED_REPLY = ["damage 1 record(s) -> logs/damage-TestWitch.jsonl (0 hit, 1 refused, 0 critical, 0 taken)"]

# The live fight that exposed the bounds defect: one area attack hitting two monsters (two outgoing records), and
# incoming hits — one of them blocked with taken 0, which dragged a whole-set `taken_min` to 0 while the records the
# expectation actually named had a minimum of 19.
FIGHT = [
    {"at": 1.0, "kind": "outgoing", "skill": "Perforate", "skillId": 700, "targetId": 0x80000d4,
     "target": "Rhoa Mare@20", "level": 20, "rarity": "normal", "raw": 27, "taken": 27, "types": "physical:27",
     "crit": False, "outcome": "hit", "lifeBefore": 144, "lifeAfter": 117, "distance": 1.0},
    {"at": 1.1, "kind": "outgoing", "skill": "Perforate", "skillId": 700, "targetId": 0x80000d5,
     "target": "Rhoa Mare@20", "level": 20, "rarity": "normal", "raw": 19, "taken": 19, "types": "physical:19",
     "crit": False, "outcome": "hit", "lifeBefore": 100, "lifeAfter": 81, "distance": 3.0},
    {"at": 2.0, "kind": "incoming", "skill": "MonsterAttack", "skillId": 257, "targetId": 0x1000, "target": "吴沃蓝",
     "level": 20, "rarity": "normal", "raw": 29, "taken": 0, "types": "physical:29", "crit": False,
     "outcome": "blocked", "lifeBefore": 900, "lifeAfter": 900, "distance": 1.0},
    {"at": 3.0, "kind": "incoming", "skill": "MonsterAttack", "skillId": 257, "targetId": 0x1000, "target": "吴沃蓝",
     "level": 20, "rarity": "normal", "raw": 30, "taken": 28, "types": "physical:30", "crit": False,
     "outcome": "hit", "lifeBefore": 900, "lifeAfter": 872, "distance": 1.0},
    {"at": 4.0, "kind": "incoming", "skill": "MonsterAttack", "skillId": 258, "targetId": 0x1000, "target": "吴沃蓝",
     "level": 2, "rarity": "normal", "raw": 4, "taken": 3, "types": "physical:4", "crit": False,
     "outcome": "hit", "lifeBefore": 872, "lifeAfter": 869, "distance": 1.0},
]


class FakeClient:
    """A bridge that answers the self-test's requests from the synthetic ledger: no frida, no client. `/spawn_mob`
    appends to its `/mobs` list, so a spawn step really does see a new id, the way a live one would."""

    def __init__(self, records=None, reply=None):
        self.ledger = list(records if records is not None else SYNTHETIC_LEDGER)
        self.reply = list(reply if reply is not None else SYNTHETIC_REPLY)
        self.mobs = []
        self.spawn_count = 0        # each spawn gets its own id, the way two monsters of one variety do
        self.commands = []          # every chat line this fake was asked to send

    def command(self, text, wait=1.5):
        self.commands.append(text)
        if text.startswith("/despawn"):
            self.mobs = [entry for entry in self.mobs if not entry.startswith(text.split()[-1] + " ")]
            return []
        if text.startswith("/spawn_mob"):
            self.spawn_count += 1
            self.mobs.append(f"0x{0x1a2b + self.spawn_count - 1:x} Rhoa Mare@20 (12, 34)")
            return []
        if text.startswith("/mobs"):
            return list(self.mobs) if self.mobs else ["No monsters."]
        if text.startswith("/damage"):
            return list(self.reply)
        return []

    def records(self, path):
        if not str(path).endswith("damage-TestWitch.jsonl"):
            raise LabError(f"the self-test has no ledger for {path}")
        return [dict(record) for record in self.ledger]

    def execute(self, request):
        """Enough of the bridge's other requests for a `goto` step."""
        if request.get("command") == "area:wait":
            return {"screen": "game", "instanceState": 3, "area": request.get("area") or "1_1_2"}
        return {"screen": "game", "instanceState": 3, "area": "1_1_2"}


class DeadCharacter(FakeClient):
    """A client whose `/state` says the character is dead: casts are dropped silently and leave no record."""

    def command(self, text, wait=1.5):
        if text.startswith("/state"):
            self.commands.append(text)
            return ['STATE {"life":0,"dead":true,"area":"1_1_1","character":"TestWitch"}']
        return super().command(text, wait=wait)


class AliveCharacter(FakeClient):
    """A live character with a parseable `/state`."""

    def command(self, text, wait=1.5):
        if text.startswith("/state"):
            self.commands.append(text)
            return ['STATE {"life":1234,"dead":false,"area":"1_1_1","character":"TestWitch"}']
        return super().command(text, wait=wait)


class RefusingCast(FakeClient):
    """A client whose `/cast` is refused the way an ambiguous monster name refuses it."""

    def command(self, text, wait=1.5):
        if text.startswith("/cast"):
            self.commands.append(text)
            return ['"Rhoa" matches 3 monsters: 0x1 (Rhoa Mare@80), 0x2, 0x3']
        return super().command(text, wait=wait)


class CastThenRefused(FakeClient):
    """A bridge whose `/cast` answers a real receipt and whose ledger then holds only a refusal: the skill was built but
    the server refused it before any hit, which is the live Splitting Steel case."""

    def __init__(self):
        super().__init__(records=SYNTHETIC_REFUSED, reply=SYNTHETIC_REFUSED_REPLY)

    def command(self, text, wait=1.5):
        if text.startswith("/cast"):
            self.commands.append(text)
            return ["cast Cyclone (0x01a4) at monster 0x1a2b (Rhoa Mare@80) - the ordinary pipeline may still refuse "
                    "it, read /damage or /state for the outcome"]
        return super().command(text, wait=wait)


class MangledPath(FakeClient):
    """A bridge whose `/damage` reply names a path the Gateway log mangled, while the character's own ledger is real:
    exactly the live failure where the reply's path cannot be read but the records exist under the character's name."""

    def command(self, text, wait=1.5):
        if text.startswith("/damage"):
            return ["damage 3 record(s) -> logs/damage-???.jsonl (2 hit, 1 critical, 230 taken)"]
        return super().command(text, wait=wait)

    def records(self, path):
        if str(path).endswith("damage-张雪峰.jsonl"):
            return [dict(record) for record in SYNTHETIC_LEDGER]
        raise LabError(f"cannot read the damage ledger {path}: [Errno 2] No such file or directory")


class Reloading(FakeClient):
    """A bridge without `area:wait`: `game:status` shows the client leaving the area and coming back, which is exactly
    what `/goto <the area you are standing in> <seed>` does."""

    def __init__(self):
        super().__init__()
        self.statuses = [{"screen": "game", "instanceState": 3, "area": "1_1_2"},
                         {"screen": "game", "instanceState": 4, "area": "1_1_2"},
                         {"screen": "game", "instanceState": 3, "area": "1_1_2"}]
        self.calls = 0

    def execute(self, request):
        if request.get("command") == "area:wait":
            raise LabError("Unknown command: area:wait")
        self.calls += 1
        return self.statuses[min(self.calls - 1, len(self.statuses) - 1)]


# A stand-in for client_control.py that answers in this machine's locale encoding. The runner must still read it: it
# spawns the child with PYTHONUTF8, and this child reports which encoding it got.
FAKE_BRIDGE = r'''
import json, sys, locale
sys.stderr.write(json.dumps({"stdin": sys.stdin.encoding, "preferred": locale.getpreferredencoding(False)}) + "\n")
sys.stderr.flush()
for line in sys.stdin:
    request = json.loads(line)
    path = "logs/damage-" + request.get("text", "x").replace("/damage ", "") + ".jsonl"
    result = {"ok": True, "elapsedMs": 1,
              "result": {"replies": ["damage 1 record(s) -> " + path + " (1 hit, 0 critical, 7 taken)"],
                         "text": request.get("text")}}
    sys.stdout.write(json.dumps(result) + "\n")
    sys.stdout.flush()
'''


def encoding_check(directory):
    """Spawn a real child the way the runner does and check the pipe is UTF-8 and the round trip survives. This is the
    one place the runner's own process handling is exercised, because the trap (a locale pipe) only shows up in one."""
    script = Path(directory) / "fake_bridge.py"
    script.write_text(FAKE_BRIDGE, encoding="utf-8")
    client = LabClient(sys.executable, "selftest", control=script, root=ROOT)
    try:
        client.start()
        result = client.execute({"command": "command", "text": "/damage 张雪峰", "wait": 0.1})
        encoding = client.process.stderr.readline().strip()
        return result, encoding
    finally:
        client.close()
        try:
            script.unlink()
        except OSError:
            pass


def selftest(out=None):
    """Parse a synthetic /damage reply and ledger and check every expectation check, with no client at all."""
    out = sys.stdout if out is None else out
    failures = []

    def check(what, condition, detail=""):
        if not condition:
            failures.append(f"{what}{': ' + detail if detail else ''}")

    named, claimed = reply_path(SYNTHETIC_REPLY)
    check("reply_path", named == "logs/damage-TestWitch.jsonl" and claimed == 3, f"{named!r} {claimed}")
    check("reply_path on a line that names nothing", reply_path(["nothing here"]) == (None, None))

    # A real file round trip, then the same content through a fake client.
    path = LOGS / "lab-selftest-damage.jsonl"
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("\n".join(json.dumps(record, ensure_ascii=True) for record in SYNTHETIC_LEDGER) + "\n",
                        encoding="utf-8")
        records = load_records("logs/lab-selftest-damage.jsonl")
        check("load_records", len(records) == 3, f"{len(records)} records")
        check("the fields survive the round trip",
              all(set(record) >= {"raw", "taken", "types", "crit", "outcome"} for record in records))
    finally:
        try:
            path.unlink()
        except OSError:
            pass

    ourgoing = [record for record in SYNTHETIC_LEDGER if record["kind"] == "outgoing"]
    # The corrected ledger: raw is the resolved damage (after crit and the target's mitigation, before the life clamp),
    # taken is the life actually lost, so the second hit overkills (raw 260, taken 80) and the first does not.
    good = evaluate_expectation(ourgoing, {"hits": 2, "taken_min": 80, "taken_max": 120, "taken_sum": 200,
                                           "raw_min": 120, "raw_max": 260, "critical": None, "target": "Rhoa",
                                           "skill": "Fireball", "taken_le_raw": True})
    check("a good expectation passes", good["passed"], "; ".join(good["failures"]))

    check("a wrong count fails", not evaluate_expectation(ourgoing, {"hits": 3})["passed"])

    wrong = evaluate_expectation(ourgoing, {"taken_min": 100})
    check("a wrong bound fails", not wrong["passed"] and any("taken_min" in f for f in wrong["failures"]),
          "; ".join(wrong["failures"]))

    check("the whole ledger obeys the three invariants",
          evaluate_expectation(SYNTHETIC_LEDGER, {"taken_le_raw": True, "life_arithmetic": True,
                                                  "overkill_clamped": True})["passed"])

    broken = [dict(SYNTHETIC_LEDGER[0]), dict(SYNTHETIC_LEDGER[1])]
    broken[1]["taken"] = 200                     # took less than raw while overkilling: not clamped
    check("a ledger that broke the clamp fails overkill_clamped",
          not evaluate_expectation(broken, {"overkill_clamped": True})["passed"])
    broken[1]["taken"] = 300                     # took more than raw: impossible
    check("a ledger that took more than raw fails taken_le_raw",
          not evaluate_expectation(broken, {"taken_le_raw": True})["passed"])
    broken[1]["taken"] = 80
    broken[1]["lifeAfter"] = 10                  # lifeBefore - lifeAfter no longer equals taken
    check("a broken life pair fails life_arithmetic",
          not evaluate_expectation(broken, {"life_arithmetic": True})["passed"])

    # Two outgoing hits whose every named field holds for both of them: this is what an `expect` states.
    both = [{**ourgoing[0]}, {**ourgoing[1]}]
    for record in both:
        record.update(skill="Fireball", target="Rhoa", kind="outgoing", outcome="hit", crit=False)
    check("value checks apply to the whole set",
          evaluate_expectation(both, {"skill": "Fireball", "target": "Rhoa", "kind": "outgoing",
                                      "outcome": "hit", "critical": False})["passed"])
    check("a value check selects the records it describes",
          evaluate_expectation(ourgoing, {"critical": True, "hits": 1})["passed"])
    check("a value no record has fails",
          not evaluate_expectation(ourgoing, {"crit": "maybe"})["passed"])

    # A live run's shape: the kind and the skill matched and only the target did not. Blaming all three would send an
    # agent after two phantom bugs.
    mismatched = evaluate_expectation(ourgoing, {"kind": "outgoing", "skill": "Fireball", "target": "Rhoa Mare@80"})
    check("only the field that disagrees is blamed",
          [failure.split(":")[0] for failure in mismatched["failures"]] == ["target"],
          str(mismatched["failures"]))
    check("a field that matches is reported as matched",
          all(entry["records"] == 2 for entry in mismatched["checks"]
              if entry["check"] in ("kind", "skill")), str(mismatched["checks"]))

    # A /goto into the area the character already stands in forces a new instance; the wait must see the client leave
    # before it accepts the (unchanged) id.
    reloading = Reloading()
    goto_report = Runner({"name": "goto", "steps": [{"goto": {"area": "1_1_2", "seed": 7}}]},
                         client=reloading, out_dir=LOGS, sleep=lambda seconds: None).run()
    check("a goto into the current area waits for the new instance",
          goto_report["summary"]["stepErrors"] == 0 and reloading.calls >= 3,
          f"{goto_report['summary']} calls={reloading.calls}")
    check("value checks and a count can be combined",
          evaluate_expectation(SYNTHETIC_LEDGER, {"kind": "incoming", "hits": 1})["passed"])
    check("records, numeric bounds and an average",
          evaluate_expectation(SYNTHETIC_LEDGER, {"records": 3, "raw_max": 260, "raw_sum": 410,
                                                  "raw_avg": (120 + 260 + 30) / 3})["passed"])
    check("a bound with no records fails",
          not evaluate_expectation([], {"taken_min": 1})["passed"])
    check("an empty set fails a count of one", not evaluate_expectation([], {"hits": 1})["passed"])

    try:
        evaluate_expectation(ourgoing, {"nonsense": 1})
        check("an unknown check is refused", False, "no error")
    except LabError:
        pass

    mobs = parse_mobs(["0x1a2b Rhoa (12, 34), 0x1a2c Rocko the Big Rhoa (13, 34) (chasing)"])
    check("parse_mobs", mobs == {0x1a2b: "Rhoa", 0x1a2c: "Rocko the Big Rhoa"}, str(mobs))
    check("no monsters", parse_mobs(["No monsters."]) == {})

    # The bridge's stdin is the Windows locale encoding. A request line must therefore be pure ASCII: `json.dumps`
    # escapes a name like 张雪峰, and JSON unescapes it on the far side whatever the pipe encoding is.
    non_ascii = json.dumps({"command": "character:play", "name": "张雪峰"})
    check("request lines are ASCII whatever the name is", non_ascii.isascii(), non_ascii)
    check("the name survives the escaping",
          json.loads(non_ascii)["name"] == "张雪峰", non_ascii)
    check("the real reply line parses",
          reply_path(["damage 2 record(s) -> logs/damage-OfflineWitch.jsonl (1 hit, 1 critical, 200 taken)"])
          == ("logs/damage-OfflineWitch.jsonl", 2))

    # A real child process, spawned the way the runner spawns its bridge: the pipe must be UTF-8 and a non-ASCII
    # request must come back intact, which is the trap a locale-encoded pipe sets.
    try:
        result, encoding = encoding_check(HERE)
        measured = json.loads(encoding) if encoding else {}
        check("the bridge child gets a UTF-8 pipe", measured.get("stdin") == "utf-8", encoding)
        check("a non-ASCII request survives the pipe",
              "张雪峰" in json.dumps(result, ensure_ascii=False), json.dumps(result, ensure_ascii=True))
    except Exception as error:  # the check itself must not take the self-test down
        check("a real bridge child runs", False, f"{type(error).__name__}: {error}")

    scenario = {"name": "selftest", "session": "default", "steps": [
        {"spawn": {"monster": "Rhoa", "level": 3, "rarity": "rare"}},
        {"cast": {"skill": "Fireball", "target": "0x1a2b"}},
        {"settle": 0.0},
        {"collect": {"filter": "Fireball"}},
        {"expect": {"hits": 2, "taken_le_raw": True, "taken_sum": 200, "raw_min": 120}},
    ]}
    report = Runner(scenario, client=FakeClient(), out_dir=LOGS).run()
    check("a scenario against a fake client runs", report["summary"]["stepErrors"] == 0,
          str([step.get("error") for step in report["steps"] if step.get("error")]))
    check("its spawn learned the new id", report["steps"][0]["attempts"][-1].get("spawned") == ["0x1a2b"],
          str(report["steps"][0]["attempts"][-1].get("spawned")))
    check("its /damage ledger was read and filtered", report["summary"]["records"] == 2, str(report["summary"]))
    check("its expectation passed", report["summary"]["expectationsPassed"] == 1, str(report["summary"]))
    check("its outcome is 0", Runner(scenario, client=FakeClient(), out_dir=LOGS).run()["summary"]["stepErrors"] == 0)
    check("a wrong expectation makes exit code 1",
          Runner({"name": "bad", "steps": [{"collect": {}}, {"expect": {"hits": 99}}]},
                 client=FakeClient(), out_dir=LOGS).run()["summary"]["expectationsFailed"] == 1)

    # A failing step is retried once, does not abort the run, and leaves the exit code at 1.
    class Flaky(FakeClient):
        """Fails the first `/god` of a run and answers the second: the retry, without a client."""

        def __init__(self):
            super().__init__()
            self.seen = {}

        def command(self, text, wait=1.5):
            self.seen[text] = self.seen.get(text, 0) + 1
            if self.seen[text] == 1 and text.startswith("/god"):
                raise LabError("transient")
            return super().command(text, wait=wait)

    retried = Runner({"name": "retry", "steps": [{"command": "/god immune"}, {"collect": {}}]},
                     client=Flaky(), out_dir=LOGS).run()
    check("a transient failure retries once and succeeds",
          retried["summary"]["stepErrors"] == 0 and len(retried["steps"][0]["attempts"]) == 2,
          str(retried["summary"]))
    once = Runner({"name": "once", "steps": [{"verb": "command", "text": "/god immune", "retries": 0}]},
                  client=Flaky(), out_dir=LOGS).run()
    check("retries: 0 tries exactly once", len(once["steps"][0]["attempts"]) == 1, str(once["steps"][0]))
    check("every attempt is timed",
          all("at" in attempt and "elapsedMs" in attempt for report_ in (retried, once, report)
              for step in report_["steps"] for attempt in step["attempts"] if not attempt.get("dryRun")))
    check("a step that cannot be built is one timed failure",
          Runner({"name": "broken", "steps": [{"cast": {"x": 1}}]}, client=FakeClient(),
                 out_dir=LOGS).run()["summary"]["stepErrors"] == 1)
    cascaded = Runner({"name": "cascade", "steps": [{"collect": {}}, {"expect": {"hits": 99}},
                                                    {"expect": {"hits": 3}}]},
                      client=FakeClient(), out_dir=LOGS).run()
    check("a failed expectation does not abort the run",
          cascaded["summary"]["expectationsFailed"] == 1 and cascaded["summary"]["expectationsPassed"] == 1,
          str(cascaded["summary"]))

    # This machine's console is a Chinese code page and its streams raise on a character they cannot encode. Printing
    # the report must not be able to kill the run, nor change the exit code it earned.
    import io
    from contextlib import redirect_stderr

    def quietly(function):
        """Run a check that must print a warning, with that warning caught instead of alarming the reader."""
        with redirect_stderr(io.StringIO()):
            return function()

    console = io.TextIOWrapper(io.BytesIO(), encoding="gbk", errors="strict")
    try:
        console.write(printable("damage 1 record(s) -> logs/damage-张雪峰.jsonl (1 hit, 0 critical, 7 taken)",
                                console) + "\n")
        console.write(printable("a mangled byte: \ufffd", console) + "\n")
        console.flush()
        check("a console that cannot encode the report still takes it", True)
    except UnicodeEncodeError as error:
        check("a console that cannot encode the report still takes it", False, str(error))
    check("a name the code page can carry is left alone", printable("张雪峰", console) == "张雪峰")

    # The reply's path travels back through the Gateway log, so it can be mangled; the character's own ledger is the
    # fallback, and when neither exists the failure must name both candidates and the reply.
    fallback = Runner({"name": "fallback", "steps": [{"collect": {}}]}, client=MangledPath(), out_dir=LOGS,
                      character="张雪峰").run()
    check("a mangled reply path falls back to the character's ledger",
          fallback["summary"]["stepErrors"] == 0 and fallback["summary"]["records"] == 3, str(fallback["summary"]))
    check("the fallback is recorded in the report",
          any("instead" in note for step in fallback["steps"] for attempt in step["attempts"]
              for note in attempt.get("replies", [])), str(fallback["steps"][0]["attempts"][-1]))
    # The raised error carries both candidates and the reply; a report line truncates it, so check it directly.
    neither = Runner({"name": "neither", "steps": []}, client=MangledPath(), out_dir=LOGS, character="Nobody")
    try:
        neither.collect(normalize_step({"collect": {}}, 1), "/damage")
        check("neither candidate existing raises", False, "no error was raised")
    except LabError as error:
        message = str(error)
        check("neither candidate existing names both and the reply",
              "damage-???.jsonl" in message and "damage-Nobody.jsonl" in message and "the reply said" in message,
              message)

    # (a) A dead character is caught before step 1 instead of four steps later as "0 records".
    try:
        Runner({"name": "dead", "steps": []}, client=DeadCharacter(), out_dir=LOGS).preflight()
        check("preflight stops a dead character", False, "no error was raised")
    except LabError as error:
        check("preflight stops a dead character", "the character is dead" in str(error), str(error))
    try:
        alive = Runner({"name": "alive", "steps": []}, client=AliveCharacter(), out_dir=LOGS).preflight()
        check("preflight passes a live character", alive is not None and alive.get("life") == 1234, str(alive))
    except LabError as error:
        check("preflight passes a live character", False, str(error))
    allowed = Runner({"name": "dead-ok", "steps": [], "allowDead": True}, client=DeadCharacter(), out_dir=LOGS)
    check("allowDead runs anyway", quietly(allowed.preflight) is not None)
    check("a /state that does not parse is a warning, not a stop",
          quietly(lambda: Runner({"name": "nodata", "steps": []}, client=FakeClient(),
                                 out_dir=LOGS).preflight()) is None)

    # (b) Whatever a spawn places is taken away again, unless the scenario keeps it.
    cleanup_client = FakeClient()
    cleaned = Runner({"name": "cleanup", "steps": [{"spawn": {"monster": "Rhoa", "level": 5}}]},
                     client=cleanup_client, out_dir=LOGS).run()
    check("a spawned monster is despawned at the end", "/despawn 0x1a2b" in cleanup_client.commands,
          str(cleanup_client.commands))
    check("the cleanup is in the report", cleaned.get("cleanup", {}).get("monsters") == ["0x1a2b"],
          str(cleaned.get("cleanup")))
    keeper = FakeClient()
    quietly(lambda: Runner({"name": "keep", "steps": [{"spawn": {"monster": "Rhoa"}}]}, client=keeper, out_dir=LOGS,
                           cleanup=False).run())
    check("cleanup can be turned off", not any(line.startswith("/despawn") for line in keeper.commands),
          str(keeper.commands))

    # (c) A refused cast is not a cast.
    check("a real receipt is not a refusal",
          cast_refusal(["cast Cyclone (0x1abc) at monster 0x1a2b (Rhoa Mare@80) - the ordinary pipeline may still "
                        "refuse it, read /damage or /state for the outcome"]) is None)
    check("every refusal shape is recognised",
          all(cast_refusal([text]) for text in (
              'no gem of yours matches "X"',
              'no living monster near you matches "Rhoa"',
              '"Rhoa" matches 3 monsters: 0x1 (Rhoa Mare@80)',
              "Usage: /cast <skill id|name> [<monster id|name> | <x> <y>]",
              "/cast <skill> <x> <y>",
              "/cast <skill> <monster> takes no third argument")), "a refusal was read as a cast")
    refused = Runner({"name": "refused", "steps": [{"cast": {"skill": "Cyclone", "target": "Rhoa"}}]},
                     client=RefusingCast(), out_dir=LOGS).run()
    check("a refused cast fails its step",
          refused["summary"]["stepErrors"] == 1 and "refused" in str(refused["steps"][0].get("error")).lower(),
          str(refused["steps"][0].get("error")))
    quiet = Runner({"name": "refused-ok", "steps": [{"cast": {"skill": "Cyclone", "target": "Rhoa",
                                                              "allowRefused": True}}]},
                   client=RefusingCast(), out_dir=LOGS).run()
    check("allowRefused records the refusal without failing", quiet["summary"]["stepErrors"] == 0
          and quiet["steps"][0]["attempts"][-1].get("refused"), str(quiet["steps"][0]["attempts"][-1]))

    # A refusal is a ledger record, not a silence: it must be named, and an expectation that fails on one must not
    # read as a formula problem.
    check("a refusal summary names the reason",
          refusal_summary(SYNTHETIC_REFUSED) == "1 refused: SplittingSteel - no projectile recipe/metadata",
          str(refusal_summary(SYNTHETIC_REFUSED)))
    check("a refusal is not counted as a hit",
          refusals(SYNTHETIC_LEDGER) == [] and len(refusals(SYNTHETIC_REFUSED)) == 1)
    check("the summary counts the hits around the refusals",
          refusal_summary(SYNTHETIC_REFUSED + SYNTHETIC_LEDGER) == "1 refused: SplittingSteel - no projectile "
          "recipe/metadata", str(refusal_summary(SYNTHETIC_REFUSED + SYNTHETIC_LEDGER)))

    refused_client = FakeClient(records=SYNTHETIC_REFUSED, reply=SYNTHETIC_REFUSED_REPLY)
    refused_run = Runner({"name": "refused-ledger", "steps": [{"collect": {}}]},
                         client=refused_client, out_dir=LOGS).run()
    attempt = refused_run["steps"][0]["attempts"][-1]
    check("a collect over a refusal says the skill never fired",
          "never fired" not in str(attempt.get("note")) and "refused:" in str(attempt.get("note")),
          str(attempt.get("note")))
    check("the refusal note names the reason",
          "no projectile recipe/metadata" in str(attempt.get("note")) and "(no outgoing hit)" in str(attempt.get("note")),
          str(attempt.get("note")))
    check("the run summary carries the refusal finding",
          refused_run["summary"].get("onlyRefusals") is True
          and "no projectile recipe/metadata" in refused_run["summary"].get("finding", ""),
          str(refused_run["summary"]))
    check("the refusal reaches the console table",
          "SplittingSteel" in console_table(refused_run), console_table(refused_run))
    check("the refusal is folded onto the step",
          refused_run["steps"][0].get("refusals") == [{"skill": "SplittingSteel",
                                                      "reason": "no projectile recipe/metadata",
                                                      "target": "Rhoa Mare@80"}],
          str(refused_run["steps"][0].get("refusals")))

    # An expectation that fails on a refusal-only ledger must say so, or an agent chases the damage formula.
    refused_expect = Runner({"name": "refused-expect", "steps": [{"collect": {}},
                                                                 {"expect": {"kind": "outgoing", "hits": 1}}]},
                            client=FakeClient(records=SYNTHETIC_REFUSED, reply=SYNTHETIC_REFUSED_REPLY),
                            out_dir=LOGS).run()
    failures_text = " ".join(refused_expect["expectations"][0]["failures"])
    check("a failed expectation on refusals says the skill never fired",
          "never fired" in failures_text and "SplittingSteel" in failures_text, failures_text)

    # When the step's filter drops the refusal record, the empty-collect hint still prefers the ledger's own refusal.
    filtered = Runner({"name": "refused-filter", "steps": [{"cast": {"skill": "Cyclone", "target": "Rhoa"}},
                                                           {"collect": {"filter": "Cyclone"}}]},
                      client=CastThenRefused(), out_dir=LOGS).run()
    hint = str(filtered["steps"][1]["attempts"][-1].get("note"))
    check("an empty filtered collect prefers the ledger's refusal over the guesses",
          "SplittingSteel" in hint and "never fired" in hint, hint)

    # Two monsters of one variety cannot be told apart by label, so a reference to the id a spawn learned is the only
    # unambiguous way to name one — the case every multi-target test (split, splash, chain, area, pierce) needs.
    pair = FakeClient()
    split = Runner({"name": "two-spawns", "steps": [
        {"spawn": {"monster": "Rhoa", "level": 20, "ref": "primary"}},
        {"spawn": {"monster": "Rhoa", "level": 20, "ref": "secondary"}},
        {"cast": {"skill": "Splitting Steel", "target": "$primary"}},
        {"cast": {"skill": "Splitting Steel", "target": "$spawn2"}},
        {"collect": {}},
    ]}, client=pair, out_dir=LOGS).run()
    check("a named reference resolves to the id its spawn learned",
          "/cast Splitting Steel 0x1a2b" in pair.commands, str(pair.commands))
    check("a positional reference resolves to the right one",
          "/cast Splitting Steel 0x1a2c" in pair.commands, str(pair.commands))
    check("the report says which monster the step acted on",
          split["steps"][2].get("resolved") == {"primary": "0x1a2b"}, str(split["steps"][2].get("resolved")))
    check("the report names what each spawn answers to",
          split["spawned"].get("primary", {}).get("id") == "0x1a2b"
          and split["spawned"].get("secondary", {}).get("id") == "0x1a2c"
          and split["spawned"].get("spawn2", {}).get("id") == "0x1a2c", str(split["spawned"]))
    check("both placed monsters are still cleaned up",
          "/despawn 0x1a2b" in pair.commands and "/despawn 0x1a2c" in pair.commands, str(pair.commands))
    check("a reference works in a raw command's text",
          "0x1a2b" in str(step_command(normalize_step({"command": "/kill $primary"}, 1),
                                       {"primary": {"id": "0x1a2b"}})),
          str(step_command(normalize_step({"command": "/kill $primary"}, 1), {"primary": {"id": "0x1a2b"}})))
    check("a chat channel is not read as a reference",
          step_command(normalize_step({"command": "$trade selling"}, 1), {}) == "/$trade selling",
          step_command(normalize_step({"command": "$trade selling"}, 1), {}))
    check("tp takes a reference too",
          step_command(normalize_step({"tp": "$primary"}, 1), {"primary": {"id": "0x1a2b"}}) == "/tp 0x1a2b",
          step_command(normalize_step({"tp": "$primary"}, 1), {"primary": {"id": "0x1a2b"}}))
    check("an expectation can name the monster it means",
          resolve_expectation({"targetId": "$primary", "hits": 1},
                              {"primary": {"id": "0x1a2b", "numeric": 6699, "label": "Rhoa Mare@20"}})
          == {"targetId": 6699, "hits": 1},
          str(resolve_expectation({"targetId": "$primary", "hits": 1},
                                  {"primary": {"id": "0x1a2b", "numeric": 6699, "label": "Rhoa Mare@20"}})))
    check("an expectation's target label resolves to the label",
          resolve_expectation({"target": "$primary"},
                              {"primary": {"id": "0x1a2b", "numeric": 6699, "label": "Rhoa Mare@20"}})
          == {"target": "Rhoa Mare@20"})
    try:
        resolve_expectation({"targetId": "$nope"}, {})
        check("an expectation's unresolved reference is refused", False, "no error was raised")
    except LabError as error:
        check("an expectation's unresolved reference is refused", "it spawned: none" in str(error), str(error))

    # An unresolved reference must never pass through as the literal text.
    missing_client = FakeClient()
    missing = Runner({"name": "typo-ref", "steps": [
        {"spawn": {"monster": "Rhoa", "ref": "primary"}},
        {"cast": {"skill": "Splitting Steel", "target": "$secondry"}},
    ]}, client=missing_client, out_dir=LOGS).run()
    error = str(missing["steps"][1].get("error"))
    check("an unresolved reference is a step error naming what exists",
          "secondry" in error and "primary" in error and "0x1a2b" in error, error)
    check("the literal reference never reaches the client",
          not any("$secondry" in line for line in missing_client.commands), str(missing_client.commands))
    none_yet = Runner({"name": "no-spawns", "steps": [{"cast": {"skill": "X", "target": "$primary"}}]},
                      client=FakeClient(), out_dir=LOGS).run()
    check("a reference with no spawns says so",
          "it spawned: none" in str(none_yet["steps"][0].get("error")), str(none_yet["steps"][0].get("error")))

    # A dry run cannot learn an id, so it prints what the reference promises.
    plan = dry_run_plan({"name": "two", "steps": [
        {"spawn": {"monster": "Rhoa", "level": 20, "ref": "primary"}},
        {"spawn": {"monster": "Rhoa", "level": 20}},
        {"cast": {"skill": "Splitting Steel", "target": "$primary"}},
        {"cast": {"skill": "Splitting Steel", "target": "$spawn2"}},
    ]}, "lab")
    check("a dry run prints a reference as the spawn it will mean",
          "$primary (the monster spawn 1 learns)" in plan and "$spawn2 (the monster spawn 2 learns)" in plan, plan)
    check("a dry run names the reference a spawn will answer to",
          "$primary and $spawn1" in plan and "$spawn2 once its id is known" in plan, plan)

    # Which records a check reads is part of the check's meaning: a bound scoped to the hits must not be dragged down
    # by a blocked incoming record, a refusal, or anything else outside the set the expectation named.
    scoped = evaluate_expectation(FIGHT, {"kind": "outgoing", "targetId": 0x80000d4, "taken_min": 1})
    check("a bound next to a value check reads the checked records", scoped["passed"], str(scoped["failures"]))
    check("counts and bounds read the same records",
          evaluate_expectation(FIGHT, {"kind": "outgoing", "hits": 2, "taken_min": 19, "taken_max": 27,
                                       "taken_sum": 46})["passed"],
          str(evaluate_expectation(FIGHT, {"kind": "outgoing", "hits": 2, "taken_min": 19, "taken_sum": 46})))
    whole = evaluate_expectation(FIGHT, {"taken_min": 1})
    check("a bound with no value check still reads the whole set", not whole["passed"], str(whole["failures"]))
    check("the whole-set failure names the record that set the value",
          "blocked" in " ".join(whole["failures"]) and "taken=0" in " ".join(whole["failures"]),
          str(whole["failures"]))
    check("the failure names how many records the bound read",
          "5 record(s)" in " ".join(whole["failures"]), str(whole["failures"]))
    check("a refusal behaves like any other out-of-scope record",
          evaluate_expectation(FIGHT + SYNTHETIC_REFUSED, {"kind": "outgoing", "taken_min": 1})["passed"]
          and not evaluate_expectation(FIGHT + SYNTHETIC_REFUSED, {"taken_min": 1})["passed"])
    check("a bound scoped to nothing says so",
          "no records" in " ".join(evaluate_expectation(FIGHT, {"kind": "refused", "taken_min": 1})["failures"]),
          str(evaluate_expectation(FIGHT, {"kind": "refused", "taken_min": 1})["failures"]))

    print(f"selftest: {'ok' if not failures else f'{len(failures)} failure(s)'}", file=out)
    for failure in failures:
        print(f"  FAIL {failure}", file=out)
    return 0 if not failures else 1

# --- command line -------------------------------------------------------------------------------------------------

def parse_args(argv):
    """`run <scenario>`, `--scenario <scenario>`, `--list`, `--selftest`, `--help`. The scenario is a flag (`-s`) so
    `--dry-run` can be the first argument without swallowing a bare scenario name."""
    if not argv:
        print(__doc__)
        return {"mode": "help"}
    parser = argparse.ArgumentParser(prog="lab.py", description=__doc__.splitlines()[0],
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("mode", nargs="?", choices=("run", "list", "selftest", "help"),
                        help="run (default), list, selftest, help")
    parser.add_argument("-s", "--scenario", help="scenario: a path, a file name, or a stem in scenarios/")
    parser.add_argument("--list", action="store_true", help="list the scenario files and exit")
    parser.add_argument("--selftest", action="store_true", help="check the parsing and expectations, with no client")
    parser.add_argument("--dry-run", action="store_true",
                        help="print the exact commands that would be sent, and touch no client")
    parser.add_argument("--session", help="client session (live.ps1 -Session; default: the scenario's, else default)")
    parser.add_argument("--python", default=os.environ.get("LAB_PYTHON") or sys.executable,
                        help="interpreter that runs client_control.py (default: LAB_PYTHON, else this interpreter)")
    parser.add_argument("--out", help="report path (default logs/lab-<yyyyMMdd-HHmmss>.json)")
    parser.add_argument("--character", help="expected character: label the report, and stop on a mismatch")
    parser.add_argument("--open", action="store_true",
                        help="start the session's client when it is not running (live.ps1 -ClientOnly)")
    parser.add_argument("--json", action="store_true", help="print the report JSON instead of the table")
    parser.add_argument("--quiet", action="store_true",
                        help="print nothing on success (the report file is still written)")
    parser.add_argument("--no-cleanup", dest="cleanup", action="store_false", default=None,
                        help="leave the monsters this run spawned in the area (the next run is then poisoned)")
    parser.add_argument("--allow-dead", dest="allow_dead", action="store_true",
                        help="run even when the character is dead (casts will produce no records)")
    options = parser.parse_args(argv)
    options.mode = "list" if options.list else ("selftest" if options.selftest else options.mode)
    return vars(options)


def main(argv=None):
    configure_console()
    options = parse_args(list(sys.argv[1:] if argv is None else argv))
    if options["mode"] == "help":
        return 0
    if options["mode"] == "selftest":
        return selftest()
    if options["mode"] == "list":
        say(listing())
        return 0

    try:
        path = find_scenario(options["scenario"]) if options["scenario"] else None
    except LabError as error:
        say(f"lab: {error}", sys.stderr)
        return 2
    if path is None:
        say("lab: run needs a scenario (a path, a file name, or a stem in scenarios/)", sys.stderr)
        return 2
    try:
        scenario = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        say(f"lab: {path}: {error}", sys.stderr)
        return 2
    if not isinstance(scenario, dict) or not isinstance(scenario.get("steps"), list):
        say(f"lab: {path}: a scenario is an object with a `steps` list", sys.stderr)
        return 2
    scenario.setdefault("name", path.stem)
    session = options["session"] or scenario.get("session") or "default"

    if options["dry_run"]:
        try:
            say(dry_run_plan(scenario, session, cleanup=options["cleanup"],
                             allow_dead=options["allow_dead"] or None))
        except LabError as error:
            say(f"lab: {error}", sys.stderr)
            return 2
        return 0

    client = LabClient(options["python"], session)
    try:
        if options["open"]:
            client.execute({"command": "game:open"})
        try:
            status = client.execute({"command": "game:status"})
        except LabError as error:
            say(f"lab: session {session!r} has no client to drive ({error})", sys.stderr)
            return 2
        character = status.get("character") if isinstance(status, dict) else None
        if options["character"] and character and character != options["character"]:
            say(f"lab: session {session!r} plays {character!r}, not {options['character']!r}", sys.stderr)
            return 2
        runner = Runner(scenario, client=client, out_dir=LOGS, session=session,
                        character=character or options["character"], cleanup=options["cleanup"],
                        allow_dead=options["allow_dead"] or None)
        runner.preflight()
        report = runner.run()
        report_path = runner.write_report(options["out"])
        if options["json"]:
            say(json.dumps(report, indent=2, ensure_ascii=True))
        elif not options["quiet"]:
            say(console_table(report))
            say(f"  report: {report_path}")
        return runner.exit_code()
    except LabError as error:
        say(f"lab: {error}", sys.stderr)
        return 2
    finally:
        client.close()


if __name__ == "__main__":
    sys.exit(main())
