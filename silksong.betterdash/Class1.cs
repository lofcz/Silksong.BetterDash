using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace silksong.betterdash
{
    [BepInPlugin("lofcz.BetterDash", "BetterDash", "1.1.5")]
    public class DashIFramesPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private Harmony _harmony;
        
        internal static ConfigEntry<bool> CEnabled;
        internal static ConfigEntry<float> CDashIFrameSeconds;
        internal static ConfigEntry<float> CDashSpeedMult;
        internal static ConfigEntry<float> CControlRegainDelay;
        internal static ConfigEntry<bool> CEnableTrail;
        internal static ConfigEntry<float> CDashTrailLength;
        internal static ConfigEntry<bool> CVerbose;
        
        private static DashController dashController;
        private static readonly Dictionary<int, float> OriginalDashSpeeds = new Dictionary<int, float>();
        internal static bool ControlRegaining;
        internal static bool need_regain_ctl_flag;

        private void Awake()
        {
            Log = Logger;
            CEnabled = Config.Bind("General", "Enabled", true, "Enable this mod");
            CDashIFrameSeconds = Config.Bind("General", "DashIFrame", 0.2f, new ConfigDescription("Dash I-Frames duration (seconds)", new AcceptableValueRange<float>(0.2f, 10f)));
            //DashFullInvincible = Config.Bind("General", "EnableDashFullInvincible", false, "Enable full invincibility during dash"); // causes flashing
            CDashSpeedMult = Config.Bind("General", "DashSpeedMultiplier", 1f, "Dash speed multiplier (>=1)");
            CControlRegainDelay = Config.Bind("General", "ControlRegainDelay", 0.3f, "Delay before regaining control after being hit (seconds)");
            CEnableTrail = Config.Bind("Visual", "EnableDashTrail", true, "Enable dash trail");
            CDashTrailLength = Config.Bind("Visual", "DashTrailLength", 2f, "Dash trail length (seconds)");
            CVerbose = Config.Bind("Debug", "VerboseLog", false, "Enable verbose logging");

            _harmony = new Harmony("lofcz.BetterDash");
            _harmony.PatchAll();

            GameObject go = new GameObject("DashIFramesController");
            dashController = go.AddComponent<DashController>();
            DontDestroyOnLoad(go);

            Log.LogInfo("[DashIFrames] Loaded.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        internal static void Verbose(string msg)
        {
            if (CVerbose.Value)
            {
                Log.LogInfo("[DashIFrames] " + msg);
            }
        }

        private static void EnsureDashSpeed(HeroController hc)
        {
            if (!CEnabled.Value) return;
            try
            {
                FieldInfo fi = AccessTools.Field(hc.GetType(), "DASH_SPEED");
                if (fi == null) return;
                int id = hc.GetInstanceID();
                if (!OriginalDashSpeeds.TryGetValue(id, out float origVal))
                {
                    float orig = (float)fi.GetValue(hc);
                    OriginalDashSpeeds[id] = orig;
                    fi.SetValue(hc, orig * CDashSpeedMult.Value);
                }
                else
                {
                    fi.SetValue(hc, origVal * CDashSpeedMult.Value);
                }
            }
            catch (System.Exception ex)
            {
                Log.LogWarning($"[DashIFrames] EnsureDashSpeed failed: {ex}");
            }
        }

        [HarmonyPatch(typeof(HeroController), "Dash")]
        private static class Dash_Patch
        {
            private static void Prefix(HeroController __instance)
            {
                if (CEnabled.Value)
                {
                    EnsureDashSpeed(__instance);
                    dashController.StartDashRoutine(__instance);
                    if (CEnableTrail.Value)
                    {
                        dashController.ShowTrail(CDashTrailLength.Value);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(HeroController), "FinishedDashing")]
        private static class FinishedDashing_Patch
        {
            private static void Postfix()
            {
                dashController.ResetTrail();
            }
        }

        [HarmonyPatch(typeof(PlayerData), "TakeHealth")]
        private static class TakeHealthPost_Patches
        {
            private static void Postfix()
            {
                if (CEnabled.Value)
                {
                    dashController.DelayedRegainControl(HeroController.instance, CControlRegainDelay.Value);
                }
            }
        }

        private class DashController : MonoBehaviour
        {
            private TrailRenderer dashTrail;
            private Coroutine iframeRoutine;

            public void StartDashRoutine(HeroController hc)
            {
                if (CEnabled.Value)
                {
                    if (iframeRoutine != null)
                    {
                        StopCoroutine(iframeRoutine);
                    }
                    iframeRoutine = StartCoroutine(IFramesRoutine(hc));
                }
            }
            
            private IEnumerator IFramesRoutine(HeroController hc)
            {
                CheatManager.Invincibility = CheatManager.InvincibilityStates.FullInvincible;
                need_regain_ctl_flag = true;
                Verbose("I-Frames START (No Flash)");

                yield return new WaitForSeconds(CDashIFrameSeconds.Value);
                
                CheatManager.Invincibility = CheatManager.InvincibilityStates.Off;
                Verbose("I-Frames END");
                
                iframeRoutine = null;
            }
            
            public void DelayedRegainControl(HeroController hc, float delay)
            {
                if (!ControlRegaining && need_regain_ctl_flag)
                {
                    StartCoroutine(DelayedRegainControlRoutine(hc, delay));
                }
            }

            private static IEnumerator DelayedRegainControlRoutine(HeroController hc, float delay)
            {
                ControlRegaining = true;
                Verbose($"Regaining control in {delay:0.00} seconds");
                yield return new WaitForSeconds(delay);
                hc.RegainControl();
                hc.StartAnimationControl();
                hc.CancelAttack();
                Verbose("Control regained");
                need_regain_ctl_flag = false;
                ControlRegaining = false;
            }

            public void ShowTrail(float length)
            {
                if (dashTrail == null)
                {
                    HeroController heroController = FindFirstObjectByType<HeroController>();
                    if (heroController == null) return;
                    
                    GameObject go = new GameObject("DashTrail");
                    go.transform.SetParent(heroController.transform, false);
                    dashTrail = go.AddComponent<TrailRenderer>();
                    dashTrail.startWidth = 0.4f;
                    dashTrail.endWidth = 0.2f;
                    dashTrail.minVertexDistance = 0.01f;
                    dashTrail.autodestruct = false;
                    dashTrail.material = new Material(Shader.Find("Sprites/Default")) { color = Color.black };
                    dashTrail.startColor = new Color(0f, 0f, 0f, 0.9f);
                    dashTrail.endColor = new Color(0f, 0f, 0f, 0f);
                }
                dashTrail.time = length;
                dashTrail.Clear();
                dashTrail.emitting = true;
            }

            public void ResetTrail()
            {
                if (dashTrail != null)
                {
                    dashTrail.Clear();
                    dashTrail.emitting = false;
                }
            }
        }
    }
}