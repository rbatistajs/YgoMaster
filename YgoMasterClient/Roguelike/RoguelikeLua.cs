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
            // card_state(uid): live state of an instance, read straight from the duel.dll
            // (DuelDll.CardBasicValByUid -> DLL_DuelGetCardBasicVal). Field cards (zone 0-6) come back with
            // live values (effects applied); off-field cards (hand/grave/deck/extra/banish) with printed
            // values. Returns { uid, cid, race, attr, level, atk, def, player_id, player_type, location,
            // zone? } or nil if the instance is gone.
            s.Globals["card_state"] = (Func<int, DynValue>)(uid =>
            {
                Table t = BuildCardState(uid);
                return t == null ? DynValue.Nil : DynValue.NewTable(t);
            });
            // field_uid(player, zone): uid of the instance in a monster/field zone (0-6), or nil if empty.
            // Handy to pick a specific card in the console: card_state(field_uid(0, 2)).
            s.Globals["field_uid"] = (Func<int, int, DynValue>)((p, zone) =>
            {
                int uid = SlotUid(p, zone);
                return uid <= 0 ? DynValue.Nil : DynValue.NewNumber(uid);
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
            // special_summon(player, from, index): begin a special summon of source[index] for player. from =
            // "deck"/"grave"/"hand"/"extra"/"banish"; index 0 = top. MUST be called from inside a hook (active
            // resolution) -- the placement only pumps while the duel loop is live. The zone is chosen by the
            // owner (UI for the player, AI for the cpu). face/mode default 0 for now. Returns true if queued.
            s.Globals["special_summon"] = (Func<int, string, int, bool>)((player, from, index) =>
            {
                int loc = LocationCode(from);
                if (loc < 0) { Console.WriteLine("[lua] special_summon: bad source '" + from + "'"); return false; }
                return DuelDll.QueueSpecialSummon(player, loc, index, 0, 0, 0);
            });
            // debug_command(player, location, index, cmd): raw engine "swiss-army-knife"
            // (DLL_DuelComDoDebugCommand). Low-level escape hatch -- nicer per-cmd aliases (to_grave, draw, ...)
            // come later. cmd: 8=->grave, 6=->hand/draw, 9/10=banish, 11=destroy, 20=shuffle deck, ... (see
            // duel-action-primitives.md). location: 13=hand, 15=deck, 16=grave, 17=banish, 0-6=monster zones.
            // Must be called from inside a hook (active resolution).
            s.Globals["debug_command"] = (Action<int, int, int, int>)((player, location, index, cmd) =>
                DuelDll.QueueDebugCommand(player, location, index, cmd));
        }

        // Location name -> code (inverse of LocationName), for the action API.
        static int LocationCode(string name)
        {
            switch (name)
            {
                case "hand": return 13;
                case "extra": return 14;
                case "deck": return 15;
                case "grave": return 16;
                case "banish": return 17;
                default: return -1;
            }
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

        // Location code (from DuelDll.CardBasicValByUid) to a script-friendly name. 0-12 are on-field zones;
        // the rest are the off-field piles.
        static string LocationName(int location)
        {
            switch (location)
            {
                case 13: return "hand";
                case 14: return "extra";
                case 15: return "deck";
                case 16: return "grave";
                case 17: return "banish";
                default: return "field";
            }
        }

        // uniqueId of the instance currently in (player, zone): from the slot's pos field (+0x5e), same as
        // IsBattleMirrorCombatant. uid identifies the specific copy (vs cid = the type); stable while the card
        // lives in a spot, recycled after it leaves play.
        static int SlotUid(int player, int zone)
        {
            IntPtr ds = DuelState();
            if (ds == IntPtr.Zero) return 0;
            ushort pos = (ushort)Marshal.ReadInt16((IntPtr)(ds.ToInt64() + (long)(player & 1) * PlayerStride + (long)zone * 0x1c + 0x5e));
            return (pos & 1) + ((pos >> 8) * 2);
        }

        // Per-card field buff query, called from the DuelGetFieldCardVal hook. The ctx carries only the LIVE
        // instance values (race/attr/level/atk/def come straight from outVal -- already altered by any in-duel
        // effect, e.g. DNA Surgery), plus cid/uid/zone and the owner (player_id 0/1 + player_type
        // "player"/"cpu"). The static category (subtype/frame/kind/icon) is NOT here; scripts pull it with
        // card_props(c.cid). Then asks the "buff" hooks for the summed deltas.
        public static bool EvalFieldBuff(int cid, int race, int attr, int level, int atk, int def, int zone, int player, bool mine,
                                         out int datk, out int ddef, out int dlevel)
        {
            datk = 0; ddef = 0; dlevel = 0;
            if (!RoguelikeDuelHooks.HasBuff) return false;
            Table t = new Table(Engine());
            t["cid"] = cid; t["uid"] = SlotUid(player, zone);
            t["race"] = race; t["attr"] = attr; t["level"] = level;
            t["atk"] = atk; t["def"] = def; t["zone"] = zone;
            t["player_id"] = player; t["player_type"] = mine ? "player" : "cpu";
            return RoguelikeDuelHooks.EvalBuff(DynValue.NewTable(t), out datk, out ddef, out dlevel);
        }

        // Live-state table of an instance by uniqueId, read straight from the duel.dll
        // (DuelDll.CardBasicValByUid -> DLL_DuelGetCardBasicVal). Field cards (zone 0-6) come back with live
        // values (effects applied); off-field with printed ones. Returns { uid, cid, race, attr, level, atk,
        // def, player_id, player_type, location, zone? } or null if the instance is gone. Shared by
        // card_state and the summon event.
        static Table BuildCardState(int uid)
        {
            if (uid <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(64);
            try
            {
                int player, location, index;
                if (!DuelDll.CardBasicValByUid(uid, buf, out player, out location, out index)) return null;
                int cid = (ushort)Marshal.ReadInt16(buf, 0);
                if (cid == 0) return null;
                Table t = new Table(Engine());
                t["uid"] = uid; t["cid"] = cid;
                t["atk"] = Marshal.ReadInt32(buf, 4);
                t["def"] = Marshal.ReadInt32(buf, 8);
                t["race"] = (ushort)Marshal.ReadInt16(buf, 20);
                t["attr"] = (ushort)Marshal.ReadInt16(buf, 22);
                t["level"] = (ushort)Marshal.ReadInt16(buf, 26);
                t["player_id"] = player;
                t["player_type"] = (player & 1) == DuelDll.MyID ? "player" : "cpu";
                t["location"] = LocationName(location);
                if (location < 7) t["zone"] = location;
                return t;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // "summon" event, fired from the view-event bus (DuelDll RunEffect on RunSummon / RunSpSummon). The
        // card is already on the field, so resolving the uid gives the full live state including the
        // destination zone. ctx = card_state + kind ("normal" | "special"). Scripts pull the category with
        // card_props(e.cid).
        public static void FireSummon(int uid, string kind)
        {
            if (!RoguelikeDuelHooks.Has("summon")) return;
            Table t = BuildCardState(uid);
            if (t == null) return;
            t["kind"] = kind;
            RoguelikeDuelHooks.Fire("summon", DynValue.NewTable(t));
        }

        // "set" event, fired from the view bus (DuelDll RunEffect on CardSet, with the uid carried by the
        // preceding CardMove). The card is already on the field, so the ctx is the full card_state.
        public static void FireSet(int uid)
        {
            if (!RoguelikeDuelHooks.Has("set")) return;
            Table t = BuildCardState(uid);
            if (t == null) return;
            RoguelikeDuelHooks.Fire("set", DynValue.NewTable(t));
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
