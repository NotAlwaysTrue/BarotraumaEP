using Barotrauma.Networking;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Xml;

namespace Barotrauma
{
    // Only the game thread can read and change game state. HTTP requests exchange plain data with it.
    internal sealed class ServerManagementHttp
    {
        public sealed record Command(string Action, Guid? PlayerId, Guid? BanId, string Reason, int? BanMinutes,
            ClientPermissions? Permissions, string[] ConsoleCommands, RoundSettings Round, MiscSettings Misc,
            string CampaignSaveId = null, string CampaignSaveName = null, Dictionary<string, JsonElement> Advanced = null,
            string Password = null, string CampaignSubmarine = null, string CampaignSeed = null,
            string PermissionOperation = null, string PermissionPresetId = null);
        public sealed record RoundSettings(string Mode, string Submarine, string Seed, float? Difficulty,
            bool? AutoRestart, float? AutoRestartInterval, bool? StartWhenClientsReady, bool? RandomizeSeed, string[] MissionTypes);
        public sealed record MiscSettings(string ServerName, string Message, int? MaxPlayers, int? TickRate,
            string PlayStyle, bool? AllowVoteKick, bool? AllowEndVoting, bool? AllowRewiring, bool? AllowDisguises);

        public sealed record Reply(HttpStatusCode Status, object Body, bool ShutdownAfterReply = false);
        private sealed record Error(string ErrorMessage);
        private sealed record Pending(Command Command, bool ReadLogs, CancellationToken Cancellation, TaskCompletionSource<Reply> Completion);
        private sealed record EntryId(Guid Value);
        private sealed record Player(Guid Id, string Name, bool Owner, bool InGame, bool CanBan, int Permissions, string[] ConsoleCommands);
        private sealed record Ban(Guid Id, string Name, string Reason, DateTime? ExpiresAtUtc);
        private sealed record Choice(string Value, string Label);
        private sealed record CampaignSaveChoice(string Value, string Name, string Submarine, DateTime? SavedAtUtc, bool Loaded);
        private sealed record PermissionChoice(string Name, int Value);
        private sealed record PermissionPresetChoice(string Value, string Label, int Permissions, string[] ConsoleCommands, bool IsCustom);
        private sealed record PermissionOptions(PermissionPresetChoice[] Presets, string[] ConsoleCommands);
        private sealed record State(Player[] Players, Ban[] Bans, PermissionChoice[] Permissions, string[] PlayStyles,
            Choice[] Modes, Choice[] MissionTypes, string[] Submarines, RoundSettings Round, MiscSettings Misc, string RoundStatus,
            double? RoundSeconds, float? AutoRestartSeconds, string ActiveMode, string ActiveSubmarine, int MaxPlayerLimit,
            CampaignSaveChoice[] CampaignSaves, ServerManagementOptions.Field[] Advanced, bool HasPassword, int CargoTypeLimit,
            bool CampaignLoaded, bool CanSaveAndEndRound, PermissionOptions PermissionOptions);

        private static readonly PermissionChoice[] PermissionChoices = Enum.GetValues<ClientPermissions>()
            .Where(permission => permission != ClientPermissions.None && permission != ClientPermissions.All)
            .Select(permission => new PermissionChoice(permission.ToString(), (int)permission)).ToArray();
        private static readonly string[] PlayStyles = Enum.GetNames<PlayStyle>();
        private readonly ConditionalWeakTable<Client, EntryId> playerIds = new ConditionalWeakTable<Client, EntryId>();
        private readonly ConditionalWeakTable<BannedPlayer, EntryId> banIds = new ConditionalWeakTable<BannedPlayer, EntryId>();
        private readonly Channel<Pending> requests = Channel.CreateBounded<Pending>(new BoundedChannelOptions(8)
        {
            SingleReader = true
        });

        public async Task<Reply> RequestAsync(Command command, CancellationToken cancellation, bool readLogs = false)
        {
            var completion = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!requests.Writer.TryWrite(new Pending(command, readLogs, cancellation, completion)))
            {
                return Failure(HttpStatusCode.ServiceUnavailable, "The management queue is full. Try again.");
            }
            return await completion.Task.WaitAsync(cancellation).ConfigureAwait(false);
        }

        public void ProcessRequests()
        {
            // Process at most one live request per iteration, even if several HTTP clients are connected.
            while (requests.Reader.TryRead(out var pending))
            {
                if (pending.Cancellation.IsCancellationRequested) { continue; }
                try
                {
                    var server = GameMain.Server;
                    var lobby = GameMain.NetLobbyScreen;
                    if (server == null || lobby == null)
                    {
                        pending.Completion.SetResult(Failure(HttpStatusCode.ServiceUnavailable, "The game server is unavailable."));
                    }
                    else if (pending.ReadLogs)
                    {
                        pending.Completion.SetResult(new Reply(HttpStatusCode.OK, new
                        {
                            Entries = server.ServerSettings.ServerLog.ReadRecentLines(200),
                            Recording = server.ServerSettings.SaveServerLogs
                        }));
                    }
                    else if (pending.Command?.Action == "shutdownServer")
                    {
                        pending.Completion.SetResult(new Reply(HttpStatusCode.Accepted,
                            new { ShuttingDown = true }, ShutdownAfterReply: true));
                    }
                    else
                    {
                        var error = pending.Command == null ? null : Apply(server, lobby, pending.Command);
                        pending.Completion.SetResult(error ?? new Reply(HttpStatusCode.OK,
                            ReadState(server, lobby, pending.Command?.Action)));
                    }
                }
                catch (Exception error)
                {
                    DebugConsole.AddWarning($"HTTP server management failed ({error.GetType().Name}).");
                    pending.Completion.SetResult(Failure(HttpStatusCode.InternalServerError, "The server could not complete the request. Refresh before retrying."));
                }
                return;
            }
        }

        private State ReadState(GameServer server, NetLobbyScreen lobby, string action)
        {
            var settings = server.ServerSettings;
            var session = GameMain.GameSession;
            var players = server.ConnectedClients.Select(client => new Player(GetPlayerId(client), client.Name,
                IsOwner(server, client), client.InGame, CanBan(client), (int)client.Permissions,
                CommandNames(client.PermittedConsoleCommands))).ToArray();
            var bans = settings.BanList.BannedPlayers.Where(ban => !ban.Expired)
                .Select(ban => new Ban(GetBanId(ban), ban.Name, ban.Reason,
                    ban.ExpirationTime.TryUnwrap(out var expiration) ? DateTime.SpecifyKind(expiration.ToUtcValue(), DateTimeKind.Utc) : null))
                .ToArray();
            var modes = lobby.GameModes.Where(mode => !mode.IsSinglePlayer)
                .Select(mode => new Choice(mode.Identifier.Value, mode.Name.Value)).ToArray();
            var missionTypes = GetSelectableMissionTypes();
            var missionChoices = missionTypes.Select(type =>
                new Choice(type.Value, TextManager.Get($"MissionType.{type}").Fallback(type.Value).Value)).ToArray();
            var enabledMissionTypes = lobby.MissionTypes.ToHashSet();
            var selectedMissionTypes = missionTypes
                .Where(type => enabledMissionTypes.Contains(Tags.MissionTypeAll) || enabledMissionTypes.Contains(type))
                .Select(type => type.Value).ToArray();
            var submarines = lobby.GetSubList().Where(sub => IsSelectableSubmarine(sub, settings)).Select(sub => sub.Name)
                .Append(lobby.SelectedSub?.Name ?? "").Where(name => name.Length != 0).Distinct().OrderBy(name => name).ToArray();
            var round = new RoundSettings(lobby.SelectedModeIdentifier.Value, lobby.SelectedSub?.Name, lobby.LevelSeed, settings.SelectedLevelDifficulty,
                settings.AutoRestart, settings.AutoRestartInterval, settings.StartWhenClientsReady, settings.RandomizeSeed, selectedMissionTypes);
            var misc = new MiscSettings(settings.ServerName, settings.ServerMessageText ?? "", settings.MaxPlayers, settings.TickRate,
                settings.PlayStyle.ToString(), settings.AllowVoteKick, settings.AllowEndVoting, settings.AllowRewiring, settings.AllowDisguises);
            string roundStatus = server.RoundStatus;
            return new State(players, bans, PermissionChoices, PlayStyles, modes, missionChoices, submarines, round, misc, roundStatus,
                server.GameStarted ? session?.RoundDuration : null, roundStatus == "lobby" ? server.RoundAutoRestartSeconds : null,
                server.GameStarted ? session?.GameMode?.Preset?.Name.Value : null,
                server.GameStarted ? session?.SubmarineInfo?.Name : null, NetConfig.MaxPlayers,
                action == "campaignSaves" ? ReadCampaignSaves(session) : null,
                action == "advancedSettings" ? ServerManagementOptions.Read(server, lobby) : null,
                settings.HasPassword, ServerSettings.MaxExtraCargoItemTypes, HasLoadedCampaign(session), server.CanSaveAndEndRound,
                action == "permissionOptions" ? ReadPermissionOptions() : null);
        }

        private static string[] CommandNames(IEnumerable<DebugConsole.Command> commands) => commands
            .Select(command => command.Names[0].Value).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        private static PermissionOptions ReadPermissionOptions() => new PermissionOptions(
            PermissionPreset.List.Select(preset => new PermissionPresetChoice(preset.Identifier.Value, preset.DisplayName.Value,
                (int)preset.Permissions, CommandNames(preset.PermittedCommands), preset.IsCustom)).ToArray(),
            CommandNames(DebugConsole.Commands));

        private Reply Apply(GameServer server, NetLobbyScreen lobby, Command command)
        {
            return command.Action switch
            {
                "kick" or "ban" or "permissions" => ManagePlayer(server, command),
                "permissionOptions" => null,
                "unban" => Unban(server, command.BanId),
                "startRound" => StartRound(server, lobby),
                "endRound" => EndRound(server),
                "saveAndEndRound" => SaveAndEndRound(server),
                "campaignSaves" => CheckCampaignLobby(server, lobby),
                "loadCampaign" => LoadCampaign(server, lobby, command.CampaignSaveId),
                "createCampaign" => CreateCampaign(server, lobby, command.CampaignSaveName, command.CampaignSubmarine, command.CampaignSeed),
                "advancedSettings" => SaveAdvancedSettings(server, lobby, command.Advanced),
                "setPassword" => SetPassword(server.ServerSettings, lobby, command.Password),
                "roundSettings" => SaveRoundSettings(server, lobby, command.Round),
                "miscSettings" => SaveMiscSettings(server.ServerSettings, lobby, command.Misc),
                _ => Failure(HttpStatusCode.BadRequest, "Unknown management action.")
            };
        }

        private Reply ManagePlayer(GameServer server, Command command)
        {
            var client = server.ConnectedClients.FirstOrDefault(player => GetPlayerId(player) == command.PlayerId);
            if (client == null) { return Failure(HttpStatusCode.NotFound, "The player is no longer connected."); }
            if (IsOwner(server, client)) { return Failure(HttpStatusCode.Forbidden, "The server owner cannot be modified."); }

            if (command.Action == "permissions")
            {
                return SetPermissions(server, client, command);
            }

            string reason = command.Reason?.Trim() ?? "";
            if (!IsValidText(reason, 256) || reason.Any(char.IsControl))
            {
                return Failure(HttpStatusCode.BadRequest, "Use a single-line reason of at most 256 characters.");
            }
            if (command.Action == "kick")
            {
                server.KickClient(client, reason);
                return null;
            }
            if (!CanBan(client))
            {
                return Failure(HttpStatusCode.BadRequest, "This local player has no account that the server can ban.");
            }
            if (command.BanMinutes is < 1 or > 525600)
            {
                return Failure(HttpStatusCode.BadRequest, "Use a ban duration of 1 to 525600 minutes, or leave it blank for a permanent ban.");
            }
            server.BanClient(client, reason, command.BanMinutes is int minutes ? TimeSpan.FromMinutes(minutes) : null);
            return null;
        }

        private static Reply SetPermissions(GameServer server, Client client, Command command)
        {
            var permissions = client.Permissions;
            var allowedCommands = new HashSet<DebugConsole.Command>(client.PermittedConsoleCommands);
            switch (command.PermissionOperation)
            {
                case "preset":
                {
                    var preset = string.IsNullOrWhiteSpace(command.PermissionPresetId) ? null :
                        PermissionPreset.List.Find(entry => entry.Identifier == command.PermissionPresetId);
                    if (preset == null || (preset.Permissions & ~ClientPermissions.All) != 0)
                    {
                        return Failure(HttpStatusCode.BadRequest, "Select an available permission preset.");
                    }
                    permissions = preset.IsCustom ? permissions | preset.Permissions : preset.Permissions;
                    if (!preset.IsCustom) { allowedCommands.Clear(); }
                    allowedCommands.UnionWith(preset.PermittedCommands);
                    break;
                }
                case "addPermission":
                case "removePermission":
                {
                    if (command.Permissions is not ClientPermissions permission || !PermissionChoices.Any(choice => choice.Value == (int)permission))
                    {
                        return Failure(HttpStatusCode.BadRequest, "Invalid player permission.");
                    }
                    permissions = command.PermissionOperation == "addPermission" ? permissions | permission : permissions & ~permission;
                    break;
                }
                case "addCommand":
                case "removeCommand":
                {
                    if (command.ConsoleCommands is not [string name] || name.Length is 0 or > 100 || DebugConsole.FindCommand(name) is not { } consoleCommand)
                    {
                        return Failure(HttpStatusCode.BadRequest, "Select a registered console command.");
                    }
                    if (command.PermissionOperation == "addCommand")
                    {
                        permissions |= ClientPermissions.ConsoleCommands;
                        allowedCommands.Add(consoleCommand);
                    }
                    else { allowedCommands.Remove(consoleCommand); }
                    break;
                }
                case "addAll":
                    permissions = ClientPermissions.All;
                    allowedCommands.UnionWith(DebugConsole.Commands);
                    break;
                case "clearAll":
                    permissions = ClientPermissions.None;
                    allowedCommands.Clear();
                    break;
                case null:
                    if (command.Permissions is not ClientPermissions replacement || (replacement & ~ClientPermissions.All) != 0)
                    {
                        return Failure(HttpStatusCode.BadRequest, "Invalid player permissions.");
                    }
                    permissions = replacement;
                    allowedCommands.Clear();
                    if (permissions.HasFlag(ClientPermissions.ConsoleCommands))
                    {
                        var names = command.ConsoleCommands;
                        if (names == null || names.Length > DebugConsole.Commands.Count || names.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 100))
                        {
                            return Failure(HttpStatusCode.BadRequest, "Invalid console command list.");
                        }
                        allowedCommands.UnionWith(names.Select(DebugConsole.FindCommand));
                        if (allowedCommands.Contains(null))
                        {
                            return Failure(HttpStatusCode.BadRequest, "One or more console command names are unknown.");
                        }
                    }
                    break;
                default:
                    return Failure(HttpStatusCode.BadRequest, "Unknown permission operation.");
            }

            if (!permissions.HasFlag(ClientPermissions.ConsoleCommands)) { allowedCommands.Clear(); }
            client.SetPermissions(permissions, allowedCommands);
            server.UpdateClientPermissions(client);
            GameServer.Log($"HTTP panel updated permissions for {client.Name}.", ServerLog.MessageType.ServerMessage);
            return null;
        }

        private Reply Unban(GameServer server, Guid? banId)
        {
            var ban = server.ServerSettings.BanList.BannedPlayers.FirstOrDefault(entry => !entry.Expired && GetBanId(entry) == banId);
            if (ban == null) { return Failure(HttpStatusCode.NotFound, "The ban is no longer active."); }
            server.ServerSettings.BanList.RemoveBan(ban);
            return null;
        }

        private static Reply StartRound(GameServer server, NetLobbyScreen lobby)
        {
            if (server.GameStarted || server.RoundStartInProgress || Screen.Selected != lobby)
            {
                return Failure(HttpStatusCode.Conflict, "The server must be idle in the lobby to start a round.");
            }
            if (lobby.SelectedMode == GameModePreset.MultiPlayerCampaign && !HasLoadedCampaign(GameMain.GameSession))
            {
                return Failure(HttpStatusCode.Conflict, "Create or load a campaign save before starting the round.");
            }
            var result = server.TryStartGame();
            return result == GameServer.TryStartGameResult.Success ? null
                : Failure(HttpStatusCode.Conflict, TextManager.Get($"TryStartGameError.{result}").Fallback(result.ToString()).Value);
        }

        private static Reply EndRound(GameServer server)
        {
            if (!server.GameStarted || GameMain.GameSession == null)
            {
                return Failure(HttpStatusCode.Conflict, "There is no running round to end.");
            }
            server.EndGame();
            return null;
        }

        private static Reply SaveAndEndRound(GameServer server)
        {
            if (!server.CanSaveAndEndRound)
            {
                return Failure(HttpStatusCode.Conflict, "The active campaign round can only be saved at a friendly outpost.");
            }
            return server.TrySaveAndEndRound() ? null
                : Failure(HttpStatusCode.InternalServerError, "The round ended, but the campaign save failed. Check the server log.");
        }

        private static Reply CheckCampaignLobby(GameServer server, NetLobbyScreen lobby)
        {
            return !InLobby(server, lobby) || lobby.SelectedMode != GameModePreset.MultiPlayerCampaign
                ? Failure(HttpStatusCode.Conflict, "Campaign saves can only be selected in the lobby in Campaign mode.")
                : null;
        }

        private static IEnumerable<CampaignMode.SaveInfo> GetCampaignSaves() =>
            SaveUtil.GetSaveFiles(SaveUtil.SaveType.Multiplayer, includeInCompatible: false, logLoadErrors: false)
                .Where(save => Path.GetExtension(save.FilePath) == ".save");

        private static string GetCampaignSaveId(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (OperatingSystem.IsWindows()) { fullPath = fullPath.ToUpperInvariant(); }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
        }

        private static CampaignSaveChoice[] ReadCampaignSaves(GameSession session)
        {
            string loadedId = session?.GameMode is MultiPlayerCampaign ? GetCampaignSaveId(session.DataPath.SavePath) : null;
            return GetCampaignSaves().Select(save =>
            {
                string id = GetCampaignSaveId(save.FilePath);
                return new CampaignSaveChoice(id, Path.GetFileNameWithoutExtension(save.FilePath), save.SubmarineName,
                    save.SaveTime.TryUnwrap(out var savedAt) ? DateTime.SpecifyKind(savedAt.ToUtcValue(), DateTimeKind.Utc) : null,
                    id == loadedId);
            }).OrderByDescending(save => save.SavedAtUtc).ThenBy(save => save.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static Reply LoadCampaign(GameServer server, NetLobbyScreen lobby, string saveId)
        {
            var error = CheckCampaignLobby(server, lobby);
            if (error != null) { return error; }
            if (saveId?.Length != 64)
            {
                return Failure(HttpStatusCode.BadRequest, "Select a valid campaign save.");
            }
            string path = GetCampaignSaves().FirstOrDefault(save => GetCampaignSaveId(save.FilePath) == saveId).FilePath;
            if (path == null || !SaveUtil.IsValidSavePath(path, SaveUtil.SaveType.Multiplayer) || !File.Exists(path))
            {
                return Failure(HttpStatusCode.NotFound, "The selected campaign save is unavailable. Reopen the save list.");
            }

            return MultiPlayerCampaign.LoadCampaign(CampaignDataPath.CreateRegular(path), client: null)
                ? null
                : Failure(HttpStatusCode.Conflict, "The server could not load the selected campaign save.");
        }

        private static Reply CreateCampaign(GameServer server, NetLobbyScreen lobby, string name, string submarine, string seed)
        {
            var error = CheckCampaignLobby(server, lobby);
            if (error != null) { return error; }
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || !IsValidText(name, 64) || name.EndsWith('.') ||
                name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character)) ||
                (OperatingSystem.IsWindows() && Regex.IsMatch(name, @"^(?:CON|PRN|AUX|NUL|COM[1-9\u00B9\u00B2\u00B3]|LPT[1-9\u00B9\u00B2\u00B3])(?:\.|$)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            {
                return Failure(HttpStatusCode.BadRequest, "Enter a save name of up to 64 characters without file path characters.");
            }
            seed ??= lobby.LevelSeed;
            if (!IsValidText(seed, 64) || string.IsNullOrWhiteSpace(seed))
            {
                return Failure(HttpStatusCode.BadRequest, "Enter a level seed of up to 64 characters.");
            }
            var sub = submarine == null || lobby.SelectedSub?.Name == submarine ? lobby.SelectedSub :
                lobby.GetSubList().FirstOrDefault(candidate => candidate.Name == submarine && IsSelectableSubmarine(candidate, server.ServerSettings));
            if (sub == null || !IsSelectableSubmarine(sub, server.ServerSettings) || !File.Exists(sub.FilePath))
            {
                return Failure(HttpStatusCode.Conflict, "Select an available starting submarine.");
            }
            string path = SaveUtil.CreateSavePath(SaveUtil.SaveType.Multiplayer, name);
            return MultiPlayerCampaign.StartNewCampaign(path, sub.FilePath, seed, server.ServerSettings.CampaignSettings)
                ? null
                : Failure(HttpStatusCode.Conflict, "The server could not create the campaign save.");
        }

        private static Reply SaveAdvancedSettings(GameServer server, NetLobbyScreen lobby, Dictionary<string, JsonElement> changes)
        {
            if (changes == null) { return null; }
            string error = ServerManagementOptions.Apply(server, lobby, changes, InLobby(server, lobby));
            if (error != null) { return Failure(HttpStatusCode.BadRequest, error); }
            SaveSettings(server.ServerSettings, lobby);
            return null;
        }

        private static Reply SetPassword(ServerSettings settings, NetLobbyScreen lobby, string password)
        {
            if (password == null || !IsValidText(password, 64) || password.Any(char.IsControl))
            {
                return Failure(HttpStatusCode.BadRequest, "Enter a password of up to 64 characters.");
            }
            settings.SetPassword(password);
            settings.ServerDetailsChanged = true;
            SaveSettings(settings, lobby);
            return null;
        }

        private static bool InLobby(GameServer server, NetLobbyScreen lobby) =>
            !server.GameStarted && !server.RoundStartInProgress && Screen.Selected == lobby;

        private static bool HasLoadedCampaign(GameSession session) => session?.GameMode is MultiPlayerCampaign { Map: not null };

        private static IEnumerable<Identifier> GetSelectableMissionTypes()
        {
            var coOpTypes = MissionPrefab.Prefabs
                .Where(prefab => MissionPrefab.CoOpMissionClasses.ContainsValue(prefab.MissionClass) &&
                    prefab.Commonness > 0.0f && !prefab.CampaignOnly && !prefab.SingleplayerOnly)
                .Select(prefab => prefab.Type).ToHashSet();
            return MissionPrefab.GetAllMultiplayerSelectableMissionTypes().Where(coOpTypes.Contains);
        }

        private static Reply SaveRoundSettings(GameServer server, NetLobbyScreen lobby, RoundSettings update)
        {
            if (server.GameStarted || server.RoundStartInProgress || Screen.Selected != lobby)
            {
                return Failure(HttpStatusCode.Conflict, "Round settings can only be changed in the lobby.");
            }
            if (update == null || (update.Seed != null && (!IsValidText(update.Seed, 64) || string.IsNullOrWhiteSpace(update.Seed)))
                || update.Difficulty is < 0 or > 100 || update.AutoRestartInterval is < 1 or > 86400)
            {
                return Failure(HttpStatusCode.BadRequest, "Invalid level seed, difficulty or auto-restart interval.");
            }
            var settings = server.ServerSettings;
            var mode = update.Mode == null ? lobby.SelectedMode : Array.Find(lobby.GameModes,
                mode => !mode.IsSinglePlayer && mode.Identifier == update.Mode);
            var sub = update.Submarine == null || update.Submarine == lobby.SelectedSub?.Name ? lobby.SelectedSub :
                lobby.GetSubList().FirstOrDefault(sub => sub.Name == update.Submarine && IsSelectableSubmarine(sub, settings));
            if (mode == null || (update.Submarine != null && sub == null))
            {
                return Failure(HttpStatusCode.BadRequest, "The selected game mode or submarine is unavailable.");
            }
            if (mode == GameModePreset.MultiPlayerCampaign &&
                (update.Seed != null || update.Difficulty.HasValue || update.RandomizeSeed.HasValue || update.AutoRestart.HasValue ||
                 update.AutoRestartInterval.HasValue || (update.Submarine != null && HasLoadedCampaign(GameMain.GameSession))))
            {
                return Failure(HttpStatusCode.Conflict, "One or more round settings are unavailable in Campaign mode.");
            }
            if (update.MissionTypes != null)
            {
                if (mode != GameModePreset.Mission)
                {
                    return Failure(HttpStatusCode.BadRequest, "Mission types can only be changed in Mission mode.");
                }
                var selectable = GetSelectableMissionTypes().ToHashSet();
                if (update.MissionTypes.Length == 0 ||
                    update.MissionTypes.Any(type => string.IsNullOrWhiteSpace(type) || !selectable.Contains(type.ToIdentifier())))
                {
                    return Failure(HttpStatusCode.BadRequest, "Select at least one available mission type.");
                }
                lobby.MissionTypes = lobby.MissionTypes.Where(type => type != Tags.MissionTypeAll && !selectable.Contains(type))
                    .Concat(update.MissionTypes.Select(type => type.ToIdentifier()));
                settings.UpdateFlag(ServerSettings.NetFlags.Misc);
            }

            lobby.SelectedModeIdentifier = mode.Identifier;
            if (sub != null && sub != lobby.SelectedSub)
            {
                lobby.SelectedSub = sub;
                settings.SelectedSubmarine = sub.Name;
            }
            if (update.Seed != null && update.Seed != lobby.LevelSeed)
            {
                lobby.LevelSeed = update.Seed;
                settings.UpdateFlag(ServerSettings.NetFlags.LevelSeed);
            }
            settings.SelectedLevelDifficulty = update.Difficulty ?? settings.SelectedLevelDifficulty;
            if (update.AutoRestartInterval is float interval && interval != settings.AutoRestartInterval)
            {
                settings.AutoRestartInterval = interval;
                if (settings.AutoRestart) { settings.AutoRestartTimer = interval; }
            }
            if (update.AutoRestart is bool autoRestart && autoRestart != settings.AutoRestart) { settings.AutoRestart = autoRestart; }
            settings.StartWhenClientsReady = update.StartWhenClientsReady ?? settings.StartWhenClientsReady;
            settings.RandomizeSeed = update.RandomizeSeed ?? settings.RandomizeSeed;
            SaveSettings(settings, lobby);
            return null;
        }

        private static Reply SaveMiscSettings(ServerSettings settings, NetLobbyScreen lobby, MiscSettings update)
        {
            if (update == null || (update.ServerName != null && (!IsValidText(update.ServerName, NetConfig.ServerNameMaxLength)
                || string.IsNullOrWhiteSpace(update.ServerName) || update.ServerName.Any(char.IsControl)))
                || (update.Message != null && !IsValidText(update.Message, NetConfig.ServerMessageMaxLength))
                || update.MaxPlayers is < 1 or > NetConfig.MaxPlayers || update.TickRate is < 1 or > 60)
            {
                return Failure(HttpStatusCode.BadRequest, "Invalid server name, message, player limit or network tick rate.");
            }
            if (update.PlayStyle != null)
            {
                if (!Enum.TryParse(update.PlayStyle, out PlayStyle playStyle) || !PlayStyles.Contains(update.PlayStyle))
                {
                    return Failure(HttpStatusCode.BadRequest, "Invalid server play style.");
                }
                settings.PlayStyle = playStyle;
            }
            if (update.ServerName != null) { settings.ServerName = update.ServerName; }
            if (update.Message != null) { settings.ServerMessageText = update.Message; }
            if (update.MaxPlayers is int maxPlayers && maxPlayers != settings.MaxPlayers)
            {
                settings.MaxPlayers = maxPlayers;
                settings.ServerDetailsChanged = true;
            }
            settings.TickRate = update.TickRate ?? settings.TickRate;
            settings.AllowVoteKick = update.AllowVoteKick ?? settings.AllowVoteKick;
            settings.AllowEndVoting = update.AllowEndVoting ?? settings.AllowEndVoting;
            settings.AllowRewiring = update.AllowRewiring ?? settings.AllowRewiring;
            settings.AllowDisguises = update.AllowDisguises ?? settings.AllowDisguises;
            SaveSettings(settings, lobby);
            return null;
        }

        private static void SaveSettings(ServerSettings settings, NetLobbyScreen lobby)
        {
            settings.ForcePropertyUpdate();
            lobby.LastUpdateID++;
            settings.SaveSettings();
        }

        private Guid GetPlayerId(Client client) => playerIds.GetValue(client, _ => new EntryId(Guid.NewGuid())).Value;
        private Guid GetBanId(BannedPlayer ban) => banIds.GetValue(ban, _ => new EntryId(Guid.NewGuid())).Value;
        private static bool IsOwner(GameServer server, Client client) => client.Connection == server.OwnerConnection;
        private static bool CanBan(Client client) => !client.AccountInfo.IsNone || !client.Connection.Endpoint.Address.IsLocalHost;
        private static bool IsSelectableSubmarine(SubmarineInfo sub, ServerSettings settings)
            => sub.IsPlayer && !sub.HasTag(SubmarineTag.Shuttle) && !settings.HiddenSubs.Contains(sub.Name);
        private static Reply Failure(HttpStatusCode status, string message) => new Reply(status, new Error(message));

        private static bool IsValidText(string text, int maxLength)
        {
            if (text.Length > maxLength) { return false; }
            try
            {
                XmlConvert.VerifyXmlChars(text);
                return true;
            }
            catch (XmlException)
            {
                return false;
            }
        }
    }
}
