// Plugin/Utils/VersionUtil.cs
using System;
using System.IO;
using System.Xml;
using TorchDiscordSync.Plugin.Config;

namespace TorchDiscordSync.Plugin.Utils
{
    /// <summary>
    /// Loads plugin version from manifest.xml dynamically
    /// Prevents hardcoded version strings throughout the codebase
    /// </summary>
    public static class VersionUtil
    {
        private static string _cachedVersion = null;
        private static readonly string ManifestPath = ResolveManifestPath();

        private static string ResolveManifestPath()
        {
            try
            {
                var assemblyDirectory = Path.GetDirectoryName(typeof(VersionUtil).Assembly.Location);
                if (!string.IsNullOrWhiteSpace(assemblyDirectory))
                    return Path.Combine(assemblyDirectory, "manifest.xml");
            }
            catch
            {
            }

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "manifest.xml");
        }

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
                LoggerUtil.LogException(
                    "[VersionUtil] Failed to load version from manifest.",
                    ex);
            }

            // _cachedVersion = "2.0.0";
            _cachedVersion = MainConfig.Load().PluginVersion; // Fallback to version from config if manifest read fails
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
                LoggerUtil.LogException(
                    "[VersionUtil] Failed to load name from manifest.",
                    ex);
            }

            return "TDS";
        }

    }
}
