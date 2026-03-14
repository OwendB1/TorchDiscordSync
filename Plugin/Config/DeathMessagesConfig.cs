// Plugin/Config/DeathMessagesConfig.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using mamba.TorchDiscordSync.Plugin.Models;
using mamba.TorchDiscordSync.Plugin.Utils;

namespace mamba.TorchDiscordSync.Plugin.Config
{
    /// <summary>
    /// ENHANCED: Death message templates with contextual variations
    /// Templates support: {victim}, {killer}, {weapon}, {location}
    /// Location is added automatically by DeathMessageHandler
    /// </summary>
    [XmlRoot("DeathMessages")]
    public class DeathMessagesConfig
    {
        private static Random _random = new Random();

        [XmlArray("Suicide")]
        [XmlArrayItem("Message")]
        public List<string> SuicideMessages { get; set; } =
            new List<string>
            {
                "{victim} restarted their character",
                "{victim} chose the easy way out",
                "{victim} decided to leave this world",
                "{victim} embraced the void",
                "{victim} became a permanent part of the scenery",
                "{victim} took matters into their own hands",
                "{victim} decided the universe was too much to handle",
                "{victim} embraced the void permanently",
                "{victim} pressed the big red button... on themselves",
                "{victim} found a permanent solution to their problems",
                "{victim} decided to take a one-way trip",
                "{victim} chose not to respawn",
                "{victim} became one with the abyss",
                "{victim} gave up on life",
                "{victim} was too lazy to respawn",
                "{victim} decided to nope out of existence",
            };

        [XmlArray("PvP")]
        [XmlArrayItem("Message")]
        public List<string> PvPMessages { get; set; } =
            new List<string>
            {
                "{killer} killed {victim} with {weapon}",
                "{killer} eliminated {victim} with {weapon}",
                "{killer} sent {victim} to respawn with {weapon}",
                "{killer} blasted {victim} with {weapon}",
                "{victim} met their end at the hands of {killer} wielding {weapon}",
                "{killer} showed {victim} no mercy with {weapon}",
                "{victim} was destroyed by {killer} using {weapon}",
                "{killer} ended {victim}'s day with {weapon}",
            };

        [XmlArray("Turret")]
        [XmlArrayItem("Message")]
        public List<string> TurretMessages { get; set; } =
            new List<string>
            {
                "{killer} killed {victim} with {weapon}",
                "{victim} walked into {killer}'s {weapon}",
                "{killer} sent {victim} to the void with {weapon}",
                "{victim} was peacefully exploring until {killer}'s {weapon} appeared",
                "{killer} ruined {victim}'s day with {weapon}",
                "{killer}'s {weapon} caught {victim} off guard",
                "{victim} met {killer}'s {weapon}",
                "{killer} used {weapon} to send {victim} to eternal hunting grounds",
            };

        [XmlArray("Environment_Oxygen")]
        [XmlArrayItem("Message")]
        public List<string> EnvironmentOxygenMessages { get; set; } =
            new List<string>
            {
                "{victim} stopped breathing due to lack of oxygen",
                "{victim} forgot to check their oxygen levels",
                "{victim} ran out of air",
                "{victim} held their breath for too long",
                "{victim} discovered that space has no oxygen",
                "{victim} suffocated",
            };

        [XmlArray("Environment_Pressure")]
        [XmlArrayItem("Message")]
        public List<string> EnvironmentPressureMessages { get; set; } =
            new List<string>
            {
                "{victim} died from environmental pressure",
                "{victim} couldn't handle the pressure",
                "{victim} experienced rapid decompression",
                "{victim}'s suit failed under pressure",
                "{victim} popped like a balloon",
                "{victim} was crushed by compression",
                "{victim} was squeezed like a pancake",
                "{victim} popped from pressure",
            };

        [XmlArray("Environment_Collision")]
        [XmlArrayItem("Message")]
        public List<string> EnvironmentCollisionMessages { get; set; } =
            new List<string>
            {
                "{victim} hit something very fast",
                "{victim} fell from a great height",
                "{victim} died in a collision",
                "{victim} forgot gravity exists",
                "{victim} learned that the ground is hard",
                "{victim} experienced rapid unplanned landing",
            };

        [XmlArray("Environment_Grinding")]
        [XmlArrayItem("Message")]
        public List<string> GrindingMessages { get; set; } =
                    new List<string>
                    {
                "{victim} met the business end of a grinder",
                "{victim} was ground into dust",
                "{victim} forgot to keep their distance from the grinder",
                "{victim} became one with the grindings",
                "{victim} experienced a fatal grinding incident",
                "{victim} was too close to the grinder",
                    };

        [XmlArray("Environment_Heat")]
        [XmlArrayItem("Message")]
        public List<string> HeatMessages { get; set; } =
            new List<string>
            {
                "{victim} was incinerated",
                "{victim} couldn't handle the heat",
                "{victim} experienced spontaneous combustion",
                "{victim} was consumed by flames",
                "{victim} forgot that fire is hot",
                "{victim} was roasted alive",
            };

        [XmlArray("Environment_Radiation")]
        [XmlArrayItem("Message")]
        public List<string> RadiationMessages { get; set; } =
            new List<string>
            {
                "{victim} was irradiated to death",
                "{victim} absorbed too much radiation",
                "{victim} became radioactive",
                "{victim} forgot their rad suit",
                "{victim} glowed briefly then exploded",
                "{victim} was poisoned by radiation",
            };

        [XmlArray("Environment_Creature")]
        [XmlArrayItem("Message")]
        public List<string> CreatureMessages { get; set; } =
            new List<string>
            {
                "{victim} was attacked by a wild creature",
                "{victim} met the local wildlife",
                "{victim} learned that creatures bite",
                "{victim} was no match for the predator",
                "{victim} became an animal's meal",
                "{victim} encountered a very angry animal",
            };

        [XmlArray("Environment_Hunger")]
        [XmlArrayItem("Message")]
        public List<string> HungerMessages { get; set; } =
            new List<string>
            {
                "{victim} starved to death",
                "{victim} forgot to eat",
                "{victim} experienced fatal hunger",
                "{victim} died of malnutrition",
                "{victim} couldn't survive without food",
                "{victim} learned that you need to eat",
            };

        [XmlArray("Environment_Weather")]
        [XmlArrayItem("Message")]
        public List<string> WeatherMessages { get; set; } =
            new List<string>
            {
                "{victim} was killed by the weather",
                "{victim} underestimated the storm",
                "{victim} was swept away by the elements",
                "{victim} couldn't survive the environmental conditions",
                "{victim} was battered by the weather",
                "{victim} learned that nature is powerful",
            };
  
        [XmlArray("Environment_Boundary")]
        [XmlArrayItem("Message")]
        public List<string> BoundaryMessages { get; set; } =
            new List<string>
            {
                "{victim} wandered too far beyond the map",
                "{victim} fell off the edge of the world",
                "{victim} exceeded the boundary",
                "{victim} discovered the edge of reality",
                "{victim} went out of bounds",
                "{victim} learned the map has limits",
            };

        [XmlArray("Grid")]
        [XmlArrayItem("Message")]
        public List<string> GridMessages { get; set; } =
            new List<string>
            {
                "{victim} was run over by a ship",
                "{victim} got too close to a moving grid",
                "{victim} was crushed",
                "{victim} met the business end of a landing gear",
                "Lord Clang claimed {victim}",
                "{victim} was flattened by a ship",
            };

        [XmlArray("Accident")]
        [XmlArrayItem("Message")]
        public List<string> AccidentMessages { get; set; } =
            new List<string>
            {
                "{victim} died in an accident",
                "{victim} is no more",
                "{victim} met an unfortunate end",
                "{victim} experienced a rapid unplanned disassembly",
                "{victim} disconnected from life unexpectedly",
                "Error 404: {victim}'s pulse not found",
            };

        public string GetRandomMessage(DeathTypeEnum deathType)
        {
            List<string> messages = GetMessagesForType(deathType);

            if (messages == null || messages.Count == 0)
                return "{victim} died"; // Fallback

            int index = _random.Next(messages.Count);
            return messages[index];
        }

        private List<string> GetMessagesForType(DeathTypeEnum deathType)
        {
            switch (deathType)
            {
                case DeathTypeEnum.Suicide:
                    return SuicideMessages;
                case DeathTypeEnum.PvP:
                    return PvPMessages;
                case DeathTypeEnum.Turret:
                    return TurretMessages;
                case DeathTypeEnum.Grid:
                    return GridMessages;
                case DeathTypeEnum.Environment_Oxygen:
                    return EnvironmentOxygenMessages;
                case DeathTypeEnum.Environment_Pressure:
                    return EnvironmentPressureMessages;
                case DeathTypeEnum.Environment_Collision:
                    return EnvironmentCollisionMessages;
                case DeathTypeEnum.Accident:
                case DeathTypeEnum.Unknown:
                default:
                    return AccidentMessages;
            }
        }

        public static DeathMessagesConfig Load()
        {
            string path = Path.Combine(MainConfig.GetConfigDirectory(), "DeathMessages.xml");
            try
            {
                if (File.Exists(path))
                {
                    XmlSerializer serializer = new XmlSerializer(typeof(DeathMessagesConfig));
                    using (FileStream fs = new FileStream(path, FileMode.Open))
                    {
                        return (DeathMessagesConfig)serializer.Deserialize(fs);
                    }
                }
                else
                {
                    var config = new DeathMessagesConfig();
                    config.Save();
                    return config;
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Failed to load DeathMessages.xml: " + ex.Message);
                return new DeathMessagesConfig();
            }
        }

        public void Save()
        {
            string path = Path.Combine(MainConfig.GetConfigDirectory(), "DeathMessages.xml");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                XmlSerializer serializer = new XmlSerializer(typeof(DeathMessagesConfig));
                using (FileStream fs = new FileStream(path, FileMode.Create))
                {
                    serializer.Serialize(fs, this);
                }
                LoggerUtil.LogInfo("DeathMessages.xml saved");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("Failed to save DeathMessages.xml: " + ex.Message);
            }
        }
    }
}
