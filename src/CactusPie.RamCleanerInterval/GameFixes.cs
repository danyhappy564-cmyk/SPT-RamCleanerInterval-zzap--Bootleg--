using System;
using System.Collections.Generic;
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
    /// logged, the others still work. Inspired by Smoothie (TominoCZ); written for this mod from the 4.1 client's own code.
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
                postfix: nameof(AiUpdatePostfix));
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

        // Checked in Assembly-CSharp 4.1: the BotCreationData constructor and the SpawnDelaysService constructor both add a
        // handler to GlobalEventDispatcher.OnStopBotSpawn. The dispatcher is created once (BaseLocalGame, "if not
        // Instantiated") and kept for the whole session, StopBotSpawn() has no caller, and GlobalEventDispatcher.Stop()
        // - run from BotsController.StopGettingInfo when a raid ends - nulls about twenty other events but not
        // _onStopBotSpawn. Each finished raid's spawn records (with their profiles) therefore stayed reachable. This
        // nulls that one field right after Stop(); during a raid nothing is different.
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

        // Checked in Assembly-CSharp 4.1: AICoreController (one per raid, a BotsController field) queues dead bots' agents
        // in _listToDel through RemoveNodeController, takes them out of _listAgents at the top of every Update, and never
        // empties _listToDel (Clear() only empties the other two sets). So every agent that ever died stays referenced
        // until the raid ends and is looked up again each frame. After Update, entries that are already gone from
        // _listAgents are dropped; ones queued during this Update are still in _listAgents and are left for the next one.
        private static bool s_brainsFailed;
        private static HashSet<AICoreAgentBase> s_agents;
        private static readonly Predicate<AICoreAgentBase> NotRunning = agent => s_agents != null && !s_agents.Contains(agent);

        private static void AiUpdatePostfix(AICoreController __instance)
        {
            try
            {
                if (DeadBrains == null || !DeadBrains.Value || __instance._listToDel.Count == 0)
                {
                    return;
                }

                // A cached predicate over a static field: no closure (= no garbage) per frame.
                s_agents = __instance._listAgents;
                __instance._listToDel.RemoveWhere(NotRunning);
                s_agents = null;
            }
            catch (Exception ex)
            {
                Failed(ref s_brainsFailed, "dead bot brains", ex);
            }
        }

        // ---------------------------------------------------------------- per-bot coroutine

        // Checked in Assembly-CSharp 4.1: Player.LateUpdate calls ProceduralWeaponAnimation.StartFovCoroutine every frame
        // for every living player, which allocates an iterator and a Coroutine. With ThirdPersonStrategy (other players
        // and bots) the coroutine only sets RibcageScaleCurrent to 1 and, after the frame, resets the ribcage and hands
        // scale - and that reset does nothing when the ribcage's local z scale is already 1. When both are already 1
        // the whole coroutine is a no-op, so it is not started; in every other case the game runs it as usual.
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

                bool nothingToDo = player.RibcageScaleCurrent == 1f && UnityEngine.Mathf.Approximately(player.PlayerBones.Ribcage.Original.localScale.z, 1f);
                return !nothingToDo;
            }
            catch (Exception ex)
            {
                Failed(ref s_fovFailed, "per-bot coroutine", ex);
                return true;
            }
        }
    }
}
