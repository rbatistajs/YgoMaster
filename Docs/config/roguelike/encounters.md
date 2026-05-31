# Roguelike `Encounters.json` Reference

The roguelike's combat opponents are a **curated pool** defined in
**`DataLE/Roguelike/Encounters.json`**. Each combat node (`duel`, `elite`, `boss`) draws its enemy
from this pool instead of picking a random deck. An encounter names the deck to fight, plus optional
**gating** (which act / floor / ascension it may appear at) and **overrides** (enemy LP, reward, who
goes first).

**Strict mode:** this file is **required**. If a combat node finds no eligible encounter, it logs an
error and **does not start a duel** (no random fallback). Author enough encounters to cover every
combat node your map can produce.

**When changes apply:** the file is read once and cached, so **restart the server** after editing.
Changes affect **new picks** only (an in-progress duel keeps the enemy it was built with).

**Decks live in `DataLE/Roguelike/Opponents/`** (`.json` or `.ydk`). `Encounters.json` references
them by filename via the `deck` field — that folder is the deck store; this file is the curation
layer on top of it.

---

## Contents

- [Structure](#structure)
- [Fields](#fields)
- [How an enemy is chosen](#how-an-enemy-is-chosen)
- [Gating (act / floor / ascension)](#gating-act--floor--ascension)
- [Overrides (LP, reward, first player)](#overrides-lp-reward-first-player)
- [Modifiers (starting board)](#modifiers-starting-board)
- [Actions (options / message / openpack)](#actions-options--message--openpack) — [stat actions (`lp` / `gold`)](#lp--gold-stat-actions) · [`addCard`](#addcard)
- [Actions.json and type defaults](#actionsjson-and-type-defaults)
- [Defaults & relationship to Settings.json](#defaults--relationship-to-settingsjson)
- [Strict coverage — avoiding soft-locks](#strict-coverage--avoiding-soft-locks)
- [Full example](#full-example)

---

## Structure

A JSON object **keyed by node type**. Each value is an array of encounter objects. The type is
implied by the key, so entries have no `type` field.

```json
{
  "duel":  [ { ...encounter... }, { ...encounter... } ],
  "elite": [ { ...encounter... } ],
  "boss":  [ { ...encounter... } ]
}
```

Only `duel`, `elite`, and `boss` are combat today. `event` / `shop` / `reward` keys are allowed and
parsed but inert until those node actions exist.

---

## Fields

| Field | Required | Default / fallback | Notes |
|---|---|---|---|
| `id` | yes | — | Unique across the whole file. |
| `name` | no | the `deck` filename (no extension) | Shown on the boss node and as the opponent's name. |
| `text` | no | `""` | Flavor; reserved for future `event` nodes. |
| `deck` | yes | — | File in `Roguelike/Opponents/` (`.json`/`.ydk`). An extension is recommended. |
| `act` | no | always eligible | `{ "min": x, "max": y }`, inclusive, 0-based act. |
| `floor` | no | always eligible | `{ "min": x, "max": y }`, inclusive; floor = the node's row. |
| `ascension` | no | always eligible | `{ "min": x, "max": y }`, inclusive. |
| `weight` | no | `1.0` | Relative weight when several encounters are eligible (all-equal = uniform). |
| `enemyLp` | no | `Settings.enemyLp[type]` (scaled) | Override the enemy's starting LP. |
| `reward` | no | `RewardFor(type)` (boss 1000 / elite 250 / duel 100) | Override run currency on win. |
| `firstPlayer` | no | random (seeded) | `0` = you go first, `1` = the enemy goes first. |
| `cpuRate` | no | `Settings.cpuRate` (100) | CPU AI strength −100…100 (100 = max). |
| `cpuFlag` | no | `Settings.cpuFlag` (None) | AI behavior: `None`/`Def`/`Fool`/`Light`/`MyTurnOnly`/`AttackOnly`/`Simple`. |
| `modifiers` | no | none | Scripted starting board + LP/hand deltas — see [Modifiers](#modifiers-starting-board). |
| `action` | no | type default (if any) | Action tree fired after the encounter resolves. Object (inline), string (ref into [`Actions.json`](#actionsjson-and-type-defaults)), `null` (no action — overrides the type default), or absent (use type default). Combat encounters fire it on win; non-combat (`event`) on arrival. See [Actions](#actions-options--message--openpack). |

In each range block (`act` / `floor` / `ascension`) either `min` or `max` may be omitted for an
open-ended bound. An omitted block means "no restriction on that axis".

---

## How an enemy is chosen

- **`duel` / `elite` — chosen on arrival.** When you step onto the node, the server lists every
  encounter of that type whose ranges contain the current act, the node's floor, and your ascension,
  then picks one at random by `weight`. The pick is deterministic (seeded by the run + node), so an
  unfinished duel **resumes with the same enemy and the same draws** — you can't quit to re-roll.
- **`boss` — chosen when the map is generated.** Each act's boss is picked the moment the act's map
  is built and baked into the boss node, so the **map shows the boss's `name` before you fight it**.

---

## Gating (act / floor / ascension)

Ranges restrict where an encounter may appear. All present ranges must contain the value for the
encounter to be eligible.

```json
{ "id": "act1_only",   "deck": "X.json", "act":   { "min": 0, "max": 0 } }
{ "id": "deep_floors", "deck": "Y.json", "floor": { "min": 4 } }
{ "id": "high_asc",    "deck": "Z.json", "ascension": { "min": 3 } }
```

- `act` 0 = the first act. The boss lives on the top row of each act, so gate bosses by **act**, not
  floor.
- `floor` is the node's row (0 = entry row). A `duel`/`elite` with `floor:{min:4}` only appears from
  row 4 up.
- `ascension` lets you reserve nastier decks for higher difficulty tiers.

---

## Overrides (LP, reward, first player)

Any encounter can override the per-type defaults:

```json
{ "id": "boss_exodia", "name": "The Forbidden One", "deck": "Exodia.json",
  "act": { "min": 2 }, "enemyLp": 13000, "reward": 4000 }
```

- `enemyLp` — absolute LP for this enemy. **Note:** an explicit value bypasses the per-act /
  ascension scaling that `Settings.json` applies to the type default. Omit it to keep that scaling.
- `reward` — flat run currency credited on win, replacing the type's default reward.
- `firstPlayer` — force the turn order (`0` you, `1` enemy). Omit for a seeded coin flip.
- `cpuRate` / `cpuFlag` — tune the opponent's AI. `cpuRate` is its strength (−100…100, default 100 =
  full); `cpuFlag` picks a behavior mode — `Fool` (plays badly), `Light` (weaker logic), `MyTurnOnly`
  (won't respond on your turn), `AttackOnly`, `Simple`, `Def` (defensive), or `None` (default full
  AI). Handy for making early/low-tier duels easier without changing the deck.

---

## Modifiers (starting board)

`modifiers` scripts the **opening board** of a duel — cards already on the field / in hand for each
side — plus small **LP/hand deltas**. Two sides: **`player`** (you) and **`enemy`** (this opponent).

```json
"modifiers": {
  "enemy": {
    "fieldSpell": { "cid": 30241314 },
    "monsters":   [ { "cid": 5381, "pos": "atk" }, null, { "cid": 22222, "pos": "def" } ],
    "spellTraps": [ { "cid": 44095762, "pos": "set" } ],
    "hand":       [ { "cid": 55144522 } ],
    "extraLp":    2000,
    "extraHand":  1
  },
  "player": { "extraHand": -1 }
}
```

- **`fieldSpell`** — one card, placed face-up active.
- **`monsters`** — slots 0–4 (M1–M5); `null` skips a slot. `pos`: `atk` / `def` / `set` (face-down
  defense) / `facedown` / `atk_fd` / `def_fd` (default `atk`).
- **`spellTraps`** — slots 0–4 (S1–S5). `pos`: `set` (face-down, default) / `face_up`.
- **`hand`** — extra cards placed in hand.
- **`extraLp`** / **`extraHand`** — additive deltas (may be negative). They **sum across layers** and
  apply on top of the base LP (`enemyLp` / your run LP) and base hand (5 Normal / 4 Rush) — they do
  not replace those. There is no separate hand-size key; use `extraHand`.

`cid` is the card's internal id — the same numbers used in deck `ids`. Cards past the 5 slots are
ignored; an unknown `cid` is silently skipped by the engine.

Modifiers merge with per-type defaults from `Settings.json` (`modifierDefaults`): **defaults first,
then the encounter on top** — cards merge by slot, `extraLp`/`extraHand` sum.

**Random cards.** Instead of `{ "cid": N }`, a card can be drawn at random from a deck or the card
pool — picked server-side and seeded, so a resumed duel reproduces it:

```json
{ "random": "monster", "subtype": "effect", "minAtk": 2000, "source": "deck", "deck_owner": "own" }
```

- `random` — `monster` / `main_monster` / `extra_monster` / `spell` / `trap` / `field_spell` /
  `spell_or_trap` (omit or `any` = no kind filter).
- `subtype` — monster: `normal` / `effect` / `ritual` / `fusion` / `synchro` / `xyz` / `link`;
  spell/trap: `normal` / `counter` / `field` / `equip` / `continuous` / `quickplay` / `ritual`.
- `minAtk` / `maxAtk` / `minDef` / `maxDef` / `minLevel` / `maxLevel` — numeric filters (monsters).
- `rarity` / `rarities` — hard filter by `CardList.json` rarity. `rarity: "UR"` or
  `rarities: ["SR","UR"]` (values: `N` / `R` / `SR` / `UR`). Applies to `any` / `link` /
  `deck` sources. Cards missing a rarity entry are excluded when the filter is set.
- `source` — `deck` (default) draws from a deck via `deck_owner` (`own` (default) / `rival` / `p1` /
  `p2`); `any` draws from the configurable card pool (below); `link` draws from cards **related**
  (`CARD_Link`) to the `deck_owner` deck's cards, kept only if also in the pool. Duplicate picks on a
  side are avoided; an empty pool drops that card.

**`source: "any"` pool** — `DataLE/Roguelike/CardPool.json` defines the pool `any` draws from (and
that card rewards will reuse):

```json
{
  "regulation": "Goat Format",
  "include": [12345],
  "exclude": [67890],
  "rarityRates": { "N": 8, "R": 4, "SR": 2, "UR": 1 },
  "rateGroups": [ { "cids": [5381, 5655], "rate": 5 }, { "cids": [9999], "rate": 0 } ],
  "pity": {
    "UR": { "increment": 10, "max": 50, "reset_on": ["UR"] },
    "SR": { "increment": 5,  "max": 30, "reset_on": ["SR", "UR"] }
  },
  "byAscension": [ {}, { "include": [99999], "rarityRates": { "UR": 3 } } ]
}
```

- **Membership** — base = `CardList.json` minus the configured (or server-default) regulation's
  banlist; then global `include`/`exclude`, then `byAscension[asc]` (exact ascension index).
- **Weighting** — `any` / `link` picks are weighted (not uniform). A card's weight =
  `rarityRates[rarity]` × the product of `rateGroups` rates it belongs to (default 1). `rarityRates`
  keys are `N` / `R` / `SR` / `UR` (or `1`–`4`, from `CardList.json`); a group `rate` of `0` makes
  its cards never appear. `byAscension[asc].rarityRates` override the global per rarity; its
  `rateGroups` stack with the global ones.
- `source: "deck"` picks stay uniform. Missing file = the default regulation's pool, uniform.
  Cached — restart the server to apply.
- **Pity** — only used by `openpack` actions (below). Per-rarity counter on the run that grows
  every pack which didn't yield a card of that rarity, adding a bonus to that rarity's weight
  on the next pack until the counter resets. `increment` = bonus added per missed pack,
  `max` = clamp on the accumulated bonus, `reset_on` = rarities that, when drawn, zero the
  counter (default `[r]`). `byAscension[asc].pity` merges per-rarity per-field with the global.

---

## Actions (`options` / `message` / `openpack`)

An encounter's `action` is a tree of action nodes the server walks after the encounter resolves
(on win for combat, on arrival for `event`). The client renders prompts; the server applies state.

### Action kinds

| `type` | What it does |
|---|---|
| `options` | Branch — show a list of labeled choices; player picks one and the engine descends into that option's `next`. |
| `message` | Show text with a single OK; advances to `next` (or completes when `next` is null/missing). |
| `openpack` | Open one or more packs of cards with weighted draws and pity, then either keep all or pick X of N. Adds the resolved cards to the run's pool / deck, then advances to `next`. |
| `lp` | Apply an LP delta to the run (heal/damage). No prompt — fires and advances to `next`. LP ≤ 0 is game over. See [stat actions](#lp--gold-stat-actions). |
| `gold` | Apply a gold delta to the run (gain/cost). No prompt — fires and advances to `next`. See [stat actions](#lp--gold-stat-actions). |
| `addCard` | Add specific card(s) to the run (collection + deck routing), with a fly-to-deck animation. See [`addCard`](#addcard). |

```json
"action": {
  "type": "options",
  "text": "Um viajante oferece uma escolha…",
  "options": [
    { "label": "Abrir o baú", "next": { "type": "message", "text": "Você pegou uma carta." } },
    { "label": "Seguir",      "next": null }
  ]
}
```

**Continuations use `next`.** Inside an action tree, every node may have a `next` field that
points to the action that follows: `option.next` (per branch under `options`), `openpack.next`
(after the pack is finalized), and `message.next` (after the OK is acknowledged). The top-level
field on an encounter is `action` (the entry point); everything inside is chained with `next`.
An option whose `next` is `null` or missing ends the tree.

### `openpack`

**Concepts — `pool`, `pull`, `pulls`, `packs`** (they nest, and are easy to mix up):

- **`pool`** — *where* cards come from: the eligibility + weight spec (`random` type, `rarity` /
  `rarities`, `rarityRates`, numeric filters). Think "the bag of eligible cards". Not plural of
  anything — same fields as the modifiers' [random spec](#modifiers-starting-board).
- **`pull`** — *one draw*: take `count` cards from **one** `pool`, with an optional `chance` of
  happening. A pull **contains** a pool.
- **`pulls`** — the *list of draws*. Each entry is a `pull` with its own `pool` + `count`, so a
  single reward can mix sources (e.g. 6 monsters + 1 spell + a 50%-chance UR).
- **`packs`** — how many times the whole `pulls` list is rolled (default 1).

Nesting: **`packs` → `pulls` → each `pull` → one `pool`**. Total cards = `packs × sum(pulls[].count)`.
(`addCard` reuses the exact same `pulls`/`pool`, plus a `pool` + `count` top-level shorthand for a
single pull — see [`addCard`](#addcard).)

```jsonc
{
  "type": "openpack",
  "packs": 3,                 // how many packs to open in sequence (default 1)

  // pick accepts three shapes:
  //   0 / missing  -> keep all (no selection UI; player just confirms the result)
  //   N (int > 0)  -> exact pick (must select exactly N)
  //   { min, max } -> range pick (must select between min and max, inclusive)
  // max is automatically clamped to the rolled pack size at stage time.
  // Use min: 0 to make the selection optional (player can confirm with nothing selected,
  // discarding the reward).
  "pick": { "min": 1, "max": 3 },

  "pulls": [
    { "count": 6, "pool": { "source": "any", "random": "monster" } },
    { "count": 1, "pool": { "source": "any", "random": "spell"   } },
    { "count": 1, "chance": 0.5,
      "pool": { "source": "any", "random": "monster", "rarity": "UR" } }
  ],

  // Optional pity override merged per-rarity with CardPool.json's global+ascension pity.
  // Set "pity": false to disable pity entirely for this action.
  "pity": { "UR": { "increment": 20 } },

  // Optional UI labels (Brazilian Portuguese defaults via RoguelikeLabels).
  // title_pick    placeholders: {0} = max picks, {1} = total shown, {2} = min picks.
  // confirm_label placeholders: {0} = live count, {1} = max picks, {2} = min picks.
  // Existing templates using only {0}/{1} keep working: when min == max, {1} reads the same
  // number the older "pick required" did.
  "title_keep":    "Cartas obtidas",
  "title_pick":    "Selecione de {2} a {0} cards (de {1})",
  "confirm_label": "Confirmar {0} ({2}-{1})",

  // Next action after the player confirms; same shape as `option.next`. Null/missing ends the tree.
  "next": null
}
```

- **`pulls`** — ordered list of draws **per pack**. Pack size = `sum(pulls[].count)`. Each of the
  `packs` packs rolls the pulls independently. `chance` (default 1.0) is a per-pull probability;
  a failed pull is skipped (that pack ends up with fewer cards). Each pull's `pool` is a random
  spec (same fields as the modifiers' [random spec](#modifiers-starting-board)), so `source`,
  `random`, `subtype`, numeric filters, and the new `rarity` / `rarities` filter all work.
- **`pool.excludeOwnedAtLimit`** *(bool, default false)* — when true, never draw a card the run
  already owns at its copy limit (standard 3, reduced by the regulation banlist: limited = 1,
  semi = 2, forbidden = 0). Owned copies are counted by **canonical** card (`CARD_Same`), so alt
  arts count together. Useful for reward pools that should keep handing out *new* / still-usable
  cards instead of dead duplicates. (Works in `openpack` and `addCard`.)
- **`pick` vs total** — pick bounds are over the **total** shown in the result UI. With
  `packs: 3` and `sum(pulls.count) = 8`, the result shows 24 cards; `pick: 5` makes the player
  choose exactly 5, `pick: { min: 1, max: 5 }` lets them pick between 1 and 5.
- **`pick` clamping** — `max` is clamped down to the rolled size automatically (asking for 10
  picks from a 5-card pack just allows up to 5). `min` is validated at load time and must not
  exceed total size, otherwise the action would be unfinishable.
- **Pity** — every pack ticks the run's per-rarity pity counter (no card of rarity `r` → `pity[r]++`;
  any card whose rarity is in `reset_on[r]` → `pity[r] = 0`). The bonus
  `min(pity[r] * increment, max)` is added to `rarityRates[r]` on the next pack. Merge order:
  global `CardPool.json` → `byAscension[asc]` → `action.pity` (per-rarity, per-field). `pity: false`
  on the action turns it off for that action (no bonus, no ticking).
- **Determinism** — all draws are seeded by the run + node + pack index. A resumed pack picks the
  same cards. Re-rolling requires advancing the run.
- **State** — cards are added to the run's pool (and to the deck in keep-all mode) when the player
  confirms. The action then advances to `next`.

### `lp` / `gold` (stat actions)

Apply a delta to a run-level stat. **Fire-and-forget**: the engine applies the change and advances
to `next` without waiting for any player input (no modal, no OK). The HUD animates the change on its
own — a colored number floats from the center of the screen to the HUD label, then the label counts
up/down to the new value.

```jsonc
{ "type": "lp",
  "delta": -500,            // OR
  "delta_percent": -0.30 }  // exactly one is required
```

```jsonc
{ "type": "gold",
  "delta": 200,             // OR
  "delta_percent": -0.20 }  // exactly one is required
```

| Field | Required | Notes |
|---|---|---|
| `delta` | XOR | Absolute int. Positive = heal/gain, negative = damage/cost. |
| `delta_percent` | XOR | Fraction. **`lp`**: of `maxLp`. **`gold`**: of the **current** gold. Out of `[-1, 1]` → warned at load, clamped at apply. |
| `next` | no | Chain another action when this one finishes (same shape as elsewhere). |

Exactly one of `delta` / `delta_percent` must be present — both or neither is a load-time error
(the encounter is dropped with a console warning).

**Clamping & game over:**
- `lp` clamps to `[0, maxLp]`. If LP reaches **0**, the run ends (game over — same path as a combat
  loss). Use this for risky events (`{ "type": "lp", "delta_percent": -0.80 }`).
- `gold` clamps at **0** (no negative gold) with no upper cap. A cost larger than the current gold
  just zeroes it; it never blocks the action or ends the run.

**Chaining example** — a message prologue, an effect, then an epilogue:

```json
"action": {
  "type": "message", "title": "O Altar", "message": "Você sangra na pedra.",
  "next": { "type": "lp", "delta": -500,
    "next": { "type": "message", "title": "Recompensa", "message": "Algo brilha no chão.",
      "next": { "type": "gold", "delta": 300 }
    }
  }
}
```

**Standalone** — no message wrapper, useful for passive triggers (e.g. a future "lose N LP per node
moved"):

```json
"action": { "type": "lp", "delta": -5 }
```

When multiple stat actions fire in one chain without a prompt in between (e.g. `lp → gold → lp`),
the HUD consolidates them into one animation per stat (it diffs the run state, so two `lp` steps
show as a single combined delta).

### `addCard`

Add one or more **specific** cards to the run (unlike `openpack`, which rolls from a pool). The
cards are added to the collection and routed into the deck by the same `deck.*` rules as openpack
picks (below `minCards` → mandatory main; under the per-section max → `autoAddToDeck`; at/over the
cap → collection only). The client plays an animation — each card renders full-art and flies from
the center of the screen to the deck button — then the action advances to `next`.

```jsonc
{ "type": "addCard", "cid": 5381 }                  // single explicit card
{ "type": "addCard", "cards": [5381, 5655, 4426] }  // several explicit cards

// Random — same pool/pulls/pity machinery as openpack, but no pick UI (all rolled cards are added):
{ "type": "addCard", "pool": { "source": "any", "random": "monster" } }            // 1 random monster
{ "type": "addCard", "count": 3, "pool": { "random": "monster", "rarity": "UR" } } // 3 random URs
{ "type": "addCard", "pulls": [                                                     // full openpack-style
  { "count": 1, "pool": { "random": "monster" } },
  { "count": 1, "pool": { "random": "spell" } }
] }
```

| Field | Required | Notes |
|---|---|---|
| `cid` | one of | Single explicit card id. |
| `cards` | one of | Array of explicit card ids. |
| `pool` | one of | Random spec (same as an openpack pull's `pool`). Rolls `count` cards (default 1). |
| `count` | no | How many to roll from `pool` (single-pull shorthand). Default 1. |
| `pulls` | one of | Array of `{ count, pool, chance }` — full openpack-style roll. |
| `pity` | no | Same as openpack: `false` to disable, or per-rarity overrides. Applies to the random roll. |
| `next` | no | Chain another action after the animation finishes. |

At least one of `cid` / `cards` / `pool` / `pulls` is required. Explicit cids and a random roll can
be combined (both are added). Random cards are drawn from the run's `any` pool (regulation + ascension
filtered), honoring `rarityRates`/`rarity`/`rarities` and pity — exactly like `openpack`. Chaining
example:

```json
"action": {
  "type": "message", "title": "Recompensa", "message": "Você recebe uma carta!",
  "next": { "type": "addCard", "cid": 6101,
    "next": { "type": "message", "title": "Pronto", "message": "Carta adicionada ao deck." }
  }
}
```

---

## `Actions.json` and type defaults

Two ways to keep `Encounters.json` clean when many encounters share the same action tree:

### Named action registry (`Actions.json`)

Optional file at `DataLE/Roguelike/Actions.json` — a flat map of `name → action tree`.
Anywhere an `action` (or `option.action`, or `openpack.next`) value is expected, a **string**
may be used instead of the inline object — the loader looks it up by name.

```json
// Actions.json
{
  "smallReward": {
    "type": "openpack",
    "packs": 1,
    "pulls": [ { "count": 3, "pool": { "source": "any", "random": "monster" } } ]
  },
  "bigBossReward": {
    "type": "openpack",
    "packs": 1,
    "pick": 1,
    "pulls": [ { "count": 5, "pool": { "source": "any", "rarity": "UR" } } ]
  },
  "treasureChest": {
    "type": "options",
    "text": "Um baú trancado…",
    "options": [
      { "label": "Abrir", "next": "smallReward" },
      { "label": "Seguir", "next": null }
    ]
  }
}
```

Then in `Encounters.json`:

```json
{ "id": "duel_basic", "deck": "Beatdown.json", "action": "smallReward" }
{ "id": "boss_a3",    "deck": "Exodia.json",    "action": "bigBossReward" }
```

- **Resolution is at load time.** The library is read once; each string ref is replaced with the
  actual tree before validation. Restart the server to apply file changes (same as `Encounters.json`
  / `CardPool.json`).
- **Shared by reference.** N encounters using the same name share the same tree object — the
  engine only reads, never mutates.
- **Strict refs.** A string that doesn't exist in `Actions.json` fails the encounter's load
  (logged and skipped). Same goes for cycles (`A → next: B → next: A`).
- **Nested refs.** Inside an action tree, every `next` (under `options[i]`, `openpack`, or
  `message`) may also be a string; they're resolved recursively. `null` and missing keys both end
  that branch.
- **Optional file.** No `Actions.json` = only inline trees and `null` work — nothing else changes.

### Type defaults (`defaultAction`)

A top-level `defaultAction` block in `Encounters.json` assigns a default action **per node type**.
Encounters of that type without an `action` key inherit the default; an explicit `null` opts out.

```json
{
  "defaultAction": {
    "duel":  "smallReward",
    "elite": "smallReward",
    "boss":  "bigBossReward"
  },
  "duel":  [
    { "id": "duel_basic",   "deck": "Beatdown.json" },                          // gets smallReward
    { "id": "duel_no_loot", "deck": "Final Countdown.json", "action": null }    // no action (opt out)
  ],
  "boss":  [
    { "id": "boss_a3", "deck": "Exodia.json", "act": { "min": 2 },
      "action": { "type": "message", "text": "…" } }                            // overrides default with inline
  ]
}
```

Each default value follows the same rules as an encounter's `action`: string ref into
`Actions.json`, inline object, or `null`. **Absent** on an encounter means "use default";
**null** on an encounter means "no action even if there's a default".

---

## Defaults & relationship to `Settings.json`

`Encounters.json` decides **which deck** you fight; `Settings.json` still provides the **defaults**
for anything an encounter doesn't override:

- enemy LP → `Settings.enemyLp[type]` (with `perAct` / `perAscension` / `ascensionScale` applied),
- reward → the built-in per-type amount,
- turn order → a seeded coin flip,
- CPU AI → `Settings.cpuRate` (100 = max) and `Settings.cpuFlag` (None = full default AI).

So the simplest encounter — just `id` + `deck` (+ optional `name`) — inherits the difficulty curve
you already tuned in `Settings.json`. Reach for overrides only when a specific enemy needs to break
from the curve.

---

## Strict coverage — avoiding soft-locks

Because there is no random fallback, **every combat node your map can generate must have at least one
eligible encounter**. Practical rules:

- Provide several **ungated** `duel` encounters — they cover every floor of every act (floor 0 is
  always a duel).
- Provide `elite` encounters that cover wherever elites can spawn (see `Settings.typeRules.elite`).
- Provide a `boss` for **every act index** `0 .. acts-1`. A boss with an open-ended `act` (e.g.
  `{ "min": 2 }`) conveniently covers that act and all later ones.

If a node finds nothing eligible, the server logs e.g.
`[Roguelike] no encounter for type elite act 1 floor 5 asc 0` (or `no boss encounter for act N`),
and the node won't start a duel. Watch the server console the first time you run a new pool.

---

## Full example

```json
{
  "duel": [
    { "id": "duel_beatdown", "deck": "Beatdown.json" },
    { "id": "duel_warrior",  "deck": "Warrior.json" },
    { "id": "duel_zombie",   "deck": "Zombie.json" },
    { "id": "duel_stall",    "name": "Stall Tactician", "deck": "Final Countdown.json",
      "floor": { "min": 4 }, "firstPlayer": 1 }
  ],

  "elite": [
    { "id": "elite_goat_control", "name": "Goat Master", "deck": "Goat Control.json", "enemyLp": 5000 },
    { "id": "elite_monarch",      "deck": "Monarch.json" },
    { "id": "elite_chaos_turbo",  "deck": "Chaos Turbo.json" }
  ],

  "boss": [
    { "id": "boss_a1_chaos",  "name": "Lord of Chaos",  "deck": "Chaos Control.json",       "act": { "max": 0 } },
    { "id": "boss_a2_ritual", "name": "The Ritual Lord", "deck": "Relinquished Control.json", "act": { "min": 1, "max": 1 } },
    { "id": "boss_a3_exodia", "name": "The Forbidden One", "deck": "Exodia.json",
      "act": { "min": 2 }, "enemyLp": 13000, "reward": 4000 }
  ]
}
```

This pool: three ungated duels (cover every floor) plus a deep-floor stall that goes first; three
elites (one tankier); and one boss per act, with the act-3 boss tankier and worth more — every other
LP/reward value comes from `Settings.json`.
