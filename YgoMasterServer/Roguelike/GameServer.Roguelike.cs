using System;
using System.Collections.Generic;

namespace YgoMaster
{
    partial class GameServer
    {
        // Piggyback run state into a response so it lands at $.Roguelike client-side.
        void WriteRoguelikeState(GameServerWebRequest request)
        {
            WriteRun(request, RoguelikeRun.Load(GetPlayerDirectory(request.Player)));
        }

        // Send the run to the client. deckOffers is persisted as bare file paths (source of
        // truth = the deck JSONs); we expand them into full deck-list items only on the wire.
        void WriteRun(GameServerWebRequest request, RoguelikeRun run)
        {
            Dictionary<string, object> dto = run.ToDictionary();
            dto["deckOffers"] = ExpandDeckOffers(run.DeckOffers);
            dto["maxAscension"] = RoguelikeMeta.Load(GetPlayerDirectory(request.Player)).MaxAscension;
            Dictionary<string, object> settings = RoguelikeSettings.Load(dataDirectory);
            dto["acts"] = RoguelikeSettings.Acts(settings);
            dto["regulationId"] = RoguelikeCardPool.RegulationId(dataDirectory);
            // Deck min/max — the client mirrors these in its save validation so the editor
            // accepts whatever the mode allows (vanilla hardcodes 40/60/15 in DeckInfo.IsValid).
            dto["deckMinCards"]      = RoguelikeSettings.DeckMinCards(settings);
            dto["deckMaxMainCards"]  = RoguelikeSettings.DeckMaxMainCards(settings);
            dto["deckMaxExtraCards"] = RoguelikeSettings.DeckMaxExtraCards(settings);
            // The full action tree (pendingAction) is server/disk-only; project a thin prompt for the wire.
            dto.Remove("pendingAction");
            Dictionary<string, object> actionPrompt = RoguelikeActionEngine.Project(run);
            if (actionPrompt != null) dto["action"] = actionPrompt;
            // openpack rides along via $.Gacha (vanilla shape) when the pending action is one.
            string actionType = actionPrompt != null ? Utils.GetValue<string>(actionPrompt, "type", "") : "";
            if (actionType == "openpack")
            {
                List<object> cards = RoguelikeActionEngine.PendingOpenPackCards(run);
                if (cards != null && cards.Count > 0)
                {
                    Dictionary<string, object> gachaProj = BuildGachaProjection(cards);
                    request.Response["Gacha"] = gachaProj;
                    Console.WriteLine("[Roguelike] WriteRun: projecting openpack token=" + Utils.GetValue<int>(actionPrompt, "token") +
                        " gachaPacks=" + ((List<object>)((Dictionary<string, object>)gachaProj["drawInfo"])["packs"]).Count);
                }
                else request.Remove("Gacha");
            }
            else
            {
                // Explicit cleanup: prevent stale pack data from leaking to other screens.
                request.Remove("Gacha");
            }
            // While inside an active run, broadcast the mod's full CardList rarities under
            // $.Master.CardRare so every native VC that reads the rarity tag (UR/SR/N) — pack
            // result, deck editor, card preview — reflects the mod's curation instead of the
            // vanilla snapshot the client got at login. Cheap (single dictionary copy) and
            // idempotent. Sent on every WriteRun so any state change keeps the client in sync.
            if (run.Active)
            {
                Dictionary<string, object> cardRareDto = new Dictionary<string, object>(CardRare.Count);
                foreach (KeyValuePair<int, int> kv in CardRare) cardRareDto[kv.Key.ToString()] = kv.Value;
                request.GetOrCreateDictionary("Master")["CardRare"] = cardRareDto;
            }
            request.Response["Roguelike"] = dto;
            request.Remove("Roguelike");
        }

        void Act_RoguelikeStartRun(GameServerWebRequest request)
        {
            string gameType = "base_deck";
            int ascension = 0;
            if (request.ActParams != null)
            {
                gameType = Utils.GetValue<string>(request.ActParams, "gameType", "base_deck");
                ascension = Utils.GetValue<int>(request.ActParams, "ascension", 0);
            }
            int maxAsc = RoguelikeMeta.Load(GetPlayerDirectory(request.Player)).MaxAscension;
            if (ascension < 0) ascension = 0;
            if (ascension > maxAsc) ascension = maxAsc; // never trust the client
            int seed = new Random().Next();
            RoguelikeRun run = new RoguelikeRun
            {
                Active = true,
                GameType = gameType,
                Seed = seed,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                DeckChosen = false,
                DeckOffers = RollDeckOfferFiles(seed, ascension),
                Deck = null,
                Act = 0,
                Ascension = ascension,
                Won = false,
            };
            run.Save(GetPlayerDirectory(request.Player));
            WriteRun(request, run);
        }

        // Roll the starter-deck offers for a new run (seeded). Returns relative paths only; the deck
        // data lives in the files and is read on demand (choose/expand). Prefers the curated pool
        // (InitialDecks.json: weighted + ascension-gated), and falls back to a uniform shuffle of the
        // whole StartingDecks folder when that file is absent or nothing is eligible for `ascension`.
        List<object> RollDeckOfferFiles(int seed, int ascension)
        {
            Random rng = new Random(seed);
            int count = RoguelikeStartingDecks.OfferCount(dataDirectory);
            List<object> result = new List<object>();

            List<RoguelikeStartingDecks.Entry> elig = RoguelikeStartingDecks.Eligible(dataDirectory, ascension);
            if (elig.Count > 0)
            {
                foreach (RoguelikeStartingDecks.Entry e in RoguelikeStartingDecks.Pick(elig, count, rng))
                    result.Add(RelPath(System.IO.Path.Combine(dataDirectory, "Roguelike", "StartingDecks", e.Deck)));
                return result;
            }

            // Fallback: uniform shuffle of every file in the folder (legacy behavior).
            List<string> files = RoguelikeDeckPool.ListFiles(dataDirectory);
            if (files.Count == 0)
            {
                Console.WriteLine("[Roguelike] no starter decks in Roguelike/StartingDecks");
                return result;
            }
            for (int i = files.Count - 1; i > 0; i--) // Fisher-Yates
            {
                int j = rng.Next(i + 1);
                string tmp = files[i]; files[i] = files[j]; files[j] = tmp;
            }
            int take = Math.Min(count, files.Count);
            for (int i = 0; i < take; i++) result.Add(RelPath(files[i]));
            return result;
        }

        // Expand persisted offer file paths into deck-list items the client renders natively
        // (deck_id + name + accessory + pick_cards, all in the game's shape).
        List<object> ExpandDeckOffers(List<object> files)
        {
            List<object> offers = new List<object>();
            if (files == null) return offers;
            for (int i = 0; i < files.Count; i++)
            {
                string rel = files[i] as string;
                if (string.IsNullOrEmpty(rel)) continue;
                try
                {
                    RoguelikeDeckPool.StarterDeck d = RoguelikeDeckPool.LoadOne(System.IO.Path.Combine(dataDirectory, rel));
                    if (d == null) continue;
                    // InitialDecks.json may override the display name; otherwise use the deck file name.
                    string nameOverride = RoguelikeStartingDecks.NameOverrideFor(dataDirectory, System.IO.Path.GetFileName(rel));
                    offers.Add(new Dictionary<string, object>
                    {
                        { "deck_id", 90000001 + i },
                        { "name", !string.IsNullOrEmpty(nameOverride) ? nameOverride : d.Name },
                        { "status", 0 },
                        { "ct", Utils.GetValue<long>(d.Json, "ct") },
                        { "et", Utils.GetValue<long>(d.Json, "et") },
                        { "regulation_id", Utils.GetValue<int>(d.Json, "regulation_id") },
                        { "accessory", Utils.GetValue<Dictionary<string, object>>(d.Json, "accessory") },
                        { "pick_cards", Utils.GetValue<Dictionary<string, object>>(d.Json, "pick_cards") },
                        { "main", IdsOf(d.Json, "m") },
                        { "extra", IdsOf(d.Json, "e") },
                        { "file", rel }, { "description", d.Description },
                    });
                }
                catch (Exception ex) { Console.WriteLine("[Roguelike] expand offer EX: " + ex.Message); }
            }
            return offers;
        }

        string RelPath(string fullPath)
        {
            return fullPath.StartsWith(dataDirectory)
                ? fullPath.Substring(dataDirectory.Length).TrimStart('\\', '/')
                : fullPath;
        }

        // Flat id list of a player-format deck section ("m"/"e"/"s") for the deck viewer.
        static List<object> IdsOf(Dictionary<string, object> deckJson, string section)
        {
            Dictionary<string, object> sec = Utils.GetValue<Dictionary<string, object>>(deckJson, section);
            List<object> ids = sec != null ? Utils.GetValue<List<object>>(sec, "ids") : null;
            return ids ?? new List<object>();
        }

        void Act_RoguelikeChooseDeck(GameServerWebRequest request)
        {
            RoguelikeRun run = RoguelikeRun.Load(GetPlayerDirectory(request.Player));
            if (run.Active && !run.DeckChosen && run.DeckOffers != null)
            {
                int index = request.ActParams != null ? Utils.GetValue<int>(request.ActParams, "index", -1) : -1;
                if (index >= 0 && index < run.DeckOffers.Count)
                {
                    string rel = run.DeckOffers[index] as string;
                    if (!string.IsNullOrEmpty(rel))
                    {
                        RoguelikeDeckPool.StarterDeck d =
                            RoguelikeDeckPool.LoadOne(System.IO.Path.Combine(dataDirectory, rel));
                        if (d != null)
                        {
                            run.Deck = new Dictionary<string, object>
                            {
                                { "name", d.Name }, { "bossCard", d.BossCard },
                                { "description", d.Description }, { "deck", d.Json },
                            };
                            run.DeckChosen = true;
                            run.DeckOffers = new List<object>();
                            // Seed the run-owned collection with the chosen deck's cards (deck multiplicity).
                            run.Cards = new Dictionary<string, object>();
                            DeckInfo picked = new DeckInfo();
                            picked.FromDictionary(d.Json, false);
                            foreach (KeyValuePair<int, CardStyleRarity> c in picked.MainDeckCards.GetCollection()) run.AddCard(c.Key);
                            foreach (KeyValuePair<int, CardStyleRarity> c in picked.ExtraDeckCards.GetCollection()) run.AddCard(c.Key);
                            foreach (KeyValuePair<int, CardStyleRarity> c in picked.SideDeckCards.GetCollection()) run.AddCard(c.Key);
                            run.Act = 0;
                            Dictionary<string, object> eff = RoguelikeSettings.Effective(
                                RoguelikeSettings.Load(dataDirectory), run.Act, run.Ascension);
                            run.Map = BuildActMap(run, eff);
                            run.Position = -1;
                            run.Visited = new List<object>();
                            run.MaxLp = RoguelikeSettings.PlayerMaxLp(eff);
                            run.Lp = run.MaxLp; // start the run at full LP
                            run.Save(GetPlayerDirectory(request.Player));
                        }
                    }
                }
            }
            WriteRun(request, run);
        }

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
                    // Combat node -> set up the duel; the client launches it and reports the result.
                    if (IsCombat(NodeType(run, target)) && BuildRoguelikeDuel(run, request, target))
                        run.PendingDuelNode = target;
                    else
                    {
                        run.PendingDuelNode = -1;
                        // Non-combat node: fire its encounter's action on arrival, if any.
                        RoguelikeEncounters.Encounter encMove = RoguelikeEncounters.ById(dataDirectory, NodeEncounter(run, target));
                        if (encMove != null && encMove.Action != null) RoguelikeActionEngine.Start(run, encMove.Action, dataDirectory, Regulation);
                    }
                    run.Save(GetPlayerDirectory(request.Player));
                }
            }
            WriteRun(request, run);
        }

        // Dev/test: run the action defined on encounter <id> through the real engine.
        void Act_RoguelikeEncounterAction(GameServerWebRequest request)
        {
            string dir = GetPlayerDirectory(request.Player);
            RoguelikeRun run = RoguelikeRun.Load(dir);
            string id = request.ActParams != null ? Utils.GetValue<string>(request.ActParams, "id", null) : null;
            Console.WriteLine("[Roguelike] encounter_action enter: id=" + id + " runActive=" + run.Active);
            RoguelikeEncounters.Encounter enc = RoguelikeEncounters.ById(dataDirectory, id);
            Console.WriteLine("[Roguelike] encounter_action: enc=" + (enc != null) + " action=" + (enc != null && enc.Action != null));
            if (enc != null && enc.Action != null) RoguelikeActionEngine.Start(run, enc.Action, dataDirectory, Regulation);
            else Console.WriteLine("[Roguelike] encounter_action: '" + id + "' not found or has no action");
            Console.WriteLine("[Roguelike] encounter_action post: PendingAction=" + (run.PendingAction != null) + " token=" + run.ActionToken);
            run.Save(dir);
            WriteRun(request, run);
        }

        // Resolve the current action prompt with the player's payload (per-type: options→choice,
        // openpack→picks). The engine validates token + payload, advances on success; on failure
        // (stale/invalid) we set ResultCode so the client can react.
        void Act_RoguelikeActionRespond(GameServerWebRequest request)
        {
            string dir = GetPlayerDirectory(request.Player);
            RoguelikeRun run = RoguelikeRun.Load(dir);
            bool ok = RoguelikeActionEngine.Respond(run, request.ActParams, dataDirectory, Regulation);
            if (!ok) request.ResultCode = -1;
            run.Save(dir);
            WriteRun(request, run);
        }

        // Persist an edited run deck. The client (RoguelikeDeckEditScreen) does its own validation and
        // sends the deck in DeckInfo dictionary shape ({m:{ids,r}, e, s}); we store it into the run's
        // deck (keeping name/bossCard/description) so subsequent duels use the edited list.
        void Act_RoguelikeSaveDeck(GameServerWebRequest request)
        {
            RoguelikeRun run = RoguelikeRun.Load(GetPlayerDirectory(request.Player));
            if (run.Active && run.DeckChosen && run.Deck != null && request.ActParams != null)
            {
                Dictionary<string, object> deckDict = Utils.GetValue<Dictionary<string, object>>(request.ActParams, "deck");
                if (deckDict != null)
                {
                    run.Deck["deck"] = deckDict;
                    run.Save(GetPlayerDirectory(request.Player));
                }
            }
            WriteRun(request, run);
        }

        // Resume an unfinished combat: rebuild the same (deterministic) duel for the pending node so
        // the client can re-launch it. No state change — the duel only clears via duel_result.
        void Act_RoguelikeResumeDuel(GameServerWebRequest request)
        {
            RoguelikeRun run = RoguelikeRun.Load(GetPlayerDirectory(request.Player));
            if (run.Active && run.PendingDuelNode >= 0)
                BuildRoguelikeDuel(run, request, run.PendingDuelNode);
            WriteRun(request, run);
        }

        // Win: carry remaining LP into run LP (+ combat heal), credit currency. Boss win advances to
        // the next act (regenerate map, inter-act heal) or, on the final act, wins the run and unlocks
        // the next ascension. Loss: LP hits 0, run ends. Clears the pending duel either way.
        void Act_RoguelikeDuelResult(GameServerWebRequest request)
        {
            string dir = GetPlayerDirectory(request.Player);
            RoguelikeRun run = RoguelikeRun.Load(dir);
            bool win = request.ActParams != null && Utils.GetValue<bool>(request.ActParams, "win", false);
            int playerLp = request.ActParams != null ? Utils.GetValue<int>(request.ActParams, "playerLp", 0) : 0;
            if (run.Active && run.PendingDuelNode >= 0)
            {
                Dictionary<string, object> baseS = RoguelikeSettings.Load(dataDirectory);
                Dictionary<string, object> eff = RoguelikeSettings.Effective(baseS, run.Act, run.Ascension);
                if (run.MaxLp <= 0) run.MaxLp = RoguelikeSettings.PlayerMaxLp(eff); // lazy init (old saves)
                string nodeType = NodeType(run, run.PendingDuelNode);
                RoguelikeEncounters.Encounter enc = RoguelikeEncounters.ById(dataDirectory, run.PendingEncounterId);
                run.PendingDuelNode = -1;
                run.PendingEncounterId = "";
                if (win)
                {
                    int heal = (int)(run.MaxLp * RoguelikeSettings.HealPercent(eff));
                    run.Lp = Math.Min(run.MaxLp, Math.Max(0, playerLp) + heal);
                    run.Currency += (enc != null && enc.Reward.HasValue) ? enc.Reward.Value : RewardFor(nodeType);
                    if (run.Lp <= 0) run.Active = false; // safety: a 0-LP "win" is still death
                    else if (nodeType == "boss") AdvanceActOrWin(run, baseS, dir);
                    // Post-win encounter action (non-boss for now; boss-advance interaction is v2).
                    if (run.Active && nodeType != "boss" && enc != null && enc.Action != null)
                        RoguelikeActionEngine.Start(run, enc.Action, dataDirectory, Regulation);
                }
                else
                {
                    run.Lp = 0;
                    run.Active = false;
                }
                run.Save(dir);
            }
            WriteRun(request, run);
        }

        // Boss beaten: go to the next act (new map + inter-act heal) or win the run + unlock ascension.
        void AdvanceActOrWin(RoguelikeRun run, Dictionary<string, object> baseS, string dir)
        {
            if (run.Act < RoguelikeSettings.Acts(baseS) - 1)
            {
                run.Act++;
                Dictionary<string, object> next = RoguelikeSettings.Effective(baseS, run.Act, run.Ascension);
                run.Map = BuildActMap(run, next);
                run.Position = -1;
                run.Visited = new List<object>();
                int interHeal = (int)(run.MaxLp * RoguelikeSettings.InterActHealPercent(next));
                run.Lp = Math.Min(run.MaxLp, run.Lp + interHeal);
            }
            else
            {
                run.Won = true;
                run.Active = false;
                RoguelikeMeta meta = RoguelikeMeta.Load(dir);
                int unlocked = Math.Min(RoguelikeSettings.Ascensions(baseS) - 1, Math.Max(meta.MaxAscension, run.Ascension + 1));
                if (unlocked > meta.MaxAscension) { meta.MaxAscension = unlocked; meta.Save(dir); }
            }
        }

        // Deterministic per-act map seed (each act of a run gets a distinct, reproducible map).
        static int ActSeed(long runSeed, int act)
        {
            unchecked { return (int)((uint)(runSeed * 31) ^ (uint)(act * 2654435761)); }
        }

        // Build the act map and bake encounters for the configured node types, so the map can name them
        // (and show their icon) ahead of the fight. Non-baked combat types pick lazily at arrival.
        Dictionary<string, object> BuildActMap(RoguelikeRun run, Dictionary<string, object> eff)
        {
            RoguelikeMapLayout layout = RoguelikeMapLayout.Create(RoguelikeSettings.Layout(eff));
            RoguelikeMap map = layout.Build(ActSeed(run.Seed, run.Act), eff);
            BakeEncounters(run, map, eff);
            return map.ToDictionary();
        }

        // For each node whose type is in mapGen.bakeTypes, pick an encounter (seeded per node) and store
        // its id + display name + icon on the node. Deterministic: a resumed run re-derives the same map.
        void BakeEncounters(RoguelikeRun run, RoguelikeMap map, Dictionary<string, object> eff)
        {
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
        }

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

        // Default node art when an encounter has no explicit icon_image: the deck's first main-deck card.
        string DeriveIconImage(string deckFile)
        {
            try
            {
                string path = RoguelikeDeckPool.ResolveOpponentDeck(dataDirectory, deckFile);
                if (path == null) return null;
                DeckInfo di = new DeckInfo();
                di.File = path;
                di.Load();
                List<KeyValuePair<int, CardStyleRarity>> cards = di.MainDeckCards.GetCollection();
                if (cards.Count > 0) return "card_" + cards[0].Key;
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] DeriveIconImage EX: " + ex.Message); }
            return null;
        }

        static bool IsCombat(string type) => type == "duel" || type == "elite" || type == "boss";

        static int RewardFor(string type)
        {
            switch (type) { case "boss": return 1000; case "elite": return 250; default: return 100; }
        }

        static Dictionary<string, object> FindNode(RoguelikeRun run, int id)
        {
            List<object> nodes = run.Map != null ? Utils.GetValue<List<object>>(run.Map, "nodes") : null;
            if (nodes == null) return null;
            foreach (object o in nodes)
            {
                Dictionary<string, object> n = o as Dictionary<string, object>;
                if (n != null && Utils.GetValue<int>(n, "id", -999) == id) return n;
            }
            return null;
        }

        static string NodeType(RoguelikeRun run, int id)
        {
            Dictionary<string, object> n = FindNode(run, id);
            return n != null ? Utils.GetValue<string>(n, "type", "") : "";
        }

        static int NodeRow(RoguelikeRun run, int id)
        {
            Dictionary<string, object> n = FindNode(run, id);
            return n != null ? Utils.GetValue<int>(n, "row", -1) : -1;
        }

        static string NodeEncounter(RoguelikeRun run, int id)
        {
            Dictionary<string, object> n = FindNode(run, id);
            return n != null ? Utils.GetValue<string>(n, "encounter", "") : "";
        }

        // Build a custom duel (run deck vs opponent) for a combat node and put it in Response["Duel"]
        // so the client's Solo-start flow launches it. Everything random — opponent pick, who goes
        // first, the engine RNG seed — derives from (run seed, node) so an unfinished duel resumes
        // identically: the player can't quit to re-roll their draws. Mirrors the CustomDuel pattern.
        bool BuildRoguelikeDuel(RoguelikeRun run, GameServerWebRequest request, int nodeId)
        {
            Dictionary<string, object> deckDict = run.Deck != null ? Utils.GetValue<Dictionary<string, object>>(run.Deck, "deck") : null;
            if (deckDict == null) return false;
            DeckInfo player = new DeckInfo();
            player.FromDictionary(deckDict, false);
            player.Name = Utils.GetValue<string>(run.Deck, "name", "Run Deck");

            string nodeType = NodeType(run, nodeId);
            int floor = NodeRow(run, nodeId);
            Random duelRng = new Random(DuelRngSeed(run.Seed, run.Act, nodeId));

            // Baked nodes (mapGen.bakeTypes) already have their encounter chosen at map-gen; the rest
            // pick here (lazy). The lazy pick is the first RNG draw, so a resumed duel re-derives the
            // same enemy and draws.
            string baked = NodeEncounter(run, nodeId);
            RoguelikeEncounters.Encounter enc = !string.IsNullOrEmpty(baked)
                ? RoguelikeEncounters.ById(dataDirectory, baked)
                : RoguelikeEncounters.Pick(dataDirectory, nodeType, run.Act, floor, run.Ascension, duelRng);
            if (enc == null)
            {
                Console.WriteLine("[Roguelike] no encounter for type " + nodeType + " act " + run.Act +
                    " floor " + floor + " asc " + run.Ascension);
                return false;
            }

            string oppFile = RoguelikeDeckPool.ResolveOpponentDeck(dataDirectory, enc.Deck);
            if (oppFile == null) { Console.WriteLine("[Roguelike] encounter deck not found: " + enc.Deck); return false; }
            DeckInfo opp = new DeckInfo();
            opp.File = oppFile;
            opp.Load();
            opp.Name = !string.IsNullOrEmpty(enc.Name) ? enc.Name : System.IO.Path.GetFileNameWithoutExtension(oppFile);

            DuelSettings ds = new DuelSettings();
            ds.Deck[DuelSettings.PlayerIndex] = player;
            ds.Deck[DuelSettings.CpuIndex] = opp;
            ds.IsCustomDuel = true;
            ds.SetRequiredDefaults();
            // Starting LP: player = current run LP, enemy = encounter override else per-type config
            // (set after defaults so they aren't zeroed; non-zero so they survive the engine's defaulting).
            Dictionary<string, object> lpSettings = RoguelikeSettings.Effective(
                RoguelikeSettings.Load(dataDirectory), run.Act, run.Ascension);
            if (run.MaxLp <= 0) run.MaxLp = RoguelikeSettings.PlayerMaxLp(lpSettings); // lazy init (old saves)
            int playerLife = run.Lp > 0 ? run.Lp : run.MaxLp;
            ds.life[DuelSettings.PlayerIndex] = playerLife;
            ds.life[DuelSettings.CpuIndex] = enc.EnemyLp ?? RoguelikeSettings.EnemyLpFor(lpSettings, nodeType);
            ds.chapter = 0;
            ds.FirstPlayer = enc.FirstPlayer ?? duelRng.Next(2);
            ds.cpu = enc.CpuRate ?? RoguelikeSettings.CpuRate(lpSettings);     // AI strength (default 100 = max)
            ds.cpuflag = enc.CpuFlag ?? RoguelikeSettings.CpuFlag(lpSettings); // AI behavior flag (default None)
            // Engine RNG seed (shuffle/draws/dice). Must be nonzero so Act_DuelBegin keeps it
            // instead of randomizing; deterministic so a resumed duel deals the same cards.
            ds.RandSeed = (uint)duelRng.Next(1, int.MaxValue);

            run.PendingEncounterId = enc.Id; // for the reward lookup at duel_result
            // Full settings ride along as duelStarterData so the client forwards them in Duel.begin —
            // there's no chapter for the server to build from. Modifiers (per-type defaults +
            // encounter) compile into cmds (starting board) and LP/hand deltas on top of the base.
            Dictionary<string, object> starter = ds.ToDictionary();
            string duelType = ds.Type == 4 ? "Rush" : "Normal";
            RoguelikeModifiers.Resolver resolver = new RoguelikeModifiers.Resolver
            {
                Rng = duelRng, Decks = new DeckInfo[] { player, opp }, DataDir = dataDirectory,
                Regulation = Regulation, Ascension = run.Ascension,
            };
            RoguelikeModifiers.Apply(starter, duelType, resolver,
                RoguelikeSettings.ModifierDefaults(lpSettings, nodeType), enc.Modifiers);
            // $.Duel drives the production visuals (cosmetics only).
            Dictionary<string, object> duelDto = ds.ToDictionaryForSoloStart();
            duelDto["duelStarterData"] = starter;
            request.Response["Duel"] = duelDto;
            return true;
        }

        // Deterministic per-(run, node) seed for everything random about a combat duel.
        static int DuelRngSeed(long runSeed, int act, int nodeId)
        {
            unchecked { return (int)((uint)(runSeed * 2654435761L) ^ (uint)(act * 0x85EBCA77) ^ (uint)(nodeId * 40503 + 0x9E3779B9)); }
        }

        // Reachable = entry (-1) -> any node in row 0; otherwise the current node's `next`.
        static bool IsReachable(RoguelikeRun run, int target)
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

        void Act_RoguelikeAbandonRun(GameServerWebRequest request)
        {
            RoguelikeRun.Delete(GetPlayerDirectory(request.Player));
            WriteRun(request, new RoguelikeRun { Active = false });
        }

        // Rehydrate the vanilla $.Gacha shape from the rolled openpack cards: 1 entry per pack,
        // cardInfo[].mrk = cid. Used by WriteRun when the pending action is an openpack.
        //
        // Order: same as `cards` (server _cards) — rarity DESC + cid ASC per pack. The Result VC
        // applies a stable rarity-DESC sort to whatever we feed it, so matching the server order
        // here gives anim == result and lets the visual click index double as the server _cards
        // index. We tried inverting (cardInfo ASC for a build-up anim, _cards DESC for result),
        // but the Result re-sort preserves our input's relative order within a rarity, which
        // de-synced anim vs. result. Single sort everywhere is the trade-off for correctness.
        static Dictionary<string, object> BuildGachaProjection(List<object> cards)
        {
            if (cards == null) cards = new List<object>();
            // Group by packIdx
            Dictionary<int, List<Dictionary<string, object>>> byPack = new Dictionary<int, List<Dictionary<string, object>>>();
            foreach (object o in cards)
            {
                Dictionary<string, object> c = o as Dictionary<string, object>;
                if (c == null) continue;
                int pi = Utils.GetValue<int>(c, "packIdx");
                List<Dictionary<string, object>> list;
                if (!byPack.TryGetValue(pi, out list)) { list = new List<Dictionary<string, object>>(); byPack[pi] = list; }
                list.Add(c);
            }
            List<object> packs = new List<object>();
            // Deterministic iteration: sort by packIdx ascending
            List<int> orderedKeys = new List<int>(byPack.Keys);
            orderedKeys.Sort();
            foreach (int pi in orderedKeys)
            {
                List<Dictionary<string, object>> kv_Value = byPack[pi];
                // Animation order: rarity ASC, cid ASC (low rarity first, big reveal last).
                // _cards on the engine side is rarity DESC — we don't touch it; this sort is
                // anim-only.
                kv_Value.Sort((a, b) =>
                {
                    int ra = Utils.GetValue<int>(a, "rarity");
                    int rb = Utils.GetValue<int>(b, "rarity");
                    if (ra != rb) return ra.CompareTo(rb);
                    int ca = Utils.GetValue<int>(a, "cid");
                    int cb = Utils.GetValue<int>(b, "cid");
                    return ca.CompareTo(cb);
                });
                List<object> cardInfo = new List<object>();
                foreach (Dictionary<string, object> c in kv_Value)
                {
                    int rarity = Utils.GetValue<int>(c, "rarity", 1);
                    cardInfo.Add(new Dictionary<string, object>
                    {
                        { "mrk", Utils.GetValue<int>(c, "cid") },
                        { "rarity", rarity },
                        { "backSideRarity", 1 },
                        { "foundSecrets", new int[0] },
                        { "extendSecrets", new int[0] },
                        { "new", Utils.GetValue<bool>(c, "new", true) },
                        { "premiumType", Utils.GetValue<int>(c, "premium", 0) }
                    });
                }
                Dictionary<string, object> effects = new Dictionary<string, object>
                {
                    { "thunder", 1 }, { "rarityup", 1 }, { "cut", 1 }, { "rarityupBg", 1 }, { "rarity", 1 }
                };
                packs.Add(new Dictionary<string, object>
                {
                    { "packInfo", new List<object> { new Dictionary<string, object> { { "effects", effects }, { "cardInfo", cardInfo } } } },
                    { "effects", new Dictionary<string, object> { { "isPickup", false }, { "imageName", "CardPackTex01_0000" }, { "smokeType", 1 } } }
                });
            }
            return new Dictionary<string, object>
            {
                { "drawInfo", new Dictionary<string, object>
                    {
                        { "packs", packs },
                        { "options", new Dictionary<string, object> { { "skippable", true } } }
                    } },
                { "resultInfo", new Dictionary<string, object>
                    {
                        { "isSendGift", false }, { "showSecretFoundResult", false },
                        { "isNextFinalizedUR", false },
                        { "NextFinalizedURNameTextId", "IDS_CARDPACK_ID0001_NAME" }, // vanilla field; the native VC may NRE if missing
                        { "setItems", new List<object>() },
                        { "buyCardFile", 0 }
                    } }
            };
        }
    }
}
