using System;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace YgoMasterClient
{
    // Registry + dispatch for relic duel hooks. Lua scripts pin callbacks via on(name, fn); each callback is
    // stored with the params table active when the script loaded, and is invoked as fn(ctx, params). "buff"
    // is a per-card query (EvalBuff sums {atk,def,level}); other names are events (Fire) for later. Cleared
    // at duel end. The Lua calls go through RoguelikeLua (the shared Script).
    static class RoguelikeDuelHooks
    {
        class Entry { public DynValue Fn; public DynValue Params; }
        static readonly Dictionary<string, List<Entry>> _hooks = new Dictionary<string, List<Entry>>();
        static DynValue _loadParams = DynValue.Nil;   // params table active during the current LoadScript

        public static bool HasBuff { get { List<Entry> l; return _hooks.TryGetValue("buff", out l) && l.Count > 0; } }
        public static bool HasAny { get { return _hooks.Count > 0; } }
        // True if at least one callback is registered under name (cheap gate for event polling).
        public static bool Has(string name) { List<Entry> l; return _hooks.TryGetValue(name, out l) && l.Count > 0; }

        // The params table the next on(...) registrations capture (set by RoguelikeLua.LoadScript around DoFile).
        public static void SetLoadParams(DynValue p) { _loadParams = p ?? DynValue.Nil; }

        // exposed to Lua as on(name, fn)
        public static void Register(string name, DynValue fn)
        {
            if (string.IsNullOrEmpty(name) || fn == null || fn.Type != DataType.Function) return;
            List<Entry> list;
            if (!_hooks.TryGetValue(name, out list)) { list = new List<Entry>(); _hooks[name] = list; }
            list.Add(new Entry { Fn = fn, Params = _loadParams });
        }

        public static void Clear() { _hooks.Clear(); _loadParams = DynValue.Nil; }

        static int Num(Table t, string k) { DynValue v = t.Get(k); return v != null && v.Type == DataType.Number ? (int)v.Number : 0; }

        // per-card buff query: ctx built by the caller. Sums {atk,def,level} from each "buff" callback.
        public static bool EvalBuff(DynValue ctx, out int atk, out int def, out int level)
        {
            atk = 0; def = 0; level = 0;
            List<Entry> list;
            if (!_hooks.TryGetValue("buff", out list)) return false;
            for (int i = 0; i < list.Count; i++)
            {
                DynValue r;
                try { r = RoguelikeLua.Call(list[i].Fn, ctx, list[i].Params); }
                catch (Exception ex) { Console.WriteLine("[hook] buff EX: " + ex.Message); continue; }
                if (r == null || r.Type != DataType.Table) continue;
                atk += Num(r.Table, "atk");
                def += Num(r.Table, "def");
                level += Num(r.Table, "level");
            }
            return atk != 0 || def != 0 || level != 0;
        }

        // generic event fire (no return value) -- summon/set/... events. Each hook runs as a coroutine so it can
        // block on select_card (which yields until the player picks, then resumes the hook with the chosen card).
        public static void Fire(string name, DynValue ctx)
        {
            List<Entry> list;
            if (!_hooks.TryGetValue(name, out list)) return;
            for (int i = 0; i < list.Count; i++)
            {
                try { RoguelikeLua.CallCoroutine(list[i].Fn, ctx, list[i].Params); }
                catch (Exception ex) { Console.WriteLine("[hook] " + name + " EX: " + ex.Message); }
            }
        }

        public static void DumpList()
        {
            if (_hooks.Count == 0) { Console.WriteLine("[rghook] (no hooks)"); return; }
            foreach (KeyValuePair<string, List<Entry>> kv in _hooks)
                Console.WriteLine("[rghook] " + kv.Key + ": " + kv.Value.Count + " handler(s)");
        }
    }
}
