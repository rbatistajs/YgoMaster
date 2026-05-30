# Roguelike M2.5 (Deck-Select Screen) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps
> use checkbox (`- [ ]`) syntax.

**Goal:** Replace the M2 ActionSheet with a real in-game **deck-select screen** (base
`Solo/SoloMode`, animated background) showing the 3 offered decks as card tiles (boss-card
art + name); tapping a tile opens a drawer to **view the deck** (native deck viewer) and
**select** it, with a per-deck **description**.

**Architecture:** Push the game's `Solo/SoloMode` screen via `ViewControllerManager`; hook
`SoloPortalViewController.OnCreatedView` (gated by our own flag so normal Solo is
untouched) and repurpose its `RecommendGroup` tiles (`RecommendButton1/2`, cloned to 3).
Card art is rendered with the stock `AssetHelper.LoadImmediateAsset` + `SpriteFromTexture`
pipeline (Roguelike-owned `RoguelikeCardImage`). "Ver deck" reuses `YgomGame.Deck.DeckView`.
Server enriches each offer with card lists + description. New client code lives in
`YgoMasterClient/Roguelike/`.

**Tech stack:** C# (.NET Framework), IL2CPP reflection (`Assembler`/`IL2Class`/`IL2Method`/
`IL2Field`/`IL2Property`), `Hook<T>` (MinHook, **one hook per target**), Unity wrappers
(`GameObject`/`Transform`/`Component`/`UnityObject` in `DuelStarter.cs`, namespace
`UnityEngine`), `ViewControllerManager`/`ContentViewControllerManager`, `TMPro.TMP_Text`,
`SelectionButton.onClick` + `_UnityAction`, `AssetHelper`, `YgomGame.Deck.DeckView`,
`MiniJSON`.

**House rule:** no reuse of `Goat/` code. Allowed: stock YgoMaster (`AssetHelper`,
`DeckEditorUtils`/`DeckView`, `ViewControllerManager`, the `Hook` infra, `MiniJSON`,
`Utils`) and our `Roguelike/` code. May read `Goat/` for inspiration (e.g.
`SoloChapterCardImage.cs`).

**Verification reality:** IL2CPP mod injected into a running game — **no unit tests**.
"Verify" = build + launch server + game + read console / observe behavior.
- Client build (auto-copies to install): `MSBuild YgoMasterClient.csproj -t:Build -p:Configuration=Release -v:minimal -nologo`.
- Server build: `MSBuild YgoMasterServer/YgoMaster.csproj -t:Build -p:Configuration=Release -p:Platform=x64 -v:minimal -nologo` (+ `-p:GoatInstallDir=""` to skip the copy if the server exe is running).
- Dev console commands available (kept from the spike): `vcdump [depth]`, `vclog`, `rgpush <path>`.

**Reused facts (verified during the spike):**
- Push a screen: `YgomSystem.UI.ViewControllerManager.PushChildViewController(manager, "Solo/SoloMode")`; manager = `YgomGame.Menu.ContentViewControllerManager.GetManager()`; close = `PopChildViewController(manager)`; fetch a pushed VC = `GetViewController(manager, <type>.IL2Typeof())`.
- `Solo/SoloMode` opens standalone with its animated bg and auto-loads `SoloPortalViewController`. Its content tree (from `vcdump 12`, file `_tmp/vc_SoloPortalViewController.txt`):
  ```
  SoloPortalUI(Clone)/Root/
    TitleSafeArea/TitleGroup/NameText                         (title TMP)
    ButtonArea/MainGroup/RecommendGroup/                      (HorizontalLayoutGroup)
      RecommendText                                           (section label TMP+Binding)
      RecommendButton1, RecommendButton2/Button[SelectionButton]/Main/
        Mask/ImageGate[Image,BindingSoloCardThumb]/SoloCardThumbMask/SoloCardThumbImage[RawImage,AutoReleaseCardIllust]
        NameArea/NameLayoutGroup/TextGateName[ExtendedTextMeshProUGUI]   (NO binding → safe to set)
    ButtonArea/MainGroup/LastPlayGroup                        (hide)
    ButtonArea/GateListGroup                                  (hide: Histórias/Treinamento)
  ```
- OnCreatedView hook pattern (see `DuelStarter.cs` `SoloStartProductionViewController`, lines 119-131, 164): `delegate void Del_OnCreatedView(IntPtr thisPtr);` + `new Hook<Del_OnCreatedView>(OnCreatedView, classInfo.GetMethod("OnCreatedView"))`; handler calls `hook.Original(thisPtr)` then customizes.
- Clone / text / onClick primitives (see `YgoMasterClient/Roguelike/RoguelikeHomeButton.cs`): `UnityObject.Instantiate(template, parent)`, `UnityObject.SetName`, `GameObject.FindGameObjectByName/FindGameObjectByPath`, `GameObject.GetComponent`, `TMPro.TMP_Text.SetText(comp, text)`, `SelectionButton.onClick` field + `UnityEvent.AddListener` + `UnityEngine.Events._UnityAction.CreateUnityAction(Action)` / `CreateAction<int>(Action<IntPtr,int>)`.
- Card art (proven by `rgproof`, see `Roguelike/RoguelikeUiProof.cs`): `AssetHelper.LoadImmediateAsset("Card/Images/Illust/tcg/<cid>")` → `AssetHelper.SpriteFromTexture(tex, name)` → keep alive `Import.Handler.il2cpp_gchandle_new(sprite, true)` → `UnityEngine.UI.Image.sprite`. `Image` type from `Assembler.GetAssembly("UnityEngine.UI").GetClass("Image","UnityEngine.UI")`; `preserveAspect` on `Image`; `color` on `Graphic`.
- Deck grid viewer: `YgomGame.Deck.DeckView.SetCards(deckViewPtr, CardCollection main, CardCollection extra)` (wrapper in `DeckEditorUtils.cs`); `DeckEditViewController2.currentInstance`/`fieldDeckView` hold the active deck view.
- Server: `dataDirectory` field on `GameServer`; offers built in `RoguelikeDeckPool`/`GameServer.Roguelike.cs`; run persisted by `RoguelikeRun`.
- Client API: `RoguelikeApi.ChooseDeck(int)`, `IsDeckChosen()`, `GetDeckOfferNames()` (extend with offer detail readers in Task 4).

---

## Task 1 — Server: enrich offers (description + card ids) + StartingDeck `description`

**Files:** `YgoMasterServer/Roguelike/RoguelikeDeckPool.cs`,
`YgoMasterServer/Roguelike/GameServer.Roguelike.cs`; data:
`<install>/DataLE/Roguelike/StartingDecks/*.json`.

- [ ] **Step 1: Add `Description` to `RoguelikeDeckPool.StarterDeck` + read it in `LoadOne`.**

In `RoguelikeDeckPool.cs`, add the field and populate it:

```csharp
public class StarterDeck
{
    public string Name;
    public int BossCard;
    public string Description;                 // from `description` field (optional)
    public Dictionary<string, object> Json;    // raw player-format deck dict
}
```
In `LoadOne`, set `Description = Utils.GetValue<string>(doc, "description", "")` in the returned object.

- [ ] **Step 2: Include description + card-id sections in each rolled offer.**

In `GameServer.Roguelike.cs` `RollDeckOffers`, change the offer dict built per deck to also
carry the description and the main/extra/side id lists (so the client can render art for the
boss card and feed the deck viewer without another round-trip):

```csharp
RoguelikeDeckPool.StarterDeck d = RoguelikeDeckPool.LoadOne(files[i]);
if (d == null) continue;
string rel = files[i].StartsWith(dataDirectory)
    ? files[i].Substring(dataDirectory.Length).TrimStart('\\', '/')
    : files[i];
offers.Add(new Dictionary<string, object>
{
    { "name", d.Name },
    { "bossCard", d.BossCard },
    { "file", rel },
    { "description", d.Description },
    { "main",  IdsOf(d.Json, "m") },
    { "extra", IdsOf(d.Json, "e") },
    { "side",  IdsOf(d.Json, "s") },
});
```

Add the helper to the same partial (`GameServer.Roguelike.cs`):

```csharp
// Pull the id list of a player-format deck section ("m"/"e"/"s") as a List<object>.
static List<object> IdsOf(Dictionary<string, object> deckJson, string section)
{
    Dictionary<string, object> sec = Utils.GetValue<Dictionary<string, object>>(deckJson, section);
    List<object> ids = sec != null ? Utils.GetValue<List<object>>(sec, "ids") : null;
    return ids ?? new List<object>();
}
```

- [ ] **Step 3: Carry description into the chosen deck.**

In `Act_RoguelikeChooseDeck`, where `run.Deck` is built, add the description:

```csharp
run.Deck = new Dictionary<string, object>
{
    { "name", d.Name }, { "bossCard", d.BossCard },
    { "description", d.Description }, { "deck", d.Json },
};
```

- [ ] **Step 4: Add `description` to the seeded StartingDeck JSONs (data).**

For each of the 6 files in `<install>/DataLE/Roguelike/StartingDecks/`, add a top-level
`"description"` string (player-editable). Example for `Chaos Turbo.json` — insert the key
after `"name"`:

```json
"description": "Controle agressivo: remove ameaças e fecha o jogo com monstros de Caos.",
```
Use these defaults (short, PT-BR; the user can rewrite later):
- Chaos Turbo → "Controle de Caos: troca recursos e fecha com Envoys."
- Cat Control → "Toolbox de controle baseada em Catnipped Kitty / King Tiger."
- Final Countdown → "Stall/burn: sobreviva 20 turnos e vença pelo cronômetro."
- Buster Blader → "Beatdown anti-Dragão com Buster Blader."
- Relinquished → "Controle: domina o monstro do oponente com Relinquished."
- Blue-Eyes → "Beatdown de poder bruto com Blue-Eyes White Dragon."

- [ ] **Step 5: Build server.**

Run: `MSBuild YgoMasterServer/YgoMaster.csproj -t:Build -p:Configuration=Release -p:Platform=x64 -v:minimal -nologo -p:GoatInstallDir=""`
Expected: build succeeds.

- [ ] **Step 6: Commit.**

```bash
git add YgoMasterServer/Roguelike/RoguelikeDeckPool.cs YgoMasterServer/Roguelike/GameServer.Roguelike.cs
git commit -m "feat(roguelike): enrich deck offers with description + card ids"
```
(StartingDeck JSONs are install-side data, not tracked in git.)

---

## Task 2 — Client: `RoguelikeCardImage` (load a card sprite by cid, cached) + attach helper

**Files:** Create `YgoMasterClient/Roguelike/RoguelikeCardImage.cs`; modify
`YgoMasterClient.csproj` (Compile include).

- [ ] **Step 1: Create the helper.** Ports the proven sprite pipeline from
`RoguelikeUiProof`/`SoloChapterCardImage` (stock `AssetHelper`), plus a method to attach a
preserve-aspect card `Image` filling a parent.

```csharp
using IL2CPP;
using System;
using System.Collections.Generic;
using UnityEngine;
using YgoMaster;

namespace YgoMasterClient
{
    // Loads card artwork by card id (stock AssetHelper: custom PNG in ClientData, else the
    // game's bundle which has every card) and attaches it to a UI parent. Sprites are cached
    // + gc-anchored so Unity doesn't collect them.
    static unsafe class RoguelikeCardImage
    {
        static IntPtr _imageType;
        static IL2Method _imageSetSprite;
        static IL2Property _imagePreserveAspect;
        static IntPtr _rectType;
        static IL2Property _anchorMin, _anchorMax, _offsetMin, _offsetMax;
        static readonly Dictionary<int, IntPtr> _cache = new Dictionary<int, IntPtr>();
        static bool _ready;

        static RoguelikeCardImage()
        {
            try
            {
                IL2Assembly ui = Assembler.GetAssembly("UnityEngine.UI");
                IL2Class image = ui.GetClass("Image", "UnityEngine.UI");
                _imageType = image.IL2Typeof();
                _imageSetSprite = image.GetProperty("sprite").GetSetMethod();
                _imagePreserveAspect = image.GetProperty("preserveAspect");
                IL2Assembly core = Assembler.GetAssembly("UnityEngine.CoreModule");
                IL2Class rect = core.GetClass("RectTransform", "UnityEngine");
                _rectType = rect.IL2Typeof();
                _anchorMin = rect.GetProperty("anchorMin");
                _anchorMax = rect.GetProperty("anchorMax");
                _offsetMin = rect.GetProperty("offsetMin");
                _offsetMax = rect.GetProperty("offsetMax");
                _ready = true;
            }
            catch (Exception ex) { Console.WriteLine("[RoguelikeCardImage] init EX: " + ex); }
        }

        public static IntPtr LoadSprite(int cid)
        {
            IntPtr cached;
            if (_cache.TryGetValue(cid, out cached)) return cached;
            IntPtr tex = AssetHelper.LoadImmediateAsset("Card/Images/Illust/tcg/" + cid);
            IntPtr sprite = tex == IntPtr.Zero ? IntPtr.Zero
                : AssetHelper.SpriteFromTexture(tex, "rg_card_" + cid);
            if (sprite != IntPtr.Zero) Import.Handler.il2cpp_gchandle_new(sprite, true);
            _cache[cid] = sprite;
            return sprite;
        }

        // Create an Image child named `name` filling `parent`, showing card `cid` (aspect
        // preserved). Returns the image GameObject, or Zero on failure. Idempotent by name.
        public static IntPtr AttachFill(IntPtr parent, int cid, string name)
        {
            if (!_ready || parent == IntPtr.Zero) return IntPtr.Zero;
            IntPtr sprite = LoadSprite(cid);
            if (sprite == IntPtr.Zero) { Console.WriteLine("[RoguelikeCardImage] no art for cid " + cid); return IntPtr.Zero; }

            IntPtr existing = GameObject.FindGameObjectByName(parent, name, false, false);
            if (existing != IntPtr.Zero) GameObject.SetActive(existing, false);

            IntPtr go = GameObject.New();
            UnityObject.SetName(go, name);
            GameObject.AddComponent(go, _rectType);
            IntPtr img = GameObject.AddComponent(go, _imageType);
            IntPtr t = GameObject.GetTransform(go);
            Transform.SetParent(t, GameObject.GetTransform(parent));
            SetFull(t);
            Transform.SetAsLastSibling(t);
            Transform.SetLocalScale(t, new Vector3(1, 1, 1));
            _imageSetSprite.Invoke(img, new IntPtr[] { sprite });
            csbool preserve = true;
            _imagePreserveAspect.GetSetMethod().Invoke(img, new IntPtr[] { new IntPtr(&preserve) });
            return go;
        }

        static void SetFull(IntPtr t)
        {
            AssetHelper.Vector2 min = new AssetHelper.Vector2(0, 0);
            AssetHelper.Vector2 max = new AssetHelper.Vector2(1, 1);
            AssetHelper.Vector2 zero = new AssetHelper.Vector2(0, 0);
            _anchorMin.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&min) });
            _anchorMax.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&max) });
            _offsetMin.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&zero) });
            _offsetMax.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&zero) });
        }
    }
}
```

- [ ] **Step 2: Register in csproj.** Add near the other `Roguelike\*` includes in
`YgoMasterClient.csproj`:
`<Compile Include="YgoMasterClient\Roguelike\RoguelikeCardImage.cs" />`

- [ ] **Step 3: Build client.** Run: `MSBuild YgoMasterClient.csproj -t:Build -p:Configuration=Release -v:minimal -nologo`. Expected: build succeeds.
- [ ] **Step 4: Commit.**
```bash
git add YgoMasterClient/Roguelike/RoguelikeCardImage.cs YgoMasterClient.csproj
git commit -m "feat(roguelike): RoguelikeCardImage (card art loader + attach)"
```

---

## Task 3 — Client: `RoguelikeDeckSelectScreen` — push SoloMode + customize (title + hide groups)

**Files:** Create `YgoMasterClient/Roguelike/RoguelikeDeckSelectScreen.cs`; modify
`YgoMasterClient.csproj`; register in `YgoMasterClient/Program.cs` `nativeTypes`
(eager cctor so the OnCreatedView hook installs at startup).

> **Hook-availability check first.** `Hook<T>` allows ONE hook per target. Confirm nothing
> already hooks `SoloPortalViewController.OnCreatedView`:
> Run a Grep for `SoloPortalViewController` across `YgoMasterClient/`. Expected: only our
> new file references it. If something else hooks `OnCreatedView`, piggyback that hook
> instead of adding a second one (call our customize from there).

- [ ] **Step 1: Create the screen class with the gated OnCreatedView hook.** It pushes
`Solo/SoloMode` and, only when WE asked for it (`_pending` flag), customizes the
`SoloPortal` content: set the title and hide the Last-Play + bottom gate-list groups.

```csharp
using IL2CPP;
using System;
using UnityEngine;

namespace YgoMasterClient
{
    // Roguelike deck-select screen. Reuses the game's Solo/SoloMode screen (animated bg +
    // header) and repurposes SoloPortal's RecommendGroup tiles as the 3 deck choices.
    // The OnCreatedView hook only customizes when _pending is set by Open() — normal Solo
    // mode is untouched.
    static unsafe class RoguelikeDeckSelectScreen
    {
        const string PortalRoot = "SoloPortalUI(Clone).Root";
        static IL2Class _portalClass;
        static IL2Method _portalOnCreatedView;
        delegate void Del_OnCreatedView(IntPtr thisPtr);
        static Hook<Del_OnCreatedView> _hook;
        static bool _pending;
        static bool _ready;

        static RoguelikeDeckSelectScreen()
        {
            try
            {
                IL2Assembly asm = Assembler.GetAssembly("Assembly-CSharp");
                _portalClass = asm.GetClass("SoloPortalViewController", "YgomSystem.UI"); // ns confirmed in Step 0 (see note)
                _portalOnCreatedView = _portalClass.GetMethod("OnCreatedView");
                _hook = new Hook<Del_OnCreatedView>(OnCreatedView, _portalOnCreatedView);
                _ready = true;
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] deckselect init EX: " + ex); }
        }

        public static void Open()
        {
            if (!_ready) { Console.WriteLine("[Roguelike] deckselect not ready"); return; }
            _pending = true;
            IntPtr manager = YgomGame.Menu.ContentViewControllerManager.GetManager();
            if (manager == IntPtr.Zero) { _pending = false; return; }
            YgomSystem.UI.ViewControllerManager.PushChildViewController(manager, "Solo/SoloMode");
        }

        static void OnCreatedView(IntPtr thisPtr)
        {
            _hook.Original(thisPtr);
            if (!_pending) return;
            _pending = false;
            try { Customize(thisPtr); }
            catch (Exception ex) { Console.WriteLine("[Roguelike] deckselect customize EX: " + ex); }
        }

        static void Customize(IntPtr portalPtr)
        {
            IntPtr go = Component.GetGameObject(portalPtr);
            IntPtr root = GameObject.FindGameObjectByPath(go, PortalRoot);
            if (root == IntPtr.Zero) { Console.WriteLine("[Roguelike] portal root not found"); return; }

            SetText(root, "TitleSafeArea.TitleGroup.NameText", "Escolha seu Deck");
            Hide(root, "ButtonArea.MainGroup.LastPlayGroup");
            Hide(root, "ButtonArea.GateListGroup");
            Console.WriteLine("[Roguelike] deckselect customized");
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
            IntPtr tmp = GameObject.GetComponent(o, CastUtils.IL2Typeof("ExtendedTextMeshProUGUI", "YgomSystem.YGomTMPro", "Assembly-CSharp"));
            if (tmp != IntPtr.Zero) TMPro.TMP_Text.SetText(tmp, text);
        }
    }
}
```

> **Note (resolve in Step 1):** the `SoloPortalViewController` namespace — confirm via
> `vcdump`/code. `SoloStartProductionViewController` is in `YgomGame.Solo` (DuelStarter.cs:125),
> so `SoloPortalViewController` is almost certainly `YgomGame.Solo` too. Use that namespace
> if `YgomSystem.UI` fails; log the exception and fix. (If `NameText` carries a
> `BindingTextMeshProUGUI`, the literal may be overwritten by binding — if so, also call
> `BindingTextMeshProUGUI.SetTextId(comp, text)` like `RoguelikeHomeButton.SetBoundText`.)

- [ ] **Step 2: Register.** Add to `YgoMasterClient.csproj`:
`<Compile Include="YgoMasterClient\Roguelike\RoguelikeDeckSelectScreen.cs" />`. Add to
`Program.cs` nativeTypes (near `RoguelikeApi`): `nativeTypes.Add(typeof(RoguelikeDeckSelectScreen));`

- [ ] **Step 3: Temporary trigger to test.** In `ConsoleHelper.cs` add a dev command
(removed in Task 7) to open the screen directly:
```csharp
case "rgdeck":// dev: open the roguelike deck-select screen
    RoguelikeDeckSelectScreen.Open();
    break;
```

- [ ] **Step 4: Build client.** Run: `MSBuild YgoMasterClient.csproj -t:Build -p:Configuration=Release -v:minimal -nologo`. Expected: build succeeds.
- [ ] **Step 5: Verify in-game.** Restart game; from the console type `rgdeck`. Expected:
SoloMode opens with animated bg; title reads "Escolha seu Deck"; the Last-Play tile and the
bottom Histórias/Treinamento row are hidden. Console logs `[Roguelike] deckselect customized`.
Back button returns cleanly. (If paths are wrong, `vcdump 12` and fix the path constants.)
- [ ] **Step 6: Commit.**
```bash
git add YgoMasterClient/Roguelike/RoguelikeDeckSelectScreen.cs YgoMasterClient.csproj YgoMasterClient/Program.cs YgoMasterClient/ConsoleHelper.cs
git commit -m "feat(roguelike): deck-select screen base (SoloMode push + title + hide groups)"
```

---

## Task 4 — Client: populate the 3 deck tiles (art + name + onClick)

**Files:** `YgoMasterClient/Roguelike/RoguelikeDeckSelectScreen.cs`,
`YgoMasterClient/Roguelike/RoguelikeApi.cs` (offer detail readers).

- [ ] **Step 1: Add offer readers to `RoguelikeApi`.** Read the full offers array once and
expose per-index detail (name, bossCard, description, card-id lists) for the tiles + drawer.

```csharp
public class DeckOffer
{
    public string Name = "Deck";
    public int BossCard;
    public string Description = "";
    public System.Collections.Generic.List<int> Main = new System.Collections.Generic.List<int>();
    public System.Collections.Generic.List<int> Extra = new System.Collections.Generic.List<int>();
    public System.Collections.Generic.List<int> Side = new System.Collections.Generic.List<int>();
}

public static System.Collections.Generic.List<DeckOffer> GetDeckOffers()
{
    var result = new System.Collections.Generic.List<DeckOffer>();
    try
    {
        string json = YgomSystem.Utility.ClientWork.SerializePath("Roguelike.deckOffers");
        if (string.IsNullOrEmpty(json)) return result;
        var list = MiniJSON.Json.Deserialize(json) as System.Collections.Generic.List<object>;
        if (list == null) return result;
        foreach (object o in list)
        {
            var d = o as System.Collections.Generic.Dictionary<string, object>;
            if (d == null) continue;
            var offer = new DeckOffer
            {
                Name = d.ContainsKey("name") ? Convert.ToString(d["name"]) : "Deck",
                BossCard = d.ContainsKey("bossCard") ? Convert.ToInt32(d["bossCard"]) : 0,
                Description = d.ContainsKey("description") ? Convert.ToString(d["description"]) : "",
            };
            ReadIds(d, "main", offer.Main); ReadIds(d, "extra", offer.Extra); ReadIds(d, "side", offer.Side);
            result.Add(offer);
        }
    }
    catch (Exception ex) { Console.WriteLine("[Roguelike] GetDeckOffers EX: " + ex); }
    return result;
}

static void ReadIds(System.Collections.Generic.Dictionary<string, object> d, string key, System.Collections.Generic.List<int> into)
{
    var arr = d.ContainsKey(key) ? d[key] as System.Collections.Generic.List<object> : null;
    if (arr == null) return;
    foreach (object v in arr) { try { into.Add(Convert.ToInt32(v)); } catch { } }
}
```

- [ ] **Step 2: Populate tiles in `Customize`.** After hiding groups, build exactly N tiles
(N = offer count, ≤3) from the `RecommendGroup`. Use `RecommendButton1` as template; clone it
(`UnityObject.Instantiate`) for any missing slots; hide extra buttons. For each tile set the
name (`TextGateName`), overlay the boss-card art onto `ImageGate`, and wire onClick.

```csharp
const string RecommendGroup = "ButtonArea.MainGroup.RecommendGroup";
static System.Collections.Generic.List<RoguelikeApi.DeckOffer> _offers;
static readonly Action<IntPtr, int>[] _tileHandlers =
{
    (c,i) => OnTile(0), (c,i) => OnTile(1), (c,i) => OnTile(2),
};

// inside Customize(...), after Hide(...):
_offers = RoguelikeApi.GetDeckOffers();
IntPtr group = GameObject.FindGameObjectByPath(root, RecommendGroup);
SetText(root, RecommendGroup + ".RecommendText", "Decks");
IntPtr template = GameObject.FindGameObjectByName(group, "RecommendButton1", false, false);
for (int i = 0; i < 3; i++)
{
    string tileName = "RgTile" + i;
    IntPtr tile = GameObject.FindGameObjectByName(group, tileName, false, false);
    if (tile == IntPtr.Zero)
    {
        IntPtr src = i == 0 ? template
            : GameObject.FindGameObjectByName(group, "RecommendButton2", false, false);
        if (src == IntPtr.Zero) src = template;
        tile = UnityObject.Instantiate(src, GameObject.GetTransform(group));
        UnityObject.SetName(tile, tileName);
    }
    if (i < _offers.Count)
    {
        GameObject.SetActive(tile, true);
        SetupTile(tile, _offers[i], i);
    }
    else GameObject.SetActive(tile, false);
}
// hide the originals we cloned from
HideByName(group, "RecommendButton1"); HideByName(group, "RecommendButton2");
```

```csharp
static void SetupTile(IntPtr tile, RoguelikeApi.DeckOffer offer, int index)
{
    // name
    IntPtr nameTmp = GameObject.FindGameObjectByPath(tile, "Button.Main.NameArea.NameLayoutGroup.TextGateName");
    if (nameTmp != IntPtr.Zero)
    {
        IntPtr c = GameObject.GetComponent(nameTmp, CastUtils.IL2Typeof("ExtendedTextMeshProUGUI", "YgomSystem.YGomTMPro", "Assembly-CSharp"));
        if (c != IntPtr.Zero) TMPro.TMP_Text.SetText(c, offer.Name);
    }
    // boss-card art overlaid on the gate image area
    IntPtr imageGate = GameObject.FindGameObjectByPath(tile, "Button.Main.Mask.ImageGate");
    if (imageGate != IntPtr.Zero && offer.BossCard > 0)
        RoguelikeCardImage.AttachFill(imageGate, offer.BossCard, "RgCardArt");
    // onClick → our handler (clear existing listeners first)
    IntPtr sel = GameObject.FindGameObjectByPath(tile, "Button");
    sel = sel == IntPtr.Zero ? IntPtr.Zero : GameObject.GetComponent(sel, SelectionButtonType());
    if (sel != IntPtr.Zero) WireOnClick(sel, index);
}
```

> Reuse the exact `SelectionButton.onClick` wiring from `RoguelikeHomeButton.cs` (cache the
> `SelectionButton` class/type + `onClick` field + `UnityEvent.AddListener` +
> `_UnityAction.CreateUnityAction` in the cctor). Add a `SelectionButtonType()` accessor +
> `WireOnClick(sel, index)`: get the `onClick` UnityEvent, **clear existing listeners**
> (`UnityEvent.RemoveAllListeners` — resolve the method on `UnityEngine.Events.UnityEvent`),
> then `AddListener(CreateUnityAction(_tileHandlers[index]))`. If `RemoveAllListeners` proves
> unavailable/unsafe, instead set the `onClick` field to a fresh `UnityEvent` instance.
> `HideByName(parent,name)` = find child by name + `SetActive(false)`. `OnTile(int)` is a stub
> here (logs `[Roguelike] tile <i>`); the drawer is wired in Task 5.

- [ ] **Step 3: Build client + verify in-game (`rgdeck`).** Expected: 3 tiles in a row, each
showing a boss-card illustration + the deck name; clicking a tile logs `[Roguelike] tile <i>`.
(If a tile is misaligned/empty, `vcdump 14` the open screen and adjust the child paths in
`SetupTile`.)
- [ ] **Step 4: Commit.**
```bash
git add YgoMasterClient/Roguelike/RoguelikeDeckSelectScreen.cs YgoMasterClient/Roguelike/RoguelikeApi.cs
git commit -m "feat(roguelike): deck-select tiles (boss art + name + click)"
```

---

## Task 5 — Client: tile drawer (Ver deck / Selecionar) + native deck viewer

**Files:** `YgoMasterClient/Roguelike/RoguelikeDeckSelectScreen.cs`,
`YgoMasterClient/Roguelike/RoguelikeApi.cs`.

- [ ] **Step 1: Drawer on tile click.** Replace the `OnTile(int)` stub with an ActionSheet
offering the two actions + the description as the title (reuses the proven
`ActionSheetViewController`/`CommonDialog` from M1):

```csharp
static void OnTile(int index)
{
    if (_offers == null || index >= _offers.Count) return;
    RoguelikeApi.DeckOffer offer = _offers[index];
    string title = offer.Name + (string.IsNullOrEmpty(offer.Description) ? "" : "\n" + offer.Description);
    string[] entries = { "Ver deck", "Selecionar" };
    YgomGame.Menu.ActionSheetViewController.Open(title, entries, (ctx, choice) =>
    {
        if (choice == 0) ShowDeck(offer);
        else if (choice == 1) Select(index);
    });
}

static void Select(int index)
{
    RoguelikeApi.ChooseDeck(index);
    IntPtr manager = YgomGame.Menu.ContentViewControllerManager.GetManager();
    if (manager != IntPtr.Zero) YgomSystem.UI.ViewControllerManager.PopChildViewController(manager);
}
```

- [ ] **Step 2: "Ver deck" via the native deck viewer.** Discovery step (the riskiest reuse).
The deck grid is rendered by `YgomGame.Deck.DeckView.SetCards(deckViewPtr, mainColl, extraColl)`,
and a deck view lives inside a DeckEdit-family screen. Determine the cleanest standalone
viewer:
  - Run `vclog`, then in-game open a real "Confirm Opponent's Deck" view (Solo gate → Confirmar
    Deck do Oponente) and read the `[vc] <path>` it loads; `vcdump` it. Record the prefab path +
    controller class.
  - Implement `ShowDeck(offer)`: push that prefab; get its view-controller via
    `GetViewController(manager, <class>.IL2Typeof())`; obtain the `DeckView` widget (its field —
    mirror `DeckEditViewController2.fieldDeckView`); build two `CardCollection`s from
    `offer.Main`/`offer.Extra` (style rarity = Normal, owned = true) and call
    `YgomGame.Deck.DeckView.SetCards(deckView, main, extra)`. Set the description text if the
    viewer exposes a description TMP (from the dump).

```csharp
static void ShowDeck(RoguelikeApi.DeckOffer offer)
{
    // Build collections from the offer ids.
    CardCollection main = new CardCollection();
    foreach (int id in offer.Main) main.Add(id, CardStyleRarity.Normal, 1);
    CardCollection extra = new CardCollection();
    foreach (int id in offer.Extra) extra.Add(id, CardStyleRarity.Normal, 1);
    // push viewer + SetCards — concrete prefab/class/field filled in from the discovery above.
    RoguelikeDeckViewer.Show(offer.Name, offer.Description, main, extra);
}
```

> Put the viewer plumbing in a small `RoguelikeDeckViewer` (new file) so the screen class
> stays focused. **Fallback if the native viewer can't be opened standalone cleanly:** render
> a simple grid ourselves — a scrollable panel of `RoguelikeCardImage.AttachFill` thumbnails
> (one per `offer.Main` id) inside a pushed copy of a simple screen, with the description as a
> TMP. Decide during the discovery step; do NOT fake the native path if it doesn't work.

- [ ] **Step 3: Build client + verify in-game.** `rgdeck` → tap a tile → ActionSheet shows
the name+description + "Ver deck"/"Selecionar". "Ver deck" shows the deck's cards (native
viewer or fallback grid). "Selecionar" issues `choose_deck` and pops back; `roguelike.json`
has `deckChosen:true` + `deck` populated.
- [ ] **Step 4: Commit.**
```bash
git add YgoMasterClient/Roguelike/RoguelikeDeckSelectScreen.cs YgoMasterClient/Roguelike/RoguelikeDeckViewer.cs YgoMasterClient/Roguelike/RoguelikeApi.cs YgoMasterClient.csproj
git commit -m "feat(roguelike): tile drawer + deck viewer (view/select)"
```

---

## Task 6 — Client: route start/continue to the screen (retire the ActionSheet path)

**Files:** `YgoMasterClient/Roguelike/RoguelikeFlow.cs`,
`YgoMasterClient/Roguelike/RoguelikeHomeButton.cs`.

- [ ] **Step 1: Open the screen on `start_run` completion.** In
`RoguelikeFlow.OnNetworkComplete`, replace the `start_run` branch body
(`OpenDeckSelect();`) with `RoguelikeDeckSelectScreen.Open();`. Keep the `choose_deck` toast.
Replace the body of `OpenDeckSelect()` (still called by "Continuar") with
`RoguelikeDeckSelectScreen.Open();` (so both paths use the new screen). Remove the now-unused
ActionSheet/`GetDeckOfferNames` name-list logic if nothing else uses it.

- [ ] **Step 2: "Continuar" path.** `RoguelikeHomeButton.OnMenuSelect` already calls
`RoguelikeFlow.OpenDeckSelect()` when `!IsDeckChosen()`; with Step 1 that now opens the new
screen. No change needed beyond confirming it compiles.

- [ ] **Step 3: Build client + full in-game verification.**
  - No `roguelike.json` → Home → ROGUELIKE → Nova Run → SoloMode screen, 3 deck tiles (art+name).
  - Tap tile → drawer → "Ver deck" shows the deck; "Selecionar" picks it → screen closes;
    `roguelike.json` has `deckChosen:true` + `deck`.
  - Reopen ROGUELIKE before choosing → "Continuar Run" reopens the screen.
- [ ] **Step 4: Commit.**
```bash
git add YgoMasterClient/Roguelike/RoguelikeFlow.cs YgoMasterClient/Roguelike/RoguelikeHomeButton.cs
git commit -m "feat(roguelike): route start/continue to the deck-select screen"
```

---

## Task 7 — Cleanup (remove `rgproof`) + final verification

**Files:** `YgoMasterClient/ConsoleHelper.cs` (remove `rgproof` + `rgdeck` cases),
delete `YgoMasterClient/Roguelike/RoguelikeUiProof.cs`, `YgoMasterClient.csproj`
(remove its include).

- [ ] **Step 1: Remove the throwaway spike.** Delete `RoguelikeUiProof.cs`; remove the
`rgproof` and `rgdeck` cases from `ConsoleHelper.cs`; remove the `RoguelikeUiProof.cs`
Compile include from `YgoMasterClient.csproj`. **Keep** `vcdump`/`vclog`/`rgpush` (reusable
dev tools).
- [ ] **Step 2: Build client + server.** Both must compile.
- [ ] **Step 3: Final DoD pass (in-game)** — repeat Task 6 Step 3 end-to-end; confirm normal
**Solo mode still works** (open Solo from Home → no roguelike customization leaks in: title
normal, all groups visible).
- [ ] **Step 4: Commit.**
```bash
git add YgoMasterClient/ConsoleHelper.cs YgoMasterClient.csproj
git rm YgoMasterClient/Roguelike/RoguelikeUiProof.cs
git commit -m "chore(roguelike): remove rgproof spike (M2.5 complete)"
```
- [ ] **Step 5: Mark M2.5 done** in `Docs/roguelike/roguelike-design.md` (add a "concluído"
note under the M2.5 section) and commit:
`git commit -am "docs(roguelike): mark M2.5 done (deck-select screen verified in-game)"`

---

## Self-review notes
- **Spec coverage:** SoloMode base + title/hide (T3); 3 tiles art+name+click (T4); drawer
  Ver deck/Selecionar + native viewer (T5); description field + enriched offers (T1); card-art
  helper (T2); start/continue routing, ActionSheet retired (T6); rgproof removed (T7).
- **Type consistency:** offers carry `name/bossCard/file/description/main/extra/side`
  everywhere (server T1 ↔ client `RoguelikeApi.DeckOffer` T4); `run.Deck` =
  `{name,bossCard,description,deck}`; `RoguelikeCardImage.AttachFill(parent,cid,name)` used by
  T4; `RoguelikeDeckSelectScreen.Open()` used by T6.
- **Honest gaps (resolve during execution, not faked):** (1) `SoloPortalViewController`
  namespace + the `NameText` binding (T3 Step 1 note); (2) clearing native tile onClick
  listeners (T4 Step 2 note); (3) standalone native deck viewer prefab/class/field, with an
  own-grid fallback (T5 Step 2). Each has a concrete discovery command (`vcdump`/`vclog`/grep)
  and a fallback — none are silent guesses.
- **House rule:** all new code in `Roguelike/`; only stock helpers reused; no `Goat/` code.
