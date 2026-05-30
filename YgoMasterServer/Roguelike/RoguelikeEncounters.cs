using System;
using System.Collections.Generic;
using System.IO;

namespace YgoMaster
{
    // Curated enemy pool (DataLE/Roguelike/Encounters.json), keyed by node type. Each type holds an
    // array of encounters that name a deck (file in Roguelike/Opponents) plus optional gating
    // (act/floor/ascension ranges) and overrides (enemy LP, reward, who goes first). Cached once
    // (restart the server to apply); affects new picks only. Mirrors RoguelikeSettings.
    static class RoguelikeEncounters
    {
        public class Range
        {
            public int? Min, Max;
            public bool Contains(int v) { return (!Min.HasValue || v >= Min.Value) && (!Max.HasValue || v <= Max.Value); }
        }

        public class Encounter
        {
            public string Id;
            public string Name;
            public string Text = "";
            public string Deck;
            public string IconImage; // "card_<cid>" or "profile_<id>" — node art (baked types only)
            public Range Act = new Range();
            public Range Floor = new Range();
            public Range Ascension = new Range();
            public double Weight = 1.0;
            public int? EnemyLp;
            public int? Reward;
            public int? FirstPlayer;
            public int? CpuRate;    // AI strength -100..100 (default = Settings, 100 = max)
            public string CpuFlag;  // DuelCpuParam name (None/Def/Fool/Light/...); null = Settings default
            public Dictionary<string, object> Modifiers; // { player, enemy } board spec (RoguelikeModifiers)
            public Dictionary<string, object> Action;    // action tree fired after this encounter (null = none)
        }

        static Dictionary<string, List<Encounter>> _cache;

        public static Dictionary<string, List<Encounter>> Load(string dataDirectory)
        {
            if (_cache != null) return _cache;
            _cache = new Dictionary<string, List<Encounter>>();
            string p = Path.Combine(dataDirectory, "Roguelike", "Encounters.json");
            if (!File.Exists(p)) { Console.WriteLine("[Roguelike] no Encounters.json (combat nodes will error)"); return _cache; }
            Dictionary<string, object> doc = null;
            try { doc = MiniJSON.Json.DeserializeStripped(File.ReadAllText(p)) as Dictionary<string, object>; }
            catch (Exception ex) { Console.WriteLine("[Roguelike] Encounters.json parse EX: " + ex.Message); }
            if (doc == null) return _cache;

            Dictionary<string, Dictionary<string, object>> actionLib = RoguelikeActions.Load(dataDirectory);

            // defaultAction: { "duel": "name" | { ... } | null, "elite": ..., "boss": ... }
            // Resolved once; applied to encounters whose `action` key is absent (null overrides).
            Dictionary<string, Dictionary<string, object>> defaultByType = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            Dictionary<string, object> defRaw = Utils.GetValue<Dictionary<string, object>>(doc, "defaultAction");
            if (defRaw != null)
            {
                foreach (KeyValuePair<string, object> kv in defRaw)
                {
                    try { defaultByType[kv.Key] = RoguelikeActions.Resolve(kv.Value, actionLib, new HashSet<string>()); }
                    catch (Exception ex) { Console.WriteLine("[Roguelike] defaultAction[" + kv.Key + "] resolve EX: " + ex.Message); }
                }
            }

            foreach (KeyValuePair<string, object> kv in doc)
            {
                if (kv.Key == "defaultAction") continue;
                List<object> arr = kv.Value as List<object>;
                if (arr == null) continue;
                List<Encounter> list = new List<Encounter>();
                foreach (object o in arr)
                {
                    Encounter e = Parse(o as Dictionary<string, object>, kv.Key, actionLib, defaultByType);
                    if (e != null) list.Add(e);
                }
                _cache[kv.Key] = list;
            }
            return _cache;
        }

        static Encounter Parse(Dictionary<string, object> d, string type, Dictionary<string, Dictionary<string, object>> actionLib, Dictionary<string, Dictionary<string, object>> defaultByType)
        {
            if (d == null) return null;
            string id = Utils.GetValue<string>(d, "id", null);
            string deck = Utils.GetValue<string>(d, "deck", null);
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(deck))
            {
                Console.WriteLine("[Roguelike] encounter missing id/deck, skipped");
                return null;
            }
            Encounter e = new Encounter
            {
                Id = id,
                Deck = deck,
                Name = Utils.GetValue<string>(d, "name", null),
                Text = Utils.GetValue<string>(d, "text", ""),
                IconImage = Utils.GetValue<string>(d, "icon_image", null),
                Act = ParseRange(Utils.GetValue<Dictionary<string, object>>(d, "act")),
                Floor = ParseRange(Utils.GetValue<Dictionary<string, object>>(d, "floor")),
                Ascension = ParseRange(Utils.GetValue<Dictionary<string, object>>(d, "ascension")),
                EnemyLp = OptInt(d, "enemyLp"),
                Reward = OptInt(d, "reward"),
                FirstPlayer = OptInt(d, "firstPlayer"),
                CpuRate = OptInt(d, "cpuRate"),
                CpuFlag = ValidCpuFlag(Utils.GetValue<string>(d, "cpuFlag", null), id),
                Modifiers = Utils.GetValue<Dictionary<string, object>>(d, "modifiers"),
            };
            // action: string ref | object | null | absent. Absent => use defaultByType[type].
            // Explicit null overrides default (encounter wants no action).
            object actionRaw;
            if (d.TryGetValue("action", out actionRaw))
            {
                try { e.Action = RoguelikeActions.Resolve(actionRaw, actionLib, new HashSet<string>()); }
                catch (Exception ex) { Console.WriteLine("[Roguelike] encounter '" + id + "' action resolve EX: " + ex.Message); return null; }
            }
            else
            {
                Dictionary<string, object> def;
                if (defaultByType != null && defaultByType.TryGetValue(type, out def)) e.Action = def;
            }
            object w; if (d.TryGetValue("weight", out w)) { try { e.Weight = Convert.ToDouble(w); } catch { } }
            if (string.IsNullOrEmpty(e.Name)) e.Name = Path.GetFileNameWithoutExtension(deck);
            if (e.Action != null)
            {
                try { ValidateActionNode(e.Action); }
                catch (Exception ex) { Console.WriteLine("[Roguelike] encounter '" + id + "' action invalid: " + ex.Message); return null; }
            }
            return e;
        }

        // Keep a cpuFlag only if it names a real DuelCpuParam; otherwise warn and fall back to default.
        static string ValidCpuFlag(string s, string id)
        {
            if (string.IsNullOrEmpty(s)) return null;
            DuelCpuParam p;
            if (Enum.TryParse<DuelCpuParam>(s, out p)) return s;
            Console.WriteLine("[Roguelike] encounter '" + id + "' invalid cpuFlag '" + s + "' (using default)");
            return null;
        }

        static Range ParseRange(Dictionary<string, object> d)
        {
            Range r = new Range();
            if (d == null) return r;
            r.Min = OptInt(d, "min");
            r.Max = OptInt(d, "max");
            return r;
        }

        static int? OptInt(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v)) { try { return Convert.ToInt32(v); } catch { } }
            return null;
        }

        // Encounters of `type` whose act/floor/ascension ranges all contain the given values.
        public static List<Encounter> Eligible(string dataDirectory, string type, int act, int floor, int ascension)
        {
            List<Encounter> result = new List<Encounter>();
            List<Encounter> pool;
            if (!Load(dataDirectory).TryGetValue(type, out pool) || pool == null) return result;
            foreach (Encounter e in pool)
                if (e.Act.Contains(act) && e.Floor.Contains(floor) && e.Ascension.Contains(ascension))
                    result.Add(e);
            return result;
        }

        // Weighted pick over the eligible set; null when none are eligible (strict — caller errors).
        public static Encounter Pick(string dataDirectory, string type, int act, int floor, int ascension, Random rng)
        {
            List<Encounter> elig = Eligible(dataDirectory, type, act, floor, ascension);
            double total = 0; Encounter last = null;
            foreach (Encounter e in elig) if (e.Weight > 0) { total += e.Weight; last = e; }
            if (total <= 0) return null;
            double roll = rng.NextDouble() * total;
            foreach (Encounter e in elig) { if (e.Weight <= 0) continue; roll -= e.Weight; if (roll <= 0) return e; }
            return last; // float drift fallback
        }

        public static Encounter ById(string dataDirectory, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (KeyValuePair<string, List<Encounter>> kv in Load(dataDirectory))
                foreach (Encounter e in kv.Value)
                    if (e.Id == id) return e;
            return null;
        }

        // Validate an action node tree recursively; throws on first error.
        // Action nodes use `type` (matching RoguelikeActionEngine).
        static void ValidateActionNode(Dictionary<string, object> node)
        {
            if (node == null) return;
            string type = Utils.GetValue<string>(node, "type", "");
            switch (type)
            {
                case "options":
                {
                    List<object> opts = Utils.GetValue<List<object>>(node, "options");
                    if (opts == null || opts.Count == 0) throw new Exception("options: options list required");
                    foreach (object o in opts)
                    {
                        Dictionary<string, object> od = o as Dictionary<string, object>;
                        if (od == null) throw new Exception("options: each option must be object");
                        Dictionary<string, object> sub = Utils.GetValue<Dictionary<string, object>>(od, "next");
                        if (sub != null) ValidateActionNode(sub);
                    }
                    return;
                }
                case "message":
                {
                    Dictionary<string, object> nxt = Utils.GetValue<Dictionary<string, object>>(node, "next");
                    if (nxt != null) ValidateActionNode(nxt);
                    return;
                }
                case "openpack":
                {
                    int packs = Utils.GetValue<int>(node, "packs", 1);
                    if (packs < 1) throw new Exception("openpack: packs must be >= 1");
                    // pick accepts int N (exact-pick) or { min, max } object (range).
                    int pickMin, pickMax;
                    ParsePickRange(node, out pickMin, out pickMax);
                    if (pickMin < 0 || pickMax < 0) throw new Exception("openpack: pick min/max must be >= 0");
                    if (pickMin > pickMax) throw new Exception("openpack: pick.min (" + pickMin + ") > pick.max (" + pickMax + ")");

                    List<object> pulls = Utils.GetValue<List<object>>(node, "pulls");
                    if (pulls == null || pulls.Count == 0) throw new Exception("openpack: pulls required");
                    int sizePerPack = 0;
                    foreach (object pullObj in pulls)
                    {
                        Dictionary<string, object> pull = pullObj as Dictionary<string, object>;
                        if (pull == null) throw new Exception("openpack: pull entry must be object");
                        int count = Utils.GetValue<int>(pull, "count", 0);
                        if (count < 1) throw new Exception("openpack: pull.count must be >= 1");
                        double chance = Utils.GetValue<double>(pull, "chance", 1.0);
                        if (chance < 0 || chance > 1) throw new Exception("openpack: pull.chance must be in [0,1]");
                        Dictionary<string, object> pool = Utils.GetValue<Dictionary<string, object>>(pull, "pool");
                        if (pool == null) throw new Exception("openpack: pull.pool required");
                        ValidatePackPool(pool);
                        sizePerPack += count;
                    }
                    int sizeTotal = sizePerPack * packs;
                    // pickMin must be reachable; the engine clamps pickMax to size at stage time
                    // (rolling fewer cards than max is fine — clamp keeps the UI consistent), but
                    // pickMin > size would deadlock the action because OK could never enable.
                    if (pickMin > sizeTotal) throw new Exception("openpack: pick.min (" + pickMin + ") > total size (" + sizeTotal + ")");

                    // pity: false | { rarityKey: {...} }
                    object pityRaw;
                    if (node.TryGetValue("pity", out pityRaw) && pityRaw != null)
                    {
                        if (pityRaw is bool && (bool)pityRaw == true)
                            throw new Exception("openpack: pity:true is invalid; use false to disable or omit to inherit global");
                        if (!(pityRaw is bool))
                        {
                            Dictionary<string, object> pity = pityRaw as Dictionary<string, object>;
                            if (pity == null) throw new Exception("openpack: pity must be object or false");
                            foreach (KeyValuePair<string, object> kv in pity)
                                if (RoguelikeCardPool.RarityKey(kv.Key) <= 0) throw new Exception("openpack: pity rarity key invalid: " + kv.Key);
                        }
                    }

                    Dictionary<string, object> nxt = Utils.GetValue<Dictionary<string, object>>(node, "next");
                    if (nxt != null) ValidateActionNode(nxt);
                    return;
                }
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
                default:
                    // Unknown types are tolerated (engine treats them as terminal no-ops).
                    return;
            }
        }


        // Parse openpack's `pick` (int N or { min, max } object) into a [min, max] pair.
        // Mirrors RoguelikeActionEngine.ParsePick — kept here so validation can reject obviously
        // bad shapes (negatives, min > max) at load time before any draw runs.
        static void ParsePickRange(Dictionary<string, object> node, out int min, out int max)
        {
            min = 0; max = 0;
            object raw;
            if (node == null || !node.TryGetValue("pick", out raw) || raw == null) return;
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
        }

        // Validate the pool spec inside an openpack pull (type checks only; optional fields).
        static void ValidatePackPool(Dictionary<string, object> pool)
        {
            object v;
            if (pool.TryGetValue("rarityRates", out v))
            {
                Dictionary<string, object> rr = v as Dictionary<string, object>;
                if (rr == null) throw new Exception("openpack pool.rarityRates must be object");
                foreach (KeyValuePair<string, object> kv in rr)
                    if (RoguelikeCardPool.RarityKey(kv.Key) <= 0) throw new Exception("openpack pool.rarityRates key invalid: " + kv.Key);
            }
            if (pool.TryGetValue("rarity", out v) && RoguelikeCardPool.RarityKey(Convert.ToString(v)) <= 0)
                throw new Exception("openpack pool.rarity invalid: " + v);
            if (pool.TryGetValue("rarities", out v))
            {
                List<object> rs = v as List<object>;
                if (rs == null) throw new Exception("openpack pool.rarities must be array");
                foreach (object o in rs)
                    if (RoguelikeCardPool.RarityKey(Convert.ToString(o)) <= 0) throw new Exception("openpack pool.rarities entry invalid: " + o);
            }
        }
    }
}
