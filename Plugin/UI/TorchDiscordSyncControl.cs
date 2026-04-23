using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TorchDiscordSync.Plugin.Config;

namespace TorchDiscordSync.Plugin.UI
{
    public sealed class TorchDiscordSyncControl : UserControl
    {
        private readonly TorchDiscordSyncPlugin _plugin;
        private readonly ConfigEditorViewModel _viewModel;
        private readonly TextBlock _statusText;

        public TorchDiscordSyncControl(TorchDiscordSyncPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _viewModel = new ConfigEditorViewModel();
            _viewModel.Reset(_plugin.Config ?? new MainConfig());

            DataContext = _viewModel;
            _statusText = new TextBlock
            {
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DimGray,
                Text = "Save writes MainConfig.xml and restarts the Discord host runtime.",
                TextWrapping = TextWrapping.Wrap,
            };

            MinWidth = 920;
            Content = BuildLayout();
        }

        private UIElement BuildLayout()
        {
            var root = new DockPanel();

            var header = BuildHeader();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var contentPanel = new StackPanel
            {
                Margin = new Thickness(12),
                Orientation = Orientation.Vertical,
            };

            contentPanel.Children.Add(BuildOverviewSection());
            contentPanel.Children.Add(BuildDiscordSection());
            contentPanel.Children.Add(BuildChatSection());
            contentPanel.Children.Add(BuildDeathSection());
            contentPanel.Children.Add(BuildMonitoringSection());
            contentPanel.Children.Add(BuildFactionSection());
            contentPanel.Children.Add(BuildStorageSection());

            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = contentPanel,
            });

            return root;
        }

        private UIElement BuildHeader()
        {
            var header = new DockPanel
            {
                Margin = new Thickness(12, 12, 12, 0),
                LastChildFill = true,
            };

            var buttonBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            var saveButton = new Button
            {
                Content = "Save Config",
                Width = 120,
                Margin = new Thickness(0, 0, 8, 0),
            };
            saveButton.Click += SaveButtonOnClick;
            buttonBar.Children.Add(saveButton);

            var reloadButton = new Button
            {
                Content = "Reset",
                Width = 100,
            };
            reloadButton.Click += ReloadButtonOnClick;
            buttonBar.Children.Add(reloadButton);

            DockPanel.SetDock(buttonBar, Dock.Left);
            header.Children.Add(buttonBar);
            header.Children.Add(_statusText);

            return header;
        }

        private GroupBox BuildOverviewSection()
        {
            var section = CreateSection("Core");
            var panel = GetSectionPanel(section);

            panel.Children.Add(CreateNote(
                "Admin Steam IDs accept commas, spaces, or new lines. Save recreates the Discord host and live timer services."));

            AddCheckBoxRow(panel, "WorkingCopy.Enabled", "Plugin enabled");
            AddCheckBoxRow(panel, "WorkingCopy.Debug", "Debug logging");
            AddTextRow(panel, "Cleanup interval (seconds)", "WorkingCopy.CleanupIntervalSeconds");
            AddTextRow(panel, "Damage history max age (seconds)", "WorkingCopy.DamageHistoryMaxSeconds");
            AddTextRow(
                panel,
                "Admin Steam IDs",
                "AdminSteamIdsText",
                "One Steam ID per line, or separate values with commas.",
                multiline: true);

            return section;
        }

        private GroupBox BuildDiscordSection()
        {
            var section = CreateSection("Discord");
            var panel = GetSectionPanel(section);

            AddTextRow(panel, "Bot token", "WorkingCopy.Discord.BotToken");
            AddTextRow(panel, "Guild ID", "WorkingCopy.Discord.GuildID");
            AddTextRow(panel, "Chat channel ID", "WorkingCopy.Discord.ChatChannelId");
            AddTextRow(panel, "Staff log channel ID", "WorkingCopy.Discord.StaffLog");
            AddTextRow(panel, "Status channel ID", "WorkingCopy.Discord.StatusChannelId");
            AddTextRow(panel, "Sim speed channel ID", "WorkingCopy.Discord.SimSpeedChannelId");
            AddTextRow(panel, "Player count channel ID", "WorkingCopy.Discord.PlayerCountChannelId");
            AddTextRow(panel, "Faction category ID", "WorkingCopy.Discord.FactionCategoryId");
            AddTextRow(panel, "Admin alert channel ID", "WorkingCopy.Discord.AdminAlertChannelId");
            AddTextRow(panel, "Admin bot channel ID", "WorkingCopy.Discord.AdminBotChannelId");
            AddTextRow(panel, "Faction sync interval (seconds)", "WorkingCopy.Discord.SyncIntervalSeconds");
            AddTextRow(
                panel,
                "Presence update interval (seconds)",
                "WorkingCopy.Discord.PresenceUpdateIntervalSeconds");

            return section;
        }

        private GroupBox BuildChatSection()
        {
            var section = CreateSection("Chat");
            var panel = GetSectionPanel(section);

            AddCheckBoxRow(panel, "WorkingCopy.Chat.Enabled", "Chat sync enabled");
            AddCheckBoxRow(panel, "WorkingCopy.Chat.BotToGame", "Relay Discord bot messages into game");
            AddCheckBoxRow(panel, "WorkingCopy.Chat.ServerToDiscord", "Relay server messages to Discord");
            AddCheckBoxRow(panel, "WorkingCopy.Chat.UseFactionChat", "Enable faction chat sync");
            AddCheckBoxRow(panel, "WorkingCopy.Chat.StripEmojisForInGameChat", "Strip emojis before sending to in-game chat");
            AddTextRow(panel, "Admin log channel ID", "WorkingCopy.Chat.AdminLogChannelId");
            AddTextRow(
                panel,
                "Game -> Discord format",
                "WorkingCopy.Chat.GameToDiscordFormat",
                "Available placeholders: {p}, {msg}");
            AddTextRow(
                panel,
                "Discord -> Game format",
                "WorkingCopy.Chat.DiscordToGameFormat",
                "Available placeholders: {p}, {msg}");
            AddComboRow(panel, "Global in-game color", "WorkingCopy.Chat.GlobalColor");
            AddComboRow(panel, "Faction in-game color", "WorkingCopy.Chat.FactionColor");
            AddTextRow(panel, "Player connect message", "WorkingCopy.Chat.ConnectMessage");
            AddTextRow(panel, "Player join message", "WorkingCopy.Chat.JoinMessage");
            AddTextRow(panel, "Player leave message", "WorkingCopy.Chat.LeaveMessage");
            AddTextRow(panel, "Faction game -> Discord format", "WorkingCopy.Chat.FactionChatFormat");
            AddTextRow(panel, "Faction Discord -> game format", "WorkingCopy.Chat.FactionDiscordFormat");

            return section;
        }

        private GroupBox BuildDeathSection()
        {
            var section = CreateSection("Death Logging");
            var panel = GetSectionPanel(section);

            AddCheckBoxRow(panel, "WorkingCopy.Death.Enabled", "Death logging enabled");
            AddCheckBoxRow(panel, "WorkingCopy.Death.LogToDiscord", "Send death events to Discord");
            AddCheckBoxRow(panel, "WorkingCopy.Death.AnnounceInGame", "Announce deaths in-game");
            AddCheckBoxRow(panel, "WorkingCopy.Death.DetectRetaliation", "Track retaliation windows");
            AddCheckBoxRow(panel, "WorkingCopy.Death.EnableLocationZones", "Enable death location zones");
            AddCheckBoxRow(panel, "WorkingCopy.Death.GridDetectionEnabled", "Enable grid detection");
            AddCheckBoxRow(panel, "WorkingCopy.Death.ShowGridName", "Show grid names in death messages");
            AddTextRow(panel, "Retaliation window (minutes)", "WorkingCopy.Death.RetaliationWindowMinutes");
            AddTextRow(panel, "Old revenge window (hours)", "WorkingCopy.Death.OldRevengeWindowHours");
            AddTextRow(
                panel,
                "Deduplication window (seconds)",
                "WorkingCopy.Death.MessageDeduplicationWindowSeconds");
            AddTextRow(panel, "Inner system max distance (km)", "WorkingCopy.Death.InnerSystemMaxKm");
            AddTextRow(panel, "Outer space max distance (km)", "WorkingCopy.Death.OuterSpaceMaxKm");
            AddTextRow(
                panel,
                "Planet proximity multiplier",
                "WorkingCopy.Death.PlanetProximityMultiplier");
            AddTextRow(
                panel,
                "Death message emotes",
                "WorkingCopy.Death.DeathMessageEmotes",
                "Comma-separated emoji list.");

            return section;
        }

        private GroupBox BuildMonitoringSection()
        {
            var section = CreateSection("Monitoring");
            var panel = GetSectionPanel(section);

            AddCheckBoxRow(panel, "WorkingCopy.Monitoring.Enabled", "Monitoring enabled");
            AddCheckBoxRow(panel, "WorkingCopy.Monitoring.EnableSimSpeedMonitoring", "Track sim speed channel");
            AddCheckBoxRow(panel, "WorkingCopy.Monitoring.EnableSimSpeedAlerts", "Send sim speed alerts");
            AddCheckBoxRow(panel, "WorkingCopy.Monitoring.EnablePlayerCountMonitoring", "Track player count channel");
            AddCheckBoxRow(panel, "WorkingCopy.Monitoring.EnablePlayerCountAlerts", "Send player count alerts");
            AddCheckBoxRow(panel, "WorkingCopy.Monitoring.EnableAdminAlerts", "Send admin alerts");
            AddTextRow(panel, "Sim speed threshold", "WorkingCopy.Monitoring.SimSpeedThreshold");
            AddTextRow(
                panel,
                "Monitoring interval (seconds)",
                "WorkingCopy.Monitoring.StatusUpdateIntervalSeconds");
            AddTextRow(
                panel,
                "Sim speed channel name format",
                "WorkingCopy.Monitoring.SimSpeedChannelNameFormat",
                "Available placeholders: {emoji}, {ss}");
            AddTextRow(panel, "Sim speed normal emoji", "WorkingCopy.Monitoring.SimSpeedNormalEmoji");
            AddTextRow(panel, "Sim speed warning emoji", "WorkingCopy.Monitoring.SimSpeedWarningEmoji");
            AddTextRow(
                panel,
                "Sim speed alert message",
                "WorkingCopy.Monitoring.SimSpeedAlertMessage",
                "Available placeholders: {ss}, {threshold}",
                multiline: true);
            AddTextRow(
                panel,
                "Sim speed alert cooldown (seconds)",
                "WorkingCopy.Monitoring.SimSpeedAlertCooldownSeconds");
            AddTextRow(
                panel,
                "Player count channel name format",
                "WorkingCopy.Monitoring.PlayerCountChannelNameFormat",
                "Available placeholders: {p}, {pp}");
            AddTextRow(
                panel,
                "Player count alert threshold",
                "WorkingCopy.Monitoring.PlayerCountAlertThreshold");
            AddTextRow(
                panel,
                "Player count alert message",
                "WorkingCopy.Monitoring.PlayerCountAlertMessage",
                "Available placeholders: {p}, {pp}");
            AddTextRow(panel, "Server started message", "WorkingCopy.Monitoring.ServerStartedMessage");
            AddTextRow(panel, "Server stopped message", "WorkingCopy.Monitoring.ServerStoppedMessage");
            AddTextRow(panel, "Server restarted message", "WorkingCopy.Monitoring.ServerRestartedMessage");
            AddTextRow(
                panel,
                "Server crashed message",
                "WorkingCopy.Monitoring.ServerCrashedMessage",
                null,
                multiline: true);

            return section;
        }

        private GroupBox BuildFactionSection()
        {
            var section = CreateSection("Faction");
            var panel = GetSectionPanel(section);

            AddCheckBoxRow(panel, "WorkingCopy.Faction.Enabled", "Faction sync enabled");
            AddCheckBoxRow(panel, "WorkingCopy.Faction.AutoCreateChannels", "Auto-create text channels");
            AddCheckBoxRow(panel, "WorkingCopy.Faction.AutoCreateVoice", "Auto-create voice channels");
            AddCheckBoxRow(
                panel,
                "WorkingCopy.Faction.FactionDiscordToGlobalFallback",
                "Fallback Discord faction messages to global chat");

            return section;
        }

        private GroupBox BuildStorageSection()
        {
            var section = CreateSection("Data Storage");
            var panel = GetSectionPanel(section);

            AddCheckBoxRow(panel, "WorkingCopy.DataStorage.UseSQLite", "Use SQLite when available");
            AddCheckBoxRow(panel, "WorkingCopy.DataStorage.SaveEventLogs", "Persist event logs");
            AddCheckBoxRow(panel, "WorkingCopy.DataStorage.SaveDeathHistory", "Persist death history");
            AddCheckBoxRow(panel, "WorkingCopy.DataStorage.SaveGlobalChat", "Persist global chat");
            AddCheckBoxRow(panel, "WorkingCopy.DataStorage.SaveFactionChat", "Persist faction chat");
            AddCheckBoxRow(panel, "WorkingCopy.DataStorage.SavePrivateChat", "Persist private chat");

            return section;
        }

        private void SaveButtonOnClick(object sender, RoutedEventArgs e)
        {
            Keyboard.ClearFocus();

            if (HasValidationErrors(this))
            {
                SetStatus("Fix the highlighted fields before saving.", isSuccess: false);
                return;
            }

            if (!_viewModel.TryCreateConfig(out var updatedConfig, out var error))
            {
                SetStatus(error, isSuccess: false);
                return;
            }

            if (!_plugin.ApplyConfiguration(updatedConfig, out error))
            {
                SetStatus(error, isSuccess: false);
                return;
            }

            _viewModel.Reset(_plugin.Config ?? updatedConfig);
            SetStatus("Configuration saved and Discord runtime restarted.", isSuccess: true);
        }

        private void ReloadButtonOnClick(object sender, RoutedEventArgs e)
        {
            _viewModel.Reset(_plugin.Config ?? new MainConfig());
            SetStatus("Unsaved changes were discarded.", isSuccess: true);
        }

        private void SetStatus(string message, bool isSuccess)
        {
            _statusText.Text = string.IsNullOrWhiteSpace(message)
                ? string.Empty
                : message;
            _statusText.Foreground = isSuccess ? Brushes.ForestGreen : Brushes.Firebrick;
        }

        private static GroupBox CreateSection(string header)
        {
            return new GroupBox
            {
                Header = header,
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(12),
                Content = new StackPanel(),
            };
        }

        private static StackPanel GetSectionPanel(ContentControl section)
        {
            return (StackPanel)section.Content;
        }

        private static TextBlock CreateNote(string text)
        {
            return new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = Brushes.DimGray,
                Text = text,
                TextWrapping = TextWrapping.Wrap,
            };
        }

        private void AddTextRow(
            Panel panel,
            string label,
            string bindingPath,
            string toolTip = null,
            bool multiline = false)
        {
            var textBox = CreateTextBox(bindingPath, toolTip, multiline);
            panel.Children.Add(CreateLabeledRow(label, textBox));
        }

        private void AddComboRow(Panel panel, string label, string bindingPath)
        {
            var comboBox = new ComboBox
            {
                MinWidth = 320,
                MaxWidth = 420,
                ItemsSource = _viewModel.ColorNames,
                IsTextSearchEnabled = true,
            };
            BindingOperations.SetBinding(
                comboBox,
                ComboBox.SelectedItemProperty,
                CreateBinding(bindingPath, UpdateSourceTrigger.PropertyChanged));

            panel.Children.Add(CreateLabeledRow(label, comboBox));
        }

        private static void AddCheckBoxRow(Panel panel, string bindingPath, string label)
        {
            var checkBox = new CheckBox
            {
                Content = label,
                Margin = new Thickness(0, 0, 0, 8),
            };
            BindingOperations.SetBinding(
                checkBox,
                CheckBox.IsCheckedProperty,
                new Binding(bindingPath) { Mode = BindingMode.TwoWay });

            panel.Children.Add(checkBox);
        }

        private static FrameworkElement CreateLabeledRow(string label, FrameworkElement input)
        {
            var grid = new Grid
            {
                Margin = new Thickness(0, 0, 0, 8),
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var textBlock = new TextBlock
            {
                Margin = new Thickness(0, 4, 16, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Text = label,
                TextWrapping = TextWrapping.Wrap,
            };

            Grid.SetColumn(textBlock, 0);
            Grid.SetColumn(input, 1);

            grid.Children.Add(textBlock);
            grid.Children.Add(input);

            return grid;
        }

        private static TextBox CreateTextBox(string bindingPath, string toolTip, bool multiline)
        {
            var textBox = new TextBox
            {
                MinWidth = 320,
                ToolTip = toolTip,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                AcceptsReturn = multiline,
                VerticalScrollBarVisibility = multiline
                    ? ScrollBarVisibility.Auto
                    : ScrollBarVisibility.Disabled,
            };

            if (multiline)
            {
                textBox.MinHeight = 72;
                textBox.MaxHeight = 120;
            }

            BindingOperations.SetBinding(
                textBox,
                TextBox.TextProperty,
                CreateBinding(bindingPath, UpdateSourceTrigger.LostFocus));

            return textBox;
        }

        private static Binding CreateBinding(string bindingPath, UpdateSourceTrigger updateSourceTrigger)
        {
            return new Binding(bindingPath)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = updateSourceTrigger,
                ValidatesOnExceptions = true,
                NotifyOnValidationError = true,
            };
        }

        private static bool HasValidationErrors(DependencyObject dependencyObject)
        {
            if (dependencyObject == null)
                return false;

            if (Validation.GetHasError(dependencyObject))
                return true;

            var childCount = VisualTreeHelper.GetChildrenCount(dependencyObject);
            for (var index = 0; index < childCount; index++)
            {
                if (HasValidationErrors(VisualTreeHelper.GetChild(dependencyObject, index)))
                    return true;
            }

            return false;
        }
    }
}
