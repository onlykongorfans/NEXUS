namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests.Communication;

/// <summary>
///     Tests the game client's fire-and-forget channel leave requests.
/// </summary>
public sealed class ChatChannelLeaveTests
{
    [Test]
    public async Task Leaving_A_Channel_The_Session_Has_Not_Joined_Is_A_No_Op()
    {
        ClientChatSession session = CreateSession("PreliminaryLeaver");
        ChatBuffer request = BuildLeaveRequest("aushon");

        LeaveChannel commandProcessor = new ();

        commandProcessor.Process(session, request);

        await Assert.That(session.CurrentChannels).IsEmpty();
    }

    [Test]
    public async Task Leaving_An_Existing_Channel_The_Session_Has_Not_Joined_Does_Not_Affect_Its_Members()
    {
        ClientChatSession existingMemberSession = CreateSession("ExistingMember");
        ChatChannel channel = ChatChannel.GetOrCreate(existingMemberSession, "Existing Leave Channel");
        ChatChannelMember existingMember = new (existingMemberSession, channel);

        channel.Members.TryAdd(existingMemberSession.Account.Name, existingMember);
        existingMemberSession.CurrentChannels.Add(channel.ID);

        ClientChatSession leavingSession = CreateSession("NonMemberLeaver");
        ChatBuffer request = BuildLeaveRequest(channel.Name);

        LeaveChannel commandProcessor = new ();

        commandProcessor.Process(leavingSession, request);

        using (Assert.Multiple())
        {
            await Assert.That(channel.Members.ContainsKey(existingMemberSession.Account.Name)).IsTrue();
            await Assert.That(existingMemberSession.CurrentChannels).Contains(channel.ID);
            await Assert.That(leavingSession.CurrentChannels).IsEmpty();
        }
    }

    [Test]
    public async Task Leaving_A_Joined_Channel_Removes_The_Session_From_It()
    {
        ClientChatSession session = CreateSession("JoinedLeaver");
        ChatChannel channel = ChatChannel.GetOrCreate(session, "Joined Leave Channel");
        ChatChannelMember member = new (session, channel);

        channel.Members.TryAdd(session.Account.Name, member);
        session.CurrentChannels.Add(channel.ID);

        ChatBuffer request = BuildLeaveRequest(channel.Name);

        LeaveChannel commandProcessor = new ();

        commandProcessor.Process(session, request);

        using (Assert.Multiple())
        {
            await Assert.That(channel.Members.ContainsKey(session.Account.Name)).IsFalse();
            await Assert.That(session.CurrentChannels).DoesNotContain(channel.ID);
        }
    }

    private static ChatBuffer BuildLeaveRequest(string channelName)
    {
        ChatBuffer request = new ();

        request.WriteCommand(ChatProtocol.Command.CHAT_CMD_LEAVE_CHANNEL);
        request.WriteString(channelName);

        return request;
    }

    private static ClientChatSession CreateSession(string accountName)
    {
        ClientChatSession session = (ClientChatSession) RuntimeHelpers.GetUninitializedObject(typeof(ClientChatSession));

        session.Account = CreateAccount(accountName);
        session.CurrentChannels = [];

        return session;
    }

    private static Account CreateAccount(string accountName)
    {
        Role role = new () { Name = "Player" };

        User user = new ()
        {
            EmailAddress    = $"{accountName}@test.local",
            Role            = role,
            SRPPasswordSalt = string.Empty,
            SRPPasswordHash = string.Empty
        };

        return new Account
        {
            ID     = Math.Abs(Guid.NewGuid().GetHashCode()),
            Name   = accountName,
            User   = user,
            IsMain = true,
            Type   = AccountType.Normal
        };
    }
}
