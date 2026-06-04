namespace YgoMasterClient
{
    // Ignition gate for the command menu. The engine's "can this card activate an effect now?" check
    // (FUN_18058d230, hooked in DuelDll) calls this, so a field card that matches a relic ignition{} offers
    // "Activate Effect" in the player's Main Phase. The activation itself is intercepted in DLL_DuelComDoCommand.
    static class RoguelikeIgnition
    {
        // From the FUN_18058d230 detour. If the engine already allows it, leave it untouched; otherwise allow it
        // when a registered ignition matches the card in (player, zone).
        public static uint ForceCanActivate(uint player, uint zone, uint original)
        {
            if (original != 0) return original;
            return RoguelikeLua.CanIgnite((int)player, (int)zone) ? 1u : 0u;
        }
    }
}
