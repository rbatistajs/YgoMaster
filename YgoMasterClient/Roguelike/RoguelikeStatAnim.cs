using System;
using System.Collections.Generic;
using IL2CPP;
using UnityEngine;

namespace YgoMasterClient
{
    // Animation primitives for run stat changes (LP / gold). Driven by RoguelikeMapScreen.Update:
    //   1. Diff detects a change -> spawn FloatingDelta + create a deferred HudCounter
    //   2. FloatingDelta slides + fades (alpha via Color) + shrinks; on T=1 -> activates HudCounter
    //   3. HudCounter lerps the HUD TMP integer from old value to new
    // Multiple in-flight floaters coexist; a HudCounter for the same label is replaced
    // (From = current displayed value, T = 0) so the tween continues from what's on screen.
    static unsafe class RoguelikeStatAnim
    {
        public const float FloatDuration = 0.7f;
        public const float CounterDuration = 0.5f;
        // RGB only — alpha is animated per-frame for the fade.
        public const float PosR = 0.290f, PosG = 0.871f, PosB = 0.502f; // #4ADE80 green
        public const float NegR = 0.973f, NegG = 0.443f, NegB = 0.443f; // #F87171 red

        public class FloatingDelta
        {
            public IntPtr Go;            // TMP clone parented under the map canvas root
            public Vector3 StartPos;     // canvas center + per-stat Y offset
            public Vector3 EndPos;       // HUD label anchored pos
            public float T;
            public float R, G, B;        // base color (sign-tinted)
            public HudCounter Pending;   // deferred — activated when this floater lands
        }

        public class HudCounter
        {
            public string LabelPath;
            public int From;
            public int To;
            public float T;
            public bool Active;          // false until the matching FloatingDelta finishes
            public Func<int, string> Format;
        }

        // Linear interp — no Mathf wrapper exists in this project.
        public static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        // EaseOutCubic: fast start, slow settle — matches the "gain/loss" feel.
        public static float EaseOutCubic(float t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            float u = 1f - t;
            return 1f - u * u * u;
        }

        // Advance every floater: lerp pos + color alpha + scale; on T>=1 destroy the GO and
        // arm its deferred HudCounter.
        public static void TickFloaters(List<FloatingDelta> floaters, float dt,
                                        IntPtr tmpType, IL2Property anchoredPos3D)
        {
            for (int i = floaters.Count - 1; i >= 0; i--)
            {
                FloatingDelta f = floaters[i];
                f.T += dt / FloatDuration;
                bool done = f.T >= 1f;
                float t = done ? 1f : f.T;
                float e = EaseOutCubic(t);
                Vector3 pos = new Vector3(
                    Lerp(f.StartPos.x, f.EndPos.x, e),
                    Lerp(f.StartPos.y, f.EndPos.y, e),
                    Lerp(f.StartPos.z, f.EndPos.z, e));
                IntPtr ct = GameObject.GetTransform(f.Go);
                anchoredPos3D.GetSetMethod().Invoke(ct, new IntPtr[] { new IntPtr(&pos) });
                float scale = Lerp(1.4f, 1.0f, e);
                Transform.SetLocalScale(ct, new Vector3(scale, scale, scale));
                IntPtr tmp = GameObject.GetComponent(f.Go, tmpType);
                if (tmp != IntPtr.Zero) TMPro.TMP_Text.SetColor(tmp, f.R, f.G, f.B, 1f - t);
                if (done)
                {
                    UnityObject.Destroy(f.Go);
                    if (f.Pending != null) f.Pending.Active = true;
                    floaters.RemoveAt(i);
                }
            }
        }

        // Advance active HUD counters: lerp From->To, write the formatted TMP text, snap + drop on T>=1.
        public static void TickCounters(List<HudCounter> counters, float dt,
                                        Action<string, string> setTmpText)
        {
            for (int i = counters.Count - 1; i >= 0; i--)
            {
                HudCounter c = counters[i];
                if (!c.Active) continue;
                c.T += dt / CounterDuration;
                bool done = c.T >= 1f;
                int cur = done ? c.To
                    : (int)Math.Round(Lerp(c.From, c.To, EaseOutCubic(c.T)));
                setTmpText(c.LabelPath, c.Format(cur));
                if (done) counters.RemoveAt(i);
            }
        }
    }
}
