using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using IL2CPP;
using YgoMaster;
using YgoMaster.Net;
using YgoMaster.Net.Message;

// NOTE: Be careful with threading here
// - If you want to run something on the duel thread use ActionsToRunInNextSysAct
// - If you want to run something in the game thread use TradeUtils.AddAction()

namespace YgoMasterClient
{


    unsafe static partial class DuelDll
    {
        public static List<byte> ReplayData = new List<byte>();

        public static List<Action> ActionsToRunInNextSysAct = new List<Action>();

        static DateTime LastSysActLogTime;
        static object LogLocker = new object();

        public static IntPtr CardPropMem;

        // Duel.dll image base, exposed so the Lua engine (RoguelikeLua) can read the duel state
        // (zone counters / deck top) without re-introducing RE plumbing here.
        public static IntPtr DuelLibBase { get { return _duelLibBase; } }

        public static DuelResultType SpecialResultType;
        public static DuelFinishType SpecialFinishType;
        public static int DuelEndResult;
        public static int DuelEndFinish;
        public static int DuelEndFinishCardID;
        public static bool HasNetworkError;
        public static bool HasDuelStart;
        public static bool HasDuelEnd;
        public static bool HasSysActFinished;
        public static DateTime BeginDuelTime;
        public static ulong RunEffectSeq;
        public static bool IsPvpDuel;
        public static bool IsPvpSpectator;
        public static PvpSpectatorRapidState PvpSpectatorRapidState;
        public static int SpectatorCount;
        public static int MyID;
        public static bool SendLiveRecordData;
        public static bool IsFieldGuideNear;
        public static bool IsFieldGuideNearReal;
        public static DateTime LastFieldGuideUpdate;
        public static bool IsInsideDuelTimerPrepareToDuel;
        public static bool IsTimerEnabled;
        public static DateTime LastCheckTimeOver;
        public static int AddTimeAtStartOfTurn;
        public static int AddTimeAtEndOfTurn;
        public static int RivalID
        {
            get { return MyID == 0 ? 1 : 0; }
        }

        static PvpEngineState pvpEngineState = new PvpEngineState();
        static Queue<PvpEngineState> pvpEngineStates = new Queue<PvpEngineState>();
        static Queue<PvpEngineState> freePvpEngineStates = new Queue<PvpEngineState>();

        static IntPtr engineInstance;
        static IntPtr engineInstanceReplayStream;
        static IL2Field fieldEngineGameMode;
        static IL2Field fieldEngineInstance;
        static IL2Field fieldEngineReplayStream;
        static IL2Method methodReplayStreamAdd;
        static IL2Method methodReplayStreamFinish;
        static IntPtr activePlayerFieldEffectInstance;
        static IL2Method methodSwitchGuide;

        delegate void Del_SetGuideEnable(IntPtr thisPtr, bool near, bool enable, bool turnchange);
        static Hook<Del_SetGuideEnable> hookSetGuideEnable;

        // Engine
        delegate int Del_RunEffect(int id, int param1, int param2, int param3);
        static Del_RunEffect myRunEffect = RunEffect;
        static Del_RunEffect originalRunEffect;

        delegate int Del_IsBusyEffect(int id);
        static Del_IsBusyEffect myIsBusyEffect = IsBusyEffect;
        static Del_IsBusyEffect originalIsBusyEffect;

        delegate void Del_DLL_SetEffectDelegate(IntPtr runEffect, IntPtr isBusyEffect);
        static Hook<Del_DLL_SetEffectDelegate> hookDLL_SetEffectDelegate;

        delegate void Del_DLL_DuelComMovePhase(int phase);
        static Hook<Del_DLL_DuelComMovePhase> hookDLL_DuelComMovePhase;

        delegate void Del_DLL_DuelComDoCommand(int player, int position, int index, int commandId);
        static Hook<Del_DLL_DuelComDoCommand> hookDLL_DuelComDoCommand;

        delegate int Del_DLL_DuelComCancelCommand();
        static Hook<Del_DLL_DuelComCancelCommand> hookDLL_DuelComCancelCommand;

        delegate int Del_DLL_DuelComCancelCommand2(bool decide);
        static Hook<Del_DLL_DuelComCancelCommand2> hookDLL_DuelComCancelCommand2;

        delegate void Del_DLL_DuelDlgSetResult(uint result);
        static Hook<Del_DLL_DuelDlgSetResult> hookDLL_DuelDlgSetResult;

        delegate void Del_DLL_DuelListSetCardExData(int index, int data);
        static Hook<Del_DLL_DuelListSetCardExData> hookDLL_DuelListSetCardExData;

        delegate void Del_DLL_DuelListSetIndex(int index);
        static Hook<Del_DLL_DuelListSetIndex> hookDLL_DuelListSetIndex;

        delegate void Del_DLL_DuelListInitString();
        static Hook<Del_DLL_DuelListInitString> hookDLL_DuelListInitString;

        public delegate void Del_DLL_DuelComCheatCard(int player, int position, int index, int cardId, int face, int turn);
        public static Del_DLL_DuelComCheatCard DLL_DuelComCheatCard;

        public delegate void Del_DLL_DuelComDoDebugCommand(int player, int position, int index, int commandId);
        public static Del_DLL_DuelComDoDebugCommand DLL_DuelComDoDebugCommand;

        public delegate void Del_DLL_DuelComDebugCommand();
        public static Del_DLL_DuelComDebugCommand DLL_DuelComDebugCommand;

        delegate int Del_DLL_DuelSysAct();
        static Hook<Del_DLL_DuelSysAct> hookDLL_DuelSysAct;

        // Internal duel.dll function that computes a field card's effective ATK/DEF (base + all
        // continuous effects). Display, AI and the damage step all read from it, so adding our
        // delta to the effective value here makes a buff show blue AND apply in battle.
        // outVal layout (short*): [0]=cid, +4=eff ATK, +8=eff DEF, +12=base ATK, +16=base DEF.
        delegate void Del_DuelGetFieldCardVal(uint player, int zone, IntPtr outVal, uint flags, uint param5);
        static Hook<Del_DuelGetFieldCardVal> hookDuelGetFieldCardVal;
        const long RVA_DuelGetFieldCardVal = 0xb0000;
        // FUN_1800b0000 recurses into itself (copy-stats / some card effects); buff only the outermost
        // call so our delta is not stacked once per nesting level.
        [ThreadStatic] static int _fieldCardValDepth;
        // duel.dll load base, kept to read DAT_1811adc50 (duel state) for the battle-mirror check.
        static IntPtr _duelLibBase;

        // Read a card's basic vals by uniqueId: FUN_18002b210 resolves uid -> encoded (player | location<<8 |
        // index<<16), or 0x1200 if the instance is gone; DLL_DuelGetCardBasicVal fills outVal (field cards
        // location 0-6 forward to FUN_1800b0000 = LIVE, off-field = printed). outVal layout matches the field
        // hook: cid+0, atk+4 (int), def+8 (int), race+20, attr+22, level+26. location codes: 0-12 field,
        // 13 hand, 14 extra, 15 deck, 16 grave, 17 banish. _inCardQuery suppresses our buff while the field
        // path re-enters FUN_1800b0000.
        delegate uint Del_ResolveUid(uint uniqueId);
        static Del_ResolveUid Func_ResolveUid;
        const long RVA_ResolveUid = 0x2b210;
        delegate void Del_DuelGetCardBasicVal(ulong player, int location, int index, IntPtr outVal);
        static Del_DuelGetCardBasicVal Func_DuelGetCardBasicVal;
        [ThreadStatic] static bool _inCardQuery;

        public static bool CardBasicValByUid(int uid, IntPtr outVal, out int player, out int location, out int index)
        {
            player = 0; location = 0; index = 0;
            if (Func_ResolveUid == null || Func_DuelGetCardBasicVal == null) return false;
            uint enc = Func_ResolveUid((uint)uid);
            if (enc == 0x1200) return false;   // instance gone
            player = (int)(enc & 0xff);
            location = (int)((enc >> 8) & 0xff);
            index = (int)(enc >> 0x10);
            _inCardQuery = true;
            try { Func_DuelGetCardBasicVal((ulong)player, location, index, outVal); }
            finally { _inCardQuery = false; }
            return true;
        }

        // Begin a special summon (FUN_180625c00, RVA 0x625c00): fills the desc (op=4) and starts the placement
        // state machine. Must run during active resolution (a hook context) -- the placement only pumps while
        // the duel loop is live; injected from idle it stalls. The zone prompt is answered by the UI (player)
        // / the AI (cpu). Signature: (player, cardRef*, face, turn, (cid<<16)|flagsLow, reason); cardRef points
        // at the 4-byte [cid, state] zone entry. face: 1=face-up, 0=face-down. turn: the atk/def rotation,
        // 0=attack, 1=defense. reason: engine reason code (pass-through).
        delegate void Del_Func625c00(ushort player, IntPtr cardRef, ushort face, ushort turn, uint param5, ushort reason);
        static Del_Func625c00 Func_625c00;
        const long RVA_Func625c00 = 0x625c00;

        // The raw chain-link push (FUN_180163050, RVA 0x163050): nearly every chain link (field effect, hand
        // spell/trap, trigger) is created here, so we hook it as the broad activation logger -- decoding the
        // packed card descriptor (low16 = effId, bit31 = player, bits16-20 = zone/index). mode 1 = new main
        // chain, 2 = sub-stack, 3 = added to an open chain.
        delegate uint Del_ChainPush(int mode, uint cardDesc, uint p3, uint p4);
        static Hook<Del_ChainPush> hookChainPush;
        const long RVA_ChainPush = 0x163050;

        // The activation entry (FUN_180163d60, RVA 0x163d60): receives the effId directly in param1's low16 -- the
        // internal "activate this effId" call (scripts use it with literal effIds, e.g. FUN_180596670). We hook it
        // to log a real activation's args when armed; QueueActivateEffect calls .Original to drive one ourselves.
        delegate ulong Del_ActivateEffect(uint param_1, uint param_2, long param_3);
        static Hook<Del_ActivateEffect> hookActivateEffect;
        const long RVA_ActivateEffect = 0x163d60;

        static bool _actLogArmed;
        public static void SetActLog(bool on)
        {
            _actLogArmed = on;
            Console.WriteLine("[rgactlog] " + (on ? "ON" : "off") + "  (push " + (hookChainPush != null ? "hooked" : "NULL") + ", activate " + (hookActivateEffect != null ? "hooked" : "NULL") + ")");
        }

        static uint ChainPushDetour(int mode, uint cardDesc, uint p3, uint p4)
        {
            if (_actLogArmed)
                try { Console.WriteLine("[rgchain] push mode=" + mode + " effId=" + (cardDesc & 0xffff) + " player=" + (cardDesc >> 31) + " idx=" + ((cardDesc >> 16) & 0x1f) + " desc=0x" + cardDesc.ToString("x") + " p3=0x" + p3.ToString("x") + " p4=0x" + p4.ToString("x")); } catch { }
            return hookChainPush.Original(mode, cardDesc, p3, p4);
        }
        static ulong ActivateEffectDetour(uint param_1, uint param_2, long param_3)
        {
            if (_actLogArmed)
                try { Console.WriteLine("[rgcast] activate param1=0x" + param_1.ToString("x") + " (effId=" + (param_1 & 0xffff) + ") uid=0x" + param_2.ToString("x") + " ctx=0x" + param_3.ToString("x")); } catch { }
            return hookActivateEffect.Original(param_1, param_2, param_3);
        }
        // Entry-base (the [cid, state] start) per off-field pile, for the cardRef.
        static long SpecialSummonSourceBase(int location)
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

        // Queue a special summon on the duel thread. The card is sourced from owner's pile (location codes per
        // SpecialSummonSourceBase; index 0 = top) but summoned to toPlayer's side -- so an opponent-owned card can
        // be Special Summoned to your field (like Monster Reborn reviving from either GY). face: 1=face-up,
        // 0=face-down. turn: the atk/def rotation, 0=attack, 1=defense. reason: engine reason code (pass-through).
        // Returns false if there's no card there / not bound.
        public static bool QueueSpecialSummon(int owner, int location, int index, int face, int turn, int reason, int toPlayer)
        {
            if (Func_625c00 == null || _duelLibBase == IntPtr.Zero) return false;
            long baseOff = SpecialSummonSourceBase(location);
            if (baseOff < 0 || index < 0) return false;
            IntPtr ds = Marshal.ReadIntPtr((IntPtr)(_duelLibBase.ToInt64() + 0x11adc50));
            if (ds == IntPtr.Zero) return false;
            long entryAddr = ds.ToInt64() + baseOff + ((long)(owner & 1) * 0x377 + index) * 4;   // source: owner's pile
            int cid = (ushort)Marshal.ReadInt16((IntPtr)entryAddr);
            if (cid == 0) return false;
            lock (ActionsToRunInNextSysAct)                                                       // dest controller: toPlayer
                ActionsToRunInNextSysAct.Add(() => Func_625c00((ushort)(toPlayer & 1), (IntPtr)entryAddr, (ushort)face, (ushort)turn, (uint)cid << 16, (ushort)reason));
            return true;
        }

        // Queue a raw debug command (DLL_DuelComDoDebugCommand, the "swiss-army-knife") on the duel thread.
        // cmd legend / location codes in duel-action-primitives.md (8=->grave, 6=->hand/draw, 9/10=banish,
        // 11=destroy, 20=shuffle deck, ...). Must run during active resolution (a hook context).
        public static void QueueDebugCommand(int player, int location, int index, int cmd)
        {
            if (DLL_DuelComDoDebugCommand == null) return;
            lock (ActionsToRunInNextSysAct)
                ActionsToRunInNextSysAct.Add(() => DLL_DuelComDoDebugCommand(player, location, index, cmd));
        }

        // Queue a view-event dispatch (the engine's runEffect, via originalRunEffect) on the duel thread --
        // plays a DuelViewType cutin/animation without touching the real effect. id = DuelViewType value;
        // params per the emit site (e.g. CutinActivate 0x48 = (player, cardTextId, 0)). See duelhooks.md.
        public static void QueueRunEffect(int id, int p1, int p2, int p3)
        {
            if (originalRunEffect == null) return;
            lock (ActionsToRunInNextSysAct)
                ActionsToRunInNextSysAct.Add(() => originalRunEffect(id, p1, p2, p3));
        }

        // dev (rgcast): activate an effId directly (FUN_180163d60) -- a real chain link with full resolution, no cid
        // change. category matches the effId's type (0 spell/trap, 2 pile, 3 monster); zone = the carrier card's real
        // zone (field 0-12) or pile location (13-17); uid = that card's uid (the source, independent of effId); ctx =
        // 0 for a fresh top-level activation. Builds param1 = (player<<31)|(category<<21)|(zone<<16)|effId. Must run on
        // the duel thread during the player's priority (the engine does validation / cost / target / resolution).
        public static void QueueActivateEffect(int player, int category, int zone, int effId, uint uid, long ctx, int effNum)
        {
            if (hookActivateEffect == null) return;
            uint param1 = BuildActivateParam(player, category, zone, effId, effNum);
            lock (ActionsToRunInNextSysAct)
                ActionsToRunInNextSysAct.Add(() => hookActivateEffect.Original(param1, uid, ctx));
        }

        // param1 packing for an activation: (player<<31) | (effNum<<25) | (category<<21) | (zone<<16) | effId(low16).
        // effNum (bits 25-30) picks which of the card's effects to activate (0 = the first/main one).
        public static uint BuildActivateParam(int player, int category, int zone, int effId, int effNum)
        {
            return ((uint)(player & 1) << 31) | (((uint)effNum & 0x3f) << 25) | (((uint)category & 7) << 21) | (((uint)zone & 0x1f) << 16) | ((uint)effId & 0xffff);
        }

        // dev (rgcmd): issue a raw player command (DLL_DuelComDoCommand) on the duel thread -- records (player,
        // position, index, cmd) into the duelState; the sysact loop processes it like a real input. Used to confirm
        // / pump an activation (cmd 12). Only valid when the engine is waiting for that command.
        public static void QueueDoCommand(int player, int position, int index, int cmd)
        {
            if (hookDLL_DuelComDoCommand == null) return;
            lock (ActionsToRunInNextSysAct)
                ActionsToRunInNextSysAct.Add(() => hookDLL_DuelComDoCommand.Original(player, position, index, cmd));
        }

        // dev: list an off-field pile's cards (index, cid, uid) for a location 13-17 (hand/extra/deck/grave/banish).
        // Each pile entry is 4 bytes [cid(low16), pos(high16)] at duelState + player*0xddc + base + (idx)*4; uid =
        // (pos & 1) + (pos >> 8) * 2 (same as the field). Use it to get the uid of a GY card to activate via rgcast.
        public static string ListPile(int player, int location)
        {
            long pbase, pcountOff;
            switch (location)
            {
                case 13: pbase = 0x1e4; pcountOff = 0x0c; break;   // hand
                case 14: pbase = 0x5a4; pcountOff = 0x18; break;   // extra
                case 15: pbase = 0x3c4; pcountOff = 0x10; break;   // deck
                case 16: pbase = 0x7fc; pcountOff = 0x14; break;   // grave
                case 17: pbase = 0xa54; pcountOff = 0x1c; break;   // banish
                default: return "(location must be 13-17)";
            }
            if (_duelLibBase == IntPtr.Zero) return "(no lib)";
            IntPtr ds = Marshal.ReadIntPtr((IntPtr)(_duelLibBase.ToInt64() + 0x11adc50));
            if (ds == IntPtr.Zero) return "(no duel)";
            long b = ds.ToInt64();
            int cnt = Marshal.ReadInt32((IntPtr)(b + (long)(player & 1) * 0xddc + pcountOff));
            var sb = new System.Text.StringBuilder();
            for (int idx = 0; idx < cnt; idx++)
            {
                int entry = Marshal.ReadInt32((IntPtr)(b + pbase + ((long)(player & 1) * 0x377 + idx) * 4));
                int cid = entry & 0xffff;
                if (cid == 0) continue;
                int pos = (entry >> 16) & 0xffff;
                sb.Append("[" + idx + "] cid=" + cid + " uid=" + ((pos & 1) + ((pos >> 8) * 2)) + "  ");
            }
            return sb.Length == 0 ? "(empty)" : sb.ToString();
        }

        // dev: the uid of the field card at (player, zone) -- the engine's own formula (DLL_DuelComDoCommand):
        // uid = (s & 1) + (s >> 8) * 2, where s is the slot state word at duelState + player*0xddc + zone*0x1c +
        // 0x5e. Also returns the slot cid (+0x5c). Returns -1 if unavailable. This is the param2 that FUN_180163d60
        // expects (= duelState 0x3cf4 in a real activation).
        public static int FieldUid(int player, int zone, out int cid)
        {
            cid = 0;
            if (_duelLibBase == IntPtr.Zero) return -1;
            IntPtr ds = Marshal.ReadIntPtr((IntPtr)(_duelLibBase.ToInt64() + 0x11adc50));
            if (ds == IntPtr.Zero) return -1;
            long slot = ds.ToInt64() + (long)(player & 1) * 0xddc + (long)zone * 0x1c;
            cid = (ushort)Marshal.ReadInt16((IntPtr)(slot + 0x5c));
            int s = (ushort)Marshal.ReadInt16((IntPtr)(slot + 0x5e));
            return (s & 1) + ((s >> 8) * 2);
        }

        // Public reads backed by the DLL proxies (DuelDll.ProxyFunctions; they handle PvP vs solo). Phase = a
        // DuelPhase value (0-5); step counters; zone availability for placement (locate = a zone code).
        public static int CurrentPhase() { return (int)DLL_DuelGetCurrentPhase(); }
        public static int CurrentStep() { return (int)DLL_DuelGetCurrentStep(); }
        public static int DamageStep() { return (int)DLL_DuelGetCurrentDmgStep(); }
        public static bool ZoneAvailable(int player, int zone) { return DLL_DuelIsThisZoneAvailable(player & 1, zone) != 0; }

        delegate void Del_AddRecord(IntPtr ptr, int size);
        delegate void Del_DLL_SetAddRecordDelegate(Del_AddRecord addRecord);
        static Del_DLL_SetAddRecordDelegate DLL_SetAddRecordDelegate;

        static DuelDll()
        {
            IL2Assembly assembly = Assembler.GetAssembly("Assembly-CSharp");

            IL2Class engineClassInfo = assembly.GetClass("Engine", "YgomGame.Duel");
            fieldEngineInstance = engineClassInfo.GetField("s_instance");
            fieldEngineReplayStream = engineClassInfo.GetField("replayStream");
            fieldEngineGameMode = engineClassInfo.GetField("gameMode");

            IL2Class replayStreamClassInfo = assembly.GetClass("ReplayStream", "YgomGame.Duel");
            methodReplayStreamAdd = replayStreamClassInfo.GetMethod("Add");
            methodReplayStreamFinish = replayStreamClassInfo.BaseType.GetMethod("Finish");

            IL2Class fieldEffectClassInfo = assembly.GetClass("ActivePlayerFieldEffect", "YgomGame.Duel");
            hookSetGuideEnable = new Hook<Del_SetGuideEnable>(SetGuideEnable, fieldEffectClassInfo.GetMethod("SetGuideEnable"));
            methodSwitchGuide = fieldEffectClassInfo.GetMethod("SwitchGuide");

            IntPtr lib = PInvoke.LoadLibrary(Path.Combine("masterduel_Data", "Plugins", "x86_64", "duel.dll"));
            if (lib == IntPtr.Zero)
            {
                throw new Exception("Failed to load duel.dll");
            }
            _duelLibBase = lib;

            InitProxyFunctions(lib);

            hookDLL_SetEffectDelegate = new Hook<Del_DLL_SetEffectDelegate>(DLL_SetEffectDelegate, PInvoke.GetProcAddress(lib, "DLL_SetEffectDelegate"));
            hookDLL_DuelSysAct = new Hook<Del_DLL_DuelSysAct>(DLL_DuelSysAct, PInvoke.GetProcAddress(lib, "DLL_DuelSysAct"));
            hookDuelGetFieldCardVal = new Hook<Del_DuelGetFieldCardVal>(DuelGetFieldCardVal, (IntPtr)(lib.ToInt64() + RVA_DuelGetFieldCardVal));
            hookChainPush = new Hook<Del_ChainPush>(ChainPushDetour, (IntPtr)(lib.ToInt64() + RVA_ChainPush));
            hookActivateEffect = new Hook<Del_ActivateEffect>(ActivateEffectDetour, (IntPtr)(lib.ToInt64() + RVA_ActivateEffect));

            hookDLL_DuelComMovePhase = new Hook<Del_DLL_DuelComMovePhase>(DLL_DuelComMovePhase, PInvoke.GetProcAddress(lib, "DLL_DuelComMovePhase"));
            hookDLL_DuelComDoCommand = new Hook<Del_DLL_DuelComDoCommand>(DLL_DuelComDoCommand, PInvoke.GetProcAddress(lib, "DLL_DuelComDoCommand"));
            hookDLL_DuelComCancelCommand = new Hook<Del_DLL_DuelComCancelCommand>(DLL_DuelComCancelCommand, PInvoke.GetProcAddress(lib, "DLL_DuelComCancelCommand"));
            hookDLL_DuelComCancelCommand2 = new Hook<Del_DLL_DuelComCancelCommand2>(DLL_DuelComCancelCommand2, PInvoke.GetProcAddress(lib, "DLL_DuelComCancelCommand2"));
            hookDLL_DuelDlgSetResult = new Hook<Del_DLL_DuelDlgSetResult>(DLL_DuelDlgSetResult, PInvoke.GetProcAddress(lib, "DLL_DuelDlgSetResult"));
            hookDLL_DuelListSetCardExData = new Hook<Del_DLL_DuelListSetCardExData>(DLL_DuelListSetCardExData, PInvoke.GetProcAddress(lib, "DLL_DuelListSetCardExData"));
            hookDLL_DuelListSetIndex = new Hook<Del_DLL_DuelListSetIndex>(DLL_DuelListSetIndex, PInvoke.GetProcAddress(lib, "DLL_DuelListSetIndex"));
            hookDLL_DuelListInitString = new Hook<Del_DLL_DuelListInitString>(DLL_DuelListInitString, PInvoke.GetProcAddress(lib, "DLL_DuelListInitString"));

            DLL_DuelComCheatCard = Utils.GetFunc<Del_DLL_DuelComCheatCard>(PInvoke.GetProcAddress(lib, "DLL_DuelComCheatCard"));
            DLL_DuelComDoDebugCommand = Utils.GetFunc<Del_DLL_DuelComDoDebugCommand>(PInvoke.GetProcAddress(lib, "DLL_DuelComDoDebugCommand"));
            DLL_DuelComDebugCommand = Utils.GetFunc<Del_DLL_DuelComDebugCommand>(PInvoke.GetProcAddress(lib, "DLL_DuelComDebugCommand"));

            DLL_SetAddRecordDelegate = Utils.GetFunc<Del_DLL_SetAddRecordDelegate>(PInvoke.GetProcAddress(lib, "DLL_SetAddRecordDelegate"));

            Func_ResolveUid = Utils.GetFunc<Del_ResolveUid>((IntPtr)(lib.ToInt64() + RVA_ResolveUid));
            Func_DuelGetCardBasicVal = Utils.GetFunc<Del_DuelGetCardBasicVal>(PInvoke.GetProcAddress(lib, "DLL_DuelGetCardBasicVal"));
            Func_625c00 = Utils.GetFunc<Del_Func625c00>((IntPtr)(lib.ToInt64() + RVA_Func625c00));
            RoguelikeCardSelect.Init(lib);
        }

        static void Log(string str)
        {
            if (ClientSettings.PvpLogToConsole)
            {
                Console.WriteLine(str);
            }
            LogToFile(str);
        }

        static void LogToFile(string str, bool append = true)
        {
            if (!ClientSettings.PvpLogToFile)
            {
                return;
            }
            lock (LogLocker)
            {
                try
                {
                    string fileName = Path.Combine(Program.ClientDataDir, "DuelLog.txt");
                    string fullLog = "[" + DateTime.Now.TimeOfDay + "] " + str + (append ? "\n" : string.Empty);
                    if (append)
                    {
                        File.AppendAllText(fileName, fullLog);
                    }
                    else
                    {
                        File.WriteAllText(fileName, fullLog);
                    }
                }
                catch
                {
                }
            }
        }

        public static void OnDuelRoomBattleReady()
        {
            lock (ActionsToRunInNextSysAct)
            {
                ActionsToRunInNextSysAct.Clear();
            }
        }

        public static void OnDuelBegin(GameMode gameMode)
        {
            LogToFile(string.Empty, false);
            ReplayData.Clear();
            RoguelikeChainEffect.Clear();
            RoguelikeLua.ClearDuelTable();
            SpecialResultType = DuelResultType.None;
            SpecialFinishType = DuelFinishType.None;
            DuelEndResult = 0;
            DuelEndFinish = 0;
            DuelEndFinishCardID = 0;
            HasNetworkError = false;
            HasDuelStart = false;
            HasDuelEnd = false;
            HasSysActFinished = false;
            BeginDuelTime = DateTime.UtcNow;
            RunEffectSeq = 0;
            IsPvpDuel = Program.NetClient != null && gameMode == GameMode.Room;
            IsPvpSpectator = Program.NetClient != null && gameMode == GameMode.Audience;
            SpectatorCount = 0;
            MyID = YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Duel.MyID");
            SendLiveRecordData = YgomSystem.Utility.ClientWork.GetByJsonPath<bool>("Duel.SendLiveRecordData");
            IsInsideDuelTimerPrepareToDuel = false;

            IsTimerEnabled = IsPvpDuel && YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Duel.TotalTimeMax") > 0;
            LastCheckTimeOver = DateTime.MinValue;
            if (IsPvpDuel)
            {
                AddTimeAtStartOfTurn = YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Duel.AddTimeAtStartOfTurn");
                AddTimeAtEndOfTurn = YgomSystem.Utility.ClientWork.GetByJsonPath<int>("Duel.AddTimeAtEndOfTurn");
            }
            else
            {
                AddTimeAtStartOfTurn = 0;
                AddTimeAtEndOfTurn = 0;
            }

            engineInstance = IntPtr.Zero;
            engineInstanceReplayStream = IntPtr.Zero;
            activePlayerFieldEffectInstance = IntPtr.Zero;

            PvpSpectatorRapidState = PvpSpectatorRapidState.None;
            if (IsPvpSpectator)
            {
                // NOTE: Although we use "rapid" it doesn't actually do what it does in-game by default which is why we do this speed-up
                if (YgomSystem.Utility.ClientWork.GetByJsonPath<bool>("Duel.rapid") &&
                    ClientSettings.DuelClientSpectatorRapidTimeMultiplayer > 0)
                {
                    PvpSpectatorRapidState = PvpSpectatorRapidState.WaitingForSysAct;
                }
                Program.NetClient.Send(new DuelSpectatorEnterMessage());
            }

            DuelTapSync.ClearState();
        }

        public static void OnDuelEnd()
        {
            PvpSpectatorRapidState = PvpSpectatorRapidState.None;
            engineInstance = IntPtr.Zero;
            engineInstanceReplayStream = IntPtr.Zero;
            activePlayerFieldEffectInstance = IntPtr.Zero;
            DuelTapSync.ClearState();
            DuelEmoteHelper.OnEndDuel();
        }

        static void ClearEngineState()
        {
            lock (pvpEngineState)
            {
                pvpEngineState.Clear();
                foreach (PvpEngineState state in pvpEngineStates)
                {
                    freePvpEngineStates.Enqueue(state);
                }
                foreach (PvpEngineState state in freePvpEngineStates)
                {
                    state.Clear();
                }
            }
        }

        public static void OnInitEngineStep()
        {
            // NOTE: DuelClient.InitEngineStep is also where ReplayStream is set up. Might be useful
            if (IsPvpDuel)
            {
                Log("OnInitEngineStep");
            }
            DLL_SetAddRecordDelegate(AddRecord);
        }

        static void DLL_SetEffectDelegate(IntPtr runEffect, IntPtr isBusyEffect)
        {
            originalRunEffect = Utils.GetFunc<Del_RunEffect>(runEffect);
            originalIsBusyEffect = Utils.GetFunc<Del_IsBusyEffect>(isBusyEffect);
            hookDLL_SetEffectDelegate.Original(Marshal.GetFunctionPointerForDelegate(myRunEffect), Marshal.GetFunctionPointerForDelegate(myIsBusyEffect));
        }

        static void DuelGetFieldCardVal(uint player, int zone, IntPtr outVal, uint flags, uint param5)
        {
            _fieldCardValDepth++;
            try
            {
                hookDuelGetFieldCardVal.Original(player, zone, outVal, flags, param5);
            }
            finally
            {
                _fieldCardValDepth--;
            }
            // Field stat hook (FUN_1800b0000). Outermost call only (depth 0); nested self-calls are the
            // engine's own sub-evaluations. zone 0-6 = monster zones (other locations return ATK 0). This is
            // the integration point for the (to-be-rebuilt) stat-buff: read cid/type/atk/def/level from
            // outVal and write deltas back, skipping the battle-snapshot mirror read (bit3 clear) via
            // IsBattleMirrorCombatant to avoid the attack-animation double.
            if (_fieldCardValDepth != 0 || _inCardQuery || outVal == IntPtr.Zero || 6 < zone || !RoguelikeDuelHooks.HasBuff)
            {
                return;
            }
            int cid = (ushort)Marshal.ReadInt16(outVal, 0);
            if (cid == 0) return;
            int race = Marshal.ReadInt16(outVal, 20);
            int attr = Marshal.ReadInt16(outVal, 22);
            int level = Marshal.ReadInt16(outVal, 26);
            int curAtk = Marshal.ReadInt32(outVal, 4);
            int curDef = Marshal.ReadInt32(outVal, 8);
            bool mine = (player & 1) == (uint)MyID;
            int atk, def, levelDelta;
            if (!RoguelikeLua.EvalFieldBuff(cid, race, attr, level, curAtk, curDef, zone, (int)(player & 1), mine, out atk, out def, out levelDelta))
            {
                return;
            }
            // Anti-double: bit3-clear reads served from the battle snapshot mirror already include the buff
            // (the mirror was built from a buffed compute-path call); apply only when this is NOT that read.
            bool mirrorRead = (flags & 8) == 0 && IsBattleMirrorCombatant(player, zone);
            if (!mirrorRead)
            {
                if (atk != 0) Marshal.WriteInt32(outVal, 4, Marshal.ReadInt32(outVal, 4) + atk);
                if (def != 0) Marshal.WriteInt32(outVal, 8, Marshal.ReadInt32(outVal, 8) + def);
                if (levelDelta != 0) Marshal.WriteInt16(outVal, 26, (short)(level + levelDelta));
            }
        }

        // True when this field card is a current battle combatant whose bit3-clear value is served from
        // the combat snapshot mirror (DAT_1811adc60) -- already built from a buffed compute-path call,
        // so re-adding our delta would double it. Mirrors the engine's own combatant match in
        // FUN_1800b0000 (slot+0x5e position id vs mirror entry +0x18, with the +0x1bf1 state check) so
        // bystander monsters still receive the live buff. duel.dll Ghidra base is 0x180000000, hence
        // DAT_1811adc50 -> RVA 0x11adc50 (duel state) and DAT_1811adc60 -> RVA 0x11adc60 (mirror).
        static bool IsBattleMirrorCombatant(uint player, int zone)
        {
            if (_duelLibBase == IntPtr.Zero) return false;
            long bas = _duelLibBase.ToInt64();
            IntPtr duelState = Marshal.ReadIntPtr((IntPtr)(bas + 0x11adc50));
            IntPtr mirror = Marshal.ReadIntPtr((IntPtr)(bas + 0x11adc60));
            if (duelState == IntPtr.Zero || mirror == IntPtr.Zero) return false;
            long ds = duelState.ToInt64();
            long mr = mirror.ToInt64();
            if ((Marshal.ReadByte((IntPtr)(ds + 0x1bb8)) & 0x10) == 0) return false;
            ushort pos = (ushort)Marshal.ReadInt16((IntPtr)(ds + (long)(player & 1) * 0xddc + (long)zone * 0x1c + 0x5e));
            int posId = (pos & 1) + ((pos >> 8) * 2);
            for (int idx = 0; idx < 2; idx++)
            {
                int entryId = (ushort)Marshal.ReadInt16((IntPtr)(mr + idx * 0x28 + 0x18));
                if (posId != entryId) continue;
                int stateByte = Marshal.ReadByte((IntPtr)(ds + 0x1bf1 + (long)(entryId & 0x1ff) * 8)) & 3;
                int entryByte17 = Marshal.ReadByte((IntPtr)(mr + idx * 0x28 + 0x17));
                if (stateByte == entryByte17) return true;
            }
            return false;
        }

        static Del_AddRecord AddRecord = (IntPtr ptr, int size) =>
        {
            for (int i = 0; i < size; i++)
            {
                ReplayData.Add(*(byte*)(ptr + i));
            }
            if (IsPvpDuel && SendLiveRecordData)
            {
                byte[] buffer = new byte[size];
                Marshal.Copy(ptr, buffer, 0, buffer.Length);
                Program.NetClient.Send(new DuelSpectatorDataMessage()
                {
                    Buffer = buffer
                });
            }
        };

        static void SetGuideEnable(IntPtr thisPtr, bool near, bool enable, bool turnchange)
        {
            if (IsPvpDuel)
            {
                //Log("SetGuideEnable:" + thisPtr + " near:" + near + " enable:" + enable + " turnchange:" + turnchange);
            }
            activePlayerFieldEffectInstance = thisPtr;
            LastFieldGuideUpdate = DateTime.UtcNow;
            bool nearEnable = (near && enable) || (!near && !enable);
            IsFieldGuideNearReal = nearEnable;
            if (IsPvpDuel && SendLiveRecordData && near && IsFieldGuideNear != nearEnable)
            {
                IsFieldGuideNear = nearEnable;
                Program.NetClient.Send(new DuelSpectatorFieldGuideMessage()
                {
                    Near = nearEnable
                });
            }
            hookSetGuideEnable.Original(thisPtr, near, enable, turnchange);
        }

        static IntPtr GetReplayStream()
        {
            if (engineInstance == IntPtr.Zero)
            {
                engineInstance = fieldEngineInstance.GetValue().ptr;
            }
            if (engineInstance != IntPtr.Zero && engineInstanceReplayStream == IntPtr.Zero)
            {
                engineInstanceReplayStream = fieldEngineReplayStream.GetValue(engineInstance).ptr;
            }
            return engineInstanceReplayStream;
        }

        static void UpdateFieldGuide()
        {
            LastFieldGuideUpdate = DateTime.UtcNow;
            if (activePlayerFieldEffectInstance != IntPtr.Zero)
            {
                int team = IsFieldGuideNear ? 0 : 1;
                bool forceswitch = true;
                methodSwitchGuide.Invoke(activePlayerFieldEffectInstance, new IntPtr[] { new IntPtr(&team), new IntPtr(&forceswitch) });
            }
        }

        static void EndSpectatorReplayStream()
        {
            IntPtr replayStream = GetReplayStream();
            if (replayStream != IntPtr.Zero)
            {
                methodReplayStreamFinish.Invoke(engineInstanceReplayStream);
            }
        }

        static void UpdateSpectatorCount(int num)
        {
            SpectatorCount = num;
            Action action = () =>
            {
                YgomGame.Duel.DuelHUD.OnChangeWatcherNum(SpectatorCount);
            };
            TradeUtils.AddAction(action);
        }

        static int InjectDuelEnd()
        {
            DuelTapSync.ClearState();
            DuelEmoteHelper.OnEndDuel();
            HasDuelEnd = true;
            if (IsPvpSpectator)
            {
                EndSpectatorReplayStream();
            }
            if (SpecialFinishType != DuelFinishType.None)
            {
                return originalRunEffect((int)DuelViewType.DuelEnd, (int)SpecialResultType, (int)SpecialFinishType, 0);
            }
            else if (HasNetworkError)
            {
                return originalRunEffect((int)DuelViewType.DuelEnd, (int)DuelResultType.Draw, (int)DuelFinishType.FinishError, 0);
            }
            return 0;
        }

        // dev: dump the managed view-event bus (rgeff). RunEffect is our effect delegate -- the DLL calls it
        // for every DuelViewType (RunSummon/RunSpSummon/BattleAttack/...) for BOTH players, in solo too. This
        // is the semantic event source we want for relic hooks. WaitFrame skipped (per-frame noise).
        public static bool LogEffects;

        // Last CardMove's uid (p1 & 0x1ff), refreshed on every CardMove. Several view events (CardSet, ...)
        // carry no uid of their own and pair with the CardMove right before them, so handlers read this.
        public static int LastCardMoveUid = -1;

        static int RunEffect(int id, int param1, int param2, int param3)
        {
            if (LogEffects && id != (int)DuelViewType.WaitFrame)
            {
                DuelViewType vt = (DuelViewType)id;
                // Different events carry the card's uniqueId in different params: Run/Cutin summon+set use p2;
                // Card* (move/set/vanish/break) use the low 9 bits of p1. Resolve it to show cid/loc/player.
                int uidCand = -1;
                if (vt == DuelViewType.RunSummon || vt == DuelViewType.RunSpSummon ||
                    vt == DuelViewType.CutinSummon || vt == DuelViewType.CutinSet)
                    uidCand = param2;
                else if (vt == DuelViewType.CardMove || vt == DuelViewType.CardSet ||
                         vt == DuelViewType.CardVanish || vt == DuelViewType.CardBreak)
                    uidCand = param1 & 0x1ff;
                string extra = "";
                if (uidCand > 0)
                {
                    IntPtr buf = Marshal.AllocHGlobal(64);
                    try
                    {
                        int pl, loc, ix;
                        if (CardBasicValByUid(uidCand, buf, out pl, out loc, out ix))
                            extra = " => uid=" + uidCand + " cid=" + (ushort)Marshal.ReadInt16(buf, 0) + " loc=" + loc + " player=" + pl;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                Console.WriteLine("[rgeff] " + vt + " p1=" + param1 + " p2=" + param2 + " p3=" + param3 + extra);
            }
            // Relic event hooks ride the view bus (semantic, catches both sides). RunSummon/RunSpSummon carry
            // the summoned card's uniqueId in param2; the card is already on the field at this point, so
            // FireSummon resolves the full live state (incl. destination zone).
            // Shared last-CardMove uid, refreshed before any per-hook dispatch so other events can reuse it.
            if (id == (int)DuelViewType.CardMove) LastCardMoveUid = param1 & 0x1ff;
            if (RoguelikeDuelEvents.Wants())
            {
                // Lifecycle (init/success/failed) for summon/special_summon/set. CardSet carries the uid in the
                // preceding CardMove (LastCardMoveUid); the others carry it in param2.
                try { RoguelikeDuelEvents.OnViewEvent(id, id == (int)DuelViewType.CardSet ? LastCardMoveUid : param2); }
                catch (Exception ex) { Console.WriteLine("[hook] lifecycle EX: " + ex.Message); }
            }
            // Turn-change event: param1 = the player whose turn is starting. Fires on("turn", player) for per-turn
            // state resets ("until end of turn" effects). Kept outside Wants() since it's its own hook name.
            if (id == (int)DuelViewType.TurnChange)
            {
                try { RoguelikeDuelEvents.OnTurn(param1); }
                catch (Exception ex) { Console.WriteLine("[hook] turn EX: " + ex.Message); }
            }
            // Phase-change event: param1 = the player, param2 = the new DuelPhase. Fires on("phase", { …, phase }).
            if (id == (int)DuelViewType.PhaseChange)
            {
                try { RoguelikeDuelEvents.OnPhase(param1, param2); }
                catch (Exception ex) { Console.WriteLine("[hook] phase EX: " + ex.Message); }
            }
            // dev (rgselnext): raise the card selection from inside a real summon's resolution (active loop).
            if (id == (int)DuelViewType.RunSummon) RoguelikeCardSelect.OnSummonResolved();
            // chain_effect: run the registered Lua callbacks at a forged blank chain's two phases -- cost at
            // CardHappen (param2 = effId), effect at ChainStep (param3 = effId). RoguelikeChainEffect matches the
            // effId to a pending blank-chain effect.
            if (RoguelikeChainEffect.Wants())
            {
                try
                {
                    if (id == (int)DuelViewType.CardHappen) RoguelikeChainEffect.OnCost(param2);
                    else if (id == (int)DuelViewType.ChainStep) RoguelikeChainEffect.OnEffect(param3);
                }
                catch (Exception ex) { Console.WriteLine("[chain_effect] EX: " + ex.Message); }
            }
            if (IsPvpDuel || IsPvpSpectator)
            {
                DuelEmoteHelper.OnRunEffect((DuelViewType)id, param1, param2, param3);
            }
            if (IsPvpSpectator)
            {
                if (HasNetworkError)
                {
                    if (HasDuelStart)
                    {
                        EndSpectatorReplayStream();
                        return InjectDuelEnd();
                    }
                    else
                    {
                        if ((DuelViewType)id == DuelViewType.DuelStart)
                        {
                            HasDuelStart = true;
                        }
                        originalRunEffect(id, param1, param2, param3);
                        EndSpectatorReplayStream();
                        return InjectDuelEnd();
                    }
                }

                switch ((DuelViewType)id)
                {
                    case DuelViewType.DuelStart:
                        UpdateSpectatorCount(SpectatorCount);
                        HasDuelStart = true;
                        break;
                    case DuelViewType.DuelEnd:
                        DuelTapSync.ClearState();
                        DuelEmoteHelper.OnEndDuel();
                        HasDuelEnd = true;
                        EndSpectatorReplayStream();
                        break;
                }
            }
            return originalRunEffect(id, param1, param2, param3);
        }

        static int IsBusyEffect(int id)
        {
            if (HasNetworkError || SpecialFinishType != DuelFinishType.None)
            {
                return originalIsBusyEffect(id);
            }
            return originalIsBusyEffect(id);
        }

        public static int DLL_DuelSysAct()
        {
            RoguelikeCardSelect.PollSelect();   // read a raised card selection once confirmed
            // Solo duels don't reach the ActionsToRunInNextSysAct drain (it lives inside the PvP block
            // below), so drain it here for non-PvP. Runs on the duel thread (this SysAct tick).
            if (!IsPvpDuel && !IsPvpSpectator && ActionsToRunInNextSysAct.Count > 0)
            {
                List<Action> soloActions;
                lock (ActionsToRunInNextSysAct)
                {
                    soloActions = new List<Action>(ActionsToRunInNextSysAct);
                    ActionsToRunInNextSysAct.Clear();
                }
                foreach (Action a in soloActions) { try { a(); } catch (Exception ex) { Console.WriteLine("[duel] solo action EX: " + ex.Message); } }
            }
            if (IsPvpDuel || IsPvpSpectator)
            {
                if (LastSysActLogTime < DateTime.UtcNow - TimeSpan.FromSeconds(3))
                {
                    LastSysActLogTime = DateTime.UtcNow;
                    LogToFile("DLL_DuelSysAct");
                }

                if (IsTimerEnabled && SpecialFinishType == DuelFinishType.None && LastCheckTimeOver < DateTime.UtcNow - TimeSpan.FromSeconds(1))
                {
                    LastCheckTimeOver = DateTime.UtcNow;
                    if (YgomGame.Duel.DuelTimer3D.IsPlayerTimeOver)
                    {
                        SpecialResultType = DuelResultType.Lose;
                        SpecialFinishType = DuelFinishType.TimeOut;
                        InjectDuelEnd();
                    }
                }

                if (IsPvpSpectator)
                {
                    if (PvpSpectatorRapidState == PvpSpectatorRapidState.WaitingForSysAct)
                    {
                        PvpSpectatorRapidState = PvpSpectatorRapidState.Active;
                        PInvoke.SetTimeMultiplier(ClientSettings.DuelClientSpectatorRapidTimeMultiplayer);
                    }
                    if (PvpSpectatorRapidState == PvpSpectatorRapidState.Active && YgomGame.Duel.DuelClient.ReplayRealtime)
                    {
                        PvpSpectatorRapidState = PvpSpectatorRapidState.Finished;
                        PInvoke.SetTimeMultiplier(ClientSettings.DuelClientTimeMultiplier != 0 ?
                            ClientSettings.DuelClientTimeMultiplier : ClientSettings.TimeMultiplier);
                    }

                    // Hacky fix for field guide being on the wrong side when spectating a duel
                    if (IsFieldGuideNearReal != IsFieldGuideNear && LastFieldGuideUpdate < DateTime.UtcNow - TimeSpan.FromSeconds(1))
                    {
                        UpdateFieldGuide();
                    }
                }

                if (IsPvpDuel)
                {
                    lock (pvpEngineStates)
                    {
                        if (HasSysActFinished && pvpEngineStates.Count == 0)
                        {
                            return 1;
                        }
                    }

                    PvpEngineState stateUpdate = null;
                    lock (pvpEngineState)
                    {
                        if (pvpEngineState.IsBusyEffect.Count > 0)
                        {
                            HashSet<DuelViewType> changedStates = null;
                            foreach (KeyValuePair<DuelViewType, int> busyState in pvpEngineState.IsBusyEffect)
                            {
                                if (originalIsBusyEffect((int)busyState.Key) == 0)
                                {
                                    if (changedStates == null)
                                    {
                                        changedStates = new HashSet<DuelViewType>();
                                    }
                                    //Log("Send DuelIsBusyEffectMessage " + busyState.Key + " " + pvpEngineState.RunEffectSeq);
                                    changedStates.Add(busyState.Key);
                                    Program.NetClient.Send(new DuelIsBusyEffectMessage()
                                    {
                                        RunEffectSeq = pvpEngineState.RunEffectSeq,
                                        ViewType = busyState.Key
                                    });
                                }
                            }
                            if (changedStates != null)
                            {
                                foreach (DuelViewType viewType in changedStates)
                                {
                                    pvpEngineState.IsBusyEffect.Remove(viewType);
                                }
                            }
                        }
                        else if (pvpEngineStates.Count > 0)
                        {
                            stateUpdate = pvpEngineStates.Dequeue();
                            if (stateUpdate != null)
                            {
                                pvpEngineState.Update(stateUpdate);
                                RunEffectSeq = pvpEngineState.RunEffectSeq;
                            }
                        }
                    }

                    if (stateUpdate != null)
                    {
                        switch (pvpEngineState.ViewType)
                        {
                            case DuelViewType.DuelStart:
                                UpdateSpectatorCount(SpectatorCount);
                                HasDuelStart = true;
                                goto default;
                            case DuelViewType.DuelEnd:
                                DuelTapSync.ClearState();
                                DuelEmoteHelper.OnEndDuel();
                                HasDuelEnd = true;
                                if (MyID != 0)
                                {
                                    switch ((DuelResultType)pvpEngineState.Param1)
                                    {
                                        case DuelResultType.Win:
                                            pvpEngineState.Param1 = (int)DuelResultType.Lose;
                                            break;
                                        case DuelResultType.Lose:
                                            pvpEngineState.Param1 = (int)DuelResultType.Win;
                                            break;
                                    }
                                }
                                // We do this because the DLL_XXXX functions don't seem to be working correctly
                                DuelEndResult = pvpEngineState.Param1;
                                DuelEndFinish = pvpEngineState.Param2;
                                DuelEndFinishCardID = pvpEngineState.Param3;
                                goto default;
                            case DuelViewType.TurnChange:
                                if (pvpEngineState.Param1 == MyID && AddTimeAtStartOfTurn > 0)
                                {
                                    TradeUtils.AddAction(() =>
                                    {
                                        YgomGame.Duel.DuelTimer3D.AddTurnTime(AddTimeAtStartOfTurn, AddTimeAtStartOfTurn + AddTimeAtEndOfTurn);
                                    });
                                }
                                else if (pvpEngineState.Param1 == RivalID && AddTimeAtEndOfTurn > 0)
                                {
                                    TradeUtils.AddAction(() =>
                                    {
                                        YgomGame.Duel.DuelTimer3D.AddTurnTime(AddTimeAtEndOfTurn, AddTimeAtStartOfTurn + AddTimeAtEndOfTurn);
                                    });
                                }
                                goto default;
                            case DuelViewType.WaitInput:
                                if (pvpEngineState.DoCommandUser == MyID)
                                {
                                    goto default;
                                }
                                else
                                {
                                    RunEffect((int)DuelViewType.CpuThinking, 0, 0, 0);
                                }
                                break;
                            case DuelViewType.RunDialog:
                                // 1 = YgomGame.Duel.Engine.DialogType.Info
                                if (pvpEngineState.RunDialogUser == MyID || pvpEngineState.Param1 == 1)
                                {
                                    goto default;
                                }
                                else
                                {
                                    RunEffect((int)DuelViewType.CpuThinking, 0, 0, 0);
                                }
                                break;
                            case DuelViewType.RunList:
                                if (pvpEngineState.Param1 == MyID)
                                {
                                    goto default;
                                }
                                else
                                {
                                    RunEffect((int)DuelViewType.CpuThinking, 0, 0, 0);
                                }
                                break;
                            default:
                                RunEffect((int)pvpEngineState.ViewType, pvpEngineState.Param1, pvpEngineState.Param2, pvpEngineState.Param3);
                                break;
                        }
                        stateUpdate.Clear();
                        lock (pvpEngineState)
                        {
                            freePvpEngineStates.Enqueue(stateUpdate);
                        }
                    }
                }

                if (ActionsToRunInNextSysAct.Count > 0)
                {
                    List<Action> actionsToRun;
                    lock (ActionsToRunInNextSysAct)
                    {
                        actionsToRun = new List<Action>(ActionsToRunInNextSysAct);
                        ActionsToRunInNextSysAct.Clear();
                    }
                    foreach (Action action in actionsToRun)
                    {
                        action();
                    }
                }

                if ((HasNetworkError || SpecialFinishType != DuelFinishType.None) && HasDuelStart)
                {
                    return 1;
                }

                if (IsPvpDuel)
                {
                    return 0;
                }
            }
            return hookDLL_DuelSysAct.Original();
        }

        static void DLL_DuelComMovePhase(int phase)
        {
            if (IsPvpDuel)
            {
                Log("DLL_DuelComMovePhase phase:" + phase + " seq:" + RunEffectSeq);
                Program.NetClient.Send(new DuelComMovePhaseMessage()
                {
                    RunEffectSeq = RunEffectSeq,
                    Phase = phase
                });
            }
            hookDLL_DuelComMovePhase.Original(phase);
        }

        public static void DLL_DuelComDoCommand(int player, int position, int index, int commandId)
        {
            if (_actLogArmed)
                try { Console.WriteLine("[rgcmd] DoCommand player=" + player + " pos=" + position + " index=" + index + " cmd=" + commandId); } catch { }
            if (IsPvpDuel)
            {
                Log("DLL_DuelComDoCommand player:" + player + " pos:" + position + " indx:" + index + " cmd:" + commandId + " seq:" + RunEffectSeq);
                Program.NetClient.Send(new DuelComDoCommandMessage()
                {
                    RunEffectSeq = RunEffectSeq,
                    Player = player,
                    Position = position,
                    Index = index,
                    CommandId = commandId
                });
            }
            hookDLL_DuelComDoCommand.Original(player, position, index, commandId);
        }

        static int DLL_DuelComCancelCommand()
        {
            if (IsPvpDuel)
            {
                Log("DLL_DuelComCancelCommand seq:" + RunEffectSeq);
                Program.NetClient.Send(new DuelComCancelCommandMessage()
                {
                    RunEffectSeq = RunEffectSeq
                });
            }
            return hookDLL_DuelComCancelCommand.Original();
        }

        static int DLL_DuelComCancelCommand2(bool decide)
        {
            if (IsPvpDuel)
            {
                Log("DLL_DuelComCancelCommand2 seq:" + RunEffectSeq);
                Program.NetClient.Send(new DuelComCancelCommand2Message()
                {
                    RunEffectSeq = RunEffectSeq,
                    Decide = decide
                });
            }
            return hookDLL_DuelComCancelCommand2.Original(decide);
        }

        static void DLL_DuelDlgSetResult(uint result)
        {
            if (IsPvpDuel)
            {
                Log("DLL_DuelDlgSetResult result:" + result + " seq:" + RunEffectSeq);
                Program.NetClient.Send(new DuelDlgSetResultMessage()
                {
                    RunEffectSeq = RunEffectSeq,
                    Result = result
                });
            }
            hookDLL_DuelDlgSetResult.Original(result);
        }

        static void DLL_DuelListSetCardExData(int index, int data)
        {
            if (IsPvpDuel)
            {
                Log("DLL_DuelListSetCardExData index:" + index + " data:" + data + " seq:" + RunEffectSeq);
                Program.NetClient.Send(new DuelListSetCardExDataMessage()
                {
                    RunEffectSeq = RunEffectSeq,
                    Index = index,
                    Data = data
                });
            }
            hookDLL_DuelListSetCardExData.Original(index, data);
        }

        static void DLL_DuelListSetIndex(int index)
        {
            if (IsPvpDuel)
            {
                Log("DLL_DuelListSetIndex index:" + index + "seq:" + RunEffectSeq);
                Program.NetClient.Send(new DuelListSetIndexMessage()
                {
                    RunEffectSeq = RunEffectSeq,
                    Index = index
                });
            }
            hookDLL_DuelListSetIndex.Original(index);
        }

        static void DLL_DuelListInitString()
        {
            if (IsPvpDuel)
            {
                Log("DLL_DuelListInitString seq:" + RunEffectSeq);
                Program.NetClient.Send(new DuelListInitStringMessage()
                {
                    RunEffectSeq = RunEffectSeq
                });
            }
            hookDLL_DuelListInitString.Original();
        }

        public static void HandleNetMessage(NetClient client, NetMessage message)
        {
            switch (message.Type)
            {
                case NetMessageType.ConnectionResponse: OnConnectionResponse((ConnectionResponseMessage)message); break;
                case NetMessageType.Ping: OnPing((PingMessage)message); break;
                case NetMessageType.DuelError: OnDuelError((DuelErrorMessage)message); break;
                case NetMessageType.OpponentDuelEnded: OnOpponentDuelEnded((OpponentDuelEndedMessage)message); break;
                case NetMessageType.DuelSpectatorData: OnDuelSpectatorData((DuelSpectatorDataMessage)message); break;
                case NetMessageType.DuelSpectatorFieldGuide: OnDuelSpectatorFieldGuide((DuelSpectatorFieldGuideMessage)message); break;
                case NetMessageType.DuelSpectatorCount: OnDuelSpectatorCount((DuelSpectatorCountMessage)message); break;
                case NetMessageType.DuelTapSync: DuelTapSync.OnDuelTapSync((DuelTapSyncMessage)message); break;
                case NetMessageType.DuelEmote: DuelEmoteHelper.OnDuelEmote((DuelEmoteMessage)message); break;
                case NetMessageType.DuelEngineState: OnDuelEngineState((DuelEngineStateMessage)message); break;
                case NetMessageType.DuelIsBusyEffect: OnDuelIsBusyEffect((DuelIsBusyEffectMessage)message); break;
                case NetMessageType.DuelSysActFinished: OnDuelSysActFinished((DuelSysActFinishedMessage)message); break;
            }
        }

        static void OnConnectionResponse(ConnectionResponseMessage message)
        {
            if (!message.Success)
            {
                Log("Session server failed to validate token '" + ClientSettings.MultiplayerToken + "'");
            }
        }

        static void OnNetworkError()
        {
            try
            {
                // try/catch as we aren't in the main thread
                if (HasNetworkError || SpecialFinishType != DuelFinishType.None || HasDuelEnd/* ||
                    (YgomGame.Duel.DuelClient.Step != DuelClientStep.ExecDuel && !IsPvpSpectator)*/)
                {
                    return;
                }
            }
            catch
            {
            }

            lock (ActionsToRunInNextSysAct)
            {
                ActionsToRunInNextSysAct.Add(() =>
                {
                    if (HasNetworkError || SpecialFinishType != DuelFinishType.None || HasDuelEnd/* ||
                        (YgomGame.Duel.DuelClient.Step != DuelClientStep.ExecDuel && !IsPvpSpectator)*/)
                    {
                        return;
                    }
                    HasNetworkError = true;
                    Log("OnNetworkError");
                    if (/*IsPvpSpectator && */!HasDuelStart)
                    {
                        // NOTE: This is really hacky and looks weird but the client can get stuck without this
                        originalRunEffect((int)DuelViewType.DuelStart, 0, 0, 0);
                        HasDuelStart = true;
                    }
                    if (HasDuelStart)
                    {
                        InjectDuelEnd();
                    }
                    if (IsPvpDuel)
                    {
                        Program.NetClient.Send(new DuelErrorMessage());
                    }
                });
            }
        }

        static void OnPing(PingMessage message)
        {
            // TODO: Send some info stating if we're in a duel
            Program.NetClient.Send(new PongMessage()
            {
                ServerToClientLatency = Utils.GetEpochTime() - Utils.GetEpochTime(message.RequestTime),
                ResponseTime = DateTime.UtcNow,
            });

            if (message.DuelingState != DuelRoomTableState.Dueling && IsPvpDuel &&
                !HasNetworkError && SpecialFinishType == DuelFinishType.None && !HasDuelEnd &&
                BeginDuelTime < DateTime.UtcNow - TimeSpan.FromSeconds(5) &&
                YgomGame.Duel.DuelClient.Instance != IntPtr.Zero)
            {
                OnNetworkError();
            }
        }

        static void OnDuelError(DuelErrorMessage message)
        {
            if (IsPvpDuel || IsPvpSpectator)
            {
                OnNetworkError();
            }
        }

        static void OnOpponentDuelEnded(OpponentDuelEndedMessage message)
        {
            if (!IsPvpDuel)
            {
                return;
            }
            Action action = () =>
            {
                if (message.Result == DuelResultType.Lose && SpecialFinishType == DuelFinishType.None)
                {
                    switch (message.Finish)
                    {
                        case DuelFinishType.TimeOut:
                        case DuelFinishType.Surrender:
                            SpecialResultType = DuelResultType.Win;
                            SpecialFinishType = message.Finish;
                            InjectDuelEnd();
                            break;
                    }
                }
                HasDuelEnd = true;
            };
            lock (ActionsToRunInNextSysAct)
            {
                ActionsToRunInNextSysAct.Add(action);
            }
        }

        static void OnDuelSpectatorData(DuelSpectatorDataMessage message)
        {
            Action action = () =>
            {
                if (message.Buffer != null && message.Buffer.Length > 0)
                {
                    if (IsPvpDuel)
                    {
                        if (message.IsFirstData)
                        {
                            ReplayData.Clear();
                        }
                        ReplayData.AddRange(message.Buffer);
                    }
                    else if (IsPvpSpectator)
                    {
                        IntPtr replayStream = GetReplayStream();
                        if (replayStream != IntPtr.Zero)
                        {
                            IL2Array<byte> buffer = new IL2Array<byte>(message.Buffer.Length, IL2SystemClass.Byte);
                            buffer.CopyFrom(message.Buffer);
                            methodReplayStreamAdd.Invoke(replayStream, new IntPtr[] { buffer.ptr });
                        }
                    }
                }
            };
            lock (ActionsToRunInNextSysAct)
            {
                ActionsToRunInNextSysAct.Add(action);
            }
        }

        static void OnDuelSpectatorFieldGuide(DuelSpectatorFieldGuideMessage message)
        {
            TradeUtils.AddAction(() =>
            {
                IsFieldGuideNear = message.Near;
                UpdateFieldGuide();
            });
        }

        static void OnDuelSpectatorCount(DuelSpectatorCountMessage message)
        {
            UpdateSpectatorCount(message.Count);
        }

        static void OnDuelEngineState(DuelEngineStateMessage message)
        {
            Log("OnDuelEngineState " + message.RunEffectSeq + " " + message.ViewType);
            if (message.RunEffectSeq == 1)
            {
                ClearEngineState();
            }
            PvpEngineState state;
            lock (pvpEngineState)
            {
                if (freePvpEngineStates.Count > 0)
                {
                    state = freePvpEngineStates.Dequeue();
                    state.Clear();
                }
                else
                {
                    state = new PvpEngineState();
                }
            }
            state.RunEffectSeq = message.RunEffectSeq;
            state.ViewType = message.ViewType;
            state.Param1 = message.Param1;
            state.Param2 = message.Param2;
            state.Param3 = message.Param3;
            state.DoCommandUser = message.DoCommandUser;
            state.RunDialogUser = message.RunDialogUser;
            state.Read(message.CompressedBuffer);
            lock (pvpEngineState)
            {
                pvpEngineStates.Enqueue(state);
            }
        }

        static void OnDuelIsBusyEffect(DuelIsBusyEffectMessage message)
        {
            lock (pvpEngineState)
            {
                PvpEngineState state;
                if (pvpEngineState.RunEffectSeq == message.RunEffectSeq)
                {
                    state = pvpEngineState;
                }
                else
                {
                    state = pvpEngineStates.FirstOrDefault(x => x.RunEffectSeq == message.RunEffectSeq);
                }
                if (state != null)
                {
                    Log("OnDuelIsBusyEffect " + message.RunEffectSeq + " " + message.ViewType);
                    state.IsBusyEffect[message.ViewType] = 0;
                }
                else
                {
                    Utils.LogWarning("Failed to find state for IsBusyEffect seq " + message.RunEffectSeq + " " + message.ViewType);
                }
            }
        }

        static void OnDuelSysActFinished(DuelSysActFinishedMessage message)
        {
            HasSysActFinished = true;
        }
    }

    enum PvpSpectatorRapidState
    {
        None,
        WaitingForSysAct,
        Active,
        Finished
    }
}