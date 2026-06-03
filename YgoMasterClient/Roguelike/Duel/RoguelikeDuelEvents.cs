using System.Collections.Generic;
using MoonSharp.Interpreter;
using YgoMaster;   // DuelViewType

namespace YgoMasterClient
{
    // Translates duel view-events into the relic lifecycle hooks (summon / special_summon / set), each carrying a
    // status:
    //   init    -- performed: RunSummon / RunSpSummon (uid in param2), or CardSet (uid = the preceding CardMove,
    //              since CardSet's param2 is the zone -- the caller passes the right uid).
    //   success -- the engine confirmed it (CutinSuccess). Monster summon/set get this; a spell/trap set never
    //              raises one, so it stays at init.
    //   failed  -- the pending card was destroyed (CardBreak) before success, e.g. a negation trap.
    // DuelDll feeds OnViewEvent; the ctx is RoguelikeLua.BuildCardState + status; dispatch goes through the generic
    // RoguelikeDuelHooks registry. (Negation at declaration, before the card reaches the field, isn't tracked.)
    static class RoguelikeDuelEvents
    {
        static readonly Dictionary<int, string> _pending = new Dictionary<int, string>();   // uid -> hook name

        // Cheap gate for the per-event poll: true if any lifecycle hook is registered.
        public static bool Wants()
        {
            return RoguelikeDuelHooks.Has("summon") || RoguelikeDuelHooks.Has("special_summon") || RoguelikeDuelHooks.Has("set");
        }

        public static void OnViewEvent(int viewId, int uid)
        {
            if (viewId == (int)DuelViewType.RunSummon) Start("summon", uid);
            else if (viewId == (int)DuelViewType.RunSpSummon) Start("special_summon", uid);
            else if (viewId == (int)DuelViewType.CardSet) Start("set", uid);
            else if (viewId == (int)DuelViewType.CutinSuccess) Resolve(uid, "success");
            else if (viewId == (int)DuelViewType.CardBreak) Resolve(uid, "failed");
        }

        // Turn-change event: fire on("turn", { player_id, player_type }) so scripts can reset per-turn state
        // ("until end of turn"). param1 of a TurnChange view = the player whose turn is starting; prev turn ended.
        public static void OnTurn(int turnPlayer)
        {
            if (!RoguelikeDuelHooks.Has("turn")) return;
            RoguelikeDuelHooks.Fire("turn", DynValue.NewTable(RoguelikeLua.BuildTurnState(turnPlayer)));
        }

        // Phase-change event: fire on("phase", { player_id, player_type, phase }). A PhaseChange view's
        // param1 = the player whose phase it is, param2 = the new DuelPhase (0-5).
        public static void OnPhase(int player, int phase)
        {
            if (!RoguelikeDuelHooks.Has("phase")) return;
            RoguelikeDuelHooks.Fire("phase", DynValue.NewTable(RoguelikeLua.BuildPhaseState(player, phase)));
        }

        // Card-activation event: fire on("activate", { cid, player_id, player_type }) when an effect is put on the
        // chain (ChainPush). cid is the activating card's id; use card_props(cid) to gate on spell/trap/etc.
        public static void OnActivate(int cid, int player)
        {
            if (!RoguelikeDuelHooks.Has("activate")) return;
            RoguelikeDuelHooks.Fire("activate", DynValue.NewTable(RoguelikeLua.BuildActivateState(cid, player)));
        }

        static void Start(string hook, int uid) { _pending[uid] = hook; Fire(hook, uid, "init"); }

        static void Resolve(int uid, string status)
        {
            string hook;
            if (_pending.TryGetValue(uid, out hook)) { _pending.Remove(uid); Fire(hook, uid, status); }
        }

        static void Fire(string hook, int uid, string status)
        {
            if (!RoguelikeDuelHooks.Has(hook)) return;
            Table t = RoguelikeLua.BuildCardState(uid);
            if (t == null) return;
            t["status"] = status;
            RoguelikeDuelHooks.Fire(hook, DynValue.NewTable(t));
        }
    }
}
