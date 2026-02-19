/*
 * HandcuffLimiter
 * Rust uMod plugin that prevents restraint abuse by enforcing maximum handcuff duration and safely teleporting victims to Outpost or Bandit Camp.
 * Also gives captives a unique opportunity to punish or forgive their captors.
 *
 * Copyright (C) 2026 SeesAll
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
 * See the GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 *
 * Commercial Licensing:
 * While this software is available under GPLv3 for open-source use,
 * commercial redistribution, resale, bundling in paid packages,
 * or closed-source modifications require a separate commercial license.
 *
 * For commercial licensing inquiries, contact:
 * (SeesAll on uMod | N01B4ME on Discord)
 */

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using Rust;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("HandcuffLimiter", "SeesAll", "0.8.5")]
    [Description("Prevents indefinite handcuff restraint by auto-releasing and teleporting restrained players to a safe monument after a configurable limit.")]
    public class HandcuffLimiter : RustPlugin
    {
        #region Permissions

        private const string PermExempt = "handcufflimiter.exempt";
        private const string PermAdmin  = "handcufflimiter.admin";

        #endregion

        #region Configuration

        private PluginConfig _config;

        private enum SafeDestination
        {
            Outpost = 1,
            BanditCamp = 2
        }

        private class PluginConfig
        {
            public bool Enabled = true;

            public int CheckIntervalSeconds = 2;

            public float ExemptPermissionCacheSeconds = 10f; // performance: cache exempt permission lookups for this many seconds

            public int MaxRestrainMinutes = 20;
            public int WarnSecondsBeforeLimit = 60;
            public bool WarnVictim = true;

            [JsonProperty("Teleport Destination (outpost / bandit)")]
            public string TeleportDestination = "outpost"; // "outpost" or "bandit"

            [JsonProperty("Destination")]
            public string LegacyDestination;
            public bool ShouldSerializeLegacyDestination() => false;
            public Vector3 DestinationOffset = new Vector3(0f, 1.5f, 0f);

            public bool TeleportOnlyWithinSafeZone = true;
            public float SafeZoneSearchRadius = 25f;
            public int SafeZoneSearchAttempts = 8;

            public float TeleportClearanceRadius = 0.6f; 
            public float TeleportClearanceHeight = 1.8f;

            public float BuildingProximityRejectRadius = 6f; 


            public float TeleportAttemptTimeoutSeconds = 3.0f; 

            public bool CacheSafeTeleportSpots = true;

            public float CacheBlacklistMinDistance = 6f; 

            public int BlacklistMaxEntriesPerDestination = 50; 

            public bool RemoveHoodBeforePrompt = false;
            public int HoodRemoveSecondsBeforeLimit = 60;

            public int VictimRecuffImmunitySeconds = 300;
            public int EpisodeMergeWindowSeconds = 60;

            public bool ChaosEnabled = false;
            public bool ChaosOnlyOutsideSafeZones = true;
            public int ChaosFuseSeconds = 10;

            public int ChaosCooldownMinutesPerVictim = 1440;

            public bool ChaosRadiusCheckPlayers = false;
            public float ChaosRadiusCheckMeters = 6f;
            public int ChaosMaxPlayersInRadius = 0;

            public bool EnablePunishForgivePrompt = false;
            public int PromptSecondsBeforeLimit = 60;

            public bool DebugEnabled = true;
            public int DebugTestSeconds = 60;
        }

        private SafeDestination ParseDestination(string value)
        {
            if (string.IsNullOrEmpty(value)) return SafeDestination.Outpost;

            var v = value.Trim().ToLowerInvariant();

            if (v == "1") return SafeDestination.Outpost;
            if (v == "2") return SafeDestination.BanditCamp;

            if (v == "outpost") return SafeDestination.Outpost;
            if (v == "bandit" || v == "banditcamp" || v == "bandit camp") return SafeDestination.BanditCamp;

            return SafeDestination.Outpost;
        }

        private int DestinationToInt() => (int)ParseDestination(_config?.TeleportDestination);


        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>() ?? new PluginConfig();

                if (!string.IsNullOrEmpty(_config.LegacyDestination))
                {
                    var legacyParsed = ParseDestination(_config.LegacyDestination);
                    if (string.IsNullOrEmpty(_config.TeleportDestination) ||
                        (_config.TeleportDestination.Trim().Equals("outpost", StringComparison.OrdinalIgnoreCase) && legacyParsed == SafeDestination.BanditCamp))
                    {
                        _config.TeleportDestination = (legacyParsed == SafeDestination.BanditCamp) ? "bandit" : "outpost";
                    }
                }
            }
            catch
            {
                PrintWarning("Config is invalid/corrupted; loading default values.");
                _config = new PluginConfig();
            }

            if (_config.MaxRestrainMinutes < 1) _config.MaxRestrainMinutes = 1;
            if (_config.CheckIntervalSeconds < 1) _config.CheckIntervalSeconds = 1;
            if (_config.CheckIntervalSeconds > 30) _config.CheckIntervalSeconds = 30;
            if (_config.WarnSecondsBeforeLimit < 0) _config.WarnSecondsBeforeLimit = 0;
            if (_config.ChaosFuseSeconds < 0) _config.ChaosFuseSeconds = 0;
            if (_config.SafeZoneSearchAttempts < 1) _config.SafeZoneSearchAttempts = 1;
            if (_config.SafeZoneSearchAttempts > 25) _config.SafeZoneSearchAttempts = 25;
            if (_config.SafeZoneSearchRadius < 0f) _config.SafeZoneSearchRadius = 0f;
            if (_config.TeleportClearanceRadius < 0.1f) _config.TeleportClearanceRadius = 0.1f;
            if (_config.TeleportClearanceRadius > 3f) _config.TeleportClearanceRadius = 3f;
            if (_config.TeleportClearanceHeight < 0.5f) _config.TeleportClearanceHeight = 0.5f;
            if (_config.TeleportClearanceHeight > 5f) _config.TeleportClearanceHeight = 5f;
            if (_config.PromptSecondsBeforeLimit < 0) _config.PromptSecondsBeforeLimit = 0;
            if (_config.DebugTestSeconds < 5) _config.DebugTestSeconds = 5;
            if (_config.HoodRemoveSecondsBeforeLimit < 0) _config.HoodRemoveSecondsBeforeLimit = 0;
            if (_config.CacheBlacklistMinDistance < 0f) _config.CacheBlacklistMinDistance = 0f;
            if (_config.TeleportAttemptTimeoutSeconds < 0f) _config.TeleportAttemptTimeoutSeconds = 0f;
            if (_config.ExemptPermissionCacheSeconds < 0f) _config.ExemptPermissionCacheSeconds = 0f;            if (_config.BlacklistMaxEntriesPerDestination < 0) _config.BlacklistMaxEntriesPerDestination = 0;

            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Lang

        private const string MsgVictimWarn    = "VictimWarn";
        private const string MsgVictimAction  = "VictimAction";
        private const string MsgAdminNoPerm   = "AdminNoPerm";
        private const string MsgAdminUsage    = "AdminUsage";
        private const string MsgAdminStatus   = "AdminStatus";
        private const string MsgAdminNotFound = "AdminNotFound";
        private const string MsgAdminReset    = "AdminReset";

        private const string MsgPromptTitle   = "PromptTitle";
        private const string MsgPromptBody    = "PromptBody";
        private const string MsgPromptPunish  = "PromptPunish";
        private const string MsgPromptForgive = "PromptForgive";

        private const string MsgDebugStarted  = "DebugStarted";
        private const string MsgDebugStopped  = "DebugStopped";
        private const string MsgDebugDisabled = "DebugDisabled";

        private const string MsgCacheCleared  = "CacheCleared";
        private const string MsgCacheUsage    = "CacheUsage";
        private const string MsgWearListHeader = "WearListHeader";
        private const string MsgWearListLine   = "WearListLine";

        private void Init()
        {
            permission.RegisterPermission(PermExempt, this);
            permission.RegisterPermission(PermAdmin, this);

            lang.RegisterMessages(new Dictionary<string, string>
            {
                [MsgVictimWarn]    = "You have been restrained for a long time. If you are still restrained in {0} seconds, you will be released and moved to a safe location.",
                [MsgVictimAction]  = "You were restrained too long. You have been released and moved to a safe location.",

                [MsgAdminNoPerm]   = "You don't have permission to use that command.",
                [MsgAdminUsage]    = "Usage: /hcl status <nameOrId>  OR  /hcl reset <nameOrId>  OR  /hcl debug  OR  /hcl debugoff  OR  /hcl clearcache [outpost|bandit|all]  OR  /hcl wear <nameOrId>",
                [MsgAdminNotFound] = "Player not found.",
                [MsgAdminStatus]   = "{0} restrained={1}, tracked={2}, elapsed={3}s, debug={4}",
                [MsgAdminReset]    = "Tracking state reset for {0}.",

                [MsgPromptTitle]   = "Punish or Forgive?",
                [MsgPromptBody]    = "You're about to be freed. Choose what happens when you teleport out.",
                [MsgPromptPunish]  = "Punish",
                [MsgPromptForgive] = "Forgive",

                [MsgDebugStarted]  = "Debug test started: you have been restrained for {0} seconds.",
                [MsgDebugStopped]  = "Debug test stopped.",
                [MsgDebugDisabled] = "Debug mode is disabled in the config.",

                [MsgCacheUsage]    = "Usage: /hcl clearcache [outpost|bandit|all]  OR  /hcl wear <nameOrId>",
                [MsgCacheCleared]  = "Cleared cached safe teleport spot for: {0}. It will be re-learned on the next rescue.",
                ["WearListHeader"] = "Wear items for {0}:",
                ["WearListLine"] = "- {0} (itemid {1})"
            }, this);
        }

        #endregion

        #region Data

        private const int MaxLogEntries = 2000;

        private StoredData _data;

        private class StoredData
        {
            public string MapId;

            public Dictionary<ulong, long> LastChaosUtc = new Dictionary<ulong, long>();

            public Dictionary<int, SerializableVector3> CachedSafeSpots = new Dictionary<int, SerializableVector3>();

            public Dictionary<int, List<SerializableVector3>> BlacklistedSpots = new Dictionary<int, List<SerializableVector3>>();

            public List<LogEntry> Log = new List<LogEntry>();
        }

        private class SerializableVector3
        {
            public float x, y, z;
            public SerializableVector3() { }
            public SerializableVector3(Vector3 v) { x = v.x; y = v.y; z = v.z; }
            public Vector3 ToVector3() => new Vector3(x, y, z);
        }

        private class LogEntry
        {
            public long Utc;
            public ulong VictimId;
            public string VictimName;
            public int DurationSeconds;
            public int Destination;
            public int Choice;
            public bool ChaosDropped;
        }

        private void LoadData()
        {
            _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name) ?? new StoredData();
            if (_data.LastChaosUtc == null) _data.LastChaosUtc = new Dictionary<ulong, long>();
            if (_data.Log == null) _data.Log = new List<LogEntry>();
            if (_data.CachedSafeSpots == null) _data.CachedSafeSpots = new Dictionary<int, SerializableVector3>();
            if (_data.BlacklistedSpots == null) _data.BlacklistedSpots = new Dictionary<int, List<SerializableVector3>>();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, _data);

        private string GetCurrentMapId()
        {
            try
            {
                var seed = ConVar.Server.seed;
                var size = ConVar.Server.worldsize;
                var level = ConVar.Server.level ?? string.Empty;
                return $"{seed}:{size}:{level}";
            }
            catch
            {
                return "unknown";
            }
        }

        private void WipeData(string reason)
        {
            if (_data == null) _data = new StoredData();

            _data.MapId = GetCurrentMapId();
            _data.LastChaosUtc.Clear();
            _data.Log.Clear();
            _data.CachedSafeSpots.Clear();
            _data.BlacklistedSpots.Clear();

            SaveData();
            Puts($"Data wiped ({reason}).");
        }

        #endregion

        #region State

        private class RestrainState
        {
            public ulong VictimId;
            public double EpisodeStartUtc;
            public double LastSeenUnrestrainedUtc;
            public bool Warned;
            public bool IsCurrentlyRestrained;

            public bool PromptShown;
            public int Choice;

            public bool Debug;
            public int DebugMaxSeconds;

            public bool HoodRemoved;
        }

        private readonly Dictionary<ulong, RestrainState> _stateByVictim = new Dictionary<ulong, RestrainState>();
        private readonly Dictionary<ulong, double> _immuneUntilUtc = new Dictionary<ulong, double>();

        private struct PermCacheEntry
        {
            public bool Value;
            public double ExpiresUtc;
        }

        private readonly Dictionary<ulong, PermCacheEntry> _exemptPermCache = new Dictionary<ulong, PermCacheEntry>();

        private Timer _pollTimer;

        private readonly Dictionary<int, Vector3> _monumentCenterByDest = new Dictionary<int, Vector3>();

        private class PendingTeleport
        {
            public int Destination;
            public Vector3 Center;
            public int AttemptsRemaining;
            public float Radius;
            public bool TriedCached;
            public double StartedUtc;
        }

        private readonly Dictionary<ulong, PendingTeleport> _pendingTeleports = new Dictionary<ulong, PendingTeleport>();

        private const string UiLayer = "HandcuffLimiter.UI";

        #endregion

        #region Hooks

        private void OnServerInitialized()
        {
            if (!_config.Enabled) return;

            LoadData();

            var mapId = GetCurrentMapId();
            if (string.IsNullOrEmpty(_data.MapId) || !_data.MapId.Equals(mapId, StringComparison.Ordinal))
                WipeData("map identity changed");

            CacheMonumentCenter(DestinationToInt());

            StartPolling();
        }

        private void OnNewSave(string filename)
        {
            if (_data != null)
                WipeData("OnNewSave");
        }

        private void Unload()
        {
            _pollTimer?.Destroy();

            var players = BasePlayer.activePlayerList;
            if (players != null)
            {
                for (var i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    if (p != null) DestroyPromptUi(p);
                }
            }

            _stateByVictim.Clear();
            _immuneUntilUtc.Clear();
            _monumentCenterByDest.Clear();
            _pendingTeleports.Clear();
            _exemptPermCache.Clear();

            if (_data != null) SaveData();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            DestroyPromptUi(player);
            _stateByVictim.Remove(player.userID);
            _immuneUntilUtc.Remove(player.userID);
            _pendingTeleports.Remove(player.userID);
            _exemptPermCache.Remove(player.userID);
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null) return;
            DestroyPromptUi(player);
            _stateByVictim.Remove(player.userID);
            _immuneUntilUtc.Remove(player.userID);
            _pendingTeleports.Remove(player.userID);
            _exemptPermCache.Remove(player.userID);
        }

        #endregion

        #region Commands

        [ChatCommand("hcl")]
        private void CmdHcl(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;

            if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                player.ChatMessage(lang.GetMessage(MsgAdminNoPerm, this, player.UserIDString));
                return;
            }

            if (args == null || args.Length < 1)
            {
                player.ChatMessage(lang.GetMessage(MsgAdminUsage, this, player.UserIDString));
                return;
            }

            var sub = args[0].ToLowerInvariant();

            if (sub == "debug")
            {
                if (!_config.DebugEnabled)
                {
                    player.ChatMessage(lang.GetMessage(MsgDebugDisabled, this, player.UserIDString));
                    return;
                }

                StartDebugEpisode(player);
                player.ChatMessage(string.Format(lang.GetMessage(MsgDebugStarted, this, player.UserIDString), _config.DebugTestSeconds));
                return;
            }

            if (sub == "debugoff")
            {
                StopDebugEpisode(player);
                player.ChatMessage(lang.GetMessage(MsgDebugStopped, this, player.UserIDString));
                return;
            }

            if (sub == "clearcache")
            {
                var which = args.Length >= 2 ? args[1] : "all";
                if (!TryClearCache(which, out var cleared))
                {
                    player.ChatMessage(lang.GetMessage(MsgCacheUsage, this, player.UserIDString));
                    return;
                }


if (sub == "wear")
{
    if (args.Length < 2)
    {
        player.ChatMessage(lang.GetMessage(MsgAdminUsage, this, player.UserIDString));
        return;
    }

    var targetWear = FindPlayer(args[1]);
    if (targetWear == null)
    {
        player.ChatMessage(lang.GetMessage(MsgAdminNotFound, this, player.UserIDString));
        return;
    }

    ShowWearList(player, targetWear);
    return;
}

                player.ChatMessage(string.Format(lang.GetMessage(MsgCacheCleared, this, player.UserIDString), cleared));
                return;
            }

            if (args.Length < 2)
            {
                player.ChatMessage(lang.GetMessage(MsgAdminUsage, this, player.UserIDString));
                return;
            }

            var target = FindPlayer(args[1]);
            if (target == null)
            {
                player.ChatMessage(lang.GetMessage(MsgAdminNotFound, this, player.UserIDString));
                return;
            }

            if (sub == "status")
            {
                var tracked = _stateByVictim.TryGetValue(target.userID, out var st);
                var restrained = (target.IsRestrained || IsWearingHandcuffs(target));
                var elapsed = tracked ? (int)Math.Floor(Interface.Oxide.Now - st.EpisodeStartUtc) : 0;
                var debug = tracked && st.Debug;

                player.ChatMessage(string.Format(lang.GetMessage(MsgAdminStatus, this, player.UserIDString),
                    target.displayName, restrained, tracked, elapsed, debug));
                return;
            }

            if (sub == "reset")
            {
                DestroyPromptUi(target);
                _stateByVictim.Remove(target.userID);
                _immuneUntilUtc.Remove(target.userID);
                player.ChatMessage(string.Format(lang.GetMessage(MsgAdminReset, this, player.UserIDString), target.displayName));
                return;
            }

            player.ChatMessage(lang.GetMessage(MsgAdminUsage, this, player.UserIDString));
        }

        [ConsoleCommand("hcl.clearcache")]
        private void CCmdClearCache(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player != null && !permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                arg.ReplyWith("No permission.");
                return;
            }

            var which = "all";
            if (arg?.Args != null && arg.Args.Length >= 1) which = arg.Args[0];

            if (!TryClearCache(which, out var cleared))
            {
                arg.ReplyWith("Usage: hcl.clearcache <outpost|bandit|all>");
                return;
            }

            arg.ReplyWith($"Cleared cached safe teleport spot for: {cleared}. It will be re-learned on the next rescue.");
        }

        [ConsoleCommand("hcl.choice")]
        private void CmdChoice(ConsoleSystem.Arg arg)
        {
            var player = arg?.Player();
            if (player == null) return;

            if (arg.Args == null || arg.Args.Length < 1) return;

            if (!_stateByVictim.TryGetValue(player.userID, out var st)) return;

            var a = arg.Args[0].ToLowerInvariant();
            if (a == "punish") st.Choice = 1;
            else if (a == "forgive") st.Choice = 2;

            DestroyPromptUi(player);
        }

        private bool TryClearCache(string which, out string clearedText)
        {
            clearedText = string.Empty;
            if (_data == null) return false;

            which = (which ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(which)) which = "all";

            var cleared = new List<string>(2);

            if (which == "all")
            {
                ClearCacheForDestination((int)SafeDestination.Outpost, cleared);
                ClearCacheForDestination((int)SafeDestination.BanditCamp, cleared);
            }
            else if (which == "outpost" || which == "1")
            {
                ClearCacheForDestination((int)SafeDestination.Outpost, cleared);
            }
            else if (which == "bandit" || which == "banditcamp" || which == "2")
            {
                ClearCacheForDestination((int)SafeDestination.BanditCamp, cleared);
            }
            else
            {
                return false;
            }

            clearedText = cleared.Count == 0 ? "none (no cached spot existed)" : string.Join(", ", cleared);
            return true;
        }

        private void ClearCacheForDestination(int destination, List<string> clearedList)
        {
            if (_data.CachedSafeSpots != null && _data.CachedSafeSpots.TryGetValue(destination, out var cached))
            {
                AddToBlacklist(destination, cached.ToVector3());
                _data.CachedSafeSpots.Remove(destination);
                SaveData();
                clearedList.Add(destination == (int)SafeDestination.Outpost ? "Outpost" : "Bandit");
            }
            else
            {
            }
        }

        private BasePlayer FindPlayer(string nameOrId)
        {
            if (string.IsNullOrEmpty(nameOrId)) return null;

            if (ulong.TryParse(nameOrId, out var id))
            {
                var p = BasePlayer.FindByID(id);
                if (p != null) return p;
                return BasePlayer.FindSleeping(id);
            }

            var lower = nameOrId.ToLowerInvariant();
            var list = BasePlayer.activePlayerList;
            for (var i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;
                if (p.displayName != null && p.displayName.ToLowerInvariant().Contains(lower))
                    return p;
            }

            return null;
        }

        private bool IsExemptCached(BasePlayer player, double now)
{
    if (player == null) return false;

    if (_config.ExemptPermissionCacheSeconds <= 0f)
        return permission.UserHasPermission(player.UserIDString, PermExempt);

    if (_exemptPermCache.TryGetValue(player.userID, out var entry) && entry.ExpiresUtc > now)
        return entry.Value;

    var value = permission.UserHasPermission(player.UserIDString, PermExempt);
    _exemptPermCache[player.userID] = new PermCacheEntry
    {
        Value = value,
        ExpiresUtc = now + _config.ExemptPermissionCacheSeconds
    };
    return value;
}

private void ShowWearList(BasePlayer admin, BasePlayer target)
{
    if (admin == null || target == null) return;

    admin.ChatMessage(string.Format(lang.GetMessage(MsgWearListHeader, this, admin.UserIDString), target.displayName));

    try
    {
        var wear = target.inventory?.containerWear;
        if (wear == null || wear.itemList == null || wear.itemList.Count == 0)
        {
            admin.ChatMessage("- (none)");
            return;
        }

        var items = wear.itemList;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it?.info == null) continue;
            admin.ChatMessage(string.Format(lang.GetMessage(MsgWearListLine, this, admin.UserIDString), it.info.shortname, it.info.itemid));
        }
    }
    catch
    {
        admin.ChatMessage("- (error reading wear container)");
    }
}

#endregion

        #region Debug

private void TryApplyDebugHandcuffs(BasePlayer player)
{
    try
    {
        if (player == null) return;
        if (IsWearingHandcuffs(player)) return;

        var def = ItemManager.FindItemDefinition(HandcuffsShortname);
        if (def == null)
        {
            PrintWarning("Debug: could not find ItemDefinition for handcuffs.");
            return;
        }

        var item = ItemManager.Create(def, 1, 0UL);
        if (item == null)
        {
            PrintWarning("Debug: could not create handcuffs item.");
            return;
        }

        var wear = player.inventory?.containerWear;
        if (wear != null && item.MoveToContainer(wear))
            return;

        item.Drop(player.transform.position + (Vector3.up * 0.25f), Vector3.zero);
        PrintWarning("Debug: handcuffs could not be worn automatically on this build. Dropped handcuffs at your feet; cuff yourself normally to test.");
    }
    catch { }
}

        private void StartDebugEpisode(BasePlayer player)
{
    if (player == null) return;

    TryApplyDebugHandcuffs(player);

    try { player.SendNetworkUpdateImmediate(); } catch { }

    var now = Interface.Oxide.Now;

    if (!_stateByVictim.TryGetValue(player.userID, out var st))
    {
        st = new RestrainState { VictimId = player.userID };
        _stateByVictim[player.userID] = st;
    }

    st.EpisodeStartUtc = now;
    st.LastSeenUnrestrainedUtc = 0;
    st.Warned = false;
    st.IsCurrentlyRestrained = true;
    st.PromptShown = false;
    st.Choice = 0;
    st.Debug = true;
    st.DebugMaxSeconds = _config.DebugTestSeconds;
    st.HoodRemoved = false;

    _immuneUntilUtc.Remove(player.userID);
}


        private void StopDebugEpisode(BasePlayer player)
        {
            if (player == null) return;

            DestroyPromptUi(player);
            _stateByVictim.Remove(player.userID);

            TryUnrestrain(player);
        }

        #endregion

        #region Core

        private void StartPolling()
        {
            _pollTimer?.Destroy();
            _pollTimer = timer.Every(_config.CheckIntervalSeconds, PollPlayers);
        }

        private void PollPlayers()
        {
            if (!_config.Enabled) return;

            var now = Interface.Oxide.Now;
            var normalMaxSeconds = _config.MaxRestrainMinutes * 60;

            var players = BasePlayer.activePlayerList;
            if (players == null || players.Count == 0) return;

            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || !p.IsConnected || p.IsDead()) continue;

                var tracked = _stateByVictim.TryGetValue(p.userID, out var st);

                var exempt = IsExemptCached(p, now);
                if (exempt && !(tracked && st.Debug))
                {
                    DestroyPromptUi(p);
                    _stateByVictim.Remove(p.userID);
                    continue;
                }

                var restrained = (p.IsRestrained || IsWearingHandcuffs(p));

                if (restrained)
                {
                    if (_immuneUntilUtc.TryGetValue(p.userID, out var immuneUntil) && immuneUntil > now)
                    {
                        TryUnrestrain(p);
                        continue;
                    }

                    if (!tracked)
                    {
                        st = new RestrainState
                        {
                            VictimId = p.userID,
                            EpisodeStartUtc = now,
                            LastSeenUnrestrainedUtc = 0,
                            Warned = false,
                            IsCurrentlyRestrained = true,
                            PromptShown = false,
                            Choice = 0,
                            Debug = false,
                            DebugMaxSeconds = 0,
                            HoodRemoved = false
                        };
                        _stateByVictim[p.userID] = st;
                    }
                    else
                    {
                        if (!st.IsCurrentlyRestrained && _config.EpisodeMergeWindowSeconds > 0)
                        {
                            var sinceUnrestrained = now - st.LastSeenUnrestrainedUtc;
                            if (sinceUnrestrained > _config.EpisodeMergeWindowSeconds)
                            {
                                st.EpisodeStartUtc = now;
                                st.Warned = false;
                                st.PromptShown = false;
                                st.Choice = 0;
                                st.HoodRemoved = false;
                            }
                        }

                        st.IsCurrentlyRestrained = true;
                    }

                    var maxSeconds = st.Debug && st.DebugMaxSeconds > 0 ? st.DebugMaxSeconds : normalMaxSeconds;

                    var elapsed = now - st.EpisodeStartUtc;
                    var remaining = maxSeconds - elapsed;

                    if (_config.WarnVictim && !st.Warned && _config.WarnSecondsBeforeLimit > 0 && remaining <= _config.WarnSecondsBeforeLimit && remaining > 0)
                    {
                        st.Warned = true;
                        p.ChatMessage(string.Format(lang.GetMessage(MsgVictimWarn, this, p.UserIDString), (int)Math.Ceiling(remaining)));
                    }

                    if (_config.RemoveHoodBeforePrompt && !st.HoodRemoved && _config.HoodRemoveSecondsBeforeLimit > 0 && remaining <= _config.HoodRemoveSecondsBeforeLimit && remaining > 0)
                    {
                        st.HoodRemoved = true;
                        TryRemovePrisonerHood(p);
                    }

                    if (_config.EnablePunishForgivePrompt && !st.PromptShown && _config.PromptSecondsBeforeLimit > 0 && remaining <= _config.PromptSecondsBeforeLimit && remaining > 0)
                    {
                        st.PromptShown = true;
                        ShowPromptUi(p);
                    }

                    if (elapsed >= maxSeconds)
                    {
                        EnforceLimit(p, st, (int)Math.Floor(elapsed));
                    }
                }
                else
{
    if (tracked)
    {
        if (st.IsCurrentlyRestrained)
        {
            st.IsCurrentlyRestrained = false;
            st.LastSeenUnrestrainedUtc = now;
        }
        else
        {
            if (_config.EpisodeMergeWindowSeconds <= 0 || (now - st.LastSeenUnrestrainedUtc) > _config.EpisodeMergeWindowSeconds)
            {
                DestroyPromptUi(p);
                _stateByVictim.Remove(p.userID);
                continue;
            }
        }

        DestroyPromptUi(p);

        if (st.Debug)
        {
            _stateByVictim.Remove(p.userID);
            continue;
        }

        if (_config.EpisodeMergeWindowSeconds <= 0)
            _stateByVictim.Remove(p.userID);
    }
}
}
        }

        private void EnforceLimit(BasePlayer victim, RestrainState st, int durationSeconds)
        {
            if (victim == null || !victim.IsConnected) return;

            _stateByVictim.Remove(victim.userID);
            DestroyPromptUi(victim);

            var originPos = victim.transform.position;
            var originWasSafeZone = victim.InSafeZone();

            TryUnrestrain(victim);
            TeleportToDestination(victim, DestinationToInt());

            NextTick(() => TryUnrestrain(victim));
            victim.ChatMessage(lang.GetMessage(MsgVictimAction, this, victim.UserIDString));

            if (_config.VictimRecuffImmunitySeconds > 0 && !(st != null && st.Debug))
                _immuneUntilUtc[victim.userID] = Interface.Oxide.Now + _config.VictimRecuffImmunitySeconds;

            var chaosDropped = false;

            if (_config.ChaosEnabled)
            {
                var shouldChaos = !_config.EnablePunishForgivePrompt || (st != null && st.Choice == 1);
                if (shouldChaos)
                    chaosDropped = TryChaosDrop(victim.userID, originPos, originWasSafeZone);
            }

            AddLogEntry(victim, durationSeconds, st != null ? st.Choice : 0, chaosDropped);

            if (st != null && st.Debug)
                return;

            Puts($"Enforced restraint limit on {victim.displayName} ({victim.userID}). Destination={_config.TeleportDestination}.");
        }

        private void AddLogEntry(BasePlayer victim, int durationSeconds, int choice, bool chaosDropped)
        {
            if (_data == null) return;

            _data.Log.Add(new LogEntry
            {
                Utc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                VictimId = victim.userID,
                VictimName = victim.displayName,
                DurationSeconds = durationSeconds,
                Destination = DestinationToInt(),
                Choice = choice,
                ChaosDropped = chaosDropped
            });

            while (_data.Log.Count > MaxLogEntries)
                _data.Log.RemoveAt(0);

            SaveData();
        }

        #endregion

private bool IsWearingHandcuffs(BasePlayer player)
{
    try
    {
        var wear = player?.inventory?.containerWear;
        if (wear == null) return false;

        var items = wear.itemList;
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it?.info == null) continue;
            if (it.info.itemid == HandcuffsItemId || string.Equals(it.info.shortname, HandcuffsShortname, StringComparison.OrdinalIgnoreCase))
                return true;
        }
    }
    catch { }

    return false;
}

private bool TryRemoveWornHandcuffs(BasePlayer player)
{
    try
    {
        var wear = player?.inventory?.containerWear;
        if (wear == null) return false;

        Item cuffs = null;
        var items = wear.itemList;

        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it?.info == null) continue;

            if (it.info.itemid == HandcuffsItemId || string.Equals(it.info.shortname, HandcuffsShortname, StringComparison.OrdinalIgnoreCase))
            {
                cuffs = it;
                break;
            }
        }

        if (cuffs == null) return false;

        cuffs.RemoveFromContainer();

        var main = player.inventory?.containerMain;
        if (main != null)
        {
            if (cuffs.MoveToContainer(main))
                return true;
        }

        cuffs.Drop(player.transform.position + (Vector3.up * 0.25f), player.estimatedVelocity);
        return true;
    }
    catch { }


	return false;
	}

	private void TryRemoveHandcuffsFromContainer(ItemContainer container)
{
    if (container == null) return;

    try
    {
        var items = container.itemList;
        if (items == null || items.Count == 0) return;

        for (var i = items.Count - 1; i >= 0; i--)
        {
            var it = items[i];
            if (it?.info == null) continue;

            if (it.info.itemid == HandcuffsItemId || string.Equals(it.info.shortname, HandcuffsShortname, StringComparison.OrdinalIgnoreCase))
            {
                it.RemoveFromContainer();
                it.Remove(); 
            }
        }
    }
    catch { }
}

private void TryClearRestrainedFlag(BasePlayer player)
{
    if (player == null) return;

    try
    {
        var names = new[] { "Restrained", "IsRestrained", "Handcuffed" };
        foreach (var n in names)
        {
            try
            {
                var flagObj = Enum.Parse(typeof(BasePlayer.PlayerFlags), n, true);
                player.SetPlayerFlag((BasePlayer.PlayerFlags)flagObj, false);
            }
            catch { /* ignore and try next */ }
        }
    }
    catch { }
}

        #region Prisoner Hood Removal

                private const string HandcuffsShortname = "handcuffs";
        private const int HandcuffsItemId = -839576748;

private const int PrisonerHoodItemId = -892718768;
        private const string PrisonerHoodShortname = "prisonerhood";

        private void TryRemovePrisonerHood(BasePlayer player)
        {
            try
            {
                var wear = player?.inventory?.containerWear;
                if (wear == null) return;

                Item hood = null;
                var items = wear.itemList;

                for (var i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it?.info == null) continue;

                    if (it.info.itemid == PrisonerHoodItemId ||
                        string.Equals(it.info.shortname, PrisonerHoodShortname, StringComparison.OrdinalIgnoreCase))
                    {
                        hood = it;
                        break;
                    }
                }

                if (hood == null) return;

                hood.RemoveFromContainer();
                hood.Drop(player.transform.position + (Vector3.up * 0.25f), player.estimatedVelocity);
            }
            catch { }
        }

        #endregion

        #region UI

        private void ShowPromptUi(BasePlayer player)
        {
            if (player == null) return;

            DestroyPromptUi(player);

            var container = new CuiElementContainer();

            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.65" },
                RectTransform = { AnchorMin = "0.35 0.35", AnchorMax = "0.65 0.60" },
                CursorEnabled = true
            }, "Overlay", UiLayer);

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = lang.GetMessage(MsgPromptTitle, this, player.UserIDString),
                    FontSize = 18,
                    Align = TextAnchor.UpperCenter,
                    Color = "1 1 1 1"
                },
                RectTransform = { AnchorMin = "0 0.65", AnchorMax = "1 0.98" }
            }, UiLayer);

            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = lang.GetMessage(MsgPromptBody, this, player.UserIDString),
                    FontSize = 14,
                    Align = TextAnchor.UpperCenter,
                    Color = "1 1 1 1"
                },
                RectTransform = { AnchorMin = "0.05 0.40", AnchorMax = "0.95 0.70" }
            }, UiLayer);

            container.Add(new CuiButton
            {
                Button =
                {
                    Command = "hcl.choice punish",
                    Color = "0.75 0.20 0.20 0.9",
                    Close = UiLayer
                },
                RectTransform = { AnchorMin = "0.07 0.08", AnchorMax = "0.47 0.32" },
                Text =
                {
                    Text = lang.GetMessage(MsgPromptPunish, this, player.UserIDString),
                    FontSize = 14,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                }
            }, UiLayer);

            container.Add(new CuiButton
            {
                Button =
                {
                    Command = "hcl.choice forgive",
                    Color = "0.20 0.60 0.25 0.9",
                    Close = UiLayer
                },
                RectTransform = { AnchorMin = "0.53 0.08", AnchorMax = "0.93 0.32" },
                Text =
                {
                    Text = lang.GetMessage(MsgPromptForgive, this, player.UserIDString),
                    FontSize = 14,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                }
            }, UiLayer);

            CuiHelper.AddUi(player, container);
        }

        private void DestroyPromptUi(BasePlayer player)
        {
            if (player == null) return;
            CuiHelper.DestroyUi(player, UiLayer);
        }

        #endregion

        #region Unrestrain

        private void TryUnrestrain(BasePlayer player)
{
    if (player == null) return;

    TryRemoveWornHandcuffs(player);
    TryRemoveHandcuffsFromContainer(player?.inventory?.containerMain);
    TryRemoveHandcuffsFromContainer(player?.inventory?.containerBelt);

    TryClearRestrainedFlag(player);

    try { player.SendNetworkUpdateImmediate(); } catch { }

    NextTick(() =>
    {
        if (player == null || !player.IsConnected) return;

        if (player.IsRestrained || IsWearingHandcuffs(player))
        {
            TryRemoveWornHandcuffs(player);
            TryRemoveHandcuffsFromContainer(player?.inventory?.containerMain);
            TryRemoveHandcuffsFromContainer(player?.inventory?.containerBelt);
            TryClearRestrainedFlag(player);

            try { player.SendNetworkUpdateImmediate(); } catch { }
        }
    });

    timer.Once(0.2f, () =>
    {
        if (player == null || !player.IsConnected) return;

        if (player.IsRestrained || IsWearingHandcuffs(player))
        {
            PrintWarning($"Unrestrain appears incomplete for {player.displayName} ({player.userID}): IsRestrained={player.IsRestrained}, WearingCuffs={IsWearingHandcuffs(player)}. If this persists, we can switch to a build-specific restraint clear routine.");
        }
    });
}



        #endregion

        #region Destination Teleport (with caching + blacklist)

        private void CacheMonumentCenter(int destination)
        {
            if (_monumentCenterByDest.ContainsKey(destination)) return;

            if (TryFindMonumentCenter(destination, out var center))
                _monumentCenterByDest[destination] = center;
        }

        private bool TryFindMonumentCenter(int destination, out Vector3 pos)
        {
            pos = Vector3.zero;

            try
            {
                var monuments = TerrainMeta.Path?.Monuments;
                if (monuments == null || monuments.Count == 0) return false;

                var dest = (SafeDestination)destination;
                var primary = dest == SafeDestination.BanditCamp ? "bandit" : "outpost";
                var secondary = dest == SafeDestination.BanditCamp ? "town" : "compound";

                for (var i = 0; i < monuments.Count; i++)
                {
                    var m = monuments[i];
                    if (m == null) continue;
                    var name = m.name;
                    if (string.IsNullOrEmpty(name)) continue;

                    if (name.IndexOf(primary, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        pos = m.transform.position;
                        return true;
                    }
                }

                for (var i = 0; i < monuments.Count; i++)
                {
                    var m = monuments[i];
                    if (m == null) continue;
                    var name = m.name;
                    if (string.IsNullOrEmpty(name)) continue;

                    if (name.IndexOf(secondary, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        pos = m.transform.position;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private void TeleportToDestination(BasePlayer player, int destination)
{
    if (player == null) return;

    CacheMonumentCenter(destination);

    if (!_monumentCenterByDest.TryGetValue(destination, out var center))
    {
        PrintWarning($"Teleport failed: destination monument not found (dest={destination}, player={player.displayName}).");
        return;
    }

    if (_config.TeleportOnlyWithinSafeZone)
    {
        BeginSafeZoneTeleport(player, destination, center);
        return;
    }

    ForceTeleport(player, MakeGrounded(center));
}

        private void BeginSafeZoneTeleport(BasePlayer player, int destination, Vector3 center)
{
    if (player == null) return;

    _pendingTeleports[player.userID] = new PendingTeleport
    {
        Destination = destination,
        Center = center,
        AttemptsRemaining = _config.SafeZoneSearchAttempts,
        Radius = _config.SafeZoneSearchRadius,
        TriedCached = false
    };

    AttemptNextSafeZoneCandidate(player);
}

private void AttemptNextSafeZoneCandidate(BasePlayer player)
{
    if (player == null || !player.IsConnected) return;

    if (!_pendingTeleports.TryGetValue(player.userID, out var pending))
        return;

            if (_config.TeleportAttemptTimeoutSeconds > 0f && (Interface.Oxide.Now - pending.StartedUtc) > _config.TeleportAttemptTimeoutSeconds)
            {
                _pendingTeleports.Remove(player.userID);
            _exemptPermCache.Remove(player.userID);
                ForceTeleport(player, MakeGrounded(pending.Center));
                return;
            }

    Vector3 candidate;
    var hasCandidate = false;

    if (_config.CacheSafeTeleportSpots && !pending.TriedCached && _data?.CachedSafeSpots != null &&
        _data.CachedSafeSpots.TryGetValue(pending.Destination, out var cached))
    {
        pending.TriedCached = true;
        candidate = cached.ToVector3();
        hasCandidate = true;
    }
    else
    {
        if (pending.AttemptsRemaining < 0)
        {
            _pendingTeleports.Remove(player.userID);
            _exemptPermCache.Remove(player.userID);
            ForceTeleport(player, MakeGrounded(pending.Center));
            return;
        }

        if (pending.AttemptsRemaining == _config.SafeZoneSearchAttempts)
        {
            candidate = MakeGrounded(pending.Center);
        }
        else
        {
            
var angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
var dist = UnityEngine.Random.Range(0.1f, Mathf.Max(0.1f, pending.Radius));
var offset = new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);
var p2 = pending.Center + offset;
candidate = MakeGrounded(p2);
        }

        pending.AttemptsRemaining--;
        hasCandidate = !IsBlacklisted(pending.Destination, candidate);
    }

    if (!hasCandidate)
    {
        NextTick(() => AttemptNextSafeZoneCandidate(player));
        return;
    }

    if (!IsTeleportSpotClear(candidate))
    {
        AddToBlacklist(pending.Destination, candidate);
        NextTick(() => AttemptNextSafeZoneCandidate(player));
        return;
    }

    if (IsTooCloseToStructures(candidate))
    {
        AddToBlacklist(pending.Destination, candidate);
        NextTick(() => AttemptNextSafeZoneCandidate(player));
        return;
    }

ForceTeleport(player, candidate);

    timer.Once(0.05f, () => ValidateSafeZoneLanding(player, candidate));
}

private void ValidateSafeZoneLanding(BasePlayer player, Vector3 attemptedSpot)
{
    if (player == null || !player.IsConnected) return;

    if (!_pendingTeleports.TryGetValue(player.userID, out var pending))
        return;

            if (_config.TeleportAttemptTimeoutSeconds > 0f && (Interface.Oxide.Now - pending.StartedUtc) > _config.TeleportAttemptTimeoutSeconds)
            {
                _pendingTeleports.Remove(player.userID);
            _exemptPermCache.Remove(player.userID);
                ForceTeleport(player, MakeGrounded(pending.Center));
                return;
            }

    if (player.InSafeZone())
    {
        CacheSafeSpot(pending.Destination, player.transform.position);
        _pendingTeleports.Remove(player.userID);
            _exemptPermCache.Remove(player.userID);
        return;
    }

    if (_config.CacheSafeTeleportSpots && _data?.CachedSafeSpots != null &&
        _data.CachedSafeSpots.TryGetValue(pending.Destination, out var cached) &&
        (attemptedSpot - cached.ToVector3()).sqrMagnitude < 0.25f)
    {
        _data.CachedSafeSpots.Remove(pending.Destination);
        AddToBlacklist(pending.Destination, attemptedSpot);
        SaveData();
    }

    NextTick(() => AttemptNextSafeZoneCandidate(player));
}

        private void CacheSafeSpot(int destination, Vector3 landedPos)
        {
            if (!_config.CacheSafeTeleportSpots) return;
            if (_data == null) return;

            if (_data.CachedSafeSpots == null)
                _data.CachedSafeSpots = new Dictionary<int, SerializableVector3>();

            _data.CachedSafeSpots[destination] = new SerializableVector3(landedPos);
            SaveData();
        }

        private bool IsBlacklisted(int destination, Vector3 candidate)
        {
            if (_data?.BlacklistedSpots == null) return false;
            if (_config.CacheBlacklistMinDistance <= 0f) return false;

            if (!_data.BlacklistedSpots.TryGetValue(destination, out var list) || list == null || list.Count == 0)
                return false;

            var min2 = _config.CacheBlacklistMinDistance * _config.CacheBlacklistMinDistance;
            for (var i = 0; i < list.Count; i++)
            {
                var v = list[i].ToVector3();
                var d = candidate - v;
                if (d.sqrMagnitude <= min2) return true;
            }

            return false;
        }

        private void AddToBlacklist(int destination, Vector3 badSpot)
{
    if (_data == null) return;

    if (_data.BlacklistedSpots == null)
        _data.BlacklistedSpots = new Dictionary<int, List<SerializableVector3>>();

    if (!_data.BlacklistedSpots.TryGetValue(destination, out var list) || list == null)
    {
        list = new List<SerializableVector3>();
        _data.BlacklistedSpots[destination] = list;
    }

    if (!IsBlacklisted(destination, badSpot))
        list.Add(new SerializableVector3(badSpot));

    var max = _config.BlacklistMaxEntriesPerDestination;
    if (max > 0 && list.Count > max)
    {
        var removeCount = list.Count - max;
        list.RemoveRange(0, removeCount);
    }
}



private bool IsTooCloseToStructures(Vector3 pos)
{
    try
    {
        var radius = Mathf.Max(0f, _config.BuildingProximityRejectRadius);
        if (radius <= 0f) return false;

        var entities = new List<BaseEntity>();
        Vis.Entities(pos, radius, entities, Layers.Mask.Construction | Layers.Mask.Deployed, QueryTriggerInteraction.Ignore);

        for (var i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            if (e == null) continue;

            if (e is BuildingBlock) return true;
            if (e is DecayEntity) return true;

            var spn = e.ShortPrefabName;
            if (!string.IsNullOrEmpty(spn))
            {
                if (spn.Contains("wall") || spn.Contains("floor") || spn.Contains("foundation") || spn.Contains("roof"))
                    return true;
                if (spn.Contains("building") || spn.Contains("barricade") || spn.Contains("deploy"))
                    return true;
            }
        }

        return false;
    }
    catch
    {
        return false;
    }
}

private bool IsTeleportSpotClear(Vector3 pos)
{
    

    try
    {

        var radius = _config.TeleportClearanceRadius;
        var height = _config.TeleportClearanceHeight;

        var bottom = pos + new Vector3(0f, 0.1f, 0f);
        var top = pos + new Vector3(0f, Mathf.Max(0.2f, height), 0f);

        var mask = Layers.Mask.World | Layers.Mask.Construction | Layers.Mask.Deployed;

        return !Physics.CheckCapsule(bottom, top, radius, mask, QueryTriggerInteraction.Ignore);
    }
    catch
    {
        return true;
    }
}

        private Vector3 MakeGrounded(Vector3 pos)
{
    try
    {
        var start = pos + Vector3.up * 200f;
        RaycastHit hit;
        var mask = Layers.Mask.World;
        if (Physics.Raycast(start, Vector3.down, out hit, 400f, mask, QueryTriggerInteraction.Ignore))
        {
            pos = hit.point;
        }
        else
        {
            var y = TerrainMeta.HeightMap.GetHeight(pos);
            pos.y = y;
        }
    }
    catch
    {
        try
        {
            var y = TerrainMeta.HeightMap.GetHeight(pos);
            pos.y = y;
        }
        catch { }
    }

    return pos + _config.DestinationOffset;
}

        private void ForceTeleport(BasePlayer player, Vector3 dest)
        {
            try { player.Teleport(dest); }
            catch
            {
                try
                {
                    player.MovePosition(dest);
                    player.SendNetworkUpdateImmediate();
                }
                catch { }
            }
        }

        #endregion

        #region Chaos Mode

        private bool TryChaosDrop(ulong victimId, Vector3 originPos, bool originWasSafeZone)
        {
            if (_config.ChaosOnlyOutsideSafeZones && originWasSafeZone)
                return false;

            if (_config.ChaosRadiusCheckPlayers)
            {
                if (CountPlayersInRadius(originPos, _config.ChaosRadiusCheckMeters) > _config.ChaosMaxPlayersInRadius)
                    return false;
            }

            if (!ChaosCooldownReady(victimId))
                return false;

            const string prefab = "assets/prefabs/tools/c4/explosive.timed.deployed.prefab";

            var ent = GameManager.server.CreateEntity(prefab, originPos, Quaternion.identity, true);
            if (ent == null)
            {
                PrintWarning("Chaos drop failed: could not create timed explosive entity.");
                return false;
            }

            try { ent.OwnerID = 0UL; } catch { }
            ent.Spawn();
            MarkChaosUsed(victimId);
            return true;
        }

        private bool ChaosCooldownReady(ulong victimId)
        {
            if (_data == null) return true;
            if (_config.ChaosCooldownMinutesPerVictim <= 0) return true;

            if (!_data.LastChaosUtc.TryGetValue(victimId, out var last))
                return true;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var cooldownSeconds = _config.ChaosCooldownMinutesPerVictim * 60L;

            return now - last >= cooldownSeconds;
        }

        private void MarkChaosUsed(ulong victimId)
        {
            if (_data == null) return;
            _data.LastChaosUtc[victimId] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveData();
        }

        private int CountPlayersInRadius(Vector3 pos, float radius)
        {
            if (radius <= 0f) return 0;

            var r2 = radius * radius;
            var count = 0;

            var players = BasePlayer.activePlayerList;
            for (var i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p == null || !p.IsConnected || p.IsDead()) continue;

                var d = p.transform.position - pos;
                if (d.sqrMagnitude <= r2) count++;
            }

            return count;
        }

        #endregion
    }
}