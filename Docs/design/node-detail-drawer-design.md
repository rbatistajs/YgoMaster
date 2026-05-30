# Node Detail Drawer — Design

**Goal:** Clicking a map node no longer moves there immediately. Instead it opens an
animated side drawer showing that node's details; a button inside the drawer ("Duelo" for
combat, "Avançar" otherwise) performs the actual move/action. Every node is clickable for
preview; the action button is enabled only on reachable nodes.

**Scope:** Mostly client (the drawer + click rewiring). The server gains a few baked
per-node fields (enemy LP, reward, a declared-modifier summary) so the drawer can show
"rich" details. No change to the move/duel flow itself — the drawer's button calls the
existing `RoguelikeApi.Move`.

---

## Today (baseline)

`RoguelikeMapScreen.RenderMap` instantiates one node GameObject per map node. Only
**reachable** nodes get a click handler, wired through a 16-entry captureless slot table
(`_slotActions[0..15]` → `OnSlot(slot)` → `RoguelikeApi.Move(_slotNodeId[slot])`).
`CreateUnityAction` only bridges **captureless/static** delegates (it sets `m_target = 0`
and uses `function.Method`), so closures over a loop index cannot be used — hence the fixed
hand-written slot lambdas.

After `Move`, `RoguelikeFlow.OnNetworkComplete("Roguelike.move")` either launches the queued
duel (`pendingDuelNode >= 0`) or re-renders the map. Baked nodes already carry `name` and
`iconImage`; combat enemy LP/reward are computed at duel-build / duel-result time, not baked.

---

## Interaction flow (new)

1. Click any node → open the drawer populated for that node id. **No move yet.**
2. Drawer shows the node's details (below). If the node is **reachable**, an action button
   is shown/enabled; otherwise the drawer is read-only (preview of what's ahead/behind).
3. Action button → `RoguelikeApi.Move(nodeId)` + close drawer → the **existing** flow runs
   (combat launches the duel, non-combat re-renders).
4. Click the scrim (outside the panel) → close drawer (no move). The current node opens the
   drawer but shows no action button (you're already there).

## Click dispatch for all nodes

Every node's `SelectionButton.onClick` is wired to open the drawer for its id. Reusing the
captureless slot pattern, the slot table is enlarged to cover the whole node set:

- `MapSlots` raised to **64** (worst-case `floors × width` for shipped configs is 12 × 4 =
  48). `_slotActions` becomes a 64-entry array of explicit captureless lambdas
  (`() => OnSlot(0) … () => OnSlot(63)`).
- During render, **every** node (not only reachable) takes a slot:
  `_slotNodeId[slot] = n.Id; WireNodeClick(node, _slotActions[slot]); slot++`.
- `OnSlot(slot)` now calls `RoguelikeNodeDrawer.Open(_slotNodeId[slot])` instead of moving.
- Nodes beyond slot 64 (won't occur in shipped configs) get no handler — graceful, documented.

Reachability is recomputed inside the drawer (`Reachable(nodes, pos).Contains(id)`) to decide
whether to show the action button — the slot no longer implies "reachable".

*(Alternative considered: a single captureless handler reading
`EventSystem.current.currentSelectedGameObject` → parse `RgNode<Id>`. Cleaner but the codebase
has no EventSystem usage and SelectionButton selection behavior is unverified. Left as a
possible future spike; the slot table is the reliable choice.)*

---

## Drawer UI (`RoguelikeNodeDrawer.cs`, new)

Built once under the run screen Window, then reused (show/repopulate/hide per open).

```
RgDrawerScrim   (full-screen transparent Image, raycast target; onClick = Close)
  └ RgDrawer    (RectTransform anchored to the right edge, ~440 wide, full height)
       ├ Bg        (Image: GUI_CommonSquareBracket sprite via FindIcon; fallback tinted panel)
       ├ Header    (type glyph + localized type name, tinted by TypeColor)
       ├ Art       (baked encounter art; reuses RoguelikeMapScreen art loaders)
       ├ NameText  (encounter display name, if baked)
       ├ Stats     (combat only: "LP do inimigo: N", "Recompensa: N")
       ├ Mods      (declared-modifier tags, one line each)
       └ ActionBtn (cloned SelectionButton + label; reachable nodes only)
```

- **Background:** `GUI_CommonSquareBracket` resolved at runtime via the existing
  `FindIcon(name)` (returns a loaded sprite). If null, fall back to a flat semi-opaque panel
  so the drawer always renders.
- **Art:** reuse `ResolveArtTexture` / the profile-sprite path already in `RoguelikeMapScreen`
  (extract to a shared helper or expose internally) so `card_<cid>` / `profile_<id>` both work.
- **Action button:** clone an existing `SelectionButton` (the run screen already clones
  buttons — same approach). `onClick` is a captureless static `OnDrawerAction` →
  `RoguelikeApi.Move(_openNodeId)` + `Close()`. Label from Labels by node kind.
- **Animation:** `TweenPosition` (YgomSystem.UI) on `RgDrawer`, sliding X from off-screen-right
  to docked on open and back on close, reusing the project's TweenPosition usage
  (`RoguelikeMapScreen` marker / `SoloVisualNovel.PlayTweenPosition`).
- **Lifecycle:** `Close()` on scrim click and before a map re-render (`RenderMap`) so a stale
  drawer never lingers over fresh state. State: `_openNodeId`, `_built`, `_visible`.

## Drawer content rules

| Node kind | Header | Art | Name | Stats | Mods | Action button |
|---|---|---|---|---|---|---|
| combat (duel/elite/boss) | yes | if baked | if baked | LP + reward | declared tags | "Duelo" (reachable) |
| non-combat (event/shop/reward) | yes | if baked | if baked | — (M4+) | declared tags | "Avançar" (reachable) |
| current node (any) | yes | … | … | … | … | hidden |
| locked / visited | yes | … | … | … | … | hidden |

---

## Server: rich node data

**`RoguelikeMap.MapNode`** gains nullable fields, serialized only when set:

```csharp
public int? EnemyLp;
public int? Reward;
public Dictionary<string, object> Modifiers; // declared-modifier summary, or null
```

`ToDictionary` adds `enemyLp`, `reward`, `modifiers` when non-null.

**`GameServer.Roguelike.BakeEncounters`** is generalized — it already loops every node:

- Encounter pick (name/art) stays gated on `bakeTypes` (unchanged).
- **For combat nodes** (`IsCombat(type)`), additionally set:
  - `n.EnemyLp = enc?.EnemyLp ?? RoguelikeSettings.EnemyLpFor(eff, type)`
  - `n.Reward  = enc?.Reward  ?? RewardFor(type)`
- **For every node**, set `n.Modifiers = SummarizeModifiers(eff, type, enc)` (null when empty):
  - `merged = RoguelikeModifiers.Merge(new[] { RoguelikeSettings.ModifierDefaults(eff, type), enc?.Modifiers })`
    (Merge sums `extraLp`/`extraHand`, keeps positional `monsters`/`spellTraps`/`hand` lists).
  - Reduce each side (`player`, `enemy`) to a flat summary the client can format without the
    modifier engine:
    ```json
    { "enemy": { "extraLp": 2000, "extraHand": 1, "monsters": 1 },
      "player": { "extraHand": 1 } }
    ```
    Keys: `extraLp`, `extraHand` (scalars, omit when 0); `monsters`, `spellTraps`, `hand`
    (non-null counts of the positional lists, omit when 0). Sides omitted when empty.
  - Declared only — no seeded resolution, so map-gen stays cheap and matches what's
    configured (the duel's random/link picks aren't previewed).

`enc` here is the encounter already picked for `bakeTypes`; for non-baked combat nodes `enc`
is null and the defaults apply.

## Client: parse + labels

- **`RoguelikeApi.MapNode`** gains `int EnemyLp = -1`, `int Reward = -1`,
  `Dictionary<string,object> Modifiers`; `GetMapNodes` parses `enemyLp`/`reward`/`modifiers`
  (absent → -1 / null).
- **`Labels.json`** new keys: `node.type.duel|elite|boss|event|shop|reward` (display names),
  `node.action.duel` ("Duelo"), `node.action.move` ("Avançar"), `node.stat.enemyLp`
  ("LP do inimigo: {0}"), `node.stat.reward` ("Recompensa: {0}"), and modifier tags
  `node.mod.enemy.extraLp` / `node.mod.player.extraLp` ("Inimigo +{0} LP" / "Você +{0} LP"),
  same for `extraHand` ("+{0} carta inicial"), and `node.mod.*.monsters`
  ("Inimigo começa com {0} monstro(s)" / "Você começa com {0} monstro(s)"). The client
  formats the summary dict into one tag line per non-zero entry via `RoguelikeLabels.Get`.

---

## Files

**New:** `YgoMasterClient/Roguelike/RoguelikeNodeDrawer.cs` (+ `YgoMasterClient.csproj` include).

**Modified (client):** `Roguelike/RoguelikeMapScreen.cs` (slot table → 64; wire all nodes;
`OnSlot` → drawer open; `Close()` before re-render; expose art loader),
`Roguelike/RoguelikeApi.cs` (parse new fields), `DataLE/Roguelike/Labels.json` (install).

**Modified (server):** `Roguelike/RoguelikeMap.cs` (3 fields + ToDictionary),
`Roguelike/GameServer.Roguelike.cs` (`BakeEncounters` stats + `SummarizeModifiers`).

---

## Edge cases / error handling

- `GUI_CommonSquareBracket` not loaded → fallback flat panel; drawer still works.
- Baked art missing for a node → art element hidden (header/name/stats still show).
- Non-combat node → no LP/reward rows (those semantics are M4+); type + name + mods only.
- Current node → action button hidden (can't move to where you are).
- Map re-render while drawer open → `Close()` first so it doesn't float over new nodes.
- `> 64` nodes (not in shipped configs) → extra nodes unclickable; documented.
- Modifier summary empty on both sides → `modifiers` omitted; Mods section hidden.

---

## Verification (no unit tests — IL2CPP)

1. Build server (`MSBuild YgoMasterServer/YgoMaster.csproj`) and client (`MSBuild YgoMasterClient.csproj`).
2. New run, open the map. Click a **reachable** combat node → drawer slides in from the right
   with type, art, name, "LP do inimigo", "Recompensa", modifier tags, and a "Duelo" button.
3. Click "Duelo" → drawer closes and the duel starts (same as before).
4. Click a **locked** node ahead and a **visited** node behind → drawer opens read-only (no
   action button). Click the current node → no action button.
5. Click outside the panel (scrim) → drawer slides out, no move.
6. Set an `enemy.extraLp`/`extraHand` via `modifierDefaults` for a type → its drawer shows the
   matching tag and the LP reflects the encounter/default value.
7. Regression: moving still works end-to-end; non-combat nodes advance via "Avançar".
