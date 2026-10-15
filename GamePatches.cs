using HarmonyLib;
using QSB.HUD;
using QSB.Player;
using UnityEngine;

namespace QSBHideAndSeek
{
    [HarmonyPatch]
    public static class GamePatches
    {
        private static readonly AccessTools.FieldRef<PlayerHUDMarker, PlayerInfo> HudMarkerPlayer =
            AccessTools.FieldRefAccess<PlayerHUDMarker, PlayerInfo>("_player");

        private static readonly AccessTools.FieldRef<PlayerMapMarker, PlayerInfo> MapMarkerPlayer =
            AccessTools.FieldRefAccess<PlayerMapMarker, PlayerInfo>("_player");

        //take over death so our kill doesn't get stolen
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(DeathManager), nameof(DeathManager.KillPlayer))]
        public static bool DeathManager_KillPlayer(DeathType deathType)
        {
            var game = HideAndSeekGame.Instance;
            return game == null || !game.TryInterceptDeath(deathType);
        }

        // LockMovement only stops walking/jumping, the jetpack reads its own input. no thrust while frozen
        [HarmonyPrefix]
        [HarmonyPatch(typeof(JetpackThrusterController), "ReadTranslationalInput")]
        public static bool JetpackThrusterController_ReadTranslationalInput(ref Vector3 __result)
        {
            var game = HideAndSeekGame.Instance;
            if (game != null && game.IsLocalFrozen)
            {
                __result = Vector3.zero;
                return false;
            }

            return true;
        }

        // nomai shuttles drag you off the planet at speed and the barrier can't win that fight. no launching mid round
        [HarmonyPrefix]
        [HarmonyPatch(typeof(NomaiShuttleController), "OnLaunchSlotActivated")]
        public static bool NomaiShuttleController_OnLaunchSlotActivated()
        {
            var game = HideAndSeekGame.Instance;
            return game == null || !game.IsInActiveRound;
        }

        //qsb nametags
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerHUDMarker), "ShouldBeVisible")]
        public static void PlayerHUDMarker_ShouldBeVisible(PlayerHUDMarker __instance, ref bool __result)
        {
            if (!__result)
            {
                return;
            }

            var game = HideAndSeekGame.Instance;
            var player = HudMarkerPlayer(__instance);
            if (game == null || player == null)
            {
                return;
            }

            __result = game.CanSeeMarker(player.PlayerId, false);
        }

        // nametags map edition
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerMapMarker), "ShouldBeVisible")]
        public static void PlayerMapMarker_ShouldBeVisible(PlayerMapMarker __instance, ref bool __result)
        {
            if (!__result)
            {
                return;
            }

            var game = HideAndSeekGame.Instance;
            var player = MapMarkerPlayer(__instance);
            if (game == null || player == null)
            {
                return;
            }

            __result = game.CanSeeMarker(player.PlayerId, true);
        }

        // remove during round or else cheating
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MultiplayerHUDManager), nameof(MultiplayerHUDManager.UpdateMinimapMarkers))]
        public static bool MultiplayerHUDManager_UpdateMinimapMarkers(MultiplayerHUDManager __instance, Minimap minimap)
        {
            var game = HideAndSeekGame.Instance;
            if (game == null || !game.IsMinimapHidden())
            {
                return true;
            }

            __instance.HideMinimap(minimap);
            return false;
        }
    }
}
