# Roguelike M1 (Foundation) — Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development or
> superpowers:executing-plans to implement task-by-task. Steps use checkbox
> (`- [ ]`) syntax.

**Goal:** From the Home screen, a "Roguelike" button opens a custom overlay
(Nova Run / Continuar / Abandonar / Fechar) backed by **server-owned** run state
persisted in `roguelike.json`.

**Architecture:** Server owns run state + logic (`YgoMasterServer/Roguelike/`,
file `Players/<id>/roguelike.json`). Read path: server piggybacks the run state into
the home response → lands in client `ClientWork` at `$.Roguelike` → overlay reads it.
Write path (start/abandon): client triggers a `Roguelike.*` act (mechanism resolved in
Task 1). Client UI is a custom overlay (`YgoMasterClient/Roguelike/`).

**Tech stack:** C# (.NET Framework), MiniJSON, IL2CPP reflection
(`Assembler`/`IL2Class`/`IL2Method`/`IL2Field`), `Hook<T>` (MinHook), Unity wrappers
(`GameObject`/`Transform`/`UnityObject` in `DuelStarter.cs`), `_UnityAction` for button
callbacks, `ClientWork` (`GetByJsonPath`/`UpdateJson`/`DeleteByJsonPath`).

**Verification reality:** This is an IL2CPP mod injected into a running game — there are
**no unit tests**. "Verify" = build + launch game + read the console / observe behavior.
- Client build (auto-copies to install): `MSBuild YgoMasterClient.csproj -t:Build -p:Configuration=Release -v:minimal -nologo` (VS2022 Community MSBuild).
- Server: build the `YgoMaster` project, run `YgoMaster.exe` (the local server) before launching the game. (Confirm exact server build target in Task 0.)

---

## Task 0 — Module skeletons + registration (build green, no behavior)

**Files:**
- Create: `YgoMasterClient/Roguelike/RoguelikeMod.cs` (placeholder static class with a cctor that logs `[Roguelike] init`)
- Create: `YgoMasterServer/Roguelike/RoguelikeRun.cs` (empty class stub)
- Modify: `YgoMasterClient.csproj` (add `<Compile Include="YgoMasterClient\Roguelike\RoguelikeMod.cs" />`)
- Modify: `YgoMasterServer/*.csproj` (add the new server file if the csproj lists files explicitly; confirm)
- Modify: `YgoMasterClient/Program.cs` (register `typeof(RoguelikeMod)` in `nativeTypes`)

- [ ] Create the two folders + skeleton files (RoguelikeMod logs `[Roguelike] init` in its static cctor; RoguelikeRun empty).
- [ ] Add the `.csproj` Compile include(s); register `RoguelikeMod` in `Program.cs` nativeTypes.
- [ ] Build client + server. Confirm both compile.
- [ ] Run server + game → console shows `[Roguelike] init`. (Proves the module loads.)
- [ ] Commit: `feat(roguelike): M1 module skeletons + registration`

---

## Task 1 — Client→server send (mechanism RESOLVED ✅)

**Proven live via UnityExplorer:** `YgomSystem.Network.Request.Entry(string act,
Dictionary<string,Il2CppSystem.Object> params, float timeout)` issues any act on demand —
the server received `Roguelike.ping` ("Unhandled act Roguelike.ping"). In the mod, call it
via IL2 reflection and build the IL2CPP params dict with
`YgomMiniJSON.Json.Deserialize(MiniJSON.Json.Serialize(managedDict))`. Read the response in
the `RequestStructure.Complete` hook (`DuelStarter.cs` switch) from `$.Roguelike` in
ClientWork. Concrete `RoguelikeApi.Call` is built in Task 4; verify a mod-issued
`Roguelike.ping` logs `Req Roguelike.ping` server-side. (Investigation steps below are
superseded.)

**The mechanism (no longer unknown):**

**Investigation (do in order, stop when one works):**
- [ ] Read `YgomSystem.Network.API` (IL2CPP) — look for a GENERIC request method
  (something taking an act name + params dict). Inspect via `IL2Class` method dump:
  in `RoguelikeMod`, get `Assembler.GetAssembly("Assembly-CSharp").GetClass("API","YgomSystem.Network")` and log its method names/params. Look for `Send`/`Request`/`Command`/`Call`/per-act methods.
- [ ] Read `NetworkMain` + nested `RequestStructure` and `YgomSystem.Network.Request`
  (`Request.CommandEvent` is already hooked in `DuelStarter.cs:389`) — find how a request
  is built and committed.
- [ ] Fallback mechanism (proven to exist): the hijack/replace pattern —
  `DuelReplayUtils.OnNetworkEntry` swaps an outgoing command/params
  (`ReplaceNetworkEntry`). If no generic sender exists, trigger a benign game request and
  swap its act to `Roguelike.ping`.

**Server side (concrete):**
- [ ] In `YgoMasterServer/Roguelike/RoguelikeActs.cs`, add `Act_Ping(GameServerWebRequest r)` that sets `r.Response["Roguelike"] = new Dictionary<string,object>{ {"pong", true} };`
- [ ] In `GameServer.cs` dispatch switch (near `case "User.set_profile":`), add
  `case "Roguelike.ping": RoguelikeActs.Act_Ping(gameServerWebRequest); break;` (or route
  all `Roguelike.*` to a single `RoguelikeActs.Handle`).

**Client side:**
- [ ] Using whichever mechanism worked, issue `Roguelike.ping`; in the
  `RequestStructure.Complete` hook (`DuelStarter.cs:419` switch) OR a new completion hook,
  add `case "Roguelike.ping":` that reads `ClientWork.GetByJsonPath<bool>("Roguelike.pong")` and logs it.

- [ ] **Success criterion:** a temporary trigger (e.g. a key press or temp button) causes
  the console to log `[Roguelike] pong=True`. This proves the full client→server→client loop.
- [ ] Document the chosen mechanism at the top of `RoguelikeApi.cs` (created in Task 4).
- [ ] Commit: `feat(roguelike): spike — custom act round-trip (Roguelike.ping)`

> If neither mechanism works cleanly, STOP and report — it changes the architecture
> (may force client-owned persistence). Do not fake it.

---

## Task 2 — Server: `RoguelikeRun` model + `roguelike.json` load/save

**Files:** `YgoMasterServer/Roguelike/RoguelikeRun.cs`

- [ ] Implement the model + (de)serialization + file IO. Use `MiniJSON.Json` and the
  player dir from `GetPlayerDirectory(player)` (`GameServer.State.cs`).

```csharp
// YgoMasterServer/Roguelike/RoguelikeRun.cs
using System;
using System.Collections.Generic;
using System.IO;

namespace YgoMaster
{
    class RoguelikeRun
    {
        public int Version = 1;
        public bool Active;
        public string GameType = "base_deck";
        public long Seed;
        public string CreatedAt;

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "version", Version }, { "active", Active }, { "gameType", GameType },
                { "seed", Seed }, { "createdAt", CreatedAt },
            };
        }

        public static RoguelikeRun FromDictionary(Dictionary<string, object> d)
        {
            if (d == null) return new RoguelikeRun { Active = false };
            return new RoguelikeRun
            {
                Version  = Utils.GetValue<int>(d, "version", 1),
                Active   = Utils.GetValue<bool>(d, "active", false),
                GameType = Utils.GetValue<string>(d, "gameType", "base_deck"),
                Seed     = Utils.GetValue<long>(d, "seed", 0),
                CreatedAt= Utils.GetValue<string>(d, "createdAt", null),
            };
        }
    }
}
```

- [ ] Add static load/save helpers (path = `<playerDir>/roguelike.json`). These take the
  player dir as a string so they don't depend on GameServer internals:

```csharp
public static string PathFor(string playerDir) => Path.Combine(playerDir, "roguelike.json");

public static RoguelikeRun Load(string playerDir)
{
    string p = PathFor(playerDir);
    if (!File.Exists(p)) return new RoguelikeRun { Active = false };
    var d = MiniJSON.Json.DeserializeStripped(File.ReadAllText(p)) as Dictionary<string, object>;
    return FromDictionary(d);
}

public void Save(string playerDir)
{
    File.WriteAllText(PathFor(playerDir),
        MiniJSON.Json.Format(MiniJSON.Json.Serialize(ToDictionary())));
}

public static void Delete(string playerDir)
{
    string p = PathFor(playerDir);
    if (File.Exists(p)) File.Delete(p);
}
```

- [ ] Build server. Confirm compiles.
- [ ] Commit: `feat(roguelike): server RoguelikeRun model + roguelike.json IO`

---

## Task 3 — Server: `RoguelikeActs` (get_state via home piggyback + start_run + abandon_run)

**Files:** `YgoMasterServer/Roguelike/RoguelikeActs.cs`, modify `Acts/Act_User.cs`
(home piggyback), `GameServer.cs` (dispatch).

`RoguelikeActs` needs the player dir; expose it via a method on the partial `GameServer`
or pass `GetPlayerDirectory(player)`. Keep handlers as `void Act_*(GameServerWebRequest)`
methods on a `GameServer` partial in `Roguelike/` (so they can call `GetPlayerDirectory`
and `SavePlayer`).

- [ ] Create `YgoMasterServer/Roguelike/GameServer.Roguelike.cs` (partial class GameServer):

```csharp
// YgoMasterServer/Roguelike/GameServer.Roguelike.cs
using System;
using System.Collections.Generic;

namespace YgoMaster
{
    partial class GameServer
    {
        void WriteRoguelikeState(GameServerWebRequest r)
        {
            RoguelikeRun run = RoguelikeRun.Load(GetPlayerDirectory(r.Player));
            r.Response["Roguelike"] = run.ToDictionary();
            r.Remove("Roguelike");
        }

        void Act_RoguelikeStartRun(GameServerWebRequest r)
        {
            var run = new RoguelikeRun
            {
                Active = true,
                GameType = Utils.GetValue<string>(r.ActParams, "gameType", "base_deck"),
                Seed = new Random().Next(),
                CreatedAt = DateTime.UtcNow.ToString("o"),
            };
            run.Save(GetPlayerDirectory(r.Player));
            r.Response["Roguelike"] = run.ToDictionary();
            r.Remove("Roguelike");
        }

        void Act_RoguelikeAbandonRun(GameServerWebRequest r)
        {
            RoguelikeRun.Delete(GetPlayerDirectory(r.Player));
            r.Response["Roguelike"] = new RoguelikeRun { Active = false }.ToDictionary();
            r.Remove("Roguelike");
        }
    }
}
```

- [ ] In `GameServer.cs` dispatch switch add:
  `case "Roguelike.start_run": Act_RoguelikeStartRun(gameServerWebRequest); break;`
  `case "Roguelike.abandon_run": Act_RoguelikeAbandonRun(gameServerWebRequest); break;`
- [ ] In `Acts/Act_User.cs` `Act_UserHome`, before its `request.Remove(...)`, add a call:
  `WriteRoguelikeState(request);` so `$.Roguelike` is populated on every home load.
- [ ] Build server. Confirm compiles.
- [ ] Verify: run server+game, go to Home → (after Task 4's reader, or temporarily log
  server-side) confirm `roguelike.json` read path executes without error.
- [ ] Commit: `feat(roguelike): server acts — get_state (home piggyback) + start/abandon`

---

## Task 4 — Client: `RoguelikeApi` (read `$.Roguelike` + call mutations)

**Files:** `YgoMasterClient/Roguelike/RoguelikeApi.cs`

- [ ] Implement `GetState()` reading ClientWork, and `StartRun()`/`AbandonRun()` using the
  Task-1 mechanism.

```csharp
// state read (concrete — ClientWork is already populated by the home response)
public static bool IsRunActive()
    => YgomSystem.Utility.ClientWork.GetByJsonPath<bool>("Roguelike.active");
```

- [ ] `StartRun()` / `AbandonRun()` issue `Roguelike.start_run` / `Roguelike.abandon_run`
  via the mechanism proven in Task 1, and refresh `$.Roguelike` from the response.
- [ ] Build client. Verify (temp trigger): `IsRunActive()` reflects server state after
  start/abandon.
- [ ] Commit: `feat(roguelike): client RoguelikeApi (state read + start/abandon)`

---

## Task 5 — Client: Home "Roguelike" button

**Files:** `YgoMasterClient/Roguelike/RoguelikeHomeButton.cs`, register in `Program.cs` +
`.csproj`.

- [ ] Hook `HomeViewController.UpdateHome` (pattern from `HomeViewTweaks.cs`). After
  `Original`, get the home GameObject (`Component.GetGameObject(thisPtr)`).
- [ ] **Runtime discovery:** call `GameObject.Dump(homeObj)` once and log it to find a
  good button to clone + the parent container path (the menu/banner area). Record the
  chosen paths in a comment. (Cannot be hardcoded blind — discover via Dump.)
- [ ] Clone the chosen button (`UnityObject.Instantiate(template, parent)`),
  `SetName("ButtonRoguelike")`, set its label (TMP text component — find via
  `GetComponent` + the TMP_Text setter), wire `onClick` via
  `_UnityAction.CreateUnityAction(OnClick)`. Idempotent (skip if already present).
- [ ] `OnClick` → `RoguelikeEntryOverlay.Open()` (Task 6).
- [ ] Build + verify: button appears on Home; clicking logs `[Roguelike] button clicked`.
- [ ] Commit: `feat(roguelike): Home Roguelike button`

---

## Task 6 — Client: Entry overlay (Nova / Continuar / Abandonar / Fechar)

**Files:** `YgoMasterClient/Roguelike/RoguelikeEntryOverlay.cs`, register.

- [ ] Build a root GameObject parented to the home canvas (discover the canvas via
  Dump/`GetRootObject`). Add a background + title + 4 buttons (clone existing buttons;
  set labels; wire `onClick` via `_UnityAction`). Position via `RectTransform` setters
  (pattern from `SoloGateGridLayout.cs`).
- [ ] On `Open()`: read `RoguelikeApi.IsRunActive()` → enable/disable Continuar +
  Abandonar.
- [ ] Wire buttons:
  - Nova Run → if active: `CommonDialogViewController.OpenYesNoConfirmationDialog(...)`
    then `RoguelikeApi.StartRun()`; else `StartRun()` directly. Then toast "Run criada".
  - Continuar → toast "abrindo run…" (M1 stub).
  - Abandonar → `OpenYesNoConfirmationDialog` → `RoguelikeApi.AbandonRun()` → refresh.
  - Fechar → destroy/hide the overlay root.
- [ ] Manage lifecycle: destroy the overlay on close; rebuild on `Open()`.
- [ ] Build + verify behavior.
- [ ] Commit: `feat(roguelike): entry overlay (new/continue/abandon/close)`

---

## Task 7 — End-to-end verification (Definition of Done)

- [ ] Fresh state (no `roguelike.json`): Home → Roguelike → overlay shows Continuar +
  Abandonar **disabled**.
- [ ] Nova Run → `roguelike.json` created on disk with `active:true`.
- [ ] Reopen overlay → Continuar + Abandonar **enabled** (read from `$.Roguelike`).
- [ ] Abandonar (confirm) → `roguelike.json` removed; overlay back to disabled state.
- [ ] Commit any final fixups: `chore(roguelike): M1 verification fixups`

---

## Self-review notes
- **Spec coverage:** model+persistence (T2), 3 acts incl. abandon (T3), home button (T5),
  overlay (T6), client API (T4), end-to-end DoD (T7). Spike for the client→server trigger
  (T1) — the design's flagged risk.
- **Honest gaps (resolved during execution, not faked):** (1) the act-trigger mechanism
  (T1 spike); (2) exact prefab paths / clone templates for the button + overlay (discovered
  via `GameObject.Dump()` in T5/T6); (3) exact server csproj include + server build target
  (T0).
