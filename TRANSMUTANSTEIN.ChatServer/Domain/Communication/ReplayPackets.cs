namespace TRANSMUTANSTEIN.ChatServer.Domain.Communication;

/// <summary>
///     Encodes the native on-demand replay commands and bounds their variable-length fields.
/// </summary>
public static class ReplayPackets
{
    public static int ReadRequest(ChatBuffer buffer)
    {
        if (buffer.Size < 7)
            throw new InvalidDataException("Truncated Replay Request");

        buffer.ReadCommandBytes();
        int matchID = buffer.ReadInt32();
        string extension = ReadString(buffer, 16);

        if (matchID <= 0 || extension != "honreplay" || buffer.Offset != buffer.Size)
            throw new InvalidDataException("Invalid Replay Request");

        return matchID;
    }

    public static (int MatchID, int AccountID, ChatProtocol.UploadUpdateType Status) ReadUpdate(ChatBuffer buffer)
    {
        if (buffer.Size < 11)
            throw new InvalidDataException("Truncated Replay Upload Status");

        buffer.ReadCommandBytes();
        int matchID = buffer.ReadInt32();
        int accountID = buffer.ReadInt32();
        byte status = buffer.ReadInt8();

        if (status > (byte) ChatProtocol.UploadUpdateType.EUUT_FILE_UPLOAD_COMPLETE || matchID <= 0 || accountID <= 0)
            throw new InvalidDataException("Invalid Replay Upload Status");

        if ((ChatProtocol.UploadUpdateType) status is ChatProtocol.UploadUpdateType.EUUT_FILE_ALREADY_UPLOADED or ChatProtocol.UploadUpdateType.EUUT_FILE_UPLOAD_COMPLETE)
            ReadString(buffer, 2048);

        if (buffer.Offset != buffer.Size)
            throw new InvalidDataException("Unexpected Replay Upload Status Fields");

        return (matchID, accountID, (ChatProtocol.UploadUpdateType) status);
    }

    private static string ReadString(ChatBuffer buffer, int maximumLength)
    {
        int remaining = checked((int) (buffer.Size - buffer.Offset));

        if (remaining <= 0 || Array.IndexOf(buffer.Data, (byte) 0, (int) buffer.Offset, Math.Min(remaining, maximumLength + 1)) < 0)
            throw new InvalidDataException("Missing Or Oversized Replay String");

        return buffer.ReadString();
    }

    public static ChatBuffer CreateManagerRequest(int accountID, int matchID, ReplayStorageSettings settings)
    {
        ChatBuffer packet = new ();
        packet.WriteCommand(ChatProtocol.ChatServerToServerManager.NET_CHAT_SM_UPLOAD_REQUEST);
        packet.WriteInt32(accountID);
        packet.WriteInt32(matchID);
        packet.WriteString("honreplay");
        packet.WriteString(new Uri(settings.ReceiverURL).Authority);
        packet.WriteString("replays");
        packet.WriteInt8(0);
        packet.WriteInt8(1);
        packet.WriteString(settings.GetDownloadURL(matchID));
        return packet;
    }

    public static ChatBuffer CreateClientStatus(int matchID, ChatProtocol.UploadUpdateType status, string downloadURL = "")
    {
        ChatBuffer packet = new ();
        packet.WriteCommand(ChatProtocol.Command.CHAT_CMD_UPLOAD_STATUS);
        packet.WriteInt32(matchID);
        packet.WriteInt8((byte) status);

        if (status is ChatProtocol.UploadUpdateType.EUUT_FILE_ALREADY_UPLOADED or ChatProtocol.UploadUpdateType.EUUT_FILE_UPLOAD_COMPLETE)
            packet.WriteString(downloadURL);

        return packet;
    }
}
