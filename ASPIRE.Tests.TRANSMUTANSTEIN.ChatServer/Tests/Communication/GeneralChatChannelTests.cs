namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests.Communication;

/// <summary>
///     Covers the load balancing across the general chat channels in <see cref="ChatChannel.GetOrCreateGeneralChannel"/> and their compaction in <see cref="ChatChannel.CompactGeneralChannels"/>.
///     The general channels live in the process-wide channel registry, so these tests run in isolation from every other test and remove the general channels before and after each test.
/// </summary>
[NotInParallel]
public sealed class GeneralChatChannelTests
{
    // Stand-In Members Are Constructed Against The Services Of A Minimal Host, Which Supply The Host Environment That A Chat Session Requires
    private WebApplication StandInHost { get; } = WebApplication.CreateSlimBuilder().Build();

    private const string StandInMemberNamePrefix = "Stand-In Member";

    private int StandInMemberCount { get; set; }

    [Before(HookType.Test)]
    public Task Before_Each_Test()
    {
        // The Chat Server's Static Logger Facade Throws Until Initialised; A Sink-Less Serilog Logger Satisfies It Without Producing Output
        Log.Initialise(new Serilog.LoggerConfiguration().CreateLogger());

        RemoveGeneralChannels();

        return Task.CompletedTask;
    }

    [After(HookType.Test)]
    public async Task After_Each_Test()
    {
        RemoveGeneralChannels();

        await StandInHost.DisposeAsync();
    }

    [Test]
    public async Task The_First_General_Channel_Is_Unnumbered_Until_It_Overflows_And_Is_Then_Renamed_For_Its_Members()
    {
        ChatChannel firstChannel = ChatChannel.GetOrCreateGeneralChannel();

        await Assert.That(firstChannel.Name).IsEqualTo(ChatProtocol.CHAT_CHANNEL_BASE_NAME);

        await using RunningClientSession client = RunningClientSession.Start(CreateAccount("RenamedMember"));

        firstChannel.Members.TryAdd(client.Session.Account.Name, new ChatChannelMember(client.Session, firstChannel));
        client.Session.CurrentChannels.Add(firstChannel.ID);

        Fill(firstChannel);

        ChatChannel overflowChannel = ChatChannel.GetOrCreateGeneralChannel();

        using (Assert.Multiple())
        {
            await Assert.That(firstChannel.Name).IsEqualTo($"{ChatProtocol.CHAT_CHANNEL_BASE_NAME} 1");
            await Assert.That(firstChannel.IsPermanent).IsTrue();
            await Assert.That(Context.ChatChannels.ContainsKey(ChatProtocol.CHAT_CHANNEL_BASE_NAME)).IsFalse();
            await Assert.That(Context.ChatChannels[firstChannel.Name]).IsSameReferenceAs(firstChannel);
            await Assert.That(overflowChannel.Name).IsEqualTo($"{ChatProtocol.CHAT_CHANNEL_BASE_NAME} 2");
            await Assert.That(overflowChannel.IsGeneralChannel).IsTrue();
            await Assert.That(overflowChannel.IsPermanent).IsFalse();
            await Assert.That(client.Session.CurrentChannels).Contains(firstChannel.ID);
            await Assert.That(client.Session.CurrentChannels.Count).IsEqualTo(1);
        }

        // The Game Client Cannot Rename An Open Channel, So The Member Leaves The Channel Under Its Old Name And Joins It Under Its New One
        using (Assert.Multiple())
        {
            await Assert.That(await client.ReadCommand()).IsEqualTo((ushort) ChatProtocol.Command.CHAT_CMD_LEFT_CHANNEL);
            await Assert.That(await client.ReadCommand()).IsEqualTo((ushort) ChatProtocol.Command.CHAT_CMD_CHANGED_CHANNEL);
            await Assert.That(await ReadNoticeMessage(client)).IsEqualTo("A Forgetfulness Spell Was Cast, And The Old Scrolls Scattered To The Winds");
        }
    }

    [Test]
    public async Task The_First_General_Channel_With_Capacity_Is_Chosen_When_Several_Have_Capacity()
    {
        ChatChannel firstChannel = ChatChannel.GetOrCreateGeneralChannel();

        Fill(firstChannel);

        ChatChannel overflowChannel = ChatChannel.GetOrCreateGeneralChannel();

        // A Member Leaving The First Channel Gives Both General Channels Capacity
        RemoveMembers(firstChannel, 1);

        ChatChannel chosenChannel = ChatChannel.GetOrCreateGeneralChannel();

        using (Assert.Multiple())
        {
            await Assert.That(overflowChannel.Members.IsEmpty).IsTrue();
            await Assert.That(chosenChannel).IsSameReferenceAs(firstChannel);
        }
    }

    [Test]
    public async Task An_Overflow_General_Channel_Takes_The_Lowest_Free_Number()
    {
        Fill(ChatChannel.GetOrCreateGeneralChannel());

        ChatChannel secondChannel = ChatChannel.GetOrCreateGeneralChannel();

        Fill(secondChannel);

        Fill(ChatChannel.GetOrCreateGeneralChannel());

        // An Empty Overflow Channel Is Removed, Leaving A Gap In The Numbering
        Context.ChatChannels.TryRemove(secondChannel.Name, out _);

        ChatChannel overflowChannel = ChatChannel.GetOrCreateGeneralChannel();

        using (Assert.Multiple())
        {
            await Assert.That(overflowChannel.Name).IsEqualTo($"{ChatProtocol.CHAT_CHANNEL_BASE_NAME} 2");
            await Assert.That(overflowChannel.Members.IsEmpty).IsTrue();
        }
    }

    [Test]
    public async Task Compaction_Moves_The_Members_Of_Unneeded_General_Channels_Into_The_Lowest_Numbered_Ones_And_Removes_The_Emptied_Channels()
    {
        int memberCap = (int) ChatProtocol.MAX_USERS_PER_CHANNEL;

        // Every General Channel Has To Fill Up Before The Next One Is Created
        ChatChannel firstChannel = ChatChannel.GetOrCreateGeneralChannel();

        AddMembers(firstChannel, memberCap);

        ChatChannel secondChannel = ChatChannel.GetOrCreateGeneralChannel();

        AddMembers(secondChannel, memberCap);

        ChatChannel thirdChannel = ChatChannel.GetOrCreateGeneralChannel();

        AddMembers(thirdChannel, 30);

        // Members Leaving The First Two Channels Leaves Enough Room In Them For Every Member Of The Third One
        RemoveMembers(firstChannel, 20);
        RemoveMembers(secondChannel, memberCap - 10);

        await using RunningClientSession movedClient = RunningClientSession.Start(CreateAccount("CompactedMember"));

        thirdChannel.Members.TryAdd(movedClient.Session.Account.Name, new ChatChannelMember(movedClient.Session, thirdChannel));
        movedClient.Session.CurrentChannels.Add(thirdChannel.ID);

        ChatChannel.CompactGeneralChannels();

        // The First Channel Takes 20 Of The 31 Members Of The Third Channel, And The Second Channel Takes The Remaining 11
        using (Assert.Multiple())
        {
            await Assert.That(firstChannel.Members.Count).IsEqualTo(memberCap);
            await Assert.That(secondChannel.Members.Count).IsEqualTo(10 + 11);
            await Assert.That(Context.ChatChannels.ContainsKey(thirdChannel.Name)).IsFalse();
            await Assert.That(movedClient.Session.CurrentChannels).DoesNotContain(thirdChannel.ID);
            await Assert.That(movedClient.Session.CurrentChannels.Count).IsEqualTo(1);
            await Assert.That(await ReadNoticeMessage(movedClient)).StartsWith($"Moved Here From {thirdChannel.Name}");
        }
    }

    [Test]
    public async Task Compaction_Moves_Nobody_When_It_Would_Not_Remove_A_General_Channel()
    {
        int memberCap = (int) ChatProtocol.MAX_USERS_PER_CHANNEL;

        ChatChannel firstChannel = ChatChannel.GetOrCreateGeneralChannel();

        AddMembers(firstChannel, memberCap);

        ChatChannel secondChannel = ChatChannel.GetOrCreateGeneralChannel();

        AddMembers(secondChannel, 20);

        // The Members Still Need Two Channels, Since They Outnumber What A Single Channel Holds
        RemoveMembers(firstChannel, 10);

        ChatChannel.CompactGeneralChannels();

        using (Assert.Multiple())
        {
            await Assert.That(firstChannel.Members.Count).IsEqualTo(memberCap - 10);
            await Assert.That(secondChannel.Members.Count).IsEqualTo(20);
        }
    }

    [Test]
    public async Task Compaction_Renames_The_Only_Remaining_General_Channel_Back_To_The_Unnumbered_Name()
    {
        ChatChannel firstChannel = ChatChannel.GetOrCreateGeneralChannel();

        Fill(firstChannel);

        ChatChannel overflowChannel = ChatChannel.GetOrCreateGeneralChannel();

        // An Overflow Channel Is Removed Once Its Last Member Leaves
        Context.ChatChannels.TryRemove(overflowChannel.Name, out _);

        ChatChannel.CompactGeneralChannels();

        using (Assert.Multiple())
        {
            await Assert.That(firstChannel.Name).IsEqualTo(ChatProtocol.CHAT_CHANNEL_BASE_NAME);
            await Assert.That(Context.ChatChannels[ChatProtocol.CHAT_CHANNEL_BASE_NAME]).IsSameReferenceAs(firstChannel);
            await Assert.That(Context.ChatChannels.ContainsKey($"{ChatProtocol.CHAT_CHANNEL_BASE_NAME} 1")).IsFalse();
        }
    }

    [Test]
    public async Task Concurrent_Joins_And_Compaction_Preserve_Every_Membership_And_Channel_Cap()
    {
        List<ClientChatSession> sessions = [.. Enumerable.Range(1, 120).Select(memberIndex => CreateStandInSession($"RaceJoin{memberIndex}"))];
        List<Task> operations = [.. sessions.Select(session => Task.Run(() => ChatChannel.GetOrCreateGeneralChannel().Join(session)))];

        operations.Add(Task.Run(() =>
        {
            for (int iteration = 0; iteration < 30; iteration++)
                ChatChannel.CompactGeneralChannels();
        }));

        await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(15));

        List<ChatChannel> channels = [.. Context.ChatChannels.Values.Where(channel => channel.IsGeneralChannel)];

        await Assert.That(channels.Sum(channel => channel.Members.Count)).IsEqualTo(sessions.Count);

        foreach (ChatChannel channel in channels)
            await Assert.That(channel.Members.Count <= ChatProtocol.MAX_USERS_PER_CHANNEL).IsTrue();

        foreach (ClientChatSession session in sessions)
        {
            ChatChannel channel = channels.Single(candidate => candidate.Members.ContainsKey(session.Account.Name));

            await Assert.That(session.CurrentChannels).IsEquivalentTo([channel.ID]);
        }
    }

    [Test]
    public async Task Concurrent_Departures_And_Compaction_Do_Not_Reintroduce_Disconnected_Members()
    {
        List<ClientChatSession> sessions = [.. Enumerable.Range(1, 120).Select(memberIndex => CreateStandInSession($"RaceLeave{memberIndex}"))];

        foreach (ClientChatSession session in sessions)
            ChatChannel.GetOrCreateGeneralChannel().Join(session);

        List<ClientChatSession> departingSessions = [.. sessions.Take(60)];
        List<Task> operations = [.. departingSessions.Select(session => Task.Run(() => session.LeaveAllChannels()))];

        operations.Add(Task.Run(() =>
        {
            for (int iteration = 0; iteration < 30; iteration++)
                ChatChannel.CompactGeneralChannels();
        }));

        await Task.WhenAll(operations).WaitAsync(TimeSpan.FromSeconds(15));

        ChatChannel.CompactGeneralChannels();

        ChatChannel remainingChannel = Context.ChatChannels.Values.Single(channel => channel.IsGeneralChannel);

        await Assert.That(remainingChannel.Members.Count).IsEqualTo(60);

        foreach (ClientChatSession session in departingSessions)
        {
            await Assert.That(session.CurrentChannels).IsEmpty();
            await Assert.That(remainingChannel.Members.ContainsKey(session.Account.Name)).IsFalse();
        }

        foreach (ClientChatSession session in sessions.Skip(60))
            await Assert.That(session.CurrentChannels).IsEquivalentTo([remainingChannel.ID]);
    }

    [Test]
    public async Task A_Join_Selected_Before_An_Empty_Overflow_Is_Removed_Is_Routed_To_A_Registered_Channel()
    {
        Fill(ChatChannel.GetOrCreateGeneralChannel());

        ChatChannel abandonedChannel = ChatChannel.GetOrCreateGeneralChannel();

        ChatChannel.CompactGeneralChannels();

        await Assert.That(Context.ChatChannels.Values.Count(channel => channel.IsGeneralChannel)).IsEqualTo(1);

        ClientChatSession session = CreateStandInSession("DelayedJoin");
        ChatChannel joinedChannel = abandonedChannel.Join(session);

        await Assert.That(joinedChannel).IsSameReferenceAs(Context.ChatChannels[joinedChannel.Name]);
        await Assert.That(abandonedChannel.Members).IsEmpty();
        await Assert.That(joinedChannel.Members.ContainsKey(session.Account.Name)).IsTrue();
        await Assert.That(session.CurrentChannels).IsEquivalentTo([joinedChannel.ID]);
    }

    [Test]
    public async Task Compaction_Preserves_The_Longer_Silence_And_Moves_A_Player_At_The_Channel_Limit()
    {
        ChatChannel firstChannel = ChatChannel.GetOrCreateGeneralChannel();

        Fill(firstChannel);

        ChatChannel sourceChannel = ChatChannel.GetOrCreateGeneralChannel();
        Account moderatorAccount = CreateAccount("MovingModerator");
        moderatorAccount.Type = AccountType.Staff;

        await using RunningClientSession moderator = RunningClientSession.Start(moderatorAccount);
        await using RunningClientSession target = RunningClientSession.Start(CreateAccount("MovingSilenced"));

        sourceChannel.Join(moderator.Session);
        sourceChannel.Join(target.Session);

        for (int channelIndex = 1; channelIndex < ChatProtocol.MAX_CHANNELS_PER_CLIENT; channelIndex++)
            ChatChannel.GetOrCreate(target.Session, $"Compaction Limit {channelIndex}").Join(target.Session);

        sourceChannel.Silence(moderator.Session, target.Session, 60_000);

        for (int channelIndex = 0; channelIndex < ChatProtocol.MAX_CHANNELS_PER_CLIENT; channelIndex++)
            await Assert.That(await target.ReadCommand()).IsEqualTo((ushort) ChatProtocol.Command.CHAT_CMD_CHANGED_CHANNEL);

        await Assert.That(await target.ReadCommand()).IsEqualTo((ushort) ChatProtocol.Command.CHAT_CMD_CHANNEL_SILENCE_PLACED);

        DateTime longerExpiry = DateTime.UtcNow.AddMinutes(2);
        firstChannel.SilencedAccounts[target.Session.Account.ID] = longerExpiry;

        RemoveMembers(firstChannel, (int) ChatProtocol.MAX_USERS_PER_CHANNEL - 1);

        ChatChannel.CompactGeneralChannels();

        await Assert.That(firstChannel.IsSilenced(target.Session)).IsTrue();
        await Assert.That(await target.ReadCommand()).IsEqualTo((ushort) ChatProtocol.Command.CHAT_CMD_LEFT_CHANNEL);
        await Assert.That(await target.ReadCommand()).IsEqualTo((ushort) ChatProtocol.Command.CHAT_CMD_CHANGED_CHANNEL);
        await Assert.That(firstChannel.SilencedAccounts[target.Session.Account.ID]).IsEqualTo(longerExpiry);
        await Assert.That(firstChannel.Members.ContainsKey(target.Session.Account.Name)).IsTrue();
        await Assert.That(target.Session.CurrentChannels.Count).IsEqualTo((int) ChatProtocol.MAX_CHANNELS_PER_CLIENT);
        await Assert.That(sourceChannel.Members).IsEmpty();
        await Assert.That(firstChannel.Name).IsEqualTo(ChatProtocol.CHAT_CHANNEL_BASE_NAME);
    }

    [Test]
    public async Task Compaction_Does_Not_Move_A_Player_Into_A_Channel_They_Are_Banned_From()
    {
        ChatChannel firstChannel = ChatChannel.GetOrCreateGeneralChannel();

        Fill(firstChannel);

        ChatChannel sourceChannel = ChatChannel.GetOrCreateGeneralChannel();
        ClientChatSession target = CreateStandInSession("BannedMover");

        sourceChannel.Join(target);
        firstChannel.BannedAccountIDs.Add(target.Account.ID);
        RemoveMembers(firstChannel, (int) ChatProtocol.MAX_USERS_PER_CHANNEL - 1);

        ChatChannel.CompactGeneralChannels();

        await Assert.That(sourceChannel.Members.ContainsKey(target.Account.Name)).IsTrue();
        await Assert.That(firstChannel.Members.ContainsKey(target.Account.Name)).IsFalse();
        await Assert.That(target.CurrentChannels).IsEquivalentTo([sourceChannel.ID]);
        await Assert.That(Context.ChatChannels[sourceChannel.Name]).IsSameReferenceAs(sourceChannel);
    }

    [Test]
    public async Task Rejoining_A_Full_General_Channel_Does_Not_Create_Another_Membership()
    {
        ChatChannel channel = ChatChannel.GetOrCreateGeneralChannel();
        ClientChatSession session = CreateStandInSession("RepeatedJoin");

        channel.Join(session);
        Fill(channel);

        ChatChannel result = channel.Join(session);

        await Assert.That(result).IsSameReferenceAs(channel);
        await Assert.That(Context.ChatChannels.Values.Count(candidate => candidate.IsGeneralChannel)).IsEqualTo(1);
        await Assert.That(session.CurrentChannels).IsEquivalentTo([channel.ID]);
    }

    private ClientChatSession CreateStandInSession(string accountName)
        => new (new DefaultConnectionContext(), StandInHost.Services)
        {
            Account = CreateAccount(accountName),
            Metadata = (ClientChatSessionMetadata) RuntimeHelpers.GetUninitializedObject(typeof(ClientChatSessionMetadata))
        };

    private void Fill(ChatChannel channel)
        => AddMembers(channel, (int) ChatProtocol.MAX_USERS_PER_CHANNEL - channel.Members.Count);

    /// <summary>
    ///     Adds stand-in members which can be sent to, since renaming a channel and moving members both announce them to the channel members, but whose sent frames nothing reads.
    /// </summary>
    private void AddMembers(ChatChannel channel, int memberCount)
    {
        for (int memberIndex = 0; memberIndex < memberCount; memberIndex++)
        {
            StandInMemberCount++;

            ClientChatSession session = new (new DefaultConnectionContext(), StandInHost.Services)
            {
                Account = CreateAccount($"{StandInMemberNamePrefix} {StandInMemberCount}"),
                Metadata = (ClientChatSessionMetadata) RuntimeHelpers.GetUninitializedObject(typeof(ClientChatSessionMetadata))
            };

            channel.Members.TryAdd(session.Account.Name, new ChatChannelMember(session, channel));
            session.CurrentChannels.Add(channel.ID);
        }
    }

    /// <summary>
    ///     Removes stand-in members from the channel, as if they had left it, leaving any other members in place.
    /// </summary>
    private static void RemoveMembers(ChatChannel channel, int memberCount)
    {
        List<string> memberNames = [.. channel.Members.Keys.Where(memberName => memberName.StartsWith(StandInMemberNamePrefix)).Take(memberCount)];

        foreach (string memberName in memberNames)
            channel.Members.TryRemove(memberName, out _);
    }

    /// <summary>
    ///     Reads the frames sent to the client until a <see cref="ChatProtocol.Command.CHAT_CMD_MESSAGE_ALL"/> notice arrives, skipping the channel announcements which precede it, and returns the notice's message.
    /// </summary>
    private static async Task<string> ReadNoticeMessage(RunningClientSession client)
    {
        while (true)
        {
            (ushort command, string message) = await client.ReadNotice();

            if (command == (ushort) ChatProtocol.Command.CHAT_CMD_MESSAGE_ALL)
                return message;
        }
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

    private static void RemoveGeneralChannels()
    {
        List<ChatChannel> generalChannels = [.. Context.ChatChannels.Values.Where(channel => channel.IsGeneralChannel)];

        foreach (ChatChannel channel in generalChannels)
            Context.ChatChannels.TryRemove(channel.Name, out _);
    }
}
