namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Connection;

[ChatCommand(ChatProtocol.ServerManagerToChatServer.NET_CHAT_SM_UPLOAD_UPDATE)]
public sealed class ReplayUploadUpdate(MerrickContext databaseContext, IDatabase distributedCacheStore, ReplayStorageService replayStorage) : IAsynchronousCommandProcessor<MatchServerManagerChatSession>
{
    public async Task Process(MatchServerManagerChatSession session, ChatBuffer buffer)
    {
        if (session.Account is null || session.Metadata is null || replayStorage.Settings.Enabled is false)
            return;

        int matchID;
        int accountID;
        ChatProtocol.UploadUpdateType status;

        try { (matchID, accountID, status) = ReplayPackets.ReadUpdate(buffer); }
        catch (InvalidDataException)
        {
            Log.Warning("Rejected Malformed Replay Update From Manager {ManagerID}", session.Metadata.ServerManagerID);
            return;
        }

        string pendingKey = ReplayStorageService.PendingRequestKey(session.Metadata.ServerManagerID, matchID, accountID);

        if (await distributedCacheStore.KeyExistsAsync(pendingKey) is false)
            return;

        MatchStatistics? match = await databaseContext.MatchStatistics.SingleOrDefaultAsync(match => match.MatchID == matchID);

        if (match is null || match.HostAccountName != session.Account.Name)
            return;

        if (status is ChatProtocol.UploadUpdateType.EUUT_FILE_ALREADY_UPLOADED or ChatProtocol.UploadUpdateType.EUUT_FILE_UPLOAD_COMPLETE)
        {
            int? storedSize = await replayStorage.GetAvailableSize(matchID, refresh: true);

            if (storedSize is null)
                status = ChatProtocol.UploadUpdateType.EUUT_GENERAL_FAILURE;
            else if (match.FileSize != storedSize.Value)
            {
                match.FileSize = storedSize.Value;
                await databaseContext.SaveChangesAsync();
            }
        }

        // A Manager Uploads One File For All Interested Clients; Its Already-Queued Reply Must Not Strand Later Requesters.
        if (status is ChatProtocol.UploadUpdateType.EUUT_FILE_ALREADY_QUEUED)
            status = ChatProtocol.UploadUpdateType.EUUT_FILE_QUEUED;

        string watchersKey = ReplayStorageService.WatchersKey(session.Metadata.ServerManagerID, matchID);
        RedisValue[] watchers = await distributedCacheStore.SetMembersAsync(watchersKey);
        bool finished = status is not (ChatProtocol.UploadUpdateType.EUUT_FILE_QUEUED or ChatProtocol.UploadUpdateType.EUUT_FILE_UPLOADING);

        foreach (RedisValue watcher in watchers)
        {
            if (int.TryParse(watcher.ToString(), out int watcherAccountID) is false)
                continue;

            string watcherPendingKey = ReplayStorageService.PendingRequestKey(session.Metadata.ServerManagerID, matchID, watcherAccountID);

            if (await distributedCacheStore.KeyExistsAsync(watcherPendingKey) is false)
                continue;

            ClientChatSession? client = Context.ClientChatSessions.Values.SingleOrDefault(client => client.Account.ID == watcherAccountID);
            client?.Send(ReplayPackets.CreateClientStatus(matchID, status, replayStorage.Settings.GetDownloadURL(matchID)));

            if (finished)
                await distributedCacheStore.KeyDeleteAsync(watcherPendingKey);
            else
                await distributedCacheStore.KeyExpireAsync(watcherPendingKey, TimeSpan.FromMinutes(5));
        }

        if (finished)
        {
            await distributedCacheStore.KeyDeleteAsync(watchersKey);
            await distributedCacheStore.KeyDeleteAsync(ReplayStorageService.AuthorisationKey(session.Metadata.ServerManagerID, matchID));
        }
        else
            await distributedCacheStore.KeyExpireAsync(watchersKey, TimeSpan.FromMinutes(5));

        Log.Information("Replay Status For Match {MatchID}, Account {AccountID}: {ReplayStatus}", matchID, accountID, status);
    }
}
