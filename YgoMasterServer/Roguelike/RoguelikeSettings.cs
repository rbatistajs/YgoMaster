using System;
using System.Collections.Generic;
using System.IO;

namespace YgoMaster
{
    // Data-driven roguelike config (DataLE/Roguelike/Settings.json). Defaults applied when
    // the file or a key is missing, so the mod runs without it.
    static class RoguelikeSettings
    {
        static Dictionary<string, object> _cache;

        public static Dictionary<string, object> Load(string dataDirectory)
        {
            if (_cache != null) return _cache;
            string p = Path.Combine(dataDirectory, "Roguelike", "Settings.json");
            Dictionary<string, object> d = null;
            if (File.Exists(p))
            {
                try { d = MiniJSON.Json.DeserializeStripped(File.ReadAllText(p)) as Dictionary<string, object>; }
                catch { d = null; }
            }
            _cache = d ?? new Dictionary<string, object>();
            return _cache;
        }

        public static string Layout(Dictionary<string, object> s) => Utils.GetValue<string>(s, "layout", "slay_the_spire");
        public static int Floors(Dictionary<string, object> s) => Utils.GetValue<int>(s, "floors", 8);
        public static int Width(Dictionary<string, object> s) => Utils.GetValue<int>(s, "width", 4);
        public static int Paths(Dictionary<string, object> s) => Utils.GetValue<int>(s, "paths", 6);

        // Node types whose encounter is chosen at map-gen ("baked": name + icon shown on the map ahead
        // of the fight); other combat types pick lazily on arrival. mapGen.bakeTypes in Settings.json;
        // default ["boss"] = legacy behavior. Passes through Effective (perAct/perAscension can replace).
        public static HashSet<string> BakeTypes(Dictionary<string, object> s)
        {
            HashSet<string> set = new HashSet<string>();
            Dictionary<string, object> mapGen = Utils.GetValue<Dictionary<string, object>>(s, "mapGen");
            List<object> types = mapGen != null ? Utils.GetValue<List<object>>(mapGen, "bakeTypes") : null;
            if (types == null) { set.Add("boss"); return set; }
            foreach (object o in types) { string t = o as string; if (!string.IsNullOrEmpty(t)) set.Add(t); }
            return set;
        }

        // ----- deck constraints (reward routing) -----
        //
        // deck.minCards     — below this, every new reward card slots into the deck so it stays
        //                     legal to duel with. Default 40 = standard Yu-Gi-Oh main-deck floor.
        // deck.maxMainCards — hard cap on the main deck. Reward cards beyond this size go only
        //                     to the collection (even with autoAddToDeck). Default 60.
        // deck.maxExtraCards — hard cap on the extra deck. Default 15.
        // deck.autoAddToDeck — true: also add the card to the deck even past minCards (still
        //                      capped at the maxes). false (default): rewards past minCards
        //                      land only in the run collection so the player decides.
        public static int DeckMinCards(Dictionary<string, object> s)
        {
            Dictionary<string, object> d = Utils.GetValue<Dictionary<string, object>>(s, "deck");
            return d != null ? Utils.GetValue<int>(d, "minCards", 40) : 40;
        }
        public static int DeckMaxMainCards(Dictionary<string, object> s)
        {
            Dictionary<string, object> d = Utils.GetValue<Dictionary<string, object>>(s, "deck");
            return d != null ? Utils.GetValue<int>(d, "maxMainCards", 60) : 60;
        }
        public static int DeckMaxExtraCards(Dictionary<string, object> s)
        {
            Dictionary<string, object> d = Utils.GetValue<Dictionary<string, object>>(s, "deck");
            return d != null ? Utils.GetValue<int>(d, "maxExtraCards", 15) : 15;
        }
        public static bool DeckAutoAdd(Dictionary<string, object> s)
        {
            Dictionary<string, object> d = Utils.GetValue<Dictionary<string, object>>(s, "deck");
            return d != null && Utils.GetValue<bool>(d, "autoAddToDeck", false);
        }

        // ----- acts / ascension -----

        public static int Acts(Dictionary<string, object> s) => Math.Max(1, Utils.GetValue<int>(s, "acts", 3));
        public static int Ascensions(Dictionary<string, object> s) => Math.Max(1, Utils.GetValue<int>(s, "ascensions", 20));

        // Heal applied (fraction of max LP) when starting each act after the first.
        public static double InterActHealPercent(Dictionary<string, object> s)
        {
            object v;
            if (s != null && s.TryGetValue("interActHealPercent", out v)) { try { return Convert.ToDouble(v); } catch { } }
            return 0.0;
        }

        // Settings for a given act + ascension: base, deep-merged with perAscension[asc], then
        // perAct[act], then perAscension[asc].perAct[act] (most specific wins). Then ascensionScale
        // applied multiplicatively (× ascension).
        public static Dictionary<string, object> Effective(Dictionary<string, object> baseSettings, int act, int ascension)
        {
            Dictionary<string, object> eff = DeepClone(baseSettings);
            Dictionary<string, object> asc = ItemAt(Utils.GetValue<List<object>>(baseSettings, "perAscension"), ascension);
            DeepMerge(eff, asc);                                                               // 2: whole ascension
            DeepMerge(eff, ItemAt(Utils.GetValue<List<object>>(baseSettings, "perAct"), act)); // 3: act, every ascension
            if (asc != null)
                DeepMerge(eff, ItemAt(Utils.GetValue<List<object>>(asc, "perAct"), act));      // 4: this act at this ascension (wins)
            if (ascension > 0)
                ApplyScale(eff, Utils.GetValue<Dictionary<string, object>>(baseSettings, "ascensionScale"), ascension);
            return eff;
        }

        static Dictionary<string, object> ItemAt(List<object> list, int i)
        {
            return (list != null && i >= 0 && i < list.Count) ? list[i] as Dictionary<string, object> : null;
        }

        static Dictionary<string, object> DeepClone(Dictionary<string, object> d)
        {
            return MiniJSON.Json.DeserializeStripped(MiniJSON.Json.Serialize(d ?? new Dictionary<string, object>()))
                as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        // Override `target` with `over`: nested dicts merge key-by-key, anything else replaces.
        static void DeepMerge(Dictionary<string, object> target, Dictionary<string, object> over)
        {
            if (target == null || over == null) return;
            foreach (KeyValuePair<string, object> kv in over)
            {
                object cur;
                if (kv.Value is Dictionary<string, object> && target.TryGetValue(kv.Key, out cur) && cur is Dictionary<string, object>)
                    DeepMerge((Dictionary<string, object>)cur, (Dictionary<string, object>)kv.Value);
                else
                    target[kv.Key] = kv.Value;
            }
        }

        // Multiplicative scaling: each scale entry `key: factor` -> value × (1 + factor·ascension).
        // Applies to a top-level numeric, every numeric entry of a top-level dict (e.g. enemyLp), or
        // a matching entry inside typeWeights (e.g. "elite").
        static void ApplyScale(Dictionary<string, object> eff, Dictionary<string, object> scale, int ascension)
        {
            if (scale == null) return;
            Dictionary<string, object> weights = Utils.GetValue<Dictionary<string, object>>(eff, "typeWeights");
            foreach (KeyValuePair<string, object> kv in scale)
            {
                double factor; try { factor = Convert.ToDouble(kv.Value); } catch { continue; }
                double mult = 1.0 + factor * ascension;
                object cur;
                if (eff.TryGetValue(kv.Key, out cur))
                {
                    if (cur is Dictionary<string, object>) ScaleDictEntries((Dictionary<string, object>)cur, mult);
                    else if (IsNumber(cur)) eff[kv.Key] = Convert.ToDouble(cur) * mult;
                }
                else if (weights != null && weights.TryGetValue(kv.Key, out cur) && IsNumber(cur))
                    weights[kv.Key] = Convert.ToDouble(cur) * mult;
            }
        }

        static void ScaleDictEntries(Dictionary<string, object> d, double mult)
        {
            List<string> keys = new List<string>(d.Keys);
            foreach (string k in keys) if (IsNumber(d[k])) d[k] = Convert.ToDouble(d[k]) * mult;
        }

        static bool IsNumber(object o) { return o is int || o is long || o is double || o is float; }

        // type -> weight (duel forced on floor 0, boss fixed on top — not in this table).
        public static Dictionary<string, object> TypeWeights(Dictionary<string, object> s)
        {
            Dictionary<string, object> w = Utils.GetValue<Dictionary<string, object>>(s, "typeWeights");
            if (w != null && w.Count > 0) return w;
            return new Dictionary<string, object>
            {
                { "duel", 0.6 }, { "elite", 0.12 }, { "event", 0.12 }, { "shop", 0.08 }, { "reward", 0.08 },
            };
        }

        // Row zones with their own weight tables: [{ from, to, weights:{...} }]. Empty = none.
        public static List<object> Bands(Dictionary<string, object> s)
        {
            return Utils.GetValue<List<object>>(s, "bands") ?? new List<object>();
        }

        // Per-type floor locks: { "<type>": { minFloor, maxFloor } } (negatives = from top). Empty = none.
        public static Dictionary<string, object> TypeRules(Dictionary<string, object> s)
        {
            return Utils.GetValue<Dictionary<string, object>>(s, "typeRules") ?? new Dictionary<string, object>();
        }

        // Whole-row overrides: { "<row>": "<type>" } (row may be negative). Empty = none.
        public static Dictionary<string, object> ForcedRows(Dictionary<string, object> s)
        {
            return Utils.GetValue<Dictionary<string, object>>(s, "forcedRows") ?? new Dictionary<string, object>();
        }

        // Per-type node-count bounds for an act map: { "<type>": { "min", "max" } } (counted over
        // the whole map). Empty = none. Passes through Effective (perAct/perAscension/nested can set it).
        public static Dictionary<string, object> TypeCounts(Dictionary<string, object> s)
        {
            return Utils.GetValue<Dictionary<string, object>>(s, "typeCounts") ?? new Dictionary<string, object>();
        }

        // ----- combat LP -----

        // Player run LP: start value and cap (heals never exceed it).
        public static int PlayerMaxLp(Dictionary<string, object> s) => Utils.GetValue<int>(s, "playerMaxLp", 8000);

        // Heal applied after a won combat, as a fraction of PlayerMaxLp (default 10%).
        public static double HealPercent(Dictionary<string, object> s)
        {
            object v;
            if (s != null && s.TryGetValue("healPercentPerCombat", out v))
            {
                try { return Convert.ToDouble(v); } catch { }
            }
            return 0.10;
        }

        // Enemy starting LP for a combat node: enemyLp[<type>] else enemyLp["default"] else 2000.
        public static int EnemyLpFor(Dictionary<string, object> s, string type)
        {
            Dictionary<string, object> e = Utils.GetValue<Dictionary<string, object>>(s, "enemyLp");
            int fallback = 2000;
            if (e != null)
            {
                object v;
                if (!string.IsNullOrEmpty(type) && e.TryGetValue(type, out v)) { try { return Convert.ToInt32(v); } catch { } }
                if (e.TryGetValue("default", out v)) { try { fallback = Convert.ToInt32(v); } catch { } }
            }
            return fallback;
        }

        // ----- CPU AI -----

        // AI strength (-100..100; default 100 = max). Fallback for encounters without `cpuRate`.
        public static int CpuRate(Dictionary<string, object> s) => Utils.GetValue<int>(s, "cpuRate", 100);

        // AI behavior flag (DuelCpuParam name: None/Def/Fool/Light/MyTurnOnly/AttackOnly/Simple).
        // Fallback for encounters without `cpuFlag`. Defaults to "None" explicitly — a null/absent
        // cpuflag makes the native EngineInitializerByServer fall back to its own default (Light),
        // so we send "None" to actually get plain AI.
        public static string CpuFlag(Dictionary<string, object> s) => Utils.GetValue<string>(s, "cpuFlag", "None");

        // ----- modifiers -----

        // Per-node-type modifier defaults: modifierDefaults[<type>] = { player, enemy }. Merged under
        // the encounter's own modifiers; null when absent. Scales via perAct/perAscension (Effective).
        public static Dictionary<string, object> ModifierDefaults(Dictionary<string, object> s, string type)
        {
            Dictionary<string, object> all = Utils.GetValue<Dictionary<string, object>>(s, "modifierDefaults");
            return all != null ? Utils.GetValue<Dictionary<string, object>>(all, type) : null;
        }
    }
}
