using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using YgoMaster;

namespace YgoMasterSettings.Util
{
    // Metadados extraídos de DataLE/CardData/* (decifrados).
    // Bit layout mirror do YdkHelper.GameCardInfo — mantido inline aqui
    // pra evitar puxar deps de YdkHelper no Settings.
    class CardInfo
    {
        public int       Id;
        public string    Name;
        public string    Desc;   // CARD_Desc.bytes — texto completo do card
        public int       PropA;
        public int       PropB;

        public CardKind  Kind  { get { return (CardKind)((PropA >> 16) & 0x3F); } }
        public int       Attr  { get { return (PropA >> (16 + 6)) & 0xF; } }
        public int       Level { get { return (PropA >> (16 + 6 + 4)) & 0xF; } }
        public int       Atk   { get { return (PropB & 0x1FF) * 10; } }
        public int       Def   { get { return ((PropB >> 9) & 0x1FF) * 10; } }
        public CardIcon  Icon  { get { return (CardIcon)((PropB >> (9 + 9)) & 0x7); } }
        public int       Type  { get { return (PropB >> (9 + 9 + 3)) & 0x1F; } }

        // Legend flag the duel.dll reads (PropB mask 0x78000000). Source of
        // truth for Legend status — replaces the old CardLegend.json sidecar.
        public bool      IsLegend { get { return (PropB & 0x78000000) != 0; } }

        // Any card can be flagged Legend EXCEPT pendulums: their scale lives in
        // PropB bits 26-29, which overlaps the Legend mask (0x78000000), so the
        // bit would change the scale instead of flagging Legend.
        public bool      IsLegendCapable
        {
            get
            {
                switch (Kind)
                {
                    case CardKind.Pend:       case CardKind.PendFx:
                    case CardKind.PendTuner:  case CardKind.PendFlip:
                    case CardKind.PendNTuner: case CardKind.PendSpirit:
                    case CardKind.XyzPend:    case CardKind.SyncPend:
                    case CardKind.FusionPend: case CardKind.SpPend:
                    case CardKind.RitualPend:
                        return false;
                    default:
                        return true;
                }
            }
        }

        // Frame mirror enxuto do GameCardInfo.Frame (cobre os casos
        // comuns; Token/God variants caem em Normal/Effect).
        public CardFrame Frame
        {
            get
            {
                switch (Kind)
                {
                    case CardKind.Normal:    case CardKind.Tuner:
                        return CardFrame.Normal;
                    case CardKind.Fusion:    case CardKind.FusionFx:
                    case CardKind.FusionTuner: case CardKind.FusionTunerFX:
                    case CardKind.R_Fusion: case CardKind.R_FusionFX:
                        return CardFrame.Fusion;
                    case CardKind.FusionPend: return CardFrame.FusionPend;
                    case CardKind.Ritual:    case CardKind.RitualFx:
                    case CardKind.RitualSpirit: case CardKind.RirualTunerFX:
                    case CardKind.RitualFlip:
                        return CardFrame.Ritual;
                    case CardKind.RitualPend: return CardFrame.RitualPend;
                    case CardKind.Magic: return CardFrame.Magic;
                    case CardKind.Trap:  return CardFrame.Trap;
                    case CardKind.Sync:  case CardKind.SyncFx: case CardKind.SyncTuner:
                        return CardFrame.Sync;
                    case CardKind.SyncPend: return CardFrame.SyncPend;
                    case CardKind.Xyz:   case CardKind.XyzFx:
                        return CardFrame.Xyz;
                    case CardKind.XyzPend: return CardFrame.XyzPend;
                    case CardKind.Pend:  case CardKind.PendNTuner:
                        return CardFrame.Pend;
                    case CardKind.PendFx: case CardKind.PendFlip: case CardKind.PendTuner:
                    case CardKind.SpPend: case CardKind.PendSpirit:
                        return CardFrame.PendFx;
                    case CardKind.Link: case CardKind.LinkFx:
                        return CardFrame.Link;
                    default:
                        return CardFrame.Effect;
                }
            }
        }

        public bool IsMonster
        {
            get
            {
                CardFrame f = Frame;
                return f != CardFrame.Magic && f != CardFrame.Trap && f != CardFrame.Token;
            }
        }
    }

    // Parser standalone dos CardData/*.bytes (já decifrados). Mirror
    // enxuto de YdkHelper.LoadCardDataFromGame — exposto como cache
    // estática por dataDir.
    //
    // Formato (mesmo que YdkHelper): 3 arquivos paralelos slot-a-slot:
    //   - CARD_Indx.bytes: pares (uint nameOffset, uint descOffset)
    //   - CARD_Name.bytes: strings null-terminated UTF-8 indexadas
    //   - CARD_Prop.bytes: pares (int PropA, int PropB) — PropA low
    //                       16 bits = cardId.
    //
    // Custo ~50ms first call (~5k cards). Cacheado global.
    static class CardNameLookup
    {
        static readonly object _lock = new object();
        static string _cachedDir;
        static Dictionary<int, CardInfo> _cachedInfos;

        // Wrappers de compat — Load(dataDir) retorna só nomes; LoadFull
        // retorna o dict completo. O tab Card Rarities usa LoadFull.
        public static Dictionary<int, string> Load(string dataDir)
        {
            Dictionary<int, CardInfo> full = LoadFull(dataDir);
            Dictionary<int, string> result = new Dictionary<int, string>(full.Count);
            foreach (KeyValuePair<int, CardInfo> kv in full)
                result[kv.Key] = kv.Value.Name;
            return result;
        }

        public static Dictionary<int, CardInfo> LoadFull(string dataDir)
        {
            lock (_lock)
            {
                if (_cachedDir == dataDir && _cachedInfos != null)
                    return _cachedInfos;
                _cachedDir = dataDir;
                _cachedInfos = ParseInternal(dataDir);
                return _cachedInfos;
            }
        }

        // Limpa o cache (próximo Load/LoadFull vai re-parsear). Chamado
        // após salvar mudanças nos .bytes pra que a UI mostre o estado
        // atual sem precisar reiniciar.
        public static void Invalidate()
        {
            lock (_lock)
            {
                _cachedDir = null;
                _cachedInfos = null;
            }
        }

        static Dictionary<int, CardInfo> ParseInternal(string dataDir)
        {
            Dictionary<int, CardInfo> result = new Dictionary<int, CardInfo>();
            string indxPath = Path.Combine(dataDir, "CardData", "en-US", "CARD_Indx.bytes");
            string namePath = Path.Combine(dataDir, "CardData", "en-US", "CARD_Name.bytes");
            string descPath = Path.Combine(dataDir, "CardData", "en-US", "CARD_Desc.bytes");
            string propPath = Path.Combine(dataDir, "CardData", "#",     "CARD_Prop.bytes");
            if (!File.Exists(indxPath) || !File.Exists(namePath) || !File.Exists(propPath))
                return result;
            bool hasDesc = File.Exists(descPath);

            try
            {
                using (BinaryReader indxRd = new BinaryReader(File.OpenRead(indxPath)))
                using (BinaryReader nameRd = new BinaryReader(File.OpenRead(namePath)))
                using (BinaryReader propRd = new BinaryReader(File.OpenRead(propPath)))
                {
                    Dictionary<uint, string> namesByOffset = ReadStrings(nameRd);
                    Dictionary<uint, string> descsByOffset = null;
                    if (hasDesc)
                    {
                        using (BinaryReader descRd = new BinaryReader(File.OpenRead(descPath)))
                            descsByOffset = ReadStrings(descRd);
                    }
                    // Coleta (nameOff, descOff) por slot
                    List<KeyValuePair<uint, uint>> slots = new List<KeyValuePair<uint, uint>>();
                    while (true)
                    {
                        uint nameOff = indxRd.ReadUInt32();
                        uint descOff = indxRd.ReadUInt32();
                        if (indxRd.BaseStream.Position >= indxRd.BaseStream.Length)
                            break;   // sentinel
                        slots.Add(new KeyValuePair<uint, uint>(nameOff, descOff));
                    }
                    // PropA/PropB são paralelos aos slots do Indx
                    foreach (KeyValuePair<uint, uint> slot in slots)
                    {
                        int propA = propRd.ReadInt32();
                        int propB = propRd.ReadInt32();
                        int cardId = propA & 0xFFFF;
                        if (cardId <= 0) continue;
                        string name, desc = null;
                        namesByOffset.TryGetValue(slot.Key, out name);
                        if (descsByOffset != null) descsByOffset.TryGetValue(slot.Value, out desc);
                        result[cardId] = new CardInfo
                        {
                            Id = cardId,
                            Name = name ?? "",
                            Desc = desc ?? "",
                            PropA = propA,
                            PropB = propB,
                        };
                    }
                }
            }
            catch
            {
                // Silent — UI mostra dict parcial/vazio.
            }
            return result;
        }

        static Dictionary<uint, string> ReadStrings(BinaryReader rd)
        {
            Dictionary<uint, string> result = new Dictionary<uint, string>();
            long len = rd.BaseStream.Length;
            while (rd.BaseStream.Position < len)
            {
                uint offset = (uint)rd.BaseStream.Position;
                string s = ReadNullTerminatedString(rd, Encoding.UTF8);
                result[offset] = s;
            }
            return result;
        }

        static string ReadNullTerminatedString(BinaryReader rd, Encoding encoding)
        {
            // Workaround do YdkHelper: StreamReader lê demais e bagunça
            // position; recalculamos com byte count manual.
            StringBuilder sb = new StringBuilder();
            StreamReader sr = new StreamReader(rd.BaseStream, encoding);
            long start = rd.BaseStream.Position;
            int ch;
            while ((ch = sr.Read()) != -1)
            {
                char c = (char)ch;
                if (c == '\0') break;
                sb.Append(c);
            }
            string s = sb.ToString();
            rd.BaseStream.Position = start + encoding.GetByteCount(s + '\0');
            return s;
        }
    }
}
