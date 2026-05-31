using System;
using System.Collections.Generic;
using System.IO;

namespace YgoMaster
{
    // Curation layer for the starter-deck offers (DataLE/Roguelike/InitialDecks.json). Each entry
    // names a deck file under Roguelike/StartingDecks plus optional weight + ascension gating and a
    // display-name override. When the file is absent (or nothing is eligible for the run's ascension),
    // the caller falls back to listing the StartingDecks folder uniformly. Cached once; restart the
    // server to apply. Mirrors RoguelikeEncounters.
    static class RoguelikeStartingDecks
    {
        public class Range
        {
            public int? Min, Max;
            public bool Contains(int v) { return (!Min.HasValue || v >= Min.Value) && (!Max.HasValue || v <= Max.Value); }
        }

        public class Entry
        {
            public string Deck;          // filename under Roguelike/StartingDecks (e.g. "Burn.json")
            public string Name;          // optional display override (null = use the deck file name)
            public double Weight = 1.0;
            public Range Ascension = new Range();
        }

        static List<Entry> _cache;
        static bool _loaded;
        static int _offerCount = 3;      // how many decks to offer at run start (default 3)

        // Offer count from InitialDecks.json (default 3). Drives both the curated and fallback rolls.
        public static int OfferCount(string dataDirectory) { Load(dataDirectory); return _offerCount; }

        // Parsed entries, or null when InitialDecks.json is absent/empty (caller uses the flat folder).
        public static List<Entry> Load(string dataDirectory)
        {
            if (_loaded) return _cache;
            _loaded = true;
            _cache = null;
            string p = Path.Combine(dataDirectory, "Roguelike", "InitialDecks.json");
            if (!File.Exists(p)) { Console.WriteLine("[Roguelike] no InitialDecks.json (offering the StartingDecks folder uniformly)"); return _cache; }
            Dictionary<string, object> doc = null;
            try { doc = MiniJSON.Json.DeserializeStripped(File.ReadAllText(p)) as Dictionary<string, object>; }
            catch (Exception ex) { Console.WriteLine("[Roguelike] InitialDecks.json parse EX: " + ex.Message); }
            if (doc == null) return _cache;
            _offerCount = Utils.GetValue<int>(doc, "offerCount", 3);
            if (_offerCount < 1) _offerCount = 1;
            List<object> arr = Utils.GetValue<List<object>>(doc, "decks");
            if (arr == null) return _cache;
            List<Entry> list = new List<Entry>();
            foreach (object o in arr)
            {
                Entry e = Parse(o as Dictionary<string, object>);
                if (e != null) list.Add(e);
            }
            _cache = list.Count > 0 ? list : null;
            return _cache;
        }

        static Entry Parse(Dictionary<string, object> d)
        {
            if (d == null) return null;
            string deck = Utils.GetValue<string>(d, "deck", null);
            if (string.IsNullOrEmpty(deck)) { Console.WriteLine("[Roguelike] initial deck entry missing 'deck', skipped"); return null; }
            Entry e = new Entry
            {
                Deck = deck,
                Name = Utils.GetValue<string>(d, "name", null),
                Ascension = ParseRange(Utils.GetValue<Dictionary<string, object>>(d, "ascension")),
            };
            object w; if (d.TryGetValue("weight", out w)) { try { e.Weight = Convert.ToDouble(w); } catch { } }
            return e;
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

        // Entries whose ascension range contains the value AND whose deck file actually exists.
        public static List<Entry> Eligible(string dataDirectory, int ascension)
        {
            List<Entry> result = new List<Entry>();
            List<Entry> pool = Load(dataDirectory);
            if (pool == null) return result;
            string dir = Path.Combine(dataDirectory, "Roguelike", "StartingDecks");
            foreach (Entry e in pool)
            {
                if (!e.Ascension.Contains(ascension)) continue;
                if (!File.Exists(Path.Combine(dir, e.Deck)))
                {
                    Console.WriteLine("[Roguelike] initial deck file not found, skipped: " + e.Deck);
                    continue;
                }
                result.Add(e);
            }
            return result;
        }

        // Weighted pick of up to `count` distinct entries (seeded); removes each pick from the pool.
        public static List<Entry> Pick(List<Entry> eligible, int count, Random rng)
        {
            List<Entry> pool = new List<Entry>(eligible);
            List<Entry> result = new List<Entry>();
            while (result.Count < count && pool.Count > 0)
            {
                double total = 0;
                foreach (Entry e in pool) if (e.Weight > 0) total += e.Weight;
                if (total <= 0) break;
                double roll = rng.NextDouble() * total;
                int idx = pool.Count - 1; // float-drift fallback
                for (int i = 0; i < pool.Count; i++)
                {
                    if (pool[i].Weight <= 0) continue;
                    roll -= pool[i].Weight;
                    if (roll <= 0) { idx = i; break; }
                }
                result.Add(pool[idx]);
                pool.RemoveAt(idx);
            }
            return result;
        }

        // Display-name override for a deck file (case-insensitive), or null when none is set.
        public static string NameOverrideFor(string dataDirectory, string deckFile)
        {
            List<Entry> pool = Load(dataDirectory);
            if (pool == null || string.IsNullOrEmpty(deckFile)) return null;
            foreach (Entry e in pool)
                if (string.Equals(e.Deck, deckFile, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.Name))
                    return e.Name;
            return null;
        }
    }
}
