// Plugin/Utils/VersionUtil.cs
using System;
using System.IO;
using System.Reflection;
using System.Xml;

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

        private static string ResolveAssemblyVersion()
        {
            try
            {
                var assembly = typeof(VersionUtil).Assembly;
                var informationalVersion = assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informationalVersion))
                    return informationalVersion;

                var assemblyVersion = assembly.GetName().Version;
                if (assemblyVersion != null)
                {
                    if (assemblyVersion.Revision > 0)
                        return assemblyVersion.ToString();

                    if (assemblyVersion.Build >= 0)
                        return string.Format(
                            "{0}.{1}.{2}",
                            assemblyVersion.Major,
                            assemblyVersion.Minor,
                            assemblyVersion.Build);

                    return string.Format(
                        "{0}.{1}",
                        assemblyVersion.Major,
                        assemblyVersion.Minor);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogException(
                    "[VersionUtil] Failed to load version from assembly metadata.",
                    ex);
            }

            return "0.0.0";
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
                    var doc = new XmlDocument();
                    doc.Load(ManifestPath);

                    var versionNode = doc.SelectSingleNode("//Version");
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

            _cachedVersion = ResolveAssemblyVersion();
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
                    var doc = new XmlDocument();
                    doc.Load(ManifestPath);

                    var nameNode = doc.SelectSingleNode("//Name");
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
