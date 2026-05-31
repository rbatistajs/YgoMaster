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
        public bool DeckChosen;
        public List<object> DeckOffers;            // rolled starter-deck file paths (rel); expanded on the wire
        public Dictionary<string, object> Deck;    // {name, bossCard, deck:{Main,Extra,Side}} or null
        public Dictionary<string, object> Cards;   // run-owned cards (Player.Cards shape: {cid:{st,r,n,...,tn}}); deck pick + rewards
        public Dictionary<string, object> Map;     // RoguelikeMap.ToDictionary() or null
        public int Position = -1;                   // current node id (-1 = entry, before row 0)
        public List<object> Visited;               // ids already walked
        public int Currency;                        // run currency, credited on duel win
        public int PendingDuelNode = -1;            // combat node whose duel is in progress (-1 = none)
        public string PendingEncounterId = "";      // encounter chosen for the in-progress duel (reward lookup)
        public int Lp;                              // current run LP (= player's starting LP each duel)
        public int MaxLp;                           // LP cap (snapshot of playerMaxLp at run start)
        public int Act;                             // current act (0-based)
        public int Ascension;                       // ascension tier this run is played at
        public bool Won;                            // set when the final act's boss falls
        public Dictionary<string, object> PendingAction; // current action-tree node awaiting resolution, or null
                                                         // for openpack the node is enriched in-place with "_cards"/"_size"/"_mode"/"_labels"
        public int ActionToken;                     // bumps each time a new prompt is presented (client dedup)
        public Dictionary<string, int> Pity;            // per-rarity miss counter ({"UR":7, "SR":2}); grows uncapped, capped in Weight

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "version", Version }, { "active", Active }, { "gameType", GameType },
                { "seed", Seed }, { "createdAt", CreatedAt },
                { "deckChosen", DeckChosen },
                { "deckOffers", DeckOffers ?? new List<object>() },
                { "deck", Deck },
                { "Cards", Cards ?? new Dictionary<string, object>() },
                { "map", Map },
                { "position", Position },
                { "visited", Visited ?? new List<object>() },
                { "gold", Currency },
                { "pendingDuelNode", PendingDuelNode },
                { "pendingEncounterId", PendingEncounterId ?? "" },
                { "lp", Lp },
                { "maxLp", MaxLp },
                { "act", Act },
                { "ascension", Ascension },
                { "won", Won },
                { "pendingAction", PendingAction },
                { "actionToken", ActionToken },
                { "pity",         Pity ?? new Dictionary<string, int>() },
            };
        }

        public static RoguelikeRun FromDictionary(Dictionary<string, object> d)
        {
            if (d == null) return new RoguelikeRun { Active = false };
            return new RoguelikeRun
            {
                Version   = Utils.GetValue<int>(d, "version", 1),
                Active    = Utils.GetValue<bool>(d, "active", false),
                GameType  = Utils.GetValue<string>(d, "gameType", "base_deck"),
                Seed      = Utils.GetValue<long>(d, "seed", 0),
                CreatedAt = Utils.GetValue<string>(d, "createdAt", null),
                DeckChosen = Utils.GetValue<bool>(d, "deckChosen", false),
                DeckOffers = Utils.GetValue<List<object>>(d, "deckOffers"),
                Deck       = Utils.GetValue<Dictionary<string, object>>(d, "deck"),
                Cards      = Utils.GetValue<Dictionary<string, object>>(d, "Cards"),
                Map        = Utils.GetValue<Dictionary<string, object>>(d, "map"),
                Position   = Utils.GetValue<int>(d, "position", -1),
                Visited    = Utils.GetValue<List<object>>(d, "visited"),
                Currency   = Utils.GetValue<int>(d, "gold", Utils.GetValue<int>(d, "currency", 0)),
                PendingDuelNode = Utils.GetValue<int>(d, "pendingDuelNode", -1),
                PendingEncounterId = Utils.GetValue<string>(d, "pendingEncounterId", ""),
                Lp         = Utils.GetValue<int>(d, "lp", Utils.GetValue<int>(d, "hp", 0)),    // hp = legacy key
                MaxLp      = Utils.GetValue<int>(d, "maxLp", Utils.GetValue<int>(d, "maxHp", 0)),
                Act        = Utils.GetValue<int>(d, "act", 0),
                Ascension  = Utils.GetValue<int>(d, "ascension", 0),
                Won        = Utils.GetValue<bool>(d, "won", false),
                PendingAction = Utils.GetValue<Dictionary<string, object>>(d, "pendingAction"),
                ActionToken   = Utils.GetValue<int>(d, "actionToken", 0),
                Pity         = ParsePity(Utils.GetValue<Dictionary<string, object>>(d, "pity")),
            };
        }

        // Add `count` normal copies of a card to the run-owned collection (Player.Cards shape). Used
        // on deck pick (deck multiplicity) and by the card-reward flow. Preserves any styled counts.
        public void AddCard(int cardId, int count = 1)
        {
            if (count <= 0) return;
            if (Cards == null) Cards = new Dictionary<string, object>();
            string key = cardId.ToString();
            Dictionary<string, object> e = Utils.GetValue<Dictionary<string, object>>(Cards, key) ?? new Dictionary<string, object>();
            // NoDismantle (p_n) so the deck editor disables the craft/dismantle button for run cards
            // natively — same as how the player's own deck cards are added.
            int n     = Utils.GetValue<int>(e, "n");
            int p1n   = Utils.GetValue<int>(e, "p1n");
            int p2n   = Utils.GetValue<int>(e, "p2n");
            int p_n   = Utils.GetValue<int>(e, "p_n") + count;
            int p_p1n = Utils.GetValue<int>(e, "p_p1n");
            int p_p2n = Utils.GetValue<int>(e, "p_p2n");
            Cards[key] = new Dictionary<string, object>
            {
                { "st", Utils.GetEpochTime() },
                { "r", 1 },
                { "n", n }, { "p1n", p1n }, { "p2n", p2n },
                { "p_n", p_n }, { "p_p1n", p_p1n }, { "p_p2n", p_p2n },
                { "tn", n + p1n + p2n + p_n + p_p1n + p_p2n },
            };
        }

        // Number of cards currently in the run's main / extra deck. Used by reward-routing rules
        // to decide whether a new card slots into the deck (below minCards / under maxMainCards)
        // or just the collection.
        public int GetMainDeckSize()  => GetDeckSectionSize("m");
        public int GetExtraDeckSize() => GetDeckSectionSize("e");

        // Copies of a CANONICAL card currently in the run deck (main + extra + side). Each deck id is
        // resolved through CARD_Same first, so alt-art cids count toward the same canonical card. Used
        // by reward routing to cap deck auto-add at the per-card copy / banlist limit.
        public int CountCanonInDeck(string dataDir, int canon)
        {
            if (Deck == null) return 0;
            Dictionary<string, object> deckInner = Utils.GetValue<Dictionary<string, object>>(Deck, "deck");
            if (deckInner == null) return 0;
            int count = 0;
            foreach (string section in new[] { "m", "e", "s" })
            {
                Dictionary<string, object> sec = Utils.GetValue<Dictionary<string, object>>(deckInner, section);
                List<object> ids = sec != null ? Utils.GetValue<List<object>>(sec, "ids") : null;
                if (ids == null) continue;
                foreach (object o in ids)
                {
                    int c; try { c = Convert.ToInt32(o); } catch { continue; }
                    if (RoguelikeCardPool.Canon(dataDir, c) == canon) count++;
                }
            }
            return count;
        }
        int GetDeckSectionSize(string section)
        {
            if (Deck == null) return 0;
            Dictionary<string, object> deckInner = Utils.GetValue<Dictionary<string, object>>(Deck, "deck");
            if (deckInner == null) return 0;
            Dictionary<string, object> sec = Utils.GetValue<Dictionary<string, object>>(deckInner, section);
            if (sec == null) return 0;
            List<object> ids = Utils.GetValue<List<object>>(sec, "ids");
            return ids != null ? ids.Count : 0;
        }

        // Place cid in the correct deck section of the run deck (Main or Extra). Creates the
        // minimal structure if missing. Used by openpack pick commit so picks land in the
        // active deck without needing a separate edit step.
        public void AddCidToDeck(string dataDir, int cardId)
        {
            if (Deck == null) Deck = new Dictionary<string, object>();
            object deckObj;
            Dictionary<string, object> deckInner;
            if (!Deck.TryGetValue("deck", out deckObj) || !(deckObj is Dictionary<string, object>))
            {
                deckInner = new Dictionary<string, object>();
                Deck["deck"] = deckInner;
            }
            else { deckInner = (Dictionary<string, object>)deckObj; }

            string section = RoguelikeCardPool.IsCardExtraDeck(dataDir, cardId) ? "e" : "m";
            object secObj;
            Dictionary<string, object> sec;
            if (!deckInner.TryGetValue(section, out secObj) || !(secObj is Dictionary<string, object>))
            {
                sec = new Dictionary<string, object>();
                deckInner[section] = sec;
            }
            else { sec = (Dictionary<string, object>)secObj; }

            List<object> ids = Utils.GetValue<List<object>>(sec, "ids") ?? new List<object>();
            List<object> rs  = Utils.GetValue<List<object>>(sec, "r")   ?? new List<object>();
            ids.Add(cardId); rs.Add(1);
            sec["ids"] = ids; sec["r"] = rs;
        }

        // Apply an LP delta from a stat action. Exactly one of absDelta / pctDelta is
        // non-null (validated at load time). pctDelta is a fraction of MaxLp (e.g. -0.30 =
        // -30% of max). Clamps to [0, MaxLp]. Sets killed=true when the result hits 0 so the
        // caller can mark the run dead (game over, same path as a combat loss).
        public int ApplyLpDelta(int? absDelta, double? pctDelta, out bool killed)
        {
            int delta = absDelta ?? (int)Math.Round(MaxLp * (pctDelta ?? 0.0));
            int newLp = Lp + delta;
            if (newLp > MaxLp) newLp = MaxLp;
            if (newLp < 0) newLp = 0;
            Lp = newLp;
            killed = newLp <= 0;
            return newLp;
        }

        // Apply a gold delta from a stat action. pctDelta is a fraction of CURRENT gold (no
        // max cap). Clamps at 0 (no negative gold), no upper bound.
        public int ApplyGoldDelta(int? absDelta, double? pctDelta)
        {
            int delta = absDelta ?? (int)Math.Round(Currency * (pctDelta ?? 0.0));
            int newGold = Currency + delta;
            if (newGold < 0) newGold = 0;
            Currency = newGold;
            return newGold;
        }

        // MiniJSON deserializes the "pity" object's values as boxed objects; coerce to int here.
        static Dictionary<string, int> ParsePity(Dictionary<string, object> raw)
        {
            Dictionary<string, int> r = new Dictionary<string, int>();
            if (raw == null) return r;
            foreach (KeyValuePair<string, object> kv in raw)
            {
                int v; try { v = Convert.ToInt32(kv.Value); } catch { continue; }
                r[kv.Key] = v;
            }
            return r;
        }

        public static string PathFor(string playerDir) => Path.Combine(playerDir, "roguelike.json");

        public static RoguelikeRun Load(string playerDir)
        {
            string p = PathFor(playerDir);
            if (!File.Exists(p)) return new RoguelikeRun { Active = false };
            try
            {
                var d = MiniJSON.Json.DeserializeStripped(File.ReadAllText(p)) as Dictionary<string, object>;
                return FromDictionary(d);
            }
            catch { return new RoguelikeRun { Active = false }; }
        }

        public void Save(string playerDir)
        {
            File.WriteAllText(PathFor(playerDir), MiniJSON.Json.Format(MiniJSON.Json.Serialize(ToDictionary())));
        }

        public static void Delete(string playerDir)
        {
            string p = PathFor(playerDir);
            if (File.Exists(p)) File.Delete(p);
        }
    }
}
