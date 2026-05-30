# Type Counts & Nested Act-in-Ascension Override — Design

**Goal:** Let `DataLE/Roguelike/Settings.json` bound how many nodes of a given type a
generated act map may contain (`min`/`max` per type), and make every config key (this one
included) overridable for a specific act *within* a specific ascension.

**Scope:** Server-side only. Two independent extensions that ship together because the
second is what makes the first tunable the way we want:

1. **`typeCounts`** — a post-generation correction pass over the act map.
2. **Effective layer 4** — `perAscension[asc].perAct[act]` nesting in the merge.

Topology (path carving, multi-parent merges, boss placement) is unchanged. The client
renders whatever types the server emits — no client changes.

---

## Today (baseline)

`SlayTheSpireLayout.Build` carves `paths` paths bottom→top on a `floors × width` grid,
calling `RoguelikeTypePolicy.PickType(floor, rng)` **independently per node**. Type
selection is forcedRows > first matching band > global `typeWeights`, filtered by per-type
`typeRules` floor ranges, then a weighted random pick. Because each node is picked
independently, the *count* of any type across the map is whatever chance produces — there
is no floor or ceiling on "how many elites" a map has.

`RoguelikeSettings.Effective(base, act, ascension)` deep-merges three layers:
`base` → `perAscension[ascension]` → `perAct[act]` (act wins), then applies
`ascensionScale`. There is no way to express "this act, but only at this ascension":
`perAct[act]` applies at every ascension, and `perAscension[asc]` applies to every act.

---

## Extension 1 — `typeCounts`

### Config schema

```json
"typeCounts": {
  "elite":  { "min": 1, "max": 2 },
  "shop":   { "min": 1, "max": 1 },
  "reward": { "min": 2 }
}
```

- Keyed by node type. Each entry: `min` (default `0`) and/or `max` (default unlimited).
- Counts are scoped to **the whole act map** (one map per act).
- Optional. Omitting `typeCounts` reproduces today's behavior exactly.
- Read from the **effective** settings, so it inherits per-act / per-ascension /
  act-in-ascension overrides (Extension 2) for free.

### Correction pass

After the layout builds the node list (types already assigned by `PickType`), run a
single seeded pass `RoguelikeTypePolicy.EnforceCounts(nodes, rng)`:

1. **Eligible set.** Consider only reassignable nodes — exclude the `boss` node and any
   node on a `forcedRows` floor (those types are explicit and must not move).
2. **Enforce `max` first (strict).** For each type over its `max`, pick the excess nodes
   (seeded shuffle) and demote them to the filler type `duel`. Doing max first frees those
   nodes to serve as filler for the mins below.
3. **Enforce `min` second (best-effort).** For each type under its `min`, promote eligible
   `duel` nodes to that type until the min is met or candidates run out. A node is eligible
   for type `T` only if its floor satisfies `T`'s `typeRules` (`minFloor`/`maxFloor`) — the
   pass never places a type where the floor rules forbid it.

**Contract:** `max` is guaranteed (never exceeded). `min` is best-effort — it is honored
unless floor rules or a shortage of filler nodes make it impossible; the pass never
fabricates nodes, violates floor rules, or touches boss/forced nodes. Filler type is
fixed to `duel` (already the universal fallback in `PickType`).

Determinism: uses the same run-seeded `Random` instance, drawing **after** path carving,
so a resumed run re-derives the identical map.

### Why a post-pass (not inline in `PickType`)

Nodes are created lazily as paths visit cells, and merges collapse cells, so the total
node count is unknown until carving finishes. A post-pass sees the final node set, making
both bounds straightforward; an inline budget would have to guess remaining slots.

---

## Extension 2 — Effective layer 4 (act-in-ascension)

Add a fourth, most-specific merge layer so any key can be overridden for one act at one
ascension. New precedence (later wins):

1. `base`
2. `perAscension[ascension]` — whole ascension
3. `perAct[act]` — that act, every ascension
4. `perAscension[ascension].perAct[act]` — that act, only at that ascension *(new)*

```csharp
public static Dictionary<string, object> Effective(baseSettings, act, ascension)
{
    var eff = DeepClone(baseSettings);
    var asc = ItemAt(GetList(baseSettings, "perAscension"), ascension);
    DeepMerge(eff, asc);                                       // 2
    DeepMerge(eff, ItemAt(GetList(baseSettings, "perAct"), act)); // 3
    if (asc != null)
        DeepMerge(eff, ItemAt(GetList(asc, "perAct"), act));      // 4 (most specific)
    if (ascension > 0)
        ApplyScale(eff, GetDict(baseSettings, "ascensionScale"), ascension);
    return eff;
}
```

`DeepMerge` already merges nested dicts key-by-key, so layer 4 can override just one field
(e.g. `elite.max`) and inherit the rest from lower layers.

### Config example

```json
"perAscension": [
  {},
  {
    "typeCounts": { "elite": { "max": 3 } },
    "perAct": [
      {},
      { "typeCounts": { "elite": { "min": 2, "max": 4 } } }
    ]
  }
]
```

At ascension 1: act 1 gets `elite.min=2, max=4` (layer 4); every other act gets
`elite.max=3` (layer 2). At other ascensions: base values.

This is a **general** merge improvement — it applies to `enemyLp`, `modifierDefaults`,
`typeWeights`, etc., not only `typeCounts`. The leftover `perAct` key sitting inside the
merged `eff` is harmless: the layout reads named keys (`typeCounts`, `enemyLp`, …), never
`perAct`.

---

## Code structure (isolated in `Roguelike/`)

- **`RoguelikeSettings.cs`** — add `TypeCounts(s)` accessor (raw dict, empty default).
  Extend `Effective` with merge layer 4 (small `GetList`/`GetDict` helpers if not present).
- **`RoguelikeTypePolicy.cs`** — parse `typeCounts` into a `min`/`max` table in
  `FromSettings`; add `public void EnforceCounts(List<MapNode> nodes, Random rng)`
  implementing the pass. It already owns `_rules` (floor ranges) and `_forcedRows`, which
  the pass reuses for eligibility.
- **`SlayTheSpireLayout.cs`** — after assembling `map.Nodes`, before `return`, call
  `policy.EnforceCounts(map.Nodes, rng)`. (Only this layout exists; if another is added,
  lift the call to a shared point — YAGNI for now.)
- **`Settings.json`** (install) — add an example `typeCounts` block.
- **Docs** — this file; cross-referenced from `map-config-design.md` and
  `acts-ascension-design.md`.

---

## Edge cases / error handling

- **No `typeCounts`** → pass is a no-op; map identical to today.
- **`min` unreachable** (floor rules exclude all candidates, or too few filler nodes) →
  best-effort; promote what's possible, no crash.
- **`max` smaller than the forced/structural count** (e.g. a `forcedRows` row produces
  more of a type than `max`) → forced/boss nodes are never demoted, so the effective count
  can exceed `max` for those; only reassignable nodes are capped. Document this precedence:
  forcedRows > typeCounts.
- **`min` on `duel`** while another type also needs promotion → promotions consume `duel`
  nodes and may leave `duel` under its own min. Resolved best-effort in deterministic key
  order; documented, not solved as a full CSP.
- **`max` on `duel`** → ignored. `duel` is the filler/demotion sink, so capping it would
  leave demoted nodes nowhere to go; the `max` loop skips the filler type.
- **`max: 0`** → demotes every reassignable node of that type to `duel` (valid way to ban a
  type from a map at a given act/ascension).
- **Unknown type key** in `typeCounts` → no nodes match; min is unreachable (no-op), max
  trivially satisfied.

---

## Verification (no unit tests — IL2CPP)

1. Build server (`MSBuild YgoMasterServer/YgoMaster.csproj`).
2. **typeCounts:** `Settings.json` with `typeCounts: { "elite": { "min": 2, "max": 2 },
   "shop": { "min": 1, "max": 1 } }`. New run, open the map → exactly 2 elites and 1 shop,
   none below their `minFloor`.
3. **max strict:** set `elite.max: 0` → no elites on the map.
4. **Layer 4:** put a `perAscension[1].perAct[1].typeCounts.elite.min` override; run at
   ascension 1, reach act 2 (act index 1) → that act's elite count reflects the nested
   value while other acts at ascension 1 use the ascension-wide value.
5. **Regression:** remove `typeCounts` and the nested `perAct` → identical to current
   behavior; existing `perAct`/`perAscension` still apply as before.
