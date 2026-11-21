using Barotrauma.Networking;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Barotrauma
{
    internal static class ServerManagementOptions
    {
        public sealed record Choice(string Value, string Label);
        public sealed record Field(string Name, string Label, string Group, string Kind, bool LobbyOnly,
            object Value, double? Min = null, double? Max = null, double? Step = null, Choice[] Choices = null,
            bool CanAdd = true, string Mode = null, bool ReadOnly = false);

        private sealed record Setting(string Name, string Label = null, double? Min = null, double? Max = null);
        private sealed record Group(string Name, bool LobbyOnly, Setting[] Settings, string Mode = null, bool CampaignDefaults = false);
        private sealed record Editor(Field Field, Func<object, Action> Prepare);

        private static readonly Group[] Groups =
        {
            new("Basic server settings", false, new Setting[]
            {
                new(nameof(ServerSettings.IsPublic), "Public server"),
                new(nameof(ServerSettings.Language), "Server language"),
                new(nameof(ServerSettings.RequireAuthentication), "Require player authentication"),
                new(nameof(ServerSettings.BanAfterWrongPassword), "Ban after repeated wrong passwords"),
                new(nameof(ServerSettings.MaxPasswordRetriesBeforeBan), "Password attempts before ban", 1, 100),
                new(nameof(ServerSettings.AllowFileTransfers)),
                new(nameof(ServerSettings.AllowModDownloads)),
                new(nameof(ServerSettings.VoiceChatEnabled), "Voice chat"),
                new(nameof(ServerSettings.AllowAFK), "Allow AFK players"),
                new(nameof(ServerSettings.KickAFKTime), "AFK kick time (seconds)", 0, 86400)
            }),
            new("Voting settings", false, new Setting[]
            {
                new(nameof(ServerSettings.EndVoteRequiredRatio), "End vote ratio (0-1)", 0, 1),
                new(nameof(ServerSettings.VoteRequiredRatio), "Vote ratio (0-1)", 0, 1),
                new(nameof(ServerSettings.KickVoteRequiredRatio), "Kick vote ratio (0-1)", 0, 1),
                new(nameof(ServerSettings.VoteTimeout), "Vote timeout (seconds)", 1, 86400),
                new(nameof(ServerSettings.DisallowKickVoteTime), "Delay before kick voting (seconds)", 0, 86400),
                new(nameof(ServerSettings.AutoBanTime), "Automatic ban duration (seconds)", 0, 86400),
                new(nameof(ServerSettings.MaxAutoBanTime), "Maximum automatic ban (seconds)", 0, 31536000)
            }),
            new("Round setup", true, new Setting[]
            {
                new(nameof(ServerSettings.SubSelectionMode), "Submarine selection mode"),
                new(nameof(ServerSettings.ModeSelectionMode), "Game mode selection"),
                new(nameof(ServerSettings.StartWhenClientsReadyRatio), "Ready player ratio (0-1)", 0, 1),
                new(nameof(ServerSettings.Biome)),
                new(nameof(ServerSettings.SelectedOutpostName), "Outpost"),
                new(nameof(ServerSettings.BotCount), "Bot count", 0, NetConfig.MaxPlayers),
                new(nameof(ServerSettings.MaxBotCount), "Maximum bots", 0, NetConfig.MaxPlayers),
                new(nameof(ServerSettings.BotSpawnMode)),
                new(nameof(ServerSettings.TraitorProbability), "Traitor probability (0-1)", 0, 1),
                new(nameof(ServerSettings.TraitorDangerLevel), null, TraitorEventPrefab.MinDangerLevel, TraitorEventPrefab.MaxDangerLevel),
                new(nameof(ServerSettings.TraitorsMinPlayerCount), "Minimum players for traitors", 1, NetConfig.MaxPlayers),
                new(nameof(ServerSettings.MinPercentageOfPlayersForTraitorAccusation), "Traitor accusation votes (%)", 0, 100)
            }),
            new("Respawn", true, new Setting[]
            {
                new(nameof(ServerSettings.RespawnMode)),
                new(nameof(ServerSettings.UseRespawnShuttle)),
                new(nameof(ServerSettings.SelectedShuttle), "Respawn shuttle"),
                new(nameof(ServerSettings.RespawnInterval), "Respawn interval (seconds)", 10, 600),
                new(nameof(ServerSettings.MinRespawnRatio), "Minimum respawn ratio (0-1)", 0, 1),
                new(nameof(ServerSettings.MaxTransportTime), "Shuttle transport time (seconds, 0 for unlimited)", 0, 660),
                new(nameof(ServerSettings.SkillLossPercentageOnDeath), "Skill loss on death (%)", 0, 100),
                new(nameof(ServerSettings.SkillLossPercentageOnImmediateRespawn), "Immediate respawn skill loss (%)", 0, 100)
            }),
            new("Gameplay settings", false, new Setting[]
            {
                new(nameof(ServerSettings.AllowSpectating)),
                new(nameof(ServerSettings.AllowFriendlyFire)),
                new(nameof(ServerSettings.AllowDragAndDropGive), "Allow drag-and-drop giving"),
                new(nameof(ServerSettings.AllowImmediateItemDelivery)),
                new(nameof(ServerSettings.LockAllDefaultWires)),
                new(nameof(ServerSettings.AllowLinkingWifiToChat), "Allow linking Wi-Fi to chat"),
                new(nameof(ServerSettings.DestructibleOutposts)),
                new(nameof(ServerSettings.KillableNPCs), "Killable NPCs"),
                new(nameof(ServerSettings.LosMode), "Line of sight"),
                new(nameof(ServerSettings.ShowEnemyHealthBars)),
                new(nameof(ServerSettings.DisableBotConversations)),
                new(nameof(ServerSettings.KarmaEnabled), "Enable karma")
            }),
            new("Karma settings", false, new Setting[]
            {
                new(nameof(ServerSettings.KarmaPreset))
            }),
            new("Campaign rules", false, new Setting[]
            {
                new(nameof(ServerSettings.AllowRemoteCampaignInteractions)),
                new(nameof(ServerSettings.LootedMoneyDestination)),
                new(nameof(ServerSettings.MaximumMoneyTransferRequest), null, 0, CampaignMode.MaxMoney),
                new(nameof(ServerSettings.NewCampaignDefaultSalary), "New campaign default salary (%)", 0, 100)
            }, Mode: "multiplayercampaign"),
            new("Campaign crew", true, new Setting[]
            {
                new(nameof(ServerSettings.ReplaceCostPercentage), "Character replacement cost (%)", 0, 100),
                new(nameof(ServerSettings.AllowBotTakeoverOnPermadeath), "Allow bot takeover on permadeath"),
                new(nameof(ServerSettings.IronmanMode))
            }, Mode: "multiplayercampaign"),
            new("Campaign Settings", true, new Setting[]
            {
                new(nameof(CampaignSettings.RadiationEnabled), "Jovian radiation"),
                new(nameof(CampaignSettings.MaxMissionCount), "Maximum missions", CampaignSettings.MinMissionCountLimit, CampaignSettings.MaxMissionCountLimit),
                new(nameof(CampaignSettings.WorldHostility)),
                new(nameof(CampaignSettings.StartItemSet), "Starting items"),
                new(nameof(CampaignSettings.StartingBalanceAmount), "Starting funds"),
                new(nameof(CampaignSettings.CrewVitalityMultiplier)),
                new(nameof(CampaignSettings.NonCrewVitalityMultiplier), "Non-crew vitality multiplier"),
                new(nameof(CampaignSettings.OxygenMultiplier)),
                new(nameof(CampaignSettings.FuelMultiplier)),
                new(nameof(CampaignSettings.MissionRewardMultiplier)),
                new(nameof(CampaignSettings.ExperienceRewardMultiplier)),
                new(nameof(CampaignSettings.ShopPriceMultiplier)),
                new(nameof(CampaignSettings.ShipyardPriceMultiplier)),
                new(nameof(CampaignSettings.RepairFailMultiplier), "Repair failure multiplier"),
                new(nameof(CampaignSettings.ShowHuskWarning)),
                new(nameof(CampaignSettings.PatdownProbability))
            }, Mode: "multiplayercampaign", CampaignDefaults: true),
            new("PvP", true, new Setting[]
            {
                new(nameof(ServerSettings.PvpTeamSelectionMode), "Team selection mode"),
                new(nameof(ServerSettings.PvpAutoBalanceThreshold), "Team auto-balance threshold (0 to disable)", 0, NetConfig.MaxPlayers),
                new(nameof(ServerSettings.PvPStunResist), "Stun resistance (0-1)", 0, 1),
                new(nameof(ServerSettings.PvPSpawnMonsters), "Spawn monsters"),
                new(nameof(ServerSettings.PvPSpawnWrecks), "Spawn wrecks"),
                new(nameof(ServerSettings.TrackOpponentInPvP), "Track opponents"),
                new(nameof(ServerSettings.DisembarkPointAllowance), "Disembark points", 0, 100),
                new(nameof(ServerSettings.WinScorePvP), "Winning score", 1, 10000)
            }, Mode: "pvp"),
            new("Log settings", false, new Setting[]
            {
                new(nameof(ServerSettings.SaveServerLogs)),
                new(nameof(ServerSettings.LinesPerLogFile), null, 1, 1000000)
            }),
            new("Network settings", false, new Setting[]
            {
                new(nameof(ServerSettings.EnableDoSProtection), "Enable DoS protection"),
                new(nameof(ServerSettings.MaxPacketAmount), "Packet limit", ServerSettings.PacketLimitMin, ServerSettings.PacketLimitMax),
                new(nameof(ServerSettings.MaxLagCompensation), "Maximum lag compensation (ms)", 0, 500),
                new(nameof(ServerSettings.KillDisconnectedTime), "Disconnected character lifetime (seconds)", 0, 86400),
                new(nameof(ServerSettings.DespawnDisconnectedPermadeathTime), "Disconnected character lifetime in permadeath (seconds)", 0, 86400),
                new(nameof(ServerSettings.MinimumMidRoundSyncTimeout), "Minimum mid-round sync timeout (seconds)", 1, 86400),
                new(nameof(ServerSettings.RoundStartSyncDuration), "Round start sync duration (seconds)", 1, 86400),
                new(nameof(ServerSettings.EventRemovalTime), "Event removal time (seconds)", 0, 86400),
                new(nameof(ServerSettings.OldReceivedEventKickTime), "Received event kick time (seconds)", 1, 86400),
                new(nameof(ServerSettings.OldEventKickTime), "Missing event kick time (seconds)", 1, 86400),
                new(nameof(ServerSettings.TimeoutThresholdNotInGame), "Lobby connection timeout (seconds)", 1, 86400),
                new(nameof(ServerSettings.TimeoutThresholdInGame), "In-game connection timeout (seconds)", 1, 86400)
            })
        };

        // These are the editable properties and ranges used by the in-game karma settings.
        private const string KarmaPrefix = "karma:";
        private static readonly Setting[] KarmaOptions =
        {
            new(nameof(KarmaManager.ResetKarmaBetweenRounds)),
            new(nameof(KarmaManager.KickBanThreshold), null, 0, 40),
            new(nameof(KarmaManager.KicksBeforeBan), null, 0, 10),
            new(nameof(KarmaManager.HerpesThreshold), null, 0, 50),
            new(nameof(KarmaManager.KarmaDecay), null, 0, 0.5),
            new(nameof(KarmaManager.KarmaDecayThreshold), null, 50, 100),
            new(nameof(KarmaManager.KarmaIncrease), null, 0, 0.5),
            new(nameof(KarmaManager.KarmaIncreaseThreshold), null, 0, 50),
            new(nameof(KarmaManager.StructureRepairKarmaIncrease), null, 0, 1),
            new(nameof(KarmaManager.HealFriendlyKarmaIncrease), null, 0, 1),
            new(nameof(KarmaManager.DamageEnemyKarmaIncrease), null, 0, 1),
            new(nameof(KarmaManager.ItemRepairKarmaIncrease), null, 0, 1),
            new(nameof(KarmaManager.ExtinguishFireKarmaIncrease), null, 0, 10),
            new(nameof(KarmaManager.BallastFloraKarmaIncrease), null, 0, 1),
            new(nameof(KarmaManager.StructureDamageKarmaDecrease), null, 0, 1),
            new(nameof(KarmaManager.DamageFriendlyKarmaDecrease), null, 0, 1),
            new(nameof(KarmaManager.StunFriendlyKarmaDecrease), null, 0, 1),
            new(nameof(KarmaManager.StunFriendlyKarmaDecreaseThreshold), null, 0, 10),
            new(nameof(KarmaManager.ReactorMeltdownKarmaDecrease), null, 0, 100),
            new(nameof(KarmaManager.ReactorOverheatKarmaDecrease), null, 0, 10),
            new(nameof(KarmaManager.AllowedWireDisconnectionsPerMinute), null, 0, 20),
            new(nameof(KarmaManager.WireDisconnectionKarmaDecrease), null, 0, 20),
            new(nameof(KarmaManager.SpamFilterKarmaDecrease), null, 0, 30),
            new(nameof(KarmaManager.DangerousItemStealKarmaDecrease), null, 0, 30),
            new(nameof(KarmaManager.DangerousItemStealBots)),
            new(nameof(KarmaManager.DangerousItemContainKarmaDecrease), null, 0, 30),
            new(nameof(KarmaManager.IsDangerousItemContainKarmaDecreaseIncremental)),
            new(nameof(KarmaManager.MaxDangerousItemContainKarmaDecrease), null, 0, 100)
        };

        public static Field[] Read(GameServer server, NetLobbyScreen lobby) =>
            GetEditors(server, lobby, server.ServerSettings.CampaignSettings).Select(editor => editor.Field).ToArray();

        private static IEnumerable<Editor> GetEditors(GameServer server, NetLobbyScreen lobby, CampaignSettings campaignDefaults)
        {
            var settings = server.ServerSettings;
            var shuttles = lobby.GetSubList().Where(sub => sub.HasTag(SubmarineTag.Shuttle))
                .Append(lobby.SelectedShuttle).Where(sub => sub != null).DistinctBy(sub => sub.Name).ToArray();

            foreach (var group in Groups)
            {
                ISerializableEntity target = group.CampaignDefaults ? campaignDefaults : settings;
                foreach (var spec in group.Settings)
                {
                    var property = target.SerializableProperties[spec.Name.ToIdentifier()];
                    var type = property.PropertyType;
                    string label = spec.Label ?? Words(spec.Name);
                    Choice[] choices = GetChoices(server, shuttles, spec.Name, type);
                    object value = property.GetValue(target);
                    if (spec.Name == nameof(ServerSettings.SelectedShuttle)) { value = lobby.SelectedShuttle?.Name ?? ""; }
                    string kind = choices != null ? "select" : type == typeof(bool) ? "switch" : "number";
                    string key = group.CampaignDefaults ? "campaign:" + spec.Name : spec.Name;
                    double? min = spec.Min;
                    double? max = spec.Max;
                    if (group.CampaignDefaults && type == typeof(float))
                    {
                        var range = CampaignSettings.GetMultiplierSettings(spec.Name);
                        min = Math.Round(range.Min, 2);
                        max = Math.Round(range.Max, 2);
                    }
                    var field = new Field(key, label, group.Name, kind, group.LobbyOnly,
                        choices != null ? value?.ToString() ?? "" : value, min, max,
                        type == typeof(int) ? 1d : null, choices, Mode: group.Mode);
                    yield return new Editor(field, next => () =>
                    {
                        if (spec.Name == nameof(ServerSettings.SelectedShuttle))
                        {
                            lobby.SelectedShuttle = shuttles.First(sub => sub.Name == (string)next);
                        }
                        property.SetValue(target, type.IsEnum ? Enum.Parse(type, (string)next)
                            : type == typeof(Identifier) ? ((string)next).ToIdentifier()
                            : type == typeof(LanguageIdentifier) ? ((string)next).ToLanguageIdentifier()
                            : next);
                    });
                }
            }

            var karma = server.KarmaManager;
            bool customKarma = settings.KarmaPreset == "custom" && karma.Presets.ContainsKey("custom");
            foreach (var spec in KarmaOptions)
            {
                var property = karma.SerializableProperties[spec.Name.ToIdentifier()];
                var type = property.PropertyType;
                var label = TextManager.Get("Karma." + spec.Name).Fallback(Words(spec.Name)).Value;
                var field = new Field(KarmaPrefix + spec.Name, label, "Karma settings", type == typeof(bool) ? "switch" : "number", false,
                    property.GetValue(karma), spec.Min, spec.Max, type == typeof(int) ? 1d : null, ReadOnly: !customKarma);
                yield return new Editor(field, next => () => property.SetValue(karma, next));
            }

            foreach (var monster in settings.MonsterEnabled.OrderBy(entry => entry.Key.Value))
            {
                var field = new Field("monster:" + monster.Key.Value, TextManager.Get($"Character.{monster.Key}").Fallback(monster.Key.Value).Value,
                    "Monster spawns", "switch", true, monster.Value);
                yield return new Editor(field, next => () => settings.MonsterEnabled[monster.Key] = (bool)next);
            }
            foreach (var item in ItemPrefab.Prefabs.Where(item => (item.AllowAsExtraCargo ?? item.CanBeBought) || settings.ExtraCargo.ContainsKey(item))
                .OrderBy(item => item.Name.Value))
            {
                var field = new Field("cargo:" + item.Identifier.Value, item.Name.Value, "Extra cargo", "number", true,
                    settings.ExtraCargo.GetValueOrDefault(item), 0, ServerSettings.MaxExtraCargoItemsOfType, 1,
                    CanAdd: item.AllowAsExtraCargo ?? item.CanBeBought);
                yield return new Editor(field, next => () =>
                {
                    int amount = (int)next;
                    if (amount == 0) { settings.ExtraCargo.Remove(item); }
                    else { settings.ExtraCargo[item] = amount; }
                });
            }
            foreach (var sub in PlayerSubmarines(lobby))
            {
                var field = new Field("hidden:" + sub.Name, sub.Name, "Hidden submarines", "switch", true, settings.HiddenSubs.Contains(sub.Name));
                yield return new Editor(field, next => () =>
                {
                    if ((bool)next) { settings.HiddenSubs.Add(sub.Name); }
                    else { settings.HiddenSubs.Remove(sub.Name); }
                });
            }
        }

        public static string Apply(GameServer server, NetLobbyScreen lobby, Dictionary<string, JsonElement> changes, bool inLobby)
        {
            var settings = server.ServerSettings;
            bool campaignMode = lobby.SelectedMode == GameModePreset.MultiPlayerCampaign;
            bool campaignLoaded = campaignMode && GameMain.GameSession?.GameMode is MultiPlayerCampaign { Map: not null };
            if (changes.ContainsKey(nameof(ServerSettings.KarmaPreset)) && changes.Keys.Any(name => name.StartsWith(KarmaPrefix, StringComparison.Ordinal)))
            {
                return "Select a karma preset before editing its settings.";
            }
            var campaignDefaults = changes.Keys.Any(name => name.StartsWith("campaign:", StringComparison.Ordinal))
                ? new CampaignSettings(settings.CampaignSettings.Save())
                : settings.CampaignSettings;
            var editors = GetEditors(server, lobby, campaignDefaults).ToDictionary(editor => editor.Field.Name, StringComparer.Ordinal);
            var actions = new List<Action>();
            int cargoTypes = settings.ExtraCargo.Count;
            int visibleSubs = PlayerSubmarines(lobby).Count(sub => !settings.HiddenSubs.Contains(sub.Name));
            bool hiddenChanged = false;
            foreach (var (name, input) in changes)
            {
                if (!editors.TryGetValue(name, out var editor)) { return "One or more settings are unavailable."; }
                var field = editor.Field;
                if (field.LobbyOnly && !inLobby) { return field.Label + " can only be changed in the lobby."; }
                if (field.Mode != null && field.Mode != lobby.SelectedModeIdentifier.Value)
                {
                    return field.Label + " is unavailable in the selected game mode.";
                }
                if ((campaignMode && name is nameof(ServerSettings.Biome) or nameof(ServerSettings.SelectedOutpostName)) ||
                    (campaignLoaded && name is nameof(ServerSettings.BotCount) or nameof(ServerSettings.MaxBotCount) or nameof(ServerSettings.BotSpawnMode)))
                {
                    return field.Label + " is unavailable in Campaign mode.";
                }
                if (field.ReadOnly) { return "Select the Custom karma preset to edit its settings."; }
                object value = Parse(field, input);
                if (value == null) { return "Invalid value for " + field.Label + "."; }
                if (Equals(field.Value, value)) { continue; }
                if (!campaignMode && name == nameof(ServerSettings.RespawnMode) && Equals(value, nameof(RespawnMode.Permadeath)))
                {
                    return "Permadeath is available in Campaign mode.";
                }
                if (name.StartsWith("cargo:", StringComparison.Ordinal))
                {
                    cargoTypes += ((int)value != 0 ? 1 : 0) - ((int)field.Value != 0 ? 1 : 0);
                }
                if (name.StartsWith("hidden:", StringComparison.Ordinal))
                {
                    visibleSubs += (bool)value ? -1 : 1;
                    hiddenChanged = true;
                }
                actions.Add(editor.Prepare(value));
            }
            if (cargoTypes > ServerSettings.MaxExtraCargoItemTypes) { return $"Extra cargo permits up to {ServerSettings.MaxExtraCargoItemTypes} item types."; }
            if (hiddenChanged && visibleSubs == 0) { return "Keep at least one player submarine available."; }
            foreach (var apply in actions) { apply(); }
            settings.CampaignSettings = campaignDefaults;
            if (hiddenChanged)
            {
                if (lobby.SelectedSub == null || settings.HiddenSubs.Contains(lobby.SelectedSub.Name))
                {
                    lobby.SelectedSub = PlayerSubmarines(lobby).First(sub => !settings.HiddenSubs.Contains(sub.Name));
                    settings.SelectedSubmarine = lobby.SelectedSub.Name;
                }
                settings.UpdateFlag(ServerSettings.NetFlags.HiddenSubs);
            }
            settings.ServerDetailsChanged = true;
            server.RefreshPvpTeamAssignments();
            return null;
        }

        private static object Parse(Field field, JsonElement input)
        {
            if (field.Kind == "switch")
            {
                return input.ValueKind is JsonValueKind.True or JsonValueKind.False ? input.GetBoolean() : null;
            }
            if (field.Kind == "select")
            {
                string value = input.ValueKind == JsonValueKind.String ? input.GetString() : null;
                return field.Choices.Any(choice => choice.Value == value) ? value : null;
            }
            if (input.ValueKind != JsonValueKind.Number || !input.TryGetDouble(out double number) ||
                !double.IsFinite(number) || number < field.Min || number > field.Max)
            {
                return null;
            }
            if (field.Step == 1)
            {
                return input.TryGetInt32(out int integer) ? integer : null;
            }
            return (float)number;
        }

        private static IEnumerable<SubmarineInfo> PlayerSubmarines(NetLobbyScreen lobby) =>
            lobby.GetSubList().Where(sub => sub.IsPlayer && !sub.HasTag(SubmarineTag.Shuttle)).DistinctBy(sub => sub.Name)
                .OrderBy(sub => sub.Name, StringComparer.OrdinalIgnoreCase);

        private static Choice[] GetChoices(GameServer server, SubmarineInfo[] shuttles, string name, Type type)
        {
            return name switch
            {
                nameof(ServerSettings.Language) => TextManager.AvailableLanguages.Select(language => new Choice(language.ToString(), language.ToString()))
                    .OrderBy(language => language.Label).Prepend(new Choice("", "Any language")).ToArray(),
                nameof(ServerSettings.Biome) => Biome.Prefabs.Where(biome => !biome.IsEndBiome)
                    .Select(biome => new Choice(biome.Identifier.Value, biome.DisplayName.Value)).Prepend(new Choice("Random", "Random")).ToArray(),
                nameof(ServerSettings.SelectedOutpostName) => SubmarineInfo.SavedSubmarines.Where(sub => sub.IsOutpost)
                    .Select(sub => new Choice(sub.Name, sub.DisplayName.Value)).DistinctBy(choice => choice.Value)
                    .Prepend(new Choice("Random", "Random")).ToArray(),
                nameof(ServerSettings.SelectedShuttle) => shuttles.Select(sub => new Choice(sub.Name, sub.DisplayName.Value)).ToArray(),
                nameof(ServerSettings.KarmaPreset) => server.KarmaManager.Presets.Keys.Select(key => new Choice(key, Words(key))).ToArray(),
                nameof(CampaignSettings.StartItemSet) => StartItemSet.Sets.OrderBy(set => set.Order)
                    .Select(set => new Choice(set.Identifier.Value, TextManager.Get($"startitemset.{set.Identifier}").Fallback(set.Identifier.Value).Value)).ToArray(),
                _ when type.IsEnum => Enum.GetNames(type).Select(value => new Choice(value, Words(value))).ToArray(),
                _ => null
            };
        }

        private static string Words(string name) => Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2");
    }
}
