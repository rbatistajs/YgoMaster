# Acts & Ascension — Design

**Goal:** A run spans N acts (each its own map; beating an act's boss generates the next act's
map). Beating the final act's boss wins the run and unlocks the next ascension. Acts count,
ascension count, and per-act / per-ascension config overrides are all data-driven.

## Today (baseline)

A run has one map (built at choose_deck from the run seed) with one `boss` node at the top.
Beating the boss only credits currency — there is no "act" or "run won" concept; a run ends only
on loss (LP 0). Config (`RoguelikeSettings`) is global: layout/floors/width/paths, typeWeights/
bands/typeRules/forcedRows, playerMaxLp/healPercentPerCombat/enemyLp. `roguelike.json` holds the
run and is deleted on loss/abandon.

## Config (`Settings.json`)

```json
"acts": 3,
"ascensions": 20,

"perAct": [ { ...act0 overrides... }, { ...act1... }, { ...act2... } ],
"perAscension": [ { ...asc0 overrides... }, { ...asc1... } ],
"ascensionScale": { "enemyLp": 0.05, "playerMaxLp": -0.01 },

"interActHealPercent": 0.30
```

- **`acts`** — acts per run (default 3). **`ascensions`** — number of ascension tiers (default 20).
- **`perAct[k]`** — partial config deep-merged over the base for act `k` (replaces those keys).
- **`perAscension[a]`** — partial config deep-merged for ascension `a` (explicit per-tier tweaks).
- **`ascensionScale`** — multiplicative per-level factors for numeric keys; applied as
  `value × (1 + factor × ascension)`. For `enemyLp` (a per-type dict) every numeric entry scales.
- **`interActHealPercent`** — heal fraction of max LP applied when starting each act (just another
  config key, so it's overridable per act/ascension).

### Effective resolution

`RoguelikeSettings.Effective(base, act, ascension)` returns a merged dict:
1. start from `base` (deep clone),
2. deep-merge `perAscension[ascension]` (if present),
3. deep-merge `perAct[act]` (if present) — **act wins over ascension wins over base**,
4. apply `ascensionScale` × `ascension` to the named numeric keys (last).

Deep-merge: for dict values, merge key-by-key; otherwise the override replaces. The existing
accessors (`Floors`, `Width`, `TypeWeights`, `EnemyLpFor`, `PlayerMaxLp`, `HealPercent`, bands,
rules, forced rows) read the effective dict unchanged — callers pass `Effective(...)` instead of
the raw base.

New accessors: `Acts(s)` (default 3), `Ascensions(s)` (default 20), `InterActHealPercent(s)`
(default 0.0 = carry LP).

## State

- **`RoguelikeRun`** adds: `Act` (0-based current act), `Ascension` (tier this run is played at),
  `Won` (set when the final boss falls). Persisted in `roguelike.json`.
- **New `RoguelikeMeta`** — `roguelike_meta.json` in the player dir, **survives** run deletion.
  Holds `maxAscension` (highest unlocked, default 0). Load/Save mirror `RoguelikeRun`.

## Server flow (`GameServer.Roguelike.cs`)

- **start_run** `{ascension}`: clamp to `[0, meta.maxAscension]`; `run.Ascension = chosen`,
  `run.Act = 0`, `run.Won = false`; roll deck offers as today.
- **choose_deck**: `eff = Effective(base, 0, run.Ascension)`; build the act-0 map with
  `seed = ActSeed(run.Seed, 0)`; `run.MaxLp = run.Lp = PlayerMaxLp(eff)`.
- **duel_result** (win on the pending node where `NodeType == "boss"`):
  - if `run.Act < Acts(base) - 1`: `run.Act++`; `eff = Effective(base, run.Act, run.Ascension)`;
    rebuild map with `ActSeed(run.Seed, run.Act)`, `Position = -1`, `Visited = []`; carry currency
    and deck; heal `Lp = min(MaxLp, Lp + InterActHealPercent(eff) × MaxLp)`; credit boss reward.
  - else (final act boss): `run.Won = true`, `run.Active = false`; credit boss reward;
    `meta.maxAscension = min(Ascensions(base) - 1, max(meta.maxAscension, run.Ascension + 1))`; save meta.
  - non-boss combats and loss: unchanged (LP carry/heal, currency, LP 0 = death).
- `ActSeed(runSeed, act)` = a deterministic per-act seed (e.g. `(int)(runSeed * 31 + act * 2654435761)`).
- `BuildRoguelikeDuel` uses `Effective(base, run.Act, run.Ascension)` for `EnemyLpFor`.

## Client

- **start_run**: before starting, open an ascension picker — `ActionSheetViewController.Open` with
  one option per `0..meta.maxAscension` ("Ascensão N"); the choice is sent as `start_run {ascension}`.
  (When `maxAscension == 0` there's nothing to pick — start at 0 directly.)
- **Map header**: show `Ato X/N` and `Asc A` alongside LP. The post-duel `MarkMapDirty` refresh
  already re-renders the (new) act's map when it reappears.
- **Run won**: `RoguelikeFlow` shows a victory dialog (`CommonDialogViewController`) on the
  duel_result that set `won = true`; the home reflects the new `maxAscension` next time it's opened.

## State read on the client

`RoguelikeApi` adds reads from `$.Roguelike`: `Act()`, `Acts()` (total), `Ascension()`, `Won()`,
and `MaxAscension()` (piggybacked into the run/home payload from `RoguelikeMeta`).

## Edge cases

- Missing config keys → defaults (acts 3, ascensions 20, no overrides, heal 0). Fully backward
  compatible: with no acts/ascension config, a run is 1 act at ascension 0 that ends on the boss.
  (Default `acts` is 3, so existing installs get 3 acts unless they set `acts: 1`.)
- `ascension` from the client clamped server-side to `[0, maxAscension]` (never trust the client).
- Beating the boss with `acts: 1` immediately wins the run and unlocks ascension 1.
- `ascensionScale` only touches numeric keys it names; unknown/non-numeric keys are ignored.

## Verification (no unit tests — IL2CPP)

1. Build server + client.
2. `Settings.json` with `acts: 3`, an `enemyLp` bump in `perAct[2]`, `ascensionScale.enemyLp 0.1`.
3. New run → ascension modal shows `Ascensão 0` only (maxAscension 0). Start, clear act 1 boss →
   map regenerates as act 2 (header `Ato 2/3`), LP healed by `interActHealPercent`.
4. Clear act 3 boss → victory dialog; reopen home → ascension modal now offers `0` and `1`.
5. Start an ascension-1 run → confirm enemy LP higher (scale applied).
