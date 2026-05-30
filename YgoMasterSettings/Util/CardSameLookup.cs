using System;
using System.Collections.Generic;
using System.IO;

namespace YgoMasterSettings.Util
{
    // Load/save de CARD_Same.bytes (DataLE/CardData/MD/).
    //
    // Formato: array de trios uint16 little-endian
    //   (variant_cid, canon_cid, slot_idx)
    //
    // Cada trio = 6 bytes. Variantes que apontam pra outras variantes
    // formam cadeia (resolve em ResolveCanon).
    //
    // Save preserva ordem original das entries — só mutates entries
    // existentes (não adiciona/remove). Permite editar canonical por
    // variante; cids sem entry existente são ignorados (no edit).
    class CardSameLookup
    {
        public class Entry
        {
            public ushort Variant;
            public ushort Canon;
            public ushort Idx;
        }

        public List<Entry> Entries { get; private set; }
        readonly string _path;

        public CardSameLookup(string dataDir)
        {
            _path = Path.Combine(dataDir, "CardData", "MD", "CARD_Same.bytes");
            Entries = new List<Entry>();
            Load();
        }

        void Load()
        {
            Entries.Clear();
            if (!File.Exists(_path)) return;
            byte[] data = File.ReadAllBytes(_path);
            int n = data.Length / 6;
            for (int i = 0; i < n; i++)
            {
                int off = i * 6;
                Entries.Add(new Entry
                {
                    Variant = (ushort)(data[off]   | (data[off+1] << 8)),
                    Canon   = (ushort)(data[off+2] | (data[off+3] << 8)),
                    Idx     = (ushort)(data[off+4] | (data[off+5] << 8)),
                });
            }
        }

        // Acesso direto: variant -> canon (sem resolver chain)
        public int GetDirectCanon(int variant)
        {
            foreach (Entry e in Entries)
                if (e.Variant == variant) return e.Canon;
            return variant;   // não há entry = canonical de si mesmo
        }

        // Resolve chain: variant -> canon -> ... -> final
        public int ResolveCanon(int cid)
        {
            HashSet<int> seen = new HashSet<int>();
            int cur = cid;
            while (true)
            {
                int next = GetDirectCanon(cur);
                if (next == cur || seen.Contains(next)) return cur;
                seen.Add(cur);
                cur = next;
            }
        }

        // Set: muda o canonical direto de uma variante existente.
        // Retorna true se algo mudou. Não cria entry nova — só edit.
        public bool SetCanon(int variant, int newCanon)
        {
            bool changed = false;
            foreach (Entry e in Entries)
            {
                if (e.Variant == variant && e.Canon != (ushort)newCanon)
                {
                    e.Canon = (ushort)newCanon;
                    changed = true;
                }
            }
            return changed;
        }

        // Mapeamento variant→canon resolvido (1 lookup) — usado por
        // callers que precisam de um dict completo (ex: column SameCID
        // do CardListTab).
        public Dictionary<int, int> ResolveAll()
        {
            // Direct map
            Dictionary<int, int> direct = new Dictionary<int, int>();
            foreach (Entry e in Entries)
                if (!direct.ContainsKey(e.Variant)) direct[e.Variant] = e.Canon;
            // Resolve chains
            Dictionary<int, int> resolved = new Dictionary<int, int>(direct.Count);
            foreach (KeyValuePair<int, int> kv in direct)
            {
                int cur = kv.Value;
                HashSet<int> seen = new HashSet<int> { kv.Key };
                while (direct.TryGetValue(cur, out int next))
                {
                    if (seen.Contains(cur) || next == cur) break;
                    seen.Add(cur);
                    cur = next;
                }
                resolved[kv.Key] = cur;
            }
            return resolved;
        }

        public void Save()
        {
            byte[] buf = new byte[Entries.Count * 6];
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry e = Entries[i];
                int off = i * 6;
                buf[off]   = (byte)(e.Variant & 0xFF);
                buf[off+1] = (byte)((e.Variant >> 8) & 0xFF);
                buf[off+2] = (byte)(e.Canon & 0xFF);
                buf[off+3] = (byte)((e.Canon >> 8) & 0xFF);
                buf[off+4] = (byte)(e.Idx & 0xFF);
                buf[off+5] = (byte)((e.Idx >> 8) & 0xFF);
            }
            // Backup + write atomic (mesmo padrão do CardTextWriter)
            string bkpDir = Path.Combine(Path.GetDirectoryName(_path), "_bkp");
            // _bkp dentro de MD/ não é a convenção — vai em DataLE/_bkp/
            // pra alinhar com os outros backups.
            string dataDirBkp = Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(_path))),
                "_bkp");
            Directory.CreateDirectory(dataDirBkp);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string bkp = Path.Combine(dataDirBkp, "CARD_Same." + ts + ".bak.bytes");
            if (File.Exists(_path)) File.Copy(_path, bkp, overwrite: true);

            string tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, buf);
            if (File.Exists(_path)) File.Replace(tmp, _path, null, ignoreMetadataErrors: true);
            else                    File.Move(tmp, _path);
        }
    }
}
