using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Loaders;
using YgoMaster;   // DuelViewType

namespace YgoMasterClient
{
    // Engine card "position"/location codes: the location reported by CardBasicValByUid and the position arg
    // of DLL_DuelComDoDebugCommand / begin-SS. 0-12 are on-field zones (Master Duel master-rule board; Goat
    // only uses M1-M5/S1-S5/Field), 13-17 the off-field piles. Member names double as the Lua location names
    // (lowercased): m1..m5, emz1/emz2, s1..s5, field, hand, extra, deck, grave, banish.
    enum CardPos
    {
        M1 = 0, M2 = 1, M3 = 2, M4 = 3, M5 = 4,      // main monster zones
        EMZ1 = 5, EMZ2 = 6,                           // extra monster zones (unused in Goat)
        S1 = 7, S2 = 8, S3 = 9, S4 = 10, S5 = 11,    // spell/trap zones
        Field = 12,                                   // field spell zone
        Hand = 13, Extra = 14, Deck = 15, Grave = 16, Banish = 17,
    }
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
                    // +Coroutine: select_card yields the running hook (a coroutine) until the player picks.
                    s = new Script(CoreModules.Preset_HardSandbox | CoreModules.Coroutine);   // no io/os/require
                }
                finally
                {
                    try { Console.SetOut(prevOut); Console.SetError(prevErr); } catch { }
                }
                s.Options.ScriptLoader = new FileSystemScriptLoader();
                RegisterApi(s);
                // select_card is split: _select_begin (C#) builds the candidates + raises the selection, then the
                // wrapper yields the running hook coroutine until the confirm resumes it with the chosen card.
                // Lua-side coroutine.yield() is more reliable here than a CLR NewYieldReq.
                s.DoString("function select_card(opts) if _select_begin(opts) then return coroutine.yield() end return nil end");
                // Table helpers for scripts: merge (shallow map-merge of any number of tables, later keys win --
                // e.g. defaults + overrides) and spread (concatenate their list/array parts in order). Tolerant of
                // nil args via select('#').
                s.DoString(
                    "function merge(...) local r={} for i=1,select('#',...) do local t=select(i,...) if type(t)=='table' then for k,v in pairs(t) do r[k]=v end end end return r end " +
                    "function spread(...) local r={} for i=1,select('#',...) do local t=select(i,...) if type(t)=='table' then for _,v in ipairs(t) do r[#r+1]=v end end end return r end");
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

        // Expose a CLR enum to Lua as a table of named string constants (member name -> the same name string), so
        // scripts reference e.g. CardFrame.Magic / CardSimpleKind.Spell instead of the literal string card_props
        // returns for .frame/.kind/.icon/.simple_kind.
        static void RegisterEnumNames(Script s, string global, System.Type enumType)
        {
            Table t = new Table(s);
            foreach (string name in System.Enum.GetNames(enumType)) t[name] = name;
            s.Globals[global] = t;
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
            // Card enums as tables of named string constants (enum member name), matching what card_props returns
            // for .frame/.kind/.icon/.simple_kind -- so scripts compare without magic strings, e.g.
            // card_props(cid).simple_kind == CardSimpleKind.Spell.
            RegisterEnumNames(s, "CardFrame", typeof(CardFrame));
            RegisterEnumNames(s, "CardKind", typeof(CardKind));
            RegisterEnumNames(s, "CardIcon", typeof(CardIcon));
            RegisterEnumNames(s, "CardSimpleKind", typeof(CardSimpleKind));
            s.Globals["hand_count"] = (Func<int, int>)(p => ZoneCount(p, 0x0c));
            s.Globals["deck_count"] = (Func<int, int>)(p => ZoneCount(p, 0x10));
            s.Globals["grave_count"] = (Func<int, int>)(p => ZoneCount(p, 0x14));
            s.Globals["extra_count"] = (Func<int, int>)(p => ZoneCount(p, 0x18));
            s.Globals["banish_count"] = (Func<int, int>)(p => ZoneCount(p, 0x1c));
            s.Globals["deck_top"] = (Func<int, DynValue>)(p =>
            {
                int cid = DeckTop(p);
                return cid == 0 ? DynValue.Nil : DynValue.NewNumber(cid);
            });
            s.Globals["log"] = (Action<DynValue>)(v => Console.WriteLine("[lua] " + Stringify(v)));
            // on(name, fn): pin a callback to a hook. The params table active during the load is captured and
            // passed back as fn's 2nd arg on dispatch (see RoguelikeDuelHooks).
            s.Globals["on"] = (Action<string, DynValue>)((name, fn) => RoguelikeDuelHooks.Register(name, fn));
            // self-documenting: special_summon({ player_id = 0, location = "deck", index = 0 }). The card table is the
            // SOURCE (player_id = owner). Optional options (defaults match a plain face-up attack SS to your side, so
            // a passed-through card just works): face (1 face-up [default], 0 face-down), turn (0 attack [default],
            // 1 defense -- the atk/def rotation), reason (engine reason code, default 0), player_control (which side
            // controls the summoned card, default you -- set to the opponent to give them the card; lets you steal an
            // opponent's card to your field when its player_id is the opponent).
            s.Globals["special_summon"] = (Func<DynValue, bool>)(card =>
            {
                if (!CardSlot(card, out int player, out int loc, out int index, out int control)) { Console.WriteLine("[lua] special_summon: needs { player_id, location, index }"); return false; }
                Table t = card.Table;
                int face = OptInt(t, "face", 1);
                int turn = OptInt(t, "turn", 0);
                int reason = OptInt(t, "reason", 0);
                return DuelDll.QueueSpecialSummon(player, loc, index, face, turn, reason, control);
            });
            // allow_special_summon_from_grave(card): true if `card` (a GY card table) can be Special Summoned by a
            // revive, per the engine's real legality -- it runs Monster Reborn's target builder, so it respects
            // properly-summoned / "cannot be Special Summoned" / etc. Meant for a select_card filter.
            s.Globals["allow_special_summon_from_grave"] = (Func<DynValue, bool>)(cardv =>
            {
                if (cardv == null || cardv.Type != DataType.Table) return false;
                DynValue uidv = cardv.Table.Get("uid");
                return uidv.Type == DataType.Number && RoguelikeCardSelect.CanReviveFromGrave((int)uidv.Number);
            });
            s.Globals["to_hand"] = (Action<DynValue>)(card => CardDebugCmd(card, 6));   // -> hand
            s.Globals["to_grave"] = (Action<DynValue>)(card => CardDebugCmd(card, 8));   // -> graveyard
            s.Globals["banish"] = (Action<DynValue>)(card => CardDebugCmd(card, 9));   // banish face-up
            s.Globals["destroy"] = (Action<DynValue>)(card => CardDebugCmd(card, 11));  // destroy -> grave
            // debug_command{ player_id, location, index, cmd }: raw engine "swiss-army-knife"
            // (DLL_DuelComDoDebugCommand). Low-level escape hatch -- nicer per-cmd aliases (to_grave, draw, ...)
            // come later. Takes the same card table as the other actions plus cmd, so a chosen card passes
            // straight through; location accepts a name ("deck"/"grave"/...) OR a raw code (0-6 monster zones,
            // 13/15/16/17 piles) so field slots stay reachable. cmd: 8=->grave, 6=->hand/draw, 9/10=banish,
            // 11=destroy, 20=shuffle deck, 23/24=set ATK/DEF (value in index), ... (see
            // duel-action-primitives.md). Must be called from inside a hook (active resolution).
            s.Globals["debug_command"] = (Action<DynValue>)(arg =>
            {
                if (arg == null || arg.Type != DataType.Table) { Console.WriteLine("[lua] debug_command: needs { player_id, location, index, cmd }"); return; }
                Table t = arg.Table;
                DynValue p = t.Get("player_id"), l = t.Get("location"), i = t.Get("index"), c = t.Get("cmd");
                int player = p.Type == DataType.Number ? (int)p.Number : 0;
                int index = i.Type == DataType.Number ? (int)i.Number : 0;
                int cmd = c.Type == DataType.Number ? (int)c.Number : 0;
                int location = l.Type == DataType.Number ? (int)l.Number : (l.Type == DataType.String ? LocationCode(l.String) : -1);
                if (location < 0) { Console.WriteLine("[lua] debug_command: bad/missing location"); return; }
                DuelDll.QueueDebugCommand(player, location, index, cmd);
            });
            // run_effect(id, p1, p2, p3): raw view-event dispatch -- plays a DuelViewType cutin/animation without
            // touching the real effect (low-level escape hatch). id = a DuelViewType number OR its name as a string
            // (e.g. 0x48 / "CutinActivate", 0x23 / "CardHappen"; case-insensitive). Must be called from inside a
            // hook (active resolution).
            s.Globals["run_effect"] = (Action<DynValue, int, int, int>)((idv, p1, p2, p3) =>
            {
                int id;
                if (idv != null && idv.Type == DataType.Number) id = (int)idv.Number;
                else
                {
                    if (idv == null || idv.Type != DataType.String || !Enum.TryParse(idv.String, true, out DuelViewType vt))
                    { Console.WriteLine("[lua] run_effect: id must be a number or a DuelViewType name"); return; }
                    id = (int)vt;
                }
                DuelDll.QueueRunEffect(id, p1, p2, p3);
            });
            // effect_activate_zone(player, zone) / effect_activate_card(player, cid): play the "effect
            // activates" flash + a card highlight. ..._zone highlights the field monster at (player, zone);
            // ..._card pops the card art (cid) on the side (works even if it's not on the field). Both open the
            // activation cutin (CutinActivate) first, since the highlight (CardHappen) only renders inside it.
            s.Globals["effect_activate_zone"] = (Action<int, int>)((player, zone) =>
            {
                DuelDll.QueueRunEffect((int)DuelViewType.CutinActivate, player & 1, 0, 0);
                DuelDll.QueueRunEffect((int)DuelViewType.CardHappen, (player & 1) + zone * 2, 0, 0);
            });
            s.Globals["effect_activate_card"] = (Action<int, int>)((player, cid) =>
            {
                DuelDll.QueueRunEffect((int)DuelViewType.CutinActivate, player & 1, 0, 0);
                DuelDll.QueueRunEffect((int)DuelViewType.CardHappen, player & 1, cid, 0);
            });
            // activate_effect(player, category, zone, effId, uid [, ctx]): drive a REAL chain-link activation of
            // effId attributed to the card at uid -- full engine resolution (validation/cost/target/effect), no cid
            // change. category matches the effId's type (0 spell/trap, 2 pile, 3 monster); zone = that card's real
            // zone, as a number or a CardPos name (m1..s5/field 0-12, hand/extra/deck/grave/banish 13-17); uid from
            // field_uid()/card_state; ctx (a DynValue) defaults to 0. Runs during the player's priority.
            s.Globals["activate_effect"] = (Action<int, int, DynValue, int, int, DynValue>)((player, category, zonev, effId, uid, ctxv) =>
            {
                int zone;
                if (zonev != null && zonev.Type == DataType.Number) zone = (int)zonev.Number;
                else { CardPos cp; if (zonev == null || zonev.Type != DataType.String || !Enum.TryParse(zonev.String, true, out cp)) { Console.WriteLine("[lua] activate_effect: zone must be a number or a CardPos name (m1..s5, hand, grave, ...)"); return; } zone = (int)cp; }
                long ctx = ctxv != null && ctxv.Type == DataType.Number ? (long)ctxv.Number : 0;
                DuelDll.QueueActivateEffect(player, category, zone, effId, (uint)uid, ctx);
            });
            // _select_begin(opts): internal half of select_card (the yielding Lua wrapper is set up in Engine()).
            // Builds the filtered candidate set and raises the selection; returns true if one was raised (>=1
            // candidate), false if none -- the wrapper yields the running hook coroutine on true, or returns nil
            // on false (the gate that avoids a stuck picker with nothing valid). The Lua `filter` runs HERE during
            // the build, before the coroutine yields, never from the native predicate -- so nothing re-enters Lua
            // while the hook is suspended. opts = { from = "grave" (or a list), filter = function(card)->bool,
            // player = who picks (default you) }.
            s.Globals["_select_begin"] = (Func<DynValue, bool>)(optsv =>
            {
                Table opts = (optsv != null && optsv.Type == DataType.Table) ? optsv.Table : null;
                HashSet<int> fromSet = new HashSet<int>();
                DynValue filterFn = DynValue.Nil;
                int selector = DuelDll.MyID;
                if (opts != null)
                {
                    DynValue fromv = opts.Get("from");
                    if (fromv.Type == DataType.String) { int c = LocationCode(fromv.String); if (c >= 0) fromSet.Add(c); }
                    else if (fromv.Type == DataType.Table)
                        foreach (TablePair pair in fromv.Table.Pairs)
                            if (pair.Value.Type == DataType.String) { int c = LocationCode(pair.Value.String); if (c >= 0) fromSet.Add(c); }
                    filterFn = opts.Get("filter");
                    DynValue pv = opts.Get("player");
                    if (pv.Type == DataType.Number) selector = (int)pv.Number;
                }
                if (fromSet.Count == 0) fromSet.Add(16);   // default: grave
                DynValue capturedFilter = filterFn;
                Func<int, int, int, bool> filterWrap = (p, loc, idx) =>
                {
                    if (!fromSet.Contains(loc)) return false;
                    Table card = PileCard(p, loc, idx);
                    if (card == null) return false;
                    if (capturedFilter == null || capturedFilter.Type != DataType.Function) return true;
                    try { DynValue r = Engine().Call(capturedFilter, DynValue.NewTable(card)); return r != null && r.CastToBool(); }
                    catch (Exception ex) { Console.WriteLine("[select] filter EX: " + ex.Message); return false; }
                };
                // Collect every candidate (both players' piles in fromSet) that passes the filter. This list both
                // gates the raise (empty -> don't raise) AND drives the native list buffer that the modal renders
                // (RoguelikeCardSelect.QueueSelect). Each entry is { player, location, index }.
                List<int[]> candidates = new List<int[]>();
                foreach (int loc in fromSet)
                    for (int pl = 0; pl < 2; pl++)
                    {
                        int cnt = PileCount(pl, loc);
                        for (int idx = 0; idx < cnt; idx++)
                            if (filterWrap(pl, loc, idx))
                                candidates.Add(new int[] { pl, loc, idx });
                    }
                if (candidates.Count == 0) return false;
                RoguelikeCardSelect.QueueSelect(selector, candidates, OnSelectConfirmed);
                return true;
            });
        }

        // The hook coroutine, suspended on a select_card until the player confirms (null if none pending).
        static DynValue _pendingCoroutine;

        // Run a hook callback as a coroutine so it can yield on select_card (the wrapper does coroutine.yield()).
        // If it suspends, hold it for resume on confirm; if it ran to completion (no select_card), drop it.
        public static void CallCoroutine(DynValue fn, DynValue ctx, DynValue prm)
        {
            try
            {
                DynValue co = Engine().CreateCoroutine(fn);
                co.Coroutine.Resume(ctx ?? DynValue.Nil, prm ?? DynValue.Nil);
                _pendingCoroutine = co.Coroutine.State == CoroutineState.Suspended ? co : null;
            }
            catch (Exception ex) { Console.WriteLine("[hook] EX: " + ex.Message); }
        }

        // RoguelikeCardSelect's onConfirm: resume the suspended hook coroutine with the chosen card, so the Lua
        // select_card(...) returns it. If that resume hits another select_card it re-suspends -- keep holding it.
        public static void OnSelectConfirmed(int player, int location, int index)
        {
            DynValue co = _pendingCoroutine; _pendingCoroutine = null;
            if (co == null) return;
            Table card = PileCard(player, location, index);
            try
            {
                co.Coroutine.Resume(card == null ? DynValue.Nil : DynValue.NewTable(card));
                if (co.Coroutine.State == CoroutineState.Suspended) _pendingCoroutine = co;
            }
            catch (Exception ex) { Console.WriteLine("[select] resume EX: " + ex.Message); }
        }

        // card count of an off-field pile (by location code), from the per-player zone counters.
        static int PileCount(int player, int location)
        {
            switch (location)
            {
                case 13: return ZoneCount(player, 0x0c);   // hand
                case 14: return ZoneCount(player, 0x18);   // extra
                case 15: return ZoneCount(player, 0x10);   // deck
                case 16: return ZoneCount(player, 0x14);   // grave
                case 17: return ZoneCount(player, 0x1c);   // banish
                default: return 0;
            }
        }

        // entry-base (the [cid, state] start) per off-field pile, keyed by the location code.
        static long PileBase(int location)
        {
            switch (location)
            {
                case 13: return 0x1e4;   // hand
                case 14: return 0x5a4;   // extra
                case 15: return 0x3c4;   // deck
                case 16: return 0x7fc;   // grave
                case 17: return 0xa54;   // banish
                default: return -1;
            }
        }

        // Card table for a pile candidate (player, location code, index). null if empty/unsupported.
        static Table PileCard(int player, int location, int index)
        {
            long baseOff = PileBase(location);
            IntPtr ds = DuelState();
            if (baseOff < 0 || ds == IntPtr.Zero || index < 0) return null;
            long entry = ds.ToInt64() + baseOff + ((long)(player & 1) * 0x377 + index) * 4;
            int cid = (ushort)Marshal.ReadInt16((IntPtr)entry);
            if (cid == 0) return null;
            int state = (ushort)Marshal.ReadInt16((IntPtr)(entry + 2));
            Table t = new Table(Engine());
            t["cid"] = cid; t["uid"] = (state & 1) + ((state >> 8) * 2);
            t["player_id"] = player & 1;
            t["player_type"] = (player & 1) == DuelDll.MyID ? "player" : "cpu";
            t["location"] = LocationName(location);
            t["index"] = index;
            return t;
        }

        // Extract (player, location code, index) from a card table (select_card / card_state result).
        static bool CardSlot(DynValue cardv, out int player, out int location, out int index, out int control)
        {
            player = DuelDll.MyID; location = -1; index = -1; control = DuelDll.MyID;
            if (cardv == null || cardv.Type != DataType.Table) return false;
            Table t = cardv.Table;
            DynValue p = t.Get("player_id"), l = t.Get("location"), i = t.Get("index"), c = t.Get("player_control");
            if (p.Type == DataType.Number) player = (int)p.Number;
            if (l.Type == DataType.String) location = LocationCode(l.String);
            if (i.Type == DataType.Number) index = (int)i.Number;
            if (c.Type == DataType.Number) control = (int)c.Number;
            return location >= 0 && index >= 0;
        }

        static bool CardSlot(DynValue cardv, out int player, out int location, out int index)
        {
            return CardSlot(cardv, out player, out location, out index, out _);
        }

        // Readable string for log(): a table becomes { k=v, ... } (shallow -- nested tables show as table:ref);
        // everything else uses MoonSharp's ToPrintString.
        static string Stringify(DynValue v)
        {
            if (v == null) return "nil";
            if (v.Type != DataType.Table) return v.ToPrintString();
            string s = "{ "; bool first = true;
            foreach (TablePair p in v.Table.Pairs)
            {
                if (!first) s += ", ";
                first = false;
                s += p.Key.ToPrintString() + "=" + p.Value.ToPrintString();
            }
            return s + " }";
        }

        // Optional int field from a Lua table (default if missing / not a number).
        static int OptInt(Table t, string key, int def)
        {
            DynValue v = t.Get(key);
            return v != null && v.Type == DataType.Number ? (int)v.Number : def;
        }

        // Run a debug command on a card table's slot (to_hand/to_grave/banish/destroy helpers).
        static void CardDebugCmd(DynValue card, int cmd)
        {
            if (!CardSlot(card, out int player, out int location, out int index)) { Console.WriteLine("[lua] action: needs a card table"); return; }
            DuelDll.QueueDebugCommand(player, location, index, cmd);
        }

        // Location name -> engine code (inverse of LocationName), for the action/select API. Names are the
        // lowercased CardPos members (m1..m5, emz1/emz2, s1..s5, field, hand, extra, deck, grave, banish).
        // -1 if unknown.
        static int LocationCode(string name)
        {
            if (!string.IsNullOrEmpty(name) && Enum.TryParse(name, true, out CardPos pos) && Enum.IsDefined(typeof(CardPos), pos))
                return (int)pos;
            return -1;
        }

        // Lua table from static card props (cid/race/attr/level/atk/def + simple_kind/frame/kind/icon).
        static Table PropsTable(Script s, RoguelikeCardProps.Props p)
        {
            Table t = new Table(s);
            t["cid"] = p.Cid; t["race"] = p.Race; t["attr"] = p.Attr; t["level"] = p.Level;
            t["atk"] = p.Atk; t["def"] = p.Def;
            t["simple_kind"] = p.SimpleKind; t["frame"] = p.Frame; t["kind"] = p.Kind; t["icon"] = p.Icon;
            return t;
        }

        // Invoke a Lua callback (fn) with (ctx, params) on the shared engine. Used by RoguelikeDuelHooks.
        public static DynValue Call(DynValue fn, DynValue ctx, DynValue prm)
        {
            return Engine().Call(fn, new DynValue[] { ctx ?? DynValue.Nil, prm ?? DynValue.Nil });
        }

        // Location code (from DuelDll.CardBasicValByUid) to a script-friendly name (inverse of LocationCode):
        // the lowercased CardPos member -- m1..m5, emz1/emz2, s1..s5, field for on-field zones; hand/extra/
        // deck/grave/banish for the piles. Unknown codes fall back to "field".
        static string LocationName(int location)
        {
            return Enum.IsDefined(typeof(CardPos), location)
                ? ((CardPos)location).ToString().ToLowerInvariant()
                : "field";
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
        // "player"/"cpu"). The static category (type/frame/kind/icon) is NOT here; scripts pull it with
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
        // card_state and the lifecycle events (RoguelikeDuelEvents).
        public static Table BuildCardState(int uid)
        {
            if (uid <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(64);
            try
            {
                if (!DuelDll.CardBasicValByUid(uid, buf, out int player, out int location, out int index)) return null;
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
