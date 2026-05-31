using IL2CPP;
using System;
using System.Collections.Generic;
using UnityEngine;
using YgoMaster;

namespace YgoMasterClient
{
    // The run map, rendered on the DeckEdit/DeckSelect screen (deck already chosen). Does NOT
    // hook the VC itself (MinHook = one hook per method; RoguelikeRunScreen owns the shared
    // DeckSelectViewController2 hooks and delegates here). Builds an RgMap container in the
    // Viewport with one node per map node (a clone of the deck-box "Template" frame), placed by
    // row/col; reachable nodes are clickable -> Move.
    static unsafe class RoguelikeMapScreen
    {
        const string Ui = "DeckSelectUI(Clone).Root.Window";
        const string DeckGroup = Ui + ".MainArea.DeckArea.DeckGroup";
        const string ScrollView = DeckGroup + ".Scroll View2";
        const string DeckContent = ScrollView + ".Viewport.Content";
        const string HeaderArea = Ui + ".TitleSafeArea.HeaderButtonArea";
        const string HeaderGroup = HeaderArea + ".HeaderButtonGroup";      // kept visible; we reuse its texts
        const string HeaderName = HeaderGroup + ".NameText";               // title (left); also the boss-label TMP template
        const string HeaderLp = HeaderGroup + ".DeckNum.TextDeckNumValue"; // repurposed "0/999" slot -> run LP (right)
        const string HeaderGold = HeaderGroup + ".GoldNum.TextDeckNumValue"; // cloned DeckNum sibling -> run gold
        const string RgScrollViewPath = DeckGroup + ".RgScrollView";
        const string RgMapPath = RgScrollViewPath + ".RgViewport.RgMap";

        const float EdgeThickness = 6f;
        const float ColStep = 180f, RowStep = 180f; // 1:1 cell spacing

        // Run-screen VC root and its Window node, for sibling overlays (the node drawer).
        internal static IntPtr Root() { return _go; }
        internal static IntPtr WindowRoot() { return _go == IntPtr.Zero ? IntPtr.Zero : GameObject.FindGameObjectByPath(_go, Ui); }
        // The header TMP cloned per node label; reuse it as a ready-to-clone text template.
        internal static IntPtr LabelTemplate() { return _bossLabelSrc; }

        static IntPtr _rectType, _rectMask2DType;
        static IL2Property _anchorMin, _anchorMax, _pivot, _anchoredPos3D, _sizeDelta, _localEuler, _rectRect;
        static IL2Method _scrollByTargetPos;
        static float _scrollTargetY;
        static bool _hasScrollTarget;
        static bool _scrollPending;        // open: apply ScrollByTargetPos after a short delay
        static DateTime _scrollDueAt;
        static float _scrollContentHeight, _scrollCurY; // recompute target at fire time (vh is 0 at build)
        static IntPtr _selectionButtonType, _colorContainerType, _profileBindingType, _spriteType;
        static IL2Field _selBtnOnClick;
        static IL2Property _behaviourEnabled, _imageSprite;
        static readonly System.Collections.Generic.Dictionary<string, IntPtr> _iconCache = new System.Collections.Generic.Dictionary<string, IntPtr>();

        static readonly Col FillDisabled = new Col { r = 0.02f, g = 0.02f, b = 0.04f, a = 0.65f };
        static IL2Method _ueAddListener, _ueRemoveAll;
        static IntPtr _scrollRectType, _extScrollRectType, _imageType, _rawImageType;
        static IL2Property _scrollRectContent, _scrollViewport, _scrollHorizontal, _scrollVertical, _scrollSensitivity, _graphicColor, _rawImageTexture;
        static IntPtr _graphicType;
        static IL2Property _maskPadding, _maskSoftness;
        static IL2Field _extDragScrollEnabled;
        static IntPtr _tweenScaleType;
        static IL2Field _tsFrom, _tsTo, _tStyle, _tDuration, _tTarget;
        static IL2Method _tPlay;
        static IntPtr _tweenPositionType;
        static IL2Field _tpRtrans, _tpFrom, _tpTo;
        static IntPtr _go;       // the current map VC GameObject (for in-place refresh)
        static IL2Method _activeInHierarchy; // GameObject.activeInHierarchy getter (refresh gate)
        static IL2Method _objAlive;          // UnityEngine.Object.op_Implicit(Object)->bool (destroyed-object guard)
        static bool _refreshPending;         // refresh once the map is on-screen again (post-duel)
        static IntPtr _tmpType;  // ExtendedTextMeshProUGUI (LP label text)
        static IntPtr _bossLabelSrc; // NameText TMP cloned as the per-node (boss) name-label template
        static IntPtr _extScroll; // our RgScrollView's ExtendedScrollRect (identity for the Start hook)
        static IntPtr _markerSource; // cached PlayerIcon clone (Home is inactive once a run is open)
        static bool _ready;
        static IntPtr _goldLabelGo;  // cloned DeckNum sibling for run gold (spawned once)

        // Stat-change animation state (LP / gold). _snap.Valid==false means "first read since the
        // map opened" -> baseline only, no animation. Diff runs only while the map is on-screen, so
        // an LP change during a duel animates when the player returns (post-combat reuses this path).
        struct HudSnap { public bool Valid; public bool Active; public int Lp; public int Gold; }
        static HudSnap _snap;
        static readonly System.Collections.Generic.List<RoguelikeStatAnim.FloatingDelta> _floaters =
            new System.Collections.Generic.List<RoguelikeStatAnim.FloatingDelta>();
        static readonly System.Collections.Generic.List<RoguelikeStatAnim.HudCounter> _counters =
            new System.Collections.Generic.List<RoguelikeStatAnim.HudCounter>();
        // Wall-clock dt source — the IL2CPP wrappers here don't expose UnityEngine.Time.deltaTime.
        static System.Diagnostics.Stopwatch _animClock;
        // Game-over detection: latch the active->dead edge (combat loss or lethal stat), then fire
        // the defeat dialog once the LP->0 animation has drained. Re-armed when a new run goes active.
        static bool _runWasActive;
        static bool _pendingDefeat;

        // addCard animation: full cards (native BindingCardMaterial on a RawImage) fly from center to
        // the deck button, then fade. When all land, the action is acked (the cards were already
        // applied server-side). _bindingMethod = BindingCardMaterial.Binding(RawImage, cid, rareType).
        static IL2Method _bindingMethod;
        const float CardFlightDuration = 0.9f;     // flight phase (after the hold)
        const float CardHoldFrac = 0.6f;           // hold the card at center this long (fraction of duration) before moving
        const float CardStaggerFrac = 0.25f;       // extra hold per card in a multi-card burst
        const float CardW = 200f, CardH = 292f;    // ~card aspect (tune)
        class CardFlight { public IntPtr Go; public Vector3 StartPos, EndPos; public float T; public float Angle; }
        static readonly System.Collections.Generic.List<CardFlight> _cardFlights =
            new System.Collections.Generic.List<CardFlight>();
        static int _addCardAckToken = -1;   // ack this action token once all flights finish

        // ExtendedScrollRect.Start()/Initialize() resets dragScrollEnabled to false after our
        // OnCreatedView set, so re-assert it post-Start. Global hook (one per method); only our
        // instance is forced.
        delegate void Del_Start(IntPtr thisPtr);
        static Hook<Del_Start> _hookExtStart;

        internal struct Col { public float r, g, b, a; }
        // Binary-compatible with UnityEngine.Vector4 / Vector2Int (RectMask2D padding/softness).
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct Vec4 { public float x, y, z, w; public Vec4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; } }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct Vec2Int { public int x, y; public Vec2Int(int x, int y) { this.x = x; this.y = y; } }

        // Map-click dispatch: CreateUnityAction only bridges captureless delegates, so each
        // reachable node wires a fixed slot lambda; slot->nodeId is rebuilt per render.
        const int MapSlots = 64;
        static readonly int[] _slotNodeId = new int[MapSlots];
        static readonly Action[] _slotActions =
        {
            () => OnSlot(0),  () => OnSlot(1),  () => OnSlot(2),  () => OnSlot(3),
            () => OnSlot(4),  () => OnSlot(5),  () => OnSlot(6),  () => OnSlot(7),
            () => OnSlot(8),  () => OnSlot(9),  () => OnSlot(10), () => OnSlot(11),
            () => OnSlot(12), () => OnSlot(13), () => OnSlot(14), () => OnSlot(15),
            () => OnSlot(16), () => OnSlot(17), () => OnSlot(18), () => OnSlot(19),
            () => OnSlot(20), () => OnSlot(21), () => OnSlot(22), () => OnSlot(23),
            () => OnSlot(24), () => OnSlot(25), () => OnSlot(26), () => OnSlot(27),
            () => OnSlot(28), () => OnSlot(29), () => OnSlot(30), () => OnSlot(31),
            () => OnSlot(32), () => OnSlot(33), () => OnSlot(34), () => OnSlot(35),
            () => OnSlot(36), () => OnSlot(37), () => OnSlot(38), () => OnSlot(39),
            () => OnSlot(40), () => OnSlot(41), () => OnSlot(42), () => OnSlot(43),
            () => OnSlot(44), () => OnSlot(45), () => OnSlot(46), () => OnSlot(47),
            () => OnSlot(48), () => OnSlot(49), () => OnSlot(50), () => OnSlot(51),
            () => OnSlot(52), () => OnSlot(53), () => OnSlot(54), () => OnSlot(55),
            () => OnSlot(56), () => OnSlot(57), () => OnSlot(58), () => OnSlot(59),
            () => OnSlot(60), () => OnSlot(61), () => OnSlot(62), () => OnSlot(63),
        };

        static RoguelikeMapScreen()
        {
            try
            {
                IL2Assembly asm = Assembler.GetAssembly("Assembly-CSharp");
                IL2Assembly core = Assembler.GetAssembly("UnityEngine.CoreModule");
                IL2Assembly ui = Assembler.GetAssembly("UnityEngine.UI");
                IL2Class rect = core.GetClass("RectTransform", "UnityEngine");
                _rectType = rect.IL2Typeof();
                _anchorMin = rect.GetProperty("anchorMin");
                _anchorMax = rect.GetProperty("anchorMax");
                _pivot = rect.GetProperty("pivot");
                _anchoredPos3D = rect.GetProperty("anchoredPosition3D");
                _sizeDelta = rect.GetProperty("sizeDelta");
                _rectRect = rect.GetProperty("rect");
                _localEuler = core.GetClass("Transform", "UnityEngine").GetProperty("localEulerAngles");
                IL2Class selBtn = asm.GetClass("SelectionButton", "YgomSystem.UI");
                _selectionButtonType = selBtn.IL2Typeof();
                _selBtnOnClick = selBtn.GetField("onClick");
                _colorContainerType = asm.GetClass("ColorContainerGraphic", "YgomSystem.UI").IL2Typeof();
                _profileBindingType = asm.GetClass("BindingProfileFrameIcon", "YgomGame.Menu.Common").IL2Typeof();
                _spriteType = core.GetClass("Sprite", "UnityEngine").IL2Typeof();
                _imageSprite = ui.GetClass("Image", "UnityEngine.UI").GetProperty("sprite");
                _ueAddListener = core.GetClass("UnityEvent", "UnityEngine.Events").GetMethod("AddListener");
                _ueRemoveAll = core.GetClass("UnityEventBase", "UnityEngine.Events").GetMethod("RemoveAllListeners");
                _behaviourEnabled = core.GetClass("Behaviour", "UnityEngine").GetProperty("enabled");
                IL2Class scrollRect = ui.GetClass("ScrollRect", "UnityEngine.UI");
                _scrollRectType = scrollRect.IL2Typeof();
                _scrollRectContent = scrollRect.GetProperty("content");
                _scrollViewport = scrollRect.GetProperty("viewport");
                _scrollHorizontal = scrollRect.GetProperty("horizontal");
                _scrollVertical = scrollRect.GetProperty("vertical");
                _scrollSensitivity = scrollRect.GetProperty("scrollSensitivity");
                IL2Class extScroll = asm.GetClass("ExtendedScrollRect", "YgomSystem.UI");
                _extScrollRectType = extScroll.IL2Typeof();
                _extDragScrollEnabled = extScroll.GetField("dragScrollEnabled");
                _scrollByTargetPos = extScroll.GetMethod("ScrollByTargetPos");
                _hookExtStart = new Hook<Del_Start>(OnExtScrollStart, extScroll.GetMethod("Start"));
                IL2Class tweenScale = asm.GetClass("TweenScale", "YgomSystem.UI");
                _tweenScaleType = tweenScale.IL2Typeof();
                _tsFrom = tweenScale.GetField("from");
                _tsTo = tweenScale.GetField("to");
                IL2Class tween = asm.GetClass("Tween", "YgomSystem.UI");
                _tStyle = tween.GetField("style");
                _tDuration = tween.GetField("duration");
                _tTarget = tween.GetField("target");
                _tPlay = tween.GetMethod("Play");
                IL2Class tweenPos = asm.GetClass("TweenPosition", "YgomSystem.UI");
                _tweenPositionType = tweenPos.IL2Typeof();
                _tpRtrans = tweenPos.GetField("rtrans");
                _tpFrom = tweenPos.GetField("from");
                _tpTo = tweenPos.GetField("to");
                IL2Class rectMask2D = ui.GetClass("RectMask2D", "UnityEngine.UI");
                _rectMask2DType = rectMask2D.IL2Typeof();
                _maskPadding = rectMask2D.GetProperty("padding");
                _maskSoftness = rectMask2D.GetProperty("softness");
                _imageType = ui.GetClass("Image", "UnityEngine.UI").IL2Typeof();
                IL2Class rawImg = ui.GetClass("RawImage", "UnityEngine.UI");
                _rawImageType = rawImg.IL2Typeof();
                _rawImageTexture = rawImg.GetProperty("texture");
                IL2Class graphic = ui.GetClass("Graphic", "UnityEngine.UI");
                _graphicType = graphic.IL2Typeof();
                _graphicColor = graphic.GetProperty("color");
                _activeInHierarchy = core.GetClass("GameObject", "UnityEngine").GetProperty("activeInHierarchy").GetGetMethod();
                _objAlive = core.GetClass("Object", "UnityEngine").GetMethod("op_Implicit", x => x.GetParameters().Length == 1);
                _tmpType = CastUtils.IL2Typeof("ExtendedTextMeshProUGUI", "YgomSystem.YGomTMPro", "Assembly-CSharp");
                IL2Class bcm = Assembler.GetAssembly("Assembly-CSharp").GetClass("BindingCardMaterial", "YgomGame.Menu.Common");
                _bindingMethod = bcm != null ? bcm.GetMethod("Binding") : null;
                if (_bindingMethod == null) Console.WriteLine("[Roguelike] BindingCardMaterial.Binding not found (addCard art disabled)");
                _ready = true;
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] mapscreen init EX: " + ex); }
        }

        // Clone the Home PlayerIcon while Home is still active (it gets deactivated once a run
        // screen is open, so GameObject.Find can't reach it at map-build time). Call from the home
        // context (e.g. when start_run completes). Idempotent; the clone persists for the session.
        public static void CaptureMarkerSource()
        {
            if (!_ready) return;
            try
            {
                IntPtr home = GameObject.Find("Home"); // only succeeds on the home screen
                IntPtr src = home != IntPtr.Zero ? GameObject.FindGameObjectByName(home, "PlayerIcon") : IntPtr.Zero;
                if (src == IntPtr.Zero) return; // not on home -> keep whatever we cached
                if (_markerSource != IntPtr.Zero) UnityObject.Destroy(_markerSource); // refresh with the latest
                IntPtr clone = UnityObject.Instantiate(src);
                UnityObject.SetName(clone, "RgMarkerSource");
                // Freeze the binding so it keeps Home's framed (hexagon) icon. On the run screen the
                // profile/frame data isn't in ClientWork, so a re-bind would fall back to the plain
                // square icon. Disabling it (while the cloned sprite is the captured hexagon) pins it.
                FreezeProfileBinding(clone);
                UnityObject.DontDestroyOnLoad(clone);
                GameObject.SetActive(clone, false);
                _markerSource = clone;
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] CaptureMarkerSource EX: " + ex); }
        }

        // Called by the host (RoguelikeRunScreen) once the DeckSelect view is created in map mode.
        public static void Build(IntPtr go)
        {
            if (!_ready) return;
            _go = go;
            try { SetupMap(go); }
            catch (Exception ex) { Console.WriteLine("[Roguelike] map build EX: " + ex); }
            // On open the layout isn't settled yet, so ScrollByTargetPos no-ops on the first frames.
            // Defer it (driven from Update) so it lands on the player.
            _scrollDueAt = DateTime.UtcNow.AddMilliseconds(300);
            _scrollPending = true;
        }

        // Request a re-render the next time the map is actually on screen. Used after a duel: the
        // duel_result completes while the map VC is deactivated (the duel covers it), so an
        // immediate Refresh would rebuild stale/invisible nodes. Defer until it re-appears.
        public static void MarkDirty() { _refreshPending = true; }

        // A destroyed Unity object keeps a non-null IL2CPP wrapper but a dead native ptr, so a raw
        // "== Zero" check passes and activeInHierarchy then NREs. op_Implicit is the Unity-safe
        // aliveness test (reads the cached native ptr without dereferencing it).
        static bool IsAlive(IntPtr go)
        {
            if (go == IntPtr.Zero) return false;
            if (_objAlive == null) return true; // can't test -> assume alive (pre-fix behavior)
            IL2Object a = _objAlive.Invoke(new IntPtr[] { go });
            return a != null && a.GetValueRef<csbool>();
        }

        static bool IsActive(IntPtr go)
        {
            if (!IsAlive(go) || _activeInHierarchy == null) return false;
            IL2Object r = _activeInHierarchy.Invoke(go);
            return r != null && r.GetValueRef<csbool>();
        }

        // Driven per-frame from TradeUtils (NetworkMain.Update). Fires the deferred open-scroll once
        // the delay elapses (acts like a coroutine WaitForSeconds).
        public static void Update()
        {
            // The map VC was destroyed (left the screen): drop the dangling pointer so we stop poking it.
            if (_go != IntPtr.Zero && !IsAlive(_go)) _go = IntPtr.Zero;
            // Drive the global "player is inside the roguelike" flag. Alive (even if temporarily
            // inactive — encounter actions push other VCs on top) means we're still in-flow.
            // Pointer becomes IntPtr.Zero when the user navigates back to home; flag drops to false.
            RoguelikeFlow.InRoguelike = _go != IntPtr.Zero;
            // Stat animation pipeline (lp/gold actions + post-combat). Runs only while the map is
            // visible so a change mid-duel animates on return. Diff detects -> spawns floaters ->
            // floaters arm the deferred HUD counters. Must run before the refresh block so the
            // counter captures the old value before SetLpText (in Refresh) snaps the label.
            if (IsActive(_go))
            {
                DetectStatDiff();
                float dt = NextAnimDt();
                RoguelikeStatAnim.TickFloaters(_floaters, dt, _tmpType);
                RoguelikeStatAnim.TickCounters(_counters, dt, SetTmpText);
                TickCardFlights(dt);
            }
            // Game over: the run died (active->false) without winning. Latch the edge, then show the
            // defeat dialog once the LP->0 animation finished (floaters/counters drained) and the map
            // is on screen. Covers both a lost duel (post-combat LP diff animates first) and a lethal
            // stat action. The dialog's OK pops back to home.
            {
                bool active = RoguelikeApi.IsRunActive();
                if (active && !_runWasActive) _pendingDefeat = false; // fresh run re-arms
                if (_runWasActive && !active && !RoguelikeApi.Won()) _pendingDefeat = true;
                _runWasActive = active;
                if (_pendingDefeat && IsActive(_go) && _floaters.Count == 0 && _counters.Count == 0)
                {
                    _pendingDefeat = false;
                    RoguelikeFlow.ShowDefeatAndReturnHome();
                }
            }
            // Pending post-duel refresh: apply only once the map is visible again (fresh ClientWork).
            if (_refreshPending && IsActive(_go))
            {
                _refreshPending = false;
                Refresh();
            }
            // Open a pending action prompt only while the map is on screen (mirrors the pending-duel
            // gate). Token dedup keeps it from re-opening; resolved/chained prompts advance here too.
            // The action driver is the single dispatcher (options/message/openpack).
            if (IsActive(_go)) RoguelikeActionDriver.Pump();
            RoguelikePackResultHook.EnsureRegistered();
            // Pick-mode CardPict wiring is deferred (vanilla VC populates clones after OnCreatedView);
            // poll each frame until they appear. No-op when there's no pending wire.
            RoguelikePackResultHook.Poll();
            // Right-click on a hovered card pict opens the CardBrowser. SelectionButton's
            // pointer events only fire for the primary button, so we poll UnityEngine.Input
            // here and look at the hovered pict tracked via onEnter/onExit.
            RoguelikePackResultHook.PollRightClick();
            if (!_scrollPending || DateTime.UtcNow < _scrollDueAt) return;
            _scrollPending = false;
            // Recompute the target now: the viewport height was 0 at build time (layout not settled).
            IntPtr deckGroup = _go != IntPtr.Zero ? GameObject.FindGameObjectByPath(_go, DeckGroup) : IntPtr.Zero;
            float vh = deckGroup != IntPtr.Zero ? RectHeight(deckGroup) : 0;
            _scrollTargetY = ScrollTargetY(_scrollContentHeight, _scrollCurY, vh);
            _hasScrollTarget = vh > 0;
            if (_extScroll != IntPtr.Zero) ApplyScroll(_extScroll);
        }

        // Re-render after a move. Keep the scroll container (so the scroll position is preserved
        // and ScrollByTargetPos animates from where the player was, not from a rebuilt top);
        // only the nodes/edges inside RgMap are rebuilt.
        public static void Refresh()
        {
            if (!_ready || _go == IntPtr.Zero) return;
            try
            {
                IntPtr sv = GameObject.FindGameObjectByPath(_go, RgScrollViewPath);
                IntPtr ctGo = GameObject.FindGameObjectByPath(_go, RgMapPath);
                IntPtr srComp = sv != IntPtr.Zero ? GameObject.GetComponent(sv, _extScrollRectType) : IntPtr.Zero;
                IntPtr content = GameObject.FindGameObjectByPath(_go, DeckContent);
                IntPtr template = content != IntPtr.Zero ? GameObject.FindGameObjectByName(content, "Template") : IntPtr.Zero;
                IntPtr deckGroup = GameObject.FindGameObjectByPath(_go, DeckGroup);
                if (ctGo == IntPtr.Zero || srComp == IntPtr.Zero || template == IntPtr.Zero || deckGroup == IntPtr.Zero)
                {
                    if (sv != IntPtr.Zero) UnityObject.Destroy(sv);
                    SetupMap(_go);
                    return;
                }
                DestroyChildrenExcept(ctGo, "RgPlayerMarker");        // keep the marker so it can animate
                RenderMap(template, GameObject.GetTransform(ctGo), srComp, deckGroup); // RenderMap takes the Transform
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] map refresh EX: " + ex); }
        }

        // Runs for every ExtendedScrollRect; re-assert dragScrollEnabled + scroll to the current
        // node only on ours (Start/Initialize clears drag and resets the scroll position after our
        // OnCreatedView build).
        static void OnExtScrollStart(IntPtr thisPtr)
        {
            _hookExtStart.Original(thisPtr);
            if (thisPtr != _extScroll) return;
            if (_extDragScrollEnabled != null)
            {
                csbool drag = true;
                _extDragScrollEnabled.SetValue(thisPtr, new IntPtr(&drag));
            }
            ApplyScroll(thisPtr);
            // First build's node styling (during OnCreatedView) gets overridden once the view fully
            // activates; re-apply now (next frame), the timing that already works on refresh.
            RestyleNodes(GameObject.FindGameObjectByPath(_go, RgMapPath));
        }

        // Re-apply the per-state styling to the already-built nodes (no rebuild, no re-wiring).
        static void RestyleNodes(IntPtr ctGo)
        {
            if (ctGo == IntPtr.Zero) return;
            List<RoguelikeApi.MapNode> nodes = RoguelikeApi.GetMapNodes();
            int pos = RoguelikeApi.Position();
            HashSet<int> reachable = Reachable(nodes, pos);
            HashSet<int> visited = RoguelikeApi.Visited();
            foreach (RoguelikeApi.MapNode n in nodes)
            {
                IntPtr node = GameObject.FindGameObjectByName(ctGo, "RgNode" + n.Id);
                if (node == IntPtr.Zero) continue;
                StyleNode(node, n.Type, reachable.Contains(n.Id), visited.Contains(n.Id), n.Id == pos);
            }
        }

        static void OnSlot(int slot)
        {
            if (slot < 0 || slot >= MapSlots) return;
            int id = _slotNodeId[slot];
            if (id >= 0) RoguelikeNodeAction.Open(id);
        }

        // Build OUR OWN scroll: RgScrollView > RgViewport > RgMap inside the DeckGroup, and hide
        // the game's Scroll View2 (its ExtendedScrollRect/InfinityScroll resize our content). A
        // plain Unity ScrollRect we fully control scrolls RgMap (one node per map node).
        static void SetupMap(IntPtr go)
        {
            IntPtr content = GameObject.FindGameObjectByPath(go, DeckContent);
            IntPtr template = content != IntPtr.Zero ? GameObject.FindGameObjectByName(content, "Template") : IntPtr.Zero;
            if (template == IntPtr.Zero) { Console.WriteLine("[Roguelike] map: template not found"); return; }
            IntPtr deckGroup = GameObject.FindGameObjectByPath(go, DeckGroup);
            if (deckGroup == IntPtr.Zero) { Console.WriteLine("[Roguelike] map: DeckGroup not found"); return; }

            // Reuse the header's own texts: NameText = title, the "0/999" slot = run LP (both filled
            // by RenderMap -> SetLpText). Capture NameText as the boss-label TMP template, then hide
            // the deck-management clutter we don't need on the map.
            _bossLabelSrc = GameObject.FindGameObjectByPath(go, HeaderName);
            HideHeaderClutter(go);
            WireDeckEditButton(go); // keep the header pickup button -> opens the run deck editor

            Hide(go, ScrollView);   // hide the game's scroll (its InfinityScroll resized our content)

            // RgScrollView (fills DeckGroup) + ScrollRect.
            IntPtr sv = GameObject.New();
            UnityObject.SetName(sv, "RgScrollView");
            GameObject.AddComponent(sv, _rectType);
            IntPtr svt = GameObject.GetTransform(sv);
            Transform.SetParent(svt, GameObject.GetTransform(deckGroup));
            SetFill(svt);
            Transform.SetLocalScale(svt, new Vector3(1, 1, 1));
            // Transparent Image on the root = raycast target so drag/wheel scrolls (matches the
            // game's Scroll View2, which has its Image on the root, not the viewport).
            IntPtr svImg = GameObject.AddComponent(sv, _imageType);
            Col clear = new Col { r = 1, g = 1, b = 1, a = 0 };
            _graphicColor.GetSetMethod().Invoke(svImg, new IntPtr[] { new IntPtr(&clear) });
            IntPtr srComp = GameObject.AddComponent(sv, _extScrollRectType); // game's ScrollRect subclass
            _extScroll = srComp; // identity for the Start hook (re-asserts dragScrollEnabled)

            // RgViewport (fills RgScrollView) + RectMask2D to clip.
            IntPtr vp = GameObject.New();
            UnityObject.SetName(vp, "RgViewport");
            GameObject.AddComponent(vp, _rectType);
            GameObject.AddComponent(vp, _rectMask2DType);
            IntPtr vpt = GameObject.GetTransform(vp);
            Transform.SetParent(vpt, svt);
            SetFill(vpt);
            Transform.SetLocalScale(vpt, new Vector3(1, 1, 1));

            // RgMap (content): full width, top-anchored. Height + children come from RenderMap.
            IntPtr container = GameObject.New();
            UnityObject.SetName(container, "RgMap");
            GameObject.AddComponent(container, _rectType);
            IntPtr ct = GameObject.GetTransform(container);
            Transform.SetParent(ct, vpt);
            SetVec(ct, _anchorMin, new AssetHelper.Vector2(0, 1));
            SetVec(ct, _anchorMax, new AssetHelper.Vector2(1, 1));
            SetVec(ct, _pivot, new AssetHelper.Vector2(0.5f, 1));
            Vector3 z0 = new Vector3(0, 0, 0);
            _anchoredPos3D.GetSetMethod().Invoke(ct, new IntPtr[] { new IntPtr(&z0) });
            Transform.SetLocalScale(ct, new Vector3(1, 1, 1));

            // Wire the ScrollRect (vertical only).
            _scrollViewport.GetSetMethod().Invoke(srComp, new IntPtr[] { vpt });
            _scrollRectContent.GetSetMethod().Invoke(srComp, new IntPtr[] { ct });
            SetBoolProp(srComp, _scrollHorizontal, false);
            SetBoolProp(srComp, _scrollVertical, true);
            float sens = 60f; // match the game's Scroll View2 (default 1 = imperceptible wheel)
            _scrollSensitivity.GetSetMethod().Invoke(srComp, new IntPtr[] { new IntPtr(&sens) });
            if (_extDragScrollEnabled != null) { csbool drag = true; _extDragScrollEnabled.SetValue(srComp, new IntPtr(&drag)); }
            else Console.WriteLine("[Roguelike] map: ExtendedScrollRect.dragScrollEnabled field not found");

            RenderMap(template, ct, srComp, deckGroup);
        }

        // Header bits irrelevant to the map: the extra counters, search/filter and deck-management
        // buttons. We keep NameText (title) and DeckNum (its value text repurposed for LP).
        static readonly string[] HeaderClutter =
        {
            "TextSub", "TournamentDeckNum", "NeuronDeckNum", "ButtonFilter",
            "BulkDecksDeletionButton", "ButtonStructureDeckView", "ExtendedInputField",
            "ButtonTrashSub", "ButtonTrash", "ButtonCaution",
            "ButtonPageUp", "ButtonPageDown", "ButtonOpenNeuronDecks",
        };

        static void HideHeaderClutter(IntPtr go)
        {
            foreach (string name in HeaderClutter) Hide(go, HeaderGroup + "." + name);
            Hide(go, HeaderGroup + ".DeckNum.IconDeckNum"); // drop the deck icon; keep just the LP text
            IntPtr deckNum = GameObject.FindGameObjectByPath(go, HeaderGroup + ".DeckNum");
            if (deckNum != IntPtr.Zero) GameObject.SetActive(deckNum, true); // ensure the LP slot shows
            EnsureGoldLabel(go);
        }

        // Clone the DeckNum subtree once into a sibling GoldNum, offset to the right of the LP slot.
        // Same TMP child structure so SetTmpText(HeaderGold,...) targets the parallel path.
        static void EnsureGoldLabel(IntPtr go)
        {
            if (_goldLabelGo != IntPtr.Zero && IsAlive(_goldLabelGo)) return;
            IntPtr src = GameObject.FindGameObjectByPath(go, HeaderGroup + ".DeckNum");
            IntPtr parentGo = GameObject.FindGameObjectByPath(go, HeaderGroup);
            if (src == IntPtr.Zero || parentGo == IntPtr.Zero) return;
            IntPtr clone = UnityObject.Instantiate(src, GameObject.GetTransform(parentGo));
            UnityObject.SetName(clone, "GoldNum");
            Hide(clone, "IconDeckNum"); // no icon (placeholder for a future gold sprite)
            // The header lays its children out left-to-right by sibling order, so place GoldNum
            // immediately after the LP slot (DeckNum). The deck-edit button keeps its later index,
            // so the order becomes LP -> GOLD -> button (a fresh clone is appended last otherwise).
            IntPtr deckT = GameObject.GetTransform(src);
            IntPtr goldT = GameObject.GetTransform(clone);
            Transform.SetSiblingIndex(goldT, Transform.GetSiblingIndex(deckT) + 1);
            GameObject.SetActive(clone, true);
            _goldLabelGo = clone;
        }

        // ---- Stat animation (lp/gold) ----

        // Wall-clock dt between Update() calls (no Unity Time wrapper here). First call returns 0;
        // capped at 0.1s to swallow debugger pauses / hitches.
        static float NextAnimDt()
        {
            if (_animClock == null) { _animClock = System.Diagnostics.Stopwatch.StartNew(); return 0f; }
            float dt = (float)_animClock.Elapsed.TotalSeconds;
            _animClock.Restart();
            return dt > 0.1f ? 0.1f : dt;
        }

        // Compare current run stats vs the last snapshot; spawn a floater + HUD counter for each
        // non-zero delta. Re-baselines silently when a fresh run goes active (so a new run doesn't
        // animate from the previous run's final values), but DOES diff the death frame (active
        // true->false) so a lethal hit animates LP down to 0 before the defeat dialog.
        static void DetectStatDiff()
        {
            bool active = RoguelikeApi.IsRunActive();
            int curLp = RoguelikeApi.Lp();
            int curGold = RoguelikeApi.Gold();
            // Fresh run (or first read): baseline only. Drop stale floaters/counters left from a
            // previous run — their GOs died with the old map VC, so just clear the lists.
            if (!_snap.Valid || (active && !_snap.Active))
            {
                _floaters.Clear();
                _counters.Clear();
                _snap = new HudSnap { Valid = true, Active = active, Lp = curLp, Gold = curGold };
                return;
            }
            int dLp = curLp - _snap.Lp;
            int dGold = curGold - _snap.Gold;
            if (dLp != 0)
                SpawnDelta("lp", dLp, _snap.Lp, curLp, HeaderLp,
                    cur => RoguelikeLabels.Get("map.lp", "LP {0} / {1}", cur, RoguelikeApi.MaxLp()));
            if (dGold != 0)
                SpawnDelta("gold", dGold, _snap.Gold, curGold, HeaderGold,
                    cur => RoguelikeLabels.Get("map.gold", "GOLD {0}", cur));
            _snap.Lp = curLp; _snap.Gold = curGold; _snap.Active = active;
        }

        // Build one FloatingDelta (clone of the HUD label, centered, sign-tinted) + its deferred
        // HudCounter. `from` is the OLD value (snapshot) so a fresh counter animates old->new even if
        // SetLpText already snapped the label. An in-flight counter for the same label is retargeted
        // from its currently displayed value (no snap).
        static void SpawnDelta(string stat, int delta, int from, int to, string labelPath,
                               Func<int, string> format)
        {
            IntPtr labelGo = GameObject.FindGameObjectByPath(_go, labelPath);
            if (labelGo == IntPtr.Zero) return;
            // Clone the HUD label into the full-screen Window node so it isn't laid out by the header
            // group nor clipped. KEEP the clone's pivot/anchors/sizeDelta/alignment (don't reset) so
            // it's an exact twin of the label — then placing its world position on the label's world
            // position makes the glyphs overlap precisely on landing.
            IntPtr overlay = WindowRoot();
            if (overlay == IntPtr.Zero) overlay = _go;
            IntPtr clone = UnityObject.Instantiate(labelGo, GameObject.GetTransform(overlay));
            UnityObject.SetName(clone, "RgStatFloater_" + stat);
            IntPtr ct = GameObject.GetTransform(clone);
            Transform.SetAsLastSibling(ct); // render on top of the window content

            float r, g, b;
            if (delta > 0) { r = RoguelikeStatAnim.PosR; g = RoguelikeStatAnim.PosG; b = RoguelikeStatAnim.PosB; }
            else           { r = RoguelikeStatAnim.NegR; g = RoguelikeStatAnim.NegG; b = RoguelikeStatAnim.NegB; }
            IntPtr tmp = GameObject.GetComponent(clone, _tmpType);
            if (tmp != IntPtr.Zero)
            {
                string sign = delta > 0 ? "+" : "";
                string label = stat == "lp"
                    ? RoguelikeLabels.Get("anim.lp.delta", "{0}{1} LP", sign, delta)
                    : RoguelikeLabels.Get("anim.gold.delta", "{0}{1} GOLD", sign, delta);
                TMPro.TMP_Text.SetText(tmp, label);
                TMPro.TMP_Text.SetColor(tmp, r, g, b, 1f);
            }

            // Target = the HUD label's exact world position (same pivot as the clone -> exact overlap).
            // Start = ~250px below it (toward screen center), so the number rises from mid-screen up to
            // the HUD. LP and GOLD start under their own labels, so a simultaneous burst stays separated.
            Vector3 endPos   = Transform.GetPosition(GameObject.GetTransform(labelGo));
            Vector3 startPos = new Vector3(endPos.x, endPos.y - 250f, endPos.z);
            Transform.SetPosition(ct, startPos); // place at start now (avoid a 1-frame flash at the inherited pos)

            RoguelikeStatAnim.HudCounter counter = _counters.Find(c => c.LabelPath == labelPath);
            if (counter != null)
            {
                counter.From = ReadCurrentDisplayed(labelPath, from);
                counter.To = to; counter.T = 0; counter.Active = false;
            }
            else
            {
                counter = new RoguelikeStatAnim.HudCounter
                { LabelPath = labelPath, From = from, To = to, T = 0, Active = false, Format = format };
                _counters.Add(counter);
            }

            _floaters.Add(new RoguelikeStatAnim.FloatingDelta
            { Go = clone, StartPos = startPos, EndPos = endPos, T = 0, R = r, G = g, B = b, Pending = counter });
        }

        // Extract the displayed integer from a HUD label (for retargeting a mid-flight counter).
        static int ReadCurrentDisplayed(string labelPath, int fallback)
        {
            IntPtr go = GameObject.FindGameObjectByPath(_go, labelPath);
            if (go == IntPtr.Zero) return fallback;
            IntPtr tmp = GameObject.GetComponent(go, _tmpType);
            if (tmp == IntPtr.Zero) return fallback;
            string s = TMPro.TMP_Text.GetText(tmp);
            System.Text.RegularExpressions.Match m =
                System.Text.RegularExpressions.Regex.Match(s ?? "", @"-?\d+");
            int v;
            return m.Success && int.TryParse(m.Value, out v) ? v : fallback;
        }

        // True while a HUD counter is animating the given label — SetLpText skips it so the snap
        // doesn't clobber the tween.
        static bool IsLabelCountering(string labelPath)
        {
            foreach (RoguelikeStatAnim.HudCounter c in _counters) if (c.LabelPath == labelPath) return true;
            return false;
        }

        // ---- addCard animation (full card flies center -> deck button) ----

        // Called by the action driver on an addCard prompt: spawn a flying card per cid, then ack the
        // token once they all land (the cards are already applied server-side; the ack just advances).
        public static void PlayAddCard(int[] cids, int token)
        {
            if (cids == null || cids.Length == 0 || _go == IntPtr.Zero) { RoguelikeApi.ActionRespond(token); return; }
            IntPtr overlay = WindowRoot();
            if (overlay == IntPtr.Zero) overlay = _go;
            // Target = the deck button (opens the run deck). Fall back to overlay top-center.
            IntPtr btn = GameObject.FindGameObjectByPath(_go, HeaderGroup + ".ButtonPickupCard");
            Vector3 endPos = btn != IntPtr.Zero
                ? Transform.GetPosition(GameObject.GetTransform(btn))
                : new Vector3(0, 0, 0);
            _addCardAckToken = token;
            for (int i = 0; i < cids.Length; i++)
            {
                IntPtr card = CreateCardImage(overlay, cids[i]);
                if (card == IntPtr.Zero) continue;
                float xOff = (i - (cids.Length - 1) / 2f) * 70f; // spread when several at once
                PlaceNode(card, xOff, 0, new AssetHelper.Vector2(CardW, CardH)); // overlay-center + xOff
                Vector3 startPos = Transform.GetPosition(GameObject.GetTransform(card));
                Transform.SetAsLastSibling(GameObject.GetTransform(card));
                // Nose-toward-target tilt (jet feel): the card's +Y points along the flight path.
                Vector3 dir = new Vector3(endPos.x - startPos.x, endPos.y - startPos.y, 0);
                float angle = (float)(Math.Atan2(dir.y, dir.x) * 180.0 / Math.PI) - 90f;
                // Negative T = hold phase (card shown upright at center); flight starts when T reaches 0.
                _cardFlights.Add(new CardFlight
                {
                    Go = card, StartPos = startPos, EndPos = endPos, Angle = angle,
                    T = -(CardHoldFrac + i * CardStaggerFrac),
                });
            }
            if (_cardFlights.Count == 0) { _addCardAckToken = -1; RoguelikeApi.ActionRespond(token); } // nothing spawned
        }

        // Build a RawImage GameObject bound to the full card art via the native BindingCardMaterial.
        static IntPtr CreateCardImage(IntPtr parent, int cid)
        {
            if (_bindingMethod == null) return IntPtr.Zero;
            IntPtr go = GameObject.New();
            UnityObject.SetName(go, "RgAddCard_" + cid);
            GameObject.AddComponent(go, _rectType);
            IntPtr raw = GameObject.AddComponent(go, _rawImageType);
            Transform.SetParent(GameObject.GetTransform(go), GameObject.GetTransform(parent));
            int cidL = cid, rareL = 0; // 0 = normal treatment
            try { _bindingMethod.Invoke(IntPtr.Zero, new IntPtr[] { raw, new IntPtr(&cidL), new IntPtr(&rareL) }); }
            catch (Exception ex) { Console.WriteLine("[Roguelike] BindingCardMaterial.Binding EX: " + ex); }
            return go;
        }

        // Advance card flights: ease world position center -> deck button, shrink, fade on arrival.
        // When the last one lands, ack the pending addCard token.
        static void TickCardFlights(float dt)
        {
            if (_cardFlights.Count == 0) return;
            for (int i = _cardFlights.Count - 1; i >= 0; i--)
            {
                CardFlight f = _cardFlights[i];
                f.T += dt / CardFlightDuration;
                bool done = f.T >= 1f;
                bool holding = f.T < 0f;        // still in the show-at-center delay
                float t = holding ? 0f : (done ? 1f : f.T);
                float e = RoguelikeStatAnim.EaseOutCubic(t);
                IntPtr ct = GameObject.GetTransform(f.Go);
                Vector3 pos = new Vector3(
                    RoguelikeStatAnim.Lerp(f.StartPos.x, f.EndPos.x, e),
                    RoguelikeStatAnim.Lerp(f.StartPos.y, f.EndPos.y, e),
                    RoguelikeStatAnim.Lerp(f.StartPos.z, f.EndPos.z, e));
                Transform.SetPosition(ct, pos);
                float scale = RoguelikeStatAnim.Lerp(1.0f, 0.18f, e); // shrink into the button
                Transform.SetLocalScale(ct, new Vector3(scale, scale, scale));
                // Jet tilt: upright during the hold, then snap the nose toward the target over the
                // first quarter of the flight and hold that angle the rest of the way.
                float rot = holding ? 0f : f.Angle * (t < 0.25f ? t / 0.25f : 1f);
                Vector3 euler = new Vector3(0, 0, rot);
                _localEuler.GetSetMethod().Invoke(ct, new IntPtr[] { new IntPtr(&euler) });
                IntPtr g = GameObject.GetComponent(f.Go, _graphicType);
                if (g != IntPtr.Zero)
                {
                    float alpha = t < 0.65f ? 1f : 1f - (t - 0.65f) / 0.35f; // opaque in flight, fade at the button
                    Col c = new Col { r = 1f, g = 1f, b = 1f, a = alpha };
                    _graphicColor.GetSetMethod().Invoke(g, new IntPtr[] { new IntPtr(&c) });
                }
                if (done) { UnityObject.Destroy(f.Go); _cardFlights.RemoveAt(i); }
            }
            if (_cardFlights.Count == 0 && _addCardAckToken >= 0)
            {
                int tok = _addCardAckToken; _addCardAckToken = -1;
                RoguelikeApi.ActionRespond(tok);
            }
        }

        // Keep the header pickup button visible and rewire its click to open the run deck editor.
        static readonly Action _onDeckEdit = RoguelikeDeckEditScreen.Open;

        static void WireDeckEditButton(IntPtr go)
        {
            IntPtr btn = GameObject.FindGameObjectByPath(go, HeaderGroup + ".ButtonPickupCard");
            if (btn == IntPtr.Zero) return;
            GameObject.SetActive(btn, true);
            IntPtr sel = GameObject.GetComponent(btn, _selectionButtonType);
            if (sel == IntPtr.Zero) return;
            IL2Object onClick = _selBtnOnClick.GetValue(sel);
            if (onClick == null) return;
            if (_ueRemoveAll != null) _ueRemoveAll.Invoke(onClick.ptr);
            IntPtr cb = UnityEngine.Events._UnityAction.CreateUnityAction(_onDeckEdit);
            _ueAddListener.Invoke(onClick.ptr, new IntPtr[] { cb });
        }

        // Fill the reused header texts each render: NameText = title (act/asc), the "0/999" slot = LP.
        static void SetLpText()
        {
            if (_go == IntPtr.Zero || _tmpType == IntPtr.Zero) return;
            int acts = RoguelikeApi.Acts(); if (acts < 1) acts = 1;
            int asc = RoguelikeApi.Ascension();
            string title = RoguelikeLabels.Get("map.title", "Mapa da Run   ·   Ato {0}/{1}", RoguelikeApi.Act() + 1, acts);
            if (asc > 0) title += RoguelikeLabels.Get("map.title.asc", "   ·   Asc {0}", asc);
            SetTmpText(HeaderName, title);
            // Skip the HUD label writes while a counter is animating that label — the counter writes
            // the interpolated value each frame; a render-time snap here would fight the tween.
            if (!IsLabelCountering(HeaderLp))
                SetTmpText(HeaderLp, RoguelikeLabels.Get("map.lp", "LP {0} / {1}", RoguelikeApi.Lp(), RoguelikeApi.MaxLp()));
            if (!IsLabelCountering(HeaderGold))
                SetTmpText(HeaderGold, RoguelikeLabels.Get("map.gold", "GOLD {0}", RoguelikeApi.Gold()));
        }

        static void SetTmpText(string path, string text)
        {
            IntPtr o = GameObject.FindGameObjectByPath(_go, path);
            if (o == IntPtr.Zero) return;
            IntPtr tmp = GameObject.GetComponent(o, _tmpType);
            if (tmp != IntPtr.Zero) TMPro.TMP_Text.SetText(tmp, text);
        }

        // Show a baked encounter name (boss) as a small label under its node. Cloned from the
        // header NameText so it inherits the themed TMP style; re-created with the node each render.
        static void AddBossLabel(IntPtr node, string name)
        {
            if (_bossLabelSrc == IntPtr.Zero || _tmpType == IntPtr.Zero || string.IsNullOrEmpty(name)) return;
            IntPtr label = UnityObject.Instantiate(_bossLabelSrc, GameObject.GetTransform(node));
            UnityObject.SetName(label, "RgNodeName");
            IntPtr lt = GameObject.GetTransform(label);
            SetVec(lt, _anchorMin, new AssetHelper.Vector2(0.5f, 0));
            SetVec(lt, _anchorMax, new AssetHelper.Vector2(0.5f, 0));
            SetVec(lt, _pivot, new AssetHelper.Vector2(0.5f, 1)); // pivot top -> hangs below the node
            SetVec(lt, _sizeDelta, new AssetHelper.Vector2(240, 0));
            Vector3 p = new Vector3(0, -8, 0);
            _anchoredPos3D.GetSetMethod().Invoke(lt, new IntPtr[] { new IntPtr(&p) });
            Transform.SetLocalScale(lt, new Vector3(0.8f, 0.8f, 0.8f));
            GameObject.SetActive(label, true);
            Transform.SetAsLastSibling(lt);
            IntPtr tmp = GameObject.GetComponent(label, _tmpType);
            if (tmp != IntPtr.Zero) TMPro.TMP_Text.SetText(tmp, name);
        }

        // (Re)build the nodes/edges/marker inside RgMap from the current run state and scroll to the
        // current node. Container is left intact so the scroll position carries across moves.
        static void RenderMap(IntPtr template, IntPtr ct, IntPtr srComp, IntPtr deckGroup)
        {
            RoguelikeNodeAction.Close(); // never leave a stale drawer over a rebuilt map
            List<RoguelikeApi.MapNode> nodes = RoguelikeApi.GetMapNodes();
            int pos = RoguelikeApi.Position();
            HashSet<int> reachable = Reachable(nodes, pos);
            HashSet<int> visited = RoguelikeApi.Visited();
            int rows = 0, cols = 0;
            foreach (RoguelikeApi.MapNode n in nodes)
            {
                if (n.Row + 1 > rows) rows = n.Row + 1;
                if (n.Col + 1 > cols) cols = n.Col + 1;
            }
            float contentHeight = rows * RowStep + RowStep * 2; // padding top+bottom
            SetVec(ct, _sizeDelta, new AssetHelper.Vector2(0, contentHeight));

            DrawEdges(ct, nodes, rows, cols, visited);

            int slot = 0;
            float curX = 0, curY = RowY(-1, rows); // default: entry, below row 0
            foreach (RoguelikeApi.MapNode n in nodes)
            {
                IntPtr node = UnityObject.Instantiate(template, ct);
                UnityObject.SetName(node, "RgNode" + n.Id);
                GameObject.SetActive(node, true);
                KeepFrame(node);
                float x = ColX(n.Col, cols);
                float y = RowY(n.Row, rows);
                PlaceNode(node, x, y, new AssetHelper.Vector2(120, 120));

                bool isCurrent = n.Id == pos;
                bool isVisited = visited.Contains(n.Id);
                bool isOpen = reachable.Contains(n.Id);
                if (isCurrent) { curX = x; curY = y; }
                if (slot < MapSlots) // every node is clickable now (drawer decides the action)
                {
                    _slotNodeId[slot] = n.Id;
                    WireNodeClick(node, _slotActions[slot]);
                    slot++;
                }
                SetNodeArt(node, n.IconImage); // baked encounter art (behind the type glyph)
                StyleNode(node, n.Type, isOpen, isVisited, isCurrent);
                if (!string.IsNullOrEmpty(n.Name)) AddBossLabel(node, n.Name);
            }

            // First build creates the marker at the spot; later renders keep it and animate it over.
            IntPtr marker = GameObject.FindGameObjectByName(Component.GetGameObject(ct), "RgPlayerMarker");
            if (marker == IntPtr.Zero) PlacePlayerMarker(ct, curX, curY);
            else AnimateMarker(marker, curX, curY);

            _scrollContentHeight = contentHeight;
            _scrollCurY = curY;
            float vh = RectHeight(deckGroup);
            _scrollTargetY = ScrollTargetY(contentHeight, curY, vh);
            _hasScrollTarget = vh > 0;
            ApplyScroll(srComp); // re-applied from the Start hook on first build (post Initialize)

            SetLpText();
        }

        // Looping ping-pong scale on the choosable (reachable) nodes so the next moves stand out.
        static void PulseNode(IntPtr node)
        {
            if (_tweenScaleType == IntPtr.Zero || _tPlay == null) return;
            IntPtr tw = GameObject.GetComponent(node, _tweenScaleType); // reuse so re-styling doesn't stack tweens
            if (tw == IntPtr.Zero) tw = GameObject.AddComponent(node, _tweenScaleType);
            if (tw == IntPtr.Zero) return;
            Vector3 from = new Vector3(1, 1, 1);
            Vector3 to = new Vector3(1.12f, 1.12f, 1.12f);
            if (_tsFrom != null) _tsFrom.SetValue(tw, new IntPtr(&from));
            if (_tsTo != null) _tsTo.SetValue(tw, new IntPtr(&to));
            float dur = 0.6f;
            if (_tDuration != null) _tDuration.SetValue(tw, new IntPtr(&dur));
            int style = 3; // Tween.Style.PingPongLoop
            if (_tStyle != null) _tStyle.SetValue(tw, new IntPtr(&style));
            if (_tTarget != null) _tTarget.SetValue(tw, node);
            _tPlay.Invoke(tw);
        }

        // Color the Out outline by state, after disabling the tile's ColorContainerGraphic (which
        // would otherwise re-apply the theme color over ours). Reachable nodes also pulse and keep
        // their SelectionButton; the rest are disabled so hover/click can't recolor them.
        static void StyleNode(IntPtr node, string type, bool isOpen, bool isVisited, bool isCurrent)
        {
            IntPtr outFrame = GameObject.FindGameObjectByPath(node, "Body.Out");
            DisableColorContainers(outFrame);
            // Disabled nodes get a dark rounded fill (the tile's "Over/Base" selection panel,
            // which is rounded to match the frame), so they read clearly as locked.
            SetDisabledFill(node, !isOpen && !isVisited);
            // Cleared nodes (visited, but not where the player currently stands) show the check.
            ShowCheck(node, isVisited && !isCurrent);
            // Outline = node-type color, brightness by state (reachable bright, visited mid, locked dim).
            float bright = isOpen ? 1f : (isVisited ? 0.8f : 0.45f);
            SetImageColor(outFrame, Scale(TypeColor(type), bright));
            SetNodeIcon(node, type, Scale(TypeColor(type), bright));
            // Disabled nodes (not reachable, not the current spot) fade their baked art.
            SetArtAlpha(node, (isOpen || isCurrent) ? 1f : 0.4f);
            // Every node stays clickable (the drawer gates the action), so we no longer disable the
            // button on locked nodes; only reachable nodes pulse.
            if (isOpen) PulseNode(node);
        }

        // Solo-mode map glyph reused per node type.
        internal static string IconNameFor(string t)
        {
            switch (t)
            {
                case "elite":  return "GUI_SoloSelectChapter_Map_Icon_Goal_S";
                case "boss":   return "GUI_SoloSelectChapter_Map_Icon_Goal";
                case "reward": return "GUI_SoloSelectChapter_Map_Icon_Reward";
                case "shop":   return "Connectingicon_card";
                case "event":  return "GUI_SoloSelectChapter_Map_Icon_Scenario";
                default:       return "GUI_SoloSelectChapter_Map_Icon_Duel";
            }
        }

        // Centered glyph (own Image child) showing the node type, tinted to match.
        static void SetNodeIcon(IntPtr node, string type, Col col)
        {
            IntPtr sprite = FindIcon(IconNameFor(type));
            if (sprite == IntPtr.Zero) return;
            IntPtr icon = GameObject.FindGameObjectByName(node, "RgIcon");
            if (icon == IntPtr.Zero)
            {
                icon = GameObject.New();
                UnityObject.SetName(icon, "RgIcon");
                GameObject.AddComponent(icon, _rectType);
                GameObject.AddComponent(icon, _imageType);
                Transform.SetParent(GameObject.GetTransform(icon), GameObject.GetTransform(node));
                PlaceNode(icon, 0, 0, new AssetHelper.Vector2(64, 64));
                Transform.SetAsLastSibling(GameObject.GetTransform(icon));
            }
            IntPtr img = GameObject.GetComponent(icon, _imageType);
            if (img == IntPtr.Zero) return;
            _imageSprite.GetSetMethod().Invoke(img, new IntPtr[] { sprite });
            _graphicColor.GetSetMethod().Invoke(img, new IntPtr[] { new IntPtr(&col) });
        }

        // Baked encounter art as an "RgArt" child behind the type glyph. Two specs:
        //   card_<cid>     -> Texture2D illustration -> RawImage
        //   profile_<id>   -> loaded "ProfileIcon<id>_L" sprite -> Image
        static void SetNodeArt(IntPtr node, string iconImage)
        {
            if (string.IsNullOrEmpty(iconImage)) return;
            try
            {
                if (iconImage.StartsWith("profile_"))
                {
                    IntPtr sprite = FindIcon("ProfileIcon" + iconImage.Substring(8) + "_L");
                    if (sprite == IntPtr.Zero) return;
                    IntPtr img = EnsureArt(node, _imageType);
                    if (img != IntPtr.Zero) _imageSprite.GetSetMethod().Invoke(img, new IntPtr[] { sprite });
                    return;
                }
                IntPtr tex = ResolveArtTexture(iconImage);
                if (tex == IntPtr.Zero) return;
                IntPtr ri = EnsureArt(node, _rawImageType);
                if (ri != IntPtr.Zero) _rawImageTexture.GetSetMethod().Invoke(ri, new IntPtr[] { tex });
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] SetNodeArt EX: " + ex); }
        }

        // Ensure the masked "RgArt" container exists (RectMask2D with soft edges) carrying a child
        // "RgArtImg" with the given graphic component; returns that graphic component.
        static IntPtr EnsureArt(IntPtr node, IntPtr graphicType)
        {
            IntPtr art = GameObject.FindGameObjectByName(node, "RgArt");
            if (art == IntPtr.Zero)
            {
                art = GameObject.New();
                UnityObject.SetName(art, "RgArt");
                GameObject.AddComponent(art, _rectType);
                IntPtr mask = GameObject.AddComponent(art, _rectMask2DType);
                Vec4 padding = new Vec4(0, 0, 0, 0);
                Vec2Int softness = new Vec2Int(50, 50);
                _maskPadding.GetSetMethod().Invoke(mask, new IntPtr[] { new IntPtr(&padding) });
                _maskSoftness.GetSetMethod().Invoke(mask, new IntPtr[] { new IntPtr(&softness) });
                Transform.SetParent(GameObject.GetTransform(art), GameObject.GetTransform(node));
                PlaceNode(art, 0, 0, new AssetHelper.Vector2(104, 104));

                IntPtr img = GameObject.New();
                UnityObject.SetName(img, "RgArtImg");
                GameObject.AddComponent(img, _rectType);
                GameObject.AddComponent(img, graphicType);
                Transform.SetParent(GameObject.GetTransform(img), GameObject.GetTransform(art));
                PlaceNode(img, 0, 0, new AssetHelper.Vector2(104, 104));
                return GameObject.GetComponent(img, graphicType);
            }
            IntPtr existing = GameObject.FindGameObjectByName(art, "RgArtImg");
            return existing != IntPtr.Zero ? GameObject.GetComponent(existing, graphicType) : IntPtr.Zero;
        }

        // Fade the baked art for disabled nodes; full opacity for reachable/current.
        static void SetArtAlpha(IntPtr node, float alpha)
        {
            IntPtr art = GameObject.FindGameObjectByName(node, "RgArt");
            if (art == IntPtr.Zero) return;
            IntPtr img = GameObject.FindGameObjectByName(art, "RgArtImg");
            if (img == IntPtr.Zero) return;
            IntPtr g = GameObject.GetComponent(img, _graphicType);
            if (g == IntPtr.Zero) return;
            Col c = new Col { r = 1f, g = 1f, b = 1f, a = alpha };
            _graphicColor.GetSetMethod().Invoke(g, new IntPtr[] { new IntPtr(&c) });
        }

        // Texture for a card_<cid> spec via the game's ResourceManager: custom PNG in ClientData
        // first, else the native MD bundle (has every card). Same path the Goat
        // SoloChapterCardImage uses.
        internal static IntPtr ResolveArtTexture(string iconImage)
        {
            if (iconImage.StartsWith("card_"))
                return AssetHelper.LoadImmediateAsset("Card/Images/Illust/tcg/" + iconImage.Substring(5));
            return IntPtr.Zero;
        }

        // Find a loaded sprite by name (Solo atlas icons), cached after the first lookup.
        internal static IntPtr FindIcon(string name)
        {
            IntPtr cached;
            if (_iconCache.TryGetValue(name, out cached) && cached != IntPtr.Zero) return cached;
            try
            {
                IL2Method findAll = Assembler.GetAssembly("UnityEngine.CoreModule").GetClass("Resources", "UnityEngine")
                    .GetMethod("FindObjectsOfTypeAll", m => m.GetParameters().Length == 1);
                IL2Object res = findAll.Invoke(new IntPtr[] { _spriteType });
                if (res == null || res.ptr == IntPtr.Zero) return IntPtr.Zero;
                IL2Array<IntPtr> arr = new IL2Array<IntPtr>(res.ptr);
                for (int i = 0; i < arr.Length; i++)
                {
                    IntPtr sp = arr[i];
                    if (sp == IntPtr.Zero) continue;
                    if (UnityObject.GetName(sp) == name) { _iconCache[name] = sp; return sp; }
                }
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] FindIcon EX: " + ex); }
            return IntPtr.Zero;
        }

        // Base outline color per node type.
        internal static Col TypeColor(string t)
        {
            switch (t)
            {
                case "elite":  return new Col { r = 0.74f, g = 0.45f, b = 0.96f, a = 1f }; // purple
                case "boss":   return new Col { r = 1.00f, g = 0.34f, b = 0.34f, a = 1f }; // red
                case "event":  return new Col { r = 0.38f, g = 0.84f, b = 0.93f, a = 1f }; // cyan
                case "shop":   return new Col { r = 1.00f, g = 0.82f, b = 0.30f, a = 1f }; // gold
                case "reward": return new Col { r = 0.45f, g = 0.90f, b = 0.50f, a = 1f }; // green
                default:       return new Col { r = 0.82f, g = 0.88f, b = 1.00f, a = 1f }; // duel / unknown
            }
        }

        static Col Scale(Col c, float f) { return new Col { r = c.r * f, g = c.g * f, b = c.b * f, a = c.a }; }

        // Show the tile's green "selected" check (IconGroup/SelectedStateToggle/IconOn/Icon) to mark
        // a cleared node; hide the rank/rate siblings that share IconGroup.
        static void ShowCheck(IntPtr node, bool on)
        {
            IntPtr ig = GameObject.FindGameObjectByPath(node, "Body.IconGroup");
            if (ig == IntPtr.Zero) return;
            GameObject.SetActive(ig, on);
            if (!on) return;
            HideChild(ig, "RateIcon");
            HideChild(ig, "RankIcon");
            ActivateChain(ig, "SelectedStateToggle", "IconOn", "Icon");
        }

        static void HideChild(IntPtr parent, string name)
        {
            IntPtr o = GameObject.FindGameObjectByPath(parent, name);
            if (o != IntPtr.Zero) GameObject.SetActive(o, false);
        }

        static void ActivateChain(IntPtr root, params string[] names)
        {
            IntPtr cur = root;
            foreach (string n in names)
            {
                cur = GameObject.FindGameObjectByPath(cur, n);
                if (cur == IntPtr.Zero) return;
                GameObject.SetActive(cur, true);
            }
        }

        static void SetDisabledFill(IntPtr node, bool on)
        {
            IntPtr over = GameObject.FindGameObjectByPath(node, "Body.Over");
            if (over == IntPtr.Zero) return;
            GameObject.SetActive(over, on);
            if (!on) return;
            DisableTweens(over);                       // stop the hover alpha fade
            SetFill(GameObject.GetTransform(over));     // match the node bounds
            IntPtr baseGo = GameObject.FindGameObjectByPath(over, "Base");
            if (baseGo == IntPtr.Zero) return;
            DisableColorContainers(baseGo);
            SetFill(GameObject.GetTransform(baseGo));
            SetImageColor(baseGo, FillDisabled);
        }

        // Disable any Tween* behaviour on a GameObject (so it stops animating alpha/etc).
        static void DisableTweens(IntPtr go)
        {
            if (_behaviourEnabled == null) return;
            IntPtr[] comps = GameObject.GetComponents(go);
            if (comps == null) return;
            csbool off = false;
            foreach (IntPtr c in comps)
            {
                IntPtr cls = Import.Object.il2cpp_object_get_class(c);
                string name = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(Import.Class.il2cpp_class_get_name(cls));
                if (name != null && name.StartsWith("Tween"))
                    _behaviourEnabled.GetSetMethod().Invoke(c, new IntPtr[] { new IntPtr(&off) });
            }
        }

        // Turn off a node's SelectionButton so hover/click can't play its recolor tweens.
        static void DisableButton(IntPtr node)
        {
            if (_behaviourEnabled == null) return;
            IntPtr body = GameObject.FindGameObjectByPath(node, "Body");
            IntPtr sel = body != IntPtr.Zero ? GameObject.GetComponent(body, _selectionButtonType) : IntPtr.Zero;
            if (sel == IntPtr.Zero) return;
            csbool off = false;
            _behaviourEnabled.GetSetMethod().Invoke(sel, new IntPtr[] { new IntPtr(&off) });
        }

        // Disable every ColorContainerGraphic on a GameObject so our Graphic.color stops being
        // overwritten by the theme palette.
        static void DisableColorContainers(IntPtr go)
        {
            if (go == IntPtr.Zero || _colorContainerType == IntPtr.Zero || _behaviourEnabled == null) return;
            IntPtr[] comps = GameObject.GetComponents(go, _colorContainerType);
            if (comps == null) return;
            csbool off = false;
            foreach (IntPtr c in comps) _behaviourEnabled.GetSetMethod().Invoke(c, new IntPtr[] { new IntPtr(&off) });
        }

        static void SetImageColor(IntPtr go, Col c)
        {
            IntPtr img = go != IntPtr.Zero ? GameObject.GetComponent(go, _imageType) : IntPtr.Zero;
            if (img != IntPtr.Zero) _graphicColor.GetSetMethod().Invoke(img, new IntPtr[] { new IntPtr(&c) });
        }

        // Disable the BindingProfileFrameIcon in a cloned PlayerIcon so it stops re-binding (which,
        // off the home screen, drops back to the plain square icon).
        static void FreezeProfileBinding(IntPtr root)
        {
            if (_profileBindingType == IntPtr.Zero || _behaviourEnabled == null) return;
            IntPtr iconFull = GameObject.FindGameObjectByName(root, "IconFull");
            IntPtr binding = iconFull != IntPtr.Zero ? GameObject.GetComponent(iconFull, _profileBindingType) : IntPtr.Zero;
            if (binding == IntPtr.Zero) return;
            csbool off = false;
            _behaviourEnabled.GetSetMethod().Invoke(binding, new IntPtr[] { new IntPtr(&off) });
        }

        static float RectHeight(IntPtr go)
        {
            IntPtr rt = GameObject.GetComponent(go, _rectType);
            if (rt == IntPtr.Zero || _rectRect == null) return 0;
            AssetHelper.Rect r = _rectRect.GetGetMethod().Invoke(rt).GetValueRef<AssetHelper.Rect>();
            return r.m_Height;
        }

        // Content anchoredPosition.y that centers nodeY in a viewport of height vh (content is
        // top-anchored, pivot top), clamped to the scrollable range. Mirrors the game's own math.
        static float ScrollTargetY(float contentHeight, float nodeY, float vh)
        {
            if (vh <= 0) return 0;
            float y = (contentHeight / 2f - nodeY) - vh / 2f;
            if (contentHeight <= vh) return 0;
            if (y < 0) return 0;
            if (y > contentHeight - vh) return contentHeight - vh;
            return y;
        }

        static void ApplyScroll(IntPtr scroll)
        {
            if (!_hasScrollTarget || _scrollByTargetPos == null || scroll == IntPtr.Zero) return;
            AssetHelper.Vector2 target = new AssetHelper.Vector2(0, _scrollTargetY);
            _scrollByTargetPos.Invoke(scroll, new IntPtr[] { new IntPtr(&target) });
        }

        static float ColX(int col, int cols) { return (col - (cols - 1) / 2f) * ColStep; }
        static float RowY(int row, int rows) { return (row - (rows - 1) / 2f) * RowStep; }

        // One thin Image per node->Next link, drawn behind the nodes (called before the node loop).
        // Links between two visited nodes are the traveled path -> highlighted.
        static void DrawEdges(IntPtr ct, List<RoguelikeApi.MapNode> nodes, int rows, int cols, HashSet<int> visited)
        {
            Dictionary<int, RoguelikeApi.MapNode> byId = new Dictionary<int, RoguelikeApi.MapNode>();
            foreach (RoguelikeApi.MapNode n in nodes) byId[n.Id] = n;
            Col idle = new Col { r = 0.8f, g = 0.82f, b = 0.88f, a = 0.5f };
            Col walked = new Col { r = 0.35f, g = 0.85f, b = 0.45f, a = 0.95f };
            foreach (RoguelikeApi.MapNode n in nodes)
            {
                float x1 = ColX(n.Col, cols);
                float y1 = RowY(n.Row, rows);
                foreach (int nextId in n.Next)
                {
                    RoguelikeApi.MapNode m;
                    if (!byId.TryGetValue(nextId, out m)) continue;
                    bool onPath = visited.Contains(n.Id) && visited.Contains(m.Id);
                    MakeEdge(ct, x1, y1, ColX(m.Col, cols), RowY(m.Row, rows), onPath ? walked : idle);
                }
            }
        }

        // A line between two points = a thin Image at the midpoint, rotated to the angle.
        static void MakeEdge(IntPtr ct, float x1, float y1, float x2, float y2, Col c)
        {
            float dx = x2 - x1, dy = y2 - y1;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1f) return;
            float ang = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);

            IntPtr edge = GameObject.New();
            UnityObject.SetName(edge, "RgEdge");
            GameObject.AddComponent(edge, _rectType);
            IntPtr img = GameObject.AddComponent(edge, _imageType);
            _graphicColor.GetSetMethod().Invoke(img, new IntPtr[] { new IntPtr(&c) });

            IntPtr t = GameObject.GetTransform(edge);
            Transform.SetParent(t, ct);
            AssetHelper.Vector2 center = new AssetHelper.Vector2(0.5f, 0.5f);
            _anchorMin.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&center) });
            _anchorMax.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&center) });
            _pivot.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&center) });
            AssetHelper.Vector2 size = new AssetHelper.Vector2(len, EdgeThickness);
            _sizeDelta.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&size) });
            Vector3 mid = new Vector3((x1 + x2) / 2f, (y1 + y2) / 2f, 0);
            _anchoredPos3D.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&mid) });
            Vector3 euler = new Vector3(0, 0, ang);
            _localEuler.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&euler) });
            Transform.SetLocalScale(t, new Vector3(1, 1, 1));
        }

        // Clone the Home view's PlayerIcon (renders the player's profile icon via
        // BindingProfileFrameIcon) and drop it on the current node as the position marker.
        static void PlacePlayerMarker(IntPtr ct, float x, float y)
        {
            IntPtr src = _markerSource;
            if (src == IntPtr.Zero)
            {
                IntPtr home = GameObject.Find("Home"); // fallback (only works while Home is active)
                src = home != IntPtr.Zero ? GameObject.FindGameObjectByName(home, "PlayerIcon") : IntPtr.Zero;
            }
            if (src == IntPtr.Zero) { Console.WriteLine("[Roguelike] map: PlayerIcon source not found"); return; }
            IntPtr icon = UnityObject.Instantiate(src, ct);
            UnityObject.SetName(icon, "RgPlayerMarker");
            GameObject.SetActive(icon, true);
            PlaceNode(icon, x, y, new AssetHelper.Vector2(96, 96));
            Transform.SetAsLastSibling(GameObject.GetTransform(icon)); // draw above nodes
        }

        // Slide the (preserved) player marker from where it sits to the new current node.
        static void AnimateMarker(IntPtr marker, float toX, float toY)
        {
            Transform.SetAsLastSibling(GameObject.GetTransform(marker)); // keep above the rebuilt nodes
            IntPtr rt = GameObject.GetComponent(marker, _rectType);
            if (rt == IntPtr.Zero) return;
            Vector3 to = new Vector3(toX, toY, 0);
            if (_tweenPositionType == IntPtr.Zero || _tPlay == null)
            {
                _anchoredPos3D.GetSetMethod().Invoke(rt, new IntPtr[] { new IntPtr(&to) }); // no tween: snap
                return;
            }
            Vector3 from = _anchoredPos3D.GetGetMethod().Invoke(rt).GetValueRef<Vector3>();
            IntPtr tw = GameObject.GetComponent(marker, _tweenPositionType); // reuse to avoid stacking tweens
            if (tw == IntPtr.Zero) tw = GameObject.AddComponent(marker, _tweenPositionType);
            if (tw == IntPtr.Zero) return;
            if (_tpRtrans != null) _tpRtrans.SetValue(tw, rt);
            if (_tpFrom != null) _tpFrom.SetValue(tw, new IntPtr(&from));
            if (_tpTo != null) _tpTo.SetValue(tw, new IntPtr(&to));
            float dur = 0.35f;
            if (_tDuration != null) _tDuration.SetValue(tw, new IntPtr(&dur));
            int style = 0; // Tween.Style.Once
            if (_tStyle != null) _tStyle.SetValue(tw, new IntPtr(&style));
            _tPlay.Invoke(tw);
        }

        static void DestroyChildrenExcept(IntPtr go, string keepName)
        {
            IntPtr t = GameObject.GetTransform(go);
            int n = Transform.GetChildCount(t);
            for (int i = n - 1; i >= 0; i--)
            {
                IntPtr child = Transform.GetChild(t, i);
                if (child == IntPtr.Zero) continue;
                IntPtr cgo = Component.GetGameObject(child);
                if (UnityObject.GetName(cgo) != keepName) UnityObject.Destroy(cgo);
            }
        }

        // Stretch a RectTransform to fill its parent.
        static void SetFill(IntPtr t)
        {
            SetVec(t, _anchorMin, new AssetHelper.Vector2(0, 0));
            SetVec(t, _anchorMax, new AssetHelper.Vector2(1, 1));
            SetVec(t, _sizeDelta, new AssetHelper.Vector2(0, 0));
            Vector3 z = new Vector3(0, 0, 0);
            _anchoredPos3D.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&z) });
        }

        static void SetBoolProp(IntPtr comp, IL2Property p, bool v)
        {
            if (p == null) return;
            csbool b = v;
            p.GetSetMethod().Invoke(comp, new IntPtr[] { new IntPtr(&b) });
        }

        // Nodes the player may move to from `pos` (-1 = entry, before row 0).
        static HashSet<int> Reachable(List<RoguelikeApi.MapNode> nodes, int pos)
        {
            HashSet<int> set = new HashSet<int>();
            if (pos < 0)
            {
                foreach (RoguelikeApi.MapNode n in nodes) if (n.Row == 0) set.Add(n.Id);
            }
            else
            {
                foreach (RoguelikeApi.MapNode n in nodes)
                    if (n.Id == pos) { foreach (int next in n.Next) set.Add(next); break; }
            }
            return set;
        }

        static void WireNodeClick(IntPtr node, Action action)
        {
            IntPtr body = GameObject.FindGameObjectByPath(node, "Body");
            IntPtr sel = body == IntPtr.Zero ? IntPtr.Zero : GameObject.GetComponent(body, _selectionButtonType);
            if (sel == IntPtr.Zero) return;
            IL2Object onClickObj = _selBtnOnClick.GetValue(sel);
            if (onClickObj == null) return;
            if (_ueRemoveAll != null) _ueRemoveAll.Invoke(onClickObj.ptr);
            IntPtr cb = UnityEngine.Events._UnityAction.CreateUnityAction(action);
            _ueAddListener.Invoke(onClickObj.ptr, new IntPtr[] { cb });
        }

        // Keep only the "Out" rounded outline of a cloned deck-box tile (hide BG fill, case, text,
        // icons, ...). The tile "BG" is a full-size square panel that doesn't match the rounded
        // shape, so we don't show it.
        static void KeepFrame(IntPtr node)
        {
            IntPtr body = GameObject.FindGameObjectByPath(node, "Body");
            if (body == IntPtr.Zero) return;
            IntPtr bt = GameObject.GetTransform(body);
            int n = Transform.GetChildCount(bt);
            for (int i = 0; i < n; i++)
            {
                IntPtr child = Transform.GetChild(bt, i);
                if (child == IntPtr.Zero) continue;
                IntPtr cgo = Component.GetGameObject(child);
                GameObject.SetActive(cgo, UnityObject.GetName(cgo) == "Out");
            }
        }

        static void PlaceNode(IntPtr node, float x, float y, AssetHelper.Vector2 size)
        {
            IntPtr t = GameObject.GetTransform(node);
            AssetHelper.Vector2 c = new AssetHelper.Vector2(0.5f, 0.5f);
            _anchorMin.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&c) });
            _anchorMax.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&c) });
            _pivot.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&c) }); // center pivot so (x,y) = node center
            _sizeDelta.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&size) });
            Vector3 p = new Vector3(x, y, 0);
            _anchoredPos3D.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&p) });
            Transform.SetLocalScale(t, new Vector3(1, 1, 1));
        }

        static void SetVec(IntPtr t, IL2Property prop, AssetHelper.Vector2 v)
        {
            prop.GetSetMethod().Invoke(t, new IntPtr[] { new IntPtr(&v) });
        }

        static void Hide(IntPtr go, string path)
        {
            IntPtr o = GameObject.FindGameObjectByPath(go, path);
            if (o != IntPtr.Zero) GameObject.SetActive(o, false);
        }
    }
}
