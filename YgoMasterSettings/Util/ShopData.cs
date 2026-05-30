using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using YgoMaster;

namespace YgoMasterSettings.Util
{
    // Wrapper de DataLE/Shop.json — carrega o JSON inteiro num
    // Dictionary<string,object> mutável e expõe helpers tipados pras
    // 3 categorias principais (PackShop / StructureShop / AccessoryShop)
    // + acesso aos globals (flags do topo) + PackShopImages.
    //
    // Save preserva o que não conhecemos (forward-compat: se Konami
    // adicionar campos novos, eles passam intactos).
    //
    // Backup automático em DataLE/_bkp/Shop.YYYYMMDD_HHMMSS.bak.json
    // antes de cada write (mesma convenção dos outros writers).
    class ShopData
    {
        public string Path { get; private set; }
        public Dictionary<string, object> Root { get; private set; }

        // Helpers tipados pras 3 sub-dicts mais usadas
        public Dictionary<string, object> Packs       => GetOrCreateDict("PackShop");
        public Dictionary<string, object> Structures  => GetOrCreateDict("StructureShop");
        public Dictionary<string, object> Accessories => GetOrCreateDict("AccessoryShop");

        // Lista de imagens disponíveis (PackShopImages) — strings tipo
        // "set11101000". O server usa essa lista pra validar packImage
        // ref'd nos packs.
        public List<object> PackShopImages
        {
            get
            {
                object v;
                if (!Root.TryGetValue("PackShopImages", out v) || !(v is List<object>))
                    Root["PackShopImages"] = new List<object>();
                return (List<object>)Root["PackShopImages"];
            }
        }

        public ShopData(string path)
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
                catch { /* root vazio em caso de corrupção — UI mostra "sem items" */ }
            }
        }

        Dictionary<string, object> GetOrCreateDict(string key)
        {
            object v;
            if (Root.TryGetValue(key, out v) && v is Dictionary<string, object>)
                return (Dictionary<string, object>)v;
            Dictionary<string, object> empty = new Dictionary<string, object>();
            Root[key] = empty;
            return empty;
        }

        // Salva pro disco com backup. Throw em IO error.
        // Retorna path do backup gerado.
        public string Save()
        {
            string bkpDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Path), "_bkp");
            Directory.CreateDirectory(bkpDir);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string bkp = System.IO.Path.Combine(bkpDir, "Shop." + ts + ".bak.json");
            if (File.Exists(Path)) File.Copy(Path, bkp, overwrite: true);

            string serialized = MiniJSON.Json.Serialize(Root);
            // Write atomic via .tmp + replace pra evitar leitor ver estado parcial
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

        // ----- typed accessors pra um item arbitrário do shop -----
        // Cada item (pack/structure/accessory) é Dictionary<string,object>
        // dentro da sub-dict. Helpers pra get/set sem repetir cast.
        public static int GetInt(Dictionary<string, object> item, string key, int fallback = 0)
        {
            object v;
            if (item == null || !item.TryGetValue(key, out v) || v == null) return fallback;
            try { return Convert.ToInt32(v); } catch { return fallback; }
        }
        public static string GetStr(Dictionary<string, object> item, string key, string fallback = "")
        {
            object v;
            if (item == null || !item.TryGetValue(key, out v) || v == null) return fallback;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        public static bool GetBool(Dictionary<string, object> item, string key, bool fallback = false)
        {
            object v;
            if (item == null || !item.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            bool b;
            return bool.TryParse(s, out b) ? b : fallback;
        }
    }
}
