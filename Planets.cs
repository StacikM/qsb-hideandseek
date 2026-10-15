using System;
using System.Globalization;
using System.Linq;
using QSB.Player;
using UnityEngine;

namespace QSBHideAndSeek
{
    /// <summary>A planet players can vote to play on.</summary>
    public class PlanetOption
    {
        public string DisplayName;
        public AstroObject.Name AstroName;

        /// <summary>
        /// If set, players are placed on this child body (e.g. an island on Giant's Deep)
        /// instead of on the astro object's own surface, since the planet itself is just ocean.
        /// </summary>
        public string SurfaceBodyName;
    }

    public static class Planets
    {
        public static readonly PlanetOption[] Options =
        {
            new PlanetOption { DisplayName = "Timber Hearth",   AstroName = AstroObject.Name.TimberHearth },
            new PlanetOption { DisplayName = "Attlerock",       AstroName = AstroObject.Name.TimberMoon },
            new PlanetOption { DisplayName = "Brittle Hollow",  AstroName = AstroObject.Name.BrittleHollow },
            new PlanetOption { DisplayName = "Hollow's Lantern", AstroName = AstroObject.Name.VolcanicMoon },
            new PlanetOption { DisplayName = "Giant's Deep (Gabbro's Island)", AstroName = AstroObject.Name.GiantsDeep, SurfaceBodyName = "GabbroIsland_Body" },
            new PlanetOption { DisplayName = "Ember Twin",      AstroName = AstroObject.Name.CaveTwin },
            new PlanetOption { DisplayName = "Ash Twin",        AstroName = AstroObject.Name.TowerTwin },
            new PlanetOption { DisplayName = "The Interloper",  AstroName = AstroObject.Name.Comet },
        };

        // Rays start this far from the planet centre and are cast inward. Bigger than any of
        // the listed bodies, but small enough that we don't start inside a neighbouring planet.
        private const float PlanetRayStart = 600f;
        private const float IslandRayStart = 250f;
        private const int Attempts = 80;

        /// <summary>
        /// Warps the local player to a random spot on the given planet's surface.
        /// Returns false if no spot could be found (the player is left where they were).
        /// </summary>
        public static bool TeleportLocalPlayer(int planetIndex)
        {
            if (planetIndex < 0 || planetIndex >= Options.Length)
            {
                return false;
            }

            if (!TryFindSurfacePoint(Options[planetIndex], out var point, out var normal, out var body))
            {
                QSBHideAndSeek.Instance.ModHelper.Console.WriteLine(
                    $"Could not find a surface point on {Options[planetIndex].DisplayName}!", OWML.Common.MessageType.Error);
                return false;
            }

            // Properly leave the pilot seat / ship computer / any attach point, otherwise the warp gets undone
            // and the ship (and QSB's "who is flying") is left thinking we're still in control.
            PlayerWarp.ReleaseFromAttachments();

            var forward = Vector3.ProjectOnPlane(UnityEngine.Random.onUnitSphere, normal);
            if (forward.sqrMagnitude < 0.01f)
            {
                forward = Vector3.ProjectOnPlane(Vector3.right, normal);
            }

            var rotation = Quaternion.LookRotation(forward.normalized, normal);
            var position = point + normal * 1.2f;

            PlayerWarp.Warp(position, rotation, body.GetPointVelocity(position));
            return true;
        }

        /// <summary>
        /// The body the play area is centred on and the radius of the planet's surface around it.
        /// For island spawns (Giant's Deep) the "surface" is wherever the island floats, not the ocean core.
        /// </summary>
        public static bool GetBarrier(int planetIndex, out OWRigidbody centreBody, out float surfaceRadius)
        {
            centreBody = null;
            surfaceRadius = 0f;

            if (planetIndex < 0 || planetIndex >= Options.Length)
            {
                return false;
            }

            var option = Options[planetIndex];
            var astro = Locator.GetAstroObject(option.AstroName);
            var planetBody = astro != null ? astro.GetOWRigidbody() : null;
            if (planetBody == null)
            {
                return false;
            }

            centreBody = planetBody;
            var centre = planetBody.GetPosition();

            if (!string.IsNullOrEmpty(option.SurfaceBodyName))
            {
                var islandObject = GameObject.Find(option.SurfaceBodyName);
                var islandBody = islandObject != null ? islandObject.GetComponent<OWRigidbody>() : null;
                if (islandBody != null)
                {
                    surfaceRadius = Vector3.Distance(islandBody.GetPosition(), centre);
                    return true;
                }
            }

            // Median distance of the outermost ground hit over a bunch of directions.
            var radii = new System.Collections.Generic.List<float>();
            for (var i = 0; i < 32; i++)
            {
                var direction = UnityEngine.Random.onUnitSphere;
                var origin = centre + direction * PlanetRayStart;
                var hits = Physics.RaycastAll(origin, -direction, PlanetRayStart + 10f, ~0, QueryTriggerInteraction.Ignore);
                foreach (var hit in hits.OrderBy(x => x.distance))
                {
                    if (hit.collider.GetComponentInParent<OWRigidbody>() == planetBody)
                    {
                        radii.Add(Vector3.Distance(hit.point, centre));
                        break;
                    }
                }
            }

            if (radii.Count == 0)
            {
                surfaceRadius = 300f;
                return true;
            }

            radii.Sort();
            surfaceRadius = radii[radii.Count / 2];
            return true;
        }

        private static bool TryFindSurfacePoint(PlanetOption option, out Vector3 point, out Vector3 normal, out OWRigidbody body)
        {
            point = Vector3.zero;
            normal = Vector3.up;
            body = null;

            var astro = Locator.GetAstroObject(option.AstroName);
            if (astro == null)
            {
                return false;
            }

            var planetBody = astro.GetOWRigidbody();
            if (planetBody == null)
            {
                return false;
            }

            var isIsland = !string.IsNullOrEmpty(option.SurfaceBodyName);
            body = planetBody;
            var centre = planetBody.GetPosition();
            var rayStart = PlanetRayStart;
            var outward = Vector3.zero;

            if (isIsland)
            {
                var islandObject = GameObject.Find(option.SurfaceBodyName);
                var islandBody = islandObject != null ? islandObject.GetComponent<OWRigidbody>() : null;
                if (islandBody == null)
                {
                    return false;
                }

                body = islandBody;
                outward = (islandBody.GetPosition() - planetBody.GetPosition()).normalized;
                centre = islandBody.GetPosition();
                rayStart = IslandRayStart;
            }

            var bestScore = float.MinValue;
            var found = false;

            for (var i = 0; i < Attempts; i++)
            {
                var direction = UnityEngine.Random.onUnitSphere;

                // Islands: only shoot from the side facing away from the planet centre.
                if (isIsland && Vector3.Dot(direction, outward) < 0.5f)
                {
                    continue;
                }

                var origin = centre + direction * rayStart;
                var hits = Physics.RaycastAll(origin, -direction, rayStart + 10f, ~0, QueryTriggerInteraction.Ignore);
                foreach (var hit in hits.OrderBy(x => x.distance))
                {
                    var hitBody = hit.collider.GetComponentInParent<OWRigidbody>();
                    if (hitBody != body)
                    {
                        // Something else (ship, probe, another player...) - look further down the ray.
                        continue;
                    }

                    // Prefer flat ground over cliffs and steep roofs.
                    var score = Vector3.Dot(hit.normal, direction);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        point = hit.point;
                        normal = hit.normal;
                        found = true;
                    }

                    break;
                }

                if (found && bestScore > 0.9f)
                {
                    break;
                }
            }

            return found;
        }
    }

    // sent to everyone when host starts
    public class RoundSettings
    {
        public int SeekerCount = 1;
        public float CountdownSeconds = 30f;
        public float RespawnSeconds = 5f;
        public float VoteSeconds = 20f;
        public float TouchDistance = 2f;
        public float NametagRevealDistance = 8f;
        public bool HidersSeeSeekerNametags = true;
        public float BarrierMeters = 150f;
        public float DeathGraceSeconds = 60f;

        public string Serialize()
        {
            var c = CultureInfo.InvariantCulture;
            return string.Join(",", new[]
            {
                SeekerCount.ToString(c),
                CountdownSeconds.ToString(c),
                RespawnSeconds.ToString(c),
                VoteSeconds.ToString(c),
                TouchDistance.ToString(c),
                NametagRevealDistance.ToString(c),
                HidersSeeSeekerNametags ? "1" : "0",
                BarrierMeters.ToString(c),
                DeathGraceSeconds.ToString(c),
            });
        }

        public static RoundSettings Deserialize(string text)
        {
            var c = CultureInfo.InvariantCulture;
            var p = text.Split(',');
            return new RoundSettings
            {
                SeekerCount = int.Parse(p[0], c),
                CountdownSeconds = float.Parse(p[1], c),
                RespawnSeconds = float.Parse(p[2], c),
                VoteSeconds = float.Parse(p[3], c),
                TouchDistance = float.Parse(p[4], c),
                NametagRevealDistance = float.Parse(p[5], c),
                HidersSeeSeekerNametags = p[6] == "1",
                BarrierMeters = float.Parse(p[7], c),
                DeathGraceSeconds = float.Parse(p[8], c),
            };
        }

        public RoundSettings Clone() => (RoundSettings)MemberwiseClone();
    }
}
