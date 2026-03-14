// Plugin/Services/DeathLocationService.cs
using System;
using System.Collections.Generic;
using mamba.TorchDiscordSync.Plugin.Config;
using mamba.TorchDiscordSync.Plugin.Models;
using mamba.TorchDiscordSync.Plugin.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Planet;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace mamba.TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// ENHANCED: Service for death location detection with surface vs orbit distinction
    /// Detects: Planet surface, planet orbit, inner system, outer space, deep space
    /// </summary>
    public class DeathLocationService
    {
        private readonly MainConfig _config;

        // Surface detection multipliers
        private const double SURFACE_MULTIPLIER = 1.15; // Within 15% of radius = surface
        private const double LOW_ORBIT_MULTIPLIER = 1.5; // 1.15-1.5x radius = low orbit
        private const double HIGH_ORBIT_MULTIPLIER = 3.0; // 1.5-3.0x radius = high orbit

        // Default constants (fallback values if config not available)
        private const double DEFAULT_INNER_SYSTEM_MAX_KM = 5000.0;
        private const double DEFAULT_OUTER_SPACE_MAX_KM = 10000.0;

        // Origin point for distance calculations (0,0,0)
        private static readonly Vector3D ORIGIN = Vector3D.Zero;

        // Properties that read from config with fallback to defaults
        private double InnerSystemMaxKm =>
            _config?.Death?.InnerSystemMaxKm ?? DEFAULT_INNER_SYSTEM_MAX_KM;
        private double OuterSpaceMaxKm =>
            _config?.Death?.OuterSpaceMaxKm ?? DEFAULT_OUTER_SPACE_MAX_KM;

        public DeathLocationService(MainConfig config = null)
        {
            _config = config;
            LoggerUtil.LogDebug(
                "[LOCATION] DeathLocationService initialized with enhanced surface detection"
            );
        }

        /// <summary>
        /// ENHANCED: Detects death location with surface/orbit distinction
        /// </summary>
        public LocationZoneResult DetectDeathZone(IMyCharacter character)
        {
            var result = new LocationZoneResult();

            try
            {
                if (character == null)
                {
                    LoggerUtil.LogWarning("[LOCATION] Character is null");
                    result.Zone = LocationZoneEnum.UNKNOWN;
                    return result;
                }

                Vector3D deathPosition = character.GetPosition();
                result.DistanceFromOrigin = Vector3D.Distance(deathPosition, ORIGIN);

                // STEP 1: Check if death is near a planet (priority)
                var nearestPlanet = FindNearestPlanet(deathPosition);
                if (nearestPlanet != null)
                {
                    double distanceToPlanet = Vector3D.Distance(
                        deathPosition,
                        nearestPlanet.PositionComp.GetPosition()
                    );
                    double planetRadius = nearestPlanet.AverageRadius;

                    LoggerUtil.LogDebug($"[LOCATION_PLANET] ═══ Planet Analysis ═══");
                    LoggerUtil.LogDebug(
                        $"[LOCATION_PLANET] Planet: {GetPlanetName(nearestPlanet)}"
                    );
                    LoggerUtil.LogDebug($"[LOCATION_PLANET] Radius: {planetRadius:F0} km");
                    LoggerUtil.LogDebug($"[LOCATION_PLANET] Distance: {distanceToPlanet:F2} km");
                    LoggerUtil.LogDebug(
                        $"[LOCATION_PLANET] Ratio: {(distanceToPlanet / planetRadius):F3}x"
                    );

                    // CRITICAL: Determine if on surface or in orbit
                    double distanceRatio = distanceToPlanet / planetRadius;

                    if (distanceRatio <= SURFACE_MULTIPLIER)
                    {
                        // ON SURFACE - within 15% of radius
                        result.Zone = LocationZoneEnum.ON_SURFACE;
                        result.PlanetName = GetPlanetName(nearestPlanet);
                        result.DistanceToPlanet = distanceToPlanet;
                        result.IsOnSurface = true;

                        LoggerUtil.LogSuccess(
                            $"[LOCATION_PLANET] ON SURFACE - {result.PlanetName} (ratio: {distanceRatio:F3})"
                        );
                        return result;
                    }
                    else if (distanceRatio <= LOW_ORBIT_MULTIPLIER)
                    {
                        // LOW ORBIT
                        result.Zone = LocationZoneEnum.LOW_ORBIT;
                        result.PlanetName = GetPlanetName(nearestPlanet);
                        result.DistanceToPlanet = distanceToPlanet;

                        LoggerUtil.LogSuccess(
                            $"[LOCATION_PLANET] LOW ORBIT - {result.PlanetName} (ratio: {distanceRatio:F3})"
                        );
                        return result;
                    }
                    else if (distanceRatio <= HIGH_ORBIT_MULTIPLIER)
                    {
                        // HIGH ORBIT
                        result.Zone = LocationZoneEnum.HIGH_ORBIT;
                        result.PlanetName = GetPlanetName(nearestPlanet);
                        result.DistanceToPlanet = distanceToPlanet;

                        LoggerUtil.LogSuccess(
                            $"[LOCATION_PLANET] HIGH ORBIT - {result.PlanetName} (ratio: {distanceRatio:F3})"
                        );
                        return result;
                    }

                    LoggerUtil.LogDebug(
                        $"[LOCATION_PLANET] Beyond orbit range (ratio: {distanceRatio:F3})"
                    );
                }

                // STEP 2: Classify by distance from origin (no planet nearby)
                if (result.DistanceFromOrigin < InnerSystemMaxKm)
                {
                    result.Zone = LocationZoneEnum.INNER_SYSTEM;
                    LoggerUtil.LogDebug(
                        $"[LOCATION] Inner system ({result.DistanceFromOrigin:F0} km from origin)"
                    );
                }
                else if (result.DistanceFromOrigin < OuterSpaceMaxKm)
                {
                    result.Zone = LocationZoneEnum.OUTER_SPACE;
                    LoggerUtil.LogDebug(
                        $"[LOCATION] Outer space ({result.DistanceFromOrigin:F0} km from origin)"
                    );
                }
                else
                {
                    result.Zone = LocationZoneEnum.DEEP_SPACE;
                    LoggerUtil.LogDebug(
                        $"[LOCATION] Deep space ({result.DistanceFromOrigin:F0} km from origin)"
                    );
                }

                // STEP 3: Detect if inside a grid
                DetectGridContext(character, result);

                return result;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[LOCATION] Error: {ex.Message}\n{ex.StackTrace}");
                result.Zone = LocationZoneEnum.UNKNOWN;
                return result;
            }
        }

        /// <summary>
        /// FIXED: Get planet name with correct priority (Name first!)
        /// </summary>
        private string GetPlanetName(MyPlanet planet)
        {
            try
            {
                // CORRECT ORDER: Name is the actual planet name!
                if (!string.IsNullOrEmpty(planet.Name))
                {
                    LoggerUtil.LogDebug($"[LOCATION_NAME] Selected from Name: {planet.Name}");
                    return planet.Name;
                }

                if (!string.IsNullOrEmpty(planet.DisplayNameText))
                {
                    LoggerUtil.LogDebug(
                        $"[LOCATION_NAME] Selected from DisplayNameText: {planet.DisplayNameText}"
                    );
                    return planet.DisplayNameText;
                }

                if (!string.IsNullOrEmpty(planet.DisplayName))
                {
                    LoggerUtil.LogDebug(
                        $"[LOCATION_NAME] Selected from DisplayName: {planet.DisplayName}"
                    );
                    return planet.DisplayName;
                }

                if (!string.IsNullOrEmpty(planet.StorageName))
                {
                    // StorageName often has UUID suffix - try to clean it
                    string storageName = planet.StorageName;
                    int dashIndex = storageName.IndexOf('-');
                    if (dashIndex > 0)
                        storageName = storageName.Substring(0, dashIndex);

                    LoggerUtil.LogDebug(
                        $"[LOCATION_NAME] Selected from StorageName: {storageName}"
                    );
                    return storageName;
                }

                LoggerUtil.LogWarning("[LOCATION_NAME] No valid planet name found");
                return "Unknown Planet";
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[LOCATION_NAME] Error: {ex.Message}");
                return "Unknown Planet";
            }
        }

        /// <summary>
        /// Detects if character is inside a grid/ship
        /// </summary>
        public void DetectGridContext(IMyCharacter character, LocationZoneResult result)
        {
            try
            {
                if (character == null || result == null)
                    return;

                var myCharacter = character as MyCharacter;
                if (myCharacter == null)
                    return;

                var parent = myCharacter.Parent;
                if (parent == null)
                    return;

                var grid = parent as MyCubeGrid;
                if (grid == null)
                    return;

                result.IsInsideGrid = true;
                result.GridName = grid.DisplayName ?? grid.Name ?? "Unknown Ship";
                LoggerUtil.LogDebug($"[LOCATION_GRID] Inside grid: {result.GridName}");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[LOCATION_GRID] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// ENHANCED: Generates location text with surface/orbit distinction
        /// </summary>
        public string GenerateLocationText(LocationZoneResult zoneResult, bool includeGridName)
        {
            if (zoneResult == null)
                return "";

            try
            {
                string zoneText = GetZoneDescription(zoneResult);

                if (
                    includeGridName
                    && zoneResult.IsInsideGrid
                    && !string.IsNullOrEmpty(zoneResult.GridName)
                )
                {
                    return $"inside {zoneResult.GridName} {zoneText}";
                }

                return zoneText;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[LOCATION_TEXT] Error: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// ENHANCED: Gets zone description with surface/orbit variants
        /// </summary>
        private string GetZoneDescription(LocationZoneResult result)
        {
            try
            {
                switch (result.Zone)
                {
                    case LocationZoneEnum.ON_SURFACE:
                        return GetRandomSurfaceMessage(result.PlanetName);

                    case LocationZoneEnum.LOW_ORBIT:
                        return GetRandomLowOrbitMessage(result.PlanetName);

                    case LocationZoneEnum.HIGH_ORBIT:
                        return GetRandomHighOrbitMessage(result.PlanetName);

                    case LocationZoneEnum.INNER_SYSTEM:
                        return GetRandomInnerSystemMessage();

                    case LocationZoneEnum.OUTER_SPACE:
                        return GetRandomOuterSpaceMessage();

                    case LocationZoneEnum.DEEP_SPACE:
                        return GetRandomDeepSpaceMessage();

                    case LocationZoneEnum.UNKNOWN:
                    default:
                        return "";
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[LOCATION_DESC] Error: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// NEW: Random messages for planet surface deaths
        /// </summary>
        private string GetRandomSurfaceMessage(string planetName)
        {
            var messages = new List<string>
            {
                $"on the surface of {planetName}",
                $"on {planetName}",
                $"while exploring {planetName}",
                $"while walking on {planetName}",
                $"on the ground of {planetName}",
                $"on {planetName}'s surface",
            };

            return messages[new Random().Next(messages.Count)];
        }

        /// <summary>
        /// NEW: Random messages for low orbit deaths
        /// </summary>
        private string GetRandomLowOrbitMessage(string planetName)
        {
            var messages = new List<string>
            {
                $"in low orbit around {planetName}",
                $"orbiting {planetName}",
                $"circling {planetName}",
                $"above {planetName}",
                $"near {planetName}",
                $"in {planetName}'s orbit",
            };

            return messages[new Random().Next(messages.Count)];
        }

        /// <summary>
        /// NEW: Random messages for high orbit deaths
        /// </summary>
        private string GetRandomHighOrbitMessage(string planetName)
        {
            var messages = new List<string>
            {
                $"in high orbit around {planetName}",
                $"far above {planetName}",
                $"in {planetName}'s orbital zone",
                $"in the vicinity of {planetName}",
                $"near {planetName}'s gravity well",
                $"approaching {planetName}",
            };

            return messages[new Random().Next(messages.Count)];
        }

        private string GetRandomInnerSystemMessage()
        {
            var messages = new List<string>
            {
                "in the inner system",
                "between planets",
                "in the system core",
                "near the center",
            };

            return messages[new Random().Next(messages.Count)];
        }

        private string GetRandomOuterSpaceMessage()
        {
            var messages = new List<string>
            {
                "in outer space",
                "on the system edge",
                "in the void",
                "far from civilization",
            };

            return messages[new Random().Next(messages.Count)];
        }

        private string GetRandomDeepSpaceMessage()
        {
            var messages = new List<string>
            {
                "in deep space",
                "lost in the void",
                "at the edge of known space",
                "in the darkness",
            };

            return messages[new Random().Next(messages.Count)];
        }

        /// <summary>
        /// Finds the nearest planet to the given position
        /// </summary>
        private MyPlanet FindNearestPlanet(Vector3D position)
        {
            try
            {
                if (MyPlanets.Static == null)
                    return null;

                var planets = MyPlanets.GetPlanets();
                if (planets == null || planets.Count == 0)
                    return null;

                MyPlanet nearest = null;
                double nearestDistance = double.MaxValue;

                foreach (var planet in planets)
                {
                    if (planet == null)
                        continue;

                    double distance = Vector3D.Distance(
                        position,
                        planet.PositionComp.GetPosition()
                    );
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearest = planet;
                    }
                }

                return nearest;
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[LOCATION_PLANET] Error finding nearest: {ex.Message}");
                return null;
            }
        }
    }
}
