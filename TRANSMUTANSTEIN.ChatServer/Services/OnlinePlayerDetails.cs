namespace TRANSMUTANSTEIN.ChatServer.Services;

internal static class OnlinePlayerDetails
{
    public static OnlinePlayerDetailsSnapshot Capture(IReadOnlyList<MatchServer> servers, DateTimeOffset now)
    {
        MatchmakingMapConfiguration[] maps = JSONConfiguration.MatchmakingConfiguration.Maps;
        Dictionary<ChatProtocol.TMMGameType, string> mapsByGameType = maps
            .SelectMany(map => map.GameTypes.Select(gameType => (GameType: (ChatProtocol.TMMGameType) gameType, map.Map)))
            .ToDictionary(entry => entry.GameType, entry => entry.Map);

        string[] configuredQueueRegions = [.. maps.SelectMany(map => map.Regions).Select(GameRegions.GetAggregate).Distinct(StringComparer.OrdinalIgnoreCase)];
        Dictionary<string, HashSet<int>> queuedByRegion = configuredQueueRegions.ToDictionary(region => region, _ => new HashSet<int>(), StringComparer.OrdinalIgnoreCase);
        Dictionary<(string Region, ChatProtocol.TMMGameType GameType), HashSet<int>> queuedByType = [];
        HashSet<int> queuedPlayers = [];

        foreach (MatchmakingQueuedGroupSnapshot group in MatchmakingQueueDiagnostics.Capture().Groups)
        {
            // Co-Op Bot Matches Bypass The Player Matchmaking Queue.
            if (group.GroupType == nameof(ChatProtocol.TMMType.TMM_TYPE_COOP)
                || Enum.TryParse(group.GameType, out ChatProtocol.TMMGameType gameType) is false
                || mapsByGameType.ContainsKey(gameType) is false)
                continue;

            int[] playerIDs = [.. group.Players.Select(player => player.AccountID)];
            queuedPlayers.UnionWith(playerIDs);

            // NEWERTH Overrides The Other Preferences And Is Displayed As Any Region.
            IEnumerable<string> regions = group.AggregateRegions.Contains(GameRegions.Wildcard, StringComparer.OrdinalIgnoreCase)
                ? [GameRegions.Wildcard]
                : group.AggregateRegions;

            foreach (string region in regions)
            {
                if (queuedByRegion.TryGetValue(region, out HashSet<int>? regionalPlayers) is false)
                    continue;

                regionalPlayers.UnionWith(playerIDs);
                (string, ChatProtocol.TMMGameType) key = (region.ToUpperInvariant(), gameType);

                if (queuedByType.TryGetValue(key, out HashSet<int>? gameTypePlayers) is false)
                    queuedByType[key] = gameTypePlayers = [];

                gameTypePlayers.UnionWith(playerIDs);
            }
        }

        OnlineQueueRegionCount[] queueRows = [.. queuedByRegion.OrderBy(region => region.Key, StringComparer.Ordinal).Select(region =>
            new OnlineQueueRegionCount(region.Key, region.Value.Count,
                [.. mapsByGameType.OrderBy(entry => entry.Key).Select(entry =>
                    new OnlineQueueGameTypeCount(entry.Value, (int) entry.Key, queuedByType.GetValueOrDefault((region.Key.ToUpperInvariant(), entry.Key))?.Count ?? 0))]))];

        HashSet<int> assignedServerIDs = [.. MatchmakingService.ActiveMatches.Values.Select(match => match.AssignedServerID).OfType<int>()];
        HashSet<int> activeMatchIDs = [];
        Dictionary<string, OnlineServerLocationCount> locations = new (StringComparer.Ordinal);

        foreach (MatchServer server in servers.OrderBy(server => server.ID))
        {
            string location = server.Location.Trim().ToUpperInvariant();

            if (location.Length is 0 or > 16 || location.Any(character => char.IsAsciiLetterOrDigit(character) is false))
                location = "UNKNOWN";

            OnlineServerLocationCount counts = locations.GetValueOrDefault(location) ?? new OnlineServerLocationCount(location, 0, 0);

            if (server.IsRetired is false
                && Context.MatchServerChatSessions.TryGetValue(server.ID, out MatchServerChatSession? session)
                && session.Metadata is { } metadata
                && MatchmakingService.HasFreshServerStatus(metadata, now))
            {
                if (MatchmakingService.IsUnassignedIdleServer(server, assignedServerIDs))
                    counts = counts with { ServersAvailable = counts.ServersAvailable + 1 };

                // The Same Timed Phases Are Displayed As In Progress By "RequestGameInfo".
                if (metadata.Status is ChatProtocol.ServerStatus.SERVER_STATUS_ACTIVE
                    && metadata.GamePhase is >= 4 and <= 6 && metadata.MatchID > 0 && activeMatchIDs.Add(metadata.MatchID))
                    counts = counts with { GamesInProgress = counts.GamesInProgress + 1 };
            }

            locations[location] = counts;
        }

        return new OnlinePlayerDetailsSnapshot(OnlinePlayerDetailsSnapshot.CurrentVersion, now.ToUnixTimeSeconds(),
            activeMatchIDs.Count, queuedPlayers.Count, [.. locations.Values.OrderBy(counts => counts.Location, StringComparer.Ordinal)], queueRows);
    }
}
