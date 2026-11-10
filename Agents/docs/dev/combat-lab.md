# Combat lab

The in-game lab compares a damage formula against what the Gateway really dealt. It runs on the local server with the
real client, through the [native client bridge](client-control.md): a monster is placed, one of the character's own
gems is used against it through the ordinary skill path, and the hits are read back from the ledger instead of being
guessed from two life readings.

The loop:

1. Edit the damage formula in `Modules/Core/Game/Combat/` and save the loose `.cs` file; the module hot-reloads
   ([the Gateway hot reloads a saved edit](live-testing.md), [modules](modules.md) "Hot reload"). Because `Game` is one
   assembly, the reload also restarts its per-connection state and the ledger.
2. Place a monster: `/spawn_mob Rhoa@4` (or `/mobs` to see what is already there).
3. Use a skill: `/cast Fireball Rhoa`, `/cast Fireball 120 340`, or `/cast Fireball` (at the character's own cell).
4. Read the ledger: `/damage 10 Fireball`, or `/damage` for the last 50. It writes
   `logs/damage-<character>.jsonl` and answers with the line below.
5. Compare `raw` with the formula (see "The ledger's fields") and report the difference.
6. [`scripts/dev/lab/lab.py`](../../../scripts/dev/lab/lab.py) does 2–5 as one scenario and turns step 5 into a pass or
   fail; use it when the experiment is a repeatable one, and the commands by hand when it is not.

## Commands

Everything here is a chat command sent through the bridge; `control.ps1` runs one from the shell, and the runner sends
them through the same `command` request. They are the lab's own commands and add nothing to normal play.

| Command | What it does |
|---|---|
| `/cast <skill id or name> [<monster id or name>]` | Uses one of the character's own gems through the ordinary skill path, as the client's own click would: cooldown, mana, reach, accuracy and ailments all still apply. |
| `/cast <skill id or name> <x> <y>` | Aims the skill at a ground cell. |
| `/cast <skill id or name>` | Aims the skill at the character's own cell (a self-cast or an area around the caster). |
| `/damage [count] [filter]` | Writes the last `count` records (default 50, at most 256) to the ledger and answers with the line below. `filter` keeps records whose skill or target contains it, case ignored. |
| `/damage clear` | Empties the ledger for this connection. |
| `/spawn_mob <row, Id or name>[@level] [friendly] [normal\|magic\|rare\|unique] [name]` | Places a monster near the player. A monster's label is its variety's, unless the command's last word names it. |
| `/mobs` | Lists the spawned monsters as `0x<id> <label> <cell>`, which is how a scenario learns the id of the monster it just placed. |
| `/skills` | Lists the character's own gems, and with an argument only those whose name contains it: how to find the name `/cast` wants. |
| `/socket_gem <gem id or name> [<equipped item id or name>] [<socket index>]` | Puts one of the character's own inventory gems into a socket of an equipped item through the ordinary server-side socket operation — what the client's C→S 0x30 does (the gem goes to the cursor, is socketed, and any gem it replaced ends on the cursor, with the same 0xbf inventory packets and a skill-list refresh) — so the gem really is socketed and `/skills`, and `/cast`, see its skill. **This is how a test decides which skill is under test.** The gem is matched in the main inventory by decimal id, `0x` hex id, or the name `/skills` prints (exact first, then substring); the item is the one named, else the first equipped item with a free socket in equipment-row order. A refusal is one readable sentence and changes nothing — no gem matches, several match (they are listed), something is on the cursor, no equipped item has a free socket, no such item, that socket does not exist, that socket already holds a gem, or the gem's requirements are unmet (checked before anything moves); success reads `socketed Splitting Steel into "Vaal Regalia" (item 0x2a) socket 1; /skills now lists it`. |
| `/smite <monster id or name> <damage> [<type>]` | One deliberate hit into a monster through the ordinary hit path, for a boss no test character can out-damage: its life really drops, its encounter's phase gate and stage machine see it, the on-death path runs, and the ledger records it as an `outgoing` record under the skill name `dev-smite` (`skillId` 0, `types` the type named, the amount applied as-is, with no evasion, block or mitigation roll). The type is `physical` (the default), `lightning`, `cold`, `fire` or `chaos`; damage is 1 to 1,000,000,000. |
| `/mob_life <monster id or name> <value\|n%>` | Sets a monster's current life to a value, or to `n%` of its maximum, and sends the ordinary 0x1b1 life update — for jumping straight to a phase threshold. It writes **no ledger row**. `0` is not written: it runs a lethal `/smite`, so the ordinary death handling happens instead of a monster sitting at 0 life while the server still thinks it lives. |
| `/state`, `/god`, `/tp`, `/kill`, `/set_level`, `/item`, `/gem` | See [campaign automation](campaign-automation.md) and [client control](client-control.md). `/god immune` keeps the character out of the measurement. |

`/damage` answers exactly one line:

```text
damage 3 record(s) -> logs/damage-OfflineWitch.jsonl (2 hit, 1 refused, 0 critical, 5 taken)
```

The path is the ledger and the parenthetical is that set's own counts, which the runner parses along with the path. The
file holds one compact JSON object per line, oldest first, at most `DamageLedger.Capacity` (256) records per
connection; each `/damage` rewrites it with exactly the records asked for, and a reload clears it.

Two results of `/smite` and `/mob_life` mislead anyone who does not know them. **A smite cannot skip a phase**: the hit
is capped at the *current* phase floor, so it stops exactly on the floor and the encounter starts its intermission —
measured live on the Shaper, four smites 22 s apart took it 75,193,704 → 56,395,278 (75%) → 37,596,852 (50%) →
18,798,426 (25%), each one landing on the next floor rather than through it. And **`/mob_life` writes no ledger row**,
because it moves life without a hit: the ledger stays a record of hits, so a test that needs the change recorded uses
`/smite`. A smite answers its own line:

```text
smite 0x8000001 The Shaper for 19000000 fire - life 75193704 -> 56395278
```

A hit that moved nothing says why, rather than looking like a broken command: `(unchanged: it is invulnerable while
its phase runs)`, or `(unchanged: its life floor refused the hit)`.

The ledger belongs to a **character**, not to a path string. The reply's path travels back through the Gateway log, so
it can be mangled by an encoding (the log is decoded best-effort) or lag the write: a `collect` whose named path cannot
be read falls back to `logs/damage-<character>.jsonl`, records that in its report, and fails naming both candidates if
neither exists. A `collect` immediately after a `/damage` is fine — the file is written before the reply.

## The ledger's fields

| Field | Meaning |
|---|---|
| `at` | Server clock in seconds. |
| `kind` | `"outgoing"` (a hit the player dealt), `"incoming"` (one the player took), or `"refused"`: a skill use the server refused **before any hit**, with the reason in `outcome` and `raw` and `taken` 0. |
| `skill`, `skillId` | The skill's name and id. |
| `targetId`, `target` | The monster's object id and its label. |
| `level`, `rarity` | The target's level and rarity, lower-cased. |
| `raw` | **The resolved damage: the number a formula must predict.** Every player-side multiplier (critical, impale) and the target's own resistances and armour are already in it; the target's remaining life is not. |
| `taken` | The life the target actually lost: `min(raw, lifeBefore)`. |
| `types` | The rolled packet per type, **before the critical multiplier**, e.g. `"physical:12.5,fire:30"`; scale it by the crit multiplier when `crit` is true. |
| `crit` | Whether the hit was a critical strike. |
| `outcome` | `"hit"` or `"killed"` for a hit; for a refusal, the short reason it was dropped. |
| `lifeBefore`, `lifeAfter` | The target's life around the hit. `lifeAfter == lifeBefore - taken` always holds. |
| `distance` | Distance from the player to the target, in cells. |

**`raw` is what you compare with the formula, not `taken`.** On a target whose remaining life is smaller than the hit,
`taken` is clamped to that life and is smaller than the damage the hit really rolled: `raw > taken` means the hit
overkilled, and `raw == taken` means it did not. That clamp is why a lab hit must be measured against a target with
enough life to survive it — otherwise the number you are comparing is the monster's life, not your damage. The
scenario runner's bounds read the field they name (`raw_min`, `taken_max`) over the records that expectation selects,
and its `taken_le_raw`, `life_arithmetic` and `overkill_clamped` checks state exactly the relations above, so a ledger
that broke one of them fails loudly.

**A refusal is a record, not a silence.** A skill the server refuses before any hit is recorded like everything else:
`kind` is `"refused"`, `outcome` holds the short reason (e.g. `no projectile recipe/metadata`), `raw` and `taken` are
0, and the aim is in `target`/`lifeBefore`/`lifeAfter` with the target's life unchanged. This is the difference that
cost two rounds of debugging on Splitting Steel: **a ledger whose records are all refusals means the skill never
fired, which is not the same finding as a skill that fired and dealt no damage.** Only the second one is a fact about
the damage formula; the first one is a fact about the skill, and no formula comparison can explain it. The runner says
which one it is — a step that collected only refusals carries the reason in its note and row, the run's summary carries
it as `finding`, and an `expect` that fails on such a ledger adds "the skill never fired" to its failures rather than
letting `kind: outgoing` read as a formula problem.

Every use the ordinary pipeline drops — the input gate (hit recovery / no action speed), a cast or attack that cannot
start, a target that is dead, owned, out of reach or in town, a disabled or unaffordable skill, and the deferred
projectile release — is a Kind `"refused"` ledger row with the short reason in `outcome` (raw/taken 0, the aim in
`targetId`/`distance`); /cast's receipt names the refusal when it happened by hand-over time, and says
"nothing recorded yet" when only a projectile still in flight could explain it.

`/cast`'s receipt still begins with `cast ` and now ends with one of three notes, read from the ledger at hand-over
time: the refusal it can already see (`; the ledger holds a refusal: <reason>`), a hit
(`; the ledger holds a outgoing record`), or `; nothing recorded yet - read /damage (a projectile may still be in
flight)`. The `cast ` prefix is what tells a receipt from a refusal, so a scenario's refusal check is unaffected by
which note is appended.

## `Game_TRACE=damage`

`Game_TRACE=damage` prints each record as one `[damage] {json}` line in the Gateway log as it is committed
([debugging](debugging.md) "Dev traces"), which is the same record `/damage` writes. It answers *when* a hit happened
relative to other log lines; the ledger answers *what* it did. Both are per connection.

## The scenario runner

```text
python scripts/dev/lab/lab.py [run] [-s <scenario>] [--session NAME] [--python EXE] [--out PATH]
                              [--character NAME] [--open] [--dry-run] [--json] [--quiet]
                              [--no-cleanup] [--allow-dead]
python scripts/dev/lab/lab.py --list
python scripts/dev/lab/lab.py --selftest
```

`-s` takes a path, a file name, or a bare stem in `scripts/dev/lab/scenarios/`. The interpreter matters: the runner
spawns `<python> scripts/dev/client_control.py --session <s> --stdio` and answers that child's requests, so it needs
`frida` and the client running. `--python` (or `LAB_PYTHON`) picks it, defaulting to the running interpreter. This
checkout has `frida` in its system Python 3.13, not in a `.venv`, so `python scripts/dev/lab/lab.py …` works as it is.

The bridge's stdio pipe is the Windows **locale** encoding, not UTF-8, so the runner spawns the child with
`PYTHONIOENCODING=utf-8` and `PYTHONUTF8=1` and writes its request lines ASCII-escaped (`json.dumps`, `ensure_ascii`),
which a real character's non-ASCII name needs — on this machine a child without that environment reports
`sys.stdin.encoding == "gbk"`, and a UTF-8 request would arrive mangled. The console is that same code page, so the
runner reconfigures its own stdout/stderr to replace what the page cannot encode: a report holding a mangled byte or a
non-ASCII name still prints, and the exit code stays the run's rather than a printing failure's.

`--dry-run` prints the exact commands the scenario would send (and its preflight and cleanup) and touches nothing — no
bridge, no client — so it also works on a machine without `frida`. `--list` and `--selftest` need nothing either.
`--selftest` parses a synthetic `/damage` reply and ledger and checks the expectation evaluation, the retry and failure
rules, the refusal detection, the preflight and the cleanup, without a client.

## Around the steps

Two of the runner's own checks exist because the live failure they catch is otherwise silent:

- **Preflight** (`/state`, before step 1). A **dead character's skill use is dropped**: `/cast` still answers its
  receipt, the skill pipeline never runs, and no record appears — so the only symptom is "0 records" several steps
  later. The runner therefore reads the character's own state first and stops with **exit 2** when `dead` is true or
  `life` is 0, saying so. `"allowDead": true` in the scenario (or `--allow-dead`) runs anyway.
- **Cleanup** (`/despawn <id>`, after the last step). Every monster a `spawn` step placed is taken away by id
  (`/despawn all` would also remove monsters the run did not place). A monster left standing attacks the character
  between runs, and once several of the same variety are alive a later `/cast <skill> <name>` is refused as ambiguous.
  That is how repeated runs accumulate monsters — enough to kill a level-100 character with its own test subjects. A
  scenario may keep them with `"cleanup": false` or `--no-cleanup`, and the runner then says so loudly; when a despawn
  fails it warns and lists the ids it could not remove.

A scenario that spawns hostile monsters and does not use this runner must end with its own `/despawn all`.

## Referring to what a scenario spawned

A label cannot name one of two identical monsters: spawn two of a variety and `/cast <skill> Rhoa` matches both, which
the Gateway refuses as ambiguous, and the scenario measures nothing. Every `spawn` step therefore answers to a name
that resolves to the id it learned, and a later step's monster argument refers to it with `$`:

- `$spawn<N>` — the Nth `spawn` step of the scenario (1-based), always available.
- `"ref": "<name>"` on the spawn — an explicit name as well, so `$primary` reads better than `$spawn1`.

References resolve in a step's monster argument (`cast`'s `target`/`monster`, `tp`'s `target`) and inside a `command`
step's text for names that exist (so `/kill $primary` works); an unknown `$name` in free text is left alone because it
may be chat (`$trade`). In an argument it is strict: an unresolved reference is a step error naming it and listing the
spawns that do exist, never a literal `$primary` sent to the Gateway. An expectation can name one too — `targetId` is
the ledger's object id and `target` the label — which is how a test says *which* of two identical monsters it hit.

```json
{
  "name": "two-targets-split",
  "steps": [
    {"verb": "spawn", "monster": "Rhoa", "level": 20, "ref": "primary"},
    {"verb": "settle", "seconds": 1.0},
    {"verb": "spawn", "monster": "Rhoa", "level": 20, "ref": "secondary"},
    {"verb": "settle", "seconds": 1.0},
    {"verb": "damage", "clear": true},
    {"verb": "cast", "skill": "Splitting Steel", "target": "$primary"},
    {"verb": "settle", "seconds": 2.0},
    {"collect": {"count": 50}},
    {"verb": "expect", "hits": 1, "kind": "outgoing", "targetId": "$primary", "taken_le_raw": true}
  ]
}
```

A dry run cannot learn an id from `/mobs`, so it prints what each reference will mean:
`/cast Splitting Steel $primary (the monster spawn 1 learns)`. The run's report carries the same mapping under
`spawned` (`"primary": {"id": "0x80000d4", "numeric": 83886164, "label": "Rhoa Mare@20", "step": 1}`) and, on each
step that used one, `resolved` (`{"primary": "0x80000d4"}`). Cleanup still despawns by id, so a referenced monster is
taken away like any other.

A scenario is one JSON object:

```json
{
  "name": "fireball-vs-rhoa",
  "description": "Fireball against one spawned Rhoa at level 4.",
  "session": "lab",
  "steps": [
    {"verb": "command", "text": "/god immune"},
    {"verb": "spawn", "monster": "Rhoa", "level": 4},
    {"verb": "settle", "seconds": 1.0},
    {"verb": "cast", "skill": "Fireball", "target": "Rhoa"},
    {"verb": "settle", "seconds": 1.0},
    {"collect": {"filter": "Fireball"}},
    {"verb": "expect", "hits": 1, "kind": "outgoing", "skill": "Fireball", "target": "Rhoa Mare@4", "taken_le_raw": true}
  ]
}
```

A step is `{"verb": "<name>", …}` or the compact `{"<verb>": <argument>}` form the campaign step files use
([campaign automation](campaign-automation.md) "Step files"). A scenario may also carry `"allowDead": true` (run even
when the character is dead) and `"cleanup": false` (keep the monsters it spawns); both have a command-line
equivalent.

| Verb | Fields | What it does |
|---|---|---|
| `command` | text | Sends any chat line and keeps its replies. The escape hatch for a command this list does not name. |
| `goto` | `area`, `seed?`, `timeout?` | `/goto <area> [seed]`, then waits for the area to be *entered*, not merely for the id: a `/goto` into the area the character already stands in with a seed makes a new instance while the id stays the same, and a chat command sent while the client loads that one fails with "Chat requires an active area". |
| `spawn` | `monster`, `level?`, `rarity?`, `name?`, `friendly?`, `ref?` | `/spawn_mob`, then `/mobs` before and after, so the step records the new monster's id and label. Fails when `/mobs` did not grow. `ref` names it for later steps (see "Referring to what a scenario spawned"). |
| `cast` | `skill`, `target` or `monster`, or `x` and `y`, or neither | `/cast`, in the contract's own order: named target, else cell, else the character's own cell. A monster argument may be a `$reference`. A receipt that does not start with `cast ` is a **refusal** (`no gem of yours…`, `no living monster…`, `"X" matches N monsters…`, a `Usage:` line) and fails the step: the skill pipeline never ran, so ok would be a lie. `"allowRefused": true` records the refusal without failing. |
| `settle` | seconds | Waits. A wall-clock wait on a live client; nothing in the runner's self-test waits. |
| `damage` | `clear?` | `/damage clear` when `clear` is true, else `/damage`. |
| `collect` | `filter?`, `count?` | `/damage`, with `count` and then `filter` as the command takes them, then reads the ledger its reply names and keeps the records — refused ones included. A filter with no count sends `count` 256. When a collect comes back empty right after a cast, the step records a hint that prefers the ledger's own `refused` record over its three guesses (a refused cast, a dead character, the skill's reach); a collect holding only refusals names the reason in the step's note and row, e.g. `1 refused: SplittingSteel - no projectile recipe/metadata (no outgoing hit)`. |
| `expect` | the checks below | Asserts on every record collected so far. |
| `tp` | `target`, or `x` and `y` | Teleports, as `/tp` or the bridge's own `tp` request. A `$reference` resolves to the object id, which `/tp` accepts. |

**A custom name is not a label, and a label carries the level.** `/spawn_mob Rhoa@80 normal LabDummy` makes a monster
whose label is `` `<variety Name>@<level>` `` — `Rhoa Mare@80` — because Variety `Rhoa` is named "Rhoa Mare". `/mobs`,
`/cast`'s name matching and the ledger's `target` all use that label: `/cast` matches it by case-insensitive
substring (so `Rhoa` finds it when one is spawned), while an `expect` compares the whole string, so the custom name
never matches. The `spawn` step learns the label from `/mobs` for exactly this reason.

Every step logs what it sent, what came back and how long it took in milliseconds. A step that fails retries once
after a short settle, unless it says `"retries": 0`. A failed `expect` does not stop the run: every failure is
collected and printed at the end.

`expect` checks the records collected so far. **Which records a check reads is part of what it means**, so a value
check *selects* and the count, bound and invariant checks *measure what it selected*:

- Value checks first — any ledger field, `kind`, `skill`, `target`, `outcome`, `crit` (or `critical`), `level`,
  `rarity`, …, and `target`/`targetId` may be a `$reference`. Each one is judged on its own — "at least one collected
  record carries this value" — and reported separately, so a step expecting `kind`, `skill` and `target` whose target
  disagrees says only that about `target`. Together they select the **checked subset**: the records satisfying all of
  them at once.
- `hits`, `count`, `records` then count that subset: `{"hits": 2, "kind": "outgoing"}` is two outgoing hits.
- `<field>_min`, `<field>_max`, `<field>_sum`, `<field>_avg` measure the same subset — `{"kind": "outgoing",
  "targetId": "$primary", "taken_min": 1}` is about the hits on that one monster, so an incoming blocked record with
  `taken` 0 cannot drag the minimum down to 0. `raw` is the field to bound for a formula.
- `taken_le_raw`, `life_arithmetic`, `overkill_clamped` read the same subset. A whole-ledger integrity check is an
  expectation with no value check at all.
- With **no** value check there is nothing to select, so counts, bounds and invariants read everything collected.

A failing bound says which records it read and, for a min or max, which one set the value:
`taken_min: wanted >= 1.0, got 0.0 (over 5 record(s) (no value check, so the whole collected set); min set by incoming
MonsterAttack -> 吴沃蓝 taken=0 (blocked))`.

One limitation to know when a check finds nothing: **a skill that fires and contacts nothing leaves no record either.**
A refusal is recorded because the server dropped the use; a miss is not recorded, because nothing was dropped and
nothing resolved. So "no record" means one of three things — a recorded refusal, a projectile still in flight, or a
genuine miss — and the ledger can only tell you the first. That is also what `/cast`'s "nothing recorded yet" note
covers: it is the truth for a projectile still in flight and for a miss alike, and it never claims a hit happened. A
scenario that must know can bound the wait (a `settle` long enough for the projectile) and check the refusal with
`kind`, but a miss stays indistinguishable from a hit that never happened.

A bare `/damage <filter>` (no count) is rejected by the Gateway as of this writing, so a `collect` with a filter sends
an explicit count; `/damage [count] [filter]` is the documented shape either way.

A run writes `logs/lab-<yyyyMMdd-HHmmss>.json` (or `--out`): the scenario, the preflight, every step with its attempts
and replies, every collected record, every expectation with its checks and verdict, the cleanup, and a summary. `--json`
prints that report instead of the table. The exit code is the report: **0** when every expectation passed and no step
failed, **1** when an expectation failed or a step did (the run still finishes), **2** for a session or argument error
(no client, an unknown scenario, a scenario that is not valid JSON, or a character that is dead when the run starts).

The runner never starts a server, never edits a save, and never uses `/goto` as a hidden fallback. `--open` starts the
session's client through `scripts/dev/live.ps1 -ClientOnly -Session <s>` when it is not running, and is off by default.

## Picking this up

```powershell
python scripts/dev/lab/lab.py --list
python scripts/dev/lab/lab.py --dry-run -s fireball-vs-rhoa
python scripts/dev/lab/lab.py run -s fireball-vs-rhoa --session lab
python scripts/dev/lab/lab.py run -s five-hits-bounds --session lab --json
python scripts/dev/lab/lab.py --selftest
```

The first two need nothing but Python. `run` needs the local servers and that session's client (start them as in
[live testing](live-testing.md); `--open` starts the client only). Read `logs/lab-<stamp>.json` for the numbers, and
compare `raw` against the formula you changed.

## What only the tree knows

The ledger's shape and its `[damage]` trace line, and the `ICombatLabCommands` boundary the Gateway exposes for
`/cast`, `/spawn_mob` and `/damage`, are in the tree as of this writing; read
`Modules/Core/Game/Combat/DamageLedger.cs` and `src/GameServer.Gateway/Connection/CombatLab.cs` for them rather than
this page, which describes what the runner sends and what the ledger means.

<!-- Once the combat-lab Gateway commands land, re-read two things against the tree and correct this doc if they moved:
     (1) the exact `/damage` reply line and the ledger path it names, and (2) the `/cast` and `/spawn_mob` argument
     shapes and whether `/mobs` still lists a monster as `0x<id> <label> <cell>`. Everything else here is the runner's
     own, verified offline by `--selftest` and `--dry-run`. -->
