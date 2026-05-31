using System;
using System.Collections.Generic;
using IL2CPP;

namespace YgoMasterClient
{
    // Server-owned Roguelike run state. Reads come from $.Roguelike in ClientWork (the
    // server piggybacks the state into the home response); writes issue a custom act via
    // Request.Entry — the same call the game uses to talk to the server.
    static unsafe class RoguelikeApi
    {
        static IL2Method methodEntry;

        static RoguelikeApi()
        {
            try
            {
                IL2Class requestClass = Assembler.GetAssembly("Assembly-CSharp")
                    .GetClass("Request", "YgomSystem.Network");
                methodEntry = requestClass.GetMethod("Entry", m => m.GetParameters().Length == 3);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Roguelike] api init EX: " + ex);
            }
        }

        // True when the server reports an active run (populated on home load).
        public static bool IsRunActive()
        {
            return YgomSystem.Utility.ClientWork.GetByJsonPath<bool>("Roguelike.active");
        }

        // Issue a server act on demand: Request.Entry(act, params, timeout). The response
        // lands in ClientWork ($.Roguelike) and fires the RequestStructure.Complete hook.
        public static void Call(string act, Dictionary<string, object> args = null)
        {
            if (methodEntry == null) return;
            if (args == null) args = new Dictionary<string, object>();
            IntPtr argsPtr = YgomMiniJSON.Json.Deserialize(MiniJSON.Json.Serialize(args));
            float timeout = 30f;
            methodEntry.Invoke(new IntPtr[] { new IL2String(act).ptr, argsPtr, new IntPtr(&timeout) });
        }

        public static void StartRun(int ascension = 0)
        {
            Call("Roguelike.start_run", new Dictionary<string, object> { { "ascension", ascension } });
        }
        public static void AbandonRun() { Call("Roguelike.abandon_run"); }

        // ----- acts / ascension -----
        public static int Act() { return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.act"); }
        public static int Acts() { return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.acts"); }
        public static int Ascension() { return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.ascension"); }
        public static int MaxAscension() { return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.maxAscension"); }
        public static bool Won() { return YgomSystem.Utility.ClientWork.GetByJsonPath<bool>("Roguelike.won"); }
        public static void ChooseDeck(int index)
        {
            Call("Roguelike.choose_deck", new Dictionary<string, object> { { "index", index } });
        }

        // True when the player has finalized a deck for the active run.
        public static bool IsDeckChosen()
        {
            return YgomSystem.Utility.ClientWork.GetByJsonPath<bool>("Roguelike.deckChosen");
        }

        // Names of the (up to 3) decks offered for the pending run.
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

        // One offered deck, in the game's deck-list-item shape (so a native DeckReference can
        // be built from it).
        public class DeckOffer
        {
            public string Name = "Deck";
            public string Description = "";
            public List<int> PickCards = new List<int>(); // the 3 cover cards (pick_cards.ids)
            public List<int> PickDecos = new List<int>(); // their treatment/foil (pick_cards.r)
            public List<int> Main = new List<int>();      // main-deck card ids (for the viewer)
            public List<int> Extra = new List<int>();     // extra-deck card ids (for the viewer)
            public int Box, Sleeve, Field, Object;        // deck-box accessory (case/protector/...)
            public int DeckId;                            // synthetic id (deck_id) for the cell
        }

        // Full detail of the (up to 3) pending offers, read from $.Roguelike.deckOffers.
        public static List<DeckOffer> GetDeckOffers()
        {
            List<DeckOffer> result = new List<DeckOffer>();
            try
            {
                string json = YgomSystem.Utility.ClientWork.SerializePath("Roguelike.deckOffers");
                if (string.IsNullOrEmpty(json)) return result;
                List<object> list = MiniJSON.Json.Deserialize(json) as List<object>;
                if (list == null) return result;
                foreach (object o in list)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    DeckOffer offer = new DeckOffer
                    {
                        Name = d.ContainsKey("name") ? Convert.ToString(d["name"]) : "Deck",
                        Description = d.ContainsKey("description") ? Convert.ToString(d["description"]) : "",
                        DeckId = d.ContainsKey("deck_id") ? Convert.ToInt32(d["deck_id"]) : 0,
                    };
                    // accessory + pick_cards arrive in the game's nested deck-list-item shape.
                    Dictionary<string, object> acc = d.ContainsKey("accessory") ? d["accessory"] as Dictionary<string, object> : null;
                    if (acc != null)
                    {
                        offer.Box = AccInt(acc, "box");
                        offer.Sleeve = AccInt(acc, "sleeve");
                        offer.Field = AccInt(acc, "field");
                        offer.Object = AccInt(acc, "object");
                    }
                    Dictionary<string, object> pc = d.ContainsKey("pick_cards") ? d["pick_cards"] as Dictionary<string, object> : null;
                    ReadIndexed(pc, "ids", offer.PickCards);
                    ReadIndexed(pc, "r", offer.PickDecos);
                    ReadList(d, "main", offer.Main);
                    ReadList(d, "extra", offer.Extra);
                    result.Add(offer);
                }
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] GetDeckOffers EX: " + ex); }
            return result;
        }

        static int AccInt(Dictionary<string, object> d, string k)
        {
            try { return d.ContainsKey(k) ? Convert.ToInt32(d[k]) : 0; } catch { return 0; }
        }

        static void ReadList(Dictionary<string, object> d, string key, List<int> into)
        {
            List<object> arr = d.ContainsKey(key) ? d[key] as List<object> : null;
            if (arr == null) return;
            foreach (object v in arr) { try { into.Add(Convert.ToInt32(v)); } catch { } }
        }

        // Read an indexed sub-dict ({"1":a,"2":b,"3":c}) in 1..3 order into a list.
        static void ReadIndexed(Dictionary<string, object> parent, string key, List<int> into)
        {
            Dictionary<string, object> m = parent != null && parent.ContainsKey(key) ? parent[key] as Dictionary<string, object> : null;
            if (m == null) return;
            for (int k = 1; k <= 3; k++)
            {
                string kk = k.ToString();
                if (m.ContainsKey(kk)) { try { into.Add(Convert.ToInt32(m[kk])); } catch { } }
            }
        }

        // ----- map (M3) -----
        public class MapNode
        {
            public int Id, Row, Col;
            public string Type = "duel";
            public string Name = "";       // baked encounter display name
            public string IconImage = "";  // baked encounter art ("card_<cid>" / "profile_<id>")
            public int EnemyLp = -1;       // baked enemy LP (-1 = none)
            public int Reward = -1;        // baked reward (-1 = none)
            public Dictionary<string, object> Modifiers; // declared-modifier summary, or null
            public List<int> Next = new List<int>();
        }

        // Current node id (-1 = entry, before row 0).
        public static int Position()
        {
            return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.position");
        }

        // Ids of nodes already walked (includes the current position).
        public static HashSet<int> Visited()
        {
            HashSet<int> set = new HashSet<int>();
            try
            {
                string json = YgomSystem.Utility.ClientWork.SerializePath("Roguelike.visited");
                if (string.IsNullOrEmpty(json)) return set;
                List<object> list = MiniJSON.Json.Deserialize(json) as List<object>;
                if (list == null) return set;
                foreach (object v in list) { try { set.Add(Convert.ToInt32(v)); } catch { } }
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] Visited EX: " + ex); }
            return set;
        }

        public static List<MapNode> GetMapNodes()
        {
            List<MapNode> result = new List<MapNode>();
            try
            {
                string json = YgomSystem.Utility.ClientWork.SerializePath("Roguelike.map");
                if (string.IsNullOrEmpty(json)) return result;
                Dictionary<string, object> map = MiniJSON.Json.Deserialize(json) as Dictionary<string, object>;
                List<object> nodes = map != null && map.ContainsKey("nodes") ? map["nodes"] as List<object> : null;
                if (nodes == null) return result;
                foreach (object o in nodes)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    MapNode n = new MapNode
                    {
                        Id = d.ContainsKey("id") ? Convert.ToInt32(d["id"]) : -1,
                        Type = d.ContainsKey("type") ? Convert.ToString(d["type"]) : "duel",
                        Row = d.ContainsKey("row") ? Convert.ToInt32(d["row"]) : 0,
                        Col = d.ContainsKey("col") ? Convert.ToInt32(d["col"]) : 0,
                        Name = d.ContainsKey("name") ? Convert.ToString(d["name"]) : "",
                        IconImage = d.ContainsKey("iconImage") ? Convert.ToString(d["iconImage"]) : "",
                        EnemyLp = d.ContainsKey("enemyLp") ? Convert.ToInt32(d["enemyLp"]) : -1,
                        Reward = d.ContainsKey("reward") ? Convert.ToInt32(d["reward"]) : -1,
                        Modifiers = d.ContainsKey("modifiers") ? d["modifiers"] as Dictionary<string, object> : null,
                    };
                    List<object> next = d.ContainsKey("next") ? d["next"] as List<object> : null;
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

        // ----- actions (M5) -----

        // Unified pending action ($.Roguelike.action). Type-specific fields live in Data; consumers
        // dispatch on Type ("options" / "message" / "openpack"). Token bumps each new prompt.
        public class PendingAction
        {
            public int Token;
            public string Type;
            public Dictionary<string, object> Data;

            // options/message accessors
            public string Title   { get { return Data != null && Data.ContainsKey("title")   ? Convert.ToString(Data["title"])   : ""; } }
            public string Message { get { return Data != null && Data.ContainsKey("message") ? Convert.ToString(Data["message"]) : ""; } }
            public string[] Options
            {
                get
                {
                    if (Data == null) return new string[0];
                    List<object> opts = Data.ContainsKey("options") ? Data["options"] as List<object> : null;
                    if (opts == null) return new string[0];
                    string[] r = new string[opts.Count];
                    for (int i = 0; i < opts.Count; i++) r[i] = Convert.ToString(opts[i]);
                    return r;
                }
            }

            // openpack accessors. Pick == pickMax (kept under the name `Pick` for retrocompat
            // with the older "pick exact N" payload; that flow now lives as a degenerate range
            // with PickMin == PickMax). PickMin defaults to Pick when missing from the wire so
            // existing exact-pick actions keep behaving the same.
            public string Mode { get { return Data != null && Data.ContainsKey("mode") ? Convert.ToString(Data["mode"]) : "keep"; } }
            public int Pick    { get { return Data != null && Data.ContainsKey("pick") ? Convert.ToInt32(Data["pick"])  : 0; } }
            public int PickMin { get { return Data != null && Data.ContainsKey("pickMin") ? Convert.ToInt32(Data["pickMin"]) : Pick; } }
            public int PickMax { get { return Pick; } }
            public int Size    { get { return Data != null && Data.ContainsKey("size") ? Convert.ToInt32(Data["size"])  : 0; } }
            public PackLabels TextLabels
            {
                get
                {
                    Dictionary<string, object> lab = Data != null && Data.ContainsKey("labels") ? Data["labels"] as Dictionary<string, object> : null;
                    return new PackLabels
                    {
                        // Priority: action spec -> Labels.json -> "" (vanilla preserved).
                        TitleKeep = lab != null && lab.ContainsKey("title_keep")
                            ? Convert.ToString(lab["title_keep"]) : RoguelikeLabels.Get("pack.title.keep", ""),
                        TitlePick = lab != null && lab.ContainsKey("title_pick")
                            ? Convert.ToString(lab["title_pick"]) : RoguelikeLabels.Get("pack.title.pick", ""),
                        Confirm   = lab != null && lab.ContainsKey("confirm")
                            ? Convert.ToString(lab["confirm"])    : RoguelikeLabels.Get("pack.confirm", ""),
                    };
                }
            }
            public class PackLabels { public string TitleKeep; public string TitlePick; public string Confirm; }

            // addCard accessor — cids the server already applied; the client plays the fly
            // animation (center -> deck button) then acks to advance the action.
            public int[] Cards
            {
                get
                {
                    if (Data == null) return new int[0];
                    List<object> arr = Data.ContainsKey("cards") ? Data["cards"] as List<object> : null;
                    if (arr == null) return new int[0];
                    int[] r = new int[arr.Count];
                    for (int i = 0; i < arr.Count; i++) { try { r[i] = Convert.ToInt32(arr[i]); } catch { } }
                    return r;
                }
            }
        }

        static string _lastActionJson; // diag dedup so the per-frame log doesn't spam

        // Current server action ($.Roguelike.action), or null when nothing is pending. Replaces
        // the older GetActionPrompt + GetPendingPack split.
        public static PendingAction GetPendingAction()
        {
            try
            {
                string json = YgomSystem.Utility.ClientWork.SerializePath("Roguelike.action");
                if (string.IsNullOrEmpty(json)) return null;
                if (json != _lastActionJson)
                {
                    _lastActionJson = json;
                    Console.WriteLine("[Roguelike] GetPendingAction: json=" + (json.Length > 200 ? json.Substring(0, 200) + "..." : json));
                }
                Dictionary<string, object> d = MiniJSON.Json.Deserialize(json) as Dictionary<string, object>;
                if (d == null) return null;
                return new PendingAction
                {
                    Token = d.ContainsKey("token") ? Convert.ToInt32(d["token"]) : 0,
                    Type  = d.ContainsKey("type")  ? Convert.ToString(d["type"]) : "",
                    Data  = d.ContainsKey("data")  ? d["data"] as Dictionary<string, object> : null,
                };
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] GetPendingAction EX: " + ex); return null; }
        }

        // Dev/test: run encounter <id>'s action through the server engine.
        public static void RunEncounterAction(string id)
        {
            Call("Roguelike.encounter_action", new Dictionary<string, object> { { "id", id } });
        }

        // Resolve the current action prompt. Payload is per-type:
        //   options  -> { token, choice }
        //   message  -> { token }            (any payload accepted; engine just advances)
        //   openpack -> { token, picks: [..] }
        public static void ActionRespond(int token, Dictionary<string, object> payload = null)
        {
            Dictionary<string, object> p = payload != null
                ? new Dictionary<string, object>(payload)
                : new Dictionary<string, object>();
            p["token"] = token;
            Call("Roguelike.action_respond", p);
        }

        public static void ActionRespondChoice(int token, int choice)
        {
            ActionRespond(token, new Dictionary<string, object> { { "choice", choice } });
        }

        public static void ActionRespondPicks(int token, int[] picks)
        {
            ActionRespond(token, new Dictionary<string, object> { { "picks", picks } });
        }

        // ----- combat (M4) -----

        // Combat node whose duel the server queued in this move's response (-1 = none). When
        // set, $.Duel holds the duel settings ready for Solo/SoloStartProduction.
        public static int PendingDuelNode()
        {
            return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.pendingDuelNode");
        }

        // Report a finished combat duel: win/loss + the player's remaining LP. Server carries the LP
        // into run LP (win, + heal) or ends the run (loss).
        public static void SendDuelResult(bool win, int playerLp)
        {
            Call("Roguelike.duel_result", new Dictionary<string, object> { { "win", win }, { "playerLp", playerLp } });
        }

        // Re-fetch the pending combat duel (same seed) so the client can relaunch an unfinished one.
        public static void ResumeDuel() { Call("Roguelike.resume_duel"); }

        // Regulation id of the run's card pool (banlist) — for the deck editor.
        public static int RegulationId() { return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.regulationId"); }

        // Current run LP / cap (for the map LP indicator).
        public static int Lp() { return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.lp"); }
        public static int MaxLp() { return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.maxLp"); }

        // Current run gold (= Currency server-side, renamed in wire). Used by the map HUD
        // label and the animation diff snapshot.
        public static int Gold() { return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.gold"); }

        // ----- run deck (for the in-run deck editor view) -----

        // Card ids of the chosen run deck's main / extra section (with duplicates, as built). Shape:
        // $.Roguelike.deck = {name, bossCard, description, deck:{m:{ids:[...]}, e:{ids:[...]}, s:{...}}}.
        public static List<int> RunDeckMain() { return RunDeckCards("m"); }
        public static List<int> RunDeckExtra() { return RunDeckCards("e"); }

        // Deck limits used by reward routing + the run deck editor's save validation. Sourced
        // from RoguelikeSettings.deck.* on the server; defaults (40/60/15) mirror vanilla.
        public static int DeckMinCards()      { return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.deckMinCards"); }
        public static int DeckMaxMainCards()  { return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.deckMaxMainCards"); }
        public static int DeckMaxExtraCards() { return YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Roguelike.deckMaxExtraCards"); }

        // Persist an edited run deck (DeckInfo.ToDictionary shape: {m:{ids,r}, e, s}) to the server.
        public static void SaveDeck(Dictionary<string, object> deck)
        {
            Call("Roguelike.save_deck", new Dictionary<string, object> { { "deck", deck } });
        }

        // Cards the active run owns, cid -> owned count (tn from $.Roguelike.Cards, the
        // Player.Cards-shaped collection). Used to restrict the deck-editor collection to
        // run-obtained cards and to show the run's copy count instead of the player's.
        public static Dictionary<int, int> RunOwnedCardCounts()
        {
            Dictionary<int, int> result = new Dictionary<int, int>();
            try
            {
                string json = YgomSystem.Utility.ClientWork.SerializePath("Roguelike.Cards");
                if (string.IsNullOrEmpty(json)) return result;
                Dictionary<string, object> d = MiniJSON.Json.Deserialize(json) as Dictionary<string, object>;
                if (d == null) return result;
                foreach (KeyValuePair<string, object> e in d)
                {
                    int cid;
                    if (!int.TryParse(e.Key, out cid)) continue;
                    Dictionary<string, object> card = e.Value as Dictionary<string, object>;
                    int tn = (card != null && card.ContainsKey("tn")) ? Convert.ToInt32(card["tn"]) : 0;
                    result[cid] = tn;
                }
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] RunOwnedCardCounts EX: " + ex); }
            return result;
        }

        // Total card copies the run owns (sum of tn across all cards).
        public static int RunOwnedCardTotal()
        {
            int total = 0;
            foreach (int v in RunOwnedCardCounts().Values) total += v;
            return total;
        }

        static List<int> RunDeckCards(string section)
        {
            List<int> result = new List<int>();
            try
            {
                string json = YgomSystem.Utility.ClientWork.SerializePath("Roguelike.deck");
                if (string.IsNullOrEmpty(json)) return result;
                Dictionary<string, object> wrap = MiniJSON.Json.Deserialize(json) as Dictionary<string, object>;
                Dictionary<string, object> deck = wrap != null && wrap.ContainsKey("deck") ? wrap["deck"] as Dictionary<string, object> : null;
                Dictionary<string, object> sect = deck != null && deck.ContainsKey(section) ? deck[section] as Dictionary<string, object> : null;
                List<object> ids = sect != null && sect.ContainsKey("ids") ? sect["ids"] as List<object> : null;
                if (ids == null) return result;
                foreach (object v in ids) { try { result.Add(Convert.ToInt32(v)); } catch { } }
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] RunDeckCards EX: " + ex); }
            return result;
        }
    }
}
