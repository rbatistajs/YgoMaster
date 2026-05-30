using System;
using System.Collections.Generic;
using System.IO;
using YgoMaster;

namespace YgoMasterSettings.Util
{
    // Uma regulation = id + nome amigável + pool de CIDs válidos +
    // limit count por cid. Lida de DataLE/Regulation.json (pool +
    // limits) + DataLE/RegulationInfo.json (labels).
    //
    // Cada regulation no Regulation.json tem 4 buckets em
    // `available.a0..a3` que representam o LIMIT do card:
    //   a0 = 0 cópias (forbidden / banlist)
    //   a1 = 1 cópia max (limited)
    //   a2 = 2 cópias max (semi-limited)
    //   a3 = 3 cópias max (default, sem restrição)
    // Cards não listados em nenhum bucket → 3 cópias (unlimited).
    class RegulationFormat
    {
        public string Id;
        public string DisplayName;
        public HashSet<int> Cids;
        // cid → limit (0/1/2/3). Cards do universo não presentes aqui
        // são unlimited (3) implícito.
        public Dictionary<int, int> Limits = new Dictionary<int, int>();

        // Retorna o limit do card neste format: 3 se não listado em
        // a0..a2 (implícito unlimited).
        public int LimitOf(int cid)
        {
            int n;
            return Limits.TryGetValue(cid, out n) ? n : 3;
        }
    }

    static class FormatPools
    {
        // IDs de formato bem conhecidos (atalhos pra quem precisar)
        public const string GoatFormatId = "2005";
        public const string RushFormatId = "100001";

        static readonly object _lock = new object();
        static string _cachedDir;
        static List<RegulationFormat> _cachedFormats;

        // Retorna TODOS os formats encontrados em Regulation.json, com
        // pool de CIDs preenchido e display name resolvido via
        // RegulationInfo.json. Ordenado pra UI estável: Goat e Rush
        // primeiro (mais usados no Goat fork), depois o resto por nome.
        public static List<RegulationFormat> LoadAll(string dataDir)
        {
            lock (_lock)
            {
                string regPath = Path.Combine(dataDir, "Regulation.json");
                if (_cachedDir == dataDir && _cachedFormats != null) return _cachedFormats;
                _cachedDir = dataDir;
                _cachedFormats = ParseAll(dataDir);
                return _cachedFormats;
            }
        }

        // Compat helpers — alguns callers (FormatPools.Goat/Rush) usados
        // em outros tabs. Mantidos pra não quebrar.
        public static HashSet<int> Goat(string dataDir)
        {
            return FindCids(LoadAll(dataDir), GoatFormatId) ?? new HashSet<int>();
        }
        public static HashSet<int> Rush(string dataDir)
        {
            return FindCids(LoadAll(dataDir), RushFormatId) ?? new HashSet<int>();
        }

        public static void Reload() { lock (_lock) { _cachedDir = null; } }

        static HashSet<int> FindCids(List<RegulationFormat> all, string id)
        {
            if (all == null) return null;
            foreach (RegulationFormat f in all)
                if (f.Id == id) return f.Cids;
            return null;
        }

        static List<RegulationFormat> ParseAll(string dataDir)
        {
            string regPath  = Path.Combine(dataDir, "Regulation.json");
            string infoPath = Path.Combine(dataDir, "RegulationInfo.json");
            List<RegulationFormat> result = new List<RegulationFormat>();
            if (!File.Exists(regPath)) return result;

            // RegulationInfo.json: { "rule_list": { "<id>": "<label or IDS_…>" } }
            Dictionary<string, string> labels = new Dictionary<string, string>();
            if (File.Exists(infoPath))
            {
                try
                {
                    Dictionary<string, object> info =
                        MiniJSON.Json.Deserialize(File.ReadAllText(infoPath))
                        as Dictionary<string, object>;
                    Dictionary<string, object> ruleList = null;
                    if (info != null && info.TryGetValue("rule_list", out object rl))
                        ruleList = rl as Dictionary<string, object>;
                    if (ruleList != null)
                    {
                        foreach (KeyValuePair<string, object> kv in ruleList)
                            labels[kv.Key] = PrettifyLabel(Convert.ToString(kv.Value));
                    }
                }
                catch { /* silent — fica sem display names */ }
            }

            // Carrega o universo (CardList.json) — pool allowed por
            // regulation = universo MENOS a0 (banlist). Mirror do que
            // RuntimeRandomResolver.BuildAnyPools faz no server.
            HashSet<int> universe = LoadCardListUniverse(dataDir);

            // Regulation.json: { "<id>": { "available": { "a0": [forbidden],
            // "a1": [limited 1x], "a2": [semi 2x], "a3": [3x explícito] } } }
            try
            {
                Dictionary<string, object> root =
                    MiniJSON.Json.Deserialize(File.ReadAllText(regPath))
                    as Dictionary<string, object>;
                if (root == null) return result;
                foreach (KeyValuePair<string, object> kv in root)
                {
                    Dictionary<string, object> fmt = kv.Value as Dictionary<string, object>;
                    Dictionary<int, int> limits = ExtractLimits(fmt);
                    // Allowed = universo menos cards com limit 0
                    HashSet<int> allowed = new HashSet<int>(universe);
                    foreach (KeyValuePair<int, int> lim in limits)
                        if (lim.Value == 0) allowed.Remove(lim.Key);
                    string display;
                    if (!labels.TryGetValue(kv.Key, out display) || string.IsNullOrEmpty(display))
                        display = "Format " + kv.Key;
                    result.Add(new RegulationFormat
                    {
                        Id = kv.Key,
                        DisplayName = display,
                        Cids = allowed,
                        Limits = limits,
                    });
                }
            }
            catch { /* silent */ }

            // Ordenação: Goat e Rush primeiro (atalho pro fork), depois
            // alfabético por DisplayName.
            result.Sort((a, b) =>
            {
                int pa = PriorityOf(a.Id), pb = PriorityOf(b.Id);
                if (pa != pb) return pa - pb;
                return string.Compare(a.DisplayName, b.DisplayName,
                    StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        static int PriorityOf(string id)
        {
            if (id == GoatFormatId) return 0;
            if (id == RushFormatId) return 1;
            return 2;
        }

        // Limpa labels que são localization keys: "IDS_CARDMENU_REGULATION_NORMAL"
        // → "Normal". Strings já-bonitas ("Goat Format") passam intactas.
        static string PrettifyLabel(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (!raw.StartsWith("IDS_")) return raw;
            // Pega o último underscore-separated token e Title-Cases
            int lastUs = raw.LastIndexOf('_');
            string tail = lastUs >= 0 ? raw.Substring(lastUs + 1) : raw;
            if (tail.Length == 0) return raw;
            return char.ToUpperInvariant(tail[0]) + tail.Substring(1).ToLowerInvariant();
        }

        // Extrai todos os 4 buckets aN do `available` e retorna dict
        // cid → max copies (0/1/2/3). Cards não listados = unlimited
        // (3) implícito — não aparecem aqui pra economizar memória.
        static Dictionary<int, int> ExtractLimits(Dictionary<string, object> fmt)
        {
            Dictionary<int, int> limits = new Dictionary<int, int>();
            if (fmt == null) return limits;
            object avObj;
            if (!fmt.TryGetValue("available", out avObj)) return limits;
            Dictionary<string, object> avail = avObj as Dictionary<string, object>;
            if (avail == null) return limits;
            for (int n = 0; n <= 3; n++)
            {
                object bucketObj;
                if (!avail.TryGetValue("a" + n, out bucketObj)) continue;
                List<object> bucket = bucketObj as List<object>;
                if (bucket == null) continue;
                foreach (object cidObj in bucket)
                {
                    try { limits[Convert.ToInt32(cidObj)] = n; }
                    catch { }
                }
            }
            return limits;
        }

        // Universo = todos os CIDs em CardList.json (mesma fonte que o
        // RuntimeRandomResolver.LoadCardListCids usa).
        static HashSet<int> LoadCardListUniverse(string dataDir)
        {
            HashSet<int> cids = new HashSet<int>();
            string path = Path.Combine(dataDir, "CardList.json");
            if (!File.Exists(path)) return cids;
            try
            {
                Dictionary<string, object> doc =
                    MiniJSON.Json.Deserialize(File.ReadAllText(path))
                    as Dictionary<string, object>;
                if (doc == null) return cids;
                foreach (string key in doc.Keys)
                {
                    int cid;
                    if (int.TryParse(key, out cid)) cids.Add(cid);
                }
            }
            catch { /* silent — fica com universo vazio */ }
            return cids;
        }
    }
}
