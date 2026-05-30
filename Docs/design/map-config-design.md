# Map Generation Config — Design (M3 extension)

**Goal:** Give `DataLE/Roguelike/Settings.json` finer, still-random control over which
node types appear where on the map: per-zone weight tables (bands), global per-type
floor ranges (rules), and whole-row overrides (forced rows).

**Scope:** Server-side type assignment only. Topology (path carving, multi-parent
merges, boss placement) is unchanged. Client renders whatever types the server emits —
no client changes.

---

## Today (baseline)

`SlayTheSpireLayout.Build` carves `paths` paths bottom→top on a `floors × width` grid.
Floor 0 is hardcoded `duel`, the boss is a single structural node on the top row, and
every other node picks a type from one global `typeWeights` table (same distribution on
every floor). Config lives in `RoguelikeSettings` (reads `Settings.json`, defaults when
missing).

## Config schema

```json
{
  "layout": "slay_the_spire",
  "floors": 8,
  "width": 4,
  "paths": 6,

  "typeWeights": { "duel": 0.6, "elite": 0.12, "event": 0.12, "shop": 0.08, "reward": 0.08 },

  "bands": [
    { "from": 0,  "to": 0,  "weights": { "duel": 1 } },
    { "from": 1,  "to": 3,  "weights": { "duel": 0.7, "event": 0.2, "shop": 0.1 } },
    { "from": 4,  "to": -2, "weights": { "duel": 0.5, "elite": 0.2, "reward": 0.2, "shop": 0.1 } }
  ],

  "typeRules": {
    "elite":  { "minFloor": 2 },
    "shop":   { "minFloor": 2, "maxFloor": -2 },
    "reward": { "minFloor": 3 }
  },

  "forcedRows": { "0": "duel", "-2": "shop" }
}
```

- **`bands`** — row zones (`from`/`to`, both inclusive) each with their own `weights`.
  Rows not covered by any band fall back to the global `typeWeights`.
- **`typeRules`** — per-type `minFloor`/`maxFloor`: a global lock applied *after* a band
  is chosen. A type whose range excludes floor `r` is dropped from that floor's roll even
  if the band lists it. Either bound may be omitted (open-ended on that side).
- **`forcedRows`** — force every node on a row to one type; overrides bands and rules.
- **Row indices accept negatives**, normalized as `floors + n`: `-1` = top (boss row),
  `-2` = the row just below the boss. Applies to `bands.from/to`, `typeRules.minFloor/
  maxFloor`, and `forcedRows` keys. This lets "before the boss" be written without
  knowing the floor count.

All three keys are optional. Omitting all of them reproduces today's behavior exactly.

## Type resolution (per node at floor `r`)

1. **Boss row** (`r == floors-1`) is structural — never touched by bands/rules/forcedRows.
2. If `forcedRows` has an entry for `r` → use that type. **Done.**
3. Otherwise pick the `weights` of the first band whose `[from, to]` contains `r`
   (else the global `typeWeights`).
4. Filter those weights by `typeRules` (drop any type with `minFloor > r` or
   `maxFloor < r`).
5. Weighted random pick. If the filtered set is empty → fallback `duel`.

Floor 0 default stays `duel`: when no band/forcedRow covers floor 0, the picker returns
`duel` (preserves current behavior with no config). All randomness uses the run seed, so
generation stays deterministic/reproducible.

## Code structure (isolated in `Roguelike/`)

- **New `RoguelikeTypePolicy.cs`** — owns all type-selection logic. Built once per map via
  `RoguelikeTypePolicy.FromSettings(settings, floors)`, which parses bands/rules/forcedRows
  and normalizes negative indices to absolute floors. Exposes
  `string PickType(int floor, Random rng)` implementing the resolution steps above.
- **`RoguelikeSettings.cs`** — add accessors `Bands(s)`, `TypeRules(s)`, `ForcedRows(s)`
  returning the raw config (with empty defaults). Keeps `Settings.json` parsing in one place.
- **`SlayTheSpireLayout.cs`** — `Build` constructs a `RoguelikeTypePolicy` once and
  `EnsureNode` calls `policy.PickType(r, rng)`. The current static `PickType`/`NormalizeWeights`
  move into `RoguelikeTypePolicy`. Path carving, `NextCol`, `WouldCross`, boss wiring unchanged.

## Edge cases / error handling

- Negative `from`/`to`/bound that normalizes out of `[0, floors-1]` → clamped to range.
- A band with empty/zero `weights` → treated as "no band here" (falls back to global
  `typeWeights`, then the empty-set fallback `duel`).
- `forcedRows` value that isn't a known pool type → used verbatim (it's a valid type
  string; the client just needs an icon/color, defaulting to the duel style for unknowns).
- `forcedRows` targeting the boss row → ignored (boss is structural).
- Overlapping bands → first match wins (document: order bands narrow→broad or
  non-overlapping).

## Verification

No unit tests (IL2CPP mod). Verify by:
1. Build server (`MSBuild YgoMasterServer/YgoMaster.csproj`).
2. Edit `Settings.json` with the example above, start a fresh run, open the map.
3. Confirm: floor 0 all `duel`; the `-2` row all `shop`; no `elite` below floor 2; no
   `reward` below floor 3; middle floors show the band mix.
4. Remove the three keys, regenerate → identical to current behavior (regression check).
