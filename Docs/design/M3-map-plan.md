# Roguelike M3 (Map) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> or superpowers:executing-plans to implement task-by-task. Steps use checkbox (`- [ ]`).

**Goal:** Generate (server, seeded) + render (custom screen) + navigate a Slay-the-Spire
style map; layout is data-driven via `DataLE/Roguelike/Settings.json` behind an abstract
layout class (multi-parent, extensible).

**Architecture:** Server builds the map at `choose_deck` from the run seed + settings,
persists `map`/`position`/`visited` in `roguelike.json`, and validates moves
(`Roguelike.move`). Client pushes a base game screen (`Solo/SoloMode`) and renders nodes +
edges as our own GameObjects (M2.5 pattern), navigating via the act. All new code in
`Roguelike/` (server + client); no `Goat/` reuse.

**Tech Stack:** C# (.NET Framework), MiniJSON, `Utils`, IL2CPP reflection, `Hook<T>`, Unity
wrappers (`GameObject`/`Transform`/`Component`/`UnityObject`), `ViewControllerManager`,
`SelectionButton.onClick` + `_UnityAction`, `RoguelikeCardImage`.

**Verification reality:** IL2CPP mod, **no unit tests** → "verify" = build + run server +
game + observe.
- Client: `MSBuild YgoMasterClient.csproj -t:Build -p:Configuration=Release -v:minimal -nologo`.
- Server: `MSBuild YgoMasterServer/YgoMaster.csproj -t:Build -p:Configuration=Release -p:Platform=x64 -v:minimal -nologo` (+ `-p:GoatInstallDir=""` if the exe is locked).
- Dev console: `vcdump [depth]`, `vclog`, `rgpush <path>`, `rgdeck`.

**Reused facts (verified earlier this feature):**
- `dataDirectory` is a `GameServer` field; Act handlers live on `partial class GameServer`
  in `Roguelike/GameServer.Roguelike.cs`; `GetPlayerDirectory(player)` gives the player dir.
- `RoguelikeRun` (`Roguelike/RoguelikeRun.cs`) round-trips fields via `ToDictionary`/
  `FromDictionary` using `Utils.GetValue<T>`; persisted to `<playerDir>/roguelike.json`;
  piggybacked into the home response so it lands at `$.Roguelike` client-side.
- `choose_deck` (`Act_RoguelikeChooseDeck`) is where the deck is finalized — generate the
  map there.
- Client screen pattern (see `Roguelike/RoguelikeDeckSelectScreen.cs`): push
  `Solo/SoloMode` via `ViewControllerManager.PushChildViewController(manager, "Solo/SoloMode")`;
  one MinHook on `SoloPortalViewController.OnCreatedView` (gated by a pending flag);
  customize the `SoloPortalUI(Clone).Root` subtree (set TMP text, hide groups, clone tiles).
  **MinHook allows ONE hook per target** — the SoloPortal hook is already owned by
  `RoguelikeDeckSelectScreen`, so Task 6 refactors it into a shared dispatcher.
- Clone/onClick: `UnityObject.Instantiate`, `UnityObject.SetName`,
  `GameObject.FindGameObjectByPath/FindGameObjectByName`, `GameObject.GetComponent`,
  `GameObject.New`, `GameObject.AddComponent`, `GameObject.GetTransform`,
  `Transform.SetParent/SetAsLastSibling/SetLocalScale`, `TMPro.TMP_Text.SetText`,
  `SelectionButton.onClick` field + `UnityEvent.AddListener` +
  `UnityEngine.Events._UnityAction.CreateUnityAction(Action)` /
  `CreateAction<int>(Action<IntPtr,int>)`; RectTransform setters
  (`anchorMin/Max`, `offsetMin/Max`, `pivot`, `sizeDelta`, `anchoredPosition`/`anchoredPosition3D`).
- `RoguelikeApi.Call(act, args)` issues acts via `Request.Entry`; `RoguelikeFlow.OnNetworkComplete`
  reacts to completed acts; `RoguelikeHomeButton.OnMenuSelect` has the Continuar/Abandonar menu.

---

## Task 1 — Server: `RoguelikeSettings` + `Settings.json`

**Files:** Create `YgoMasterServer/Roguelike/RoguelikeSettings.cs`, modify
`YgoMasterServer/YgoMaster.csproj`; data: create `<install>/DataLE/Roguelike/Settings.json`.

- [ ] **Step 1: Settings loader.** Reads/caches the JSON with defaults if absent.

```csharp
// YgoMasterServer/Roguelike/RoguelikeSettings.cs
using System.Collections.Generic;
using System.IO;

namespace YgoMaster
{
    // Data-driven roguelike config (DataLE/Roguelike/Settings.json). Defaults applied when
    // the file or a key is missing, so the mod runs without it.
    static class RoguelikeSettings
    {
        static Dictionary<string, object> _cache;

        public static Dictionary<string, object> Load(string dataDirectory)
        {
            if (_cache != null) return _cache;
            string p = Path.Combine(dataDirectory, "Roguelike", "Settings.json");
            Dictionary<string, object> d = null;
            if (File.Exists(p))
            {
                try { d = MiniJSON.Json.DeserializeStripped(File.ReadAllText(p)) as Dictionary<string, object>; }
                catch { d = null; }
            }
            _cache = d ?? new Dictionary<string, object>();
            return _cache;
        }

        public static string Layout(Dictionary<string, object> s) => Utils.GetValue<string>(s, "layout", "slay_the_spire");
        public static int Floors(Dictionary<string, object> s) => Utils.GetValue<int>(s, "floors", 8);
        public static int Width(Dictionary<string, object> s) => Utils.GetValue<int>(s, "width", 4);

        // type -> weight (duel forced on floor 0, boss fixed on top — not in this table).
        public static Dictionary<string, object> TypeWeights(Dictionary<string, object> s)
        {
            Dictionary<string, object> w = Utils.GetValue<Dictionary<string, object>>(s, "typeWeights");
            if (w != null && w.Count > 0) return w;
            return new Dictionary<string, object>
            {
                { "duel", 0.6 }, { "elite", 0.12 }, { "event", 0.12 }, { "shop", 0.08 }, { "reward", 0.08 },
            };
        }
    }
}
```

- [ ] **Step 2: csproj include.** Add to `YgoMasterServer/YgoMaster.csproj` near the other
  `Roguelike\*` includes: `<Compile Include="Roguelike\RoguelikeSettings.cs" />`
- [ ] **Step 3: Create the data file** `<install>/DataLE/Roguelike/Settings.json`:

```json
{
  "layout": "slay_the_spire",
  "floors": 8,
  "width": 4,
  "typeWeights": { "duel": 0.6, "elite": 0.12, "event": 0.12, "shop": 0.08, "reward": 0.08 }
}
```

- [ ] **Step 4: Build server** (command in header). Expected: success.
- [ ] **Step 5: Commit** (code only — Settings.json is install-side data):
```bash
git add YgoMasterServer/Roguelike/RoguelikeSettings.cs YgoMasterServer/YgoMaster.csproj
git commit -m "feat(roguelike): RoguelikeSettings (DataLE/Roguelike/Settings.json)"
```

---

## Task 2 — Server: map model + `RoguelikeRun` fields

**Files:** Create `YgoMasterServer/Roguelike/RoguelikeMap.cs`, modify
`YgoMasterServer/Roguelike/RoguelikeRun.cs`, `YgoMasterServer/YgoMaster.csproj`.

- [ ] **Step 1: Map model.**

```csharp
// YgoMasterServer/Roguelike/RoguelikeMap.cs
using System.Collections.Generic;

namespace YgoMaster
{
    class MapNode
    {
        public int Id;
        public string Type;          // duel|elite|event|shop|reward|boss
        public int Row;
        public int Col;
        public List<int> Next = new List<int>();   // connected nodes in Row+1

        public Dictionary<string, object> ToDictionary()
        {
            List<object> next = new List<object>();
            foreach (int n in Next) next.Add(n);
            return new Dictionary<string, object>
            {
                { "id", Id }, { "type", Type }, { "row", Row }, { "col", Col }, { "next", next },
            };
        }
    }

    class RoguelikeMap
    {
        public int Rows;
        public List<MapNode> Nodes = new List<MapNode>();

        public Dictionary<string, object> ToDictionary()
        {
            List<object> nodes = new List<object>();
            foreach (MapNode n in Nodes) nodes.Add(n.ToDictionary());
            return new Dictionary<string, object> { { "rows", Rows }, { "nodes", nodes } };
        }
    }
}
```

- [ ] **Step 2: `RoguelikeRun` fields.** Add after `Deck`:

```csharp
public Dictionary<string, object> Map;     // RoguelikeMap.ToDictionary() or null
public int Position = -1;                    // current node id (-1 = entry, before row 0)
public List<object> Visited;                // ids already walked
```

In `ToDictionary()` add:
```csharp
{ "map", Map },
{ "position", Position },
{ "visited", Visited ?? new List<object>() },
```

In `FromDictionary(...)` add:
```csharp
Map      = Utils.GetValue<Dictionary<string, object>>(d, "map"),
Position = Utils.GetValue<int>(d, "position", -1),
Visited  = Utils.GetValue<List<object>>(d, "visited"),
```

- [ ] **Step 3: csproj include** `<Compile Include="Roguelike\RoguelikeMap.cs" />`.
- [ ] **Step 4: Build server.** Expected: success.
- [ ] **Step 5: Commit:**
```bash
git add YgoMasterServer/Roguelike/RoguelikeMap.cs YgoMasterServer/Roguelike/RoguelikeRun.cs YgoMasterServer/YgoMaster.csproj
git commit -m "feat(roguelike): map model + run map/position/visited"
```

---

## Task 3 — Server: abstract layout + factory + `SlayTheSpireLayout`

**Files:** Create `YgoMasterServer/Roguelike/RoguelikeMapLayout.cs`,
`YgoMasterServer/Roguelike/SlayTheSpireLayout.cs`, modify `YgoMasterServer/YgoMaster.csproj`.

- [ ] **Step 1: Abstract base + factory.**

```csharp
// YgoMasterServer/Roguelike/RoguelikeMapLayout.cs
using System.Collections.Generic;

namespace YgoMaster
{
    // Abstract map generator. Add new shapes by subclassing + registering in Create().
    abstract class RoguelikeMapLayout
    {
        public abstract RoguelikeMap Build(int seed, Dictionary<string, object> settings);

        public static RoguelikeMapLayout Create(string layout)
        {
            switch (layout)
            {
                case "slay_the_spire":
                default:
                    return new SlayTheSpireLayout();
            }
        }
    }
}
```

- [ ] **Step 2: Slay-the-Spire generator.** Floors bottom→top; each floor (except boss) has
  2..width nodes spread across columns; each node links to 1-2 nearest nodes in the next
  floor (no crossing); every upper node gets ≥1 incoming edge (connectivity); floor 0 =
  all `duel`; top floor = single `boss`; middle nodes by weighted random.

```csharp
// YgoMasterServer/Roguelike/SlayTheSpireLayout.cs
using System;
using System.Collections.Generic;

namespace YgoMaster
{
    class SlayTheSpireLayout : RoguelikeMapLayout
    {
        public override RoguelikeMap Build(int seed, Dictionary<string, object> settings)
        {
            int floors = Math.Max(2, RoguelikeSettings.Floors(settings));
            int width = Math.Max(2, RoguelikeSettings.Width(settings));
            List<KeyValuePair<string, double>> weights = NormalizeWeights(RoguelikeSettings.TypeWeights(settings));
            Random rng = new Random(seed);

            RoguelikeMap map = new RoguelikeMap { Rows = floors };
            int nextId = 0;
            List<List<MapNode>> rows = new List<List<MapNode>>();

            for (int r = 0; r < floors; r++)
            {
                List<MapNode> row = new List<MapNode>();
                int count = r == floors - 1 ? 1 : (r == 0 ? width : 2 + rng.Next(width - 1)); // boss=1, floor0=width
                // spread `count` nodes across `width` columns, centered
                List<int> cols = SpreadColumns(count, width);
                for (int i = 0; i < count; i++)
                {
                    MapNode n = new MapNode { Id = nextId++, Row = r, Col = cols[i] };
                    n.Type = r == 0 ? "duel" : (r == floors - 1 ? "boss" : PickType(weights, rng));
                    row.Add(n);
                    map.Nodes.Add(n);
                }
                rows.Add(row);
            }

            // edges: connect each node to 1-2 nearest next-floor nodes, then guarantee every
            // next-floor node has an incoming edge.
            for (int r = 0; r < floors - 1; r++)
            {
                List<MapNode> cur = rows[r];
                List<MapNode> nxt = rows[r + 1];
                foreach (MapNode n in cur)
                {
                    MapNode nearest = NearestByCol(nxt, n.Col);
                    n.Next.Add(nearest.Id);
                    if (nxt.Count > 1 && rng.Next(100) < 45) // sometimes a 2nd branch
                    {
                        MapNode second = NearestByCol(nxt, n.Col, exclude: nearest.Id);
                        if (second != null && !n.Next.Contains(second.Id)) n.Next.Add(second.Id);
                    }
                }
                foreach (MapNode up in nxt)
                {
                    if (!HasIncoming(cur, up.Id))
                        NearestByCol(cur, up.Col).Next.Add(up.Id);
                }
            }
            return map;
        }

        static List<int> SpreadColumns(int count, int width)
        {
            List<int> cols = new List<int>();
            if (count >= width) { for (int i = 0; i < count; i++) cols.Add(i % width); return cols; }
            double step = (double)width / (count + 1);
            for (int i = 0; i < count; i++) cols.Add((int)Math.Round(step * (i + 1)) - 1 < 0 ? 0 : (int)Math.Round(step * (i + 1)) - 1);
            return cols;
        }

        static MapNode NearestByCol(List<MapNode> nodes, int col, int exclude = -1)
        {
            MapNode best = null; int bestD = int.MaxValue;
            foreach (MapNode n in nodes)
            {
                if (n.Id == exclude) continue;
                int d = Math.Abs(n.Col - col);
                if (d < bestD) { bestD = d; best = n; }
            }
            return best;
        }

        static bool HasIncoming(List<MapNode> from, int id)
        {
            foreach (MapNode n in from) if (n.Next.Contains(id)) return true;
            return false;
        }

        static List<KeyValuePair<string, double>> NormalizeWeights(Dictionary<string, object> w)
        {
            List<KeyValuePair<string, double>> list = new List<KeyValuePair<string, double>>();
            double total = 0;
            foreach (KeyValuePair<string, object> kv in w)
            {
                double v; try { v = Convert.ToDouble(kv.Value); } catch { v = 0; }
                if (v > 0) { list.Add(new KeyValuePair<string, double>(kv.Key, v)); total += v; }
            }
            if (total <= 0) list.Add(new KeyValuePair<string, double>("duel", 1.0));
            return list;
        }

        static string PickType(List<KeyValuePair<string, double>> weights, Random rng)
        {
            double total = 0; foreach (var kv in weights) total += kv.Value;
            double roll = rng.NextDouble() * total;
            foreach (var kv in weights) { roll -= kv.Value; if (roll <= 0) return kv.Key; }
            return weights[0].Key;
        }
    }
}
```

- [ ] **Step 3: csproj includes** for both files.
- [ ] **Step 4: Build server.** Expected: success.
- [ ] **Step 5: Commit:**
```bash
git add YgoMasterServer/Roguelike/RoguelikeMapLayout.cs YgoMasterServer/Roguelike/SlayTheSpireLayout.cs YgoMasterServer/YgoMaster.csproj
git commit -m "feat(roguelike): abstract map layout + Slay-the-Spire generator"
```

---

## Task 4 — Server: generate on choose_deck + `Roguelike.move` + dispatch

**Files:** `YgoMasterServer/Roguelike/GameServer.Roguelike.cs`, `YgoMasterServer/GameServer.cs`.

- [ ] **Step 1: Generate the map when the deck is chosen.** In `Act_RoguelikeChooseDeck`,
  right after `run.DeckChosen = true; run.DeckOffers = new List<object>();` (and before
  `run.Save(...)`), add:

```csharp
Dictionary<string, object> settings = RoguelikeSettings.Load(dataDirectory);
RoguelikeMapLayout layout = RoguelikeMapLayout.Create(RoguelikeSettings.Layout(settings));
run.Map = layout.Build((int)run.Seed, settings).ToDictionary();
run.Position = -1;
run.Visited = new List<object>();
```

- [ ] **Step 2: `Act_RoguelikeMove`.** Validate the target is reachable from the current
  position (entry → any row-0 node; else a `next` of the current node), then advance.

```csharp
void Act_RoguelikeMove(GameServerWebRequest request)
{
    RoguelikeRun run = RoguelikeRun.Load(GetPlayerDirectory(request.Player));
    if (run.Active && run.DeckChosen && run.Map != null)
    {
        int target = request.ActParams != null ? Utils.GetValue<int>(request.ActParams, "nodeId", -1) : -1;
        if (IsReachable(run, target))
        {
            run.Position = target;
            if (run.Visited == null) run.Visited = new List<object>();
            if (!run.Visited.Contains(target)) run.Visited.Add(target);
            run.Save(GetPlayerDirectory(request.Player));
        }
    }
    request.Response["Roguelike"] = run.ToDictionary();
    request.Remove("Roguelike");
}

// Reachable = entry → any node in row 0; otherwise the current node's `next` list.
bool IsReachable(RoguelikeRun run, int target)
{
    List<object> nodes = Utils.GetValue<List<object>>(run.Map, "nodes");
    if (nodes == null) return false;
    Dictionary<string, object> targetNode = null, currentNode = null;
    foreach (object o in nodes)
    {
        Dictionary<string, object> n = o as Dictionary<string, object>;
        if (n == null) continue;
        int id = Utils.GetValue<int>(n, "id", -999);
        if (id == target) targetNode = n;
        if (id == run.Position) currentNode = n;
    }
    if (targetNode == null) return false;
    if (run.Position < 0) return Utils.GetValue<int>(targetNode, "row", -1) == 0;
    if (currentNode == null) return false;
    List<object> next = Utils.GetValue<List<object>>(currentNode, "next");
    if (next == null) return false;
    foreach (object v in next) { try { if (Convert.ToInt32(v) == target) return true; } catch { } }
    return false;
}
```

- [ ] **Step 3: Dispatch.** In `GameServer.cs`, after the `Roguelike.choose_deck` case add:
  `case "Roguelike.move": Act_RoguelikeMove(gameServerWebRequest); break;`
- [ ] **Step 4: Build server.** Expected: success.
- [ ] **Step 5: Verify (temporary, optional):** run server+game, choose a deck, then inspect
  `<player>/roguelike.json` — `map` has `nodes`/`rows`, `position` is -1, `visited` is empty.
- [ ] **Step 6: Commit:**
```bash
git add YgoMasterServer/Roguelike/GameServer.Roguelike.cs YgoMasterServer/GameServer.cs
git commit -m "feat(roguelike): generate map on choose_deck + move act"
```

---

## Task 5 — Client: `RoguelikeApi` map readers + `Move`

**Files:** `YgoMasterClient/Roguelike/RoguelikeApi.cs`.

- [ ] **Step 1: Add a node struct + readers + Move.** Read `$.Roguelike.map`/`position` via
  `ClientWork.SerializePath` + `MiniJSON.Json.Deserialize`.

```csharp
public class MapNode
{
    public int Id, Row, Col;
    public string Type = "duel";
    public System.Collections.Generic.List<int> Next = new System.Collections.Generic.List<int>();
}

public static int Position()
{
    return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.position");
}

public static System.Collections.Generic.List<MapNode> GetMapNodes()
{
    var result = new System.Collections.Generic.List<MapNode>();
    try
    {
        string json = YgomSystem.Utility.ClientWork.SerializePath("Roguelike.map");
        if (string.IsNullOrEmpty(json)) return result;
        var map = MiniJSON.Json.Deserialize(json) as Dictionary<string, object>;
        var nodes = map != null && map.ContainsKey("nodes") ? map["nodes"] as List<object> : null;
        if (nodes == null) return result;
        foreach (object o in nodes)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) continue;
            var n = new MapNode
            {
                Id = d.ContainsKey("id") ? Convert.ToInt32(d["id"]) : -1,
                Type = d.ContainsKey("type") ? Convert.ToString(d["type"]) : "duel",
                Row = d.ContainsKey("row") ? Convert.ToInt32(d["row"]) : 0,
                Col = d.ContainsKey("col") ? Convert.ToInt32(d["col"]) : 0,
            };
            var next = d.ContainsKey("next") ? d["next"] as List<object> : null;
            if (next != null) foreach (object v in next) { try { n.Next.Add(Convert.ToInt32(v)); } catch { } }
            result.Add(n);
        }
    }
    catch (Exception ex) { Console.WriteLine("[Roguelike] GetMapNodes EX: " + ex); }
    return result;
}

public static void Move(int nodeId)
{
    Call("Roguelike.move", new Dictionary<string, object> { { "nodeId", nodeId } });
}
```

- [ ] **Step 2: Build client.** Expected: success.
- [ ] **Step 3: Commit:**
```bash
git add YgoMasterClient/Roguelike/RoguelikeApi.cs
git commit -m "feat(roguelike): client api — map readers + move"
```

---

## Task 6 — Client: shared SoloPortal hook + `RoguelikeMapScreen` scaffold

**Files:** Create `YgoMasterClient/Roguelike/RoguelikeSoloScreen.cs` (shared hook), modify
`YgoMasterClient/Roguelike/RoguelikeDeckSelectScreen.cs` (use the shared hook), create
`YgoMasterClient/Roguelike/RoguelikeMapScreen.cs`, modify `YgoMasterClient.csproj` +
`Program.cs`.

> **Why a shared hook:** MinHook = one hook per target. `RoguelikeDeckSelectScreen` already
> hooks `SoloPortalViewController.OnCreatedView`. The map reuses the same `Solo/SoloMode`
> base, so move the hook into a shared dispatcher that runs a pending customize callback.

- [ ] **Step 1: Shared hook dispatcher.**

```csharp
// YgoMasterClient/Roguelike/RoguelikeSoloScreen.cs
using IL2CPP;
using System;

namespace YgoMasterClient
{
    // Owns the single SoloPortalViewController.OnCreatedView hook. Open(customize) pushes
    // Solo/SoloMode and runs `customize(portalPtr)` once when the portal view is created.
    static unsafe class RoguelikeSoloScreen
    {
        delegate void Del_OnCreatedView(IntPtr thisPtr);
        static Hook<Del_OnCreatedView> _hook;
        static Action<IntPtr> _pending;
        static bool _ready;

        static RoguelikeSoloScreen()
        {
            try
            {
                IL2Class portal = Assembler.GetAssembly("Assembly-CSharp").GetClass("SoloPortalViewController", "YgomGame.Solo");
                _hook = new Hook<Del_OnCreatedView>(OnCreatedView, portal.GetMethod("OnCreatedView"));
                _ready = true;
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] soloscreen init EX: " + ex); }
        }

        public static void Open(Action<IntPtr> customize)
        {
            if (!_ready) return;
            IntPtr manager = YgomGame.Menu.ContentViewControllerManager.GetManager();
            if (manager == IntPtr.Zero) return;
            _pending = customize;
            YgomSystem.UI.ViewControllerManager.PushChildViewController(manager, "Solo/SoloMode");
        }

        static void OnCreatedView(IntPtr thisPtr)
        {
            _hook.Original(thisPtr);
            Action<IntPtr> c = _pending; _pending = null;
            if (c == null) return;
            try { c(thisPtr); } catch (Exception ex) { Console.WriteLine("[Roguelike] soloscreen customize EX: " + ex); }
        }

        // Helper: the SoloPortal content root GameObject.
        public static IntPtr PortalRoot(IntPtr portalPtr)
        {
            return GameObject.FindGameObjectByPath(Component.GetGameObject(portalPtr), "SoloPortalUI(Clone).Root");
        }
    }
}
```

- [ ] **Step 2: Refactor `RoguelikeDeckSelectScreen` to use it.** Remove its own
  `_portalClass`/`_hook`/`Del_OnCreatedView`/`OnCreatedView` and the `_pending` push logic;
  change `Open()` to:
```csharp
public static void Open()
{
    if (!_ready) return;
    RoguelikeSoloScreen.Open(Customize);
}
```
  Keep `Customize(IntPtr portalPtr)` as-is (it already finds `SoloPortalUI(Clone).Root` and
  builds tiles). Remove `RoguelikeDeckSelectScreen` from `Program.cs` nativeTypes only if it
  no longer needs eager init — it still resolves IL2 types in its cctor (SelectionButton,
  etc.), so keep it registered. Register `RoguelikeSoloScreen` in `Program.cs` nativeTypes
  (eager — installs the hook at startup): `nativeTypes.Add(typeof(RoguelikeSoloScreen));`

- [ ] **Step 3: Map screen scaffold.** Pushes via the shared hook, sets the title, hides the
  SoloPortal content groups, and creates an empty full-screen `RgMapContent` panel (nodes
  come in Task 7).

```csharp
// YgoMasterClient/Roguelike/RoguelikeMapScreen.cs
using IL2CPP;
using System;
using UnityEngine;

namespace YgoMasterClient
{
    static unsafe class RoguelikeMapScreen
    {
        static IntPtr _tmpType, _bindingTextType, _rectType, _imageType;
        static IL2Property _anchorMin, _anchorMax, _offsetMin, _offsetMax;
        static IntPtr _content;
        static bool _ready;

        static RoguelikeMapScreen()
        {
            try
            {
                _tmpType = CastUtils.IL2Typeof("ExtendedTextMeshProUGUI", "YgomSystem.YGomTMPro", "Assembly-CSharp");
                _bindingTextType = CastUtils.IL2Typeof("BindingTextMeshProUGUI", "YgomSystem.UI", "Assembly-CSharp");
                IL2Assembly core = Assembler.GetAssembly("UnityEngine.CoreModule");
                IL2Class rect = core.GetClass("RectTransform", "UnityEngine");
                _rectType = rect.IL2Typeof();
                _anchorMin = rect.GetProperty("anchorMin");
                _anchorMax = rect.GetProperty("anchorMax");
                _offsetMin = rect.GetProperty("offsetMin");
                _offsetMax = rect.GetProperty("offsetMax");
                _imageType = Assembler.GetAssembly("UnityEngine.UI").GetClass("Image", "UnityEngine.UI").IL2Typeof();
                _ready = true;
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] map init EX: " + ex); }
        }

        public static void Open()
        {
            if (!_ready) return;
            RoguelikeSoloScreen.Open(Customize);
        }

        static void Customize(IntPtr portalPtr)
        {
            IntPtr root = RoguelikeSoloScreen.PortalRoot(portalPtr);
            if (root == IntPtr.Zero) { Console.WriteLine("[Roguelike] map: portal root not found"); return; }
            SetText(root, "TitleSafeArea.TitleGroup.NameText", "Mapa");
            Hide(root, "ButtonArea.MainGroup");
            Hide(root, "ButtonArea.GateListGroup");

            // full-screen transparent content panel for our nodes/edges
            IntPtr content = GameObject.New();
            UnityObject.SetName(content, "RgMapContent");
            GameObject.AddComponent(content, _rectType);
            IntPtr ct = GameObject.GetTransform(content);
            Transform.SetParent(ct, GameObject.GetTransform(root));
            SetFull(ct);
            Transform.SetAsLastSibling(ct);
            Transform.SetLocalScale(ct, new Vector3(1, 1, 1));
            _content = content;
            Console.WriteLine("[Roguelike] map customized");
            // Task 7 calls Render(_content) here.
        }

        static void Hide(IntPtr root, string path)
        {
            IntPtr o = GameObject.FindGameObjectByPath(root, path);
            if (o != IntPtr.Zero) GameObject.SetActive(o, false);
        }

        static void SetText(IntPtr root, string path, string text)
        {
            IntPtr o = GameObject.FindGameObjectByPath(root, path);
            if (o == IntPtr.Zero) return;
            IntPtr binding = GameObject.GetComponent(o, _bindingTextType);
            if (binding != IntPtr.Zero) { YgomSystem.UI.BindingTextMeshProUGUI.SetTextId(binding, text); return; }
            IntPtr tmp = GameObject.GetComponent(o, _tmpType);
            if (tmp != IntPtr.Zero) TMPro.TMP_Text.SetText(tmp, text);
        }

        static void SetFull(IntPtr t)
        {
            AssetHelper.Vector2 min = new AssetHelper.Vector2(0, 0), max = new AssetHelper.Vector2(1, 1), zero = new AssetHelper.Vector2(0, 0);
            _anchorMin.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&min) });
            _anchorMax.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&max) });
            _offsetMin.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&zero) });
            _offsetMax.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&zero) });
        }
    }
}
```
> `using YgoMaster;` needed for `AssetHelper`. Add it.

- [ ] **Step 4: Register.** csproj includes for `RoguelikeSoloScreen.cs` + `RoguelikeMapScreen.cs`;
  `Program.cs`: add `nativeTypes.Add(typeof(RoguelikeSoloScreen));` (and keep
  `RoguelikeDeckSelectScreen`). Add a dev command in `ConsoleHelper.cs`:
  `case "rgmap": RoguelikeMapScreen.Open(); break;`
- [ ] **Step 5: Build client.** Expected: success.
- [ ] **Step 6: Verify in-game.** With a run that has a chosen deck (so the map exists), type
  `rgmap` → SoloMode opens, title "Mapa", the recommend/lastplay/gatelist groups hidden,
  empty `RgMapContent` present (console `[Roguelike] map customized`). Also confirm the
  deck-select screen still works (Nova Run) after the hook refactor, and normal Solo is intact.
- [ ] **Step 7: Commit:**
```bash
git add YgoMasterClient/Roguelike/RoguelikeSoloScreen.cs YgoMasterClient/Roguelike/RoguelikeDeckSelectScreen.cs YgoMasterClient/Roguelike/RoguelikeMapScreen.cs YgoMasterClient.csproj YgoMasterClient/Program.cs YgoMasterClient/ConsoleHelper.cs
git commit -m "feat(roguelike): shared SoloPortal hook + map screen scaffold"
```

---

## Task 7 — Client: render nodes + navigation

**Files:** `YgoMasterClient/Roguelike/RoguelikeMapScreen.cs`.

- [ ] **Step 1: Node colors + render.** Build one GameObject per node (Image colored by
  type) positioned by row/col inside `RgMapContent`; current node highlighted; reachable
  nodes get a click → `RoguelikeApi.Move`. Re-render after a move.

```csharp
// fields
static readonly Action<IntPtr, int>[] _nodeHandlers = BuildHandlers(64);
static System.Collections.Generic.List<RoguelikeApi.MapNode> _nodes;
static IL2Property _imageColor;     // resolve in cctor: Graphic.color (see RoguelikeUiProof note)
static IL2Property _anchoredPos;    // RectTransform.anchoredPosition
static IL2Property _sizeDelta;      // RectTransform.sizeDelta
static IntPtr _selectionButtonType; static IL2Field _selBtnOnClick; static IL2Method _ueAddListener, _ueRemoveAll;

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct Rgba { public float r, g, b, a; public Rgba(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; } }

static Rgba ColorFor(string type, bool reachable, bool current)
{
    if (current) return new Rgba(1f, 0.85f, 0.2f, 1f);
    Rgba c;
    switch (type)
    {
        case "elite":  c = new Rgba(0.85f, 0.2f, 0.2f, 1f); break;
        case "boss":   c = new Rgba(0.6f, 0.1f, 0.7f, 1f); break;
        case "shop":   c = new Rgba(0.2f, 0.6f, 0.9f, 1f); break;
        case "event":  c = new Rgba(0.9f, 0.7f, 0.2f, 1f); break;
        case "reward": c = new Rgba(0.2f, 0.8f, 0.4f, 1f); break;
        default:        c = new Rgba(0.7f, 0.7f, 0.7f, 1f); break; // duel
    }
    if (!reachable) { c.r *= 0.5f; c.g *= 0.5f; c.b *= 0.5f; }
    return c;
}
```

> **cctor additions (Step 1):** resolve `Graphic.color` (it's on `Graphic`, not `Image` —
> mirror `RoguelikeCardImage`/`RoguelikeUiProof`), `RectTransform.anchoredPosition`,
> `RectTransform.sizeDelta`, and the SelectionButton onClick wiring (copy from
> `RoguelikeDeckSelectScreen`: `SelectionButton` type + `onClick` field, `UnityEvent`
> `AddListener`, `UnityEventBase` `RemoveAllListeners`). `BuildHandlers(n)` returns an
> `Action<IntPtr,int>[]` of `(c,i)=>OnNode(index)` closures so each node has a stable
> callback (node click is parameterless → wrap an index per slot).

- [ ] **Step 2: Render method** (called at the end of `Customize`, and after each move):

```csharp
const float NodeSize = 90f;

static void Render()
{
    if (_content == IntPtr.Zero) return;
    // clear previous nodes
    int childCount = Transform.GetChildCount(GameObject.GetTransform(_content));
    for (int i = childCount - 1; i >= 0; i--)
        GameObject.SetActive(Component.GetGameObject(Transform.GetChild(GameObject.GetTransform(_content), i)), false);

    _nodes = RoguelikeApi.GetMapNodes();
    if (_nodes.Count == 0) return;
    int pos = RoguelikeApi.Position();
    int rows = 0; foreach (var n in _nodes) rows = Math.Max(rows, n.Row + 1);

    foreach (RoguelikeApi.MapNode n in _nodes)
    {
        bool current = n.Id == pos;
        bool reachable = IsReachableClient(n, pos);
        IntPtr node = MakeNode(n, reachable, current, rows);
        // index handler by node id (ids are 0..N, < BuildHandlers cap)
        if (reachable && n.Id < _nodeHandlers.Length) WireClick(node, n.Id);
    }
}

static bool IsReachableClient(RoguelikeApi.MapNode n, int pos)
{
    if (pos < 0) return n.Row == 0;
    foreach (var cur in _nodes) if (cur.Id == pos) return cur.Next.Contains(n.Id);
    return false;
}

static void OnNode(int nodeId)
{
    RoguelikeApi.Move(nodeId); // server validates; response updates $.Roguelike → Render() re-runs from the completion hook
}
```

> `MakeNode` creates a GameObject under `_content` with `_rectType` + `_imageType`, sets the
> color (`_imageColor` setter via a boxed `Rgba` pointer), anchors it bottom-left, and sets
> `anchoredPosition` from row/col: `x = (col+1) * contentWidth/(width+1)`,
> `y = (row+1) * contentHeight/(rows+1)` — compute `contentWidth/Height` from the content
> RectTransform `rect` (resolve `RectTransform.rect` → a `Rect` struct), or use fixed screen
> constants (e.g. 1920×1080 design) if reading `rect` is fiddly. `sizeDelta = (NodeSize,
> NodeSize)`. Add a small TMP label (type initial) or skip for v1. `WireClick(node, id)`
> clones the SelectionButton-onClick approach: add a `SelectionButton` is not trivial on a
> bare GameObject, so instead clone a minimal existing `SelectionButton` for the node OR use
> `UnityEngine.UI.Button` + `Button.onClick`. **Discovery:** try `UnityEngine.UI.Button`
> first (resolve `Button` + `onClick`); if clicks don't register, fall back to cloning a
> SelectionButton template captured via `vcdump`.

- [ ] **Step 3: Re-render after a move.** In `RoguelikeFlow.OnNetworkComplete`, add:
  `else if (cmd == "Roguelike.move") RoguelikeMapScreen.OnMoved();` and implement
  `public static void OnMoved() { Render(); }` (re-reads `$.Roguelike` which the move
  response refreshed). Call `Render()` at the end of `Customize` too (initial draw).
- [ ] **Step 4: Build client.** Expected: success.
- [ ] **Step 5: Verify in-game (`rgmap`).** Nodes appear in a bottom→top layout, colored by
  type, boss at top; row-0 nodes clickable; clicking moves (current node turns gold, next
  row becomes clickable); reaching the top works. `roguelike.json` `position`/`visited` update.
- [ ] **Step 6: Commit:**
```bash
git add YgoMasterClient/Roguelike/RoguelikeMapScreen.cs YgoMasterClient/Roguelike/RoguelikeFlow.cs
git commit -m "feat(roguelike): render map nodes + navigation"
```

---

## Task 8 — Client: edges (connection lines)

**Files:** `YgoMasterClient/Roguelike/RoguelikeMapScreen.cs`.

- [ ] **Step 1: Draw a line per edge.** For each node→next, draw a thin Image from the node
  center to the next-node center (drawn into `RgMapContent` BEFORE the nodes so nodes sit on
  top). Build a helper:

```csharp
static IL2Property _localEuler;   // resolve in cctor: Transform.localEulerAngles

static void DrawLine(AssetHelper.Vector2 a, AssetHelper.Vector2 b)
{
    IntPtr line = GameObject.New();
    UnityObject.SetName(line, "RgEdge");
    GameObject.AddComponent(line, _rectType);
    IntPtr img = GameObject.AddComponent(line, _imageType);
    IntPtr t = GameObject.GetTransform(line);
    Transform.SetParent(t, GameObject.GetTransform(_content));
    // anchor bottom-left, pivot center; position at midpoint; size = (distance, thickness)
    float dx = b.x - a.x, dy = b.y - a.y;
    float len = (float)Math.Sqrt(dx * dx + dy * dy);
    AssetHelper.Vector2 mid = new AssetHelper.Vector2((a.x + b.x) / 2, (a.y + b.y) / 2);
    SetBottomLeftPivotCenter(t);
    SetAnchoredPosition(t, mid);
    SetSizeDelta(t, new AssetHelper.Vector2(len, 6f));
    float angle = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);
    Vector3 euler = new Vector3(0, 0, angle);
    _localEuler.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&euler) });
    // dim color
    Rgba c = new Rgba(1f, 1f, 1f, 0.25f);
    _imageColor.GetSetMethod().Invoke(img, new IntPtr[] { new IntPtr(&c) });
    Transform.SetLocalScale(t, new Vector3(1, 1, 1));
}
```

> `SetBottomLeftPivotCenter` sets anchorMin=anchorMax=(0,0), pivot=(0.5,0.5).
> `SetAnchoredPosition`/`SetSizeDelta` use the `_anchoredPos`/`_sizeDelta` setters from Task 7.
> Node screen positions are the same `(x,y)` computed in `MakeNode` — extract that into a
> `NodePos(node, rows)` helper so lines and nodes agree. `_localEuler` is
> `Transform.localEulerAngles` (UnityEngine.CoreModule, `Transform`).

- [ ] **Step 2: Call line drawing first in `Render`** (before creating nodes): iterate nodes,
  for each `next` id resolve the next node, `DrawLine(NodePos(n), NodePos(nextNode))`.
- [ ] **Step 3: Build + verify in-game (`rgmap`).** Lines connect nodes floor→floor; nodes
  render on top; layout reads clearly.
- [ ] **Step 4: Commit:**
```bash
git add YgoMasterClient/Roguelike/RoguelikeMapScreen.cs
git commit -m "feat(roguelike): map edges (connection lines)"
```

---

## Task 9 — Client: wire "Continuar" → map + end-to-end verification

**Files:** `YgoMasterClient/Roguelike/RoguelikeHomeButton.cs`, `YgoMasterClient/ConsoleHelper.cs`.

- [ ] **Step 1: Continuar opens the map.** In `RoguelikeHomeButton.OnMenuSelect`, replace the
  "Run em andamento" alert (the `index == 0` branch when `IsDeckChosen()` is true) with:
  `RoguelikeMapScreen.Open();`
- [ ] **Step 2: Remove the dev `rgmap` command** (keep `vcdump`/`vclog`/`rgpush`/`rgdeck`).
- [ ] **Step 3: Build client.**
- [ ] **Step 4: Full end-to-end verification (DoD):**
  - Fresh: Home → ROGUELIKE → Nova Run → pick a deck → run created with a map.
  - Home → ROGUELIKE → **Continuar** → the map screen opens (StS layout, colored nodes,
    edges, boss on top).
  - Navigate bottom→top; current node highlights; only connected next nodes are clickable;
    `position`/`visited` persist (reopen Continuar resumes where you were).
  - Edit `DataLE/Roguelike/Settings.json` (e.g. `floors`/`width`), restart server, Nova Run
    → the new map reflects the config.
  - Normal Solo mode + the deck-select screen still work.
- [ ] **Step 5: Commit + mark M3 done:**
```bash
git add YgoMasterClient/Roguelike/RoguelikeHomeButton.cs YgoMasterClient/ConsoleHelper.cs
git commit -m "feat(roguelike): Continuar opens the map (M3 complete)"
```
  Then add an "M3 concluído" note to `Docs/roguelike/roguelike-design.md` and commit.

---

## Self-review notes
- **Spec coverage:** Settings.json + loader (T1); map model + run fields (T2); abstract
  layout + StS (T3); generate on choose_deck + move act (T4); client api (T5); shared hook +
  map screen scaffold (T6); nodes + navigation (T7); edges (T8); Continuar wiring + DoD (T9).
- **Type consistency:** server `MapNode{Id,Type,Row,Col,Next}` ↔ client `RoguelikeApi.MapNode`;
  run fields `Map`(dict)/`Position`(int,-1=entry)/`Visited`(list); `Roguelike.move{nodeId}`;
  reachable rule identical server (`IsReachable`) and client (`IsReachableClient`).
- **Honest gaps (resolve during execution, not faked):** (1) node click mechanism —
  `UnityEngine.UI.Button.onClick` vs cloning a `SelectionButton` (T7 Step 2 discovery);
  (2) content panel pixel size — read `RectTransform.rect` or use a fixed design size (T7);
  (3) `Graphic.color` is on `Graphic` not `Image` (already learned in M2.5). Each has a
  concrete fallback; none are silent guesses.
- **House rule:** all new code in `Roguelike/`; no `Goat/` reuse; config in
  `DataLE/Roguelike/Settings.json`.
