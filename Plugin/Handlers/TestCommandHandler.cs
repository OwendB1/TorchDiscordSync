// Plugin/Handlers/TestCommandHandler.cs
using System;
using System.Collections;
using System.Reflection;
using mamba.TorchDiscordSync.Plugin.Services;
using mamba.TorchDiscordSync.Plugin.Utils;
using Sandbox;
using Sandbox.Game;
using Sandbox.Game.World;
using Sandbox.ModAPI;

namespace mamba.TorchDiscordSync.Plugin.Handlers
{
    public class TestCommandHandler
    {
        private readonly DiscordService _discordService;

        public TestCommandHandler(DiscordService discordService)
        {
            _discordService = discordService;
        }

        // ----------------------------------------------------------------
        // ENTRY POINT
        // ----------------------------------------------------------------

        public void HandleTestCommand(
            string subcommand,
            long playerSteamID,
            string playerName,
            bool isAdmin
        )
        {
            try
            {
                var parts = subcommand.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);

                // test:help
                if (parts.Length == 2 && parts[1].ToLower() == "help")
                {
                    HandleTestHelp(playerSteamID, playerName);
                    return;
                }

                if (parts.Length < 3)
                {
                    ChatUtils.SendError("Invalid format. Use: test:help or test:strong:button", playerSteamID);
                    return;
                }

                string strengthStr = parts[1].ToLower();
                string action = parts[2].ToLower();
                bool isStrong = strengthStr == "strong";

                if (action == "button")
                {
                    HandleTestButton(isStrong, playerSteamID, playerName);
                }
                else if (action == "spawn")
                {
                    if (!isAdmin)
                    {
                        _discordService.SendDirectMessage(
                            playerSteamID,
                            "Access Denied: Admin only."
                        );
                        ChatUtils.SendError("Access Denied: Admin only.", playerSteamID);
                        return;
                    }
                    HandleTestSpawn(isStrong, playerSteamID, playerName);
                }
                else
                {
                    string msg = string.Format(
                        "Unknown action '{0}'. Use 'button' or 'spawn'.",
                        action
                    );
                    _discordService.SendDirectMessage(playerSteamID, msg);
                    ChatUtils.SendError(msg, playerSteamID);
                }
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError("[TEST] Exception in HandleTestCommand: " + ex);
            }
        }

        // ----------------------------------------------------------------
        // HELP
        // ----------------------------------------------------------------

        private void HandleTestHelp(long playerSteamID, string playerName)
        {
            string helpText =
                "=== Test Commands ===\n"
                + "test:help                  - this help\n"
                + "test:strong:button         - trigger Strong signal (like Admin Panel button click)\n"
                + "test:normal:button         - trigger Normal signal (like Admin Panel button click)\n"
                + "test:strong:spawn (Admin)  - spawn a new Strong signal immediately\n"
                + "test:normal:spawn (Admin)  - spawn a new Normal signal immediately\n"
                + "\n"
                + "button: activates the existing scheduled event or spawns a new one if none exists.\n"
                + "spawn:  always spawns a brand-new signal regardless of server state.";

            ChatUtils.SendHelpText(helpText, playerSteamID);
            LoggerUtil.LogInfo("[TEST] Help requested by " + playerName);
        }

        // ----------------------------------------------------------------
        // BUTTON  – simulate Admin Panel button click
        //
        // Strategy (in order):
        //   1. Find the existing scheduled event by SubtypeId and call
        //      ActivateNow() / TriggerNow() on it directly.
        //   2. If no scheduled event exists, fall back to AddEvent with
        //      activationTime=0 so the signal spawns immediately anyway.
        //
        // This means button ALWAYS works, even with no active signal.
        // ----------------------------------------------------------------

        private void HandleTestButton(bool isStrong, long playerSteamID, string playerName)
        {
            string strengthLabel = isStrong ? "Strong" : "Normal";
            string eventSubtype = isStrong
                ? "SpawnCargoShipSignal_Strong"
                : "SpawnCargoShipSignal_Normal";

            LoggerUtil.LogInfo(
                string.Format(
                    "[TEST] Button requested by {0} ({1}) -> {2}",
                    playerName,
                    playerSteamID,
                    eventSubtype
                )
            );

            ChatUtils.SendInfo(string.Format("Test Button {0} – activating signal...", strengthLabel, playerSteamID),
                playerSteamID
            );

            ActivateOrSpawnEvent(eventSubtype, isStrong, "button");

            ChatUtils.SendSuccess(string.Format("Test Button {0} event sent.", strengthLabel, playerSteamID),
                playerSteamID
            );
        }

        // ----------------------------------------------------------------
        // SPAWN  – always add a brand-new event with activationTime = 0
        // ----------------------------------------------------------------

        private void HandleTestSpawn(bool isStrong, long playerSteamID, string playerName)
        {
            string strengthLabel = isStrong ? "Strong" : "Normal";
            string eventSubtype = isStrong
                ? "SpawnCargoShipSignal_Strong"
                : "SpawnCargoShipSignal_Normal";

            LoggerUtil.LogInfo(
                string.Format(
                    "[TEST] Spawn requested by {0} ({1}) - Strong: {2}",
                    playerName,
                    playerSteamID,
                    isStrong
                )
            );

            ChatUtils.SendInfo(string.Format("Test Spawn {0} – scheduling signal...", strengthLabel, playerSteamID),
                playerSteamID
            );

            SpawnNewEvent(eventSubtype, isStrong);

            ChatUtils.SendSuccess(string.Format("Test Spawn {0} event sent.", strengthLabel, playerSteamID),
                playerSteamID
            );

            LoggerUtil.LogInfo("[TEST] Global Event triggered for spawn: " + eventSubtype);
        }

        // ================================================================
        // CORE REFLECTION LOGIC
        // ================================================================

        /// <summary>
        /// Button path:
        ///   1. Try to find the existing scheduled event and call ActivateNow/TriggerNow.
        ///   2. If not found (no signal in world yet), fall back to AddEvent(activationTime=0).
        /// Both paths result in a signal appearing – button always works.
        /// </summary>
        private void ActivateOrSpawnEvent(string eventSubtype, bool isStrong, string source)
        {
            MySandboxGame.Static.Invoke(
                () =>
                {
                    try
                    {
                        var gameAssembly = typeof(MySandboxGame).Assembly;
                        var eventSysType = gameAssembly.GetType(
                            "Sandbox.Game.World.MyGlobalEventSystem"
                        );

                        if (eventSysType == null)
                        {
                            LoggerUtil.LogError("[TEST] MyGlobalEventSystem type not found.");
                            return;
                        }

                        var staticProp = eventSysType.GetProperty(
                            "Static",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                        );

                        object eventSys = staticProp != null ? staticProp.GetValue(null) : null;

                        if (eventSys == null)
                        {
                            LoggerUtil.LogError("[TEST] MyGlobalEventSystem.Static is null.");
                            return;
                        }

                        // ── Step 1: try to activate the existing scheduled event ──────────
                        object existing = FindEventBySubtype(eventSysType, eventSys, eventSubtype);

                        if (existing != null)
                        {
                            bool activated = TryActivateEventInstance(existing, eventSubtype);
                            if (activated)
                            {
                                LoggerUtil.LogSuccess(
                                    string.Format(
                                        "[TEST] [{0}] ActivateNow succeeded for: {1}",
                                        source,
                                        eventSubtype
                                    )
                                );
                                return;
                            }
                            LoggerUtil.LogWarning(
                                "[TEST] Found event but no activate method worked – falling back to AddEvent."
                            );
                        }
                        else
                        {
                            LoggerUtil.LogWarning(
                                string.Format(
                                    "[TEST] [{0}] Event '{1}' not in scheduler – spawning via AddEvent.",
                                    source,
                                    eventSubtype
                                )
                            );
                        }

                        // ── Step 2: fallback – spawn immediately via AddEvent ─────────────
                        AddEventViaReflection(eventSysType, eventSys, eventSubtype, isStrong, 0L);
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError("[TEST] ActivateOrSpawnEvent reflection failed: " + ex);
                    }
                },
                "TestCommandHandler.ActivateOrSpawnEvent"
            );
        }

        /// <summary>
        /// Spawn path: always calls AddEvent with activationTime=0 regardless of server state.
        /// </summary>
        private void SpawnNewEvent(string eventSubtype, bool isStrong)
        {
            MySandboxGame.Static.Invoke(
                () =>
                {
                    try
                    {
                        var gameAssembly = typeof(MySandboxGame).Assembly;
                        var eventSysType = gameAssembly.GetType(
                            "Sandbox.Game.World.MyGlobalEventSystem"
                        );

                        if (eventSysType == null)
                        {
                            LoggerUtil.LogError("[TEST] MyGlobalEventSystem type not found.");
                            return;
                        }

                        var staticProp = eventSysType.GetProperty(
                            "Static",
                            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                        );

                        object eventSys = staticProp != null ? staticProp.GetValue(null) : null;

                        if (eventSys == null)
                        {
                            LoggerUtil.LogError("[TEST] MyGlobalEventSystem.Static is null.");
                            return;
                        }

                        // activationTime = 0 → fires on the very next scheduler tick
                        AddEventViaReflection(eventSysType, eventSys, eventSubtype, isStrong, 0L);
                    }
                    catch (Exception ex)
                    {
                        LoggerUtil.LogError("[TEST] SpawnNewEvent reflection failed: " + ex);
                    }
                },
                "TestCommandHandler.SpawnNewEvent"
            );
        }

        // ----------------------------------------------------------------
        // REFLECTION HELPERS
        // ----------------------------------------------------------------

        /// <summary>
        /// Searches the known collection field/property names on MyGlobalEventSystem
        /// for an event whose SubtypeId / Name matches eventSubtype.
        /// Returns the event object or null if not found.
        /// Logs available field names at Debug level if nothing matches.
        /// </summary>
        private static object FindEventBySubtype(
            Type eventSysType,
            object eventSys,
            string eventSubtype
        )
        {
            // Candidate collection names used across SE builds
            string[] candidateNames =
            {
                "m_events",
                "m_scheduledEvents",
                "Events",
                "ScheduledEvents",
                "m_globalEvents",
            };

            foreach (string name in candidateNames)
            {
                object collection = GetMemberValue(eventSysType, eventSys, name);
                if (collection == null)
                    continue;

                var enumerable = collection as IEnumerable;
                if (enumerable == null)
                    continue;

                foreach (object item in enumerable)
                {
                    if (item == null)
                        continue;

                    string subtypeValue =
                        GetStringMemberValue(item, "SubtypeId")
                        ?? GetStringMemberValue(item, "Subtype")
                        ?? GetStringMemberValue(item, "EventSubtype")
                        ?? GetStringMemberValue(item, "Id")
                        ?? GetStringMemberValue(item, "Name");

                    if (
                        string.Equals(
                            subtypeValue,
                            eventSubtype,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        LoggerUtil.LogDebug(
                            string.Format(
                                "[TEST] Found event '{0}' in collection '{1}'",
                                eventSubtype,
                                name
                            )
                        );
                        return item;
                    }
                }
            }

            // Debug dump so we can find the right names next run
            LogMembersForDebug(eventSysType, eventSys);
            return null;
        }

        /// <summary>
        /// Tries to call ActivateNow / TriggerNow / Trigger / Activate / TriggerEvent
        /// on the event instance (first method found that succeeds).
        /// Returns true if any method was invoked without exception.
        /// </summary>
        private static bool TryActivateEventInstance(object eventInstance, string eventSubtype)
        {
            string[] methodNames =
            {
                "ActivateNow",
                "TriggerNow",
                "Trigger",
                "Activate",
                "TriggerEvent",
            };
            Type eventType = eventInstance.GetType();

            foreach (string methodName in methodNames)
            {
                var method = eventType.GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                );

                if (method == null)
                    continue;

                try
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length == 0)
                    {
                        method.Invoke(eventInstance, null);
                    }
                    else
                    {
                        object[] args = new object[parameters.Length];
                        for (int i = 0; i < parameters.Length; i++)
                            args[i] = parameters[i].HasDefaultValue
                                ? parameters[i].DefaultValue
                                : null;
                        method.Invoke(eventInstance, args);
                    }

                    LoggerUtil.LogDebug(
                        string.Format(
                            "[TEST] Called {0}.{1}() for event '{2}'",
                            eventType.Name,
                            methodName,
                            eventSubtype
                        )
                    );
                    return true;
                }
                catch (Exception ex)
                {
                    LoggerUtil.LogWarning(
                        string.Format(
                            "[TEST] {0} threw: {1}",
                            methodName,
                            ex.InnerException != null ? ex.InnerException.Message : ex.Message
                        )
                    );
                }
            }

            // Dump available methods so we can pick the right one next time
            var allMethods = eventType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            var methodList = new System.Text.StringBuilder();
            foreach (var m in allMethods)
            {
                if (methodList.Length > 0)
                    methodList.Append(", ");
                methodList.Append(m.Name);
            }
            LoggerUtil.LogWarning(
                string.Format(
                    "[TEST] No activate method found on {0}. Public methods: {1}",
                    eventType.Name,
                    methodList
                )
            );

            return false;
        }

        /// <summary>
        /// Calls MyGlobalEventSystem.AddEvent(subtype, eventType, activationTime, player).
        /// activationTime = 0  → fires on the very next scheduler tick.
        /// FIX: enum value is derived from isStrong (was hardcoded to "Normal" in previous version).
        /// </summary>
        private static void AddEventViaReflection(
            Type eventSysType,
            object eventSys,
            string eventSubtype,
            bool isStrong,
            long activationTime
        )
        {
            var addEventMethod = eventSysType.GetMethod(
                "AddEvent",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

            if (addEventMethod == null)
            {
                LoggerUtil.LogError("[TEST] AddEvent method not found on MyGlobalEventSystem.");
                return;
            }

            var gameAssembly = typeof(MySandboxGame).Assembly;
            var enumType = gameAssembly.GetType("Sandbox.Game.World.MyGlobalEventTypeEnum");

            if (enumType == null)
            {
                LoggerUtil.LogError("[TEST] MyGlobalEventTypeEnum not found.");
                return;
            }

            // FIX: was always Enum.Parse(enumType, "Normal") in original code.
            object enumValue = Enum.Parse(enumType, isStrong ? "Strong" : "Normal");

            object[] callParams = { eventSubtype, enumValue, activationTime, null };
            addEventMethod.Invoke(eventSys, callParams);

            LoggerUtil.LogInfo(
                string.Format(
                    "[TEST] AddEvent -> subtype={0}, type={1}, activationTime={2}",
                    eventSubtype,
                    enumValue,
                    activationTime
                )
            );
        }

        // ----------------------------------------------------------------
        // UTILITY HELPERS
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns the value of a field or property (by name) on obj, or null if not found.
        /// </summary>
        private static object GetMemberValue(Type type, object obj, string name)
        {
            if (obj == null || type == null)
                return null;

            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var field = type.GetField(name, flags);
            if (field != null)
                return field.GetValue(obj);

            var prop = type.GetProperty(name, flags);
            if (prop != null)
            {
                try
                {
                    return prop.GetValue(obj, null);
                }
                catch
                { /* ignore */
                }
            }

            return null;
        }

        /// <summary>
        /// Returns the string value of a field or property on obj, or null if not found / not a string.
        /// </summary>
        private static string GetStringMemberValue(object obj, string memberName)
        {
            if (obj == null)
                return null;

            object val = GetMemberValue(obj.GetType(), obj, memberName);
            return val != null ? val.ToString() : null;
        }

        /// <summary>
        /// Logs all fields and properties on MyGlobalEventSystem at Debug level.
        /// Used to identify the correct collection name when reflection lookup fails.
        /// </summary>
        private static void LogMembersForDebug(Type type, object instance)
        {
            try
            {
                const BindingFlags flags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                var fields = type.GetFields(flags);
                var props = type.GetProperties(flags);

                var fieldNames = new System.Text.StringBuilder();
                foreach (var f in fields)
                {
                    if (fieldNames.Length > 0)
                        fieldNames.Append(", ");
                    fieldNames.Append(f.Name);
                }

                var propNames = new System.Text.StringBuilder();
                foreach (var p in props)
                {
                    if (propNames.Length > 0)
                        propNames.Append(", ");
                    propNames.Append(p.Name);
                }

                LoggerUtil.LogDebug("[TEST] MyGlobalEventSystem fields: " + fieldNames);
                LoggerUtil.LogDebug("[TEST] MyGlobalEventSystem props:  " + propNames);
            }
            catch
            {
                // debug only – swallow
            }
        }
    }
}