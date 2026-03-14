// Plugin/Models/LocationZoneModels.cs
using System;

namespace mamba.TorchDiscordSync.Plugin.Models
{
    /// <summary>
    /// ENHANCED: Location zones with surface vs orbit distinction
    /// </summary>
    public enum LocationZoneEnum
    {
        /// <summary>ON the planet surface - within 15% of radius</summary>
        ON_SURFACE,

        /// <summary>Low orbit - 1.15x to 1.5x planet radius</summary>
        LOW_ORBIT,

        /// <summary>High orbit - 1.5x to 3x planet radius</summary>
        HIGH_ORBIT,

        /// <summary>Inner system - 0-5000 km from origin, no nearby planet</summary>
        INNER_SYSTEM,

        /// <summary>Outer space - 5000-10000 km from origin</summary>
        OUTER_SPACE,

        /// <summary>Deep space - beyond 10000 km</summary>
        DEEP_SPACE,

        /// <summary>Unknown or unable to determine</summary>
        UNKNOWN,

        /// <summary>DEPRECATED: Use ON_SURFACE, LOW_ORBIT, or HIGH_ORBIT instead</summary>
        [Obsolete("Use ON_SURFACE, LOW_ORBIT, or HIGH_ORBIT")]
        AROUND_PLANET,
    }

    /// <summary>
    /// Container for location detection results
    /// </summary>
    public class LocationZoneResult
    {
        /// <summary>Detected zone based on distance calculations</summary>
        public LocationZoneEnum Zone { get; set; }

        /// <summary>Planet name if near a planet, otherwise empty</summary>
        public string PlanetName { get; set; }

        /// <summary>Distance from origin in kilometers</summary>
        public double DistanceFromOrigin { get; set; }

        /// <summary>Distance to nearest planet in kilometers, if applicable</summary>
        public double DistanceToPlanet { get; set; }

        /// <summary>NEW: Is the character on the planet surface?</summary>
        public bool IsOnSurface { get; set; }

        /// <summary>Grid/ship name if character is inside one, otherwise null</summary>
        public string GridName { get; set; }

        /// <summary>Whether the character is inside a grid</summary>
        public bool IsInsideGrid { get; set; }

        public LocationZoneResult()
        {
            Zone = LocationZoneEnum.UNKNOWN;
            PlanetName = string.Empty;
            DistanceFromOrigin = 0;
            DistanceToPlanet = 0;
            IsOnSurface = false;
            GridName = null;
            IsInsideGrid = false;
        }
    }
}
