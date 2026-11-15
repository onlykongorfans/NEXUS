namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests.Services;

public sealed class ReplayPacketTests
{
    [Test]
    public async Task Native_Replay_Request_Is_Read_Without_A_Client_Change()
    {
        ChatBuffer request = new ();
        request.WriteCommand(ChatProtocol.Command.CHAT_CMD_UPLOAD_REQUEST);
        request.WriteInt32(1853590033);
        request.WriteString("honreplay");
        await Assert.That(ReplayPackets.ReadRequest(request)).IsEqualTo(1853590033);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(6)]
    public async Task Truncated_Requests_Are_Rejected(int length)
    {
        await Assert.That(() => ReplayPackets.ReadRequest(new ChatBuffer(new byte[length]))).Throws<InvalidDataException>();
    }

    [Test]
    [Arguments("clog")]
    [Arguments("../honreplay")]
    public async Task Only_Replay_Files_Can_Be_Requested(string extension)
    {
        ChatBuffer request = new ();
        request.WriteCommand(ChatProtocol.Command.CHAT_CMD_UPLOAD_REQUEST);
        request.WriteInt32(123);
        request.WriteString(extension);
        await Assert.That(() => ReplayPackets.ReadRequest(request)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Missing_String_Terminator_Is_Rejected_Even_With_Spare_Buffer_Capacity()
    {
        ChatBuffer request = new ();
        request.WriteCommand(ChatProtocol.Command.CHAT_CMD_UPLOAD_REQUEST);
        request.WriteInt32(123);
        request.WriteString("honreplay");
        request.Resize(request.Size - 1);
        await Assert.That(() => ReplayPackets.ReadRequest(request)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task Captured_Native_Completion_Packet_Is_Parsed()
    {
        byte[] packet = Convert.FromHexString("0316118a7b6e2a00000007687474703a2f2f73796e632e6b6f6e676f722e66616e732f7265706c6179732f4d313835333539303033332e686f6e7265706c617900");
        (int matchID, int accountID, ChatProtocol.UploadUpdateType status) = ReplayPackets.ReadUpdate(new ChatBuffer(packet));
        await Assert.That(matchID).IsEqualTo(1853590033);
        await Assert.That(accountID).IsEqualTo(42);
        await Assert.That(status).IsEqualTo(ChatProtocol.UploadUpdateType.EUUT_FILE_UPLOAD_COMPLETE);
    }

    [Test]
    public async Task Manager_Request_Uses_The_Native_S3_Form_Upload_Flags()
    {
        ReplayStorageSettings settings = new ()
        {
            ReceiverURL = "http://master.test/replays/upload",
            DownloadBaseURL = "http://files.test/replays/"
        };
        ChatBuffer packet = ReplayPackets.CreateManagerRequest(42, 123, settings);
        await Assert.That(BitConverter.ToUInt16(packet.ReadCommandBytes())).IsEqualTo(ChatProtocol.ChatServerToServerManager.NET_CHAT_SM_UPLOAD_REQUEST);
        await Assert.That(packet.ReadInt32()).IsEqualTo(42);
        await Assert.That(packet.ReadInt32()).IsEqualTo(123);
        await Assert.That(packet.ReadString()).IsEqualTo("honreplay");
        await Assert.That(packet.ReadString()).IsEqualTo("master.test");
        await Assert.That(packet.ReadString()).IsEqualTo("replays");
        await Assert.That(packet.ReadInt8()).IsEqualTo((byte) 0);
        await Assert.That(packet.ReadInt8()).IsEqualTo((byte) 1);
        await Assert.That(packet.ReadString()).IsEqualTo("http://files.test/replays/M123.honreplay");
    }

    [Test]
    public async Task Client_Completion_Packet_Contains_The_Public_URL_Without_An_Account_ID()
    {
        ChatBuffer packet = ReplayPackets.CreateClientStatus(123, ChatProtocol.UploadUpdateType.EUUT_FILE_UPLOAD_COMPLETE, "http://files.test/replays/M123.honreplay");
        await Assert.That(BitConverter.ToUInt16(packet.ReadCommandBytes())).IsEqualTo(ChatProtocol.Command.CHAT_CMD_UPLOAD_STATUS);
        await Assert.That(packet.ReadInt32()).IsEqualTo(123);
        await Assert.That(packet.ReadInt8()).IsEqualTo((byte) 7);
        await Assert.That(packet.ReadString()).IsEqualTo("http://files.test/replays/M123.honreplay");
        await Assert.That(packet.Offset).IsEqualTo(packet.Size);
    }
}
