using System;
using System.Runtime.InteropServices;

namespace YgoMasterClient
{
    // Resolves the duel.dll's INTERNAL (non-exported) functions by a byte signature instead of a hardcoded RVA, so
    // a game patch that just relocates them is found automatically. THIS is the one place to update when the dll
    // changes: if a function's bytes changed, re-dump its sig (the `rgsig` dev command prints where each one now
    // resolves) and replace the entry below. Exports (DLL_*) are found by name and need no maintenance; data globals
    // (duelState etc.) are still by RVA -- a separate, code-xref problem.
    //
    // Sig = the function's first bytes as hex; "??" = a wildcard byte (we wildcard the rel32 operand after E8/E9).
    // Rva = the address in the build the sigs were taken from -- a fallback (with a warning) if the scan ever misses.
    static class DuelSig
    {
        public class Fn { public string Name; public string Sig; public long Rva; }

        public static readonly Fn[] All =
        {
            new Fn { Name = "DuelGetFieldCardVal",   Rva = 0xb0000,  Sig = "40 55 53 56 41 54 41 55 41 56 41 57 48 8D AC 24 20 FF" },
            new Fn { Name = "ResolveUid",            Rva = 0x2b210,  Sig = "48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 4C 8B 15" },
            new Fn { Name = "BeginSpecialSummon",    Rva = 0x625c00, Sig = "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 54 41 55 41 56 41 57 48 83 EC 40 44 0F B7 52" },
            new Fn { Name = "ChainPush",             Rva = 0x163050, Sig = "40 55 56 57 41 57 48 8D 6C 24 C1 48" },
            new Fn { Name = "ActivateEffect",        Rva = 0x163d60, Sig = "40 53 55 41 54 41 55 41 57 48 83 EC 70 48" },
            new Fn { Name = "ValidateSelectConfirm", Rva = 0x592bf0, Sig = "48 89 5C 24 10 48 89 6C 24 18 56 48 83 EC 20 4C 8B 0D" },
            new Fn { Name = "BuildSelectList",       Rva = 0x534bc0, Sig = "48 89 5C 24 18 48 89 6C 24 20 41 56 48 83 EC 20 45 0F" },
            new Fn { Name = "GetSpellSpeed",         Rva = 0x38350,  Sig = "48 89 4C 24 08 53 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 48" },
        };

        static IntPtr _base;
        static byte[] _text;        // a managed copy of the module's .text section (scanned in-process, fast)
        static long _textBase;      // runtime address of _text[0]

        // Snapshot the loaded module's .text section (parsed from its PE headers in memory) so Resolve can scan it.
        public static void Init(IntPtr lib)
        {
            _base = lib; _text = null; _textBase = 0;
            try
            {
                long b = lib.ToInt64();
                int e = Marshal.ReadInt32((IntPtr)(b + 0x3c));                    // e_lfanew
                int ns = Marshal.ReadInt16((IntPtr)(b + e + 6)) & 0xffff;         // NumberOfSections
                int osz = Marshal.ReadInt16((IntPtr)(b + e + 20)) & 0xffff;       // SizeOfOptionalHeader
                long st = b + e + 24 + osz;                                       // section table
                for (int i = 0; i < ns; i++)
                {
                    long o = st + (long)i * 40;
                    if (Marshal.ReadByte((IntPtr)o) == (byte)'.' && Marshal.ReadByte((IntPtr)(o + 1)) == (byte)'t' &&
                        Marshal.ReadByte((IntPtr)(o + 2)) == (byte)'e' && Marshal.ReadByte((IntPtr)(o + 3)) == (byte)'x' &&
                        Marshal.ReadByte((IntPtr)(o + 4)) == (byte)'t' && Marshal.ReadByte((IntPtr)(o + 5)) == 0)
                    {
                        int vsize = Marshal.ReadInt32((IntPtr)(o + 8));
                        int vaddr = Marshal.ReadInt32((IntPtr)(o + 12));
                        _text = new byte[vsize];
                        Marshal.Copy((IntPtr)(b + vaddr), _text, 0, vsize);
                        _textBase = b + vaddr;
                        return;
                    }
                }
                Console.WriteLine("[duelsig] .text not found -- RVA fallbacks only");
            }
            catch (Exception ex) { _text = null; Console.WriteLine("[duelsig] init EX: " + ex.Message); }
        }

        // Address of the named function: scan .text for its sig; fall back to base+rva (with a warning) on a miss.
        public static IntPtr Resolve(string name)
        {
            Fn f = null;
            for (int i = 0; i < All.Length; i++) if (All[i].Name == name) { f = All[i]; break; }
            if (f == null) { Console.WriteLine("[duelsig] unknown '" + name + "'"); return IntPtr.Zero; }
            IntPtr hit = Scan(f.Sig);
            if (hit != IntPtr.Zero) return hit;
            Console.WriteLine("[duelsig] '" + name + "' sig MISS -> fallback RVA 0x" + f.Rva.ToString("x") + " (re-dump the sig if the dll changed)");
            return (IntPtr)(_base.ToInt64() + f.Rva);
        }

        // First .text address whose bytes match the pattern ("AA BB ?? .."), or Zero. "??" matches any byte.
        static IntPtr Scan(string pattern)
        {
            if (_text == null) return IntPtr.Zero;
            string[] toks = pattern.Split(' ');
            int n = toks.Length;
            byte[] pat = new byte[n];
            bool[] wild = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (toks[i] == "??") wild[i] = true;
                else pat[i] = Convert.ToByte(toks[i], 16);
            }
            int last = _text.Length - n;
            for (int o = 0; o <= last; o++)
            {
                bool ok = true;
                for (int k = 0; k < n; k++)
                {
                    if (wild[k]) continue;
                    if (_text[o + k] != pat[k]) { ok = false; break; }
                }
                if (ok) return (IntPtr)(_textBase + o);
            }
            return IntPtr.Zero;
        }

        // dev (rgsig): report where each sig resolves vs its baked RVA -- a quick "are the addresses still good?"
        // after a patch. ok = found at the expected RVA; MOVED = found elsewhere (sig still valid, just relocated);
        // MISS = sig no longer matches (that function changed -> needs a fresh sig).
        public static void Verify()
        {
            if (_base == IntPtr.Zero) { Console.WriteLine("[rgsig] not initialized"); return; }
            long b = _base.ToInt64();
            for (int i = 0; i < All.Length; i++)
            {
                Fn f = All[i];
                IntPtr hit = Scan(f.Sig);
                long rva = hit == IntPtr.Zero ? -1 : hit.ToInt64() - b;
                string tag = hit == IntPtr.Zero ? "MISS" : (rva == f.Rva ? "ok" : "MOVED");
                Console.WriteLine("[rgsig] " + f.Name.PadRight(22) + " expect=0x" + f.Rva.ToString("x") +
                                  " found=" + (hit == IntPtr.Zero ? "none" : "0x" + rva.ToString("x")) + "  " + tag);
            }
        }
    }
}
