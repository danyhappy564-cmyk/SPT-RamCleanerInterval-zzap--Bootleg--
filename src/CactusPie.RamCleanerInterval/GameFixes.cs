using System;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using EFT.Animations;
using HarmonyLib;

namespace CactusPie.RamCleanerInterval
{
    /// <summary>
    /// "19. Game memory fixes": three spots where the game itself keeps memory it no longer needs, or makes garbage every
    /// frame for nothing. Each one keeps the game's behaviour. Patched once at start; each patch checks its setting, so
    /// switching one off in F12 takes effect at once. A patch whose target is missing (another game build) is skipped and
    /// logged, the others still work. Ideas from Smoothie (TominoCZ), written anew and checked against the 4.1 client.
    /// </summary>
    internal static class GameFixes
    {
        internal static ConfigEntry<bool> BotSpawnData;
        internal static ConfigEntry<bool> DeadBrains;
        internal static ConfigEntry<bool> BotCoroutine;

        private static ManualLogSource s_log;

        internal static void Apply(Harmony harmony, ManualLogSource log)
        {
            s_log = log;
            Patch(harmony, "bot spawn data after raid", AccessTools.Method(typeof(GlobalEventDispatcher), nameof(GlobalEventDispatcher.Stop)),
                postfix: nameof(DispatcherStopPostfix));
            Patch(harmony, "dead bot brains", AccessTools.Method(typeof(AICoreController), nameof(AICoreController.Update)),
                prefix: nameof(AiUpdatePrefix));
            Patch(harmony, "per-bot coroutine", AccessTools.Method(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.StartFovCoroutine)),
                prefix: nameof(FovCoroutinePrefix));
        }

        private static void Patch(Harmony harmony, string name, MethodInfo target, string prefix = null, string postfix = null)
        {
            try
            {
                if (target == null)
                {
                    throw new MissingMethodException("target not found");
                }

                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(GameFixes), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(GameFixes), postfix));
                s_log.LogInfo($"[fix] {name}: on");
            }
            catch (Exception ex)
            {
                s_log.LogWarning($"[fix] {name}: skipped, the game code is not as expected ({ex.Message})");
            }
        }

        private static void Failed(ref bool logged, string name, Exception ex)
        {
            if (logged)
            {
                return;
            }

            logged = true;
            s_log.LogWarning($"[fix] {name} failed (logged once, the game carries on as without the fix): {ex}");
        }

        // ---------------------------------------------------------------- bot spawn data after raid

        // Every spawn attempt (BotCreationData, with its bots' whole profiles) and every raid's SpawnDelaysService subscribe
        // to GlobalEventDispatcher.OnStopBotSpawn. The dispatcher lives as long as the game, nothing in the game calls
        // StopBotSpawn(), and its Stop() at raid end (BotsController.Stop) clears all its other events but not this one:
        // every raid's spawn data stayed referenced until the game was closed. Cleared at that Stop() - after the raid,
        // so nothing in a raid changes.
        private static readonly AccessTools.FieldRef<GlobalEventDispatcher, Action> StopBotSpawnEvent =
            AccessTools.FieldRefAccess<GlobalEventDispatcher, Action>("_onStopBotSpawn");

        private static bool s_spawnFailed;

        private static void DispatcherStopPostfix(GlobalEventDispatcher __instance)
        {
            try
            {
                if (BotSpawnData == null || !BotSpawnData.Value)
                {
                    return;
                }

                Action pending = StopBotSpawnEvent(__instance);
                if (pending == null)
                {
                    return;
                }

                int count = pending.GetInvocationList().Length;
                StopBotSpawnEvent(__instance) = null;
                s_log.LogInfo($"[fix] raid end: let go of {count} spawn records the game kept");
            }
            catch (Exception ex)
            {
                Failed(ref s_spawnFailed, "bot spawn data", ex);
            }
        }

        // ---------------------------------------------------------------- dead bot brains

        // AICoreController (one per raid) takes the dead bots' brains out of its agent list every frame but never empties
        // its to-remove list, so it grows all raid and keeps every dead bot's brain (and through it the BotOwner) alive.
        // A prefix: brains are queued during the agent loop, so emptying afterwards would drop them before removal.
        private static bool s_brainsFailed;

        private static void AiUpdatePrefix(AICoreController __instance)
        {
            try
            {
                if (DeadBrains == null || !DeadBrains.Value || !__instance._enable || __instance._listToDel.Count == 0)
                {
                    return;
                }

                foreach (AICoreAgentBase agent in __instance._listToDel)
                {
                    __instance._listAgents.Remove(agent);
                }

                __instance._listToDel.Clear();
            }
            catch (Exception ex)
            {
                Failed(ref s_brainsFailed, "dead bot brains", ex);
            }
        }

        // ---------------------------------------------------------------- per-bot coroutine

        // Player.LateUpdate calls StartFovCoroutine for every living player every frame. For a third-person body (every bot)
        // the coroutine sets RibcageScaleCurrent to 1, waits for the end of the frame and resets a ribcage scale that is
        // only ever changed by the first-person strategy. Doing both now saves an iterator and a Coroutine object per bot
        // per frame (30 bots at 120 fps: 3,600 pieces of garbage a second while the game's GC is off in raid).
        private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, IProceduralAnimationStrategy> Strategy =
            AccessTools.FieldRefAccess<ProceduralWeaponAnimation, IProceduralAnimationStrategy>("_strategy");

        private static bool s_fovFailed;

        private static bool FovCoroutinePrefix(ProceduralWeaponAnimation __instance, Player player)
        {
            try
            {
                if (BotCoroutine == null || !BotCoroutine.Value || player == null || player.IsYourPlayer || !(Strategy(__instance) is ThirdPersonStrategy))
                {
                    return true;
                }

                __instance.ResetFovAdjustments(player);
                player.RibcageScaleCurrent = 1f;
                return false;
            }
            catch (Exception ex)
            {
                Failed(ref s_fovFailed, "per-bot coroutine", ex);
                return true;
            }
        }
    }
}
