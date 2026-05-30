using System;
using System.Collections.Generic;
using System.Linq;
using IL2CPP;
using UnityEngine;

namespace YgoMasterClient
{
    // Customizes CardPackOpenResultViewController when the pack belongs to us (the unified action
    // is an openpack). Keep mode: relabel OK -> Confirm; left-click sends ActionRespondPicks and
    // closes the VC. Pick mode: each CardPict is selectable; OK is gated on |sel| == pick.
    // Right-click and ESC are neutralized by clearing the SelectionButton's shortcut bindings,
    // so the only way to dismiss the result is via the OK button (left-click).
    static unsafe class RoguelikePackResultHook
    {
        static IL2Class _vcClass;
        delegate void Del_OnCreatedView(IntPtr thisPtr);
        static Hook<Del_OnCreatedView> _hook;
        static IntPtr _selectionButtonType, _bindingTextType;
        static IL2Field _selBtnOnClick;
        static IL2Property _selBtnInteractable;
        static IL2Method _ueAddListener, _ueRemoveAll;
        static IL2Method _selBtnRemoveMouseDown, _selBtnRemoveMouseRelease;
        static IL2Method _selBtnRemoveKeyDown, _selBtnRemoveKeyRelease;
        // SelectionButton.Click() — programmatically toggle ShowOwnedNumToggle to "on" so the
        // result lists show owned counts from the start (matches what most players want for the
        // openpack reward screen).
        static IL2Method _selBtnClick;
        // Saved snapshot of $.Cards.have so we can restore the player's collection when our
        // result VC closes (mirrors the deck editor's swap).
        static string _savedHave;
        // SelectionButton PointerCallback events. Neither onDown nor onUp fire on right-click —
        // the native pipeline only raises pointer events for the primary (left) button. So we
        // track hover via onEnter/onExit and poll UnityEngine.Input for right-clicks in Update.
        static IL2Class _pointerCallbackClass;
        static IL2Method _selBtnAddOnEnter;
        static IL2Method _selBtnAddOnExit;
        // UnityEngine.Input.GetMouseButtonDown(int) for right-click detection.
        static IL2Method _inputGetMouseButtonDown;
        // Visual index currently under the mouse, -1 if none. Maintained by onEnter/onExit.
        static int _hoveredIdx = -1;
        // Cids in visual order, captured during ReorderDrawDatas (which already reads each
        // entry's "mrk" while sorting). Reused when right-click opens the CardBrowser — no
        // need to re-walk m_DrawDatas or read AutoReleaseCardMaterial on every click.
        static int[] _cidsByVisual;
        // IL2CPP plumbing for hand-built CardBrowser args. MiniJSON's round-trip widens int
        // to long and arrays to List<object>; the native browser hardcasts to Int32 +
        // List<int> and NREs otherwise. We build the right shape directly.
        static IL2Class _int32Class;
        static IL2Class _dictStrObjClass;          // Dictionary<string, object>
        static IL2Method _dictStrObjCtor;
        static IL2Method _dictStrObjItemSet;       // set_Item(string, object)
        // TMP_Text.text setter — used to update the OK label live as picks toggle. SetTextId
        // (via BindingTextMeshProUGUI) only takes effect the first time; the binding component
        // doesn't refresh on repeated assignments, so we write straight to the TMP text field.
        static IntPtr _tmpTextType;
        // UnityEngine.UI.Image type + sprite setter — used to swap LimitIcon's sprite for the
        // selected-card checkmark using a sprite already loaded in the scene.
        static IntPtr _imageType;
        static IL2Method _imageSpriteSet;
        static IntPtr _checkOnSprite;
        // RectTransform helpers for centering + resizing the LimitIcon over the card (vanilla
        // anchors it at the corner with a small icon size).
        static IntPtr _rectTransformType;
        static IL2Method _rtSetAnchorMin, _rtSetAnchorMax, _rtSetAnchoredPosition, _rtSetPivot, _rtSetSizeDelta;
        // m_DrawDatas reorder: reflect into CardPackOpenResultViewController and the
        // IL2Dictionary<string, object> indexer so we can read "mrk"/"rarity" from each entry
        // and rewrite the list in our desired order BEFORE the native VC builds clones from it.
        static IL2Field _drawDatasField;
        static IL2Class _objectClass;
        static IL2Method _dictItemGet;

        // Pick-mode per-view state. _selected holds the index that matches both visual click
        // order AND server _cards order — because OnCreatedView reorders m_DrawDatas to mirror
        // the server sort BEFORE the native VC instantiates clones from it.
        static HashSet<int> _selected;
        // Selection bounds — server-side both ends ([min, max]) of how many cards the player
        // can pick. When the action declared an exact `pick: N`, both are equal and the UX
        // collapses to the original "pick exactly N" behavior.
        static int _pickMin;
        static int _pickMax;
        static IntPtr _okBtn;
        static List<IntPtr> _pictGos;        // GameObjects of each CardPict, in visual index order
        // Confirm label template — server passes a string with optional {0}/{1} placeholders
        // (e.g. "Confirmar {0}/{1}"). We re-format it whenever the selected count changes so
        // the OK button label tracks progress live. Cached because UpdateOkLabel runs per click.
        static string _confirmTemplate;
        // Deferred wiring: OnCreatedView fires before the vanilla VC instantiates
        // PackCardGroupTemplate(Clone). We capture Content + the pick request here, and
        // Poll() (driven from RoguelikeMapScreen.Update) retries each frame until clones
        // appear, then wires them and clears the flag.
        static IntPtr _pendingContent;
        static bool _wirePending;

        // Pre-built Action[] of pick handlers — each entry is a closure-free lambda over a
        // literal index, so the C# compiler emits a cacheable STATIC method (no display class,
        // no captured `this`). This avoids the IL2CPP delegate-marshalling crash that closures
        // over local vars trigger: _UnityAction.CreateUnityAction routes to the "static method,
        // no this" path in DelegateHelper, which silently drops any captured target.
        // Cap chosen above any realistic pack size (5 in our smokes; bump if you ever roll more).
        const int MaxPickHandlers = 32;
        // Left-click selection: closure-free lambdas over literal indices (compiler emits
        // them as static methods so the IL2CPP delegate marshalling has no captured target
        // to drop). Wired via onClick UnityEvent through WireButton.
        static readonly Action[] _pictHandlers = new Action[]
        {
            () => ToggleCard(0),  () => ToggleCard(1),  () => ToggleCard(2),  () => ToggleCard(3),
            () => ToggleCard(4),  () => ToggleCard(5),  () => ToggleCard(6),  () => ToggleCard(7),
            () => ToggleCard(8),  () => ToggleCard(9),  () => ToggleCard(10), () => ToggleCard(11),
            () => ToggleCard(12), () => ToggleCard(13), () => ToggleCard(14), () => ToggleCard(15),
            () => ToggleCard(16), () => ToggleCard(17), () => ToggleCard(18), () => ToggleCard(19),
            () => ToggleCard(20), () => ToggleCard(21), () => ToggleCard(22), () => ToggleCard(23),
            () => ToggleCard(24), () => ToggleCard(25), () => ToggleCard(26), () => ToggleCard(27),
            () => ToggleCard(28), () => ToggleCard(29), () => ToggleCard(30), () => ToggleCard(31),
        };
        // Hover-tracking handlers — closure-free literals as usual (compiler emits static methods
        // so the IL2CPP delegate marshalling has no captured target to lose).
        static readonly Action<IntPtr>[] _pictEnterHandlers = new Action<IntPtr>[]
        {
            _ => OnPictEnter(0),  _ => OnPictEnter(1),  _ => OnPictEnter(2),  _ => OnPictEnter(3),
            _ => OnPictEnter(4),  _ => OnPictEnter(5),  _ => OnPictEnter(6),  _ => OnPictEnter(7),
            _ => OnPictEnter(8),  _ => OnPictEnter(9),  _ => OnPictEnter(10), _ => OnPictEnter(11),
            _ => OnPictEnter(12), _ => OnPictEnter(13), _ => OnPictEnter(14), _ => OnPictEnter(15),
            _ => OnPictEnter(16), _ => OnPictEnter(17), _ => OnPictEnter(18), _ => OnPictEnter(19),
            _ => OnPictEnter(20), _ => OnPictEnter(21), _ => OnPictEnter(22), _ => OnPictEnter(23),
            _ => OnPictEnter(24), _ => OnPictEnter(25), _ => OnPictEnter(26), _ => OnPictEnter(27),
            _ => OnPictEnter(28), _ => OnPictEnter(29), _ => OnPictEnter(30), _ => OnPictEnter(31),
        };
        static readonly Action<IntPtr>[] _pictExitHandlers = new Action<IntPtr>[]
        {
            _ => OnPictExit(0),  _ => OnPictExit(1),  _ => OnPictExit(2),  _ => OnPictExit(3),
            _ => OnPictExit(4),  _ => OnPictExit(5),  _ => OnPictExit(6),  _ => OnPictExit(7),
            _ => OnPictExit(8),  _ => OnPictExit(9),  _ => OnPictExit(10), _ => OnPictExit(11),
            _ => OnPictExit(12), _ => OnPictExit(13), _ => OnPictExit(14), _ => OnPictExit(15),
            _ => OnPictExit(16), _ => OnPictExit(17), _ => OnPictExit(18), _ => OnPictExit(19),
            _ => OnPictExit(20), _ => OnPictExit(21), _ => OnPictExit(22), _ => OnPictExit(23),
            _ => OnPictExit(24), _ => OnPictExit(25), _ => OnPictExit(26), _ => OnPictExit(27),
            _ => OnPictExit(28), _ => OnPictExit(29), _ => OnPictExit(30), _ => OnPictExit(31),
        };

        static RoguelikePackResultHook()
        {
            try
            {
                IL2Assembly asm = Assembler.GetAssembly("Assembly-CSharp");
                _vcClass = asm.GetClass("CardPackOpenResultViewController", "YgomGame.CardPack.OpenResult");
                _hook = new Hook<Del_OnCreatedView>(OnCreatedView, _vcClass.GetMethod("OnCreatedView"));
                IL2Class selBtn = asm.GetClass("SelectionButton", "YgomSystem.UI");
                _selectionButtonType = selBtn.IL2Typeof();
                _selBtnOnClick = selBtn.GetField("onClick");
                _selBtnInteractable = selBtn.GetProperty("interactable");
                _selBtnRemoveMouseDown = selBtn.GetMethod("RemoveClickShortCutMouseDown");
                _selBtnRemoveMouseRelease = selBtn.GetMethod("RemoveClickShortCutMouseRelease");
                _selBtnRemoveKeyDown = selBtn.GetMethod("RemoveClickShortCutKeyDown", x => x.GetParameters().Length == 1);
                _selBtnRemoveKeyRelease = selBtn.GetMethod("RemoveClickShortCutKeyRelease", x => x.GetParameters().Length == 1);
                _selBtnClick = selBtn.GetMethod("Click");
                IL2Assembly core = Assembler.GetAssembly("UnityEngine.CoreModule");
                IL2Class ue = core.GetClass("UnityEvent", "UnityEngine.Events");
                _ueAddListener = ue.GetMethod("AddListener");
                _ueRemoveAll = core.GetClass("UnityEventBase", "UnityEngine.Events").GetMethod("RemoveAllListeners");
                _bindingTextType = CastUtils.IL2Typeof("BindingTextMeshProUGUI", "YgomSystem.UI", "Assembly-CSharp");
                _drawDatasField = _vcClass.GetField("m_DrawDatas");
                IL2Assembly mscor = Assembler.GetAssembly("mscorlib");
                _objectClass = mscor.GetClass("Object", "System");
                // Dictionary<string, object> indexer (get_Item) — resolve via generic dict class.
                IL2Class stringClass = mscor.GetClass("String", "System");
                IL2Class dictGeneric = mscor.GetClass("Dictionary`2", "System.Collections.Generic")
                    .MakeGenericType(new IntPtr[] { stringClass.IL2Typeof(), _objectClass.IL2Typeof() });
                _dictItemGet = dictGeneric.GetProperty("Item").GetGetMethod();
                // Reused below to hand-build CardBrowser args with int32 / List<int> shapes.
                _int32Class = typeof(int).GetClass();
                _dictStrObjClass = dictGeneric;
                _dictStrObjCtor = dictGeneric.GetMethod(".ctor", m => m.GetParameters().Length == 0);
                _dictStrObjItemSet = dictGeneric.GetProperty("Item").GetSetMethod();
                _tmpTextType = Assembler.GetAssembly("Unity.TextMeshPro").GetClass("TMP_Text", "TMPro").IL2Typeof();
                IL2Class imageClass = Assembler.GetAssembly("UnityEngine.UI").GetClass("Image", "UnityEngine.UI");
                _imageType = imageClass.IL2Typeof();
                _imageSpriteSet = imageClass.GetProperty("sprite").GetSetMethod();
                IL2Class rectTf = core.GetClass("RectTransform", "UnityEngine");
                _rectTransformType = rectTf.IL2Typeof();
                _rtSetAnchorMin = rectTf.GetProperty("anchorMin").GetSetMethod();
                _rtSetAnchorMax = rectTf.GetProperty("anchorMax").GetSetMethod();
                _rtSetAnchoredPosition = rectTf.GetProperty("anchoredPosition").GetSetMethod();
                _rtSetPivot = rectTf.GetProperty("pivot").GetSetMethod();
                _rtSetSizeDelta = rectTf.GetProperty("sizeDelta").GetSetMethod();
                // PointerCallback (nested in SelectionButton) for hover tracking. We don't try
                // to wire onDown/onUp — neither fires for right-click in this VC's pipeline.
                _pointerCallbackClass = selBtn.GetNestedType("PointerCallback");
                _selBtnAddOnEnter = selBtn.GetMethod("add_onEnter");
                _selBtnAddOnExit = selBtn.GetMethod("add_onExit");
                // UnityEngine.Input.GetMouseButtonDown(int button) for polling right-click.
                IL2Class inputClass = Assembler.GetAssembly("UnityEngine.InputLegacyModule")
                    .GetClass("Input", "UnityEngine");
                _inputGetMouseButtonDown = inputClass.GetMethod("GetMouseButtonDown",
                    m => m.GetParameters().Length == 1 && m.GetParameters()[0].Type.Name == typeof(int).FullName);
                Console.WriteLine("[Roguelike] pack result hook installed: enter=" + (_selBtnAddOnEnter != null) +
                    " exit=" + (_selBtnAddOnExit != null) + " input=" + (_inputGetMouseButtonDown != null));
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] pack result hook init EX: " + ex); }
        }

        public static void EnsureRegistered() { /* trigger cctor */ }

        // Driven from RoguelikeMapScreen.Update once per frame. While we're in pick mode waiting
        // for the vanilla VC to instantiate PackCardGroupTemplate(Clone) -> PackCardTemplate(Clone)*
        // under Content, retry the enumeration. Wire on the first frame we find them and clear the
        // pending flag. Visual order matches server _cards order (we sorted m_DrawDatas in
        // OnCreatedView), so the visual click index doubles as the server pick index.
        public static void Poll()
        {
            if (!_wirePending || _pendingContent == IntPtr.Zero) return;
            _pictGos = new List<IntPtr>();
            EnumerateCardPicts(_pendingContent, _pictGos);
            if (_pictGos.Count == 0) return; // not populated yet, try next frame
            Console.WriteLine("[Roguelike] pack pick: wiring " + _pictGos.Count + " CardPicts (left=select, right=preview via hover+Input poll)");
            for (int i = 0; i < _pictGos.Count; i++)
            {
                IntPtr pict = _pictGos[i];
                if (i < MaxPickHandlers)
                {
                    // Left = select (replace native onClick with our toggle, neutralize esc/right
                    // shortcuts so they can't fire it).
                    WireButton(pict, _pictHandlers[i]);
                    StripShortCuts(pict);
                    // Hover tracking — Update poll reads _hoveredIdx + Input.GetMouseButtonDown(1)
                    // to open the CardBrowser.
                    WirePictPointer(pict, _selBtnAddOnEnter, _pictEnterHandlers[i]);
                    WirePictPointer(pict, _selBtnAddOnExit, _pictExitHandlers[i]);
                }
                else Console.WriteLine("[Roguelike] pack pick: card " + i + " > MaxPickHandlers (" + MaxPickHandlers + "), not selectable");
                SetSelectVisual(pict, false);
            }
            _hoveredIdx = -1;
            _wirePending = false;
        }

        // Attach a PointerCallback handler to one of SelectionButton's pointer events
        // (onEnter/onExit). The native pipeline raises these reliably on hover.
        static void WirePictPointer(IntPtr buttonGo, IL2Method addMethod, Action<IntPtr> handler)
        {
            IntPtr sel = GameObject.GetComponent(buttonGo, _selectionButtonType);
            if (sel == IntPtr.Zero || addMethod == null || _pointerCallbackClass == null) return;
            IntPtr cb = UnityEngine.Events._UnityAction.CreateDelegate(handler, IntPtr.Zero, _pointerCallbackClass);
            addMethod.Invoke(sel, new IntPtr[] { cb });
        }

        static void OnPictEnter(int idx) { _hoveredIdx = idx; }
        static void OnPictExit(int idx)  { if (_hoveredIdx == idx) _hoveredIdx = -1; }

        // Polled per-frame from RoguelikeMapScreen.Update via Poll(). When right-click
        // (mouse button 1) fires AND we're hovering a pict, open the CardBrowser. We use
        // UnityEngine.Input.GetMouseButtonDown because SelectionButton's pointer events
        // (onDown/onUp) only fire for the primary button.
        public static void PollRightClick()
        {
            if (_inputGetMouseButtonDown == null || _hoveredIdx < 0) return;
            int btn = 1;
            IL2Object r = _inputGetMouseButtonDown.Invoke(IntPtr.Zero, new IntPtr[] { new IntPtr(&btn) });
            bool pressed = r != null && r.GetValueRef<csbool>();
            if (!pressed) return;
            OpenCardBrowser(_hoveredIdx);
        }

        // Push CardBrowserViewController on top of the result VC, starting on the clicked card.
        // Args shape — captured from a vanilla open (see `vcpushlog`):
        //   { startIdx:int, mrks:List<int>, styleIds:List<int>, regulationId:int }
        // styleIds = 1 (CardStyleRarity.Normal) per card — 0 NREs in the native VC.
        // regulationId comes from the active roguelike run (pool's banlist).
        static void OpenCardBrowser(int startIdx)
        {
            try
            {
                if (_cidsByVisual == null || _cidsByVisual.Length == 0)
                {
                    Console.WriteLine("[Roguelike] OpenCardBrowser: no cids captured (ReorderDrawDatas didn't run?)");
                    return;
                }
                int regulationId = RoguelikeApi.RegulationId();
                IntPtr argsPtr = BuildCardBrowserArgs(startIdx, _cidsByVisual, regulationId);
                if (argsPtr == IntPtr.Zero) { Console.WriteLine("[Roguelike] OpenCardBrowser: BuildCardBrowserArgs failed"); return; }
                // CardBrowser is an overlay VC (lives on OverlayCanvas via DialogManager).
                IntPtr manager = YgomGame.Menu.DialogViewControllerManager.GetManager();
                if (manager == IntPtr.Zero) { Console.WriteLine("[Roguelike] OpenCardBrowser: no dialog manager"); return; }
                YgomSystem.UI.ViewControllerManager.PushChildViewControllerArgs(manager, "CardBrowser", argsPtr);
                Console.WriteLine("[Roguelike] OpenCardBrowser: pushed startIdx=" + startIdx + " mrks=" + _cidsByVisual.Length + " regulationId=" + regulationId);
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] OpenCardBrowser EX: " + ex); }
        }

        // Build CardBrowser args as a proper IL2CPP Dictionary<string, object> with int + List<int>
        // values (matching what vanilla pushes — see `vcpushlog` capture). Returns IntPtr.Zero on
        // failure. Doing it by hand because MiniJSON serialization widens int -> long and
        // arrays -> List<object>, which the native browser doesn't accept.
        static IntPtr BuildCardBrowserArgs(int startIdx, int[] cids, int regulationId)
        {
            if (_dictStrObjClass == null || _dictStrObjCtor == null || _dictStrObjItemSet == null || _int32Class == null) return IntPtr.Zero;
            // Dictionary<string, object> instance.
            IntPtr dict = Import.Object.il2cpp_object_new(_dictStrObjClass.ptr);
            _dictStrObjCtor.Invoke(dict);
            // Boxed Int32 for the two scalar fields.
            int siCopy = startIdx, riCopy = regulationId;
            IntPtr startIdxBoxed = Import.Object.il2cpp_value_box(_int32Class.ptr, new IntPtr(&siCopy));
            IntPtr regIdBoxed   = Import.Object.il2cpp_value_box(_int32Class.ptr, new IntPtr(&riCopy));
            // List<int> for mrks + styleIds.
            IL2ListExplicit mrksList = new IL2ListExplicit(IntPtr.Zero, _int32Class, true);
            IL2ListExplicit stylesList = new IL2ListExplicit(IntPtr.Zero, _int32Class, true);
            for (int i = 0; i < cids.Length; i++)
            {
                int cid = cids[i];
                mrksList.Add(new IntPtr(&cid));
                int style = 1;
                stylesList.Add(new IntPtr(&style));
            }
            // Set each key. set_Item(string, object) → key boxed as IL2String, value already a boxed object.
            DictSet(dict, "startIdx", startIdxBoxed);
            DictSet(dict, "mrks", mrksList.ptr);
            DictSet(dict, "styleIds", stylesList.ptr);
            DictSet(dict, "regulationId", regIdBoxed);
            return dict;
        }

        static void DictSet(IntPtr dict, string key, IntPtr boxedValue)
        {
            _dictStrObjItemSet.Invoke(dict, new IntPtr[] { new IL2String(key).ptr, boxedValue, _dictStrObjItemSet.ptr });
        }

        // Re-sort the VC's m_DrawDatas list (List<Dictionary<string, object>>) in place by
        // (rarity DESC, mrk ASC) so visual order == server _cards order. We treat the list as
        // an IL2ListExplicit of System.Object (the field's declared type), grab each entry's
        // raw IntPtr, read mrk/rarity via the Dictionary indexer, sort a parallel array of
        // (mrk, rarity, ptr), then write the ptrs back at the same positions.
        static void ReorderDrawDatas(IntPtr vcPtr)
        {
            if (_drawDatasField == null || _dictItemGet == null || _objectClass == null) return;
            IL2Object listObj = _drawDatasField.GetValue(vcPtr);
            if (listObj == null || listObj.ptr == IntPtr.Zero) return;
            IL2ListExplicit list = new IL2ListExplicit(listObj.ptr, _objectClass);
            int count = list.Count;
            if (count <= 1) return;
            int[] mrks = new int[count];
            int[] rars = new int[count];
            IntPtr[] ptrs = new IntPtr[count];
            for (int i = 0; i < count; i++)
            {
                ptrs[i] = list[i];
                mrks[i] = ReadDictInt(ptrs[i], "mrk");
                rars[i] = ReadDictInt(ptrs[i], "rarity");
            }
            int[] order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                if (rars[a] != rars[b]) return rars[b].CompareTo(rars[a]); // rarity DESC
                return mrks[a].CompareTo(mrks[b]);                          // mrk ASC tiebreaker
            });
            int[] cidsByVisual = new int[count];
            for (int i = 0; i < count; i++)
            {
                list[i] = ptrs[order[i]];
                cidsByVisual[i] = mrks[order[i]];
            }
            _cidsByVisual = cidsByVisual;
            Console.WriteLine("[Roguelike] m_DrawDatas reordered (" + count + " entries) rarity DESC + mrk ASC");
        }

        // Read an int field from an IL2 Dictionary<string, object> by key. "mrk"/"rarity" arrive
        // as Int64-boxed (System.Object); GetValueRef<long> + downcast handles that.
        static int ReadDictInt(IntPtr dictPtr, string key)
        {
            if (dictPtr == IntPtr.Zero) return 0;
            IL2Object v = _dictItemGet.Invoke(dictPtr, new IntPtr[] { new IL2String(key).ptr, _dictItemGet.ptr });
            if (v == null || v.ptr == IntPtr.Zero) return 0;
            // Boxed long (MiniJSON deserializes ints as long). Unbox to long, cast to int.
            try { return (int)v.GetValueRef<long>(); } catch { }
            try { return v.GetValueRef<int>(); } catch { }
            return 0;
        }

        static void OnCreatedView(IntPtr thisPtr)
        {
            // Gate by the global flow flag: only customize this VC while the player is
            // engaged in a roguelike (RoguelikeFlow.InRoguelike). Outside the run — shop,
            // menu, free-play — leave the result VC fully vanilla.
            bool ours = RoguelikeFlow.InRoguelike;
            if (ours)
            {
                try { ReorderDrawDatas(thisPtr); }
                catch (Exception ex) { Console.WriteLine("[Roguelike] pack result hook reorder EX: " + ex); }
                // Swap the global collection ($.Cards.have) for the run's owned-card map so the
                // result screen's per-card "you own N" counters reflect run state. Restored when
                // the user confirms via OnOkClick (which closes the VC). Mirrors the deck editor.
                try { SwapInRunCollection(); }
                catch (Exception ex) { Console.WriteLine("[Roguelike] pack result hook swap EX: " + ex); }
            }
            _hook.Original(thisPtr);
            if (!ours) return; // not our flow: untouched from here on too
            try
            {
                RoguelikeApi.PendingAction p = RoguelikeApi.GetPendingAction();
                if (p == null || p.Type != "openpack") return;
                Console.WriteLine("[Roguelike] pack result labels: mode=" + p.Mode +
                    " titlePick='" + p.TextLabels.TitlePick + "'" +
                    " titleKeep='" + p.TextLabels.TitleKeep + "'" +
                    " confirm='" + p.TextLabels.Confirm + "'");

                IntPtr go = Component.GetGameObject(thisPtr);

                // (1) Optional override of the "Cards obtidos" sub-label (UpperInfo>LabelRoot>LabelText).
                string title = p.Mode == "pick" ? p.TextLabels.TitlePick : p.TextLabels.TitleKeep;
                IntPtr upperInfo = GameObject.FindGameObjectByName(go, "UpperInfo");
                if (!string.IsNullOrEmpty(title))
                {
                    IntPtr subLabel = upperInfo != IntPtr.Zero
                        ? GameObject.FindGameObjectByPath(upperInfo, "LabelRoot.LabelText")
                        : IntPtr.Zero;
                    if (subLabel != IntPtr.Zero) SetBindingText(subLabel, null, title);
                }
                // (1b) Instructional sub-text: vanilla's "SendedPresentText" sibling under
                // UpperInfo is normally inactive — we hijack it for a right-click hint.
                // Server-configurable via labels.
                if (upperInfo != IntPtr.Zero && p.Mode == "pick")
                {
                    IntPtr instruction = GameObject.FindGameObjectByPath(upperInfo, "SendedPresentText");
                    if (instruction != IntPtr.Zero)
                    {
                        string hint = RoguelikeLabels.Get("pack.pick.hint", "Clique com o botão direito para ver detalhes da carta");
                        GameObject.SetActive(instruction, true);
                        SetBindingText(instruction, null, hint);
                    }
                }

                // (2) Wire OK button: only left-click can fire OnOkClick. Right-click and ESC
                //     are neutralized by removing the SelectionButton's mouse + key shortcut bindings.
                IntPtr okBtn = GameObject.FindGameObjectByName(go, "OKButton");
                _confirmTemplate = p.TextLabels.Confirm;
                if (okBtn != IntPtr.Zero)
                {
                    WireButton(okBtn, OnOkClick);
                    StripShortCuts(okBtn);
                }
                // (3) Default "Show owned count" toggle to ON — players usually want to see how
                // many of each card they already have in the run collection on the result screen.
                IntPtr ownedToggle = GameObject.FindGameObjectByPath(go,
                    "CardPackOpenResultUI(Clone).TitleSafeArea.TitleGroup.ShowOwnedNumToggle");
                if (ownedToggle != IntPtr.Zero) ClickButton(ownedToggle);

                if (p.Mode != "pick")
                {
                    // Keep mode: no placeholders meaningful; render confirm template as-is.
                    if (okBtn != IntPtr.Zero && !string.IsNullOrEmpty(_confirmTemplate))
                        SetBindingText(okBtn, "TextTMP", _confirmTemplate);
                    return;
                }

                _selected = new HashSet<int>();
                _pickMin = p.PickMin;
                _pickMax = p.PickMax;
                _okBtn = okBtn;
                // When min == 0 the player can confirm without selecting anything, so OK starts
                // enabled. Otherwise it stays disabled until they reach min picks.
                SetButtonInteractable(okBtn, _pickMin <= 0);
                UpdateOkLabel(initial: true);

                IntPtr obtainedRoot = GameObject.FindGameObjectByName(go, "ObtainedCardsRoot");
                _pendingContent = obtainedRoot != IntPtr.Zero
                    ? GameObject.FindGameObjectByPath(obtainedRoot, "CardAreaGroup.CardsScrollView.Viewport.Content")
                    : IntPtr.Zero;
                _pictGos = new List<IntPtr>();
                _wirePending = _pendingContent != IntPtr.Zero;
                if (!_wirePending) Console.WriteLine("[Roguelike] pack pick: no Content GO under ObtainedCardsRoot");
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] pack result hook EX: " + ex); }
        }

        // Only fires on legitimate left-click (right-click/ESC bindings were stripped).
        static void OnOkClick()
        {
            RoguelikeApi.PendingAction p = RoguelikeApi.GetPendingAction();
            if (p == null || p.Type != "openpack") return;
            int[] picks;
            if (p.Mode == "pick")
            {
                int count = _selected != null ? _selected.Count : 0;
                if (count < _pickMin || count > _pickMax) return; // gated; should not happen
                picks = _selected != null ? _selected.OrderBy(i => i).ToArray() : new int[0];
            }
            else
            {
                picks = Enumerable.Range(0, p.Size).ToArray();
            }
            Console.WriteLine("[Roguelike] OK clicked, finalize (" + p.Mode + ") picks=[" + string.Join(",", picks) + "]");
            // Restore the player's real collection before sending picks — the response will refresh
            // $.Cards.have with the server's updated counts anyway, but this keeps any UI that polls
            // ClientWork during the brief gap (animations / focus changes) showing the right data.
            try { RestorePlayerCollection(); } catch (Exception ex) { Console.WriteLine("[Roguelike] restore EX: " + ex); }
            RoguelikeApi.ActionRespondPicks(p.Token, picks);
        }

        // Swap $.Cards.have to the run's owned-card map so the result screen's per-card "owned"
        // counters reflect run state (mirrors RoguelikeDeckEditScreen). _savedHave holds the
        // original; restored on OK.
        static void SwapInRunCollection()
        {
            string runCards = YgomSystem.Utility.ClientWork.SerializePath("Roguelike.Cards");
            if (string.IsNullOrEmpty(runCards) || runCards == "{}") return;
            _savedHave = YgomSystem.Utility.ClientWork.SerializePath("$.Cards.have");
            YgomSystem.Utility.ClientWork.DeleteByJsonPath("Cards.have");
            YgomSystem.Utility.ClientWork.UpdateJson("{\"Cards\":{\"have\":" + runCards + "}}");
        }

        static void RestorePlayerCollection()
        {
            if (_savedHave == null) return;
            YgomSystem.Utility.ClientWork.DeleteByJsonPath("Cards.have");
            if (_savedHave != "" && _savedHave != "{}")
                YgomSystem.Utility.ClientWork.UpdateJson("{\"Cards\":{\"have\":" + _savedHave + "}}");
            _savedHave = null;
        }

        // Programmatically click a SelectionButton (used to flip the show-owned toggle to ON
        // on result open).
        static void ClickButton(IntPtr buttonGo)
        {
            IntPtr sel = GameObject.GetComponent(buttonGo, _selectionButtonType);
            if (sel == IntPtr.Zero || _selBtnClick == null) return;
            _selBtnClick.Invoke(sel);
        }

        // Remove all mouse + key shortcut bindings on a SelectionButton so right-click,
        // ESC, and gamepad-B can't trigger its onClick.
        static void StripShortCuts(IntPtr buttonGo)
        {
            IntPtr sel = GameObject.GetComponent(buttonGo, _selectionButtonType);
            if (sel == IntPtr.Zero || _selBtnRemoveMouseDown == null) return;
            for (int mt = 0; mt < 8; mt++)
            {
                int v = mt;
                _selBtnRemoveMouseDown.Invoke(sel, new IntPtr[] { new IntPtr(&v) });
                _selBtnRemoveMouseRelease.Invoke(sel, new IntPtr[] { new IntPtr(&v) });
            }
            for (int kt = 0; kt < 64; kt++)
            {
                int v = kt;
                if (_selBtnRemoveKeyDown != null) _selBtnRemoveKeyDown.Invoke(sel, new IntPtr[] { new IntPtr(&v) });
                if (_selBtnRemoveKeyRelease != null) _selBtnRemoveKeyRelease.Invoke(sel, new IntPtr[] { new IntPtr(&v) });
            }
        }

        // Walk Content > PackCardGroupTemplate(Clone) > PackCardTemplate(Clone) > CardPict and collect
        // the CardPict GameObjects in order. Skip inactive template prefabs (`...Template` without
        // `(Clone)` suffix) — vanilla keeps them parented under Content as hidden source nodes.
        //
        // Type discipline: Transform.GetChild returns a Transform, NOT a GameObject. Never pass it
        // back through GameObject.GetTransform — that invokes GameObject.transform's getter on a
        // Transform pointer (UB in IL2CPP → crash). Use Component.GetGameObject to hop from Transform
        // to its owning GameObject.
        // Walk Content > PackCardGroupTemplate(Clone) > PackCardTemplate(Clone) > CardPict and
        // collect the active CardPict GameObjects. Skips: inactive template prefabs (no "(Clone)"
        // suffix), the SecretPulldownRoot sibling inside the group, and inactive PackCardTemplates
        // (the vanilla pool keeps spares inactive). Quiet on retry — only logs once we find > 0.
        static void EnumerateCardPicts(IntPtr content, List<IntPtr> outList)
        {
            if (content == IntPtr.Zero) return;
            IntPtr contentTf = GameObject.GetTransform(content);
            if (contentTf == IntPtr.Zero) return;
            int n = Transform.GetChildCount(contentTf);
            for (int i = 0; i < n; i++)
            {
                IntPtr groupTf = Transform.GetChild(contentTf, i);
                if (groupTf == IntPtr.Zero) continue;
                IntPtr groupGo = Component.GetGameObject(groupTf);
                if (groupGo == IntPtr.Zero) continue;
                string groupName = UnityObject.GetName(groupGo);
                // Only the cloned group holds the cards; the template prefab has no children of interest.
                if (!groupName.StartsWith("PackCardGroupTemplate") || !groupName.Contains("(Clone)")) continue;
                if (!GameObject.IsActive(groupGo)) continue;

                int m = Transform.GetChildCount(groupTf);
                for (int j = 0; j < m; j++)
                {
                    IntPtr cardTf = Transform.GetChild(groupTf, j);
                    if (cardTf == IntPtr.Zero) continue;
                    IntPtr cardGo = Component.GetGameObject(cardTf);
                    if (cardGo == IntPtr.Zero) continue;
                    string cardName = UnityObject.GetName(cardGo);
                    // Filter out SecretPulldownRoot (sibling under the group) + the inactive template.
                    if (!cardName.StartsWith("PackCardTemplate") || !cardName.Contains("(Clone)")) continue;
                    if (!GameObject.IsActive(cardGo)) continue;
                    IntPtr pict = GameObject.FindGameObjectByPath(cardGo, "CardPict");
                    if (pict != IntPtr.Zero) outList.Add(pict);
                }
            }
        }

        static void ToggleCard(int idx)
        {
            // idx is both the visual click index and the server _cards index — m_DrawDatas
            // was reordered in OnCreatedView to match server order before the clones were built.
            if (idx < 0 || _pictGos == null || idx >= _pictGos.Count) return;
            if (_selected.Contains(idx))
            {
                _selected.Remove(idx);
                SetSelectVisual(_pictGos[idx], false);
            }
            else
            {
                if (_selected.Count >= _pickMax) return; // ignore clicks above the max
                _selected.Add(idx);
                SetSelectVisual(_pictGos[idx], true);
            }
            // OK enables anywhere in the [min, max] range; the click handler enforces the upper
            // bound and selection guards the lower bound.
            int count = _selected.Count;
            SetButtonInteractable(_okBtn, count >= _pickMin && count <= _pickMax);
            UpdateOkLabel();
        }

        // Format _confirmTemplate with the live selection counter and push it to the OK button's
        // text. Placeholders: {0}=count, {1}=max, {2}=min. Existing templates that only used
        // {0}/{1} keep working unchanged (when min==max, {1} reads the same value the old
        // "required" used). No-op if no template.
        //
        // initial=true happens once in OnCreatedView: we route through SetBindingText (sets
        // TextId with the HackID prefix) so the BindingTextMeshProUGUI Start() that follows
        // picks up our string instead of clobbering it back to "OK" on first frame.
        // initial=false (toggles) writes straight to TMP_Text.text — the binding's setter
        // doesn't re-render on repeated assignments after the first one.
        static void UpdateOkLabel(bool initial = false)
        {
            if (_okBtn == IntPtr.Zero || string.IsNullOrEmpty(_confirmTemplate)) return;
            int count = _selected != null ? _selected.Count : 0;
            string label;
            try { label = string.Format(_confirmTemplate, count, _pickMax, _pickMin); }
            catch { label = _confirmTemplate; } // template had no placeholders
            if (initial)
            {
                SetBindingText(_okBtn, "TextTMP", label);
                return;
            }
            IntPtr textGo = GameObject.FindGameObjectByPath(_okBtn, "TextTMP");
            if (textGo == IntPtr.Zero) return;
            IntPtr tmp = GameObject.GetComponent(textGo, _tmpTextType);
            if (tmp != IntPtr.Zero) TMPro.TMP_Text.SetText(tmp, label);
        }

        static void SetSelectVisual(IntPtr pict, bool on)
        {
            IntPtr pcCursor = GameObject.FindGameObjectByPath(pict, "SelectCursorCardForPC");
            if (pcCursor != IntPtr.Zero) GameObject.SetActive(pcCursor, on);
            IntPtr consoleCursor = GameObject.FindGameObjectByPath(pict, "SelectCursorForConsole");
            if (consoleCursor != IntPtr.Zero) GameObject.SetActive(consoleCursor, on);
            // Highlight + LimitIcon-as-checkmark live one level up under PackCardTemplate(Clone).
            IntPtr cardTpl = UnityEngine.GameObject.GetTransform(pict);
            if (cardTpl != IntPtr.Zero)
            {
                IntPtr cardTplGo = UnityEngine.Component.GetGameObject(
                    UnityEngine.Transform.GetParent(cardTpl));
                if (cardTplGo != IntPtr.Zero)
                {
                    IntPtr highlight = GameObject.FindGameObjectByPath(cardTplGo, "Highlight");
                    if (highlight != IntPtr.Zero) GameObject.SetActive(highlight, on);
                    IntPtr limitIcon = GameObject.FindGameObjectByPath(cardTplGo, "LimitIcon");
                    if (limitIcon != IntPtr.Zero)
                    {
                        GameObject.SetActive(limitIcon, on);
                        if (on) SetCheckmarkSprite(limitIcon);
                    }
                }
            }
        }

        // Swap LimitIcon's Image.sprite for the vanilla checkmark sprite the first time we need
        // it; subsequent picks reuse the cached reference. Falls back silently if the sprite
        // isn't loaded yet (the limit icon stays empty but still indicates selection via the
        // GameObject being active). Also recenters the icon over the card (vanilla anchors it
        // at the corner) so the checkmark reads as "selected this card", not "limit reached".
        static void SetCheckmarkSprite(IntPtr limitIcon)
        {
            if (_imageSpriteSet == null) return;
            if (_checkOnSprite == IntPtr.Zero)
                _checkOnSprite = YgoMasterClient.RoguelikeMapScreen.FindIcon("GUI_CommonButtonToggleM_CheckOn");
            if (_checkOnSprite == IntPtr.Zero) return;
            IntPtr img = GameObject.GetComponent(limitIcon, _imageType);
            if (img == IntPtr.Zero) return;
            _imageSpriteSet.Invoke(img, new IntPtr[] { _checkOnSprite });
            CenterRect(limitIcon);
        }

        // Roughly half the visible card art; tweak if the checkmark feels too big or too small.
        const float CheckmarkSize = 70f;

        // Recenter + resize a RectTransform over its parent: anchors + pivot at (0.5, 0.5),
        // zero anchoredPosition, sizeDelta = CheckmarkSize square. Idempotent.
        static void CenterRect(IntPtr go)
        {
            if (_rectTransformType == IntPtr.Zero) return;
            IntPtr rt = GameObject.GetComponent(go, _rectTransformType);
            if (rt == IntPtr.Zero) return;
            AssetHelper.Vector2 half = new AssetHelper.Vector2(0.5f, 0.5f);
            AssetHelper.Vector2 zero = new AssetHelper.Vector2(0f, 0f);
            AssetHelper.Vector2 size = new AssetHelper.Vector2(CheckmarkSize, CheckmarkSize);
            _rtSetAnchorMin.Invoke(rt, new IntPtr[] { new IntPtr(&half) });
            _rtSetAnchorMax.Invoke(rt, new IntPtr[] { new IntPtr(&half) });
            _rtSetPivot.Invoke(rt, new IntPtr[] { new IntPtr(&half) });
            _rtSetAnchoredPosition.Invoke(rt, new IntPtr[] { new IntPtr(&zero) });
            _rtSetSizeDelta.Invoke(rt, new IntPtr[] { new IntPtr(&size) });
        }

        static void SetButtonInteractable(IntPtr btn, bool on)
        {
            IntPtr sel = GameObject.GetComponent(btn, _selectionButtonType);
            if (sel == IntPtr.Zero || _selBtnInteractable == null) return;
            csbool v = on;
            _selBtnInteractable.GetSetMethod().Invoke(sel, new IntPtr[] { new IntPtr(&v) });
        }

        // Replace ALL existing listeners with `action`. Native onClick lambda is removed -
        // ours is the only handler, and shortcut bindings are stripped so only left-click fires it.
        static void WireButton(IntPtr buttonGo, Action action)
        {
            IntPtr sel = GameObject.GetComponent(buttonGo, _selectionButtonType);
            if (sel == IntPtr.Zero) return;
            IL2Object onClick = _selBtnOnClick.GetValue(sel);
            if (onClick == null) return;
            if (_ueRemoveAll != null) _ueRemoveAll.Invoke(onClick.ptr);
            IntPtr cb = UnityEngine.Events._UnityAction.CreateUnityAction(action);
            _ueAddListener.Invoke(onClick.ptr, new IntPtr[] { cb });
        }

        static void SetBindingText(IntPtr root, string path, string text)
        {
            IntPtr o = string.IsNullOrEmpty(path) ? root : GameObject.FindGameObjectByPath(root, path);
            if (o == IntPtr.Zero) return;
            IntPtr binding = GameObject.GetComponent(o, _bindingTextType);
            if (binding != IntPtr.Zero) YgomSystem.UI.BindingTextMeshProUGUI.SetTextId(binding, text);
        }
    }
}
