# M5 — Actions System Implementation Plan

> **For agentic workers:** implement task-by-task. Build to verify (no unit-test
> framework in this IL2CPP mod); each task ends with a build + an in-game check.
> Client build: `MSBuild YgoMasterRogueLike\YgoMasterClient.csproj`. Server build:
> the `YgoMaster` project (`YgoMasterRogueLike\YgoMasterServer\YgoMaster.csproj`).

**Goal:** A server-authoritative "action" system — a recursive JSON action tree the
server walks, pausing to prompt the client for UI choices, applying state itself.
v1 ships the framework + the `options` action type (nested dialogs) + a terminal
`message` leaf, drivable end-to-end by a `rgencounter <ID>` console command that
runs that encounter's `action` through the real engine.

**Architecture:** The server owns the action tree and a per-run cursor
(`RoguelikeRun.PendingAction` + `ActionToken`). `RoguelikeActionEngine` advances the
cursor; when it reaches a UI node (`options`/`message`) it stops and `WriteRun`
projects a thin prompt into `$.Roguelike.action`. The client `RoguelikeActionDriver`
reads that prompt, renders it (ActionSheet for `options`, CommonDialog for
`message`), and sends the choice back via `Roguelike.action_respond`, which resumes
the engine. The loop self-drives because every response flows through
`RoguelikeFlow.OnNetworkComplete`, which calls `Pump()`. Triggers: a combat
encounter's `action` fires after a win (`Act_RoguelikeDuelResult`); a non-combat
node's encounter `action` fires on arrival (`Act_RoguelikeMove`).

**Tech stack:** C# server (plain), C# client (IL2CPP via reflection helpers
`IL2Class/IL2Method/IL2Property`, `Hook<T>`), JSON via `MiniJSON`, wire via
`YgomSystem.Network.Request.Entry` + `$.Roguelike` (ClientWork piggyback).

**Action tree shape (JSON, lives in an encounter's `action`):**
```json
{
  "type": "options",
  "text": "Um viajante oferece uma escolha…",
  "options": [
    { "label": "Abrir o baú", "action": { "type": "options", "text": "…", "options": [
        { "label": "Esquerdo", "action": { "type": "message", "text": "Você pegou o esquerdo." } },
        { "label": "Direito",  "action": { "type": "message", "text": "Você pegou o direito." } } ] } },
    { "label": "Seguir",      "action": { "type": "message", "text": "Você seguiu sem abrir." } }
  ]
}
```
- `options`: branch — player picks one `options[i]`, engine descends into its `action`.
- `message`: terminal — show text, single OK, then the action completes.
- `option.action` may be absent/null → that choice ends the action (no-op).

**Wire projection (`$.Roguelike.action`, server→client; absent when no prompt):**
```json
{ "token": 3, "type": "options", "text": "…", "options": ["Abrir o baú", "Seguir"] }
{ "token": 4, "type": "message", "text": "Você seguiu sem abrir." }
```
The full tree is NEVER sent — only `type`, `text`, and (for `options`) the labels.

**Out of v1 (note only, do not build):** state-mutating leaves (`addCard`,
`randomCard`, `modifyLp`, `gems`, `openpack` with a pick-N grid), a `sequence` type
(would need a frame stack instead of the single cursor), and real `event`-node
content. v1 leaves are `message` only.

**New files**
- `YgoMasterServer\Roguelike\Actions\RoguelikeActionEngine.cs`
- `YgoMasterClient\Roguelike\Actions\RoguelikeActionDriver.cs`

**Modified files**
- `YgoMasterServer\Roguelike\RoguelikeEncounters.cs` (Encounter.Action)
- `YgoMasterServer\Roguelike\RoguelikeRun.cs` (PendingAction/ActionToken)
- `YgoMasterServer\Roguelike\GameServer.Roguelike.cs` (acts + WriteRun projection + triggers)
- `YgoMasterServer\GameServer.cs` (2 switch cases)
- `YgoMasterServer\YgoMaster.csproj` (1 include)
- `YgoMasterClient\Roguelike\RoguelikeApi.cs` (RunEncounterAction/ActionRespond/GetActionPrompt)
- `YgoMasterClient\Roguelike\RoguelikeFlow.cs` (Pump at end of OnNetworkComplete)
- `YgoMasterClient\ConsoleHelper.cs` (rgencounter case)
- `YgoMasterClient.csproj` (1 include)
- `DataLE\Roguelike\Encounters.json` (install data — seed a test `action`; no repo copy, same as Labels.json)

---

## Task 1 — Server: `Encounter.Action` schema

**Files:** Modify `YgoMasterServer\Roguelike\RoguelikeEncounters.cs`

- [ ] **Step 1:** Add a field to `Encounter` (after `Modifiers`, ~line 35):
```csharp
public Dictionary<string, object> Action; // action tree fired after this encounter (null = none)
```
- [ ] **Step 2:** Parse it in `Parse` (in the `new Encounter { … }` initializer, ~line 90, next to `Modifiers = …`):
```csharp
Action = Utils.GetValue<Dictionary<string, object>>(d, "action"),
```
- [ ] **Step 3:** Build server; expect no errors. (Field is unused until Task 6.)

---

## Task 2 — Server: run cursor state

**Files:** Modify `YgoMasterServer\Roguelike\RoguelikeRun.cs`

- [ ] **Step 1:** Add fields (after `Won`, ~line 28):
```csharp
public Dictionary<string, object> PendingAction; // current action-tree node awaiting resolution, or null
public int ActionToken;                          // bumps each time a new prompt is presented (client dedup)
```
- [ ] **Step 2:** Persist in `ToDictionary` (add to the dictionary literal, after `"won"`):
```csharp
{ "pendingAction", PendingAction },
{ "actionToken", ActionToken },
```
  > `WriteRun` (Task 5) strips `pendingAction` off the wire and injects the thin
  > `action` projection; the raw tree is persisted to disk only (survives between
  > requests, since each act reloads the run).
- [ ] **Step 3:** Read in `FromDictionary` (add to the `new RoguelikeRun { … }`):
```csharp
PendingAction = Utils.GetValue<Dictionary<string, object>>(d, "pendingAction"),
ActionToken   = Utils.GetValue<int>(d, "actionToken", 0),
```
- [ ] **Step 4:** Build server; expect no errors.

---

## Task 3 — Server: `RoguelikeActionEngine`

**Files:** Create `YgoMasterServer\Roguelike\Actions\RoguelikeActionEngine.cs`

- [ ] **Step 1:** Write the engine. It mutates only `run.PendingAction` /
  `run.ActionToken` for v1 (leaf application comes in v2). Caller saves the run.
```csharp
using System.Collections.Generic;

namespace YgoMaster
{
    // Server-authoritative walker over an action tree. v1 understands two node types:
    //   options : { type, text, options:[ { label, action } ] }  -> branch (await a choice)
    //   message : { type, text }                                 -> terminal (await OK)
    // The cursor (run.PendingAction) is the node currently presented; ActionToken bumps on
    // each new prompt so the client renders it once. Project() builds the thin wire payload.
    static class RoguelikeActionEngine
    {
        // Begin an action: set the root as the cursor, then settle on the first UI node.
        public static void Start(RoguelikeRun run, Dictionary<string, object> action)
        {
            SetPending(run, action);
            Step(run);
        }

        // Resolve the current prompt with the player's choice, then settle on the next UI node.
        public static void Respond(RoguelikeRun run, int choice)
        {
            Dictionary<string, object> cur = run.PendingAction;
            if (cur == null) return;
            string type = Utils.GetValue<string>(cur, "type", "");
            if (type == "options")
            {
                List<object> opts = Utils.GetValue<List<object>>(cur, "options");
                Dictionary<string, object> chosen = (opts != null && choice >= 0 && choice < opts.Count)
                    ? opts[choice] as Dictionary<string, object> : null;
                SetPending(run, chosen != null ? Utils.GetValue<Dictionary<string, object>>(chosen, "action") : null);
            }
            else
            {
                SetPending(run, null); // message OK (or unknown) -> done
            }
            Step(run);
        }

        // Advance through non-UI nodes; stop on a UI node (options/message) or when finished.
        static void Step(RoguelikeRun run)
        {
            while (run.PendingAction != null)
            {
                string type = Utils.GetValue<string>(run.PendingAction, "type", "");
                if (type == "options" || type == "message") return; // needs UI; Project() will emit it
                // v2: apply a state-mutating leaf here, then SetPending(next) or null.
                SetPending(run, null); // v1: unknown leaf -> end
            }
        }

        static void SetPending(RoguelikeRun run, Dictionary<string, object> node)
        {
            run.PendingAction = node;
            if (node != null) run.ActionToken++;
        }

        // Thin prompt for the wire ($.Roguelike.action), or null when nothing is pending.
        public static Dictionary<string, object> Project(RoguelikeRun run)
        {
            Dictionary<string, object> cur = run.PendingAction;
            if (cur == null) return null;
            string type = Utils.GetValue<string>(cur, "type", "");
            Dictionary<string, object> p = new Dictionary<string, object>
            {
                { "token", run.ActionToken },
                { "type", type },
                { "text", Utils.GetValue<string>(cur, "text", "") },
            };
            if (type == "options")
            {
                List<object> labels = new List<object>();
                List<object> opts = Utils.GetValue<List<object>>(cur, "options");
                if (opts != null)
                    foreach (object o in opts)
                    {
                        Dictionary<string, object> od = o as Dictionary<string, object>;
                        labels.Add(od != null ? Utils.GetValue<string>(od, "label", "") : "");
                    }
                p["options"] = labels;
            }
            return p;
        }
    }
}
```
- [ ] **Step 2:** Don't build yet (csproj include is Task 5).

---

## Task 4 — Seed a test action on an encounter

**Files:** Modify `DataLE\Roguelike\Encounters.json` (install data; the server reads
this at runtime — there is no repo copy, same as Labels.json)

- [ ] **Step 1:** Add an `action` to the `boss_a1_dragon` encounter so `rgencounter
  boss_a1_dragon` has a real action to run. Add this field inside that object:
```json
"action": {
  "type": "options",
  "text": "O Dragão Ancestral tombou. Um baú surge à sua frente.",
  "options": [
    { "label": "Abrir o baú", "action": { "type": "options", "text": "O baú tem dois compartimentos.", "options": [
        { "label": "Esquerdo", "action": { "type": "message", "text": "Você pegou o compartimento esquerdo." } },
        { "label": "Direito",  "action": { "type": "message", "text": "Você pegou o compartimento direito." } } ] } },
    { "label": "Seguir em frente", "action": { "type": "message", "text": "Você seguiu sem abrir o baú." } }
  ]
}
```
- [ ] **Step 2:** No build. Encounters.json is cached once at server load — the server
  restart in Task 10 picks it up.

---

## Task 5 — Server: acts + WriteRun projection + csproj

**Files:** Modify `YgoMasterServer\Roguelike\GameServer.Roguelike.cs`,
`YgoMasterServer\GameServer.cs`, `YgoMasterServer\YgoMaster.csproj`

- [ ] **Step 1:** In `WriteRun` (GameServer.Roguelike.cs, ~line 16-25), after
  `Dictionary<string, object> dto = run.ToDictionary();` and before
  `request.Response["Roguelike"] = dto;`, strip the raw tree and inject the projection:
```csharp
dto.Remove("pendingAction");
Dictionary<string, object> actionPrompt = RoguelikeActionEngine.Project(run);
if (actionPrompt != null) dto["action"] = actionPrompt;
```
- [ ] **Step 2:** Add two handlers in GameServer.Roguelike.cs (next to `Act_RoguelikeMove`):
```csharp
// Dev/test: run the action defined on encounter <id> through the real engine.
void Act_RoguelikeEncounterAction(GameServerWebRequest request)
{
    string dir = GetPlayerDirectory(request.Player);
    RoguelikeRun run = RoguelikeRun.Load(dir);
    string id = request.ActParams != null ? Utils.GetValue<string>(request.ActParams, "id", null) : null;
    RoguelikeEncounters.Encounter enc = RoguelikeEncounters.ById(dataDirectory, id);
    if (enc != null && enc.Action != null) RoguelikeActionEngine.Start(run, enc.Action);
    else Console.WriteLine("[Roguelike] encounter_action: '" + id + "' not found or has no action");
    run.Save(dir);
    WriteRun(request, run);
}

// Resolve the current action prompt with the player's choice; the engine settles on the next.
void Act_RoguelikeActionRespond(GameServerWebRequest request)
{
    string dir = GetPlayerDirectory(request.Player);
    RoguelikeRun run = RoguelikeRun.Load(dir);
    int choice = request.ActParams != null ? Utils.GetValue<int>(request.ActParams, "choice", -1) : -1;
    RoguelikeActionEngine.Respond(run, choice);
    run.Save(dir);
    WriteRun(request, run);
}
```
- [ ] **Step 3:** Register both in the `switch (actName)` in GameServer.cs (after the
  `Roguelike.resume_duel` case, ~line 504):
```csharp
case "Roguelike.encounter_action":
    Act_RoguelikeEncounterAction(gameServerWebRequest);
    break;
case "Roguelike.action_respond":
    Act_RoguelikeActionRespond(gameServerWebRequest);
    break;
```
- [ ] **Step 4:** Add the new file to `YgoMasterServer\YgoMaster.csproj` next to the
  other Roguelike `<Compile Include>` lines (~line 139-143):
```xml
<Compile Include="Roguelike\Actions\RoguelikeActionEngine.cs" />
```
- [ ] **Step 5:** Build server; expect no errors. (Triggers are Task 6; client is Task 7+.)

---

## Task 6 — Server: real triggers (post-win, on-arrival)

**Files:** Modify `YgoMasterServer\Roguelike\GameServer.Roguelike.cs`

- [ ] **Step 1:** Post-win (combat) — in `Act_RoguelikeDuelResult`, inside the `if (win)`
  block, AFTER the currency line and BEFORE the `else if (nodeType == "boss")` advance
  (so a non-boss win shows its action; boss/advance interaction deferred to v2):
```csharp
if (enc != null && enc.Action != null && run.Active && nodeType != "boss")
    RoguelikeActionEngine.Start(run, enc.Action);
```
- [ ] **Step 2:** On-arrival (non-combat) — in `Act_RoguelikeMove`, in the branch where
  the target is NOT combat (the `else { run.PendingDuelNode = -1; }`), look up the
  node's baked encounter and start its action:
```csharp
else
{
    run.PendingDuelNode = -1;
    string encId = NodeEncounterId(run, target); // helper below
    RoguelikeEncounters.Encounter enc = RoguelikeEncounters.ById(dataDirectory, encId);
    if (enc != null && enc.Action != null) RoguelikeActionEngine.Start(run, enc.Action);
}
```
- [ ] **Step 2b:** Add the `NodeEncounterId` helper near `NodeType` (read the node dict
  out of `run.Map`, mirroring how `NodeType` finds the node):
```csharp
string NodeEncounterId(RoguelikeRun run, int nodeId)
{
    List<object> nodes = run.Map != null ? Utils.GetValue<List<object>>(run.Map, "nodes") : null;
    if (nodes == null) return null;
    foreach (object o in nodes)
    {
        Dictionary<string, object> d = o as Dictionary<string, object>;
        if (d != null && Utils.GetValue<int>(d, "id", -1) == nodeId)
            return Utils.GetValue<string>(d, "encounter", null);
    }
    return null;
}
```
  > If `NodeType` already parses the node dict, reuse its lookup instead of duplicating
  > the loop — check `NodeType`'s implementation and factor a shared `FindNode` if cheap.
- [ ] **Step 3:** Build server; expect no errors.

---

## Task 7 — Client: `RoguelikeApi` action calls + prompt read

**Files:** Modify `YgoMasterClient\Roguelike\RoguelikeApi.cs`

- [ ] **Step 1:** Add an action-prompt model + accessors (after `Move`, ~line 240):
```csharp
// ----- actions (M5) -----

public class ActionPrompt
{
    public int Token;
    public string Type;          // "options" | "message"
    public string Text;
    public string[] Options;     // option labels (empty for message)
}

// Current server action prompt ($.Roguelike.action), or null when nothing is pending.
public static ActionPrompt GetActionPrompt()
{
    try
    {
        string json = YgomSystem.Utility.ClientWork.SerializePath("Roguelike.action");
        if (string.IsNullOrEmpty(json)) return null;
        Dictionary<string, object> d = MiniJSON.Json.Deserialize(json) as Dictionary<string, object>;
        if (d == null) return null;
        ActionPrompt p = new ActionPrompt
        {
            Token = d.ContainsKey("token") ? Convert.ToInt32(d["token"]) : 0,
            Type = d.ContainsKey("type") ? Convert.ToString(d["type"]) : "",
            Text = d.ContainsKey("text") ? Convert.ToString(d["text"]) : "",
        };
        List<object> opts = d.ContainsKey("options") ? d["options"] as List<object> : null;
        if (opts != null)
        {
            p.Options = new string[opts.Count];
            for (int i = 0; i < opts.Count; i++) p.Options[i] = Convert.ToString(opts[i]);
        }
        else p.Options = new string[0];
        return p;
    }
    catch (Exception ex) { Console.WriteLine("[Roguelike] GetActionPrompt EX: " + ex); return null; }
}

public static void RunEncounterAction(string id)
{
    Call("Roguelike.encounter_action", new Dictionary<string, object> { { "id", id } });
}
public static void ActionRespond(int choice)
{
    Call("Roguelike.action_respond", new Dictionary<string, object> { { "choice", choice } });
}
```
- [ ] **Step 2:** Build client; expect no errors. (Driver/command come next.)

---

## Task 8 — Client: `RoguelikeActionDriver`

**Files:** Create `YgoMasterClient\Roguelike\Actions\RoguelikeActionDriver.cs`;
modify `YgoMasterClient.csproj`

- [ ] **Step 1:** Write the driver. `Pump()` is idempotent via the prompt token:
  it renders `options` as an ActionSheet and `message` as a confirmation dialog, then
  sends the choice back (which triggers the next response → next `Pump`).
```csharp
using System;

namespace YgoMasterClient
{
    // Renders the server's current action prompt ($.Roguelike.action) and sends the player's
    // choice back. Driven by RoguelikeFlow.OnNetworkComplete (called after every roguelike
    // response), so the prompt -> choice -> next-prompt loop self-advances. Token dedup keeps
    // a given prompt from re-opening. Options sheets are non-cancelable: ActionSheetViewController.OnCancel
    // (fired by the CANCEL button AND the full-screen tap-outside button) is hooked and swallowed while
    // an "options" action prompt is pending, so the player must pick an option.
    static class RoguelikeActionDriver
    {
        static int _shownToken = -1;

        public static void Pump()
        {
            try
            {
                RoguelikeApi.ActionPrompt p = RoguelikeApi.GetActionPrompt();
                if (p == null) { _shownToken = -1; return; }
                if (p.Token == _shownToken) return; // already shown
                _shownToken = p.Token;

                if (p.Type == "message")
                {
                    YgomGame.Menu.CommonDialogViewController.OpenConfirmationDialog(
                        "", p.Text, RoguelikeLabels.Get("common.ok", "OK"), OnMessageOk);
                }
                else // "options"
                {
                    YgomGame.Menu.ActionSheetViewController.Open(p.Text, p.Options, OnOptionSelected);
                }
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] action pump EX: " + ex); }
        }

        static void OnMessageOk() { RoguelikeApi.ActionRespond(0); }

        static void OnOptionSelected(IntPtr thisPtr, int index)
        {
            if (index >= 0) RoguelikeApi.ActionRespond(index);
        }
    }
}
```
  > Confirm `CommonDialogViewController.OpenConfirmationDialog` signature matches
  > `(title, message, okLabel, Action onOk)` — it's already used in `RoguelikeFlow`
  > line 44. `ActionSheetViewController.Open(string, string[], Action<IntPtr,int>)` is
  > the existing helper used by `RoguelikeNodeAction`.
- [ ] **Step 2:** Register in `YgoMasterClient.csproj` near the other Roguelike includes:
```xml
<Compile Include="YgoMasterClient\Roguelike\Actions\RoguelikeActionDriver.cs" />
```
- [ ] **Step 3:** Build client; expect no errors. (Wired in Task 9.)

---

## Task 9 — Client: wire Pump + `rgencounter` command

**Files:** Modify `YgoMasterClient\Roguelike\RoguelikeFlow.cs`,
`YgoMasterClient\ConsoleHelper.cs`

- [ ] **Step 1:** Call the driver after every roguelike response. At the END of
  `RoguelikeFlow.OnNetworkComplete` (after the `if/else` chain, before the closing
  brace, ~line 55):
```csharp
RoguelikeActionDriver.Pump();
```
  > This covers `encounter_action`, `action_respond`, plus `move`/`duel_result`
  > (server-triggered actions). It's a no-op when `$.Roguelike.action` is absent.
- [ ] **Step 2:** Add the console command in `ConsoleHelper.HandleCommand`, next to
  `rgabandon` (~line 940):
```csharp
case "rgencounter":// dev: run encounter <id>'s action through the real engine
    if (splitted.Length < 2) { Console.WriteLine("[rgencounter] usage: rgencounter <EncounterId>"); break; }
    RoguelikeApi.RunEncounterAction(splitted[1]);
    Console.WriteLine("[rgencounter] " + splitted[1]);
    break;
```
  > Encounter ids have no spaces (e.g. `boss_a1_dragon`), so `splitted[1]` is the full id.
- [ ] **Step 3:** Build client; expect no errors. Server should already be built (Task 6).

---

## Task 10 — End-to-end verification (in-game)

- [ ] **Step 1:** Restart the server and the client; start (or resume) a run so a map
  is up (`$.Roguelike` active).
- [ ] **Step 2:** In the client console run: `rgencounter boss_a1_dragon`.
  - Expected: an ActionSheet opens titled "O Dragão Ancestral tombou. Um baú surge à
    sua frente." with two buttons: "Abrir o baú", "Seguir em frente".
- [ ] **Step 3:** Tap "Abrir o baú".
  - Expected: a second ActionSheet "O baú tem dois compartimentos." with "Esquerdo",
    "Direito".
- [ ] **Step 4:** Tap "Esquerdo".
  - Expected: a confirmation dialog "Você pegou o compartimento esquerdo." with OK;
    tapping OK closes it and no prompt remains.
- [ ] **Step 5:** Run `rgencounter boss_a1_dragon` again, tap "Seguir em frente".
  - Expected: dialog "Você seguiu sem abrir o baú." → OK closes.
- [ ] **Step 6:** Confirm idempotency: re-rendering the map (e.g. moving) does not
  reopen a resolved prompt (token dedup), and `roguelike.json` has `pendingAction:
  null` after a completed action.
- [ ] **Step 7 (optional, real trigger):** Add an `event` encounter with an `action`
  to `Encounters.json`, ensure `event` is in `mapGen.bakeTypes`, walk onto that node,
  and confirm the action fires on arrival.
```

If any step fails, debug to root cause before continuing (systematic-debugging):
ActionSheet not opening → check `$.Roguelike.action` is present (server projection)
and that `OnNetworkComplete` runs `Pump()`; wrong/blank labels → check `Project`
reads `option.label`; cancel/tap-outside is suppressed via the OnCancel hook while an
options prompt is pending (no soft-lock).
