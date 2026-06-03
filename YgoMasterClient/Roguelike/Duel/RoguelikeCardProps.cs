using System.Collections.Generic;
using YgoMaster;

namespace YgoMasterClient
{
    // The archetype-agnostic "simple kind" of a card (one per card), derived from its CardFrame by
    // FrameToSimpleKind. Exposed to Lua as CardSimpleKind (the enum names) to match card_props(cid).simple_kind.
    enum CardSimpleKind { Normal, Effect, Ritual, Fusion, Synchro, Xyz, Link, Pendulum, Spell, Trap, Token, God }

    // Static card properties by cid (race/attr/level/simple_kind/atk/def), resolved from the game's CARD_Prop
    // data via YdkHelper.GameCardInfo (PropA/PropB decode) and cached. Single source for both the
    // RoguelikeStatBuff type filter and the duelHook get_card_props, so buff rules and Lua hooks agree.
    // Props are by-cid (static), independent of any in-duel instance.
    static class RoguelikeCardProps
    {
        public struct Props
        {
            public int Cid, Race, Attr, Level, Atk, Def;
            public string SimpleKind;   // CardSimpleKind name: Normal/Effect/Fusion/Synchro/Xyz/Link/Ritual/Pendulum/Spell/Trap/Token/God
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
            p.SimpleKind = FrameToSimpleKind(gi.Frame).ToString();
            p.Frame = gi.Frame.ToString();
            p.Kind = gi.Kind.ToString();
            p.Icon = gi.Icon.ToString();
            _cache[cid] = p;
            return true;
        }

        // CardFrame -> the card's CardSimpleKind (archetype-agnostic token used by buff rules and Lua filters).
        public static CardSimpleKind FrameToSimpleKind(CardFrame f)
        {
            switch (f)
            {
                case CardFrame.Normal: return CardSimpleKind.Normal;
                case CardFrame.Effect: return CardSimpleKind.Effect;
                case CardFrame.Ritual:
                case CardFrame.RitualPend: return CardSimpleKind.Ritual;
                case CardFrame.Fusion:
                case CardFrame.FusionPend: return CardSimpleKind.Fusion;
                case CardFrame.Sync:
                case CardFrame.Dsync:
                case CardFrame.SyncPend: return CardSimpleKind.Synchro;
                case CardFrame.Xyz:
                case CardFrame.XyzPend: return CardSimpleKind.Xyz;
                case CardFrame.Link: return CardSimpleKind.Link;
                case CardFrame.Pend:
                case CardFrame.PendFx: return CardSimpleKind.Pendulum;
                case CardFrame.Magic: return CardSimpleKind.Spell;
                case CardFrame.Trap: return CardSimpleKind.Trap;
                case CardFrame.Token: return CardSimpleKind.Token;
                case CardFrame.Oberisk:
                case CardFrame.Osiris:
                case CardFrame.Ra: return CardSimpleKind.God;
                default: return CardSimpleKind.Effect;
            }
        }
    }
}
