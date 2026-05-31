using System;
using System.Collections.Generic;

namespace YgoMaster
{
    // Server-authoritative walker over an action tree. v1 understands three UI node types:
    //   options  : { type, title, message, options:[ { label, next } ] }     -> branch (await a choice)
    //   message  : { type, title, message, next? }                            -> terminal/chain (await OK)
    //   openpack : { type, packs, pick, pulls:[...], next?, ... }             -> generate cards (await picks)
    // The cursor (run.PendingAction) is the node currently presented; ActionToken bumps on
    // each new prompt so the client renders it once. Project() builds the thin wire payload.
    //
    // openpack flow (unified with options/message):
    //  Step()    -> rolls cards, enriches the same node in-place with "_cards"/"_size"/"_mode"/"_labels",
    //               leaves PendingAction pointing at it (no separate PendingPack state).
    //  Respond() -> reads picks from the payload, validates, commits via run.AddCard + run.AddCidToDeck,
    //               then advances to `next`.
    static class RoguelikeActionEngine
    {
        // Begin an action: set the root as the cursor, then settle on the first UI node.
        public static void Start(RoguelikeRun run, Dictionary<string, object> action,
            string dataDirectory = null, Dictionary<string, object> regulation = null)
        {
            string actType = action != null ? Utils.GetValue<string>(action, "type", "") : "(null)";
            Console.WriteLine("[Roguelike] engine.Start: type=" + actType + " dataDir=" + (dataDirectory != null) + " reg=" + (regulation != null));
            SetPending(run, action);
            Step(run, dataDirectory, regulation);
        }

        // Resolve the current prompt with the player's payload (per-type fields), then settle on the next.
        // Returns true when applied; false when stale (token mismatch) or invalid (bad picks). On false,
        // run state is untouched — the caller may set request.ResultCode.
        public static bool Respond(RoguelikeRun run, Dictionary<string, object> data,
            string dataDirectory = null, Dictionary<string, object> regulation = null)
        {
            Dictionary<string, object> cur = run.PendingAction;
            if (cur == null) return false;
            // Token guard: drops late acks for an already-replaced prompt.
            int token = data != null ? Utils.GetValue<int>(data, "token", -1) : -1;
            if (token != run.ActionToken) { Console.WriteLine("[Roguelike] engine.Respond: stale token " + token + " (expected " + run.ActionToken + ")"); return false; }
            string type = Utils.GetValue<string>(cur, "type", "");
            if (type == "options")
            {
                int choice = data != null ? Utils.GetValue<int>(data, "choice", -1) : -1;
                List<object> opts = Utils.GetValue<List<object>>(cur, "options");
                Dictionary<string, object> chosen = (opts != null && choice >= 0 && choice < opts.Count)
                    ? opts[choice] as Dictionary<string, object> : null;
                SetPending(run, chosen != null ? Utils.GetValue<Dictionary<string, object>>(chosen, "next") : null);
            }
            else if (type == "message")
            {
                SetPending(run, Utils.GetValue<Dictionary<string, object>>(cur, "next"));
            }
            else if (type == "openpack")
            {
                if (!CommitOpenPackPicks(run, cur, data, dataDirectory, regulation)) return false;
                SetPending(run, Utils.GetValue<Dictionary<string, object>>(cur, "next"));
            }
            else if (type == "addCard")
            {
                // Cards were already committed in Step; the ack just means "animation done, continue".
                SetPending(run, Utils.GetValue<Dictionary<string, object>>(cur, "next"));
            }
            else
            {
                SetPending(run, null); // unknown -> done
            }
            Step(run, dataDirectory, regulation);
            return true;
        }

        // Validate picks against the enriched openpack node and commit cards. Returns false on
        // invalid input (out-of-range index, wrong count for the mode); cur is untouched in that case.
        static bool CommitOpenPackPicks(RoguelikeRun run, Dictionary<string, object> cur,
            Dictionary<string, object> data, string dataDirectory, Dictionary<string, object> regulation)
        {
            List<object> cards = Utils.GetValue<List<object>>(cur, "_cards");
            int size = Utils.GetValue<int>(cur, "_size");
            string mode = Utils.GetValue<string>(cur, "_mode");
            int pickMin = Utils.GetValue<int>(cur, "_pickMin");
            int pickMax = Utils.GetValue<int>(cur, "_pickMax");
            List<object> picksRaw = data != null ? Utils.GetValue<List<object>>(data, "picks") : null;
            if (picksRaw == null) picksRaw = new List<object>();
            HashSet<int> picks = new HashSet<int>();
            foreach (object o in picksRaw) { int i; try { i = Convert.ToInt32(o); } catch { continue; } picks.Add(i); }
            foreach (int i in picks) if (i < 0 || i >= size) return false;
            if (mode == "keep" && picks.Count != size) return false;
            if (mode == "pick" && (picks.Count < pickMin || picks.Count > pickMax)) return false;
            if (cards != null)
            {
                Dictionary<string, object> settings = RoguelikeSettings.Load(dataDirectory);
                bool autoAdd = RoguelikeSettings.DeckAutoAdd(settings);
                int minCards = RoguelikeSettings.DeckMinCards(settings);
                int maxMain  = RoguelikeSettings.DeckMaxMainCards(settings);
                int maxExtra = RoguelikeSettings.DeckMaxExtraCards(settings);
                foreach (int idx in picks)
                {
                    Dictionary<string, object> c = cards[idx] as Dictionary<string, object>;
                    if (c == null) continue;
                    CommitRewardCard(run, Utils.GetValue<int>(c, "cid"), dataDirectory, regulation, autoAdd, minCards, maxMain, maxExtra);
                }
            }
            return true;
        }

        // Route one reward card into the run: always add to the collection (keeping the exact cid/art),
        // then add to the deck per the deck.* rules. The deck add is also capped at the card's copy
        // limit — standard 3, reduced by the run regulation's banlist (limited=1, semi=2, forbidden=0).
        // Copies are counted by CANONICAL card (CARD_Same) so alt arts share the limit. When the limit
        // (or the per-section size cap) is reached, the card goes to the collection only.
        static void CommitRewardCard(RoguelikeRun run, int cid, string dataDirectory,
            Dictionary<string, object> regulation, bool autoAdd, int minCards, int maxMain, int maxExtra)
        {
            run.AddCard(cid, 1);
            bool isExtra = RoguelikeCardPool.IsCardExtraDeck(dataDirectory, cid);
            int curSize = isExtra ? run.GetExtraDeckSize() : run.GetMainDeckSize();
            int max = isExtra ? maxExtra : maxMain;
            int canon = RoguelikeCardPool.Canon(dataDirectory, cid);
            int copyCap = Math.Min(3, RoguelikeCardPool.CopyLimit(regulation, RoguelikeCardPool.RegulationId(dataDirectory), canon));
            int copiesInDeck = run.CountCanonInDeck(dataDirectory, canon);
            bool toDeck;
            if (copiesInDeck >= copyCap) toDeck = false;             // at copy/banlist limit -> collection only
            else if (curSize >= max) toDeck = false;                 // section cap reached -> collection only
            else if (!isExtra && curSize < minCards) toDeck = true;  // below min -> mandatory
            else toDeck = autoAdd;                                   // between -> opt-in
            if (toDeck) run.AddCidToDeck(dataDirectory, cid);
        }

        // Advance through non-UI nodes; stop on a UI node (options/message/openpack) or when finished.
        static void Step(RoguelikeRun run,
            string dataDirectory = null, Dictionary<string, object> regulation = null)
        {
            while (run.PendingAction != null)
            {
                Dictionary<string, object> node = run.PendingAction;
                string type = Utils.GetValue<string>(node, "type", "");
                Console.WriteLine("[Roguelike] engine.Step: type=" + type);
                if (type == "options" || type == "message") return; // needs UI; Project() will emit it
                else if (type == "openpack")
                {
                    if (node.ContainsKey("_cards")) return; // already rolled (re-entry on reload); waiting for picks
                    // Clone the openpack node before staging runtime data: the action tree is
                    // shared (Encounters/Actions.json cache) so mutating in-place would pollute
                    // subsequent invocations across calls and players.
                    Dictionary<string, object> clone = new Dictionary<string, object>(node);
                    run.PendingAction = clone;
                    if (!RollOpenPack(run, clone, dataDirectory, regulation)) continue; // 0 cards drawn -> advance
                    return; // staged; awaits picks
                }
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
                else if (type == "addCard")
                {
                    if (node.ContainsKey("_cards")) return; // already applied (re-entry on reload); waiting for the ack
                    // Clone before stashing runtime data: the action tree is shared (Encounters/
                    // Actions.json cache), so mutating in-place would pollute later invocations.
                    Dictionary<string, object> clone = new Dictionary<string, object>(node);
                    run.PendingAction = clone;
                    if (!ApplyAddCard(run, clone, dataDirectory, regulation)) continue; // 0 cards -> advanced to next
                    return; // applied; awaits the animation ack
                }
                else
                {
                    SetPending(run, null); // unknown leaf -> end
                }
            }
        }

        // Read delta fields from an lp action node and apply via the run helper. The XOR contract
        // (delta vs delta_percent) is enforced at load time (RoguelikeEncounters.ValidateActionNode).
        // If LP hits 0, marks the run inactive — game over, same path as a combat loss.
        static void ApplyStatLp(RoguelikeRun run, Dictionary<string, object> node)
        {
            int? abs = null; double? pct = null;
            object v;
            if (node.TryGetValue("delta", out v)) { try { abs = Convert.ToInt32(v); } catch { } }
            if (node.TryGetValue("delta_percent", out v)) { try { pct = Convert.ToDouble(v); } catch { } }
            bool killed;
            int newLp = run.ApplyLpDelta(abs, pct, out killed);
            Console.WriteLine("[Roguelike] lp action: delta=" + (abs.HasValue ? abs.Value.ToString() : "pct " + pct) +
                " -> lp=" + newLp + (killed ? " (LETHAL)" : ""));
            if (killed) run.Active = false;
        }

        // Read delta fields from a gold action node and apply via the run helper.
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

        // Add the action's card(s) to the run (collection + deck routing) and stash the resolved cids
        // on the node ("_cards") for the wire projection — the client plays the add-card animation and
        // acks when it finishes (Respond just advances; the cards are already applied here). Returns
        // false (and advances to `next`) when the action names no cards.
        static bool ApplyAddCard(RoguelikeRun run, Dictionary<string, object> node, string dataDirectory,
            Dictionary<string, object> regulation)
        {
            List<int> cids = ReadCardCids(node);
            // Random roll (same pool/pulls/pity machinery as openpack) when the action specifies a
            // pool instead of (or in addition to) explicit cids. All rolled cards are added.
            if (node.ContainsKey("pulls") || node.ContainsKey("pool"))
                foreach (Dictionary<string, object> c in RollPackCards(run, node, dataDirectory, regulation))
                    cids.Add(Utils.GetValue<int>(c, "cid"));
            if (cids.Count == 0)
            {
                Console.WriteLine("[Roguelike] addCard: no cids; advancing");
                SetPending(run, Utils.GetValue<Dictionary<string, object>>(node, "next"));
                return false;
            }
            Dictionary<string, object> settings = RoguelikeSettings.Load(dataDirectory);
            bool autoAdd = RoguelikeSettings.DeckAutoAdd(settings);
            int minCards = RoguelikeSettings.DeckMinCards(settings);
            int maxMain  = RoguelikeSettings.DeckMaxMainCards(settings);
            int maxExtra = RoguelikeSettings.DeckMaxExtraCards(settings);
            List<object> applied = new List<object>();
            foreach (int cid in cids)
            {
                CommitRewardCard(run, cid, dataDirectory, regulation, autoAdd, minCards, maxMain, maxExtra);
                applied.Add(cid);
            }
            node["_cards"] = applied;
            Console.WriteLine("[Roguelike] addCard: added " + applied.Count + " card(s)");
            return true;
        }

        // Read cids from an addCard node: `cid` (single int) and/or `cards` (array of ints).
        static List<int> ReadCardCids(Dictionary<string, object> node)
        {
            List<int> result = new List<int>();
            object single;
            if (node.TryGetValue("cid", out single)) { try { result.Add(Convert.ToInt32(single)); } catch { } }
            List<object> arr = Utils.GetValue<List<object>>(node, "cards");
            if (arr != null) foreach (object o in arr) { try { result.Add(Convert.ToInt32(o)); } catch { } }
            return result;
        }

        // Roll cards for this openpack node and stash them on the node ("_cards"/"_size"/"_mode"/"_labels").
        // Returns true when at least one card was drawn (UI will follow); false on empty result
        // (the caller should advance to `next`).
        static bool RollOpenPack(RoguelikeRun run, Dictionary<string, object> node,
            string dataDirectory, Dictionary<string, object> regulation)
        {
            // pick:
            //   absent or 0   -> keep mode (pick everything, no selection UI)
            //   N (int > 0)   -> exact-pick mode (min=max=N)
            //   {min,max} obj -> range mode; min defaults to max if missing, max defaults to min
            //                    or 0 if both missing. max is clamped to pack size at stage time.
            int pickMin, pickMax;
            ParsePick(node, out pickMin, out pickMax);
            List<Dictionary<string, object>> allCards = RollPackCards(run, node, dataDirectory, regulation);

            int size = allCards.Count;
            Console.WriteLine("[Roguelike] openpack staged: size=" + size + " pick=" + pickMin + "-" + pickMax + " token=" + run.ActionToken);
            if (size == 0)
            {
                // No cards drawn (empty universe / over-filtered pool / weights all zero).
                // Advance to `next` (or terminate) instead of staging an empty pack.
                Console.WriteLine("[Roguelike] openpack: 0 cards drawn; skipping stage and advancing");
                SetPending(run, Utils.GetValue<Dictionary<string, object>>(node, "next"));
                return false;
            }
            // Clamp the range to the rolled pack size (asking for 10 picks from a 5-card pack
            // is nonsense). pickMax <= 0 implies keep mode regardless of how it was specified.
            if (pickMax > size) pickMax = size;
            if (pickMin > pickMax) pickMin = pickMax;
            if (pickMin < 0) pickMin = 0;
            string mode = pickMax > 0 ? "pick" : "keep";
            // Within each pack: sort DESC by rarity (cid asc tiebreaker) so the persisted order
            // matches what the vanilla Result VC shows (it always reorders rarity desc). Picks
            // indices from the client then map 1:1 to _cards[i] for the commit. The Gacha
            // projection (animation) inverts to ASC per pack so the dramatic build-up reads
            // low-rarity first, high last.
            allCards.Sort((a, b) =>
            {
                int pa = Utils.GetValue<int>(a, "packIdx");
                int pb = Utils.GetValue<int>(b, "packIdx");
                if (pa != pb) return pa.CompareTo(pb);
                int ra = Utils.GetValue<int>(a, "rarity");
                int rb = Utils.GetValue<int>(b, "rarity");
                if (ra != rb) return rb.CompareTo(ra); // rarity desc
                int ca = Utils.GetValue<int>(a, "cid");
                int cb = Utils.GetValue<int>(b, "cid");
                return ca.CompareTo(cb);
            });
            node["_cards"] = allCards.ConvertAll(c => (object)c);
            node["_size"] = size;
            node["_mode"] = mode;
            node["_pickMin"] = pickMin;
            node["_pickMax"] = pickMax;
            node["_labels"] = BuildOpenPackLabels(node, pickMin, pickMax, size);
            return true;
        }

        // Draw cards from a node's `packs` × `pulls` spec (with pity + rarity rates). Shared by
        // openpack (which stages a pick afterward) and addCard (which commits all). Returns the
        // rolled card dicts {cid, rarity, new, premium, packIdx}. Accepts a `pool` + `count`
        // shorthand (one implicit pull) in addition to an explicit `pulls` array.
        static List<Dictionary<string, object>> RollPackCards(RoguelikeRun run, Dictionary<string, object> node,
            string dataDirectory, Dictionary<string, object> regulation)
        {
            int packs = Utils.GetValue<int>(node, "packs", 1);
            List<object> pulls = Utils.GetValue<List<object>>(node, "pulls");
            if (pulls == null)
            {
                Dictionary<string, object> pool0 = Utils.GetValue<Dictionary<string, object>>(node, "pool");
                if (pool0 != null)
                    pulls = new List<object> { new Dictionary<string, object> {
                        { "count", Utils.GetValue<int>(node, "count", 1) }, { "pool", pool0 } } };
            }
            Console.WriteLine("[Roguelike] rollpack: packs=" + packs + " pulls=" + (pulls != null ? pulls.Count : 0));

            // pity: node.pity = false disables; merge global+asc+action
            object pityRaw;
            bool pityEnabled = true;
            Dictionary<string, object> actionPity = null;
            if (node.TryGetValue("pity", out pityRaw))
            {
                if (pityRaw is bool && !(bool)pityRaw) pityEnabled = false;
                else actionPity = pityRaw as Dictionary<string, object>;
            }
            Dictionary<int, RoguelikeCardPool.PityConfig> pityCfg =
                pityEnabled ? MergePity(RoguelikeCardPool.Pity(dataDirectory, run.Ascension), actionPity) : null;
            if (run.Pity == null) run.Pity = new Dictionary<string, int>();

            Random rng = new Random(unchecked((int)(run.Seed ^ ((long)run.ActionToken * 2654435761L)))); // Knuth multiplicative hash
            HashSet<int> anyPool = RoguelikeCardPool.AnyPool(dataDirectory, regulation, run.Ascension);

            List<Dictionary<string, object>> allCards = new List<Dictionary<string, object>>();
            for (int packIdx = 0; packIdx < packs; packIdx++)
            {
                HashSet<int> usedPack = new HashSet<int>();
                List<RoguelikeCardPool.DrawResult> packDraws = new List<RoguelikeCardPool.DrawResult>();
                if (pulls != null)
                    foreach (object pullObj in pulls)
                    {
                        Dictionary<string, object> pull = pullObj as Dictionary<string, object>;
                        if (pull == null) continue;
                        double chance = Utils.GetValue<double>(pull, "chance", 1.0);
                        if (chance < 1.0 && rng.NextDouble() >= chance) continue;
                        int count = Utils.GetValue<int>(pull, "count", 0);
                        Dictionary<string, object> pool = Utils.GetValue<Dictionary<string, object>>(pull, "pool");
                        string source = pool != null ? Utils.GetValue<string>(pool, "source", "any") : "any";
                        if (source != "any")
                            Console.WriteLine("[Roguelike] pool.source '" + source + "' not supported; falling back to 'any'");
                        // rarityRates: action override + pity bonus, on top of layered (global+asc) rates
                        Dictionary<int, double> rrEffective = MergeRarityRatesWithPity(
                            RoguelikeCardPool.LayeredRarityRates(dataDirectory, run.Ascension),
                            pool != null ? Utils.GetValue<Dictionary<string, object>>(pool, "rarityRates") : null,
                            pityCfg, run.Pity);
                        List<RoguelikeCardPool.DrawResult> drawn = RoguelikeCardPool.DrawN(
                            dataDirectory, anyPool, pool, count, rng, run.Ascension, usedPack, rrEffective, true);
                        Console.WriteLine("[Roguelike] DrawN: requested=" + count + " got=" + drawn.Count);
                        packDraws.AddRange(drawn);
                    }
                foreach (RoguelikeCardPool.DrawResult d in packDraws)
                    allCards.Add(new Dictionary<string, object>
                    {
                        { "cid", d.Cid }, { "rarity", d.Rarity },
                        { "new", d.IsNew }, { "premium", d.PremiumType }, { "packIdx", packIdx }
                    });
                if (pityCfg != null) UpdatePity(run.Pity, packDraws, pityCfg);
            }
            return allCards;
        }

        // pick:
        //   absent / null    -> 0/0 (keep mode)
        //   N (int)          -> N/N
        //   { min, max } obj -> uses both; defaults each to the other if only one present.
        // Negative values are normalized to 0.
        static void ParsePick(Dictionary<string, object> node, out int min, out int max)
        {
            min = 0; max = 0;
            if (node == null) return;
            object raw;
            if (!node.TryGetValue("pick", out raw) || raw == null) return;
            Dictionary<string, object> obj = raw as Dictionary<string, object>;
            if (obj != null)
            {
                bool hasMin = obj.ContainsKey("min");
                bool hasMax = obj.ContainsKey("max");
                if (hasMin) try { min = Convert.ToInt32(obj["min"]); } catch { }
                if (hasMax) try { max = Convert.ToInt32(obj["max"]); } catch { }
                if (hasMin && !hasMax) max = min;
                else if (hasMax && !hasMin) min = max;
            }
            else
            {
                try { max = Convert.ToInt32(raw); min = max; } catch { }
            }
            if (min < 0) min = 0;
            if (max < 0) max = 0;
            if (min > max) min = max;
        }

        static void SetPending(RoguelikeRun run, Dictionary<string, object> node)
        {
            run.PendingAction = node;
            if (node != null) run.ActionToken++;
        }

        // Wire prompt for $.Roguelike.action: { type, token, data:{...} } or null.
        // For openpack, the rolled cards live on the node as "_cards" (server-only); the wire
        // exposes just mode/pick/size/labels — the cards ride along via $.Gacha (vanilla shape).
        public static Dictionary<string, object> Project(RoguelikeRun run)
        {
            Dictionary<string, object> cur = run.PendingAction;
            if (cur == null) return null;
            string type = Utils.GetValue<string>(cur, "type", "");
            Dictionary<string, object> data = new Dictionary<string, object>();
            if (type == "options")
            {
                // title = header, message = body. `text` is a legacy alias for title.
                data["title"]   = Utils.GetValue<string>(cur, "title", Utils.GetValue<string>(cur, "text", ""));
                data["message"] = Utils.GetValue<string>(cur, "message", "");
                List<object> labels = new List<object>();
                List<object> opts = Utils.GetValue<List<object>>(cur, "options");
                if (opts != null)
                    foreach (object o in opts)
                    {
                        Dictionary<string, object> od = o as Dictionary<string, object>;
                        labels.Add(od != null ? Utils.GetValue<string>(od, "label", "") : "");
                    }
                data["options"] = labels;
            }
            else if (type == "message")
            {
                data["title"]   = Utils.GetValue<string>(cur, "title", Utils.GetValue<string>(cur, "text", ""));
                data["message"] = Utils.GetValue<string>(cur, "message", "");
            }
            else if (type == "openpack")
            {
                // Only expose post-roll fields. If the node hasn't been rolled yet (e.g., 0-card stage
                // already advanced), skip projection — the cursor moved on and Step will settle.
                if (!cur.ContainsKey("_cards")) return null;
                data["mode"]    = Utils.GetValue<string>(cur, "_mode");
                // pick == max for retrocompat (older clients read just `pick`); pickMin is new.
                data["pick"]    = Utils.GetValue<int>(cur, "_pickMax");
                data["pickMin"] = Utils.GetValue<int>(cur, "_pickMin");
                data["size"]    = Utils.GetValue<int>(cur, "_size");
                data["labels"] = Utils.GetValue<Dictionary<string, object>>(cur, "_labels") ?? new Dictionary<string, object>();
            }
            else if (type == "addCard")
            {
                // Cards already applied in Step; expose the cids so the client plays the fly animation
                // and acks when it's done. Skip projection if nothing was applied (0-card advanced).
                if (!cur.ContainsKey("_cards")) return null;
                data["cards"] = Utils.GetValue<List<object>>(cur, "_cards") ?? new List<object>();
            }
            return new Dictionary<string, object>
            {
                { "type",  type },
                { "token", run.ActionToken },
                { "data",  data },
            };
        }

        // Server-only: rolled cards for the current openpack node (for the $.Gacha projection).
        // Returns null when no openpack is pending. Mirrors RoguelikeRun internals; not on the wire.
        public static List<object> PendingOpenPackCards(RoguelikeRun run)
        {
            Dictionary<string, object> cur = run.PendingAction;
            if (cur == null) return null;
            if (Utils.GetValue<string>(cur, "type", "") != "openpack") return null;
            return Utils.GetValue<List<object>>(cur, "_cards");
        }

        // Merge: global+asc -> action (per-field within rarity). null `action` = global+asc only.
        static Dictionary<int, RoguelikeCardPool.PityConfig> MergePity(
            Dictionary<int, RoguelikeCardPool.PityConfig> globalCfg,
            Dictionary<string, object> action)
        {
            Dictionary<int, RoguelikeCardPool.PityConfig> merged =
                new Dictionary<int, RoguelikeCardPool.PityConfig>(globalCfg);
            if (action == null) return merged;
            foreach (KeyValuePair<string, object> kv in action)
            {
                int r = RoguelikeCardPool.RarityKey(kv.Key);
                if (r <= 0) continue;
                Dictionary<string, object> entry = kv.Value as Dictionary<string, object>;
                if (entry == null) continue;
                RoguelikeCardPool.PityConfig pc;
                if (!merged.TryGetValue(r, out pc)) pc = new RoguelikeCardPool.PityConfig
                    { Increment = 0, Max = 0, ResetOn = new HashSet<int> { r } };
                object v;
                if (entry.TryGetValue("increment", out v)) { try { pc.Increment = Convert.ToDouble(v); } catch { } }
                if (entry.TryGetValue("max", out v))       { try { pc.Max = Convert.ToDouble(v); } catch { } }
                List<object> rs = Utils.GetValue<List<object>>(entry, "reset_on");
                if (rs != null)
                {
                    HashSet<int> set = new HashSet<int>();
                    foreach (object o in rs) { int rr = RoguelikeCardPool.RarityKey(Convert.ToString(o)); if (rr > 0) set.Add(rr); }
                    if (set.Count > 0) pc.ResetOn = set;
                }
                merged[r] = pc;
            }
            return merged;
        }

        // Effective rarityRates: layered (global+asc) -> action override (per-key) -> + pity bonus.
        static Dictionary<int, double> MergeRarityRatesWithPity(
            Dictionary<int, double> layered,
            Dictionary<string, object> actionOverride,
            Dictionary<int, RoguelikeCardPool.PityConfig> pityCfg,
            Dictionary<string, int> pity)
        {
            Dictionary<int, double> rates = layered != null
                ? new Dictionary<int, double>(layered)
                : new Dictionary<int, double>();
            if (actionOverride != null)
                foreach (KeyValuePair<string, object> kv in actionOverride)
                {
                    int rk = RoguelikeCardPool.RarityKey(kv.Key);
                    if (rk <= 0) continue;
                    double w; try { w = Convert.ToDouble(kv.Value); } catch { continue; }
                    rates[rk] = w;
                }
            if (pityCfg != null && pity != null)
                foreach (KeyValuePair<int, RoguelikeCardPool.PityConfig> kv in pityCfg)
                {
                    int rk = kv.Key;
                    int counter; pity.TryGetValue(RarityToKey(rk), out counter);
                    double bonus = Math.Min(counter * kv.Value.Increment, kv.Value.Max);
                    double cur; rates.TryGetValue(rk, out cur);
                    rates[rk] = cur + bonus;
                }
            return rates;
        }

        // Update counters after a pack: increment if no card has a rarity in reset_on; otherwise zero.
        static void UpdatePity(Dictionary<string, int> pity, List<RoguelikeCardPool.DrawResult> packCards,
                               Dictionary<int, RoguelikeCardPool.PityConfig> pityCfg)
        {
            HashSet<int> raritiesInPack = new HashSet<int>();
            foreach (RoguelikeCardPool.DrawResult d in packCards) raritiesInPack.Add(d.Rarity);
            foreach (KeyValuePair<int, RoguelikeCardPool.PityConfig> kv in pityCfg)
            {
                string key = RarityToKey(kv.Key);
                bool hit = false;
                foreach (int rr in kv.Value.ResetOn) if (raritiesInPack.Contains(rr)) { hit = true; break; }
                int cur; pity.TryGetValue(key, out cur);
                pity[key] = hit ? 0 : cur + 1;
            }
        }

        static string RarityToKey(int r)
        {
            switch (r) { case 1: return "N"; case 2: return "R"; case 3: return "SR"; case 4: return "UR"; }
            return "?";
        }

        // Resolve labels with passthrough; interpolate title_pick with {0}=max, {1}=size, {2}=min
        // (positional retrocompat: existing templates using {0}=pick, {1}=size still work, because
        // pickMax replaces pick when min==max). confirm_label is left as-is — the client formats
        // it live with the running selection count.
        // Returns ONLY the keys the action specified — client falls back to RoguelikeLabels defaults.
        static Dictionary<string, object> BuildOpenPackLabels(Dictionary<string, object> node, int pickMin, int pickMax, int size)
        {
            if (node == null) return new Dictionary<string, object>();
            string titleKeep = Utils.GetValue<string>(node, "title_keep", null);
            string titlePick = Utils.GetValue<string>(node, "title_pick", null);
            string confirm   = Utils.GetValue<string>(node, "confirm_label", null);
            Dictionary<string, object> r = new Dictionary<string, object>();
            if (titleKeep != null) r["title_keep"] = titleKeep;
            if (titlePick != null) r["title_pick"] = string.Format(titlePick, pickMax, size, pickMin);
            if (confirm   != null) r["confirm"]    = confirm;
            return r;
        }
    }
}
