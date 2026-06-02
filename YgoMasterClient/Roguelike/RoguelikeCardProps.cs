using System.Collections.Generic;
using YgoMaster;

namespace YgoMasterClient
{
    // Static card properties by cid (race/attr/level/type/atk/def), resolved from the game's CARD_Prop
    // data via YdkHelper.GameCardInfo (PropA/PropB decode) and cached. Single source for both the
    // RoguelikeStatBuff type filter and the duelHook get_card_props, so buff rules and Lua hooks agree.
    // Props are by-cid (static), independent of any in-duel instance.
    static class RoguelikeCardProps
    {
        public struct Props
        {
            public int Cid, Race, Attr, Level, Atk, Def;
            public string Type;   // normal/effect/fusion/synchro/xyz/link/ritual/pendulum/spell/trap/token/god
            public string Frame;     // CardFrame name: Normal/Effect/Fusion/Sync/Xyz/Link/Magic/Trap/...
            public string Kind;      // CardKind name: Normal/Effect/Tuner/Toon/Spirit/Flip/Maximum/...
            public string Icon;      // CardIcon name (spell/trap): Normal/Continuous/Equip/QuickPlay/Field/Ritual/Counter
        }

        static Dictionary<int, YdkHelper.GameCardInfo> _data;
        static readonly Dictionary<int, Props> _cache = new Dictionary<int, Props>();

        // Lazy load of the whole game card table (cached). First touch reads disk; callers gate on a real
        // type/peek need so the duel hot path doesn't pay unless a rule/script actually asks.
        static Dictionary<int, YdkHelper.GameCardInfo> Data()
        {
            if (_data == null)
            {
                try { _data = YdkHelper.LoadCardDataFromGame(Program.DataDir); }
                catch { _data = new Dictionary<int, YdkHelper.GameCardInfo>(); }
            }
            return _data;
        }

        public static bool TryGet(int cid, out Props p)
        {
            if (_cache.TryGetValue(cid, out p)) return true;
            YdkHelper.GameCardInfo gi;
            if (!Data().TryGetValue(cid, out gi)) { p = default(Props); return false; }
            p = new Props();
            p.Cid = cid;
            p.Race = gi.Type;     // species (Dragon/Spellcaster/...)
            p.Attr = gi.Attr;
            p.Level = gi.Level;
            p.Atk = gi.Atk;
            p.Def = gi.Def;
            p.Type = FrameToType(gi.Frame);
            p.Frame = gi.Frame.ToString();
            p.Kind = gi.Kind.ToString();
            p.Icon = gi.Icon.ToString();
            _cache[cid] = p;
            return true;
        }

        // CardFrame -> the lowercase type token used by buff rules and Lua filters (archetype-agnostic).
        public static string FrameToType(CardFrame f)
        {
            switch (f)
            {
                case CardFrame.Normal: return "normal";
                case CardFrame.Effect: return "effect";
                case CardFrame.Ritual:
                case CardFrame.RitualPend: return "ritual";
                case CardFrame.Fusion:
                case CardFrame.FusionPend: return "fusion";
                case CardFrame.Sync:
                case CardFrame.Dsync:
                case CardFrame.SyncPend: return "synchro";
                case CardFrame.Xyz:
                case CardFrame.XyzPend: return "xyz";
                case CardFrame.Link: return "link";
                case CardFrame.Pend:
                case CardFrame.PendFx: return "pendulum";
                case CardFrame.Magic: return "spell";
                case CardFrame.Trap: return "trap";
                case CardFrame.Token: return "token";
                case CardFrame.Oberisk:
                case CardFrame.Osiris:
                case CardFrame.Ra: return "god";
                default: return "effect";
            }
        }
    }
}
