// Plugin/Services/SqliteDatabaseService.cs
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using mamba.TorchDiscordSync.Plugin.Models;
using mamba.TorchDiscordSync.Plugin.Utils;

namespace mamba.TorchDiscordSync.Plugin.Services
{
    /// <summary>
    /// SQLite-backed database service.
    /// Handles all CRUD operations for factions, players, events, deaths,
    /// verification history, pending verifications, and verified players.
    ///
    /// This service is used as the PRIMARY storage when config.DataStorage.UseSQLite = true.
    /// The legacy XML DatabaseService acts as fallback if SQLite fails to initialize.
    /// </summary>
    public class SqliteDatabaseService
    {
        private readonly string _dbPath;
        private SQLiteConnection _connection;
        private bool _initialized = false;
        private readonly object _lock = new object();

        public bool IsInitialized => _initialized;

        /// <summary>
        /// Initialize the SQLite database at the given path.
        /// Creates the file and all tables if they do not exist.
        /// Throws on fatal error – caller should catch and fall back to XML.
        /// </summary>
        public SqliteDatabaseService(string dataDirectory)
        {
            _dbPath = Path.Combine(dataDirectory, "TorchDiscordSync.db");
            LoggerUtil.LogDebug($"[SQLITE] Database path: {_dbPath}");

            // Pre-check: verify System.Data.SQLite assembly can be resolved.
            // If not present in Torch root, this gives a clear actionable error
            // instead of a cryptic IL/type load failure.
            try
            {
                var testType = typeof(SQLiteConnection);
                LoggerUtil.LogDebug($"[SQLITE] System.Data.SQLite assembly resolved: {testType.Assembly.Location}");
            }
            catch (Exception ex)
            {
                throw new FileNotFoundException(
                    "[SQLITE] System.Data.SQLite.dll not found in Torch root directory. " +
                    "Place System.Data.SQLite.dll and x64\\SQLite.Interop.dll in your Torch server root to enable SQLite storage. " +
                    "Falling back to XML storage. Original error: " + ex.Message);
            }

            Initialize();
        }

        // ================================================================
        // INIT & SCHEMA
        // ================================================================

        private void Initialize()
        {
            try
            {
                LoggerUtil.LogDebug("[SQLITE] Starting initialization...");

                bool isNewFile = !File.Exists(_dbPath);
                if (isNewFile)
                    LoggerUtil.LogDebug("[SQLITE] Database file does not exist – will be created.");
                else
                    LoggerUtil.LogDebug("[SQLITE] Database file found – opening existing database.");

                _connection = new SQLiteConnection($"Data Source={_dbPath};Version=3;");
                _connection.Open();
                LoggerUtil.LogDebug("[SQLITE] Connection opened successfully.");

                EnableWAL();
                CreateTables();

                _initialized = true;
                LoggerUtil.LogSuccess($"[SQLITE] Initialized successfully. Path: {_dbPath}");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[SQLITE] Initialization FAILED: {ex.Message}\n{ex.StackTrace}");
                _initialized = false;
                throw; // re-throw so DatabaseService can fall back to XML
            }
        }

        private void EnableWAL()
        {
            LoggerUtil.LogDebug("[SQLITE] Enabling WAL journal mode for better concurrency...");
            using (var cmd = new SQLiteCommand("PRAGMA journal_mode=WAL;", _connection))
            {
                var result = cmd.ExecuteScalar()?.ToString();
                LoggerUtil.LogDebug($"[SQLITE] journal_mode = {result}");
            }
            using (var cmd = new SQLiteCommand("PRAGMA foreign_keys = ON;", _connection))
                cmd.ExecuteNonQuery();
        }

        private void CreateTables()
        {
            LoggerUtil.LogDebug("[SQLITE] Checking / creating tables...");

            ExecuteDDL("factions", @"
                CREATE TABLE IF NOT EXISTS factions (
                    faction_id              INTEGER PRIMARY KEY,
                    tag                     TEXT NOT NULL,
                    name                    TEXT,
                    discord_role_id         INTEGER DEFAULT 0,
                    discord_channel_id      INTEGER DEFAULT 0,
                    discord_role_name       TEXT,
                    discord_channel_name    TEXT,
                    discord_voice_id        INTEGER DEFAULT 0,
                    discord_voice_name      TEXT,
                    game_faction_chat_id    INTEGER DEFAULT 0,
                    sync_status             TEXT DEFAULT 'Pending',
                    synced_at               TEXT,
                    synced_by               TEXT,
                    error_message           TEXT,
                    created_at              TEXT NOT NULL,
                    updated_at              TEXT NOT NULL
                );");

            ExecuteDDL("faction_players", @"
                CREATE TABLE IF NOT EXISTS faction_players (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    faction_id      INTEGER NOT NULL,
                    player_id       INTEGER,
                    steam_id        INTEGER NOT NULL,
                    original_nick   TEXT,
                    synced_nick     TEXT,
                    discord_user_id INTEGER DEFAULT 0,
                    UNIQUE(faction_id, steam_id)
                );");

            ExecuteDDL("faction_channels_created", @"
                CREATE TABLE IF NOT EXISTS faction_channels_created (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    faction_id      INTEGER NOT NULL,
                    channel_id      INTEGER NOT NULL,
                    channel_name    TEXT,
                    channel_type    TEXT,
                    created_at      TEXT,
                    deleted_on_undo INTEGER DEFAULT 0
                );");

            ExecuteDDL("players", @"
                CREATE TABLE IF NOT EXISTS players (
                    steam_id        INTEGER PRIMARY KEY,
                    player_id       INTEGER,
                    original_nick   TEXT,
                    synced_nick     TEXT,
                    faction_id      INTEGER DEFAULT 0,
                    discord_user_id INTEGER DEFAULT 0,
                    created_at      TEXT NOT NULL,
                    updated_at      TEXT NOT NULL
                );");

            ExecuteDDL("event_logs", @"
                CREATE TABLE IF NOT EXISTS event_logs (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_type  TEXT,
                    details     TEXT,
                    timestamp   TEXT NOT NULL
                );");

            ExecuteDDL("death_history", @"
                CREATE TABLE IF NOT EXISTS death_history (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    killer_steam_id INTEGER DEFAULT 0,
                    victim_steam_id INTEGER NOT NULL,
                    death_time      TEXT NOT NULL,
                    death_type      TEXT,
                    killer_name     TEXT,
                    victim_name     TEXT,
                    weapon          TEXT,
                    location        TEXT
                );");

            ExecuteDDL("verification_history", @"
                CREATE TABLE IF NOT EXISTS verification_history (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    steam_id        INTEGER NOT NULL,
                    discord_username TEXT,
                    discord_user_id INTEGER DEFAULT 0,
                    verified_at     TEXT NOT NULL,
                    status          TEXT
                );");

            ExecuteDDL("pending_verifications", @"
                CREATE TABLE IF NOT EXISTS pending_verifications (
                    steam_id            INTEGER PRIMARY KEY,
                    discord_username    TEXT,
                    verification_code   TEXT,
                    code_generated_at   TEXT NOT NULL,
                    expires_at          TEXT NOT NULL,
                    game_player_name    TEXT
                );");

            ExecuteDDL("verified_players", @"
                CREATE TABLE IF NOT EXISTS verified_players (
                    steam_id            INTEGER PRIMARY KEY,
                    discord_username    TEXT,
                    discord_user_id     INTEGER DEFAULT 0,
                    verified_at         TEXT NOT NULL,
                    game_player_name    TEXT
                );");

            LoggerUtil.LogDebug("[SQLITE] All tables verified/created.");
        }

        private void ExecuteDDL(string tableName, string sql)
        {
            try
            {
                using (var cmd = new SQLiteCommand(sql.Trim(), _connection))
                    cmd.ExecuteNonQuery();
                LoggerUtil.LogDebug($"[SQLITE] Table '{tableName}': OK");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogError($"[SQLITE] Failed to create table '{tableName}': {ex.Message}");
                throw;
            }
        }

        // ================================================================
        // HELPER
        // ================================================================

        private SQLiteCommand Cmd(string sql) => new SQLiteCommand(sql, _connection);

        private static string ToDb(DateTime? dt) => dt?.ToString("o");
        private static string ToDb(DateTime dt) => dt.ToString("o");
        private static DateTime ParseDt(object val)
        {
            if (val == null || val is DBNull) return DateTime.MinValue;
            return DateTime.Parse(val.ToString());
        }
        private static DateTime? ParseDtN(object val)
        {
            if (val == null || val is DBNull) return null;
            return DateTime.Parse(val.ToString());
        }
        private static long ToLong(object val) => val == null || val is DBNull ? 0L : Convert.ToInt64(val);
        private static ulong ToUlong(object val) => val == null || val is DBNull ? 0UL : Convert.ToUInt64(val);
        private static int ToInt(object val) => val == null || val is DBNull ? 0 : Convert.ToInt32(val);
        private static string ToStr(object val) => val == null || val is DBNull ? null : val.ToString();

        // ================================================================
        // FACTIONS
        // ================================================================

        public void SaveFaction(FactionModel faction)
        {
            lock (_lock)
            {
                LoggerUtil.LogDebug($"[SQLITE] SaveFaction: faction_id={faction.FactionID}, tag={faction.Tag}");
                using (var tx = _connection.BeginTransaction())
                {
                    try
                    {
                        string now = ToDb(DateTime.UtcNow);

                        // Upsert faction row
                        using (var cmd = Cmd(@"
                            INSERT INTO factions
                                (faction_id, tag, name, discord_role_id, discord_channel_id,
                                 discord_role_name, discord_channel_name,
                                 discord_voice_id, discord_voice_name,
                                 game_faction_chat_id, sync_status, synced_at, synced_by,
                                 error_message, created_at, updated_at)
                            VALUES
                                (@id, @tag, @name, @roleId, @channelId,
                                 @roleName, @channelName,
                                 @voiceId, @voiceName,
                                 @gameChatId, @syncStatus, @syncedAt, @syncedBy,
                                 @error, @now, @now)
                            ON CONFLICT(faction_id) DO UPDATE SET
                                tag=excluded.tag, name=excluded.name,
                                discord_role_id=excluded.discord_role_id,
                                discord_channel_id=excluded.discord_channel_id,
                                discord_role_name=excluded.discord_role_name,
                                discord_channel_name=excluded.discord_channel_name,
                                discord_voice_id=excluded.discord_voice_id,
                                discord_voice_name=excluded.discord_voice_name,
                                game_faction_chat_id=excluded.game_faction_chat_id,
                                sync_status=excluded.sync_status,
                                synced_at=excluded.synced_at,
                                synced_by=excluded.synced_by,
                                error_message=excluded.error_message,
                                updated_at=excluded.updated_at;"))
                        {
                            cmd.Parameters.AddWithValue("@id", faction.FactionID);
                            cmd.Parameters.AddWithValue("@tag", faction.Tag ?? "");
                            cmd.Parameters.AddWithValue("@name", faction.Name ?? "");
                            cmd.Parameters.AddWithValue("@roleId", (long)faction.DiscordRoleID);
                            cmd.Parameters.AddWithValue("@channelId", (long)faction.DiscordChannelID);
                            cmd.Parameters.AddWithValue("@roleName", faction.DiscordRoleName ?? "");
                            cmd.Parameters.AddWithValue("@channelName", faction.DiscordChannelName ?? "");
                            cmd.Parameters.AddWithValue("@voiceId", (long)faction.DiscordVoiceChannelID);
                            cmd.Parameters.AddWithValue("@voiceName", faction.DiscordVoiceChannelName ?? "");
                            cmd.Parameters.AddWithValue("@gameChatId", faction.GameFactionChatId);
                            cmd.Parameters.AddWithValue("@syncStatus", faction.SyncStatus ?? "Pending");
                            cmd.Parameters.AddWithValue("@syncedAt", ToDb(faction.SyncedAt) ?? (object)DBNull.Value);
                            cmd.Parameters.AddWithValue("@syncedBy", faction.SyncedBy ?? (object)DBNull.Value);
                            cmd.Parameters.AddWithValue("@error", faction.ErrorMessage ?? (object)DBNull.Value);
                            cmd.Parameters.AddWithValue("@now", now);
                            cmd.ExecuteNonQuery();
                        }

                        // Sync faction_players: delete old rows, insert fresh
                        using (var del = Cmd("DELETE FROM faction_players WHERE faction_id=@fid;"))
                        {
                            del.Parameters.AddWithValue("@fid", faction.FactionID);
                            del.ExecuteNonQuery();
                        }

                        if (faction.Players != null)
                        {
                            foreach (var p in faction.Players)
                            {
                                using (var ins = Cmd(@"
                                    INSERT OR REPLACE INTO faction_players
                                        (faction_id, player_id, steam_id, original_nick, synced_nick, discord_user_id)
                                    VALUES (@fid, @pid, @sid, @nick, @snick, @did);"))
                                {
                                    ins.Parameters.AddWithValue("@fid", faction.FactionID);
                                    ins.Parameters.AddWithValue("@pid", p.PlayerID);
                                    ins.Parameters.AddWithValue("@sid", p.SteamID);
                                    ins.Parameters.AddWithValue("@nick", p.OriginalNick ?? "");
                                    ins.Parameters.AddWithValue("@snick", p.SyncedNick ?? "");
                                    ins.Parameters.AddWithValue("@did", (long)p.DiscordUserID);
                                    ins.ExecuteNonQuery();
                                }
                            }
                        }

                        // Sync channels_created
                        using (var del = Cmd("DELETE FROM faction_channels_created WHERE faction_id=@fid;"))
                        {
                            del.Parameters.AddWithValue("@fid", faction.FactionID);
                            del.ExecuteNonQuery();
                        }

                        if (faction.ChannelsCreated != null)
                        {
                            foreach (var ch in faction.ChannelsCreated)
                            {
                                using (var ins = Cmd(@"
                                    INSERT INTO faction_channels_created
                                        (faction_id, channel_id, channel_name, channel_type, created_at, deleted_on_undo)
                                    VALUES (@fid, @cid, @cname, @ctype, @cat, @del);"))
                                {
                                    ins.Parameters.AddWithValue("@fid", faction.FactionID);
                                    ins.Parameters.AddWithValue("@cid", (long)ch.ChannelID);
                                    ins.Parameters.AddWithValue("@cname", ch.ChannelName ?? "");
                                    ins.Parameters.AddWithValue("@ctype", ch.ChannelType ?? "");
                                    ins.Parameters.AddWithValue("@cat", ToDb(ch.CreatedAt));
                                    ins.Parameters.AddWithValue("@del", ch.DeletedOnUndo ? 1 : 0);
                                    ins.ExecuteNonQuery();
                                }
                            }
                        }

                        tx.Commit();
                        LoggerUtil.LogDebug($"[SQLITE] SaveFaction committed: {faction.Tag} (ID: {faction.FactionID})");
                    }
                    catch (Exception ex)
                    {
                        tx.Rollback();
                        LoggerUtil.LogError($"[SQLITE] SaveFaction rollback for {faction.Tag}: {ex.Message}");
                        throw;
                    }
                }
            }
        }

        public FactionModel GetFaction(int factionID)
        {
            lock (_lock)
            {
                LoggerUtil.LogDebug($"[SQLITE] GetFaction: faction_id={factionID}");
                using (var cmd = Cmd("SELECT * FROM factions WHERE faction_id=@id;"))
                {
                    cmd.Parameters.AddWithValue("@id", factionID);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        var f = ReadFaction(r);
                        LoadFactionRelations(f);
                        return f;
                    }
                }
            }
        }

        public FactionModel GetFactionByTag(string tag)
        {
            lock (_lock)
            {
                using (var cmd = Cmd("SELECT * FROM factions WHERE tag=@tag COLLATE NOCASE;"))
                {
                    cmd.Parameters.AddWithValue("@tag", tag ?? "");
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        var f = ReadFaction(r);
                        LoadFactionRelations(f);
                        return f;
                    }
                }
            }
        }

        public FactionModel GetFactionByGameChatId(long gameFactionChatId)
        {
            lock (_lock)
            {
                using (var cmd = Cmd("SELECT * FROM factions WHERE game_faction_chat_id=@id;"))
                {
                    cmd.Parameters.AddWithValue("@id", gameFactionChatId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        var f = ReadFaction(r);
                        LoadFactionRelations(f);
                        return f;
                    }
                }
            }
        }

        public bool FactionExists(int factionID)
        {
            lock (_lock)
            {
                using (var cmd = Cmd("SELECT COUNT(*) FROM factions WHERE faction_id=@id;"))
                {
                    cmd.Parameters.AddWithValue("@id", factionID);
                    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
        }

        public List<FactionModel> GetAllFactions()
        {
            lock (_lock)
            {
                LoggerUtil.LogDebug("[SQLITE] GetAllFactions");
                var list = new List<FactionModel>();
                using (var cmd = Cmd("SELECT * FROM factions;"))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var f = ReadFaction(r);
                        LoadFactionRelations(f);
                        list.Add(f);
                    }
                }
                LoggerUtil.LogDebug($"[SQLITE] GetAllFactions returned {list.Count} factions");
                return list;
            }
        }

        public void DeleteFaction(int factionID)
        {
            lock (_lock)
            {
                LoggerUtil.LogDebug($"[SQLITE] DeleteFaction: faction_id={factionID}");
                using (var tx = _connection.BeginTransaction())
                {
                    Execute("DELETE FROM faction_players WHERE faction_id=@id;", ("@id", factionID));
                    Execute("DELETE FROM faction_channels_created WHERE faction_id=@id;", ("@id", factionID));
                    Execute("DELETE FROM factions WHERE faction_id=@id;", ("@id", factionID));
                    tx.Commit();
                }
            }
        }

        private FactionModel ReadFaction(IDataRecord r)
        {
            return new FactionModel
            {
                FactionID = ToInt(r["faction_id"]),
                Tag = ToStr(r["tag"]),
                Name = ToStr(r["name"]),
                DiscordRoleID = ToUlong(r["discord_role_id"]),
                DiscordChannelID = ToUlong(r["discord_channel_id"]),
                DiscordRoleName = ToStr(r["discord_role_name"]),
                DiscordChannelName = ToStr(r["discord_channel_name"]),
                DiscordVoiceChannelID = ToUlong(r["discord_voice_id"]),
                DiscordVoiceChannelName = ToStr(r["discord_voice_name"]),
                GameFactionChatId = ToLong(r["game_faction_chat_id"]),
                SyncStatus = ToStr(r["sync_status"]) ?? "Pending",
                SyncedAt = ParseDtN(r["synced_at"]),
                SyncedBy = ToStr(r["synced_by"]),
                ErrorMessage = ToStr(r["error_message"]),
                CreatedAt = ParseDt(r["created_at"]),
                UpdatedAt = ParseDt(r["updated_at"]),
            };
        }

        private void LoadFactionRelations(FactionModel f)
        {
            // Players
            f.Players = new List<FactionPlayerModel>();
            using (var cmd = Cmd("SELECT * FROM faction_players WHERE faction_id=@fid;"))
            {
                cmd.Parameters.AddWithValue("@fid", f.FactionID);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        f.Players.Add(new FactionPlayerModel
                        {
                            PlayerID = ToInt(r["player_id"]),
                            SteamID = ToLong(r["steam_id"]),
                            OriginalNick = ToStr(r["original_nick"]),
                            SyncedNick = ToStr(r["synced_nick"]),
                            DiscordUserID = ToUlong(r["discord_user_id"]),
                        });
                    }
                }
            }

            // ChannelsCreated
            f.ChannelsCreated = new List<DiscordChannelCreated>();
            using (var cmd = Cmd("SELECT * FROM faction_channels_created WHERE faction_id=@fid;"))
            {
                cmd.Parameters.AddWithValue("@fid", f.FactionID);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        f.ChannelsCreated.Add(new DiscordChannelCreated
                        {
                            ChannelID = ToUlong(r["channel_id"]),
                            ChannelName = ToStr(r["channel_name"]),
                            ChannelType = ToStr(r["channel_type"]),
                            CreatedAt = ParseDt(r["created_at"]),
                            DeletedOnUndo = ToInt(r["deleted_on_undo"]) == 1,
                        });
                    }
                }
            }
        }

        // ================================================================
        // PLAYERS
        // ================================================================

        public void SavePlayer(PlayerModel player)
        {
            lock (_lock)
            {
                LoggerUtil.LogDebug($"[SQLITE] SavePlayer: steam_id={player.SteamID}, nick={player.OriginalNick}");
                string now = ToDb(DateTime.UtcNow);
                Execute(@"
                    INSERT INTO players
                        (steam_id, player_id, original_nick, synced_nick, faction_id, discord_user_id, created_at, updated_at)
                    VALUES (@sid, @pid, @nick, @snick, @fid, @did, @now, @now)
                    ON CONFLICT(steam_id) DO UPDATE SET
                        player_id=excluded.player_id,
                        original_nick=excluded.original_nick,
                        synced_nick=excluded.synced_nick,
                        faction_id=excluded.faction_id,
                        discord_user_id=excluded.discord_user_id,
                        updated_at=excluded.updated_at;",
                    ("@sid", player.SteamID),
                    ("@pid", player.PlayerID),
                    ("@nick", player.OriginalNick ?? ""),
                    ("@snick", player.SyncedNick ?? ""),
                    ("@fid", player.FactionID),
                    ("@did", (long)player.DiscordUserID),
                    ("@now", now));
            }
        }

        public PlayerModel GetPlayerBySteamID(long steamID)
        {
            lock (_lock)
            {
                using (var cmd = Cmd("SELECT * FROM players WHERE steam_id=@sid;"))
                {
                    cmd.Parameters.AddWithValue("@sid", steamID);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        return new PlayerModel
                        {
                            SteamID = ToLong(r["steam_id"]),
                            PlayerID = ToInt(r["player_id"]),
                            OriginalNick = ToStr(r["original_nick"]),
                            SyncedNick = ToStr(r["synced_nick"]),
                            FactionID = ToInt(r["faction_id"]),
                            DiscordUserID = ToUlong(r["discord_user_id"]),
                            CreatedAt = ParseDt(r["created_at"]),
                            UpdatedAt = ParseDt(r["updated_at"]),
                        };
                    }
                }
            }
        }

        // ================================================================
        // EVENT LOGS
        // ================================================================

        public void LogEvent(EventLogModel evt)
        {
            lock (_lock)
            {
                Execute(@"
                    INSERT INTO event_logs (event_type, details, timestamp)
                    VALUES (@type, @details, @ts);",
                    ("@type", evt.EventType ?? ""),
                    ("@details", evt.Details ?? ""),
                    ("@ts", ToDb(evt.Timestamp)));
            }
        }

        // ================================================================
        // DEATH HISTORY
        // ================================================================

        public void LogDeath(long killerSteamID, long victimSteamID, string deathType,
            string weapon = null, string location = null,
            string killerName = null, string victimName = null)
        {
            lock (_lock)
            {
                Execute(@"
                    INSERT INTO death_history
                        (killer_steam_id, victim_steam_id, death_time, death_type, killer_name, victim_name, weapon, location)
                    VALUES (@kid, @vid, @dt, @type, @kname, @vname, @weapon, @loc);",
                    ("@kid", killerSteamID),
                    ("@vid", victimSteamID),
                    ("@dt", ToDb(DateTime.UtcNow)),
                    ("@type", deathType ?? ""),
                    ("@kname", killerName ?? (object)DBNull.Value),
                    ("@vname", victimName ?? (object)DBNull.Value),
                    ("@weapon", weapon ?? (object)DBNull.Value),
                    ("@loc", location ?? (object)DBNull.Value));
            }
        }

        public DeathHistoryModel GetLastKill(long killerSteamID, long victimSteamID)
        {
            lock (_lock)
            {
                using (var cmd = Cmd(@"
                    SELECT * FROM death_history
                    WHERE killer_steam_id=@kid AND victim_steam_id=@vid
                    ORDER BY death_time DESC LIMIT 1;"))
                {
                    cmd.Parameters.AddWithValue("@kid", killerSteamID);
                    cmd.Parameters.AddWithValue("@vid", victimSteamID);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        return new DeathHistoryModel
                        {
                            KillerSteamID = ToLong(r["killer_steam_id"]),
                            VictimSteamID = ToLong(r["victim_steam_id"]),
                            DeathTime = ParseDt(r["death_time"]),
                            DeathType = ToStr(r["death_type"]),
                            KillerName = ToStr(r["killer_name"]),
                            VictimName = ToStr(r["victim_name"]),
                            Weapon = ToStr(r["weapon"]),
                            Location = ToStr(r["location"]),
                        };
                    }
                }
            }
        }

        // ================================================================
        // VERIFICATION HISTORY
        // ================================================================

        public void SaveVerificationHistory(VerificationHistoryModel entry)
        {
            lock (_lock)
            {
                // Duplicate check (same SteamID within ±1 second)
                using (var check = Cmd(@"
                    SELECT COUNT(*) FROM verification_history
                    WHERE steam_id=@sid AND verified_at BETWEEN @from AND @to;"))
                {
                    check.Parameters.AddWithValue("@sid", entry.SteamID);
                    check.Parameters.AddWithValue("@from", ToDb(entry.VerifiedAt.AddSeconds(-1)));
                    check.Parameters.AddWithValue("@to", ToDb(entry.VerifiedAt.AddSeconds(1)));
                    if (Convert.ToInt32(check.ExecuteScalar()) > 0) return;
                }

                Execute(@"
                    INSERT INTO verification_history
                        (steam_id, discord_username, discord_user_id, verified_at, status)
                    VALUES (@sid, @user, @did, @vat, @status);",
                    ("@sid", entry.SteamID),
                    ("@user", entry.DiscordUsername ?? ""),
                    ("@did", (long)entry.DiscordUserID),
                    ("@vat", ToDb(entry.VerifiedAt)),
                    ("@status", entry.Status ?? ""));
            }
        }

        public List<VerificationHistoryModel> GetVerificationHistory(long steamID)
        {
            lock (_lock)
            {
                var list = new List<VerificationHistoryModel>();
                using (var cmd = Cmd(@"
                    SELECT * FROM verification_history
                    WHERE steam_id=@sid ORDER BY verified_at DESC;"))
                {
                    cmd.Parameters.AddWithValue("@sid", steamID);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            list.Add(new VerificationHistoryModel
                            {
                                SteamID = ToLong(r["steam_id"]),
                                DiscordUsername = ToStr(r["discord_username"]),
                                DiscordUserID = ToUlong(r["discord_user_id"]),
                                VerifiedAt = ParseDt(r["verified_at"]),
                                Status = ToStr(r["status"]),
                            });
                        }
                    }
                }
                return list;
            }
        }

        // ================================================================
        // PENDING VERIFICATIONS
        // ================================================================

        public void AddPendingVerification(long steamID, string discordUsername,
            string verificationCode, int expirationMinutes, string gamePlayerName = null)
        {
            lock (_lock)
            {
                LoggerUtil.LogDebug($"[SQLITE] AddPendingVerification: steam_id={steamID}");
                string now = ToDb(DateTime.UtcNow);
                string expires = ToDb(DateTime.UtcNow.AddMinutes(expirationMinutes));
                Execute(@"
                    INSERT INTO pending_verifications
                        (steam_id, discord_username, verification_code, code_generated_at, expires_at, game_player_name)
                    VALUES (@sid, @user, @code, @now, @exp, @name)
                    ON CONFLICT(steam_id) DO UPDATE SET
                        discord_username=excluded.discord_username,
                        verification_code=excluded.verification_code,
                        code_generated_at=excluded.code_generated_at,
                        expires_at=excluded.expires_at,
                        game_player_name=excluded.game_player_name;",
                    ("@sid", steamID),
                    ("@user", discordUsername ?? ""),
                    ("@code", verificationCode ?? ""),
                    ("@now", now),
                    ("@exp", expires),
                    ("@name", gamePlayerName ?? (object)DBNull.Value));
            }
        }

        public PendingVerification GetPendingVerification(long steamID)
        {
            lock (_lock)
            {
                using (var cmd = Cmd("SELECT * FROM pending_verifications WHERE steam_id=@sid;"))
                {
                    cmd.Parameters.AddWithValue("@sid", steamID);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;

                        var pv = new PendingVerification
                        {
                            SteamID = ToLong(r["steam_id"]),
                            DiscordUsername = ToStr(r["discord_username"]),
                            VerificationCode = ToStr(r["verification_code"]),
                            CodeGeneratedAt = ParseDt(r["code_generated_at"]),
                            ExpiresAt = ParseDt(r["expires_at"]),
                            GamePlayerName = ToStr(r["game_player_name"]),
                        };

                        // Expired? Clean up
                        if (pv.ExpiresAt < DateTime.UtcNow)
                        {
                            Execute("DELETE FROM pending_verifications WHERE steam_id=@sid;",
                                ("@sid", steamID));
                            return null;
                        }

                        return pv;
                    }
                }
            }
        }

        public List<PendingVerification> GetAllPendingVerifications()
        {
            lock (_lock)
            {
                var list = new List<PendingVerification>();
                using (var cmd = Cmd("SELECT * FROM pending_verifications;"))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new PendingVerification
                        {
                            SteamID = ToLong(r["steam_id"]),
                            DiscordUsername = ToStr(r["discord_username"]),
                            VerificationCode = ToStr(r["verification_code"]),
                            CodeGeneratedAt = ParseDt(r["code_generated_at"]),
                            ExpiresAt = ParseDt(r["expires_at"]),
                            GamePlayerName = ToStr(r["game_player_name"]),
                        });
                    }
                }
                return list;
            }
        }

        public void DeletePendingVerification(long steamID)
        {
            lock (_lock)
            {
                LoggerUtil.LogDebug($"[SQLITE] DeletePendingVerification: steam_id={steamID}");
                Execute("DELETE FROM pending_verifications WHERE steam_id=@sid;", ("@sid", steamID));
            }
        }

        // ================================================================
        // VERIFIED PLAYERS
        // ================================================================

        public void MarkAsVerified(long steamID, string discordUsername, ulong discordUserID,
            string gamePlayerName = null)
        {
            lock (_lock)
            {
                LoggerUtil.LogDebug($"[SQLITE] MarkAsVerified: steam_id={steamID}, discord={discordUsername}");

                // Resolve player name from pending if not provided
                if (string.IsNullOrEmpty(gamePlayerName))
                {
                    var pv = GetPendingVerification(steamID);
                    if (pv != null) gamePlayerName = pv.GamePlayerName;
                }

                Execute("DELETE FROM pending_verifications WHERE steam_id=@sid;", ("@sid", steamID));

                Execute(@"
                    INSERT INTO verified_players
                        (steam_id, discord_username, discord_user_id, verified_at, game_player_name)
                    VALUES (@sid, @user, @did, @vat, @name)
                    ON CONFLICT(steam_id) DO UPDATE SET
                        discord_username=excluded.discord_username,
                        discord_user_id=excluded.discord_user_id,
                        verified_at=excluded.verified_at,
                        game_player_name=excluded.game_player_name;",
                    ("@sid", steamID),
                    ("@user", discordUsername ?? ""),
                    ("@did", (long)discordUserID),
                    ("@vat", ToDb(DateTime.UtcNow)),
                    ("@name", gamePlayerName ?? (object)DBNull.Value));
            }
        }

        public VerifiedPlayer GetVerifiedPlayer(long steamID)
        {
            lock (_lock)
            {
                using (var cmd = Cmd("SELECT * FROM verified_players WHERE steam_id=@sid;"))
                {
                    cmd.Parameters.AddWithValue("@sid", steamID);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        return ReadVerifiedPlayer(r);
                    }
                }
            }
        }

        public VerifiedPlayer GetVerifiedPlayerByDiscordID(ulong discordUserID)
        {
            lock (_lock)
            {
                using (var cmd = Cmd("SELECT * FROM verified_players WHERE discord_user_id=@did;"))
                {
                    cmd.Parameters.AddWithValue("@did", (long)discordUserID);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        return ReadVerifiedPlayer(r);
                    }
                }
            }
        }

        public List<VerifiedPlayer> GetAllVerifiedPlayers()
        {
            lock (_lock)
            {
                var list = new List<VerifiedPlayer>();
                using (var cmd = Cmd("SELECT * FROM verified_players;"))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(ReadVerifiedPlayer(r));
                }
                return list;
            }
        }

        public void DeleteVerifiedPlayer(long steamID)
        {
            lock (_lock)
            {
                LoggerUtil.LogDebug($"[SQLITE] DeleteVerifiedPlayer: steam_id={steamID}");
                Execute("DELETE FROM verified_players WHERE steam_id=@sid;", ("@sid", steamID));
            }
        }

        private static VerifiedPlayer ReadVerifiedPlayer(IDataRecord r) =>
            new VerifiedPlayer
            {
                SteamID = ToLong(r["steam_id"]),
                DiscordUsername = ToStr(r["discord_username"]),
                DiscordUserID = ToUlong(r["discord_user_id"]),
                VerifiedAt = ParseDt(r["verified_at"]),
                GamePlayerName = ToStr(r["game_player_name"]),
            };

        // ================================================================
        // CLEAR ALL
        // ================================================================

        public void ClearAllData()
        {
            lock (_lock)
            {
                LoggerUtil.LogDebug("[SQLITE] ClearAllData – truncating all tables");
                using (var tx = _connection.BeginTransaction())
                {
                    foreach (var table in new[] {
                        "faction_channels_created", "faction_players", "factions",
                        "players", "event_logs", "death_history",
                        "verification_history", "pending_verifications", "verified_players" })
                    {
                        using (var cmd = Cmd($"DELETE FROM {table};"))
                            cmd.ExecuteNonQuery();
                        LoggerUtil.LogDebug($"[SQLITE] Cleared table: {table}");
                    }
                    tx.Commit();
                }
                LoggerUtil.LogSuccess("[SQLITE] ClearAllData complete");
            }
        }

        // ================================================================
        // DISPOSE
        // ================================================================

        public void Close()
        {
            try
            {
                _connection?.Close();
                _connection?.Dispose();
                LoggerUtil.LogDebug("[SQLITE] Connection closed.");
            }
            catch (Exception ex)
            {
                LoggerUtil.LogWarning($"[SQLITE] Error closing connection: {ex.Message}");
            }
        }

        // ================================================================
        // PRIVATE HELPERS
        // ================================================================

        private void Execute(string sql, params (string name, object value)[] parameters)
        {
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                foreach (var (name, value) in parameters)
                    cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }
    }
}
