# Encounters — Design

**Goal:** Replace the random opponent-deck pick with a curated, data-driven encounter pool
(`Roguelike/Encounters.json`) keyed by node type. Each encounter names the deck to use plus
optional gating (act / floor / ascension ranges) and per-encounter overrides (enemy LP, reward,
who goes first). Regular `duel`/`elite` encounters are chosen lazily when the player reaches the
node; the act `boss` is chosen at map-generation time and baked into the node so the map can name
it ahead of the fight.

## Today (baseline)

`BuildRoguelikeDuel` picks a random file from `Roguelike/Opponents/` (seeded by run+act+node),
loads it as the CPU deck, and sets enemy LP from `Settings.enemyLp[type]` (else `default` else
2000). Reward is fixed per type via `RewardFor(type)` (boss 1000 / elite 250 / duel 100). The map
node carries only `type` (no enemy identity). There is no curation: any deck in the folder can
appear at any combat node.

## Config (`Encounters.json`)

A dict keyed by node type (matches the rest of the config — `enemyLp`, `typeWeights`,
`forcedRows` are all keyed by type). Each value is an array of encounter objects. `event` /
`shop` / `reward` keys are allowed and parsed but inert until those node actions exist (M4+).

```json
{
  "duel": [
    {
      "id": "burn_goblin",
      "name": "Goblin Piromante",
      "text": "Gosta de ver tudo queimar.",
      "deck": "BurnAggro.json",
      "act":       { "min": 0, "max": 0 },
      "floor":     { "min": 1, "max": 3 },
      "ascension": { "min": 0 },
      "weight": 1.0,
      "enemyLp": 2500,
      "reward": 150,
      "firstPlayer": 1
    }
  ],
  "elite": [
    { "id": "twin_serpents", "name": "Serpentes Gêmeas", "deck": "ReptileBeatdown.json",
      "floor": { "min": 4 }, "enemyLp": 4500 }
  ],
  "boss": [
    { "id": "act1_boss_dragon", "name": "Senhor dos Dragões", "deck": "DragonRamp.json",
      "act": { "min": 0, "max": 0 }, "enemyLp": 9000, "reward": 1500 },
    { "id": "act3_boss_exodia", "name": "O Proibido", "deck": "ExodiaFTK.json",
      "act": { "min": 2 }, "ascension": { "min": 3 }, "enemyLp": 12000, "reward": 3000 }
  ]
}
```

### Fields

| Field | Required | Default / fallback | Notes |
|---|---|---|---|
| `id` | yes | — | Unique across the whole file; persisted as the run's pending encounter. |
| `name` | no | `deck` filename (no ext) | Display name (shown on the boss node). |
| `text` | no | `""` | Flavor; useful for future `event` nodes. |
| `deck` | yes | — | File in `Roguelike/Opponents/` (`.json`/`.ydk`); resolved at duel build. |
| `act` | no | always | `{min,max}` inclusive (0-based act). Either bound omittable = open-ended. |
| `floor` | no | always | `{min,max}` inclusive; floor = the node's `row`. |
| `ascension` | no | always | `{min,max}` inclusive (tier the run is played at). |
| `weight` | no | `1.0` | Relative weight in the eligible pool (uniform when all 1). |
| `enemyLp` | no | `Settings.enemyLp[type]` | Override CPU starting LP for this encounter. |
| `reward` | no | `RewardFor(type)` | Override run currency credited on win. |
| `firstPlayer` | no | random (seeded) | `0` = player first, `1` = CPU first; else seeded coin flip. |
| `cpuRate` | no | `Settings.cpuRate` (100) | CPU AI strength −100…100 (100 = max). |
| `cpuFlag` | no | `Settings.cpuFlag` (None) | DuelCpuParam name; invalid → warn + fall back. |

Type is implied by the key, so encounter objects have no `type` field. A missing gating block
means "always eligible" on that axis. `floor` uses absolute rows (no negative-index sugar — keep
it simple; the boss is the top row, set its `act` instead).

## Selection & determinism

**Eligibility:** an encounter under key `T` is eligible for a combat node of type `T` at the
current `act`, the node's `floor` (= `row`), and the run's `ascension` when each present range
contains the value. Absent ranges always pass.

- **`duel` / `elite` (lazy, at arrival):** in `BuildRoguelikeDuel`, build the eligible list for
  `(type, act, floor, ascension)`, then weighted-pick using `new Random(DuelRngSeed(seed, act,
  nodeId))`. The pick is the first RNG draw, so resume re-derives the same encounter (and the same
  `firstPlayer` / `RandSeed` that follow) — a player can't quit to re-roll.
- **`boss` (at generation):** when the map is built (`choose_deck` and on advancing acts), pick a
  boss encounter from the eligible boss pool using `new Random(DuelRngSeed(seed, act, bossNodeId))`
  and store its `id` + `name` on the boss `MapNode`. At duel build, the boss path reads the baked
  `encounter` id instead of re-picking.

Both paths set `run.PendingEncounterId` so `duel_result` can credit the right reward.

## Strict (no fallback)

If no encounter is eligible:
- **boss at generation:** log an error (`type/act/ascension`); leave the boss node's `encounter`
  empty.
- **duel/elite at arrival, or boss with no baked id:** `BuildRoguelikeDuel` logs an error
  (`type/act/floor/ascension`) and returns `false`, so the node does not start a duel
  (`PendingDuelNode` stays `-1`).

This is a config error for the modder to fix — there is no random `Opponents/` fallback. The
failure is loud (server console) rather than a silent wrong-deck fight. The `Opponents/` folder
remains the deck-file store, referenced by `deck`; only the random *selection* is removed.

## State

- **`RoguelikeRun`** adds `PendingEncounterId` (string, default `""`): the encounter chosen for
  the in-progress combat. Serialized in `roguelike.json`; cleared in `duel_result`.
- **`MapNode`** adds `Encounter` (id) and `Name` (display), populated only for the boss node.
  Both are included in `ToDictionary` (omitted/empty for non-boss nodes).

## Model & loader (`RoguelikeEncounters`)

New `RoguelikeEncounters.cs`, mirroring `RoguelikeSettings` (static cache; **restart the server**
to apply; affects new picks only):

- `class Encounter { string Id, Name, Text, Deck; Range Act, Floor, Ascension; double Weight;
  int? EnemyLp, Reward, FirstPlayer, CpuRate; string CpuFlag; }` with `Range { int? Min, Max;
  bool Contains(int) }`. `cpuFlag` is validated against `DuelCpuParam` at parse (invalid → null).
- `Load(dataDirectory)` — read `Roguelike/Encounters.json`, cache; missing file = empty (so the
  strict path errors clearly per node rather than crashing on load).
- `Eligible(type, act, floor, ascension)` — index the `type` array, filter by the three ranges.
- `Pick(type, act, floor, ascension, rng)` — weighted pick over `Eligible(...)`; `null` if empty.
- `ById(id)` — scan all type arrays (used by the boss baked-id path and reward lookup).

## Server flow (`GameServer.Roguelike.cs`)

- **Map build helper** (DRY across `choose_deck` and `AdvanceActOrWin`): build the
  `RoguelikeMap`, then bake the boss encounter — find the boss node, `Pick("boss", act, bossRow,
  ascension, rng)`, set `node.Encounter`/`node.Name`; then `ToDictionary()`.
- **`BuildRoguelikeDuel`:** resolve the encounter —
  - boss node: `ById(node.encounter)` (baked); else
  - `Pick(nodeType, act, node.row, ascension, duelRng)`.
  On `null` → log + `return false`. Resolve `deck` → `Opponents/<deck>` (json/ydk) via
  `RoguelikeDeckPool`. Set CPU LP = `encounter.EnemyLp ?? EnemyLpFor(eff, type)`; `firstPlayer` =
  override or `duelRng.Next(2)`; `ds.cpu` / `ds.cpuflag` = encounter override else
  `Settings.CpuRate/CpuFlag` (defaults 100 / None); keep the existing `RandSeed` draw. Set
  `run.PendingEncounterId = encounter.Id`. (`cpu`/`cpuflag` ride to the engine via `ds.ToDictionary()`,
  which reflects all public fields.)
- **`Act_RoguelikeDuelResult`:** reward = `ById(run.PendingEncounterId)?.Reward ?? RewardFor(type)`;
  clear `PendingEncounterId` alongside `PendingDuelNode`.
- Remove the random `ListOpponentFiles` selection (the listing helper may stay only as a
  file-resolver / validation aid).

## Client

- **`RoguelikeMapScreen`:** when a node dict has a non-empty `name` (the boss), render it as a
  small label near the boss icon so the player sees who waits at the top. Reuses the existing
  cloned-text pattern; no API change (the name rides in the map node payload already sent).

## Edge cases

- Empty / missing `Encounters.json` → every combat node errors (strict). Surfaced as console
  errors; the mode is unplayable until the pool is authored (intended given the strict choice).
- An encounter whose `deck` file is missing → log + `return false` (treated like no encounter).
- Boss pool not covering an act/ascension → that act's boss node has no encounter; reaching it
  logs and won't fight (config error to fix).
- `weight <= 0` → treated as 0 (excluded); if all eligible weights are 0, treated as no match.
- Duplicate `id`s → first wins on `ById`; document as author error (no dedupe enforcement).

## Verification (no unit tests — IL2CPP)

1. Build server + client.
2. Author `Encounters.json` with: a couple of `duel` (one gated `floor 1-3`, one `floor 4+`), one
   `elite` (`floor>=4`), and two `boss` (act 0 vs act 2), each naming a deck present in
   `Opponents/`.
3. New run → reach a row-0 duel → confirm only floor-1-3 duels can appear; the chosen deck/LP
   matches an authored encounter (override applied).
4. Confirm the boss node shows its `name` on the map before the fight; beat it → reward matches
   the boss `reward` override; advancing acts bakes the act-2 boss.
5. Remove all `boss` encounters for an act → reaching that boss logs an error and does not start a
   duel (strict, no random fallback).
6. Resume: enter a combat node, quit before finishing, re-enter → same encounter/deck/draws.
