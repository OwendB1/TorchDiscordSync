using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using TorchDiscordSync.Plugin.Config;
using VRageMath;

namespace TorchDiscordSync.Plugin.UI
{
    public sealed class ConfigEditorViewModel : INotifyPropertyChanged
    {
        private MainConfig _workingCopy;
        private string _adminSteamIdsText;

        public ConfigEditorViewModel()
        {
            ColorNames = new ObservableCollection<string>(
                typeof(Color)
                    .GetProperties(BindingFlags.Public | BindingFlags.Static)
                    .Select(property => property.Name)
                    .OrderBy(name => name));

            Reset(new MainConfig());
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<string> ColorNames { get; }

        public MainConfig WorkingCopy
        {
            get { return _workingCopy; }
            private set
            {
                if (ReferenceEquals(_workingCopy, value))
                    return;

                _workingCopy = value;
                OnPropertyChanged(nameof(WorkingCopy));
            }
        }

        public string AdminSteamIdsText
        {
            get { return _adminSteamIdsText; }
            set
            {
                if (string.Equals(_adminSteamIdsText, value, StringComparison.Ordinal))
                    return;

                _adminSteamIdsText = value;
                OnPropertyChanged(nameof(AdminSteamIdsText));
            }
        }

        public void Reset(MainConfig source)
        {
            var workingCopy = (source ?? new MainConfig()).Clone();
            workingCopy.EnsureDefaults();

            WorkingCopy = workingCopy;
            AdminSteamIdsText = FormatSteamIds(workingCopy.AdminSteamIDs);
        }

        public bool TryCreateConfig(out MainConfig config, out string error)
        {
            config = null;

            if (!TryParseSteamIds(AdminSteamIdsText, out var steamIds, out error))
                return false;

            var updated = (WorkingCopy ?? new MainConfig()).Clone();
            updated.AdminSteamIDs = steamIds;
            updated.EnsureDefaults();

            config = updated;
            return true;
        }

        private static string FormatSteamIds(long[] steamIds)
        {
            return steamIds == null || steamIds.Length == 0
                ? string.Empty
                : string.Join(Environment.NewLine, steamIds);
        }

        private static bool TryParseSteamIds(string text, out long[] steamIds, out string error)
        {
            var tokens = (text ?? string.Empty)
                .Split(
                    new[] { ',', ';', '\r', '\n', '\t', ' ' },
                    StringSplitOptions.RemoveEmptyEntries);

            if (tokens.Length == 0)
            {
                steamIds = Array.Empty<long>();
                error = null;
                return true;
            }

            var parsedIds = new long[tokens.Length];
            for (var index = 0; index < tokens.Length; index++)
            {
                if (!long.TryParse(tokens[index], out parsedIds[index]))
                {
                    steamIds = Array.Empty<long>();
                    error = "Admin Steam IDs must contain only numeric values.";
                    return false;
                }
            }

            steamIds = parsedIds;
            error = null;
            return true;
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
