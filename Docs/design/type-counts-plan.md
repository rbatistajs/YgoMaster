# Type Counts & Nested Act-in-Ascension Override — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bound how many nodes of each type an act map may contain (`typeCounts` min/max), and let any config key be overridden for one act within one ascension (`Effective` layer 4).

**Architecture:** Server-side only. (1) Extend `RoguelikeSettings.Effective` with a 4th, most-specific merge layer `perAscension[asc].perAct[act]`. (2) Add a `typeCounts` accessor; parse it in `RoguelikeTypePolicy`; run a seeded post-generation pass `EnforceCounts` from `SlayTheSpireLayout.Build` that demotes over-`max` types to the `duel` filler (strict) then promotes eligible `duel` nodes to reach `min` (best-effort, honoring floor rules).

**Tech Stack:** C# (.NET Framework), `YgoMaster` server project. Spec: `Docs/roguelike/type-counts-design.md`.

**No unit tests:** This is an IL2CPP-adjacent mod with no test harness; verification is build + in-game, matching the other roguelike design docs. Each task ends with a server build and (where observable) an in-game check.

**Build command (server):**
```
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterServer\YgoMaster.csproj" -nologo -v:minimal
```
Expected tail: `YgoMaster -> ...\YgoMaster.exe`. If the server is running, the post-build copy fails with MSB3027 — the compile still succeeds; close the server and copy `YgoMaster.exe` to the install manually.

**Commits:** This repo commits only on explicit user request, conventional style (`feat:`/`refactor:`/`docs:`), no AI attribution. Commit steps below are grouped; run them only when the user approves.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `YgoMasterServer/Roguelike/RoguelikeSettings.cs` | Settings parsing + `Effective` merge | Add `TypeCounts(s)` accessor; add layer-4 merge in `Effective` |
| `YgoMasterServer/Roguelike/RoguelikeTypePolicy.cs` | Node-type selection + (new) count enforcement | Parse `typeCounts` in `FromSettings`; add `EnforceCounts` + helpers |
| `YgoMasterServer/Roguelike/SlayTheSpireLayout.cs` | Map topology + type assignment | One call to `policy.EnforceCounts(map.Nodes, rng)` before `return` |
| `<install>/DataLE/Roguelike/Settings.json` | Live config | Add example `typeCounts` block |
| `Docs/roguelike/map-config-design.md`, `acts-ascension-design.md` | Reference docs | Cross-reference the new feature |

`<install>` = `D:\SteamLibrary\steamapps\common\Yu-Gi-Oh!  Master Duel\YgoMasterLE - Goat`

---

## Task 1: Effective layer 4 (act-in-ascension nesting)

**Files:**
- Modify: `YgoMasterServer/Roguelike/RoguelikeSettings.cs` (method `Effective`, currently lines 60-68)

- [ ] **Step 1: Replace `Effective` to add the nested per-act layer**

The current method merges base → perAscension → perAct. Capture the ascension dict in a local, then deep-merge its own `perAct[act]` last (most specific). Uses only existing helpers (`ItemAt`, `Utils.GetValue<List<object>>`).

Replace:

```csharp
        public static Dictionary<string, object> Effective(Dictionary<string, object> baseSettings, int act, int ascension)
        {
            Dictionary<string, object> eff = DeepClone(baseSettings);
            DeepMerge(eff, ItemAt(Utils.GetValue<List<object>>(baseSettings, "perAscension"), ascension));
            DeepMerge(eff, ItemAt(Utils.GetValue<List<object>>(baseSettings, "perAct"), act));
            if (ascension > 0)
                ApplyScale(eff, Utils.GetValue<Dictionary<string, object>>(baseSettings, "ascensionScale"), ascension);
            return eff;
        }
```

with:

```csharp
        public static Dictionary<string, object> Effective(Dictionary<string, object> baseSettings, int act, int ascension)
        {
            Dictionary<string, object> eff = DeepClone(baseSettings);
            Dictionary<string, object> asc = ItemAt(Utils.GetValue<List<object>>(baseSettings, "perAscension"), ascension);
            DeepMerge(eff, asc);                                                               // 2: whole ascension
            DeepMerge(eff, ItemAt(Utils.GetValue<List<object>>(baseSettings, "perAct"), act)); // 3: act, every ascension
            if (asc != null)
                DeepMerge(eff, ItemAt(Utils.GetValue<List<object>>(asc, "perAct"), act));      // 4: this act at this ascension (wins)
            if (ascension > 0)
                ApplyScale(eff, Utils.GetValue<Dictionary<string, object>>(baseSettings, "ascensionScale"), ascension);
            return eff;
        }
```

Also update the doc-comment directly above the method (currently "base, deep-merged with perAscension[asc] then perAct[act] (act wins)...") to mention the 4th layer:

```csharp
        // Settings for a given act + ascension: base, deep-merged with perAscension[asc], then
        // perAct[act], then perAscension[asc].perAct[act] (most specific wins). Then ascensionScale
        // applied multiplicatively (× ascension).
```

- [ ] **Step 2: Build the server**

Run the build command from the header.
Expected: `YgoMaster -> ...\YgoMaster.exe` (or MSB3027 copy-only failure if the server is running — compile OK).

- [ ] **Step 3: (Deferred) in-game check**

Layer 4 is verified together with `typeCounts` in Task 5 (it has no visible effect on its own without a key that uses it). No standalone check here.

---

## Task 2: `TypeCounts` accessor in RoguelikeSettings

**Files:**
- Modify: `YgoMasterServer/Roguelike/RoguelikeSettings.cs` (add accessor next to `TypeRules`/`ForcedRows`, ~line 152)

- [ ] **Step 1: Add the accessor**

Immediately after the `ForcedRows` accessor (the method ending at ~line 152), add:

```csharp
        // Per-type node-count bounds for an act map: { "<type>": { "min", "max" } } (counted over
        // the whole map). Empty = none. Passes through Effective (perAct/perAscension/nested can set it).
        public static Dictionary<string, object> TypeCounts(Dictionary<string, object> s)
        {
            return Utils.GetValue<Dictionary<string, object>>(s, "typeCounts") ?? new Dictionary<string, object>();
        }
```

- [ ] **Step 2: Build the server**

Run the build command.
Expected: compile succeeds (accessor is unused until Task 3 — no warnings expected for a public method).

---

## Task 3: Parse `typeCounts` + add `EnforceCounts` in RoguelikeTypePolicy

**Files:**
- Modify: `YgoMasterServer/Roguelike/RoguelikeTypePolicy.cs`

- [ ] **Step 1: Add the count model field**

After the existing `_forcedRows` field (line 19) and the `Rule` nested class (line 13), add a `Count` class and a `_counts` dict, plus a filler constant. Insert the class next to `Rule`:

```csharp
        class Band { public int From, To; public List<KeyValuePair<string, double>> Weights; }
        class Rule { public int Min, Max; } // inclusive; Max = int.MaxValue when open-ended
        class Count { public int Min, Max; } // Max = int.MaxValue when open-ended

        const string Filler = "duel"; // demotion sink / promotion source for count enforcement
```

And add the field next to `_forcedRows`:

```csharp
        readonly Dictionary<string, Count> _counts = new Dictionary<string, Count>();
```

- [ ] **Step 2: Parse `typeCounts` in `FromSettings`**

After the `forcedRows` parse loop (the loop ending right before `return p;`, ~line 59), add:

```csharp
            foreach (KeyValuePair<string, object> kv in RoguelikeSettings.TypeCounts(settings))
            {
                Dictionary<string, object> c = kv.Value as Dictionary<string, object>;
                if (c == null) continue;
                p._counts[kv.Key] = new Count
                {
                    Min = c.ContainsKey("min") ? ToInt(c["min"]) : 0,
                    Max = c.ContainsKey("max") ? ToInt(c["max"]) : int.MaxValue,
                };
            }
```

(Counts are raw counts — no `Norm` normalization; `ToInt` already exists in this class.)

- [ ] **Step 3: Add `EnforceCounts` and its helpers**

Add these methods after `PickType` (after line ~91, before the `Norm` helper). `_forcedRows` (keyed by absolute floor) and `_rules` (absolute floor ranges) are reused for eligibility:

```csharp
        // Post-generation count correction over the whole act map. Skips the boss node and any node
        // on a forced row. Enforces each type's max strictly (demote excess to the Filler), then min
        // best-effort (promote eligible Filler nodes whose floor satisfies the type's rule). The
        // Filler type itself is exempt from max (it is the demotion sink). Deterministic via rng.
        public void EnforceCounts(List<MapNode> nodes, Random rng)
        {
            if (_counts.Count == 0 || nodes == null) return;

            List<MapNode> pool = new List<MapNode>();
            foreach (MapNode n in nodes)
                if (n.Type != "boss" && !_forcedRows.ContainsKey(n.Row)) pool.Add(n);

            // max (strict): demote excess of each capped type to the filler.
            foreach (KeyValuePair<string, Count> kv in _counts)
            {
                if (kv.Value.Max == int.MaxValue || kv.Key == Filler) continue;
                List<MapNode> of = NodesOfType(pool, kv.Key);
                Shuffle(of, rng);
                for (int i = kv.Value.Max; i < of.Count; i++) of[i].Type = Filler;
            }

            // min (best-effort): promote eligible filler nodes until min is met.
            foreach (KeyValuePair<string, Count> kv in _counts)
            {
                int need = kv.Value.Min - NodesOfType(pool, kv.Key).Count;
                if (need <= 0) continue;
                List<MapNode> eligible = new List<MapNode>();
                foreach (MapNode n in NodesOfType(pool, Filler))
                    if (FloorAllowed(kv.Key, n.Row)) eligible.Add(n);
                Shuffle(eligible, rng);
                for (int i = 0; i < need && i < eligible.Count; i++) eligible[i].Type = kv.Key;
            }
        }

        static List<MapNode> NodesOfType(List<MapNode> pool, string type)
        {
            List<MapNode> r = new List<MapNode>();
            foreach (MapNode n in pool) if (n.Type == type) r.Add(n);
            return r;
        }

        // True when `floor` is inside the type's floor rule (or the type has no rule).
        bool FloorAllowed(string type, int floor)
        {
            Rule rule;
            if (!_rules.TryGetValue(type, out rule)) return true;
            return floor >= rule.Min && floor <= rule.Max;
        }

        static void Shuffle(List<MapNode> list, Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                MapNode t = list[i]; list[i] = list[j]; list[j] = t;
            }
        }
```

- [ ] **Step 4: Build the server**

Run the build command.
Expected: compile succeeds. (`EnforceCounts` is still unused until Task 4 — it is `public`, so no unused-warning.)

---

## Task 4: Call `EnforceCounts` from the layout

**Files:**
- Modify: `YgoMasterServer/Roguelike/SlayTheSpireLayout.cs` (method `Build`, the node-collection loop ending at line 51)

- [ ] **Step 1: Add the enforcement call after assembling nodes**

The build currently ends:

```csharp
            for (int r = 0; r < floors; r++)
                for (int c = 0; c < width; c++)
                    if (grid[r, c] != null) map.Nodes.Add(grid[r, c]);
            return map;
```

Change to:

```csharp
            for (int r = 0; r < floors; r++)
                for (int c = 0; c < width; c++)
                    if (grid[r, c] != null) map.Nodes.Add(grid[r, c]);
            policy.EnforceCounts(map.Nodes, rng); // bound per-type counts after the full set exists
            return map;
```

(`policy` and `rng` are already in scope from the top of `Build`.)

- [ ] **Step 2: Build the server**

Run the build command.
Expected: `YgoMaster -> ...\YgoMaster.exe`.

---

## Task 5: Settings.json example, docs cross-ref, and in-game verification

**Files:**
- Modify: `<install>/DataLE/Roguelike/Settings.json`
- Modify: `Docs/roguelike/map-config-design.md`, `Docs/roguelike/acts-ascension-design.md`

- [ ] **Step 1: Add a `typeCounts` block to the live Settings.json**

Add this top-level key to `<install>/DataLE/Roguelike/Settings.json` (alongside `typeRules`):

```json
  "typeCounts": {
    "elite": { "min": 1, "max": 2 },
    "shop":  { "min": 1, "max": 1 }
  },
```

- [ ] **Step 2: Cross-reference the docs**

In `Docs/roguelike/map-config-design.md`, append a line at the end of the "Config schema" notes:

```markdown
- **`typeCounts`** — per-type map-wide `min`/`max` node counts, enforced by a post-generation
  pass. See `type-counts-design.md`.
```

In `Docs/roguelike/acts-ascension-design.md`, under "Effective resolution", append:

```markdown
5. deep-merge `perAscension[ascension].perAct[act]` — the most specific layer (this act at
   this ascension), applied after step 3 and before step 4. See `type-counts-design.md`.
```

- [ ] **Step 3: In-game — typeCounts min/max**

Start the server and client, begin a fresh run, open the act map. With the Step-1 config:
Expected: exactly 1-2 elite nodes and exactly 1 shop node; no elite below floor 2 / no shop below floor 2 (existing `typeRules`). Try a couple of seeds (new runs) — counts stay in range every time.

- [ ] **Step 4: In-game — max strict (ban a type)**

Temporarily set `"elite": { "max": 0 }` in `typeCounts`, restart server, new run.
Expected: zero elite nodes on the map. Revert to the Step-1 values afterward.

- [ ] **Step 5: In-game — layer 4 (act-in-ascension)**

Temporarily add to `Settings.json`:

```json
  "perAscension": [
    {},
    { "perAct": [ {}, { "typeCounts": { "elite": { "min": 3, "max": 3 } } } ] }
  ],
```

Restart server. Start an **ascension 1** run, clear act 1's boss to reach **act 2** (act index 1).
Expected: act 2's map has exactly 3 elites (layer-4 override); act 1 of the same run uses the base `typeCounts`. Revert the temporary `perAscension` block afterward.

- [ ] **Step 6: Regression**

Remove `typeCounts` entirely from `Settings.json`, restart, new run.
Expected: map identical in character to pre-feature behavior (no count constraints); existing `perAct`/`perAscension` overrides still apply.

- [ ] **Step 7: Commit (only if the user approves)**

```bash
git add YgoMasterServer/Roguelike/RoguelikeSettings.cs YgoMasterServer/Roguelike/RoguelikeTypePolicy.cs YgoMasterServer/Roguelike/SlayTheSpireLayout.cs Docs/roguelike/type-counts-design.md Docs/roguelike/type-counts-plan.md Docs/roguelike/map-config-design.md Docs/roguelike/acts-ascension-design.md
git commit -m "feat(roguelike): per-type map counts + act-in-ascension config layer"
```

(The live `Settings.json` lives under the install dir, not the repo — not part of the commit.)

---

## Self-Review

**Spec coverage:**
- `typeCounts` schema + map scope → Task 2 (accessor), Task 3 (parse), Task 5 Step 1 (config). ✓
- Post-pass: max strict → min best-effort, filler `duel`, skip boss/forced, floor-rule eligibility, seeded → Task 3 Step 3. ✓
- Effective layer 4 (`perAscension[asc].perAct[act]`) → Task 1. ✓
- Wire into layout → Task 4. ✓
- Edge cases: `max: 0` (Task 5 Step 4), filler exempt from max (`kv.Key == Filler` skip, Task 3 Step 3), min unreachable (best-effort loop bound `i < eligible.Count`), no `typeCounts` no-op (`_counts.Count == 0` guard). ✓
- Docs → Task 5 Step 2. ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code. ✓

**Type/name consistency:** `Count{Min,Max}`, `_counts`, `Filler="duel"`, `EnforceCounts(List<MapNode>, Random)`, `NodesOfType`, `FloorAllowed`, `Shuffle` — used identically across Tasks 3 and 4. `ToInt` and `_rules`/`_forcedRows` are existing members of `RoguelikeTypePolicy`. `ItemAt`/`DeepMerge`/`Utils.GetValue` existing in `RoguelikeSettings`. ✓
