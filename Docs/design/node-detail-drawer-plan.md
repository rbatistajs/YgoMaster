# Node Detail Drawer — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clicking any map node opens an animated right-side drawer with that node's details (type, art, name, enemy LP, reward, declared-modifier tags); an action button inside (reachable nodes only) performs the move/duel.

**Architecture:** Client-heavy. Server bakes three extra per-node fields (`enemyLp`, `reward`, `modifiers` summary). A new `RoguelikeNodeDrawer` (built once under the run-screen Window, slid in via `TweenPosition`) replaces the click-to-move behavior; `RoguelikeMapScreen` wires every node (not just reachable) to open it, and the drawer's button calls the existing `RoguelikeApi.Move`.

**Tech Stack:** C# IL2CPP client mod (`YgoMasterClient`) + C# server (`YgoMaster`). Spec: `Docs/roguelike/node-detail-drawer-design.md`.

**No unit tests:** IL2CPP mod — verification is build + in-game. IL2CPP UI sizing/positioning routinely needs in-engine tweaks; the code below is complete and compiling, refine layout constants during the Task 6 in-game pass.

**Build commands:**
- Client: `"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterClient.csproj" -nologo -v:minimal` → tail `[Goat] Copied YgoMasterClient.exe -> ...install`.
- Server: `"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "D:\www\ygomaster-fork\YgoMasterRogueLike\YgoMasterServer\YgoMaster.csproj" -nologo -v:minimal` → `YgoMaster -> ...\YgoMaster.exe`. If the server is running, copy fails MSB3027 (compile OK); close it and `cp` the exe to the install.

**Install dir:** `D:\SteamLibrary\steamapps\common\Yu-Gi-Oh!  Master Duel\YgoMasterLE - Goat`

**Commits:** This repo commits only on explicit user request (conventional style, no AI attribution). The final commit step runs only when the user approves.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `YgoMasterServer/Roguelike/RoguelikeMap.cs` | Map/node model + serialization | +3 nullable fields + ToDictionary |
| `YgoMasterServer/Roguelike/GameServer.Roguelike.cs` | Map gen / bake | Bake stats; `SummarizeModifiers` |
| `YgoMasterClient/Roguelike/RoguelikeApi.cs` | Client read model | Parse `enemyLp`/`reward`/`modifiers` |
| `YgoMasterClient/Roguelike/RoguelikeMapScreen.cs` | Map render + click | Expose helpers; 64 slots; wire all nodes; `OnSlot`→drawer; close-on-render |
| `YgoMasterClient/Roguelike/RoguelikeNodeDrawer.cs` (new) | The drawer | Build/Open/Close/Populate/animate |
| `YgoMasterClient.csproj` | Build | Include the new file |
| `<install>/DataLE/Roguelike/Labels.json` | Live labels | New keys |

---

## Task 1: Server — MapNode rich fields + serialization

**Files:**
- Modify: `YgoMasterServer/Roguelike/RoguelikeMap.cs`

- [ ] **Step 1: Add the fields**

After the `IconImage` field (line 14) add:

```csharp
        public int? EnemyLp;         // baked combat enemy starting LP (preview), if any
        public int? Reward;          // baked currency reward (preview), if any
        public Dictionary<string, object> Modifiers; // declared-modifier summary, or null
```

- [ ] **Step 2: Serialize them**

In `ToDictionary`, after the `IconImage` line (`if (!string.IsNullOrEmpty(IconImage)) d["iconImage"] = IconImage;`) add:

```csharp
            if (EnemyLp.HasValue) d["enemyLp"] = EnemyLp.Value;
            if (Reward.HasValue) d["reward"] = Reward.Value;
            if (Modifiers != null && Modifiers.Count > 0) d["modifiers"] = Modifiers;
```

- [ ] **Step 3: Build server**

Run the server build command. Expected: `YgoMaster -> ...\YgoMaster.exe`.

---

## Task 2: Server — bake stats + modifier summary

**Files:**
- Modify: `YgoMasterServer/Roguelike/GameServer.Roguelike.cs` (`BakeEncounters`, ~line 300)

- [ ] **Step 1: Bake LP/reward/modifiers in the node loop**

`BakeEncounters` currently `continue`s for non-bakeTypes before computing anything. Restructure so the encounter pick stays gated but stats run for every node. Replace the loop body:

```csharp
            HashSet<string> bakeTypes = RoguelikeSettings.BakeTypes(eff);
            foreach (MapNode n in map.Nodes)
            {
                RoguelikeEncounters.Encounter e = null;
                if (bakeTypes.Contains(n.Type))
                {
                    Random rng = new Random(DuelRngSeed(run.Seed, run.Act, n.Id));
                    e = RoguelikeEncounters.Pick(dataDirectory, n.Type, run.Act, n.Row, run.Ascension, rng);
                    if (e == null)
                        Console.WriteLine("[Roguelike] no " + n.Type + " encounter to bake (act " + run.Act + " asc " + run.Ascension + ")");
                }
                if (e != null)
                {
                    n.Encounter = e.Id;
                    n.Name = e.Name;
                    n.IconImage = !string.IsNullOrEmpty(e.IconImage) ? e.IconImage : DeriveIconImage(e.Deck);
                }
                if (IsCombat(n.Type))
                {
                    n.EnemyLp = (e != null && e.EnemyLp.HasValue) ? e.EnemyLp.Value : RoguelikeSettings.EnemyLpFor(eff, n.Type);
                    n.Reward = (e != null && e.Reward.HasValue) ? e.Reward.Value : RewardFor(n.Type);
                }
                n.Modifiers = SummarizeModifiers(eff, n.Type, e);
            }
```

- [ ] **Step 2: Add `SummarizeModifiers`**

After `BakeEncounters` (before `DeriveIconImage`) add. It merges the declared layers (per-type defaults + encounter) via the existing `RoguelikeModifiers.Merge`, then flattens each side to scalars/counts:

```csharp
        // Declared-modifier preview for a node: per-type defaults merged with the encounter's own
        // modifiers (Merge sums extraLp/extraHand, keeps positional lists). Flattened to
        // { side: { extraLp, extraHand, monsters, spellTraps, hand } } with zero entries omitted.
        // Declared only — no seeded resolution. Returns null when nothing to show.
        Dictionary<string, object> SummarizeModifiers(Dictionary<string, object> eff, string type, RoguelikeEncounters.Encounter enc)
        {
            List<Dictionary<string, object>> layers = new List<Dictionary<string, object>>();
            Dictionary<string, object> defaults = RoguelikeSettings.ModifierDefaults(eff, type);
            if (defaults != null) layers.Add(defaults);
            if (enc != null && enc.Modifiers != null) layers.Add(enc.Modifiers);
            if (layers.Count == 0) return null;

            Dictionary<string, object> merged = RoguelikeModifiers.Merge(layers);
            Dictionary<string, object> outDict = new Dictionary<string, object>();
            foreach (string side in new[] { "player", "enemy" })
            {
                Dictionary<string, object> s = Utils.GetValue<Dictionary<string, object>>(merged, side);
                if (s == null) continue;
                Dictionary<string, object> flat = new Dictionary<string, object>();
                AddIfNonZero(flat, "extraLp", Utils.GetValue<int>(s, "extraLp", 0));
                AddIfNonZero(flat, "extraHand", Utils.GetValue<int>(s, "extraHand", 0));
                AddIfNonZero(flat, "monsters", CountList(s, "monsters"));
                AddIfNonZero(flat, "spellTraps", CountList(s, "spellTraps"));
                AddIfNonZero(flat, "hand", CountList(s, "hand"));
                if (flat.Count > 0) outDict[side] = flat;
            }
            return outDict.Count > 0 ? outDict : null;
        }

        static void AddIfNonZero(Dictionary<string, object> d, string key, int v) { if (v != 0) d[key] = v; }

        static int CountList(Dictionary<string, object> side, string key)
        {
            List<object> l = Utils.GetValue<List<object>>(side, key);
            if (l == null) return 0;
            int c = 0;
            foreach (object o in l) if (o != null) c++;
            return c;
        }
```

- [ ] **Step 3: Confirm `Encounter.EnemyLp` / `Encounter.Reward` are nullable**

Open `YgoMasterServer/Roguelike/RoguelikeEncounters.cs` and verify the `Encounter` class exposes `public int? EnemyLp;` and `public int? Reward;` (used in `BuildRoguelikeDuel` as `enc.EnemyLp ?? ...` and at duel_result as `enc.Reward.HasValue`). They already exist — no change. If a build error says otherwise, they are non-nullable; adjust Step 1's checks to match.

- [ ] **Step 4: Build server**

Run the server build command. Expected: `YgoMaster -> ...\YgoMaster.exe`.

---

## Task 3: Client — parse new node fields

**Files:**
- Modify: `YgoMasterClient/Roguelike/RoguelikeApi.cs` (`MapNode` ~line 168, `GetMapNodes` ~line 213)

- [ ] **Step 1: Add fields to the client model**

In `class MapNode`, after `IconImage`:

```csharp
            public int EnemyLp = -1;   // baked enemy LP (-1 = none)
            public int Reward = -1;    // baked reward (-1 = none)
            public Dictionary<string, object> Modifiers; // declared-modifier summary, or null
```

- [ ] **Step 2: Parse them in `GetMapNodes`**

In the `MapNode n = new MapNode { ... }` initializer, after the `IconImage = ...` line:

```csharp
                        EnemyLp = d.ContainsKey("enemyLp") ? Convert.ToInt32(d["enemyLp"]) : -1,
                        Reward = d.ContainsKey("reward") ? Convert.ToInt32(d["reward"]) : -1,
                        Modifiers = d.ContainsKey("modifiers") ? d["modifiers"] as Dictionary<string, object> : null,
```

- [ ] **Step 3: Build client**

Run the client build command. Expected: `[Goat] Copied YgoMasterClient.exe -> ...`.

---

## Task 4: Client — expose map-screen helpers + Labels

**Files:**
- Modify: `YgoMasterClient/Roguelike/RoguelikeMapScreen.cs`
- Modify: `<install>/DataLE/Roguelike/Labels.json`

- [ ] **Step 1: Make the reused helpers and roots `internal`**

Change these member signatures in `RoguelikeMapScreen.cs` from `private`/implicit to `internal` so `RoguelikeNodeDrawer` can call them:

- `struct Col { ... }` → `internal struct Col { public float r, g, b, a; }`
- `static IntPtr FindIcon(string name)` → `internal static IntPtr FindIcon(string name)`
- `static IntPtr ResolveArtTexture(string iconImage)` → `internal static IntPtr ResolveArtTexture(string iconImage)`
- `static Col TypeColor(string t)` → `internal static Col TypeColor(string t)`
- `static string IconNameFor(string t)` → `internal static string IconNameFor(string t)`

- [ ] **Step 2: Add accessors for the run-screen roots**

Add near the top of the class body (after the const block):

```csharp
        // Run-screen VC root and its Window node, for sibling overlays (the node drawer).
        internal static IntPtr Root() { return _go; }
        internal static IntPtr WindowRoot() { return _go == IntPtr.Zero ? IntPtr.Zero : GameObject.FindGameObjectByPath(_go, Ui); }
```

- [ ] **Step 3: Add Labels keys (install)**

Merge these keys into `<install>/DataLE/Roguelike/Labels.json` (keep existing keys):

```json
  "node.type.duel": "Duelo",
  "node.type.elite": "Elite",
  "node.type.boss": "Chefe",
  "node.type.event": "Evento",
  "node.type.shop": "Loja",
  "node.type.reward": "Recompensa",
  "node.action.duel": "Duelar",
  "node.action.move": "Avançar",
  "node.stat.enemyLp": "LP do inimigo: {0}",
  "node.stat.reward": "Recompensa: {0}",
  "node.mod.enemy.extraLp": "Inimigo: +{0} LP",
  "node.mod.player.extraLp": "Você: +{0} LP",
  "node.mod.enemy.extraHand": "Inimigo: +{0} carta inicial",
  "node.mod.player.extraHand": "Você: +{0} carta inicial",
  "node.mod.enemy.monsters": "Inimigo começa com {0} monstro(s)",
  "node.mod.player.monsters": "Você começa com {0} monstro(s)",
  "node.mod.enemy.spellTraps": "Inimigo começa com {0} mágica/armadilha",
  "node.mod.player.spellTraps": "Você começa com {0} mágica/armadilha",
  "node.mod.enemy.hand": "Inimigo: +{0} na mão",
  "node.mod.player.hand": "Você: +{0} na mão"
```

- [ ] **Step 4: Build client**

Run the client build command. Expected: copied OK (no behavior change yet; the `internal` members are still unused until Task 5).

---

## Task 5: Client — the drawer (`RoguelikeNodeDrawer.cs`)

**Files:**
- Create: `YgoMasterClient/Roguelike/RoguelikeNodeDrawer.cs`
- Modify: `YgoMasterClient.csproj`

- [ ] **Step 1: Create the drawer file**

Create `YgoMasterClient/Roguelike/RoguelikeNodeDrawer.cs` with this content:

```csharp
using IL2CPP;
using System;
using System.Collections.Generic;
using UnityEngine;
using YgoMaster;

namespace YgoMasterClient
{
    // Right-side detail drawer for a clicked map node: type, baked art, name, enemy LP, reward,
    // declared-modifier tags, and an action button (reachable nodes only). Built once under the
    // run-screen Window, repopulated per open, slid in with TweenPosition; the scrim closes it.
    static unsafe class RoguelikeNodeDrawer
    {
        const float Width = 440f;
        const string PanelName = "RgNodeDrawer";
        const string ScrimName = "RgNodeDrawerScrim";

        static IntPtr _rectType, _imageType, _rawImageType, _selButtonType, _tmpType, _bindingTextType;
        static IntPtr _tweenPosType, _graphicTypePtr;
        static IL2Property _anchorMin, _anchorMax, _pivot, _sizeDelta, _anchoredPos3D, _graphicColor, _imageSprite, _rawTexture;
        static IL2Field _selOnClick, _tpRtrans, _tpFrom, _tpTo, _tStyle, _tDuration, _tTarget;
        static IL2Method _tPlay, _ueAddListener, _ueRemoveAll;
        static bool _ready, _visible;
        static int _openNodeId = -1;
        static IntPtr _scrim, _panel, _btn;

        static readonly Action _onScrim = OnScrimClick;
        static readonly Action _onAction = OnActionClick;

        static RoguelikeNodeDrawer()
        {
            try
            {
                IL2Assembly asm = Assembler.GetAssembly("Assembly-CSharp");
                IL2Assembly core = Assembler.GetAssembly("UnityEngine.CoreModule");
                IL2Assembly ui = Assembler.GetAssembly("UnityEngine.UI");
                _rectType = core.GetClass("RectTransform", "UnityEngine").IL2Typeof();
                IL2Class rect = core.GetClass("RectTransform", "UnityEngine");
                _anchorMin = rect.GetProperty("anchorMin");
                _anchorMax = rect.GetProperty("anchorMax");
                _pivot = rect.GetProperty("pivot");
                _sizeDelta = rect.GetProperty("sizeDelta");
                _anchoredPos3D = rect.GetProperty("anchoredPosition3D");
                _imageType = ui.GetClass("Image", "UnityEngine.UI").IL2Typeof();
                _imageSprite = ui.GetClass("Image", "UnityEngine.UI").GetProperty("sprite");
                IL2Class rawImg = ui.GetClass("RawImage", "UnityEngine.UI");
                _rawImageType = rawImg.IL2Typeof();
                _rawTexture = rawImg.GetProperty("texture");
                IL2Class graphic = ui.GetClass("Graphic", "UnityEngine.UI");
                _graphicTypePtr = graphic.IL2Typeof();
                _graphicColor = graphic.GetProperty("color");
                IL2Class selBtn = asm.GetClass("SelectionButton", "YgomSystem.UI");
                _selButtonType = selBtn.IL2Typeof();
                _selOnClick = selBtn.GetField("onClick");
                _tmpType = CastUtils.IL2Typeof("ExtendedTextMeshProUGUI", "YgomSystem.YGomTMPro", "Assembly-CSharp");
                _bindingTextType = asm.GetClass("BindingTextMeshProUGUI", "YgomSystem.UI").IL2Typeof();
                IL2Class tweenPos = asm.GetClass("TweenPosition", "YgomSystem.UI");
                _tweenPosType = tweenPos.IL2Typeof();
                _tpRtrans = tweenPos.GetField("rtrans");
                _tpFrom = tweenPos.GetField("from");
                _tpTo = tweenPos.GetField("to");
                IL2Class tween = asm.GetClass("Tween", "YgomSystem.UI");
                _tStyle = tween.GetField("style");
                _tDuration = tween.GetField("duration");
                _tTarget = tween.GetField("target");
                _tPlay = tween.GetMethod("Play");
                IL2Class ueBase = ui.GetClass("UnityEvent", "UnityEngine.Events");
                _ueAddListener = ueBase.GetMethod("AddListener");
                _ueRemoveAll = ueBase.GetMethod("RemoveAllListeners");
                _ready = true;
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] drawer init EX: " + ex); }
        }

        // Open the drawer for a node id (no move). Rebuilds content each time.
        public static void Open(int nodeId)
        {
            if (!_ready) return;
            try
            {
                IntPtr window = RoguelikeMapScreen.WindowRoot();
                if (window == IntPtr.Zero) return;
                EnsureBuilt(window);
                _openNodeId = nodeId;
                Populate(nodeId);
                Show();
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] drawer Open EX: " + ex); }
        }

        public static void Close()
        {
            if (!_visible || _scrim == IntPtr.Zero) return;
            _visible = false;
            GameObject.SetActive(_scrim, false);
        }

        static void Show()
        {
            _visible = true;
            GameObject.SetActive(_scrim, true);
            Transform.SetAsLastSibling(GameObject.GetTransform(_scrim));
            SlideIn();
        }

        // ----- build (once) -----
        static void EnsureBuilt(IntPtr window)
        {
            if (_scrim != IntPtr.Zero) { Transform.SetParent(GameObject.GetTransform(_scrim), GameObject.GetTransform(window)); return; }

            _scrim = GameObject.New();
            UnityObject.SetName(_scrim, ScrimName);
            GameObject.AddComponent(_scrim, _rectType);
            IntPtr scrimImg = GameObject.AddComponent(_scrim, _imageType);
            Transform.SetParent(GameObject.GetTransform(_scrim), GameObject.GetTransform(window));
            Stretch(GameObject.GetTransform(_scrim));
            SetColor(scrimImg, new RoguelikeMapScreen.Col { r = 0, g = 0, b = 0, a = 0.55f });
            WireClick(_scrim, _onScrim);

            _panel = GameObject.New();
            UnityObject.SetName(_panel, PanelName);
            GameObject.AddComponent(_panel, _rectType);
            IntPtr panelImg = GameObject.AddComponent(_panel, _imageType);
            Transform.SetParent(GameObject.GetTransform(_panel), GameObject.GetTransform(_scrim));
            IntPtr frame = RoguelikeMapScreen.FindIcon("GUI_CommonSquareBracket");
            if (frame != IntPtr.Zero) { _imageSprite.GetSetMethod().Invoke(panelImg, new IntPtr[] { frame }); SetColor(panelImg, new RoguelikeMapScreen.Col { r = 1, g = 1, b = 1, a = 1 }); }
            else SetColor(panelImg, new RoguelikeMapScreen.Col { r = 0.08f, g = 0.09f, b = 0.13f, a = 0.97f });
            // Right edge, full height, fixed width.
            IntPtr pt = GameObject.GetTransform(_panel);
            SetVec(pt, _anchorMin, new AssetHelper.Vector2(1, 0));
            SetVec(pt, _anchorMax, new AssetHelper.Vector2(1, 1));
            SetVec(pt, _pivot, new AssetHelper.Vector2(0, 0.5f));
            SetVec(pt, _sizeDelta, new AssetHelper.Vector2(Width, 0));

            // Action button cloned from the footer's deck-search button (has a TextTMP label).
            IntPtr root = RoguelikeMapScreen.Root();
            IntPtr tmpl = root == IntPtr.Zero ? IntPtr.Zero : GameObject.FindGameObjectByPath(root,
                "DeckSelectUI(Clone).Root.Window.MainArea.FooterArea.FooterGroup.BaseAll.BaseRight.ButtonOpenDeckSearch");
            if (tmpl != IntPtr.Zero)
            {
                _btn = UnityObject.Instantiate(tmpl, _panel);
                UnityObject.SetName(_btn, "RgDrawerAction");
                GameObject.SetActive(_btn, true);
                IntPtr bt = GameObject.GetTransform(_btn);
                SetVec(bt, _anchorMin, new AssetHelper.Vector2(0.5f, 0));
                SetVec(bt, _anchorMax, new AssetHelper.Vector2(0.5f, 0));
                SetVec(bt, _pivot, new AssetHelper.Vector2(0.5f, 0));
                Vector3 bp = new Vector3(0, 40, 0);
                _anchoredPos3D.GetSetMethod().Invoke(bt, new IntPtr[] { new IntPtr(&bp) });
                WireClick(_btn, _onAction);
            }
        }

        // ----- populate per open -----
        static void Populate(int nodeId)
        {
            RoguelikeApi.MapNode node = FindNode(nodeId);
            if (node == null) return;
            bool reachable = IsReachable(nodeId);

            ClearContent();
            float y = -40f;
            y = AddHeader(node.Type, y);
            if (!string.IsNullOrEmpty(node.IconImage)) y = AddArt(node.IconImage, y);
            if (!string.IsNullOrEmpty(node.Name)) y = AddLine(node.Name, y, 26);
            if (IsCombat(node.Type))
            {
                if (node.EnemyLp >= 0) y = AddLine(RoguelikeLabels.Get("node.stat.enemyLp", "LP do inimigo: {0}", node.EnemyLp), y, 22);
                if (node.Reward >= 0) y = AddLine(RoguelikeLabels.Get("node.stat.reward", "Recompensa: {0}", node.Reward), y, 22);
            }
            y = AddModifierTags(node.Modifiers, y);

            // Action button: shown only for reachable, non-current nodes.
            if (_btn != IntPtr.Zero)
            {
                bool show = reachable && nodeId != RoguelikeApi.Position();
                GameObject.SetActive(_btn, show);
                if (show)
                {
                    string label = IsCombat(node.Type)
                        ? RoguelikeLabels.Get("node.action.duel", "Duelar")
                        : RoguelikeLabels.Get("node.action.move", "Avançar");
                    SetButtonLabel(_btn, label);
                }
            }
        }

        static float AddHeader(string type, float y)
        {
            RoguelikeMapScreen.Col col = RoguelikeMapScreen.TypeColor(type);
            string name = RoguelikeLabels.Get("node.type." + type, type);
            IntPtr glyph = RoguelikeMapScreen.FindIcon(RoguelikeMapScreen.IconNameFor(type));
            IntPtr row = NewChild("Header", 64);
            IntPtr rt = GameObject.GetTransform(row);
            Place(rt, 0, y, new AssetHelper.Vector2(Width - 40, 64));
            if (glyph != IntPtr.Zero)
            {
                IntPtr ico = GameObject.New(); UnityObject.SetName(ico, "Glyph");
                GameObject.AddComponent(ico, _rectType);
                IntPtr img = GameObject.AddComponent(ico, _imageType);
                Transform.SetParent(GameObject.GetTransform(ico), rt);
                Place(GameObject.GetTransform(ico), -(Width - 40) / 2 + 36, 0, new AssetHelper.Vector2(56, 56));
                _imageSprite.GetSetMethod().Invoke(img, new IntPtr[] { glyph });
                SetColor(img, col);
            }
            SetLabelOn(row, name, 30, col);
            return y - 76;
        }

        static float AddArt(string iconImage, float y)
        {
            IntPtr container = NewChild("Art", 220);
            Place(GameObject.GetTransform(container), 0, y - 110, new AssetHelper.Vector2(220, 220));
            if (iconImage.StartsWith("profile_"))
            {
                IntPtr sp = RoguelikeMapScreen.FindIcon("ProfileIcon" + iconImage.Substring(8) + "_L");
                if (sp != IntPtr.Zero) { IntPtr img = GameObject.AddComponent(container, _imageType); _imageSprite.GetSetMethod().Invoke(img, new IntPtr[] { sp }); }
            }
            else
            {
                IntPtr tex = RoguelikeMapScreen.ResolveArtTexture(iconImage);
                if (tex != IntPtr.Zero) { IntPtr ri = GameObject.AddComponent(container, _rawImageType); _rawTexture.GetSetMethod().Invoke(ri, new IntPtr[] { tex }); }
            }
            return y - 244;
        }

        static float AddModifierTags(Dictionary<string, object> mods, float y)
        {
            if (mods == null) return y;
            foreach (string side in new[] { "enemy", "player" })
            {
                Dictionary<string, object> s = mods.ContainsKey(side) ? mods[side] as Dictionary<string, object> : null;
                if (s == null) continue;
                foreach (string key in new[] { "extraLp", "extraHand", "monsters", "spellTraps", "hand" })
                {
                    if (!s.ContainsKey(key)) continue;
                    int v; try { v = Convert.ToInt32(s[key]); } catch { continue; }
                    if (v == 0) continue;
                    string txt = RoguelikeLabels.Get("node.mod." + side + "." + key, side + " " + key + " {0}", v);
                    y = AddLine(txt, y, 20);
                }
            }
            return y;
        }

        static float AddLine(string text, float y, int size)
        {
            IntPtr row = NewChild("Line", size + 10);
            Place(GameObject.GetTransform(row), 0, y, new AssetHelper.Vector2(Width - 48, size + 10));
            SetLabelOn(row, text, size, new RoguelikeMapScreen.Col { r = 0.92f, g = 0.92f, b = 0.95f, a = 1 });
            return y - (size + 16);
        }

        // ----- helpers -----
        static IntPtr NewChild(string name, float h)
        {
            IntPtr go = GameObject.New();
            UnityObject.SetName(go, "RgC_" + name);
            GameObject.AddComponent(go, _rectType);
            Transform.SetParent(GameObject.GetTransform(go), GameObject.GetTransform(_panel));
            return go;
        }

        static void SetLabelOn(IntPtr parent, string text, int size, RoguelikeMapScreen.Col col)
        {
            IntPtr lbl = GameObject.New(); UnityObject.SetName(lbl, "Label");
            GameObject.AddComponent(lbl, _rectType);
            IntPtr tmp = GameObject.AddComponent(lbl, _tmpType);
            Transform.SetParent(GameObject.GetTransform(lbl), GameObject.GetTransform(parent));
            Stretch(GameObject.GetTransform(lbl));
            TMPro.TMP_Text.SetText(tmp, text);
            // size/color best-effort via Graphic color; TMP font size left at prefab default.
            IntPtr g = GameObject.GetComponent(lbl, _graphicTypePtr);
            if (g != IntPtr.Zero) SetColor(g, col);
        }

        static void SetButtonLabel(IntPtr btn, string text)
        {
            IntPtr t = GameObject.FindGameObjectByPath(btn, "TextTMP");
            if (t == IntPtr.Zero) return;
            IntPtr binding = GameObject.GetComponent(t, _bindingTextType);
            if (binding != IntPtr.Zero) YgomSystem.UI.BindingTextMeshProUGUI.SetTextId(binding, text);
        }

        static void ClearContent()
        {
            IntPtr pt = GameObject.GetTransform(_panel);
            int n = Transform.GetChildCount(pt);
            for (int i = n - 1; i >= 0; i--)
            {
                IntPtr child = Transform.GetChild(pt, i);
                if (child == IntPtr.Zero) continue;
                IntPtr cgo = Component.GetGameObject(child);
                string nm = UnityObject.GetName(cgo);
                if (nm == "RgDrawerAction") continue; // keep the persistent button
                if (nm.StartsWith("RgC_")) UnityObject.Destroy(cgo);
            }
        }

        static void SlideIn()
        {
            IntPtr rt = GameObject.GetComponent(_panel, _rectType);
            if (rt == IntPtr.Zero) return;
            Vector3 to = new Vector3(0, 0, 0);
            if (_tweenPosType == IntPtr.Zero || _tPlay == null)
            {
                _anchoredPos3D.GetSetMethod().Invoke(rt, new IntPtr[] { new IntPtr(&to) });
                return;
            }
            Vector3 from = new Vector3(Width, 0, 0);
            IntPtr tw = GameObject.GetComponent(_panel, _tweenPosType);
            if (tw == IntPtr.Zero) tw = GameObject.AddComponent(_panel, _tweenPosType);
            if (tw == IntPtr.Zero) { _anchoredPos3D.GetSetMethod().Invoke(rt, new IntPtr[] { new IntPtr(&to) }); return; }
            if (_tpRtrans != null) _tpRtrans.SetValue(tw, rt);
            if (_tpFrom != null) _tpFrom.SetValue(tw, new IntPtr(&from));
            if (_tpTo != null) _tpTo.SetValue(tw, new IntPtr(&to));
            float dur = 0.25f; if (_tDuration != null) _tDuration.SetValue(tw, new IntPtr(&dur));
            int style = 0; if (_tStyle != null) _tStyle.SetValue(tw, new IntPtr(&style));
            _tPlay.Invoke(tw);
        }

        static void OnScrimClick() { try { Close(); } catch (Exception ex) { Console.WriteLine("[Roguelike] scrim EX: " + ex); } }

        static void OnActionClick()
        {
            try { int id = _openNodeId; Close(); if (id >= 0) RoguelikeApi.Move(id); }
            catch (Exception ex) { Console.WriteLine("[Roguelike] drawer action EX: " + ex); }
        }

        static RoguelikeApi.MapNode FindNode(int id)
        {
            foreach (RoguelikeApi.MapNode n in RoguelikeApi.GetMapNodes()) if (n.Id == id) return n;
            return null;
        }

        static bool IsReachable(int id)
        {
            int pos = RoguelikeApi.Position();
            if (id == pos) return false;
            foreach (RoguelikeApi.MapNode n in RoguelikeApi.GetMapNodes())
                if (n.Id == pos) return n.Next.Contains(id);
            // entry (pos == -1): row-0 nodes are reachable
            if (pos < 0) { foreach (RoguelikeApi.MapNode n in RoguelikeApi.GetMapNodes()) if (n.Id == id && n.Row == 0) return true; }
            return false;
        }

        static bool IsCombat(string t) { return t == "duel" || t == "elite" || t == "boss"; }

        static void WireClick(IntPtr go, Action action)
        {
            IntPtr sel = GameObject.GetComponent(go, _selButtonType);
            if (sel == IntPtr.Zero)
            {
                // scrim/plain images: add a SelectionButton so onClick works
                sel = GameObject.AddComponent(go, _selButtonType);
                if (sel == IntPtr.Zero) return;
            }
            IL2Object onClickObj = _selOnClick.GetValue(sel);
            if (onClickObj == null) return;
            if (_ueRemoveAll != null) _ueRemoveAll.Invoke(onClickObj.ptr);
            IntPtr cb = UnityEngine.Events._UnityAction.CreateUnityAction(action);
            _ueAddListener.Invoke(onClickObj.ptr, new IntPtr[] { cb });
        }

        static void Place(IntPtr t, float x, float y, AssetHelper.Vector2 size)
        {
            AssetHelper.Vector2 c = new AssetHelper.Vector2(0.5f, 1f);
            _anchorMin.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&c) });
            _anchorMax.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&c) });
            AssetHelper.Vector2 piv = new AssetHelper.Vector2(0.5f, 1f);
            _pivot.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&piv) });
            _sizeDelta.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&size) });
            Vector3 p = new Vector3(x, y, 0);
            _anchoredPos3D.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&p) });
        }

        static void Stretch(IntPtr t)
        {
            AssetHelper.Vector2 min = new AssetHelper.Vector2(0, 0), max = new AssetHelper.Vector2(1, 1), zero = new AssetHelper.Vector2(0, 0);
            _anchorMin.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&min) });
            _anchorMax.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&max) });
            _sizeDelta.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&zero) });
            Vector3 p = new Vector3(0, 0, 0);
            _anchoredPos3D.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&p) });
        }

        static void SetVec(IntPtr t, IL2Property prop, AssetHelper.Vector2 v)
        {
            prop.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&v) });
        }

        static void SetColor(IntPtr graphic, RoguelikeMapScreen.Col c)
        {
            _graphicColor.GetSetMethod().Invoke(graphic, new IntPtr[] { new IntPtr(&c) });
        }
    }
}
```

- [ ] **Step 2: Add the file to the project**

In `YgoMasterClient.csproj`, next to the other `Roguelike\*.cs` `<Compile Include=...>` entries, add:

```xml
    <Compile Include="YgoMasterClient\Roguelike\RoguelikeNodeDrawer.cs" />
```

(Match the exact include path style used by the neighbouring roguelike entries — e.g. `RoguelikeLabels.cs` — in this csproj.)

- [ ] **Step 3: Build client**

Run the client build command. Expected: copied OK. Fix any IL2 lookup name mismatches the compiler/runtime surfaces (e.g. `UnityEvent` class location) before moving on.

---

## Task 6: Client — wire all nodes to the drawer

**Files:**
- Modify: `YgoMasterClient/Roguelike/RoguelikeMapScreen.cs`

- [ ] **Step 1: Raise the slot cap to 64**

Change `const int MapSlots = 16;` to `const int MapSlots = 64;` and replace the `_slotActions` initializer with all 64 explicit captureless lambdas:

```csharp
        static readonly Action[] _slotActions =
        {
            () => OnSlot(0),  () => OnSlot(1),  () => OnSlot(2),  () => OnSlot(3),
            () => OnSlot(4),  () => OnSlot(5),  () => OnSlot(6),  () => OnSlot(7),
            () => OnSlot(8),  () => OnSlot(9),  () => OnSlot(10), () => OnSlot(11),
            () => OnSlot(12), () => OnSlot(13), () => OnSlot(14), () => OnSlot(15),
            () => OnSlot(16), () => OnSlot(17), () => OnSlot(18), () => OnSlot(19),
            () => OnSlot(20), () => OnSlot(21), () => OnSlot(22), () => OnSlot(23),
            () => OnSlot(24), () => OnSlot(25), () => OnSlot(26), () => OnSlot(27),
            () => OnSlot(28), () => OnSlot(29), () => OnSlot(30), () => OnSlot(31),
            () => OnSlot(32), () => OnSlot(33), () => OnSlot(34), () => OnSlot(35),
            () => OnSlot(36), () => OnSlot(37), () => OnSlot(38), () => OnSlot(39),
            () => OnSlot(40), () => OnSlot(41), () => OnSlot(42), () => OnSlot(43),
            () => OnSlot(44), () => OnSlot(45), () => OnSlot(46), () => OnSlot(47),
            () => OnSlot(48), () => OnSlot(49), () => OnSlot(50), () => OnSlot(51),
            () => OnSlot(52), () => OnSlot(53), () => OnSlot(54), () => OnSlot(55),
            () => OnSlot(56), () => OnSlot(57), () => OnSlot(58), () => OnSlot(59),
            () => OnSlot(60), () => OnSlot(61), () => OnSlot(62), () => OnSlot(63),
        };
```

- [ ] **Step 2: `OnSlot` opens the drawer instead of moving**

Replace `OnSlot`:

```csharp
        static void OnSlot(int slot)
        {
            if (slot < 0 || slot >= MapSlots) return;
            int id = _slotNodeId[slot];
            if (id >= 0) RoguelikeNodeDrawer.Open(id);
        }
```

- [ ] **Step 3: Wire every node (not only reachable)**

In `RenderMap`, replace the reachable-gated wiring block:

```csharp
                bool isCurrent = n.Id == pos;
                bool isVisited = visited.Contains(n.Id);
                bool isOpen = reachable.Contains(n.Id) && slot < MapSlots;
                if (isCurrent) { curX = x; curY = y; }
                if (isOpen)
                {
                    _slotNodeId[slot] = n.Id;
                    WireNodeClick(node, _slotActions[slot]);
                    slot++;
                }
```

with (every node gets a slot; reachability is now decided in the drawer):

```csharp
                bool isCurrent = n.Id == pos;
                bool isVisited = visited.Contains(n.Id);
                bool isOpen = reachable.Contains(n.Id);
                if (isCurrent) { curX = x; curY = y; }
                if (slot < MapSlots)
                {
                    _slotNodeId[slot] = n.Id;
                    WireNodeClick(node, _slotActions[slot]);
                    slot++;
                }
```

(`StyleNode` still receives `isOpen` for the existing pulse/disable visuals — unchanged. Note: `DisableButton` is called for non-open nodes inside `StyleNode`; it must NOT remove the click handler. Verify in Step 4.)

- [ ] **Step 4: Keep non-open nodes clickable**

Read `DisableButton` (RoguelikeMapScreen, ~line 759). It turns off the SelectionButton to stop hover recolor tweens. If it disables the component such that `onClick` no longer fires, change it to disable only the recolor/interactable visuals while leaving click working — the simplest reliable approach is to NOT disable the button at all now that all nodes are clickable. Replace the `else DisableButton(node);` line in `StyleNode` with nothing (delete it), and instead rely on the existing color/brightness to convey locked state. If hover-recolor on locked nodes looks wrong in-game, revisit by disabling only the color tween component rather than the button.

- [ ] **Step 5: Close the drawer before a re-render**

At the very start of `RenderMap` (first line of the method body), add:

```csharp
            RoguelikeNodeDrawer.Close();
```

- [ ] **Step 6: Build client**

Run the client build command. Expected: copied OK.

- [ ] **Step 7: In-game verification**

Start server + client, new run, open the map:
1. Click a reachable combat node → drawer slides in from the right with type header, art, name, "LP do inimigo", "Recompensa", any modifier tags, and a "Duelar" button.
2. "Duelar" → drawer closes, duel starts (existing flow).
3. Click a locked node (ahead) and a visited node (behind) → drawer opens read-only (no button). Click the current node → no button.
4. Click the scrim (outside the panel) → drawer slides/closes, no move.
5. Non-combat node (event/shop/reward) → drawer shows type + name (+ mods); "Avançar" advances.
6. Move across a few nodes → no stale drawer floats over the rebuilt map.

- [ ] **Step 8: Commit (only if the user approves)**

```bash
git add YgoMasterServer/Roguelike/RoguelikeMap.cs YgoMasterServer/Roguelike/GameServer.Roguelike.cs YgoMasterClient/Roguelike/RoguelikeApi.cs YgoMasterClient/Roguelike/RoguelikeMapScreen.cs YgoMasterClient/Roguelike/RoguelikeNodeDrawer.cs YgoMasterClient.csproj Docs/roguelike/node-detail-drawer-design.md Docs/roguelike/node-detail-drawer-plan.md
git commit -m "feat(roguelike): node detail drawer with action button"
```

(The live `Labels.json` lives under the install dir, not the repo.)

---

## Self-Review

**Spec coverage:**
- Click any node → drawer; action button reachable-only; scrim closes → Task 5 (`Open`/`Populate`/`OnScrimClick`/`OnActionClick`), Task 6 (`OnSlot`, wire all). ✓
- Dispatch for all nodes via expanded captureless slot table (cap 64) → Task 6 Steps 1-3. ✓
- Drawer UI (right panel, GUI_CommonSquareBracket + fallback, art, TweenPosition slide, cloned button) → Task 5 Step 1. ✓
- Server rich fields (enemyLp/reward/modifiers summary, Merge-based, declared-only) → Tasks 1-2. ✓
- Client parse + Labels → Tasks 3-4. ✓
- Edge cases: frame fallback (EnsureBuilt), missing art (Populate guards), non-combat no stats (IsCombat gate), current node no button (Populate), close-before-render (Task 6 Step 5), >64 nodes (slot guard). ✓

**Placeholder scan:** No TBD/TODO; full code in every code step. Two explicit "verify in-engine and adjust" notes (Task 2 Step 3 nullable check; Task 6 Step 4 DisableButton) are conditional fixes with the concrete change spelled out, not placeholders.

**Type/name consistency:** `MapNode.EnemyLp/Reward/Modifiers` (server `int?`/dict; client `int=-1`/dict) and JSON keys `enemyLp`/`reward`/`modifiers` match across Tasks 1-3. `SummarizeModifiers` signature matches its caller. `RoguelikeNodeDrawer.Open/Close` match the `OnSlot` and `RenderMap` callers in Task 6. Exposed `RoguelikeMapScreen.Col/FindIcon/ResolveArtTexture/TypeColor/IconNameFor/Root/WindowRoot` (Task 4) match the drawer's uses (Task 5). ✓
