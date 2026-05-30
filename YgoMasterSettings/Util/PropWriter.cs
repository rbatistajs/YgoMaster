using System;
using System.Collections.Generic;
using System.IO;
using YgoMaster;

namespace YgoMasterSettings.Util
{
    // In-place edits a CARD_Prop.bytes file: per-cid overrides for Kind
    // (PropA bits 16-21), Icon (PropB bits 18-20) and the Legend flag
    // (PropB mask 0x78000000). Other bits are preserved so ATK/DEF/Level/
    // Attr/Type/Scale stay intact.
    //
    // Slot format (8 bytes/slot, little-endian uint32 + uint32):
    //   PropA: [cid 16][kind 6][attr 4][level 4][unused 2]
    //   PropB: [atk 9][def 9][icon 3][type 5][scale 4][exist 1][unused 1]
    static class PropWriter
    {
        // Legend flag the native duel.dll reads via DLL_CardIsLegend. We set
        // bit 27 to mark; clearing wipes the whole mask so no bit leaks.
        const uint LegendMask = 0x78000000u;
        const uint LegendSetBit = 0x08000000u;

        public class Edit
        {
            public CardKind? Kind;
            public CardIcon? Icon;
            public bool? Legend;
        }

        public static int Save(string dataDir, Dictionary<int, Edit> edits,
                               Action<string> progress = null)
        {
            string propPath = Path.Combine(dataDir, "CardData", "#", "CARD_Prop.bytes");
            if (!File.Exists(propPath))
                throw new FileNotFoundException("CARD_Prop.bytes not found: " + propPath);

            if (progress != null) progress("Reading CARD_Prop.bytes…");
            byte[] data = File.ReadAllBytes(propPath);
            int slots = data.Length / 8;
            int applied = 0;

            if (progress != null) progress("Applying " + edits.Count + " prop edits…");
            for (int i = 0; i < slots; i++)
            {
                int off = i * 8;
                uint a = (uint)(data[off] | (data[off+1] << 8) | (data[off+2] << 16) | (data[off+3] << 24));
                uint b = (uint)(data[off+4] | (data[off+5] << 8) | (data[off+6] << 16) | (data[off+7] << 24));
                int cid = (int)(a & 0xFFFF);
                Edit ed;
                if (!edits.TryGetValue(cid, out ed)) continue;
                if (ed.Kind.HasValue)
                {
                    a = (a & ~(0x3Fu << 16)) | (((uint)ed.Kind.Value & 0x3F) << 16);
                }
                if (ed.Icon.HasValue)
                {
                    b = (b & ~(0x7u << 18)) | (((uint)ed.Icon.Value & 0x7) << 18);
                }
                if (ed.Legend.HasValue)
                {
                    b = ed.Legend.Value ? (b | LegendSetBit) : (b & ~LegendMask);
                }
                data[off]   = (byte)(a & 0xFF);
                data[off+1] = (byte)((a >> 8) & 0xFF);
                data[off+2] = (byte)((a >> 16) & 0xFF);
                data[off+3] = (byte)((a >> 24) & 0xFF);
                data[off+4] = (byte)(b & 0xFF);
                data[off+5] = (byte)((b >> 8) & 0xFF);
                data[off+6] = (byte)((b >> 16) & 0xFF);
                data[off+7] = (byte)((b >> 24) & 0xFF);
                applied++;
            }

            if (progress != null) progress("Backup…");
            string bkpDir = Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(propPath))),
                "_bkp");
            Directory.CreateDirectory(bkpDir);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string bkp = Path.Combine(bkpDir, "CARD_Prop." + ts + ".bak.bytes");
            File.Copy(propPath, bkp, overwrite: true);

            if (progress != null) progress("Writing CARD_Prop.bytes…");
            string tmp = propPath + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Replace(tmp, propPath, null, ignoreMetadataErrors: true);
            return applied;
        }
    }
}
