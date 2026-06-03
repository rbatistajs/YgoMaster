using System;
using System.Collections.Generic;

namespace YgoMasterClient
{
    // Bridges a server-built duel into the Solo production flow WITHOUT the RoomCreate machinery.
    // A roguelike combat has no Solo chapter, so the server can't build the duel from a chapter id;
    // instead it ships the full DuelSettings as duelStarterData. This holds that data, drives the
    // production straight past turn-select to the duel, and injects it into the outgoing Duel.begin
    // (the custom-duel path the server already understands). Mirrors what IsHacked does for the
    // Room flow, but roguelike-owned and gated on its own flag.
    static class RoguelikeDuel
    {
        public static bool Active;
        public static int FirstPlayer = -1;
        static Dictionary<string, object> _starterData;

        // Capture the queued duel from the move response ($.Duel.duelStarterData) before the
        // production mutates $.Duel. Returns false when no duel was queued.
        public static bool Arm()
        {
            try
            {
                string json = YgomSystem.Utility.ClientWork.SerializePath("Duel.duelStarterData");
                if (string.IsNullOrEmpty(json)) { Console.WriteLine("[Roguelike] Arm: no duelStarterData in $.Duel"); return false; }
                _starterData = MiniJSON.Json.Deserialize(json) as Dictionary<string, object>;
                if (_starterData == null) { Console.WriteLine("[Roguelike] Arm: duelStarterData parse failed"); return false; }
                FirstPlayer = _starterData.ContainsKey("FirstPlayer") ? Convert.ToInt32(_starterData["FirstPlayer"]) : -1;
                Active = true;
                Console.WriteLine("[Roguelike] duel armed (FirstPlayer=" + FirstPlayer + ")");
                return true;
            }
            catch (Exception ex) { Console.WriteLine("[Roguelike] Arm EX: " + ex.Message); return false; }
        }

        public static void Disarm() { Active = false; _starterData = null; FirstPlayer = -1; }

        // Insert our settings into the Duel.begin rule so the server builds the custom duel.
        public static void Inject(Dictionary<string, object> rule)
        {
            if (_starterData != null)
            {
                rule["duelStarterData"] = _starterData;
                Console.WriteLine("[Roguelike] injected duelStarterData into Duel.begin");
            }
        }
    }
}
