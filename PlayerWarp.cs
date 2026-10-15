using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace QSBHideAndSeek
{
    /// <summary>
    /// Moves the local player somewhere else and tells the game about it.
    ///
    /// Just calling WarpToPositionRotation leaves the player's detectors registered in the volumes
    /// (sectors, gravity, fluids, audio...) of the OLD location and not registered in the new ones.
    /// Planets only enable their collision / full-detail meshes for sectors that contain a player detector,
    /// so a plain warp gives you a low-detail, non-solid planet until something else (like a scout) re-registers you.
    /// The game's own spawn code does the same "leave old volumes, enter new volumes" dance with authored spawn points;
    /// we do it for an arbitrary position by testing which trigger volumes contain the detectors.
    /// </summary>
    public static class PlayerWarp
    {
        private static readonly AccessTools.FieldRef<SectorDetector, List<Sector>> SectorList =
            AccessTools.FieldRefAccess<SectorDetector, List<Sector>>("_sectorList");

        /// <summary>
        /// Gets the player out of anything they're strapped into, going through the game's own exit paths
        /// so the events other systems (the ship, QSB's flyer tracking) listen to actually fire.
        /// </summary>
        public static void ReleaseFromAttachments()
        {
            // Pilot seat: just detaching the attach point skips the "ExitFlightConsole" event,
            // which is what tells QSB the ship has no pilot any more.
            var wasInShipMenu = PlayerState.UsingShipComputer() || PlayerState.AtFlightConsole();

            if (PlayerState.AtFlightConsole())
            {
                var cockpit = Object.FindObjectOfType<ShipCockpitController>();
                if (cockpit != null && cockpit.IsPlayerAtFlightConsole())
                {
                    cockpit.ExitFlightConsole();
                }
            }

            // Whatever is still holding us (seat, ship computer, ladder...).
            if (QSB.Player.PlayerAttachWatcher.Current != null)
            {
                QSB.Player.PlayerAttachWatcher.Current.DetachPlayer();
            }

            // Menus like the ship computer leave the input mode on "menu"; make sure we can move afterwards.
            if (wasInShipMenu)
            {
                OWInput.ChangeInputMode(InputMode.Character);
            }
        }

        public static void Warp(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            var playerBody = Locator.GetPlayerBody();

            LeaveAllVolumes();

            playerBody.WarpToPositionRotation(position, rotation);
            playerBody.SetVelocity(velocity);

            // Colliders have to be at the new position before we test volumes against them.
            Physics.SyncTransforms();

            EnterContainingVolumes();
        }

        private static List<GameObject> GetPlayerDetectors()
        {
            var detectors = new HashSet<GameObject>();

            void Add(GameObject go)
            {
                if (go != null)
                {
                    detectors.Add(go);
                }
            }

            Add(Locator.GetPlayerDetector());
            Add(Locator.GetPlayerCameraDetector());

            var sectorDetector = Locator.GetPlayerSectorDetector();
            if (sectorDetector != null)
            {
                Add(sectorDetector.gameObject);
            }

            var forceDetector = Locator.GetPlayerForceDetector();
            if (forceDetector != null)
            {
                Add(forceDetector.gameObject);
            }

            var body = Locator.GetPlayerBody();
            if (body != null)
            {
                foreach (var fluid in body.GetComponentsInChildren<FluidDetector>(true))
                {
                    Add(fluid.gameObject);
                }
            }

            var camera = Locator.GetPlayerCamera();
            if (camera != null)
            {
                foreach (var fluid in camera.GetComponentsInChildren<FluidDetector>(true))
                {
                    Add(fluid.gameObject);
                }
            }

            return detectors.ToList();
        }

        private static void LeaveAllVolumes()
        {
            var detectors = GetPlayerDetectors();

            foreach (var volume in Object.FindObjectsOfType<OWTriggerVolume>())
            {
                foreach (var detector in detectors)
                {
                    if (volume.IsTrackingObject(detector))
                    {
                        volume.RemoveObjectFromVolume(detector);
                    }
                }
            }

            // Anything still listed (shouldn't be) would keep a far-away sector "occupied" by us.
            var sectorDetector = Locator.GetPlayerSectorDetector();
            if (sectorDetector != null)
            {
                var sectors = SectorList(sectorDetector);
                foreach (var sector in sectors.ToList())
                {
                    sectorDetector.RemoveSector(sector);
                }
            }
        }

        private static void EnterContainingVolumes()
        {
            var detectors = GetPlayerDetectors();

            foreach (var volume in Object.FindObjectsOfType<OWTriggerVolume>())
            {
                if (!volume.isActiveAndEnabled)
                {
                    continue;
                }

                foreach (var detector in detectors)
                {
                    if (volume.IsTrackingObject(detector))
                    {
                        continue;
                    }

                    if (Contains(volume, detector.transform.position))
                    {
                        volume.AddObjectToVolume(detector);
                    }
                }
            }
        }

        private static bool Contains(OWTriggerVolume volume, Vector3 point)
        {
            try
            {
                var shape = volume.GetShape();
                if (shape != null)
                {
                    return shape.active && shape.PointInside(point);
                }

                if (volume.GetOWCustomCollider() != null)
                {
                    return false;
                }

                var owCollider = volume.GetOWCollider();
                var collider = owCollider != null ? owCollider.GetCollider() : null;
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                {
                    return false;
                }

                // Physics.ClosestPoint only supports primitives and convex meshes.
                var meshCollider = collider as MeshCollider;
                var supported = collider is BoxCollider
                    || collider is SphereCollider
                    || collider is CapsuleCollider
                    || (meshCollider != null && meshCollider.convex);
                if (!supported)
                {
                    return false;
                }

                var closest = Physics.ClosestPoint(point, collider, collider.transform.position, collider.transform.rotation);
                return (closest - point).sqrMagnitude < 0.0001f;
            }
            catch
            {
                // Entryway-only / compound volumes and anything exotic: let normal physics events handle those.
                return false;
            }
        }
    }
}
