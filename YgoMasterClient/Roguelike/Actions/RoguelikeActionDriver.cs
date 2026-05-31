using System;
using System.Collections.Generic;

namespace YgoMasterClient
{
    // Single dispatcher for the server's pending action ($.Roguelike.action). Per type:
    //   options  -> ActionSheet (title header + body message, N labels; non-cancelable)
    //   message  -> CommonDialog confirmation (single OK; non-cancelable)
    //   openpack -> push the native CardPackOpen VC; the result hook (RoguelikePackResultHook)
    //               wires OK to ActionRespondPicks once the player confirms picks.
    // Token dedup keeps a prompt from re-opening; pumped from RoguelikeMapScreen.Update so it
    // only fires while the map is visible.
    static class RoguelikeActionDriver
    {
        static int _shownToken = -1;
        // Token of the prompt currently dispatched — captured to a static field because IL2CPP
        // delegate marshalling can't see closure-captured locals (the instance-method `this` of
        // a closure display class isn't propagated; only methods with no capture work).
        // OnOptionSelected/OnMessageConfirm read this on click.
        static int _activeToken = -1;
        static readonly Action<IntPtr, int> _onSelect = OnOptionSelected;
        static readonly Action _onMessageConfirm = OnMessageConfirm;

        public static void Pump()
        {
            try
            {
                RoguelikeApi.PendingAction p = RoguelikeApi.GetPendingAction();
                if (p == null) { _shownToken = -1; return; }
                if (p.Token == _shownToken) return; // already shown
                _shownToken = p.Token;
                _activeToken = p.Token;
                Console.WriteLine("[Roguelike] action pump: type=" + p.Type + " token=" + p.Token);

                if (p.Type == "message")
                {
                    YgomGame.Menu.CommonDialogViewController.OpenConfirmationDialog(
                        p.Title, p.Message, RoguelikeLabels.Get("common.ok", "OK"),
                        _onMessageConfirm, null, false);
                }
                else if (p.Type == "options")
                {
                    YgomGame.Menu.ActionSheetViewController.OpenCustomSheet(p.Title, p.Message, p.Options ?? new string[0], _onSelect, true);
                }
                else if (p.Type == "openpack")
                {
                    IntPtr manager = YgomGame.Menu.ContentViewControllerManager.GetManager();
                    if (manager == IntPtr.Zero) { _shownToken = -1; return; }
                    IntPtr args = YgomMiniJSON.Json.Deserialize(MiniJSON.Json.Serialize(
                        new Dictionary<string, object> { { "ForwardResultArgs", null } }));
                    YgomSystem.UI.ViewControllerManager.PushChildViewControllerArgs(manager, "CardPack/CardPackOpen", args);
                }
                else if (p.Type == "addCard")
                {
                    // Cards already applied server-side; play the fly animation, which acks the token
                    // when the last card lands (advancing to the next action).
                    RoguelikeMapScreen.PlayAddCard(p.Cards, p.Token);
                }
                else
                {
                    Console.WriteLine("[Roguelike] action pump: unknown type '" + p.Type + "'");
                }
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] action pump EX: " + ex); }
        }

        static void OnMessageConfirm() { RoguelikeApi.ActionRespond(_activeToken); }

        static void OnOptionSelected(IntPtr thisPtr, int index)
        {
            if (index >= 0) RoguelikeApi.ActionRespondChoice(_activeToken, index);
        }
    }
}
