using System;
using HarmonyLib;
using System.Reflection;
using UnityEngine;

namespace MegaTrainer
{
    // ═══════════════════════════════════════════════════════
    // GOD MODE — Prevent all damage to player
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Character), "Damage")]
    public static class GodModePatch
    {
        static bool Prefix(Character __instance, HitData hit)
        {
            if (!MegaTrainerPlugin.IsCheatEnabled("god_mode")) return true;
            if (__instance is Player player && player == Player.m_localPlayer)
                return false; // Skip damage entirely
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════
    // UNLIMITED STAMINA — Prevent stamina drain
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Player), nameof(Player.UseStamina))]
    public static class UnlimitedStaminaPatch
    {
        static bool Prefix(Player __instance)
        {
            if (!MegaTrainerPlugin.IsCheatEnabled("unlimited_stamina")) return true;
            if (__instance == Player.m_localPlayer)
                return false; // Skip stamina drain entirely
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════
    // UNLIMITED CARRY WEIGHT — Set massive carry limit
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Player), nameof(Player.GetMaxCarryWeight))]
    public static class UnlimitedWeightPatch
    {
        static void Postfix(Player __instance, ref float __result)
        {
            if (!MegaTrainerPlugin.IsCheatEnabled("unlimited_weight")) return;
            if (__instance == Player.m_localPlayer)
                __result = 99999f;
        }
    }

    // ═══════════════════════════════════════════════════════
    // NO SKILL DRAIN — Prevent skill loss on death
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Skills), nameof(Skills.LowerAllSkills))]
    public static class NoSkillDrainPatch
    {
        static bool Prefix(Skills __instance)
        {
            if (!MegaTrainerPlugin.IsCheatEnabled("no_skill_drain")) return true;
            // Skills belongs to its Player — check via Harmony reflection
            var player = Traverse.Create(__instance).Field("m_player").GetValue<Player>();
            if (player == Player.m_localPlayer)
                return false;
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════
    // FREE BUILD — No resource cost for building/crafting
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements), new[] { typeof(Piece), typeof(Player.RequirementMode) })]
    public static class FreeBuildPiecePatch
    {
        static void Postfix(Player __instance, ref bool __result)
        {
            if (!MegaTrainerPlugin.IsCheatEnabled("no_placement_cost")) return;
            if (__instance == Player.m_localPlayer)
                __result = true;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
    public static class FreeBuildConsumeResourcesPatch
    {
        static bool Prefix(Player __instance)
        {
            if (!MegaTrainerPlugin.IsCheatEnabled("no_placement_cost")) return true;
            if (__instance == Player.m_localPlayer)
                return false;
            return true;
        }
    }

    /// <summary>
    /// Finds and sets the m_noPlacementCost field on whatever class owns it (ZNet in current Valheim).
    /// This is the same flag the vanilla "nocost" console command toggles.
    /// Falls back to patching Player.HaveRequirements if the field can't be found.
    /// </summary>
    public static class FreeBuildHelper
    {
        private static FieldInfo _npcField;
        private static object _npcTarget;
        private static bool _searched;

        public static void SetNoPlacementCost(bool enabled)
        {
            if (!_searched)
            {
                _searched = true;
                // Search common classes for m_noPlacementCost
                foreach (var typeName in new[] { "Player", "ZNet", "Game", "Piece" })
                {
                    var type = AccessTools.TypeByName(typeName);
                    if (type == null) continue;
                    var field = AccessTools.Field(type, "m_noPlacementCost");
                    if (field != null)
                    {
                        _npcField = field;
                        MegaTrainerPlugin.Log.LogInfo($"Found m_noPlacementCost on {typeName} (static={field.IsStatic})");
                        break;
                    }
                }
                if (_npcField == null)
                    MegaTrainerPlugin.Log.LogWarning("Could not find m_noPlacementCost field — Free Build relies on HaveRequirements patch only");
            }

            if (_npcField == null) return;

            try
            {
                if (_npcField.IsStatic)
                {
                    _npcField.SetValue(null, enabled);
                }
                else
                {
                    // For instance fields, we need the instance
                    // Player instance
                    if (_npcField.DeclaringType == typeof(Player))
                    {
                        var player = Player.m_localPlayer;
                        if (player != null) _npcField.SetValue(player, enabled);
                    }
                    // ZNet instance
                    else if (_npcField.DeclaringType.Name == "ZNet")
                    {
                        var znet = ZNet.instance;
                        if (znet != null) _npcField.SetValue(znet, enabled);
                    }
                }
            }
            catch (Exception ex)
            {
                MegaTrainerPlugin.Log.LogError($"Failed to set m_noPlacementCost: {ex.Message}");
            }
        }
    }

    // ═══════════════════════════════════════════════════════
    // NO WEATHER DAMAGE — Block freezing/cold/wet effects
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.IsCold))]
    public static class NoColdPatch
    {
        static void Postfix(ref bool __result)
        {
            if (MegaTrainerPlugin.IsCheatEnabled("no_weather_damage"))
                __result = false;
        }
    }

    [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.IsFreezing))]
    public static class NoFreezingPatch
    {
        static void Postfix(ref bool __result)
        {
            if (MegaTrainerPlugin.IsCheatEnabled("no_weather_damage"))
                __result = false;
        }
    }

    [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.IsWet))]
    public static class NoWetPatch
    {
        static void Postfix(ref bool __result)
        {
            if (MegaTrainerPlugin.IsCheatEnabled("no_weather_damage"))
                __result = false;
        }
    }

    // ═══════════════════════════════════════════════════════
    // ONE-HIT KILL — Multiply damage to non-players
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Character), "Damage")]
    public static class OneHitKillPatch
    {
        static void Prefix(Character __instance, ref HitData hit)
        {
            if (!MegaTrainerPlugin.IsCheatEnabled("instant_kill")) return;
            if (__instance == null || __instance.IsPlayer()) return;

            // Only boost damage from local player
            if (hit.GetAttacker() == Player.m_localPlayer)
            {
                hit.m_damage.m_damage = 999999f;
            }
        }
    }

    // ═══════════════════════════════════════════════════════
    // NO DURABILITY LOSS — Prevent tool/weapon degradation
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetMaxDurability), new Type[] { })]
    public static class NoDurabilityLossPatch
    {
        static void Postfix(ItemDrop.ItemData __instance, ref float __result)
        {
            if (!MegaTrainerPlugin.IsCheatEnabled("no_durability_loss")) return;
            // Force durability to max whenever durability is checked
            __instance.m_durability = __result;
        }
    }

    // ═══════════════════════════════════════════════════════
    // ALWAYS RESTED — Permanent rested comfort bonus
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Player), nameof(Player.GetComfortLevel))]
    public static class AlwaysRestedPatch
    {
        static void Postfix(Player __instance, ref int __result)
        {
            if (!MegaTrainerPlugin.IsCheatEnabled("always_rested")) return;
            if (__instance == Player.m_localPlayer)
                __result = 20; // Max comfort level
        }
    }

    // ═══════════════════════════════════════════════════════
    // INFINITE EITR — Prevent Eitr drain
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Player), nameof(Player.UseEitr))]
    public static class InfiniteEitrPatch
    {
        static bool Prefix(Player __instance)
        {
            if (!MegaTrainerPlugin.IsCheatEnabled("infinite_eitr")) return true;
            if (__instance == Player.m_localPlayer)
                return false; // Skip Eitr drain entirely
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════
    // SPEED ADJUSTMENT — Multiply movement speed
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(SEMan), "ApplyStatusEffectSpeedMods")]
    public static class SpeedModPatch
    {
        [HarmonyPostfix]
        public static void Postfix(SEMan __instance, ref float speed)
        {
            if (Mathf.Approximately(MegaTrainerPlugin.SpeedMultiplier, 1.0f)) return;

            var characterField = typeof(SEMan).GetField("m_character", BindingFlags.NonPublic | BindingFlags.Instance);
            if (characterField == null) return;

            var character = characterField.GetValue(__instance) as Character;
            if (character == null || character != Player.m_localPlayer) return;

            speed *= MegaTrainerPlugin.SpeedMultiplier;
        }
    }

    [HarmonyPatch(typeof(Character), "UpdateMotion")]
    public static class SpeedAccelPatch
    {
        private static float _savedAccel;
        private static float _savedSwimAccel;
        private static float _savedSwimTurnSpeed;
        private static float _savedTurnSpeed;
        private static float _savedRunTurnSpeed;
        private static bool _applied;

        [HarmonyPrefix]
        public static void Prefix(Character __instance)
        {
            _applied = false;
            if (__instance != Player.m_localPlayer) return;
            if (Mathf.Approximately(MegaTrainerPlugin.SpeedMultiplier, 1.0f)) return;

            float mult = MegaTrainerPlugin.SpeedMultiplier;
            _savedAccel = __instance.m_acceleration;
            _savedSwimAccel = __instance.m_swimAcceleration;
            _savedSwimTurnSpeed = __instance.m_swimTurnSpeed;
            _savedTurnSpeed = __instance.m_turnSpeed;
            _savedRunTurnSpeed = __instance.m_runTurnSpeed;
            _applied = true;

            __instance.m_acceleration *= mult;
            __instance.m_swimAcceleration *= mult;
            __instance.m_swimTurnSpeed *= mult;
            __instance.m_turnSpeed *= mult;
            __instance.m_runTurnSpeed *= mult;
        }

        [HarmonyPostfix]
        public static void Postfix(Character __instance)
        {
            if (!_applied) return;
            _applied = false;
            __instance.m_acceleration = _savedAccel;
            __instance.m_swimAcceleration = _savedSwimAccel;
            __instance.m_swimTurnSpeed = _savedSwimTurnSpeed;
            __instance.m_turnSpeed = _savedTurnSpeed;
            __instance.m_runTurnSpeed = _savedRunTurnSpeed;
        }
    }

    // ═══════════════════════════════════════════════════════
    // JUMP HEIGHT ADJUSTMENT — Multiply jump force
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Character), "Jump", new System.Type[] { typeof(bool) })]
    public static class JumpHeightPatch
    {
        private static float _savedJumpForce;
        private static float _savedJumpForceForward;
        private static bool _applied;

        [HarmonyPrefix]
        public static void Prefix(Character __instance)
        {
            _applied = false;
            if (__instance != Player.m_localPlayer) return;
            if (Mathf.Approximately(MegaTrainerPlugin.JumpMultiplier, 1.0f)) return;

            float mult = MegaTrainerPlugin.JumpMultiplier;
            _savedJumpForce = __instance.m_jumpForce;
            _savedJumpForceForward = __instance.m_jumpForceForward;
            _applied = true;

            __instance.m_jumpForce *= mult;
            __instance.m_jumpForceForward *= mult;
        }

        [HarmonyPostfix]
        public static void Postfix(Character __instance)
        {
            if (!_applied) return;
            _applied = false;
            __instance.m_jumpForce = _savedJumpForce;
            __instance.m_jumpForceForward = _savedJumpForceForward;
        }
    }

    // ═══════════════════════════════════════════════════════
    // NO STRUCTURE DAMAGE — Protect player-built structures
    // Structures can still be deconstructed with the hammer
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Damage))]
    public static class NoStructureDamagePatch
    {
        static bool Prefix(WearNTear __instance, HitData hit)
        {
            if (!MegaTrainerPlugin.IsCheatEnabled("no_structure_damage")) return true;

            // Allow hammer deconstruction (m_toolTier >= 0 means it's a tool hit for removal)
            // When the player uses "middle click" to remove, the game calls WearNTear.Remove() directly,
            // not Damage(). But some removal tools go through Damage with specific flags.
            // The key indicator is: if the piece was placed by a player (has a creator),
            // block all damage to it.
            var piece = __instance.GetComponent<Piece>();
            if (piece != null && piece.IsPlacedByPlayer())
            {
                return false; // Block all damage to player-built structures
            }

            return true;
        }
    }

    // ═══════════════════════════════════════════════════════
    // FAST SKILL UP — 1 skill point per use instead of
    // the tiny default increments
    // ═══════════════════════════════════════════════════════
    [HarmonyPatch(typeof(Skills), nameof(Skills.RaiseSkill))]
    public static class FastSkillUpPatch
    {
        static void Prefix(Skills __instance, ref float factor)
        {
            if (!MegaTrainerPlugin.IsCheatEnabled("fast_skill_up")) return;

            var player = Traverse.Create(__instance).Field("m_player").GetValue<Player>();
            if (player != Player.m_localPlayer) return;

            // Override the factor so each skill use raises by ~1 full point
            // Default raises are tiny (0.1-0.5), so we multiply to get ~1.0 per action
            // Setting factor to a large value lets the vanilla XP curve handle the rest
            factor = Mathf.Max(factor * 20f, 1.0f);
        }
    }
}
