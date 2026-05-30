using System;
using System.Collections.Generic;
using System.IO;
using YgoMaster;

namespace YgoMasterSettings.Util
{
    // Wrapper de DataLE/ShopPackOdds.json — array de entries, cada uma
    // descrevendo distribuição de raridade por slot do pack.
    //
    // Cada entry pode ser:
    //   1. NAMED: tem campo "name" → referenciada por packs via
    //      `oddsName` (ex: "low_rares_8")
    //   2. DEFAULT por packType: tem `packTypes` (array) + `gachaType`
    //      → aplicada automaticamente em packs do(s) tipo(s) listados
    //      quando não há oddsName explícito
    //
    // Estrutura comum:
    //   - cardRateList: array de slot rules (start_num..end_num,
    //     rate por rarity, opcional settle_rare_min/max no slot final
    //     pra force-upgrade quando não há card de raridade alta
    //     ainda)
    //   - premiereRateList (opcional): rates pra "premiere" cards
    class ShopOddsData
    {
        public string Path { get; private set; }
        // Array raw — cada item é Dictionary<string,object>
        public List<object> Entries { get; private set; }

        public ShopOddsData(string path)
        {
            Path = path;
            Entries = new List<object>();
            if (File.Exists(path))
            {
                try
                {
                    string text = File.ReadAllText(path);
                    List<object> parsed = MiniJSON.Json.Deserialize(text) as List<object>;
                    if (parsed != null) Entries = parsed;
                }
                catch { /* lista vazia em caso de parse error */ }
            }
        }

        // Display name pra UI: prefere "name" se houver, senão monta
        // descrição baseada em packTypes.
        public static string DescribeEntry(object entry)
        {
            Dictionary<string, object> d = entry as Dictionary<string, object>;
            if (d == null) return "(invalid)";
            string name = ShopData.GetStr(d, "name");
            if (!string.IsNullOrEmpty(name)) return name;
            object pt;
            if (d.TryGetValue("packTypes", out pt) && pt is List<object>)
            {
                List<object> ptList = (List<object>)pt;
                List<string> typesStr = new List<string>();
                foreach (object o in ptList) typesStr.Add(Convert.ToString(o));
                int gt = ShopData.GetInt(d, "gachaType");
                return "(default packTypes=[" + string.Join(",", typesStr.ToArray()) +
                       "] gacha=" + gt + ")";
            }
            return "(unnamed)";
        }

        // Lista de names únicos pra combo de oddsName em pack editor
        public List<string> GetNames()
        {
            List<string> names = new List<string>();
            foreach (object o in Entries)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                string name = ShopData.GetStr(d, "name");
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
            return names;
        }

        public string Save()
        {
            string bkpDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Path), "_bkp");
            Directory.CreateDirectory(bkpDir);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string bkp = System.IO.Path.Combine(bkpDir, "ShopPackOdds." + ts + ".bak.json");
            if (File.Exists(Path)) File.Copy(Path, bkp, overwrite: true);
            string serialized = MiniJSON.Json.Serialize(Entries);
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, serialized);
            try { File.Replace(tmp, Path, null, ignoreMetadataErrors: true); }
            catch (PlatformNotSupportedException)
            {
                File.Delete(Path);
                File.Move(tmp, Path);
            }
            return bkp;
        }
    }

    // Wrapper de DataLE/ShopPackOddsVisuals.json — 3 booleans globais
    // que afetam apresentação do pack opening no cliente:
    //   - RarityJebait: fake-out de raridade pra suspense
    //   - RarityOnCardBack: mostra rarity no verso do card
    //   - RarityOnPack: mostra rarity no pack art
    class ShopVisualsData
    {
        public string Path { get; private set; }
        public Dictionary<string, object> Root { get; private set; }

        public ShopVisualsData(string path)
        {
            Path = path;
            Root = new Dictionary<string, object>();
            if (File.Exists(path))
            {
                try
                {
                    string text = File.ReadAllText(path);
                    Dictionary<string, object> parsed =
                        MiniJSON.Json.Deserialize(text) as Dictionary<string, object>;
                    if (parsed != null) Root = parsed;
                }
                catch { }
            }
        }

        public string Save()
        {
            string bkpDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Path), "_bkp");
            Directory.CreateDirectory(bkpDir);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string bkp = System.IO.Path.Combine(bkpDir, "ShopPackOddsVisuals." + ts + ".bak.json");
            if (File.Exists(Path)) File.Copy(Path, bkp, overwrite: true);
            string serialized = MiniJSON.Json.Serialize(Root);
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, serialized);
            try { File.Replace(tmp, Path, null, ignoreMetadataErrors: true); }
            catch (PlatformNotSupportedException)
            {
                File.Delete(Path);
                File.Move(tmp, Path);
            }
            return bkp;
        }
    }
}
