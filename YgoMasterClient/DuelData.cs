using System;
using System.Runtime.InteropServices;

namespace YgoMasterClient
{
    // Resolves the duel.dll's DATA globals (the duel-state pointer cluster) by reading the RIP-relative offset baked
    // into a stable export, instead of a hardcoded RVA -- so a game patch that relocates .data is followed
    // automatically. DuelSig does this for code (functions); this is its data counterpart.
    //
    // How: a simple getter export like DLL_DuelGetTurnNum begins with `mov rax,[rip+disp32]` that loads the global;
    // decoding disp32 yields the global's runtime address with no RVA assumed. Two independent exports anchor the
    // cluster (duelState via DLL_DuelGetTurnNum, info via DLL_DuelIsReplayMode); their known delta cross-checks the
    // layout, then the neighbours that lack a dedicated getter (selectBuf, mirror) are derived by the confirmed
    // contiguous offsets. On any mismatch we fall back to the baked RVAs (with a warning).
    //
    // The exposed values are SLOT addresses (lib+offset): each slot stores a pointer that the engine reallocates per
    // duel, so callers must Marshal.ReadIntPtr(slot) at use time -- only the slot address is stable, never the ptr.
    static class DuelData
    {
        // Baked RVAs (image base 0x180000000) -- the layout the anchors were taken from, and the fallback on a miss.
        const long Rva_DuelState = 0x11adc50;   // *slot = duel-state struct ptr (per-player stride 0xddc)
        const long Rva_Info      = 0x11adc48;   // *slot = info struct (+0 LP xor key, +8 duel mode/replay)
        const long Rva_SelectBuf = 0x11adc30;   // *slot = engine's target-list buffer (duelState - 0x20)
        const long Rva_Mirror    = 0x11adc60;   // *slot = battle combat snapshot mirror (duelState + 0x10)

        public static IntPtr DuelStateSlot { get; private set; }
        public static IntPtr InfoSlot      { get; private set; }
        public static IntPtr SelectBufSlot { get; private set; }
        public static IntPtr MirrorSlot    { get; private set; }

        static IntPtr _base;

        // Anchor duelState + info from their getters, cross-check the delta, derive the rest. Call once with the lib.
        public static void Init(IntPtr lib)
        {
            _base = lib;
            long b = lib.ToInt64();
            long ds  = AnchorRip(lib, "DLL_DuelGetTurnNum");     // mov rax,[rip] -> duelState slot
            long inf = AnchorRip(lib, "DLL_DuelIsReplayMode");   // mov rcx,[rip] -> info slot
            // Cross-check: the two independent anchors must sit at the known contiguous delta. If not, the cluster
            // changed shape -> trust nothing derived and fall back to baked RVAs entirely.
            bool ok = ds != 0 && inf != 0 && (ds - inf) == (Rva_DuelState - Rva_Info);
            if (ok)
            {
                DuelStateSlot = (IntPtr)ds;
                InfoSlot      = (IntPtr)inf;
                SelectBufSlot = (IntPtr)(ds - (Rva_DuelState - Rva_SelectBuf));   // ds - 0x20
                MirrorSlot    = (IntPtr)(ds + (Rva_Mirror - Rva_DuelState));      // ds + 0x10
                return;
            }
            Console.WriteLine("[dueldata] anchor cross-check failed (ds=0x" + ds.ToString("x") + " info=0x" +
                              inf.ToString("x") + ") -> RVA fallback");
            DuelStateSlot = (IntPtr)(b + Rva_DuelState);
            InfoSlot      = (IntPtr)(b + Rva_Info);
            SelectBufSlot = (IntPtr)(b + Rva_SelectBuf);
            MirrorSlot    = (IntPtr)(b + Rva_Mirror);
        }

        // Decode the first `48|4C 8B|8D <rip-modrm> disp32` in the export body and return the runtime address it
        // references (instr_end + disp32). Follows an incremental-link jmp thunk first. 0 if not found.
        static long AnchorRip(IntPtr lib, string export)
        {
            IntPtr fn = PInvoke.GetProcAddress(lib, export);
            if (fn == IntPtr.Zero) { Console.WriteLine("[dueldata] export '" + export + "' missing"); return 0; }
            long p = fn.ToInt64();
            if (Marshal.ReadByte((IntPtr)p) == 0xe9)                              // jmp rel32 thunk -> real body
                p = p + 5 + Marshal.ReadInt32((IntPtr)(p + 1));
            for (int i = 0; i < 0x40; i++)
            {
                byte b0 = Marshal.ReadByte((IntPtr)(p + i));
                if (b0 != 0x48 && b0 != 0x4c) continue;                           // REX.W (.R)
                byte op = Marshal.ReadByte((IntPtr)(p + i + 1));
                byte modrm = Marshal.ReadByte((IntPtr)(p + i + 2));
                if ((op == 0x8b || op == 0x8d) && (modrm & 0xc7) == 0x05)         // mov/lea reg,[rip+disp32]
                    return p + i + 7 + Marshal.ReadInt32((IntPtr)(p + i + 3));
            }
            Console.WriteLine("[dueldata] no rip-load in '" + export + "' -> RVA fallback");
            return 0;
        }

        // dev (rgdata): report where each slot resolved vs its baked RVA. ok = unmoved; MOVED = the anchor followed
        // a relocation (resilience working); fb = the RVA fallback (anchors failed -- investigate).
        public static void Verify()
        {
            if (_base == IntPtr.Zero) { Console.WriteLine("[rgdata] not initialized"); return; }
            long b = _base.ToInt64();
            Report("duelState", DuelStateSlot, b + Rva_DuelState);
            Report("info",      InfoSlot,      b + Rva_Info);
            Report("selectBuf", SelectBufSlot, b + Rva_SelectBuf);
            Report("mirror",    MirrorSlot,    b + Rva_Mirror);
        }

        static void Report(string name, IntPtr got, long expect)
        {
            long g = got.ToInt64();
            string tag = g == 0 ? "fb" : (g == expect ? "ok" : "MOVED");
            Console.WriteLine("[rgdata] " + name.PadRight(10) + " expect=0x" + expect.ToString("x") +
                              " found=0x" + g.ToString("x") + "  " + tag);
        }
    }
}
