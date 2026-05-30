# Roguelike M2 (Deck Select) — Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development or
> superpowers:executing-plans to implement task-by-task. Steps use checkbox
> (`- [ ]`) syntax.

**Goal:** Starting a `base_deck` run rolls **3 random decks** server-side; the player
**picks 1**, which is persisted as the run's deck in `roguelike.json`.

**Architecture:** Server owns the roll + choice (`YgoMasterServer/Roguelike/`). The deck
pool is **our own** `DataLE/Roguelike/StartingDecks/*.json` (player format), loaded by a
**Roguelike-owned** loader `RoguelikeDeckPool` (inspired by Goat's `DeckPoolLoader` but
NOT reusing it — house rule: no `Goat/` code). The client reacts to the `start_run`
response in the `RequestStructure.Complete` hook (`DuelStarter.cs`) and opens an
**ActionSheet** with the 3 deck names; tapping one issues `choose_deck`. No card art yet
(deferred to M3 map UI).

**House rule (this milestone):** do not reuse any code under `Goat/` (server or client).
Allowed: stock YgoMaster code (`MiniJSON`, `Utils`, `DeckInfo`, the `GameServer` base,
the `DuelStarter` network-complete hook, game `YgomGame.*` classes) and our own
`Roguelike/` code. May take inspiration from `Goat/` and reimplement in `Roguelike/`.

**Tech stack:** C# (.NET Framework), MiniJSON, `RoguelikeDeckPool` (ours), IL2CPP
reflection, `Request.Entry`, `ClientWork` (`GetByJsonPath`/`SerializePath`),
`ActionSheetViewController`, `CommonDialogViewController`.

**Verification reality:** IL2CPP mod injected into a running game — **no unit tests**.
"Verify" = build + launch server + game + read console / observe behavior.
- Client build (auto-copies to install): `MSBuild YgoMasterClient.csproj -t:Build -p:Configuration=Release -v:minimal -nologo`.
- Server build: `MSBuild YgoMasterServer/YgoMaster.csproj -t:Build -p:Configuration=Release -p:Platform=x64 -v:minimal -nologo`. Add `-p:GoatInstallDir=""` to skip the install-copy if `YgoMaster.exe` is running/locked.

**Reused facts (already verified this milestone):**
- `dataDirectory` is a field on `GameServer` (GameServer.State.cs:28); at runtime it is
  `<install>/DataLE` (per `DataDir.txt`). Act handlers in the `partial class GameServer`
  (`Roguelike/GameServer.Roguelike.cs`) can use it directly.
- Deck pool lives at `<dataDirectory>/Roguelike/StartingDecks/*.json` (player format:
  `{name, m:{ids,r}, e:{ids,r}, s:{ids,r}, ...}`). We add a Roguelike-owned loader
  `RoguelikeDeckPool` (Task 2) — do NOT use Goat's `DeckPoolLoader`.
- `GetPlayerDirectory(player)` returns the player dir (where `roguelike.json` lives).
- Client completion hook: `DuelStarter.cs` `Complete(thisPtr)` switch (~line 419), with a
  `ProfileReplayViewController.OnNetworkComplete(thisPtr, cmd)` call right after (~line 442).
- `ClientWork.SerializePath("Roguelike.deckOffers")` returns the subtree as a JSON string
  (same call used for `"Duel"` at DuelStarter.cs:425) → `MiniJSON.Json.Deserialize` to a list.
- M1 menu lives in `RoguelikeHomeButton.OnMenuSelect`; `RoguelikeApi.Call(act, args)`
  already issues acts via `Request.Entry`.
- Existing dispatch cases in `GameServer.cs` (~lines 484-489): `Roguelike.start_run`,
  `Roguelike.abandon_run`.

---

## Task 1 — Server: extend `RoguelikeRun` model ✅ DONE

Added `DeckChosen` (bool), `DeckOffers` (`List<object>` of `{name,bossCard,file}`), `Deck`
(`Dictionary<string,object>` or null) to `RoguelikeRun.cs`, round-tripped through
`ToDictionary`/`FromDictionary`. Built + committed (`5ad89ea`).

---

## Task 2 — Server: `RoguelikeDeckPool` loader + `StartingDecks` data

**Files:** Create `YgoMasterServer/Roguelike/RoguelikeDeckPool.cs`; modify
`YgoMasterServer/YgoMaster.csproj` (Compile include); create deck data under
`<install>/DataLE/Roguelike/StartingDecks/` (data, not in git).

- [ ] Create the loader. Uses only stock code (`MiniJSON`, `Utils`). Display name = file
  name (clean curation); boss card = `boss_card` field or first main id.

```csharp
// YgoMasterServer/Roguelike/RoguelikeDeckPool.cs
using System;
using System.Collections.Generic;
using System.IO;

namespace YgoMaster
{
    // Roguelike-owned starter-deck loader. Reads player-format deck JSONs from
    // DataLE/Roguelike/StartingDecks. Display name = file name; boss card = `boss_card`
    // field or the first main-deck id. (Inspired by Goat's DeckPoolLoader; not reused.)
    static class RoguelikeDeckPool
    {
        public class StarterDeck
        {
            public string Name;                       // file name (no extension)
            public int BossCard;
            public Dictionary<string, object> Json;   // raw player-format deck dict
        }

        // All .json files under <dataDirectory>/Roguelike/StartingDecks (full paths).
        public static List<string> ListFiles(string dataDirectory)
        {
            string dir = Path.Combine(dataDirectory, "Roguelike", "StartingDecks");
            if (!Directory.Exists(dir)) return new List<string>();
            return new List<string>(Directory.GetFiles(dir, "*.json"));
        }

        public static StarterDeck LoadOne(string fullPath)
        {
            Dictionary<string, object> doc = MiniJSON.Json.DeserializeStripped(
                File.ReadAllText(fullPath)) as Dictionary<string, object>;
            if (doc == null) return null;
            return new StarterDeck
            {
                Name = Path.GetFileNameWithoutExtension(fullPath),
                BossCard = ResolveBossCard(doc),
                Json = doc,
            };
        }

        static int ResolveBossCard(Dictionary<string, object> doc)
        {
            int explicitId = Utils.GetValue<int>(doc, "boss_card");
            if (explicitId > 0) return explicitId;
            Dictionary<string, object> main = Utils.GetValue<Dictionary<string, object>>(doc, "m");
            List<object> ids = main != null ? Utils.GetValue<List<object>>(main, "ids") : null;
            if (ids == null || ids.Count == 0) return 0;
            try { return Convert.ToInt32(ids[0]); } catch { return 0; }
        }
    }
}
```

- [ ] Add to `YgoMasterServer/YgoMaster.csproj` near the other `Roguelike\*` includes:
  `<Compile Include="Roguelike\RoguelikeDeckPool.cs" />`
- [ ] Create the data folder `<install>/DataLE/Roguelike/StartingDecks/` and seed it with a
  handful of valid player-format starter decks (so "choose 1 of 3" has variety). These are
  install-side data (not tracked in git, like the other deck folders). The controller
  seeds these directly; the user can curate/replace afterwards (drop a player-format JSON
  in the folder, file name = display name).
- [ ] Build server. Expected: build succeeds.
- [ ] Commit (code only): `feat(roguelike): starter-deck pool loader (RoguelikeDeckPool)`

---

## Task 3 — Server: roll 3 offers in `start_run` + new `choose_deck` act + dispatch

**Files:** `YgoMasterServer/Roguelike/GameServer.Roguelike.cs`, modify
`YgoMasterServer/GameServer.cs` (dispatch).

- [ ] Add a private roll helper to the partial. Lists starter files via
  `RoguelikeDeckPool.ListFiles`, picks up to 3 distinct with a seeded RNG, loads each
  (name + bossCard). The stored `file` is **relative** to `dataDirectory`.

```csharp
List<object> RollDeckOffers(int seed, int count)
{
    List<string> files = RoguelikeDeckPool.ListFiles(dataDirectory);
    List<object> offers = new List<object>();
    if (files.Count == 0)
    {
        Console.WriteLine("[Roguelike] no starter decks in Roguelike/StartingDecks");
        return offers;
    }
    Random rng = new Random(seed);
    for (int i = files.Count - 1; i > 0; i--) // Fisher-Yates
    {
        int j = rng.Next(i + 1);
        string tmp = files[i]; files[i] = files[j]; files[j] = tmp;
    }
    int take = Math.Min(count, files.Count);
    for (int i = 0; i < take; i++)
    {
        try
        {
            RoguelikeDeckPool.StarterDeck d = RoguelikeDeckPool.LoadOne(files[i]);
            if (d == null) continue;
            string rel = files[i].StartsWith(dataDirectory)
                ? files[i].Substring(dataDirectory.Length).TrimStart('\\', '/')
                : files[i];
            offers.Add(new Dictionary<string, object>
            {
                { "name", d.Name }, { "bossCard", d.BossCard }, { "file", rel },
            });
        }
        catch (Exception ex) { Console.WriteLine("[Roguelike] offer load EX: " + ex.Message); }
    }
    return offers;
}
```

- [ ] Evolve `Act_RoguelikeStartRun` to set `DeckChosen=false` and roll offers from the new
  run's seed (replace the existing method body):

```csharp
void Act_RoguelikeStartRun(GameServerWebRequest request)
{
    string gameType = "base_deck";
    if (request.ActParams != null)
        gameType = Utils.GetValue<string>(request.ActParams, "gameType", "base_deck");
    int seed = new Random().Next();
    RoguelikeRun run = new RoguelikeRun
    {
        Active = true,
        GameType = gameType,
        Seed = seed,
        CreatedAt = DateTime.UtcNow.ToString("o"),
        DeckChosen = false,
        DeckOffers = RollDeckOffers(seed, 3),
        Deck = null,
    };
    run.Save(GetPlayerDirectory(request.Player));
    request.Response["Roguelike"] = run.ToDictionary();
    request.Remove("Roguelike");
}
```

- [ ] Add `Act_RoguelikeChooseDeck` (new). Validates index + pending state, loads the chosen
  offer's file, stores it as the run's deck (player format), marks chosen, clears offers:

```csharp
void Act_RoguelikeChooseDeck(GameServerWebRequest request)
{
    RoguelikeRun run = RoguelikeRun.Load(GetPlayerDirectory(request.Player));
    if (run.Active && !run.DeckChosen && run.DeckOffers != null)
    {
        int index = request.ActParams != null ? Utils.GetValue<int>(request.ActParams, "index", -1) : -1;
        if (index >= 0 && index < run.DeckOffers.Count)
        {
            Dictionary<string, object> offer = run.DeckOffers[index] as Dictionary<string, object>;
            string rel = offer != null ? Utils.GetValue<string>(offer, "file") : null;
            if (!string.IsNullOrEmpty(rel))
            {
                RoguelikeDeckPool.StarterDeck d =
                    RoguelikeDeckPool.LoadOne(System.IO.Path.Combine(dataDirectory, rel));
                if (d != null)
                {
                    run.Deck = new Dictionary<string, object>
                    {
                        { "name", d.Name }, { "bossCard", d.BossCard }, { "deck", d.Json },
                    };
                    run.DeckChosen = true;
                    run.DeckOffers = new List<object>();
                    run.Save(GetPlayerDirectory(request.Player));
                }
            }
        }
    }
    request.Response["Roguelike"] = run.ToDictionary();
    request.Remove("Roguelike");
}
```

- [ ] In `GameServer.cs` dispatch switch, add right after the `Roguelike.abandon_run` case
  (~line 489):
  `case "Roguelike.choose_deck": Act_RoguelikeChooseDeck(gameServerWebRequest); break;`
- [ ] Build server. Expected: build succeeds.
- [ ] Commit: `feat(roguelike): roll 3 deck offers on start + choose_deck act`

---

## Task 4 — Client: `RoguelikeApi` additions (deckChosen / offer names / choose_deck)

**Files:** `YgoMasterClient/Roguelike/RoguelikeApi.cs`

- [ ] Add three members. `GetDeckOfferNames` reads the offers subtree via
  `ClientWork.SerializePath` and extracts each `name`:

```csharp
// True when the player has finalized a deck for the active run.
public static bool IsDeckChosen()
{
    return YgomSystem.Utility.ClientWork.GetByJsonPath<bool>("Roguelike.deckChosen");
}

// Names of the 3 (or fewer) decks offered for the pending run.
public static string[] GetDeckOfferNames()
{
    try
    {
        string json = YgomSystem.Utility.ClientWork.SerializePath("Roguelike.deckOffers");
        if (string.IsNullOrEmpty(json)) return new string[0];
        List<object> list = MiniJSON.Json.Deserialize(json) as List<object>;
        if (list == null) return new string[0];
        List<string> names = new List<string>();
        foreach (object o in list)
        {
            Dictionary<string, object> item = o as Dictionary<string, object>;
            names.Add(item != null && item.ContainsKey("name") ? Convert.ToString(item["name"]) : "Deck");
        }
        return names.ToArray();
    }
    catch (Exception ex) { Console.WriteLine("[Roguelike] offer names EX: " + ex); return new string[0]; }
}

public static void ChooseDeck(int index)
{
    Call("Roguelike.choose_deck", new Dictionary<string, object> { { "index", index } });
}
```

- [ ] Build client. Expected: build succeeds.
- [ ] Commit: `feat(roguelike): client api — deckChosen, offer names, choose_deck`

---

## Task 5 — Client: deck-select reaction (`RoguelikeFlow`) + completion hook + menu wiring

**Files:** Create `YgoMasterClient/Roguelike/RoguelikeFlow.cs`; modify
`YgoMasterClient/DuelStarter.cs` (one call in `Complete`), `YgoMasterClient/Roguelike/RoguelikeHomeButton.cs`
(menu wiring), `YgoMasterClient.csproj` (Compile include).

- [ ] Create `RoguelikeFlow.cs`: opens the deck-select ActionSheet (3 deck names) and
  reacts to act completions. Mirrors how `RoguelikeHomeButton` already calls
  `ActionSheetViewController` / `CommonDialogViewController` / `RoguelikeApi`.

```csharp
using System;

namespace YgoMasterClient
{
    // Run-flow reactions: opens the deck-select ActionSheet (3 deck names) and handles
    // the async responses of Roguelike acts (driven from DuelStarter's Complete hook).
    static class RoguelikeFlow
    {
        // Open the "choose 1 of 3" sheet from the current $.Roguelike offers.
        public static void OpenDeckSelect()
        {
            string[] names = RoguelikeApi.GetDeckOfferNames();
            if (names.Length == 0)
            {
                YgomGame.Menu.CommonDialogViewController.OpenAlertDialog("Roguelike",
                    "Nenhum deck disponivel no pool.", () => { });
                return;
            }
            YgomGame.Menu.ActionSheetViewController.Open("Escolha seu deck", names, OnDeckPicked);
        }

        static void OnDeckPicked(IntPtr ctx, int index)
        {
            RoguelikeApi.ChooseDeck(index);
        }

        // Called from DuelStarter.Complete for every completed network command.
        public static void OnNetworkComplete(string cmd)
        {
            if (cmd == "Roguelike.start_run")
            {
                OpenDeckSelect();
            }
            else if (cmd == "Roguelike.choose_deck")
            {
                string name = YgomSystem.Utility.ClientWork.GetByJsonPath<string>("Roguelike.deck.name");
                YgomGame.Menu.CommonDialogViewController.OpenAlertDialog("Roguelike",
                    string.IsNullOrEmpty(name) ? "Deck escolhido!" : ("Deck escolhido: " + name), () => { });
            }
        }
    }
}
```

- [ ] In `DuelStarter.cs` `Complete(thisPtr)`, add the Roguelike reaction right after the
  existing `ProfileReplayViewController.OnNetworkComplete(thisPtr, cmd);` line (~442):
  `YgoMasterClient.RoguelikeFlow.OnNetworkComplete(cmd);`
- [ ] Update `RoguelikeHomeButton.OnMenuSelect` so "Nova Run" only fires `start_run` (the
  sheet now comes from the completion), and "Continuar" resumes the selection when the deck
  is not yet chosen:

```csharp
static void OnMenuSelect(IntPtr ctx, int index)
{
    if (_menuRunActive)
    {
        if (index == 0) // Continuar Run
        {
            if (!RoguelikeApi.IsDeckChosen())
                RoguelikeFlow.OpenDeckSelect();
            else
                YgomGame.Menu.CommonDialogViewController.OpenAlertDialog("Roguelike",
                    "Run em andamento — o mapa vem no proximo milestone.", () => { });
        }
        else if (index == 1) // Abandonar Run
        {
            YgomGame.Menu.CommonDialogViewController.OpenYesNoConfirmationDialog("Abandonar Run",
                "Tem certeza? A run atual sera perdida.", () => { RoguelikeApi.AbandonRun(); }, () => { });
        }
    }
    else if (index == 0) // Nova Run
    {
        RoguelikeApi.StartRun(); // deck-select sheet opens on start_run completion
    }
}
```

- [ ] Add to `YgoMasterClient.csproj` (near the other `Roguelike\*` includes):
  `<Compile Include="Roguelike\RoguelikeFlow.cs" />`
- [ ] Build client. Expected: build succeeds.
- [ ] Commit: `feat(roguelike): deck-select sheet on start + choose wiring`

---

## Task 6 — End-to-end verification (Definition of Done)

Run the server (`YgoMaster.exe`), then launch the game. Use a player with **no**
`roguelike.json` (or abandon any existing run first).

- [ ] Home → ROGUELIKE → "Nova Run" → an ActionSheet titled "Escolha seu deck" lists 3
  deck names (from `Roguelike/StartingDecks`).
- [ ] `roguelike.json` now has `active:true`, `deckChosen:false`, `deckOffers` with 3 items.
- [ ] Pick a deck → toast "Deck escolhido: <name>"; `roguelike.json` now has
  `deckChosen:true`, `deck` populated (name/bossCard/deck), `deckOffers` empty.
- [ ] Reopen ROGUELIKE before choosing (start a fresh run, dismiss the sheet) → "Continuar
  Run" reopens the selection.
- [ ] After choosing, "Continuar Run" shows the "Run em andamento" toast.
- [ ] "Abandonar Run" (confirm) → `roguelike.json` removed.
- [ ] Commit any fixups: `chore(roguelike): M2 verification fixups`

---

## Self-review notes
- **Spec coverage:** model (T1 ✅), loader + starter data (T2), roll + choose act +
  dispatch (T3), client api (T4), deck-select sheet + completion reaction + menu wiring
  (T5), end-to-end DoD (T6).
- **House rule honored:** no `Goat/` code reused; `RoguelikeDeckPool` is ours (stock
  `MiniJSON`/`Utils` only); pool is `DataLE/Roguelike/StartingDecks`.
- **Type consistency:** `DeckOffers` is `List<object>` of `Dictionary<string,object>`
  everywhere; `Deck` is `Dictionary<string,object>` with `{name, bossCard, deck}` (deck =
  player-format `m/e/s`); client reads `Roguelike.deckChosen` (bool), `Roguelike.deckOffers`
  (list, via `SerializePath`), `Roguelike.deck.name` (string).
- **Honest gaps (resolve during execution, not faked):** (1) confirm `MiniJSON.Json`
  (managed) is the right namespace in `RoguelikeApi.cs` (DuelStarter.cs uses it at :425);
  (2) confirm `ActionSheetViewController.Open` signature matches the M1 usage in
  `RoguelikeHomeButton` (it does — reused).
