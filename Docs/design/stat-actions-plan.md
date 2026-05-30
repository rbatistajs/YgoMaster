# Stat Actions (lp/gold) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Adicionar duas actions (`lp`, `gold`) ao `RoguelikeActionEngine`, com diff client-side e animação de floating number + tween HUD. Pré-requisito pra non-combat nodes.

**Architecture:** Server aplica delta no pump loop sem ack (sem `pendingAction`); cliente diff snapshot do run state e anima. `Currency` field server-side mantém; wire renomeia `currency` → `gold` com back-compat de leitura.

**Tech Stack:** C# .NET 4.8 (server + client BepInEx/IL2CPP), MiniJSON, IL2CPP reflection (`IL2Class`/`IL2Method`), TextMeshPro via reflection wrappers (`TMPro.TMP_Text`), Unity `Vector3` / `Transform` via wrappers.

**Tests:** Smokes manuais via `rgencounter <id>` no console do client (codebase não tem framework de teste automatizado server-side). Cada task termina com smoke verificado in-game antes do commit.

---

## File Structure

**Modify:**
- `YgoMasterServer/Roguelike/RoguelikeRun.cs` — rename JSON key `currency` → `gold` (back-compat read), add `ApplyLpDelta` / `ApplyGoldDelta` helpers.
- `YgoMasterServer/Roguelike/Actions/RoguelikeActionEngine.cs` — add `lp`/`gold` cases no `Step` switch.
- `YgoMasterServer/Roguelike/RoguelikeEncounters.cs` — add `lp`/`gold` cases no `ValidateActionNode` switch.
- `YgoMasterClient/Roguelike/RoguelikeApi.cs` — add `Gold()` getter.
- `YgoMasterClient/Roguelike/RoguelikeMapScreen.cs` — clone DeckNum slot → GoldNum; diff detection; animation queues.
- `DataLE/Roguelike/Encounters.json` — add smokes.
- `Docs/config/roguelike/encounters.md` — document stat actions.
- `Docs/config/roguelike/settings.md` — note `currency` → `gold` rename.

**Create:**
- `YgoMasterClient/Roguelike/RoguelikeStatAnim.cs` — `FloatingDelta` + `HudCounter` classes + tick helpers.

---

## Task 1: Server — run state currency rename + delta helpers

**Files:**
- Modify: `YgoMasterServer/Roguelike/RoguelikeRun.cs`

- [ ] **Step 1: Rename wire key currency → gold in ToDictionary**

Edit `RoguelikeRun.ToDictionary()` (around line 47):

```csharp
// Before:
{ "currency", Currency },

// After:
{ "gold", Currency },
```

- [ ] **Step 2: Back-compat read in FromDictionary**

Edit `RoguelikeRun.FromDictionary()` (around line 78):

```csharp
// Before:
Currency   = Utils.GetValue<int>(d, "currency", 0),

// After:
Currency   = Utils.GetValue<int>(d, "gold", Utils.GetValue<int>(d, "currency", 0)),
```

- [ ] **Step 3: Add ApplyLpDelta helper**

Add this method to `RoguelikeRun` (after `AddCidToDeck`, before `ParsePity`):

```csharp
// Apply LP delta from a stat action. Exactly one of absDelta / pctDelta must
// be non-null (validated at parse time). pctDelta is a fraction of MaxLp
// (e.g., -0.30 = -30% of max). Returns (newLp, killed) where killed=true if
// the new value <= 0 (caller marks run dead).
public (int, bool) ApplyLpDelta(int? absDelta, double? pctDelta)
{
    int delta = absDelta ?? (int)Math.Round(MaxLp * (pctDelta ?? 0.0));
    int newLp = Lp + delta;
    if (newLp > MaxLp) newLp = MaxLp;
    if (newLp < 0) newLp = 0;
    Lp = newLp;
    return (newLp, newLp <= 0);
}
```

- [ ] **Step 4: Add ApplyGoldDelta helper**

Add this method right after `ApplyLpDelta`:

```csharp
// Apply gold delta from a stat action. pctDelta is fraction of CURRENT gold
// (not max — gold has no cap). Clamps at 0 (no negative gold), no upper cap.
public int ApplyGoldDelta(int? absDelta, double? pctDelta)
{
    int delta = absDelta ?? (int)Math.Round(Currency * (pctDelta ?? 0.0));
    int newGold = Currency + delta;
    if (newGold < 0) newGold = 0;
    Currency = newGold;
    return newGold;
}
```

- [ ] **Step 5: Build server**

Run:
```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Bin\MSBuild.exe" YgoMasterServer/YgoMaster.csproj /p:Configuration=Debug /p:Platform=x64 /nologo /v:minimal
```
Expected: builds cleanly, copies exe to install dir.

- [ ] **Step 6: Smoke — verify wire key**

Start an active run in-game; open the run JSON at `YgoMaster/Data/Players/<playerId>/roguelike.json` and confirm `"gold": 0` is present (not `"currency"`). Old saves still load — delete the file if needed to force fresh state.

- [ ] **Step 7: Commit**

```bash
git add YgoMasterServer/Roguelike/RoguelikeRun.cs
git commit -m "feat(roguelike): currency→gold wire rename + LP/gold delta helpers"
```

---

## Task 2: Server — engine handles lp/gold actions

**Files:**
- Modify: `YgoMasterServer/Roguelike/Actions/RoguelikeActionEngine.cs`

- [ ] **Step 1: Add lp/gold cases to Step switch**

Edit `Step()` (around line 117) — replace the `else` branch (currently `// v1: unknown leaf -> end`):

```csharp
// Before:
else
{
    // v2: apply a state-mutating leaf here, then SetPending(next) or null.
    SetPending(run, null); // v1: unknown leaf -> end
}

// After:
else if (type == "lp")
{
    ApplyStatLp(run, node);
    SetPending(run, Utils.GetValue<Dictionary<string, object>>(node, "next"));
}
else if (type == "gold")
{
    ApplyStatGold(run, node);
    SetPending(run, Utils.GetValue<Dictionary<string, object>>(node, "next"));
}
else
{
    SetPending(run, null); // unknown leaf -> end
}
```

- [ ] **Step 2: Add ApplyStatLp method**

Add this method to `RoguelikeActionEngine` (after `Step`, before `RollOpenPack`):

```csharp
// Read delta fields from an lp action node and apply via run helper.
// XOR contract is enforced at load time (RoguelikeEncounters.ValidateActionNode).
// If LP hits 0, sets run.Active = false (game over — same path as combat loss).
static void ApplyStatLp(RoguelikeRun run, Dictionary<string, object> node)
{
    int? abs = null; double? pct = null;
    object v;
    if (node.TryGetValue("delta", out v)) { try { abs = Convert.ToInt32(v); } catch { } }
    if (node.TryGetValue("delta_percent", out v)) { try { pct = Convert.ToDouble(v); } catch { } }
    var (newLp, killed) = run.ApplyLpDelta(abs, pct);
    Console.WriteLine("[Roguelike] lp action: delta=" + (abs.HasValue ? abs.Value.ToString() : "pct " + pct) +
        " -> lp=" + newLp + (killed ? " (LETHAL)" : ""));
    if (killed) run.Active = false;
}
```

- [ ] **Step 3: Add ApplyStatGold method**

Add this method right after `ApplyStatLp`:

```csharp
// Read delta fields from a gold action node and apply via run helper.
static void ApplyStatGold(RoguelikeRun run, Dictionary<string, object> node)
{
    int? abs = null; double? pct = null;
    object v;
    if (node.TryGetValue("delta", out v)) { try { abs = Convert.ToInt32(v); } catch { } }
    if (node.TryGetValue("delta_percent", out v)) { try { pct = Convert.ToDouble(v); } catch { } }
    int newGold = run.ApplyGoldDelta(abs, pct);
    Console.WriteLine("[Roguelike] gold action: delta=" + (abs.HasValue ? abs.Value.ToString() : "pct " + pct) +
        " -> gold=" + newGold);
}
```

- [ ] **Step 4: Build server**

Run:
```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Bin\MSBuild.exe" YgoMasterServer/YgoMaster.csproj /p:Configuration=Debug /p:Platform=x64 /nologo /v:minimal
```
Expected: builds cleanly.

- [ ] **Step 5: Commit**

```bash
git add YgoMasterServer/Roguelike/Actions/RoguelikeActionEngine.cs
git commit -m "feat(roguelike): engine handles lp/gold actions (no ack)"
```

---

## Task 3: Server — Encounters validation for lp/gold

**Files:**
- Modify: `YgoMasterServer/Roguelike/RoguelikeEncounters.cs`

- [ ] **Step 1: Add lp/gold cases to ValidateActionNode**

Edit `ValidateActionNode()` (around line 194) — add two cases inside the switch, before the `default:`:

```csharp
case "lp":
case "gold":
{
    bool hasAbs = node.ContainsKey("delta");
    bool hasPct = node.ContainsKey("delta_percent");
    if (hasAbs == hasPct) throw new Exception(type + ": exactly one of 'delta'/'delta_percent' required");
    if (hasAbs)
    {
        try { Convert.ToInt32(node["delta"]); } catch { throw new Exception(type + ": 'delta' must be int"); }
    }
    else
    {
        double p;
        try { p = Convert.ToDouble(node["delta_percent"]); }
        catch { throw new Exception(type + ": 'delta_percent' must be number"); }
        if (p < -1.0 || p > 1.0)
            Console.WriteLine("[Roguelike] " + type + ": delta_percent " + p + " out of [-1,1]; clamped at apply time");
    }
    Dictionary<string, object> nxt = Utils.GetValue<Dictionary<string, object>>(node, "next");
    if (nxt != null) ValidateActionNode(nxt);
    return;
}
```

- [ ] **Step 2: Build server**

Run:
```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Bin\MSBuild.exe" YgoMasterServer/YgoMaster.csproj /p:Configuration=Debug /p:Platform=x64 /nologo /v:minimal
```
Expected: builds cleanly.

- [ ] **Step 3: Smoke — invalid lp action rejected**

Add this entry to `DataLE/Roguelike/Encounters.json` under `boss` (temporary, will be removed):

```json
{ "id": "tmp_bad_lp_both", "name": "Tmp Bad LP",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": { "type": "lp", "delta": 100, "delta_percent": 0.10 } }
```

Restart server. Expected: server log shows `[Roguelike] encounter 'tmp_bad_lp_both' action invalid: lp: exactly one of 'delta'/'delta_percent' required` and the encounter is dropped (not loaded). Revert the change before committing.

- [ ] **Step 4: Commit**

```bash
git add YgoMasterServer/Roguelike/RoguelikeEncounters.cs
git commit -m "feat(roguelike): validate lp/gold action XOR + range"
```

---

## Task 4: Server — smokes in Encounters.json

**Files:**
- Modify: `DataLE/Roguelike/Encounters.json` (install path: `D:\SteamLibrary\steamapps\common\Yu-Gi-Oh!  Master Duel\YgoMasterLE - Goat\DataLE\Roguelike\Encounters.json`)

- [ ] **Step 1: Add stat smokes**

Append these entries inside the `"boss"` array (alongside existing `smoke_openpack_*`):

```json
{ "id": "smoke_lp_heal_absolute", "name": "Smoke LP +500",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": { "type": "lp", "delta": 500 } },

{ "id": "smoke_lp_damage_percent", "name": "Smoke LP -30%",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": { "type": "lp", "delta_percent": -0.30 } },

{ "id": "smoke_gold_gain", "name": "Smoke Gold +200",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": { "type": "gold", "delta": 200 } },

{ "id": "smoke_lp_lethal", "name": "Smoke LP lethal",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": { "type": "lp", "delta": -99999 } },

{ "id": "smoke_chain_msg_lp_msg", "name": "Smoke msg->lp->msg",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": {
    "type": "message", "title": "Antes", "message": "Vai perder 500 LP.",
    "next": { "type": "lp", "delta": -500,
      "next": { "type": "message", "title": "Depois", "message": "Perdeu." }
    }
  }},

{ "id": "smoke_multi_stat_burst", "name": "Smoke lp+gold mesmo tick",
  "act": {"min":99}, "deck": "Beatdown.json",
  "action": {
    "type": "lp", "delta": -300,
    "next": { "type": "gold", "delta": 200,
      "next": { "type": "lp", "delta": -200 }
    }
  }}
```

- [ ] **Step 2: Smoke — encounters parse**

Restart server. Expected: log shows no errors for these new ids; eligible list for `boss` includes them when filtered to act 99.

- [ ] **Step 3: Smoke — apply via console**

In an active run, open the client console (backtick) and run:
```
rgencounter smoke_lp_heal_absolute
```
Expected: server log shows `[Roguelike] lp action: delta=500 -> lp=...`. Inspect `roguelike.json` to confirm `lp` increased by 500 (capped at maxLp). HUD still snaps (no animation yet).

Repeat for `smoke_gold_gain` — confirm `gold` field grew by 200.

Repeat for `smoke_chain_msg_lp_msg` — confirm: 1º message modal opens, OK, lp applied (HUD snaps from old to new), 2º message modal opens, OK, action done.

Repeat for `smoke_lp_lethal` — confirm `active: false` in roguelike.json (game over flagged).

- [ ] **Step 4: Commit**

```bash
git add "D:/SteamLibrary/steamapps/common/Yu-Gi-Oh!  Master Duel/YgoMasterLE - Goat/DataLE/Roguelike/Encounters.json"
git commit -m "test(roguelike): smokes for lp/gold actions"
```

Note: install-dir is outside the repo working tree; if `DataLE/Roguelike/Encounters.json` lives in the repo (under `YgoMaster/Data/...`), edit there instead. Verify with `git status` which path is tracked.

---

## Task 5: Client — Gold() getter in RoguelikeApi

**Files:**
- Modify: `YgoMasterClient/Roguelike/RoguelikeApi.cs`

- [ ] **Step 1: Add Gold getter**

Edit `RoguelikeApi.cs` — after the existing `MaxLp()` method (around line 378), add:

```csharp
// Current run gold (= Currency server-side, renamed in wire). Used by the
// map HUD label and animation diff snapshot.
public static int Gold() { return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.gold"); }
```

- [ ] **Step 2: Build client**

Run:
```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Bin\MSBuild.exe" YgoMasterClient.csproj /p:Configuration=Debug /p:Platform=x64 /nologo /v:minimal
```
Expected: builds cleanly.

- [ ] **Step 3: Commit**

```bash
git add YgoMasterClient/Roguelike/RoguelikeApi.cs
git commit -m "feat(roguelike): client RoguelikeApi.Gold() getter"
```

---

## Task 6: Client — GoldNum label clone in HUD

**Files:**
- Modify: `YgoMasterClient/Roguelike/RoguelikeMapScreen.cs`

- [ ] **Step 1: Add HeaderGold const + cache field**

Edit the constants block at the top of `RoguelikeMapScreen` (around line 23, after `HeaderLp`):

```csharp
// Cloned right-sibling of DeckNum, used for run gold. Spawned 1x in EnsureGoldLabel().
const string HeaderGold = HeaderGroup + ".GoldNum.TextDeckNumValue";
static IntPtr _goldLabelGo = IntPtr.Zero;
```

- [ ] **Step 2: Add EnsureGoldLabel method**

Add this method near `SetLpText` (around line 453):

```csharp
// Clone the DeckNum subtree once into a sibling GoldNum (positioned to the
// right of LP). Keeps the same TMP child structure so SetTmpText(HeaderGold,...)
// targets the same path pattern as HeaderLp.
static void EnsureGoldLabel(IntPtr go)
{
    if (_goldLabelGo != IntPtr.Zero) return;
    IntPtr src = GameObject.FindGameObjectByPath(go, HeaderGroup + ".DeckNum");
    if (src == IntPtr.Zero) return;
    IntPtr parent = GameObject.GetTransform(
        GameObject.FindGameObjectByPath(go, HeaderGroup));
    if (parent == IntPtr.Zero) return;
    IntPtr clone = UnityObject.Instantiate(src, parent);
    UnityObject.SetName(clone, "GoldNum");
    // Offset to the right of DeckNum by ~140px (tune visually).
    IntPtr ct = GameObject.GetTransform(clone);
    Vector3 p = new Vector3(140f, 0, 0);
    _anchoredPos3D.GetSetMethod().Invoke(ct, new IntPtr[] { new IntPtr(&p) });
    _goldLabelGo = clone;
}
```

- [ ] **Step 3: Wire EnsureGoldLabel into the header setup**

Edit `HideHeaderClutter` (around line 427) — call `EnsureGoldLabel(go)` at the bottom:

```csharp
static void HideHeaderClutter(IntPtr go)
{
    foreach (string name in HeaderClutter) Hide(go, HeaderGroup + "." + name);
    Hide(go, HeaderGroup + ".DeckNum.IconDeckNum");
    IntPtr deckNum = GameObject.FindGameObjectByPath(go, HeaderGroup + ".DeckNum");
    if (deckNum != IntPtr.Zero) GameObject.SetActive(deckNum, true);
    EnsureGoldLabel(go); // NEW
}
```

- [ ] **Step 4: Render gold text in SetLpText**

Edit `SetLpText()` (around line 453) — add a line after the existing LP `SetTmpText`:

```csharp
SetTmpText(HeaderLp, RoguelikeLabels.Get("map.lp", "LP {0} / {1}", RoguelikeApi.Lp(), RoguelikeApi.MaxLp()));
SetTmpText(HeaderGold, RoguelikeLabels.Get("map.gold", "GOLD {0}", RoguelikeApi.Gold())); // NEW
```

- [ ] **Step 5: Build client**

Run:
```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Bin\MSBuild.exe" YgoMasterClient.csproj /p:Configuration=Debug /p:Platform=x64 /nologo /v:minimal
```
Expected: builds cleanly.

- [ ] **Step 6: Smoke — gold label visible**

Restart client, enter run, open map. Expected: "GOLD 0" appears to the right of "LP X / Y" in the header. After running `rgencounter smoke_gold_gain`, the GOLD text updates to "GOLD 200" (snaps, no animation yet).

- [ ] **Step 7: Commit**

```bash
git add YgoMasterClient/Roguelike/RoguelikeMapScreen.cs
git commit -m "feat(roguelike): GoldNum HUD label cloned from DeckNum"
```

---

## Task 7: Client — TMP_Text.SetColor wrapper + RoguelikeStatAnim primitives

**Context:** The project's TMP_Text wrapper (`YgoMasterClient/DeckEditorUtils.cs:14-39`) only has `GetText` / `SetText`. We need `SetColor` for the colored floating numbers (alpha is encoded in the Color's a channel). No `Mathf` wrapper exists — use an inline `Lerp` helper.

**Files:**
- Modify: `YgoMasterClient/DeckEditorUtils.cs` (extend `TMP_Text` static class)
- Create: `YgoMasterClient/Roguelike/RoguelikeStatAnim.cs`

- [ ] **Step 1: Add TMP_Text.SetColor wrapper**

Edit `YgoMasterClient/DeckEditorUtils.cs:14-39`. Find the `TMP_Text` static class and extend it:

```csharp
namespace TMPro
{
    static unsafe class TMP_Text
    {
        static IL2Method methodGetText;
        static IL2Method methodSetText;
        static IL2Method methodSetColor;   // NEW

        static TMP_Text()
        {
            IL2Assembly assembly = Assembler.GetAssembly("Unity.TextMeshPro");
            IL2Class classInfo = assembly.GetClass("TMP_Text", "TMPro");
            methodGetText = classInfo.GetProperty("text").GetGetMethod();
            methodSetText = classInfo.GetProperty("text").GetSetMethod();
            methodSetColor = classInfo.GetProperty("color").GetSetMethod();   // NEW
        }

        public static string GetText(IntPtr thisPtr)
        {
            IL2Object result = methodGetText.Invoke(thisPtr);
            return result != null ? result.GetValueObj<string>() : null;
        }

        public static void SetText(IntPtr thisPtr, string value)
        {
            methodSetText.Invoke(thisPtr, new IntPtr[] { new IL2String(value).ptr });
        }

        // NEW — UnityEngine.Color is a value type; pass via boxed pointer.
        public static void SetColor(IntPtr thisPtr, UnityEngine.Color color)
        {
            methodSetColor.Invoke(thisPtr, new IntPtr[] { new IntPtr(&color) });
        }
    }
}
```

- [ ] **Step 2: Build to confirm wrapper compiles**

Run:
```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Bin\MSBuild.exe" YgoMasterClient.csproj /p:Configuration=Debug /p:Platform=x64 /nologo /v:minimal
```
Expected: builds cleanly. If `UnityEngine.Color` isn't in scope, find the existing `Color` struct in the client codebase (grep `struct Color\|class Color` under `YgoMasterClient/`) and qualify accordingly.

- [ ] **Step 3: Create RoguelikeStatAnim.cs**

Write `YgoMasterClient/Roguelike/RoguelikeStatAnim.cs`:

```csharp
using System;
using System.Collections.Generic;
using IL2CPP;
using UnityEngine;
using TMPro;

namespace YgomGame.Roguelike
{
    // Animation primitives for stat changes (LP / gold). Driven by RoguelikeMapScreen.Update:
    //   1. Diff detects change -> spawn FloatingDelta + create deferred HudCounter
    //   2. FloatingDelta slides + fades (alpha via Color) + shrinks; on T=1 -> activates HudCounter
    //   3. HudCounter lerps HUD TMP text integer from old value to new
    // Multiple in-flight floaters coexist; HudCounter for the same stat is replaced (From=cur, T=0).
    static unsafe class RoguelikeStatAnim
    {
        public const float FloatDuration = 0.7f;
        public const float CounterDuration = 0.5f;
        // Colors only used as RGB; alpha is replaced per-frame for fade.
        public static readonly Color PositiveRGB = new Color(0.290f, 0.871f, 0.502f, 1f); // #4ADE80
        public static readonly Color NegativeRGB = new Color(0.973f, 0.443f, 0.443f, 1f); // #F87171

        public class FloatingDelta
        {
            public IntPtr Go;            // TMP clone parented under the canvas root
            public Vector3 StartPos;     // screen center + per-stat Y offset
            public Vector3 EndPos;       // HUD label anchored pos
            public float T;
            public string TargetStat;    // "lp" | "gold"
            public Color RGB;            // base color (sign-tinted)
            public HudCounter Pending;   // deferred — activated on T=1
        }

        public class HudCounter
        {
            public string LabelPath;
            public int From;
            public int To;
            public float T;
            public bool Active;          // false until the matching FloatingDelta finishes
            public Func<int, string> Format;
        }

        // Linear interp helper — no Mathf wrapper exists in this project.
        public static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        // EaseOutCubic: starts fast, settles slow — matches "ganho/perda" feel.
        public static float EaseOutCubic(float t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            float u = 1f - t;
            return 1f - u * u * u;
        }

        // Tick all floaters: advance T, lerp pos + color alpha + scale; on T>=1 destroy GO + activate Pending.
        public static void TickFloaters(List<FloatingDelta> floaters, float dt,
                                         IntPtr tmpType, IL2Field anchoredPos3D)
        {
            for (int i = floaters.Count - 1; i >= 0; i--)
            {
                FloatingDelta f = floaters[i];
                f.T += dt / FloatDuration;
                bool done = f.T >= 1f;
                float t = done ? 1f : f.T;
                float e = EaseOutCubic(t);
                Vector3 pos = new Vector3(
                    Lerp(f.StartPos.x, f.EndPos.x, e),
                    Lerp(f.StartPos.y, f.EndPos.y, e),
                    Lerp(f.StartPos.z, f.EndPos.z, e));
                IntPtr ct = GameObject.GetTransform(f.Go);
                anchoredPos3D.GetSetMethod().Invoke(ct, new IntPtr[] { new IntPtr(&pos) });
                float scale = Lerp(1.4f, 1.0f, e);
                Transform.SetLocalScale(ct, new Vector3(scale, scale, scale));
                IntPtr tmp = GameObject.GetComponent(f.Go, tmpType);
                if (tmp != IntPtr.Zero)
                {
                    Color c = new Color(f.RGB.r, f.RGB.g, f.RGB.b, 1f - t);
                    TMP_Text.SetColor(tmp, c);
                }
                if (done)
                {
                    UnityObject.Destroy(f.Go);
                    if (f.Pending != null) f.Pending.Active = true;
                    floaters.RemoveAt(i);
                }
            }
        }

        // Tick all HUD counters: advance T (only Active ones), lerp From->To, write TMP text.
        public static void TickCounters(List<HudCounter> counters, float dt,
                                          Action<string, string> setTmpText)
        {
            for (int i = counters.Count - 1; i >= 0; i--)
            {
                HudCounter c = counters[i];
                if (!c.Active) continue;
                c.T += dt / CounterDuration;
                bool done = c.T >= 1f;
                int cur = done ? c.To
                    : (int)Math.Round(Lerp(c.From, c.To, EaseOutCubic(c.T)));
                setTmpText(c.LabelPath, c.Format(cur));
                if (done) counters.RemoveAt(i);
            }
        }
    }
}
```

- [ ] **Step 4: Add RoguelikeStatAnim.cs to csproj**

Edit `YgoMasterClient.csproj` — find the `<ItemGroup>` block listing `Roguelike\*.cs` Compile entries and add:

```xml
<Compile Include="Roguelike\RoguelikeStatAnim.cs" />
```

- [ ] **Step 5: Build client**

Run:
```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Bin\MSBuild.exe" YgoMasterClient.csproj /p:Configuration=Debug /p:Platform=x64 /nologo /v:minimal
```
Expected: builds cleanly.

- [ ] **Step 6: Commit**

```bash
git add YgoMasterClient/DeckEditorUtils.cs YgoMasterClient/Roguelike/RoguelikeStatAnim.cs YgoMasterClient.csproj
git commit -m "feat(roguelike): TMP_Text.SetColor wrapper + RoguelikeStatAnim primitives"
```

---

## Task 8: Client — diff detection + spawn floaters

**Files:**
- Modify: `YgoMasterClient/Roguelike/RoguelikeMapScreen.cs`

- [ ] **Step 1: Add snapshot fields + animation lists + dt source**

Add to the static field block at top of `RoguelikeMapScreen` (near `_goldLabelGo`):

```csharp
// Animation state — drives the floating-delta + HUD counter pipeline.
// _snap.Valid==false means "first read since this screen opened" — baseline only, no anim.
struct HudSnap { public bool Valid; public int Lp; public int Gold; }
static HudSnap _snap;
static readonly List<RoguelikeStatAnim.FloatingDelta> _floaters = new List<RoguelikeStatAnim.FloatingDelta>();
static readonly List<RoguelikeStatAnim.HudCounter>   _counters = new List<RoguelikeStatAnim.HudCounter>();

// Wall-clock dt source. The IL2CPP wrappers in this project don't expose
// UnityEngine.Time.deltaTime, so we measure between Update() calls ourselves.
static System.Diagnostics.Stopwatch _animClock;

static float NextAnimDt()
{
    if (_animClock == null) { _animClock = System.Diagnostics.Stopwatch.StartNew(); return 0f; }
    float dt = (float)_animClock.Elapsed.TotalSeconds;
    _animClock.Restart();
    if (dt > 0.1f) dt = 0.1f; // cap on hitches / debugger pauses
    return dt;
}
```

- [ ] **Step 2: Add DetectStatDiff method**

Add this method near `SetLpText`:

```csharp
// Compare current run stats vs last snapshot; spawn floaters + HUD counters
// for any non-zero delta. First call (Valid=false) only captures baseline.
// Called every Update tick AFTER WriteRun has applied to ClientWork.
static void DetectStatDiff()
{
    if (!RoguelikeApi.IsRunActive()) { ResetAnim(); return; }
    int curLp   = RoguelikeApi.Lp();
    int curGold = RoguelikeApi.Gold();
    if (!_snap.Valid)
    {
        _snap = new HudSnap { Valid = true, Lp = curLp, Gold = curGold };
        return;
    }
    int dLp   = curLp   - _snap.Lp;
    int dGold = curGold - _snap.Gold;
    if (dLp != 0)   SpawnDelta("lp",   dLp,   _snap.Lp,   curLp, HeaderLp,
        cur => RoguelikeLabels.Get("map.lp", "LP {0} / {1}", cur, RoguelikeApi.MaxLp()));
    if (dGold != 0) SpawnDelta("gold", dGold, _snap.Gold, curGold, HeaderGold,
        cur => RoguelikeLabels.Get("map.gold", "GOLD {0}", cur));
    _snap.Lp = curLp; _snap.Gold = curGold;
}

// Cancel all in-flight floaters/counters (Destroy GOs); used when run ends.
static void ResetAnim()
{
    foreach (var f in _floaters) if (f.Go != IntPtr.Zero) UnityObject.Destroy(f.Go);
    _floaters.Clear();
    _counters.Clear();
    _snap = default(HudSnap);
}
```

- [ ] **Step 3: Add SpawnDelta method**

Add this method right after `DetectStatDiff`:

```csharp
// Build one FloatingDelta + HudCounter pair. Clones the matching HUD label as
// the floater, places it at screen center (with per-stat Y offset to avoid
// overlap when two stats change at once), tints by sign. The HudCounter is
// deferred (Active=false) — it activates when the floater finishes.
static void SpawnDelta(string stat, int delta, int from, int to,
                       string labelPath, Func<int, string> format)
{
    IntPtr labelGo = GameObject.FindGameObjectByPath(_go, labelPath);
    if (labelGo == IntPtr.Zero) return;
    // Clone the label as the floater (parent on the same canvas root, sibling-of-HUD).
    IntPtr parent = GameObject.GetTransform(_go);
    IntPtr clone = UnityObject.Instantiate(labelGo, parent);
    UnityObject.SetName(clone, "RgStatFloater_" + stat);

    // Floater text + color. RGB is sign-tinted; alpha is animated per-frame in TickFloaters.
    Color rgb = delta > 0 ? RoguelikeStatAnim.PositiveRGB : RoguelikeStatAnim.NegativeRGB;
    IntPtr tmp = GameObject.GetComponent(clone, _tmpType);
    if (tmp != IntPtr.Zero)
    {
        string sign = delta > 0 ? "+" : "";
        string label = (stat == "lp")
            ? RoguelikeLabels.Get("anim.lp.delta",   "{0}{1} LP",   sign, delta)
            : RoguelikeLabels.Get("anim.gold.delta", "{0}{1} GOLD", sign, delta);
        TMPro.TMP_Text.SetText(tmp, label);
        TMPro.TMP_Text.SetColor(tmp, rgb);
    }

    // Endpoint = current HUD label pos. Start = screen center + per-stat Y offset.
    Vector3 endPos = GameObject.GetTransform(labelGo) != IntPtr.Zero
        ? GetAnchoredPos3D(GameObject.GetTransform(labelGo))
        : new Vector3(0, 0, 0);
    float yOff = stat == "lp" ? 60f : -60f; // LP up, GOLD down
    Vector3 startPos = new Vector3(0, yOff, 0); // canvas-center

    GetAnchoredPos3DSet(GameObject.GetTransform(clone), startPos);

    // Replace any in-flight HudCounter for the same stat — From=cur (visual),
    // To=novo target, T=0 — so the existing tween continues from the displayed value.
    RoguelikeStatAnim.HudCounter existing = _counters.Find(c => c.LabelPath == labelPath);
    if (existing != null)
    {
        existing.From = ReadCurrentDisplayed(labelPath, from);
        existing.To = to;
        existing.T = 0;
        existing.Active = false; // will be re-armed by the new floater
    }
    else
    {
        existing = new RoguelikeStatAnim.HudCounter
        {
            LabelPath = labelPath, From = from, To = to, T = 0, Active = false,
            Format = format,
        };
        _counters.Add(existing);
    }

    _floaters.Add(new RoguelikeStatAnim.FloatingDelta
    {
        Go = clone, StartPos = startPos, EndPos = endPos, T = 0,
        TargetStat = stat, RGB = rgb, Pending = existing,
    });
}

// Read the current TMP text and extract the displayed integer (for mid-tween
// "From"). Fallback to the parameter on parse fail (e.g., label not yet set).
static int ReadCurrentDisplayed(string labelPath, int fallback)
{
    IntPtr go = GameObject.FindGameObjectByPath(_go, labelPath);
    if (go == IntPtr.Zero) return fallback;
    IntPtr tmp = GameObject.GetComponent(go, _tmpType);
    if (tmp == IntPtr.Zero) return fallback;
    string s = TMPro.TMP_Text.GetText(tmp);
    System.Text.RegularExpressions.Match m =
        System.Text.RegularExpressions.Regex.Match(s ?? "", @"-?\d+");
    int v;
    return m.Success && int.TryParse(m.Value, out v) ? v : fallback;
}

// Read anchored 3D position via existing project pattern (see line 961 in this
// file: `_anchoredPos3D.GetGetMethod().Invoke(rt).GetValueRef<Vector3>()`).
static Vector3 GetAnchoredPos3D(IntPtr t)
{
    return _anchoredPos3D.GetGetMethod().Invoke(t).GetValueRef<Vector3>();
}
static void SetAnchoredPos3D(IntPtr t, Vector3 v)
{
    _anchoredPos3D.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&v) });
}
```

**Replace** the inline `GetAnchoredPos3DSet` calls in `SpawnDelta` with the new `SetAnchoredPos3D`. Update the endPos line to use `GetAnchoredPos3D`:

```csharp
Vector3 endPos = GameObject.GetTransform(labelGo) != IntPtr.Zero
    ? GetAnchoredPos3D(GameObject.GetTransform(labelGo))
    : new Vector3(0, 0, 0);
// ...
SetAnchoredPos3D(GameObject.GetTransform(clone), startPos);
```

- [ ] **Step 4: Hook DetectStatDiff into Update**

`RoguelikeMapScreen.Update()` is at line 242 of `RoguelikeMapScreen.cs`. Add this block at the **start** of the method body (before `RoguelikeFlow.InRoguelike = _go != IntPtr.Zero;`):

```csharp
public static void Update()
{
    // Animation pipeline (stat actions + post-combat) — runs every frame.
    // DetectStatDiff captures changes; TickFloaters drives the floating delta,
    // which activates the deferred HudCounter when it lands.
    DetectStatDiff();
    float dt = NextAnimDt();
    RoguelikeStatAnim.TickFloaters(_floaters, dt, _tmpType, _anchoredPos3D);
    RoguelikeStatAnim.TickCounters(_counters, dt, SetTmpText);
    // ...existing body...
    RoguelikeFlow.InRoguelike = _go != IntPtr.Zero;
    // ...
}
```

Important: the HUD counter writes the LP/Gold text via the `format` lambda each frame, but the existing `SetLpText()` (line 453) ALSO writes the LP/Gold text every frame with the *final* values. To prevent the SetLpText snap from clobbering the counter mid-tween, guard SetLpText:

```csharp
static void SetLpText()
{
    if (_go == IntPtr.Zero || _tmpType == IntPtr.Zero) return;
    int acts = RoguelikeApi.Acts(); if (acts < 1) acts = 1;
    int asc = RoguelikeApi.Ascension();
    string title = RoguelikeLabels.Get("map.title", "Mapa da Run   ·   Ato {0}/{1}", RoguelikeApi.Act() + 1, acts);
    if (asc > 0) title += RoguelikeLabels.Get("map.title.asc", "   ·   Asc {0}", asc);
    SetTmpText(HeaderName, title);
    // Skip the HUD label writes when a counter is animating that label —
    // the counter will write the (interpolated) value each frame instead.
    if (!IsLabelCountering(HeaderLp))
        SetTmpText(HeaderLp, RoguelikeLabels.Get("map.lp", "LP {0} / {1}", RoguelikeApi.Lp(), RoguelikeApi.MaxLp()));
    if (!IsLabelCountering(HeaderGold))
        SetTmpText(HeaderGold, RoguelikeLabels.Get("map.gold", "GOLD {0}", RoguelikeApi.Gold()));
}

static bool IsLabelCountering(string labelPath)
{
    foreach (var c in _counters) if (c.LabelPath == labelPath) return true;
    return false;
}
```

- [ ] **Step 5: Build client**

Run:
```bash
"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Bin\MSBuild.exe" YgoMasterClient.csproj /p:Configuration=Debug /p:Platform=x64 /nologo /v:minimal
```
Expected: builds cleanly. May surface missing reflection wrappers (TMP_Text.GetText/SetAlpha/SetColor) — add to whatever `IL2*` helper file holds the existing TMP wrappers.

- [ ] **Step 6: Smoke — full animation pipeline**

Restart client, enter run, open map. Run each smoke:

- `rgencounter smoke_lp_heal_absolute` → green "+500 LP" floats from center to LP label, fades; LP counter ticks from old → new.
- `rgencounter smoke_lp_damage_percent` → red "-XXXX LP" (XXXX = 30% of MaxLp).
- `rgencounter smoke_gold_gain` → green "+200 GOLD" floats to GOLD label.
- `rgencounter smoke_multi_stat_burst` → 1 LP floater + 1 gold floater spawn at the same time, side-by-side (LP up, GOLD down), each animates to its respective HUD label.
- `rgencounter smoke_chain_msg_lp_msg` → message modal, OK, animation runs while next modal opens, OK.
- `rgencounter smoke_lp_lethal` → red giant "-99999 LP" floats, HUD ticks down to 0, then game over modal opens (already wait-driven via `IsRunActive`).

If positions are off, tune `yOff` constants and the offset in `EnsureGoldLabel`.

- [ ] **Step 7: Smoke — post-combat reuses path**

Run an actual combat encounter, let LP drop. Expected: same red floater appears with the damage taken, HUD ticks down. Confirms the diff-driven path is shared.

- [ ] **Step 8: Commit**

```bash
git add YgoMasterClient/Roguelike/RoguelikeMapScreen.cs
git commit -m "feat(roguelike): diff-driven floating delta + HUD counter animation"
```

---

## Task 9: Docs — encounters.md stat actions

**Files:**
- Modify: `Docs/config/roguelike/encounters.md`

- [ ] **Step 1: Add stat actions section**

Edit `Docs/config/roguelike/encounters.md` — add this section after the `openpack` description (look for the "Actions (options / message / openpack)" heading):

```markdown
### Stat actions (`lp` / `gold`)

Apply a delta to a run-level stat. Fire-and-forget: the engine runs the
action and advances to `next` without waiting for client ack. The HUD
animates the change automatically (floating number sliding from center
to the HUD label, then HUD counter ticks to the new value).

**Schema:**

```json
{ "type": "lp",
  "delta": -500,                 // OR
  "delta_percent": -0.30 }       // exactly one is required
```

```json
{ "type": "gold",
  "delta": +200,                 // OR
  "delta_percent": -0.20 }       // exactly one is required
```

| Field | Required | Notes |
|---|---|---|
| `delta` | XOR | Absolute int. Positive = heal/gain, negative = damage/cost. |
| `delta_percent` | XOR | Fraction. **lp**: fraction of `MaxLp`. **gold**: fraction of CURRENT gold. Out-of-range `[-1, 1]` warned + clamped at apply time. |
| `next` | no | Chain another action when this one finishes. Same shape as elsewhere. |

**Clamping:**
- `lp`: upper = `MaxLp`; lower = `0`. If `lp <= 0` after apply → **game over** (same path as combat loss).
- `gold`: lower = `0` (no negative gold); no upper cap.

**Chaining example** (event prologue → effect → epilogue):

```json
{ "type": "message", "title": "O Altar", "message": "Você sangra na pedra.",
  "next": { "type": "lp", "delta": -500,
    "next": { "type": "message", "title": "Recompensa", "message": "Algo brilha no chão.",
      "next": { "type": "gold", "delta": 300 }
    }
  }
}
```

**Standalone** (no message wrapper — useful for passive triggers like
"perde 5 LP por nó andado" no futuro):

```json
{ "type": "lp", "delta": -5 }
```
```

- [ ] **Step 2: Commit**

```bash
git add Docs/config/roguelike/encounters.md
git commit -m "docs(roguelike): document lp/gold stat actions"
```

---

## Task 10: Docs — settings.md gold rename note

**Files:**
- Modify: `Docs/config/roguelike/settings.md`

- [ ] **Step 1: Update reference**

Open `Docs/config/roguelike/settings.md`. Find any reference to `currency` in the wire/JSON description. Replace with `gold`. If there's a changelog section at the bottom, add:

```markdown
### Wire change

Run state JSON now exposes the run currency under `gold` (was `currency`).
Old `roguelike.json` files on disk are read with back-compat (tries `gold`
first, falls back to `currency`).
```

If `settings.md` doesn't mention `currency` directly, this task is a no-op — just verify with grep:

```bash
grep -n currency Docs/config/roguelike/settings.md
```

If empty, skip the edit and proceed to commit. If any matches found, update them.

- [ ] **Step 2: Commit (if changes)**

```bash
git add Docs/config/roguelike/settings.md
git commit -m "docs(roguelike): note currency->gold wire rename"
```

---

## Task 11: Cleanup smokes (optional — keep or trim)

**Files:**
- Modify: `DataLE/Roguelike/Encounters.json`

- [ ] **Step 1: Decide which smokes stay**

The 6 stat smokes were useful for verification. Keep them on `act: {min:99}` (off the regular eligible pool) for future regression. **No action required if all 6 stay** — just verify they don't accidentally appear in normal play (act 0-2).

If you want to trim, remove `smoke_lp_lethal` (since it's instant game over — rarely useful again) and keep the other 5.

- [ ] **Step 2: Commit (if changes)**

```bash
git add "D:/SteamLibrary/steamapps/common/Yu-Gi-Oh!  Master Duel/YgoMasterLE - Goat/DataLE/Roguelike/Encounters.json"
git commit -m "chore(roguelike): trim stat smokes (kept core regression set)"
```

---

## Final verification

After all tasks complete:

- [ ] Run full duel chain: pick deck → first combat → take damage → see floater + counter
- [ ] Reload client mid-tween: previous floater disappears, baseline resets, no ghost animation
- [ ] Verify `roguelike.json` has `gold` (not `currency`); old saves still load
- [ ] Grep for any leftover `Roguelike.currency` references in client code:
  ```bash
  grep -rn 'Roguelike\.currency' YgoMasterClient/
  ```
  Expected: no matches.
- [ ] Read the design doc one more time and confirm every requirement has an implementing task.
