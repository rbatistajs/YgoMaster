# Roguelike duelHooks (Lua) Reference

Relics give cards **in-duel behaviour** through small **Lua scripts** (MoonSharp, sandboxed). A script
pins callbacks to named **hooks** with `on(name, fn)`; the engine calls them back during the duel. The
same scripts can also read live duel state and run **actions** — from simple ones (special summon, send
to grave, set ATK) up to a **real chain** that carries an arbitrary scripted effect.

Scripts live in **`DataLE/Roguelike/Scripts/*.lua`**. A relic points at one or more scripts (with an
optional `params` table) so a single generic `.lua` can be reused by many relics.

**Two kinds of hook:**
- **query hooks** (`buff`) — called per card, *return* a value the engine consumes.
- **event hooks** (`summon`, `special_summon`, `set`, `turn`, …) — fire when something happens; no return.

> **Sandbox:** Lua 5.2 with a hard sandbox — `string`/`math`/`table` are available, but `io`/`os`/
> `require` are not. Only the API below is exposed. RNG (`math.random`) is allowed (the duel is played
> live, not replayed). There is **no coroutine** API — interactive picks use `select_card`'s callback.

---

## Contents

- [How a relic loads scripts](#how-a-relic-loads-scripts)
- [Registering hooks: `on`](#registering-hooks-on)
- [Live values vs printed values](#live-values-vs-printed-values)
- [Card category enums](#card-category-enums)
- [Events](#events)
  - [`buff` (query)](#buff-query)
  - [`summon` / `special_summon`](#summon--special_summon)
  - [`set`](#set)
  - [`turn`](#turn)
  - [`phase`](#phase)
- [Reading the duel](#reading-the-duel)
- [Actions](#actions)
  - [`special_summon`](#special_summon)
  - [`to_grave` / `to_hand` / `banish` / `destroy`](#to_grave--to_hand--banish--destroy)
  - [`debug_command`](#debug_command)
  - [`run_effect` / `effect_activate_*`](#run_effect--effect_activate_)
  - [`activate_effect`](#activate_effect)
  - [`chain_effect`](#chain_effect)
  - [`select_card`](#select_card)
- [Table helpers: `merge` / `spread`](#table-helpers-merge--spread)
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
  -- c      = the per-event context (see each event below)
  -- params = this relic's params table from Relics.json
end)
```

A single script may register several hooks. Hook callbacks run **synchronously** during the duel.
Script-wide state is just a Lua `local` shared by the closures (see the [end-of-turn buff](#examples)).

---

## Live values vs printed values

Every event/query context carries **live instance values** — the card's *current* stats, already
altered by any in-duel effect (a field-type change, a continuous buff). The **static printed category**
(is it a Normal monster? a Tuner? its frame/icon) is **not** in the context; pull it with
[`card_props(c.cid)`](#reading-the-duel).

| source | what | examples |
|---|---|---|
| context (`c`/`e`) | **live** instance values | `cid, uid, zone, player_id, player_type, race, attr, level, atk, def` |
| `card_props(cid)` | **static** category by cid | `simple_kind, frame, kind, icon` + printed stats |

`player_id` is `0`/`1`; `player_type` is `"player"` (you) or `"cpu"`.

---

## Card category enums

`card_props(cid)` returns the static category as **strings** (`simple_kind`, `frame`, `kind`, `icon`).
To compare without magic strings, the matching enums are exposed as global tables whose members are the
same string (`CardSimpleKind.Spell == "Spell"`):

| enum | field on `card_props` | members (examples) |
|---|---|---|
| `CardSimpleKind` | `.simple_kind` | `Normal, Effect, Ritual, Fusion, Synchro, Xyz, Link, Pendulum, Spell, Trap, Token, God` |
| `CardFrame` | `.frame` | `Normal, Effect, Ritual, Fusion, Sync, Xyz, Link, Pend, Magic, Trap, …` |
| `CardKind` | `.kind` | `Normal, Effect, Tuner, Toon, Spirit, Flip, Maximum, …` |
| `CardIcon` | `.icon` | `Normal, Continuous, Equip, QuickPlay, Field, Ritual, Counter` |

`simple_kind` is the **archetype-agnostic kind** (one per card, derived from the frame) — the easy filter
for "is this a Spell / a Synchro / a Normal monster". `frame`/`kind`/`icon` give finer detail.

```lua
on("buff", function(c)
  local p = card_props(c.cid)
  if p.simple_kind == CardSimpleKind.Normal then   -- a vanilla (Normal) monster
    return { atk = 300 }
  end
end)
```

> The `DuelPhase` enum (`Draw`/`Standby`/`Main1`/`Battle`/`Main2`/`End`) is also exposed — used by the
> [`phase`](#phase) event, not by `card_props`.

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

### `summon` / `special_summon`

Two separate events:

- **`summon`** — a monster is **Normal Summoned / Set-summoned to face-up** (the `RunSummon` view).
- **`special_summon`** — a monster is **Special Summoned** (the `RunSpSummon` view).

Both fire for **both** the player and the cpu, and both carry a **`status`** that tracks the attempt:

| status | when |
|---|---|
| `"init"` | the summon was performed (card is on the field) |
| `"success"` | the engine confirmed it (`CutinSuccess`) |
| `"failed"` | the pending card was destroyed before success (e.g. a negation) |

So a normal flow fires `init` then `success`; a negated one fires `init` then `failed`. Gate on
`e.status == "success"` for "after it resolves".

**Context `e`:** `{ uid, cid, race, attr, level, atk, def, player_id, player_type, location, zone, status }`

```lua
on("summon", function(e)
  if e.status == "success" and e.player_type == "player" then
    -- "when you Normal Summon, ..."
  end
end)
```

### `set`

Fires when a card is **Set face-down** (monster or spell/trap, both sides), with the same `status`
field. A monster Set raises `success` (via `CutinSuccess`); a spell/trap Set has no success view, so it
stays at `init`.

**Context `e`:** `{ uid, cid, race, attr, level, atk, def, player_id, player_type, location, zone, status }`

```lua
on("set", function(e)
  if e.status == "init" and card_props(e.cid).simple_kind == CardSimpleKind.Trap then ... end
end)
```

### `turn`

Fires on every **turn change**. Use it to reset per-turn state ("until end of turn").

**Context `t`:** `{ player_id, player_type }` — the player whose turn is **starting** (the previous turn
just ended).

```lua
on("turn", function(t)
  if t.player_type == "player" then
    -- start of YOUR turn
  end
end)
```

### `phase`

Fires on every **phase change** (Draw → Standby → Main1 → Battle → Main2 → End), for the turn player.

**Context `p`:** `{ phase }` — the new phase as a `DuelPhase` name (`"Draw"`, `"Standby"`, `"Main1"`,
`"Battle"`, `"Main2"`, `"End"`). The phase carries no player; combine with [`turn`](#turn) to know whose
phase it is.

```lua
local mine = false
on("turn",  function(t) mine = t.player_type == "player" end)
on("phase", function(p)
  if mine and p.phase == DuelPhase.Battle then
    -- start of YOUR Battle Phase
  end
end)
```

> More events (`attack`, `activate`, `destroy`, `draw`, …) ride the same view bus and will follow the
> same shape — a context carrying at least the `uid`, from which `card_state(uid)` resolves the rest.

---

## Reading the duel

All read functions are available from any hook.

| function | returns |
|---|---|
| `card_props(cid)` | static props `{ cid, race, attr, level, atk, def, simple_kind, frame, kind, icon }`, or `nil` |
| `card_state(uid)` | **live** state of an instance (see below), or `nil` if the instance is gone |
| `field_uid(player, zone)` | `uid` of the monster in `zone` (0–6) for `player`, or `nil` if empty |
| `deck_top(player)` | `cid` of the top deck card, or `nil` |
| `hand_count(player)` | cards in hand (`int`) |
| `deck_count(player)` | cards in main deck |
| `grave_count(player)` | cards in graveyard |
| `extra_count(player)` | cards in extra deck |
| `banish_count(player)` | banished cards |
| `log(x)` | print `x` to the console (tables print as `{ k=v, … }`) |

**`card_state(uid)`** is read straight from the engine, so it always reflects the *current* state:

```
{ uid, cid, race, attr, level, atk, def, player_id, player_type, location, zone? }
```

- `location` is `"field"`, `"hand"`, `"grave"`, `"deck"`, `"extra"`, or `"banish"`.
- `zone` (0–6) is present only when `location == "field"`.
- A field card returns **live** atk/def (effects applied); an off-field card returns the printed values.

`uid` identifies a specific copy (vs `cid` = the card type); it is stable while the card stays in a
spot. Get one from an event context (`e.uid`), from `field_uid(player, zone)`, or from `c.uid` in a
`buff` query.

---

## Actions

Actions **change the duel**. They must be called **from inside a hook** — i.e. during active duel
resolution (when an event fires). Called from an idle moment they will not take effect, because the
engine only pumps the command/placement queue while it is actively resolving.

Most card-targeting actions take a **card table** `{ player_id, location, index }` — the same shape a
chosen card already has, so a card from `select_card` passes straight through. `location` accepts a
name (`"deck"`, `"grave"`, …) or a raw code (`0`–`6` field zones, `13/15/16/17` piles).

### `special_summon`

```lua
special_summon{ player_id = 0, location = "deck", index = 0 }   -- returns true if queued
```

Special Summons one card. The table is the **source** (`player_id` = owner; `location`/`index` = where
it comes from). The **destination zone is chosen by the controller** (UI prompt for you, AI for the cpu).
Options (defaults = a plain face-up attack SS to your side):

- **`face`** — `1` face-up (default), `0` face-down.
- **`turn`** — `0` attack (default), `1` defense (the atk/def rotation).
- **`reason`** — engine reason code (default `0`).
- **`player_control`** — which side **controls** the summoned card (default you). Set it to the opponent
  to give them the card; combined with an opponent-owned source, it lets you **steal** a card to your field.

### `to_grave` / `to_hand` / `banish` / `destroy`

Per-operation aliases over `debug_command`, each taking a card table `{ player_id, location, index }`:

```lua
to_hand{ player_id = 0, location = "deck", index = 0 }   -- draw the top of your deck
to_grave{ player_id = 1, location = "deck", index = 0 }  -- mill the opponent's top deck
banish(card)                                              -- banish face-up
destroy(card)                                             -- destroy -> graveyard
```

### `debug_command`

The raw engine "swiss-army-knife" (`DLL_DuelComDoDebugCommand`) — the low-level escape hatch the aliases
are built on.

```lua
debug_command{ player_id = 0, location = "deck", index = 0, cmd = 6 }
```

- **`location`** — name or code: `13`=hand, `15`=deck, `16`=grave, `17`=banish, `0`–`6`=monster zones.
- **`cmd`** — the operation (below). Some `cmd` repurpose `location`/`index` (e.g. a value).

| cmd | action | notes |
|---|---|---|
| `0` | change phase / turn | `index` = phase; `location=0x12` swaps turn player |
| `3` / `4` | +LP / −LP | `location` = amount |
| `6` | → hand (any pile; deck = **draw**) | |
| `7` | → top of deck | |
| `8` | → graveyard | |
| `9` / `10` | banish face-up / face-down | |
| `11` | destroy → graveyard | |
| `20` | shuffle deck | |
| `23` / `24` | **set ATK / set DEF** | `location` = monster zone (0–6), `index` = value; counts in damage calc. Absolute set, resets when the card is flipped face-down. |

Full `cmd` catalogue and the RE detail: `Docs/design/duel-action-primitives.md`.

### `run_effect` / `effect_activate_*`

Play a **view-only** animation (a cutin/highlight) without touching the real effect — for flavour.

```lua
run_effect(id, p1, p2, p3)              -- id = a DuelViewType number OR its name ("CutinActivate", …)
effect_activate_zone(player, zone)      -- "effect activates" flash on the field monster at (player, zone)
effect_activate_card(player, cid)       -- pop the card art (cid) on the side (works off-field too)
```

### `activate_effect`

Drive a **real chain-link activation** of an existing `effect_id` (an engine effId / card id), attributed
to the card at `uid` — full engine resolution (validation, cost, targeting, effect), no cid change.

```lua
activate_effect{ player_id = 0, effect_id = 5323, category = 3, zone = 2, uid = 2, effect_number = 0, ctx = 0 }
```

- **`effect_id`** — the effId to activate (shares the card-id space).
- **`category`** — must match the effId's type: `0` spell/trap, `2` pile, `3` monster (default `3`).
- **`zone`** — the carrier card's real zone (a number or a `CardPos` name).
- **`uid`** — that carrier card's uid.
- **`effect_number`** — which of the card's effects (default `0` = the first).
- **`ctx`** — engine context (default `0`).

### `chain_effect`

Run an **arbitrary scripted effect on a real chain**. It opens a chain by activating a **blank** effect —
the source card's own cid, when that card is a **Normal (effect-less) monster**, is an effId with an empty
program, so the chain opens/animates/closes with no effect of its own. We then run our callbacks at the
engine's two phases, and they apply our own primitives:

```lua
chain_effect{
  source_uid = uid,                 -- a Normal monster YOU control on the field (zone 0-6); its cid = the blank effId
  cost = function() ... end,        -- optional; runs at the cost phase (CardHappen), before the effect
  effect = function() ... end,      -- runs at the resolution step (ChainStep)
}
```

This gives a real chain link (cost/timing/negation windows) carrying a custom cost+effect, without an
existing engine effId. The callbacks call the same actions as any hook (`special_summon`, `destroy`,
`to_grave`, `select_card`, …). Call `chain_effect` during your priority (e.g. from a `summon` hook).

### `select_card`

Raise a **card-selection modal** over the candidates that pass a filter, and call `result` with the chosen
card on confirm. **Callback-based** (no coroutine) — works from any hook, including `chain_effect`'s
`cost`/`effect`.

```lua
select_card{
  from   = "deck",                       -- a location name or a list of names; default "grave"
  player = 0,                            -- who picks (default you)
  filter = function(card) return ... end,-- runs during the candidate build; return true to include
  result = function(card)                -- called when the player confirms, with the chosen card table
    if card then special_summon(merge(card, { player_control = 0 })) end
  end,
}
```

- `filter` receives a **card table** (`{ uid, cid, player_id, player_type, … }` — same as `card_state`).
- `result` receives the chosen card table (or is never called if there are **no candidates**).
- `allow_special_summon_from_grave(card)` → `bool`: engine-accurate "can this GY card be revived" check
  (respects properly-summoned / "cannot be Special Summoned"), handy inside a `filter`.

---

## Table helpers: `merge` / `spread`

```lua
merge(t1, t2, ...)    -- shallow map-merge; later keys win (defaults + overrides)
spread(l1, l2, ...)   -- concatenate the list/array parts in order
```

`merge` is the idiomatic way to pass a chosen card through to an action with extra options:
`special_summon(merge(card, { player_control = 0 }))`.

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

**+500 ATK on Normal Summon, until end of turn** (`summon_atk_buff.lua`) — `summon` + `buff` + `turn`
sharing a script-local table:

```lua
local buffed = {}                                   -- uid -> true, for monsters summoned this turn
on("summon", function(e)
  if e.status == "success" and e.player_type == "player" then buffed[e.uid] = true end
end)
on("buff", function(c)
  if buffed[c.uid] then return { atk = 500 } end
end)
on("turn", function() buffed = {} end)              -- new turn -> the +500 expires
```

**Normal Summon → mill, then revive from GY on a real chain** (`chain_effect` + `select_card`):

```lua
on("summon", function(e)
  if e.status ~= "success" or e.player_type ~= "player" then return end
  chain_effect{
    source_uid = e.uid,
    cost   = function() to_grave{ player_id = 0, location = "deck", index = 0 } end,   -- cost: mill
    effect = function()
      select_card{
        from   = "grave",
        filter = function(card) return allow_special_summon_from_grave(card) end,
        result = function(c) if c then special_summon(merge(c, { player_control = 0 })) end end,
      }
    end,
  }
end)
```

---

## Dev commands

While iterating, scripts can be loaded/inspected from the in-game console:

- `rghook load <file.lua> [params-json]` — load a script with a params table.
- `rghook clear` — drop all hooks. `rghook list` — list registered hooks.
- `rglua <code>` / `rglua file <name.lua>` — run Lua on the duel engine.
- `rgcardprops <cid>` — dump a card's static props (`simple_kind`/`frame`/`kind`/`icon` + stats).
- `rgeff [on|off]` — log the view-event bus (DuelViewType + params) — what the hooks ride on.
- `rgcast <player> <category> <zone> <effId> <uid> [ctx]` — manual `activate_effect` (real chain).
- `rgpile <player> <location>` / `rguid <player> <zone>` — list a pile / get a field card's uid+cid.
