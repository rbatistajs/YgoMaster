using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using IL2CPP;
using YgoMaster;

namespace YgoMasterClient
{
    // Generic card selection for relic scripts. Raises the engine's target selection for an arbitrary set of
    // candidate slots (any pile/location), then reads the player's pick and fires onConfirm. Minimal recipe (the
    // Monster Reborn select boils down to this): during active resolution, poke command-mode 8 + a predicate at
    // duelState 0x3d18 -- the engine renders the list AND tears it down itself on confirm (no prompt/list/dismiss
    // calls needed, so nothing stays stuck). The predicate is pure C# membership against a pre-computed candidate
    // set (it runs from native code mid-enumeration, so it must NOT re-enter Lua). The location is NOT hardcoded --
    // it comes from each candidate { player, location, index }.
    static class RoguelikeCardSelect
    {
        delegate uint Del_SelectPredicate(uint player, int location, int index);
        static Del_SelectPredicate _predicate;
        static IntPtr _predicatePtr;

        // FUN_180592bf0(cmdMode): validates the confirmed selection (the check Monster Reborn does at confirm).
        delegate int Del_ValidateSelectConfirm(int cmdMode);
        static Del_ValidateSelectConfirm ValidateSelectConfirm;

        // FUN_180534bc0(ctx, player, cardId, p4): the engine's effect target-builder -- looks up cardId's handler
        // and fills the shared list buffer (DAT_1811adc30) with its legal targets. We call it with Monster Reborn's
        // cid to get the engine's real "can be Special Summoned from a GY" set (see CanReviveFromGrave).
        delegate int Del_BuildSelectList(long ctx, long player, short cardId, int p4);
        static Del_BuildSelectList BuildSelectList;
        const int MonsterRebornCid = 4842;          // a generic GY-revive effect; its target set = revivable cards

        static bool _active;
        static bool _armed; static int _armPlayer;      // rgselnext: raise from the next Normal Summon
        static List<int[]> _cands;                      // candidate slots: { player, location, index }
        static Action<int, int, int> _onConfirm;        // called with the chosen (player, location, index)

        static IntPtr Ds()
        {
            if (DuelData.DuelStateSlot == IntPtr.Zero) return IntPtr.Zero;
            return Marshal.ReadIntPtr(DuelData.DuelStateSlot);
        }

        // Bind the validator. Called once from DuelDll's static ctor.
        public static void Init(IntPtr lib)
        {
            ValidateSelectConfirm = Utils.GetFunc<Del_ValidateSelectConfirm>(DuelSig.Resolve("ValidateSelectConfirm"));
            BuildSelectList = Utils.GetFunc<Del_BuildSelectList>(DuelSig.Resolve("BuildSelectList"));
        }

        // True if the card with this uid can be Special Summoned from a graveyard, per the engine's real legality:
        // run Monster Reborn's (cid 4842) own target builder and check whether the uid is in the legal set -- so it
        // respects properly-summoned, "cannot be Special Summoned", etc. MUST run during active resolution (a hook).
        // The build clears/refills the shared list buffer, but that's transient (the engine rebuilds it when a
        // selection is actually raised).
        public static bool CanReviveFromGrave(int uid)
        {
            if (BuildSelectList == null || DuelData.SelectBufSlot == IntPtr.Zero) return false;
            IntPtr buf = Marshal.ReadIntPtr(DuelData.SelectBufSlot);
            if (buf == IntPtr.Zero) return false;
            int n = BuildSelectList(0, DuelDll.MyID & 1, (short)MonsterRebornCid, 0);
            long bb = buf.ToInt64();
            for (int i = 0; i < n; i++)
            {
                int pos = (Marshal.ReadInt32((IntPtr)(bb + 0x18 + (long)i * 4)) >> 16) & 0xffff;
                if ((pos & 1) + ((pos >> 8) * 2) == uid) return true;
            }
            return false;
        }

        // rgselnext: arm the selection to be raised from the next Normal Summon's resolution (active loop).
        public static void ArmNext(int player) { _armPlayer = player & 1; _armed = true; Console.WriteLine("[rgsel] armed: next Normal Summon raises the selection"); }

        // Called from DuelDll on RunSummon: if armed, raise the GY select now (we're inside active resolution).
        public static void OnSummonResolved() { if (_armed) { _armed = false; QueueBeginSelect(_armPlayer); } }

        // Engine predicate, called from native code during enumeration -- pure C# (no Lua re-entry). Matches the
        // engine's own predicate (FUN_180484240): index < 1 is the per-location PROBE ("any target here?"), index
        // >= 1 is a specific slot mapping to candidate idx (index - 1). Generic over whatever locations the
        // candidates cover. Returns 0x1000 = selectable, 0 = not.
        static uint SelectPredicate(uint player, int location, int index)
        {
            try
            {
                List<int[]> cands = _cands;
                if (cands == null) return 0u;
                int p = (int)(player & 1);
                if (index < 1)                                                  // probe: any candidate here?
                {
                    for (int i = 0; i < cands.Count; i++)
                        if (cands[i][0] == p && cands[i][1] == location) return 0x1000u;
                    return 0u;
                }
                int slot = index - 1;

                for (int i = 0; i < cands.Count; i++)
                {
                    if (cands[i][0] == p && cands[i][1] == location && cands[i][2] == slot) return 0x1000u;
                }
                return 0u;
            }
            catch { return 0; }
        }

        // Raise a card selection on the duel thread. MUST run during active resolution (a hook) -- the engine only
        // renders the selection while resolving. candidates = { player, location, index } slots (already filtered).
        // onConfirm gets the chosen (player, location, index).
        public static void QueueSelect(int selector, List<int[]> candidates, Action<int, int, int> onConfirm)
        {
            if (_predicatePtr == IntPtr.Zero)
            {
                _predicate = SelectPredicate;   // keep the ref alive (GC) for the native pointer
                _predicatePtr = Marshal.GetFunctionPointerForDelegate(_predicate);
            }
            List<Action> q = DuelDll.ActionsToRunInNextSysAct;
            lock (q)
                q.Add(() =>
                {
                    IntPtr ds = Ds(); if (ds == IntPtr.Zero) return;
                    long b = ds.ToInt64();
                    _cands = candidates; _onConfirm = onConfirm;
                    Marshal.WriteInt32((IntPtr)(b + 0x3d00), selector & 1);          // player who decides
                    Marshal.WriteInt32((IntPtr)(b + 0x3cd0), 8);                     // command-mode 8 = selection
                    Marshal.WriteIntPtr((IntPtr)(b + 0x3d18), _predicatePtr);        // the predicate
                    Marshal.WriteInt32((IntPtr)(b + 0x3cec), 0);                     // no source card
                    Marshal.WriteInt32((IntPtr)(b + 0x3cd8), 0);
                    Marshal.WriteInt32((IntPtr)(b + 0x3ce4), 0);                     // pending reset
                    _active = true;
                });
        }

        // dev (rgsel/rgselnext): raise a "pick a card in your GY" selection and Special Summon the pick -- a full
        // standalone revive test, no Lua needed. (Fill the GY first, e.g. rgdbg 0 15 0 8 a few times.)
        public static void QueueBeginSelect(int player)
        {
            List<int[]> cands = new List<int[]>();
            IntPtr ds = Ds();
            if (ds != IntPtr.Zero)
            {
                int cnt = Marshal.ReadInt32((IntPtr)(ds.ToInt64() + (long)(player & 1) * 0xddc + 0x14));   // grave count
                for (int idx = 0; idx < cnt; idx++)
                    if ((ushort)Marshal.ReadInt16((IntPtr)(ds.ToInt64() + 0x7fc + ((long)(player & 1) * 0x377 + idx) * 4)) != 0)
                        cands.Add(new int[] { player & 1, 16, idx });   // location 16 = grave
            }
            if (cands.Count == 0) { Console.WriteLine("[rgsel] GY is empty -- fill it first (e.g. rgdbg " + (player & 1) + " 15 0 8 a few times)"); return; }
            QueueSelect(player, cands, (p, loc, idx) => DuelDll.QueueSpecialSummon(p, loc, idx, 1, 0, 0, player));
        }

        // Pumped from DuelDll.DuelSysAct: detect the confirm and fire onConfirm. 0x3d0c is 1-based (0 = no pick /
        // re-prompt; the real pile index is 0x3d0c - 1). The engine finalizes the selection on its own.
        public static void PollSelect()
        {
            if (!_active) return;
            IntPtr dsp = Ds(); if (dsp == IntPtr.Zero) return;
            long b = dsp.ToInt64();
            int cmd = Marshal.ReadInt32((IntPtr)(b + 0x3cf8));
            int pend = Marshal.ReadInt32((IntPtr)(b + 0x3ce4));
            if (pend == 0 || cmd != 0xc) return;                                     // not a confirm (cmd 12)
            if (ValidateSelectConfirm != null && ValidateSelectConfirm(Marshal.ReadInt32((IntPtr)(b + 0x3ce0))) == 0) return;
            int player = Marshal.ReadInt32((IntPtr)(b + 0x3d04));
            int location = Marshal.ReadInt32((IntPtr)(b + 0x3d08));
            int rawIndex = Marshal.ReadInt32((IntPtr)(b + 0x3d0c));
            // Index convention for the pick is location-aware: piles browsed via an "open the list" modal
            // (GY/deck/extra/banish) reserve 0x3d0c == 0 as the "open list" sentinel, so their picks are 1-based;
            // the hand (clicked directly) reports the raw 0-based slot (0 is a valid pick).
            bool oneBased = location != (int)CardPos.Hand;
            if (oneBased ? rawIndex <= 0 : rawIndex < 0) return;                      // skip non-picks
            int index = oneBased ? rawIndex - 1 : rawIndex;
            _active = false;
            Action<int, int, int> cb = _onConfirm; _onConfirm = null; _cands = null;
            try { if (cb != null) cb(player, location, index); }
            catch (Exception ex) { Console.WriteLine("[select] confirm EX: " + ex.Message); }
        }
    }
}
