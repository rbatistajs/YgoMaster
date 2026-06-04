using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MoonSharp.Interpreter;

namespace YgoMasterClient
{
    // chain_effect{ source_uid, cost?, effect }: run an arbitrary scripted effect on a REAL chain. We open the
    // chain by activating a "blank" effect -- the source card's own cid, when that card is a Normal (effect-less)
    // monster, is an effId with an empty program, so the engine opens/animates/closes the chain with no effect of
    // its own. We then inject our own callbacks at the engine's two phases (same order a real card uses):
    //   cost   -- at CardHappen (the cost phase: where a real card pays its tribute/discard/LP). Optional.
    //   effect -- at ChainStep (the resolution: where the real effect happens).
    // Each callback applies our primitives (special_summon, destroy, draw, select, debug_command, ...). This gives
    // a real chain link (cost/timing/negation windows) carrying a custom cost+effect, without the engine's effId
    // VM. The source must be a field monster we control; its cid is reused as the blank effId (so it passes the
    // engine's "this card is here with that effId" activation check).
    static class RoguelikeChainEffect
    {
        class Pending { public int EffId; public DynValue Cost; public DynValue Effect; public DynValue Ctx; public bool CostFired; }
        static readonly List<Pending> _pending = new List<Pending>();

        // Cheap gate for the per-view poll in DuelDll.RunEffect (skip unless something is pending).
        public static bool Wants() { return _pending.Count > 0; }

        // Begin a blank-chain custom effect anchored on the field card at sourceUid. cost is optional. Queues the
        // activation now; cost fires at the cost phase, effect at resolution. Returns false on bad args/source.
        public static bool Begin(int sourceUid, DynValue cost, DynValue effect, DynValue ctx)
        {
            if (effect == null || effect.Type != DataType.Function) { Console.WriteLine("[chain_effect] needs an effect function"); return false; }
            if (cost != null && cost.Type != DataType.Function) cost = null;   // cost is optional
            if (sourceUid <= 0) { Console.WriteLine("[chain_effect] needs source_uid"); return false; }
            IntPtr buf = Marshal.AllocHGlobal(64);
            try
            {
                if (!DuelDll.CardBasicValByUid(sourceUid, buf, out int player, out int location, out int index))
                { Console.WriteLine("[chain_effect] source uid " + sourceUid + " not found"); return false; }
                if (location >= 7) { Console.WriteLine("[chain_effect] source must be a field monster (zone 0-6), got location " + location); return false; }
                int cid = (ushort)Marshal.ReadInt16(buf, 0);
                if (cid == 0) { Console.WriteLine("[chain_effect] source has no cid"); return false; }
                _pending.Add(new Pending { EffId = cid, Cost = cost, Effect = effect, Ctx = ctx });
                DuelDll.QueueActivateEffect(player, 3, location, cid, (uint)sourceUid, 0, 0);   // category 3 = monster, blank effId = the source's own cid
                return true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // Cost phase (CardHappen, param2 = the resolving effId): run the cost fn once, before the effect resolves.
        public static void OnCost(int effId)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                Pending p = _pending[i];
                if (p.EffId == effId && !p.CostFired)
                {
                    p.CostFired = true;
                    if (p.Cost != null) RoguelikeLua.Call(p.Cost, p.Ctx, DynValue.Nil);
                    return;
                }
            }
        }

        // Resolution (ChainStep, param3 = the resolving effId): run the effect fn and finish this chain effect.
        public static void OnEffect(int effId)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].EffId == effId)
                {
                    DynValue fn = _pending[i].Effect;
                    DynValue ctx = _pending[i].Ctx;
                    _pending.RemoveAt(i);
                    RoguelikeLua.Call(fn, ctx, DynValue.Nil);
                    return;
                }
            }
        }

        // Drop pending effects (called on duel begin) so a stale fn can't fire into a new duel.
        public static void Clear() { _pending.Clear(); }
    }
}
