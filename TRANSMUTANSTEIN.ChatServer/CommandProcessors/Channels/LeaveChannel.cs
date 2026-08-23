namespace TRANSMUTANSTEIN.ChatServer.CommandProcessors.Channels;

[ChatCommand(ChatProtocol.Command.CHAT_CMD_LEAVE_CHANNEL)]
public class LeaveChannel : ISynchronousCommandProcessor<ClientChatSession>
{
    public void Process(ClientChatSession session, ChatBuffer buffer)
    {
        LeaveChannelRequestData requestData = new (buffer);

        if (ChatChannel.TryGet(session, requestData.ChannelName, out ChatChannel? channel) && channel is not null)
            channel.Leave(session);
    }
}

file class LeaveChannelRequestData
{
    public byte[] CommandBytes { get; init; }

    public string ChannelName { get; init; }

    public LeaveChannelRequestData(ChatBuffer buffer)
    {
        CommandBytes = buffer.ReadCommandBytes();
        ChannelName = buffer.ReadString();
    }
}
