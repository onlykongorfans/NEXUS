namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Communication;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_UPLOAD_REQUEST)]
public sealed class ReplayRequest(MerrickContext databaseContext, IDatabase distributedCacheStore, ReplayStorageService replayStorage, FloodPreventionService floodPrevention) : IAsynchronousCommandProcessor<ClientChatSession>
{
    public async Task Process(ClientChatSession session, ChatBuffer buffer)
    {
        if (session.Account is null || session.Metadata is null)
            return;

        int matchID;

        try { matchID = ReplayPackets.ReadRequest(buffer); }
        catch (InvalidDataException)
        {
            Log.Warning("Rejected Malformed Replay Request From Account {AccountID}", session.Account.ID);
            return;
        }

        if (floodPrevention.CheckAndHandleFloodPrevention(session) is false)
            return;

        if (replayStorage.Settings.Enabled is false)
        {
            session.Send(ReplayPackets.CreateClientStatus(matchID, ChatProtocol.UploadUpdateType.EUUT_GENERAL_FAILURE));
            return;
        }

        MatchStatistics? match = await databaseContext.MatchStatistics.SingleOrDefaultAsync(match => match.MatchID == matchID);

        if (match is null)
        {
            session.Send(ReplayPackets.CreateClientStatus(matchID, ChatProtocol.UploadUpdateType.EUUT_FILE_DOES_NOT_EXIST));
            return;
        }

        int? storedSize = await replayStorage.GetAvailableSize(matchID);

        if (storedSize.HasValue)
        {
            if (match.FileSize != storedSize.Value)
            {
                match.FileSize = storedSize.Value;
                await databaseContext.SaveChangesAsync();
            }

            session.Send(ReplayPackets.CreateClientStatus(matchID, ChatProtocol.UploadUpdateType.EUUT_FILE_ALREADY_UPLOADED, replayStorage.Settings.GetDownloadURL(matchID)));
            return;
        }

        int managerID = match.HostAccountName.GetDeterministicInt32Hash();

        if (Context.MatchServerManagerChatSessions.TryGetValue(managerID, out MatchServerManagerChatSession? manager) is false || manager.Account.Name != match.HostAccountName)
        {
            session.Send(ReplayPackets.CreateClientStatus(matchID, ChatProtocol.UploadUpdateType.EUUT_FILE_INVALID_HOST));
            return;
        }

        string pendingKey = ReplayStorageService.PendingRequestKey(managerID, matchID, session.Account.ID);
        string watchersKey = ReplayStorageService.WatchersKey(managerID, matchID);

        if (await distributedCacheStore.StringSetAsync(pendingKey, "pending", TimeSpan.FromMinutes(5), When.NotExists) is false)
        {
            session.Send(ReplayPackets.CreateClientStatus(matchID, ChatProtocol.UploadUpdateType.EUUT_FILE_QUEUED));
            return;
        }

        await distributedCacheStore.SetAddAsync(watchersKey, session.Account.ID);
        await distributedCacheStore.KeyExpireAsync(watchersKey, TimeSpan.FromMinutes(5));

        MatchServerManager? registration = await distributedCacheStore.GetMatchServerManagerBySessionCookie(manager.Metadata.SessionCookie);

        if (registration is not null)
            await replayStorage.CreateAuthorisation(matchID, match.HostAccountName, registration.IPAddress);

        if (registration is null || manager.Send(ReplayPackets.CreateManagerRequest(session.Account.ID, matchID, replayStorage.Settings)) is false)
        {
            await distributedCacheStore.KeyDeleteAsync(ReplayStorageService.AuthorisationKey(managerID, matchID));
            await distributedCacheStore.KeyDeleteAsync(pendingKey);
            await distributedCacheStore.SetRemoveAsync(watchersKey, session.Account.ID);
            session.Send(ReplayPackets.CreateClientStatus(matchID, ChatProtocol.UploadUpdateType.EUUT_FILE_INVALID_HOST));
        }
        else
        {
            session.Send(ReplayPackets.CreateClientStatus(matchID, ChatProtocol.UploadUpdateType.EUUT_FILE_QUEUED));
            Log.Information("Requested Replay For Match {MatchID} From Manager {ManagerID}, Account {AccountID}", matchID, managerID, session.Account.ID);
        }
    }
}
