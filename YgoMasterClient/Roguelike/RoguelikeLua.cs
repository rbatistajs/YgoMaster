using System;
using System.IO;
using System.Runtime.InteropServices;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Loaders;

namespace YgoMasterClient
{
    // Lua runtime for the roguelike duel layer (MoonSharp, sandboxed). v1 = engine + read-only API + a dev
    // runner. Actions and event dispatch come later; the buff may move here too. A single shared Script holds
    // the API (registered once); reads go through DuelDll.DuelLibBase -> duel state. Offsets per
    // duel-action-primitives.md: duel state = *(libBase+0x11adc50), per-player stride 0xddc.
    static class RoguelikeLua
    {
        const long DuelStateRva = 0x11adc50;
        const int PlayerStride = 0xddc;

        static Script _script;

        static Script Engine()
        {
            if (_script == null)
            {
                // MoonSharp auto-detects Unity and builds a UnityAssetsScriptLoader for its static DefaultOptions;
                // its Resources reflection throws a (caught, benign) NullReference under IL2CPP, logged once as
                // "Error initializing UnityScriptLoader" when the first Script is created. Forcing the Unity flags
                // off didn't stop it (the default loader is built regardless), and we never use it -- we pin our
                // own FileSystemScriptLoader below. So just swallow that one-time console noise during creation.
                TextWriter prevOut = Console.Out, prevErr = Console.Error;
                Script s;
                try
                {
                    try { Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null); } catch { }
                    s = new Script(CoreModules.Preset_HardSandbox);   // no io/os/require; string/math/table ok
                }
                finally
                {
                    try { Console.SetOut(prevOut); Console.SetError(prevErr); } catch { }
                }
                s.Options.ScriptLoader = new FileSystemScriptLoader();
                RegisterApi(s);
                _script = s;
            }
            return _script;
        }

        // --- duel state reads ---
        static IntPtr DuelState()
        {
            IntPtr lib = DuelDll.DuelLibBase;
            if (lib == IntPtr.Zero) return IntPtr.Zero;
            return Marshal.ReadIntPtr((IntPtr)(lib.ToInt64() + DuelStateRva));
        }

        // zone count: duel state + player*0xddc + off (0x0c hand, 0x10 deck, 0x14 grave, 0x18 extra, 0x1c banish)
        static int ZoneCount(int player, int off)
        {
            IntPtr ds = DuelState();
            if (ds == IntPtr.Zero) return 0;
            return Marshal.ReadInt32((IntPtr)(ds.ToInt64() + (long)(player & 1) * PlayerStride + off));
        }

        // top of main deck: entry [cid, state] at base 0x3c4 + (player*0x377 + 0)*4
        static int DeckTop(int player)
        {
            IntPtr ds = DuelState();
            if (ds == IntPtr.Zero) return 0;
            long entry = ds.ToInt64() + 0x3c4 + ((long)(player & 1) * 0x377) * 4;
            return (ushort)Marshal.ReadInt16((IntPtr)entry);
        }

        // --- API registration (read-only) ---
        static void RegisterApi(Script s)
        {
            s.Globals["card_props"] = (Func<int, DynValue>)(cid =>
            {
                RoguelikeCardProps.Props p;
                if (!RoguelikeCardProps.TryGet(cid, out p)) return DynValue.Nil;
                return DynValue.NewTable(PropsTable(s, p));
            });
            s.Globals["hand_count"]   = (Func<int, int>)(p => ZoneCount(p, 0x0c));
            s.Globals["deck_count"]   = (Func<int, int>)(p => ZoneCount(p, 0x10));
            s.Globals["grave_count"]  = (Func<int, int>)(p => ZoneCount(p, 0x14));
            s.Globals["extra_count"]  = (Func<int, int>)(p => ZoneCount(p, 0x18));
            s.Globals["banish_count"] = (Func<int, int>)(p => ZoneCount(p, 0x1c));
            s.Globals["deck_top"] = (Func<int, DynValue>)(p =>
            {
                int cid = DeckTop(p);
                return cid == 0 ? DynValue.Nil : DynValue.NewNumber(cid);
            });
            s.Globals["log"] = (Action<DynValue>)(v => Console.WriteLine("[lua] " + (v == null ? "nil" : v.ToPrintString())));
            // on(name, fn): pin a callback to a hook. The params table active during the load is captured and
            // passed back as fn's 2nd arg on dispatch (see RoguelikeDuelHooks).
            s.Globals["on"] = (Action<string, DynValue>)((name, fn) => RoguelikeDuelHooks.Register(name, fn));
        }

        // Lua table from static card props (cid/race/attr/level/atk/def + subtype/frame/kind/icon).
        static Table PropsTable(Script s, RoguelikeCardProps.Props p)
        {
            Table t = new Table(s);
            t["cid"] = p.Cid; t["race"] = p.Race; t["attr"] = p.Attr; t["level"] = p.Level;
            t["atk"] = p.Atk; t["def"] = p.Def;
            t["subtype"] = p.SubType; t["frame"] = p.Frame; t["kind"] = p.Kind; t["icon"] = p.Icon;
            return t;
        }

        // Invoke a Lua callback (fn) with (ctx, params) on the shared engine. Used by RoguelikeDuelHooks.
        public static DynValue Call(DynValue fn, DynValue ctx, DynValue prm)
        {
            return Engine().Call(fn, new DynValue[] { ctx ?? DynValue.Nil, prm ?? DynValue.Nil });
        }

        // Per-card field buff query, called from the DuelGetFieldCardVal hook. The ctx carries only the LIVE
        // instance values (race/attr/level/atk/def come straight from outVal -- already altered by any in-duel
        // effect, e.g. DNA Surgery), plus cid/zone/mine. The static category (subtype/frame/kind/icon) is NOT
        // here; scripts pull it with card_props(c.cid). Then asks the "buff" hooks for the summed deltas.
        public static bool EvalFieldBuff(int cid, int race, int attr, int level, int atk, int def, int zone, bool mine,
                                         out int datk, out int ddef, out int dlevel)
        {
            datk = 0; ddef = 0; dlevel = 0;
            if (!RoguelikeDuelHooks.HasBuff) return false;
            Table t = new Table(Engine());
            t["cid"] = cid; t["race"] = race; t["attr"] = attr; t["level"] = level;
            t["atk"] = atk; t["def"] = def; t["zone"] = zone; t["mine"] = mine;
            return RoguelikeDuelHooks.EvalBuff(DynValue.NewTable(t), out datk, out ddef, out dlevel);
        }

        // dev: load a hook script with a params table (JSON), registering its on(...) callbacks.
        public static void LoadScript(string name, string paramsJson)
        {
            Script s = Engine();
            DynValue prm = DynValue.Nil;
            if (!string.IsNullOrEmpty(paramsJson))
            {
                try { prm = DynValue.NewTable(MoonSharp.Interpreter.Serialization.Json.JsonTableConverter.JsonToTable(paramsJson, s)); }
                catch (Exception ex) { Console.WriteLine("[rghook] bad params json: " + ex.Message); }
            }
            string path = Path.Combine(Program.DataDir, "Roguelike", "Scripts", name);
            if (!File.Exists(path)) { Console.WriteLine("[rghook] not found: " + path); return; }
            RoguelikeDuelHooks.SetLoadParams(prm);
            try { s.DoFile(path); Console.WriteLine("[rghook] loaded " + name); }
            catch (Exception ex) { Console.WriteLine("[rghook] EX: " + ex.Message); }
            RoguelikeDuelHooks.SetLoadParams(DynValue.Nil);
        }

        // --- dev runner ---
        public static void RunCode(string code)
        {
            try
            {
                DynValue r = Engine().DoString(code);
                Console.WriteLine("[rglua] => " + (r == null ? "nil" : r.ToPrintString()));
            }
            catch (Exception ex) { Console.WriteLine("[rglua] EX: " + ex.Message); }
        }

        public static void RunFile(string name)
        {
            string path = Path.Combine(Program.DataDir, "Roguelike", "Scripts", name);
            if (!File.Exists(path)) { Console.WriteLine("[rglua] not found: " + path); return; }
            try
            {
                DynValue r = Engine().DoFile(path);
                Console.WriteLine("[rglua] " + name + " => " + (r == null ? "nil" : r.ToPrintString()));
            }
            catch (Exception ex) { Console.WriteLine("[rglua] EX: " + ex.Message); }
        }
    }
}
