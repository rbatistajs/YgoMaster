using System;
using System.Collections.Generic;
using System.IO;

namespace YgoMaster
{
    // Pure-C# card classifier for random modifier specs — no duel.dll. Parses CARD_Prop via
    // YdkHelper.GameCardInfo (cached once) and answers "does this cid match this random spec?"
    // (kind / subtype / atk-def-level filters). Avoids the server-side DLL card queries that
    // caused table corruption before.
    static class RoguelikeCardPool
    {
        static Dictionary<int, YdkHelper.GameCardInfo> _cards;
        static readonly object _lock = new object();

        static Dictionary<int, YdkHelper.GameCardInfo> Cards(string dataDirectory)
        {
            if (_cards != null) return _cards;
            lock (_lock)
            {
                if (_cards == null)
                {
                    try { _cards = YdkHelper.LoadCardDataFromGame(dataDirectory); }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[Roguelike] card data load failed (random filters disabled): " + ex.Message);
                        _cards = new Dictionary<int, YdkHelper.GameCardInfo>();
                    }
                }
            }
            return _cards;
        }

        // spell/trap subtype label -> CardIcon int (same bit field as GameCardInfo.Icon).
        static readonly Dictionary<string, int> SpellTrapIcon = new Dictionary<string, int>
        {
            { "normal", 0 }, { "counter", 1 }, { "field", 2 }, { "equip", 3 },
            { "continuous", 4 }, { "quickplay", 5 }, { "ritual", 6 },
        };

        // True if cid satisfies the spec's random kind / subtype / numeric filters.
        public static bool Matches(string dataDirectory, int cid, Dictionary<string, object> spec)
        {
            YdkHelper.GameCardInfo c;
            // NOTE: do NOT gate on c.Exist — in mod installs with a curated CardList.json, many real
            // playable cards have Exist=false in the binary (it's a legacy/cosmetic flag). The actual
            // data integrity is enforced by PassesKind/Numeric below (which reject empty/zeroed slots).
            if (!Cards(dataDirectory).TryGetValue(cid, out c)) return false;
            if (!PassesKind(Utils.GetValue<string>(spec, "random"), c)) return false;
            string subtype = Utils.GetValue<string>(spec, "subtype");
            if (!string.IsNullOrEmpty(subtype) && !PassesSubtype(subtype.ToLowerInvariant(), c)) return false;
            if (c.IsMonster && !PassesNumeric(spec, c)) return false;
            if (!PassesRarity(spec, cid, dataDirectory)) return false;
            return true;
        }

        static bool PassesKind(string rkind, YdkHelper.GameCardInfo c)
        {
            if (string.IsNullOrEmpty(rkind) || rkind == "any" || rkind == "true") return true;
            bool isSpell = c.Frame == CardFrame.Magic;
            bool isTrap = c.Frame == CardFrame.Trap;
            switch (rkind)
            {
                case "monster":       return c.IsMonster;
                case "main_monster":  return c.IsMonster && c.IsMainDeck;
                case "extra_monster": return c.IsMonster && c.IsExtraDeck;
                case "spell":         return isSpell;
                case "trap":          return isTrap;
                case "field_spell":   return isSpell && (int)c.Icon == 2;
                case "spell_or_trap": return isSpell || isTrap;
                default:              return true; // unknown kind = no filter
            }
        }

        static bool PassesSubtype(string subtype, YdkHelper.GameCardInfo c)
        {
            if (c.IsMonster) return MonsterSubtype(subtype, c.Frame);
            if (c.Frame == CardFrame.Magic || c.Frame == CardFrame.Trap)
            {
                int icon;
                return SpellTrapIcon.TryGetValue(subtype, out icon) && (int)c.Icon == icon;
            }
            return false; // subtype set but card is neither monster nor spell/trap
        }

        // Coarse monster subtype by frame (DLL-free).
        static bool MonsterSubtype(string subtype, CardFrame frame)
        {
            switch (subtype)
            {
                case "normal":  return frame == CardFrame.Normal;
                case "effect":  return frame == CardFrame.Effect;
                case "ritual":  return frame == CardFrame.Ritual || frame == CardFrame.RitualPend;
                case "fusion":  return frame == CardFrame.Fusion || frame == CardFrame.FusionPend;
                case "synchro": return frame == CardFrame.Sync || frame == CardFrame.SyncPend;
                case "xyz":     return frame == CardFrame.Xyz || frame == CardFrame.XyzPend;
                case "link":    return frame == CardFrame.Link;
                default:        return false;
            }
        }

        static bool PassesNumeric(Dictionary<string, object> spec, YdkHelper.GameCardInfo c)
        {
            int v;
            if (TryInt(spec, "minAtk", out v) && c.Atk < v) return false;
            if (TryInt(spec, "maxAtk", out v) && c.Atk > v) return false;
            if (TryInt(spec, "minDef", out v) && c.Def < v) return false;
            if (TryInt(spec, "maxDef", out v) && c.Def > v) return false;
            if (TryInt(spec, "minLevel", out v) && c.Level < v) return false;
            if (TryInt(spec, "maxLevel", out v) && c.Level > v) return false;
            return true;
        }

        // "rarity": "UR" / "rarities": ["SR","UR"]. None = no filter.
        static bool PassesRarity(Dictionary<string, object> spec, int cid, string dataDirectory)
        {
            if (spec == null) return true;
            HashSet<int> allowed = null;
            object single, multi;
            if (spec.TryGetValue("rarity", out single) && single != null)
            {
                int r = RarityKey(Convert.ToString(single));
                if (r > 0) { allowed = new HashSet<int>(); allowed.Add(r); }
            }
            if (spec.TryGetValue("rarities", out multi) && multi is List<object>)
            {
                if (allowed == null) allowed = new HashSet<int>();
                foreach (object o in (List<object>)multi)
                {
                    int r = RarityKey(Convert.ToString(o));
                    if (r > 0) allowed.Add(r);
                }
            }
            if (allowed == null || allowed.Count == 0) return true;
            int rarity;
            if (!CardListRarity(dataDirectory).TryGetValue(cid, out rarity)) return false;
            return allowed.Contains(rarity);
        }

        static bool TryInt(Dictionary<string, object> d, string key, out int value)
        {
            value = 0;
            object o;
            if (d == null || !d.TryGetValue(key, out o) || o == null) return false;
            try { value = Convert.ToInt32(o); return true; } catch { return false; }
        }

        // ----- "any" pool (DataLE/Roguelike/CardPool.json) -----
        // Configurable cid pool for `source:"any"` random picks (and, later, card rewards). Base =
        // CardList minus the configured/default regulation's banlist; then global and per-ascension
        // include/exclude. Cached per ascension; restart the server to apply edits.
        static Dictionary<string, object> _poolCfg;
        static bool _poolCfgLoaded;
        static readonly Dictionary<int, HashSet<int>> _anyByAsc = new Dictionary<int, HashSet<int>>();

        static Dictionary<string, object> PoolConfig(string dataDirectory)
        {
            if (_poolCfgLoaded) return _poolCfg;
            _poolCfgLoaded = true;
            string p = Path.Combine(dataDirectory, "Roguelike", "CardPool.json");
            if (File.Exists(p))
            {
                try { _poolCfg = MiniJSON.Json.DeserializeStripped(File.ReadAllText(p)) as Dictionary<string, object>; }
                catch (Exception ex) { Console.WriteLine("[Roguelike] CardPool.json parse EX: " + ex.Message); }
            }
            return _poolCfg;
        }

        // Regulation id for the pool's configured regulation name (CardPool.json "regulation"), or the
        // default when unset/unknown. Used to drive the run deck editor's banlist.
        public static int RegulationId(string dataDirectory)
        {
            Dictionary<string, object> cfg = PoolConfig(dataDirectory);
            string regName = cfg != null ? Utils.GetValue<string>(cfg, "regulation") : null;
            int regId;
            if (!string.IsNullOrEmpty(regName) && DeckInfo.RegulationIdsByName.TryGetValue(regName, out regId))
                return regId;
            return DeckInfo.DefaultRegulationId;
        }

        public static HashSet<int> AnyPool(string dataDirectory, Dictionary<string, object> regulation, int ascension)
        {
            HashSet<int> cached;
            if (_anyByAsc.TryGetValue(ascension, out cached)) return cached;

            Dictionary<string, object> cfg = PoolConfig(dataDirectory);
            HashSet<int> pool = LoadCardListCids(dataDirectory);

            int regId = DeckInfo.DefaultRegulationId;
            string regName = cfg != null ? Utils.GetValue<string>(cfg, "regulation") : null;
            if (!string.IsNullOrEmpty(regName) && !DeckInfo.RegulationIdsByName.TryGetValue(regName, out regId))
            {
                Console.WriteLine("[Roguelike] CardPool regulation '" + regName + "' unknown — using default");
                regId = DeckInfo.DefaultRegulationId;
            }
            pool.ExceptWith(Banned(regulation, regId));

            if (cfg != null)
            {
                ApplyIncludeExclude(pool, Utils.GetValue<List<object>>(cfg, "include"), Utils.GetValue<List<object>>(cfg, "exclude"));
                Dictionary<string, object> asc = ItemAt(Utils.GetValue<List<object>>(cfg, "byAscension"), ascension);
                if (asc != null)
                    ApplyIncludeExclude(pool, Utils.GetValue<List<object>>(asc, "include"), Utils.GetValue<List<object>>(asc, "exclude"));
            }

            _anyByAsc[ascension] = pool;
            Console.WriteLine("[Roguelike] any-pool asc " + ascension + ": " + pool.Count + " cids");
            return pool;
        }

        // True if this cid is an Extra Deck card (fusion/synchro/xyz/link), using the cached card data.
        // Returns false (Main) for unknown cids.
        public static bool IsCardExtraDeck(string dataDirectory, int cid)
        {
            YdkHelper.GameCardInfo info;
            return Cards(dataDirectory).TryGetValue(cid, out info) && info.IsExtraDeck;
        }

        static void ApplyIncludeExclude(HashSet<int> pool, List<object> include, List<object> exclude)
        {
            if (include != null) foreach (object o in include) { int c; if (TryCid(o, out c)) pool.Add(c); }
            if (exclude != null) foreach (object o in exclude) { int c; if (TryCid(o, out c)) pool.Remove(c); }
        }

        static HashSet<int> Banned(Dictionary<string, object> regulation, int regId)
        {
            HashSet<int> banned = new HashSet<int>();
            Dictionary<string, object> entry = regulation != null
                ? Utils.GetValue<Dictionary<string, object>>(regulation, regId.ToString()) : null;
            Dictionary<string, object> avail = entry != null
                ? Utils.GetValue<Dictionary<string, object>>(entry, "available") : null;
            List<object> a0 = avail != null ? Utils.GetValue<List<object>>(avail, "a0") : null;
            if (a0 != null) foreach (object o in a0) { int c; if (TryCid(o, out c)) banned.Add(c); }
            return banned;
        }

        // ----- CARD_Same (alt-art lineage) -----
        // variant cid -> canonical cid, chain-resolved. Read once from DataLE/CardData/MD/CARD_Same.bytes
        // (array of uint16 trios: variant, canon, slotIdx — 6 bytes each). Mirrors the Settings editor's
        // CardSameLookup. Cards with no entry are their own canon.
        static Dictionary<int, int> _sameResolved;

        // Resolve a cid to its canonical card id (alt arts share a canon). Returns the cid itself when
        // there's no CARD_Same entry. Used by reward routing so copy/banlist limits treat alt arts as
        // the same card.
        public static int Canon(string dataDirectory, int cid)
        {
            EnsureSame(dataDirectory);
            int canon;
            return _sameResolved.TryGetValue(cid, out canon) ? canon : cid;
        }

        static void EnsureSame(string dataDirectory)
        {
            if (_sameResolved != null) return;
            _sameResolved = new Dictionary<int, int>();
            Dictionary<int, int> direct = new Dictionary<int, int>();
            string p = Path.Combine(dataDirectory, "CardData", "MD", "CARD_Same.bytes");
            if (!File.Exists(p)) { Console.WriteLine("[Roguelike] no CARD_Same.bytes (alt arts won't share a canon)"); return; }
            try
            {
                byte[] data = File.ReadAllBytes(p);
                int n = data.Length / 6;
                for (int i = 0; i < n; i++)
                {
                    int off = i * 6;
                    int variant = data[off] | (data[off + 1] << 8);
                    int canon   = data[off + 2] | (data[off + 3] << 8);
                    if (!direct.ContainsKey(variant)) direct[variant] = canon;
                }
                foreach (KeyValuePair<int, int> kv in direct)
                {
                    int cur = kv.Value;
                    HashSet<int> seen = new HashSet<int> { kv.Key };
                    int next;
                    while (direct.TryGetValue(cur, out next))
                    {
                        if (next == cur || seen.Contains(cur)) break;
                        seen.Add(cur);
                        cur = next;
                    }
                    _sameResolved[kv.Key] = cur;
                }
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] CARD_Same load EX: " + ex.Message); }
        }

        // Allowed copies of a cid under a regulation's banlist: a0 = 0 (forbidden), a1 = 1 (limited),
        // a2 = 2 (semi-limited), else 3 (a3 / unlisted). Used by reward routing to cap deck auto-add.
        public static int CopyLimit(Dictionary<string, object> regulation, int regId, int cid)
        {
            Dictionary<string, object> entry = regulation != null
                ? Utils.GetValue<Dictionary<string, object>>(regulation, regId.ToString()) : null;
            Dictionary<string, object> avail = entry != null
                ? Utils.GetValue<Dictionary<string, object>>(entry, "available") : null;
            if (avail == null) return 3;
            if (BucketHas(avail, "a0", cid)) return 0;
            if (BucketHas(avail, "a1", cid)) return 1;
            if (BucketHas(avail, "a2", cid)) return 2;
            return 3;
        }

        static bool BucketHas(Dictionary<string, object> avail, string key, int cid)
        {
            List<object> list = Utils.GetValue<List<object>>(avail, key);
            if (list == null) return false;
            foreach (object o in list) { int c; if (TryCid(o, out c) && c == cid) return true; }
            return false;
        }

        static HashSet<int> LoadCardListCids(string dataDirectory)
        {
            return new HashSet<int>(CardListRarity(dataDirectory).Keys);
        }

        // ----- rate weighting (rarity + groups; CardPool.json) -----
        // CardList.json is { cid: rarity } with rarity 1=N 2=R 3=SR 4=UR.
        static Dictionary<int, int> _rarityByCid;
        static bool _rarityLoaded;

        static Dictionary<int, int> CardListRarity(string dataDirectory)
        {
            if (_rarityLoaded) return _rarityByCid;
            _rarityLoaded = true;
            _rarityByCid = new Dictionary<int, int>();
            string path = Path.Combine(dataDirectory, "CardList.json");
            if (!File.Exists(path)) { Console.WriteLine("[Roguelike] CardList.json not found — 'any' pool empty"); return _rarityByCid; }
            try
            {
                Dictionary<string, object> doc = MiniJSON.Json.DeserializeStripped(File.ReadAllText(path)) as Dictionary<string, object>;
                if (doc != null)
                    foreach (KeyValuePair<string, object> kv in doc)
                    {
                        int c; if (!int.TryParse(kv.Key, out c)) continue;
                        int r; try { r = Convert.ToInt32(kv.Value); } catch { r = 0; }
                        _rarityByCid[c] = r;
                    }
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] CardList.json parse EX: " + ex.Message); }
            return _rarityByCid;
        }

        class WeightCtx { public Dictionary<int, double> RarityRate; public Dictionary<int, double> GroupMult; }
        static readonly Dictionary<int, WeightCtx> _weightByAsc = new Dictionary<int, WeightCtx>();

        public class PityConfig
        {
            public double Increment;          // bonus added to effective rarityRate per miss
            public double Max;                // cap on the accumulated bonus
            public HashSet<int> ResetOn;      // rarities (1..4) that zero this counter when pulled
        }

        class PityCtx { public Dictionary<int, PityConfig> ByRarity = new Dictionary<int, PityConfig>(); }
        static readonly Dictionary<int, PityCtx> _pityByAsc = new Dictionary<int, PityCtx>();

        static WeightCtx Weights(string dataDirectory, int ascension)
        {
            WeightCtx cached;
            if (_weightByAsc.TryGetValue(ascension, out cached)) return cached;

            WeightCtx ctx = new WeightCtx { RarityRate = new Dictionary<int, double>(), GroupMult = new Dictionary<int, double>() };
            Dictionary<string, object> cfg = PoolConfig(dataDirectory);
            if (cfg != null)
            {
                ParseRarityRates(ctx.RarityRate, Utils.GetValue<Dictionary<string, object>>(cfg, "rarityRates"));
                ParseGroups(ctx.GroupMult, Utils.GetValue<List<object>>(cfg, "rateGroups"));
                Dictionary<string, object> asc = ItemAt(Utils.GetValue<List<object>>(cfg, "byAscension"), ascension);
                if (asc != null)
                {
                    ParseRarityRates(ctx.RarityRate, Utils.GetValue<Dictionary<string, object>>(asc, "rarityRates")); // override per rarity
                    ParseGroups(ctx.GroupMult, Utils.GetValue<List<object>>(asc, "rateGroups"));                       // stack with global
                }
            }
            _weightByAsc[ascension] = ctx;
            return ctx;
        }

        // Snapshot of the rarityRates merged from global + per-ascension config (no action override).
        public static Dictionary<int, double> LayeredRarityRates(string dataDirectory, int ascension)
        {
            return new Dictionary<int, double>(Weights(dataDirectory, ascension).RarityRate);
        }

        public static Dictionary<int, PityConfig> Pity(string dataDirectory, int ascension)
        {
            PityCtx cached;
            if (_pityByAsc.TryGetValue(ascension, out cached)) return cached.ByRarity;

            PityCtx ctx = new PityCtx();
            Dictionary<string, object> cfg = PoolConfig(dataDirectory);
            if (cfg != null)
            {
                ParsePity(ctx.ByRarity, Utils.GetValue<Dictionary<string, object>>(cfg, "pity"));
                Dictionary<string, object> asc = ItemAt(Utils.GetValue<List<object>>(cfg, "byAscension"), ascension);
                if (asc != null) ParsePity(ctx.ByRarity, Utils.GetValue<Dictionary<string, object>>(asc, "pity"));
            }
            _pityByAsc[ascension] = ctx;
            return ctx.ByRarity;
        }

        static void ParsePity(Dictionary<int, PityConfig> into, Dictionary<string, object> raw)
        {
            if (raw == null) return;
            foreach (KeyValuePair<string, object> kv in raw)
            {
                int r = RarityKey(kv.Key);
                if (r <= 0) continue;
                Dictionary<string, object> entry = kv.Value as Dictionary<string, object>;
                if (entry == null) continue;
                PityConfig pc;
                if (!into.TryGetValue(r, out pc)) pc = new PityConfig { Increment = 0, Max = 0, ResetOn = new HashSet<int> { r } };
                // per-field merge:
                object v;
                if (entry.TryGetValue("increment", out v)) { try { pc.Increment = Convert.ToDouble(v); } catch { } }
                if (entry.TryGetValue("max", out v))       { try { pc.Max = Convert.ToDouble(v); } catch { } }
                List<object> rs = Utils.GetValue<List<object>>(entry, "reset_on");
                if (rs != null)
                {
                    HashSet<int> set = new HashSet<int>();
                    foreach (object o in rs)
                    {
                        int rr = RarityKey(Convert.ToString(o));
                        if (rr > 0) set.Add(rr);
                    }
                    if (set.Count > 0) pc.ResetOn = set;
                }
                into[r] = pc;
            }
        }

        // Selection weight of cid at an ascension: rarityRate(rarity) × ∏(group rates). Default 1.
        // Optional rarityRatesOverride replaces global+asc rarityRate per key (used by openpack pulls).
        public static double Weight(string dataDirectory, int cid, int ascension,
            Dictionary<int, double> rarityRatesOverride = null)
        {
            WeightCtx ctx = Weights(dataDirectory, ascension);
            double w = 1.0;
            int rarity;
            if (CardListRarity(dataDirectory).TryGetValue(cid, out rarity))
            {
                double rr;
                if (rarityRatesOverride != null && rarityRatesOverride.TryGetValue(rarity, out rr))
                    w *= rr;
                else if (ctx.RarityRate.TryGetValue(rarity, out rr))
                    w *= rr;
            }
            double gm; if (ctx.GroupMult.TryGetValue(cid, out gm)) w *= gm;
            return w;
        }

        public class DrawResult
        {
            public int Cid;
            public int Rarity;       // 1..4 from CardListRarity
            public bool IsNew;       // always true in roguelike (client decides visual)
            public int PremiumType;  // 0 in v1
        }

        // Draw n unique cids (within this call) that match spec from pool.
        // pool     = universe (caller provides via AnyPool/Deck/Link).
        // used     = anti-dup set (caller-owned; DrawN appends picked cids).
        // rarityRatesOverride (optional) = replaces global+asc rarityRate per key.
        // weighted = true for weighted pick (source any/link); false for uniform (deck).
        // Returns empty list if it can't fill; caller validates.
        // Partial draws (< n) are possible when all remaining candidates have weight 0.
        public static List<DrawResult> DrawN(
            string dataDirectory,
            HashSet<int> pool,
            Dictionary<string, object> spec,
            int n,
            Random rng,
            int ascension,
            HashSet<int> used,
            Dictionary<int, double> rarityRatesOverride,
            bool weighted)
        {
            List<DrawResult> result = new List<DrawResult>();
            if (pool == null || n <= 0 || rng == null || used == null) return result;

            Dictionary<int, int> rarityMap = CardListRarity(dataDirectory);

            List<int> cands = new List<int>();
            int passedExist = 0, passedKind = 0;
            int sampleFromPool = -1, sampleInCards = -1;
            var allCards = Cards(dataDirectory);
            foreach (int cid in pool)
            {
                if (sampleFromPool == -1) sampleFromPool = cid; // first cid of pool, for diag
                if (used.Contains(cid)) continue;
                // diag: split the filter so we know which step kills the candidates
                YdkHelper.GameCardInfo c;
                if (!allCards.TryGetValue(cid, out c)) continue;
                passedExist++;
                if (!PassesKind(Utils.GetValue<string>(spec, "random"), c)) continue;
                passedKind++;
                if (Matches(dataDirectory, cid, spec)) cands.Add(cid);
            }
            foreach (var kv in allCards) { sampleInCards = kv.Key; break; }
            // Probe the first cid of the pool: is it in Cards? Exist? IsMonster?
            YdkHelper.GameCardInfo probe;
            bool inCards = allCards.TryGetValue(sampleFromPool, out probe);
            Console.WriteLine("[Roguelike] DrawN filter: pool=" + pool.Count + " cardsDict=" + allCards.Count
                + " samplePoolCid=" + sampleFromPool + " sampleCardsCid=" + sampleInCards
                + " probeInCards=" + inCards + " probeExist=" + (inCards ? probe.Exist.ToString() : "n/a")
                + " probeIsMonster=" + (inCards ? probe.IsMonster.ToString() : "n/a")
                + " probeFrame=" + (inCards ? probe.Frame.ToString() : "n/a")
                + " passedExist=" + passedExist + " passedKind=" + passedKind + " matched=" + cands.Count);
            if (cands.Count == 0) return result;

            for (int draw = 0; draw < n && cands.Count > 0; draw++)
            {
                int pickIdx;
                if (weighted)
                {
                    double[] w = new double[cands.Count];
                    double total = 0;
                    for (int i = 0; i < cands.Count; i++)
                    {
                        w[i] = Math.Max(0, Weight(dataDirectory, cands[i], ascension, rarityRatesOverride));
                        total += w[i];
                    }
                    if (total <= 0) break;
                    double roll = rng.NextDouble() * total;
                    pickIdx = cands.Count - 1;
                    for (int i = 0; i < cands.Count; i++) { roll -= w[i]; if (roll <= 0) { pickIdx = i; break; } }
                }
                else
                {
                    pickIdx = rng.Next(cands.Count);
                }
                int picked = cands[pickIdx];
                cands.RemoveAt(pickIdx);
                used.Add(picked);
                int rarity; rarityMap.TryGetValue(picked, out rarity);
                result.Add(new DrawResult { Cid = picked, Rarity = rarity, IsNew = true, PremiumType = 0 });
            }
            return result;
        }

        static void ParseRarityRates(Dictionary<int, double> into, Dictionary<string, object> rates)
        {
            if (rates == null) return;
            foreach (KeyValuePair<string, object> kv in rates)
            {
                int r = RarityKey(kv.Key);
                if (r <= 0) continue;
                double w; try { w = Convert.ToDouble(kv.Value); } catch { continue; }
                into[r] = w;
            }
        }

        public static int RarityKey(string k)
        {
            switch ((k ?? "").ToUpperInvariant())
            {
                case "N": return 1; case "R": return 2; case "SR": return 3; case "UR": return 4;
            }
            int n; return int.TryParse(k, out n) ? n : 0;
        }

        static void ParseGroups(Dictionary<int, double> into, List<object> groups)
        {
            if (groups == null) return;
            foreach (object o in groups)
            {
                Dictionary<string, object> g = o as Dictionary<string, object>;
                if (g == null) continue;
                object rv; if (!g.TryGetValue("rate", out rv)) continue;
                double rate; try { rate = Convert.ToDouble(rv); } catch { continue; }
                List<object> cids = Utils.GetValue<List<object>>(g, "cids");
                if (cids == null) continue;
                foreach (object o2 in cids)
                {
                    int c; if (!TryCid(o2, out c)) continue;
                    double cur;
                    into[c] = (into.TryGetValue(c, out cur) ? cur : 1.0) * rate;
                }
            }
        }

        static Dictionary<string, object> ItemAt(List<object> list, int i)
        {
            return (list != null && i >= 0 && i < list.Count) ? list[i] as Dictionary<string, object> : null;
        }

        static bool TryCid(object o, out int cid) { cid = 0; try { cid = Convert.ToInt32(o); return true; } catch { return false; } }
    }
}
