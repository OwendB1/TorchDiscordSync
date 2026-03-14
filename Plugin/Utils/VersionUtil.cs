// Plugin/Utils/VersionUtil.cs
using System;
using System.IO;
using System.Xml;

namespace mamba.TorchDiscordSync.Plugin.Utils
{
    /// <summary>
    /// Loads plugin version from manifest.xml dynamically
    /// Prevents hardcoded version strings throughout the codebase
    /// </summary>
    public static class VersionUtil
    {
        private static string _cachedVersion = null;
        private static readonly string ManifestPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Plugins",
            "mamba.TorchDiscordSync.Plugin",
            "manifest.xml");

        /// <summary>
        /// Get current plugin version from manifest.xml
        /// Cached after first read for performance
        /// </summary>
        public static string GetVersion()
        {
            if (_cachedVersion != null)
                return _cachedVersion;

            try
            {
                if (File.Exists(ManifestPath))
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(ManifestPath);

                    XmlNode versionNode = doc.SelectSingleNode("//Version");
                    if (versionNode != null && !string.IsNullOrEmpty(versionNode.InnerText))
                    {
                        _cachedVersion = versionNode.InnerText.Trim();
                        return _cachedVersion;
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[VersionUtil] Failed to load version from manifest: " + ex.Message);
            }

            _cachedVersion = "2.0.0";
            return _cachedVersion;
        }

        /// <summary>
        /// Get full version string for display
        /// Example: "v2.0.1"
        /// </summary>
        public static string GetVersionString()
        {
            return "v" + GetVersion();
        }

        /// <summary>
        /// Get plugin name from manifest.xml
        /// </summary>
        public static string GetPluginName()
        {
            try
            {
                if (File.Exists(ManifestPath))
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(ManifestPath);

                    XmlNode nameNode = doc.SelectSingleNode("//Name");
                    if (nameNode != null && !string.IsNullOrEmpty(nameNode.InnerText))
                    {
                        return nameNode.InnerText.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[VersionUtil] Failed to load name from manifest: " + ex.Message);
            }

            return "mamba.TorchDiscordSync.Plugin";
        }

        /// <summary>
        /// Get author from manifest.xml
        /// </summary>
        public static string GetAuthor()
        {
            try
            {
                if (File.Exists(ManifestPath))
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(ManifestPath);

                    XmlNode authorNode = doc.SelectSingleNode("//Author");
                    if (authorNode != null && !string.IsNullOrEmpty(authorNode.InnerText))
                    {
                        return authorNode.InnerText.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[VersionUtil] Failed to load author from manifest: " + ex.Message);
            }

            return "mamba";
        }

        /// <summary>
        /// Get description from manifest.xml
        /// </summary>
        public static string GetDescription()
        {
            try
            {
                if (File.Exists(ManifestPath))
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(ManifestPath);

                    XmlNode descNode = doc.SelectSingleNode("//Description");
                    if (descNode != null && !string.IsNullOrEmpty(descNode.InnerText))
                    {
                        return descNode.InnerText.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[VersionUtil] Failed to load description from manifest: " + ex.Message);
            }

            return "Advanced Space Engineers Discord Sync Plugin";
        }
    }
}
