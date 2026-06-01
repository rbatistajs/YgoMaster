# Roguelike duelHooks (Lua) Reference

Relics give cards **in-duel behaviour** through small **Lua scripts** (MoonSharp, sandboxed). A script
pins callbacks to named **hooks** with `on(name, fn)`; the engine calls them back during the duel. The
same scripts can also read live duel state and run **actions** (special summon, send to grave, set ATK,
…).

Scripts live in **`DataLE/Roguelike/Scripts/*.lua`**. A relic points at one or more scripts (with an
optional `params` table) so a single generic `.lua` can be reused by many relics.

**Two kinds of hook:**
- **query hooks** (`buff`) — called per card, *return* a value the engine consumes.
- **event hooks** (`summon`, `set`, …) — fire when something happens; no return value.

> **Sandbox:** Lua 5.2 with a hard sandbox — `string`/`math`/`table` are available, but `io`/`os`/
> `require` are not. Only the API below is exposed. RNG (`math.random`) is allowed (the duel is played
> live, not replayed).

---

## Contents

- [How a relic loads scripts](#how-a-relic-loads-scripts)
- [Registering hooks: `on`](#registering-hooks-on)
- [Live values vs printed values](#live-values-vs-printed-values)
- [Events](#events)
  - [`buff` (query)](#buff-query)
  - [`summon`](#summon)
  - [`set`](#set)
- [Reading the duel](#reading-the-duel)
- [Actions](#actions)
  - [`special_summon`](#special_summon)
  - [`debug_command`](#debug_command)
- [Examples](#examples)
- [Dev commands](#dev-commands)

---

## How a relic loads scripts

A relic declares the scripts it uses in `Relics.json`. The relic only *points* at the file; the script
attaches itself to the hooks via `on(...)`.

```jsonc
"dragon_soul": {
  "name": "Dragon Soul", "icon": "card_...", "text": "...", "rarity": "SR",
  "scripts": [
    { "file": "race_buff.lua", "params": { "race": 18, "atk": 300 } }
  ]
}
```

- **`file`** — a `.lua` filename under `DataLE/Roguelike/Scripts/`.
- **`params`** — a free-form table passed back to the script's callbacks (so one `.lua` serves many
  relics with different numbers). See [`on`](#registering-hooks-on).

> The hooks registered by a script are cleared at the end of each duel.

---

## Registering hooks: `on`

```lua
on(name, fn)
```

Pins `fn` to the hook called `name`. The relic's `params` (active while the script loads) is captured
and passed back as the callback's **second argument**:

```lua
on("buff", function(c, params)
  -- c      = the per-card context (see each event below)
  -- params = this relic's params table from Relics.json
end)
```

A single script may register several hooks (`on("buff", …)`, `on("summon", …)`, …).

---

## Live values vs printed values

Every hook context (`c`/`e`) carries **live instance values** — the card's *current* stats, already
altered by any in-duel effect (e.g. a field-type change, a continuous buff). The **static printed
category** (is it a Normal monster? a Tuner? its frame/icon) is **not** in the context; pull it with
[`card_props(c.cid)`](#reading-the-duel).

| source | what | examples |
|---|---|---|
| context (`c`/`e`) | **live** instance values | `cid, uid, zone, player_id, player_type, race, attr, level, atk, def` |
| `card_props(cid)` | **static** category by cid | `subtype, frame, kind, icon` + printed stats |

`player_id` is `0`/`1`; `player_type` is `"player"` (you) or `"cpu"`.

---

## Events

### `buff` (query)

Called **once per field card** (monster zones 0–6) every time the engine computes a card's value.
Return a table of **deltas** to apply, or `nil`/nothing for no change. Deltas from all `buff` hooks are
summed. The engine applies them to the live value (with the battle-mirror rule, so attacking doesn't
double-count).

**Context `c`:** `{ cid, uid, race, attr, level, atk, def, zone, player_id, player_type }`
**Return:** `{ atk?, def?, level? }` (any subset) or `nil`.

```lua
on("buff", function(c, params)
  if c.player_type == "player" and c.race == params.race then
    return { atk = params.atk or 0, def = params.def or 0, level = params.level or 0 }
  end
end)
```

### `summon`

Fires when a monster is summoned — **normal** (`kind="normal"`) or **special** (`kind="special"`),
for **both** the player and the cpu. The card is already on the field, so the context is the full
live state plus `kind`.

**Context `e`:** `{ uid, cid, race, attr, level, atk, def, player_id, player_type, location, zone, kind }`

```lua
on("summon", function(e, params)
  if e.kind == "normal" and card_props(e.cid).frame == "normal" then
    -- "when you Normal Summon a Normal monster, ..."
  end
end)
```

### `set`

Fires when a **monster is Set** face-down (both sides). Same context as a field card.

**Context `e`:** `{ uid, cid, race, attr, level, atk, def, player_id, player_type, location, zone }`

```lua
on("set", function(e, params)
  if card_props(e.cid).kind == "monster" then ... end
end)
```

> More events (`attack`, `activate`, `destroy`, `draw`, …) are mapped from the same view bus and will
> follow the same shape — a context carrying at least the `uid`, from which `card_state(uid)` resolves
> the rest.

---

## Reading the duel

All read functions are available from any hook.

| function | returns |
|---|---|
| `card_props(cid)` | static props `{ cid, race, attr, level, atk, def, subtype, frame, kind, icon }`, or `nil` |
| `card_state(uid)` | **live** state of an instance (see below), or `nil` if the instance is gone |
| `field_uid(player, zone)` | `uid` of the monster in `zone` (0–6) for `player`, or `nil` if empty |
| `deck_top(player)` | `cid` of the top deck card, or `nil` |
| `hand_count(player)` | cards in hand (`int`) |
| `deck_count(player)` | cards in main deck |
| `grave_count(player)` | cards in graveyard |
| `extra_count(player)` | cards in extra deck |
| `banish_count(player)` | banished cards |
| `log(x)` | print `x` to the console (debugging) |

**`card_state(uid)`** is read straight from the engine, so it always reflects the *current* state:

```
{ uid, cid, race, attr, level, atk, def, player_id, player_type, location, zone? }
```

- `location` is `"field"`, `"hand"`, `"grave"`, `"deck"`, `"extra"`, or `"banish"`.
- `zone` (0–6) is present only when `location == "field"`.
- A field card returns **live** atk/def (effects applied); an off-field card returns the printed
  values.

`uid` identifies a specific copy (vs `cid` = the card type); it is stable while the card stays in a
spot. Get one from an event context (`e.uid`), from `field_uid(player, zone)`, or from the `c.uid` in a
`buff` query.

---

## Actions

Actions **change the duel**. They must be called **from inside a hook** — i.e. during active duel
resolution (when an event fires). Called from an idle moment they will not take effect, because the
engine only pumps the command/placement queue while it is actively resolving.

### `special_summon`

```lua
special_summon(player, from, index)   -- returns true if it was queued
```

Begins a Special Summon of one card to `player`'s field. The **destination zone is chosen by the
owner** — the UI prompt for the player, the AI for the cpu.

- **`player`** — `0`/`1`.
- **`from`** — source pile: `"deck"`, `"grave"`, `"hand"`, `"extra"`, `"banish"`.
- **`index`** — position in that pile (`0` = top of deck / first entry).

```lua
on("summon", function(e)
  -- when you Normal Summon, also Special Summon the top of your deck
  if e.kind == "normal" and e.player_type == "player" then
    special_summon(e.player_id, "deck", 0)
  end
end)
```

### `debug_command`

The raw engine "swiss-army-knife" (`DLL_DuelComDoDebugCommand`) — a low-level escape hatch for the
many small operations the engine exposes. Friendly per-operation aliases are built on top of this.

```lua
debug_command(player, location, index, cmd)
```

- **`location`** — `13`=hand, `15`=deck, `16`=grave, `17`=banish, `0`–`6`=monster zones.
- **`cmd`** — the operation (see below). Some `cmd` repurpose `location`/`index` (e.g. a value).

| cmd | action | notes |
|---|---|---|
| `0` | change phase / turn | `index` = phase; `location=0x12` swaps turn player |
| `3` / `4` | +LP / −LP | `location` = amount |
| `6` | → hand (any pile; deck = **draw**) | `location`/`index` |
| `7` | → top of deck | |
| `8` | → graveyard | |
| `9` / `10` | banish face-up / face-down | |
| `11` | destroy → graveyard | |
| `12` | destroy (field only, `location < 7`) | |
| `15` / `16` | level +1 / −1 | `location=13` (hand) |
| `20` | shuffle deck | |
| `23` / `24` | **set ATK / set DEF** | `location` = monster zone (0–6), `index` = value; counts in damage calc. Absolute set on the live instance — resets when the card is flipped face-down. |
| `27` | lose the duel (LP→0) | |

Full `cmd` catalogue and the RE detail: `Docs/design/duel-action-primitives.md`.

```lua
on("attack", function(e)            -- (once the attack event is wired)
  local c = card_state(e.uid)       -- read the attacker's current ATK
  debug_command(c.player_id, c.zone, c.atk + 500, 23)   -- +500 for this battle
end)
```

---

## Examples

**Race buff** (`race_buff.lua`, reusable via `params`):

```lua
on("buff", function(c, params)
  if c.player_type == "player" and c.race == params.race then
    return { atk = params.atk or 0, def = params.def or 0 }
  end
end)
```
```jsonc
"scripts": [ { "file": "race_buff.lua", "params": { "race": 18, "atk": 300 } } ]
```

**Draw on Set:**

```lua
on("set", function(e)
  if e.player_type == "player" then
    debug_command(e.player_id, 15, 0, 6)   -- deck[0] -> hand
  end
end)
```

**Mill the opponent on Normal Summon:**

```lua
on("summon", function(e)
  if e.kind == "normal" and e.player_type == "player" then
    debug_command(1, 15, 0, 8)   -- opponent's deck[0] -> graveyard
  end
end)
```

---

## Dev commands

While iterating, scripts can be loaded/inspected from the in-game console:

- `rghook load <file.lua> [params-json]` — load a script with a params table.
- `rghook clear` — drop all hooks. `rghook list` — list registered hooks.
- `rglua <code>` / `rglua file <name.lua>` — run Lua on the duel engine.
- `rgcardprops <cid>` — dump a card's static props.
