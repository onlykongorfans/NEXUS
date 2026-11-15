namespace ASPIRE.Tests.TRANSMUTANSTEIN.ChatServer.Tests.Transport;

/// <summary>
///     Exercises the chat session's transport behaviour (framing, command-code routing, sending, and the connection lifecycle) over an in-memory connection.
///     The session is driven through a <see cref="DefaultConnectionContext"/> backed by a pair of in-memory pipes, so these tests cover the real session code path without binding sockets or starting Kestrel.
///     The <c>PING</c> command is used as the probe because it has no database or distributed-cache dependency and simply responds with <c>PONG</c>.
/// </summary>
public sealed class InMemoryChatConnectionTests
{
    [Test]
    public async Task Ping_Command_Receives_Pong_Response()
    {
        await using InMemoryChatConnection connection = InMemoryChatConnection.Start();

        await connection.WriteFrame(CommandPayload(ChatProtocol.Bidirectional.NET_CHAT_PING));

        byte[] response = await connection.ReadFrame();

        await Assert.That(BitConverter.ToUInt16(response, 0)).IsEqualTo((ushort) ChatProtocol.Bidirectional.NET_CHAT_PONG);
    }

    [Test]
    public async Task Two_Frames_In_One_Write_Are_Both_Dispatched()
    {
        await using InMemoryChatConnection connection = InMemoryChatConnection.Start();

        byte[] pingFrame = InMemoryChatConnection.Frame(CommandPayload(ChatProtocol.Bidirectional.NET_CHAT_PING));

        await connection.WriteBytes([.. pingFrame, .. pingFrame]);

        byte[] firstResponse = await connection.ReadFrame();
        byte[] secondResponse = await connection.ReadFrame();

        using (Assert.Multiple())
        {
            await Assert.That(BitConverter.ToUInt16(firstResponse, 0)).IsEqualTo((ushort) ChatProtocol.Bidirectional.NET_CHAT_PONG);
            await Assert.That(BitConverter.ToUInt16(secondResponse, 0)).IsEqualTo((ushort) ChatProtocol.Bidirectional.NET_CHAT_PONG);
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Frame_Split_Across_Two_Writes_Is_Reassembled(int splitOffset)
    {
        await using InMemoryChatConnection connection = InMemoryChatConnection.Start();

        byte[] pingFrame = InMemoryChatConnection.Frame(CommandPayload(ChatProtocol.Bidirectional.NET_CHAT_PING));

        // Split Within The Length Prefix, At The Payload Boundary, Or Within The Command
        await connection.WriteBytes(pingFrame[..splitOffset]);
        await connection.WriteBytes(pingFrame[splitOffset..]);

        byte[] response = await connection.ReadFrame();

        await Assert.That(BitConverter.ToUInt16(response, 0)).IsEqualTo((ushort) ChatProtocol.Bidirectional.NET_CHAT_PONG);
    }

    [Test]
    public async Task Unknown_Command_Is_Skipped_And_The_Connection_Survives()
    {
        await using InMemoryChatConnection connection = InMemoryChatConnection.Start();

        // 0xFFFF Maps To No Command Processor; It Should Be Logged And Skipped Rather Than Faulting The Read Loop
        await connection.WriteFrame(CommandPayload(0xFFFF));
        await connection.WriteFrame(CommandPayload(ChatProtocol.Bidirectional.NET_CHAT_PING));

        byte[] response = await connection.ReadFrame();

        // The Following Ping Still Round-Trips, Which Proves The Connection And The Read Loop Survived The Unknown Command
        await Assert.That(BitConverter.ToUInt16(response, 0)).IsEqualTo((ushort) ChatProtocol.Bidirectional.NET_CHAT_PONG);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task Short_Frame_Closes_The_Session_With_A_Warning_And_No_Error(int payloadLength)
    {
        await using InMemoryChatConnection connection = InMemoryChatConnection.Start();

        await connection.WriteFrame(new byte[payloadLength]);

        await connection.WaitForCompletion();

        using (Assert.Multiple())
        {
            await Assert.That(connection.LogEvents.Count(logEvent => logEvent.Level is LogEventLevel.Warning)).IsEqualTo(1);
            await Assert.That(connection.LogEvents.Count(logEvent => logEvent.Level >= LogEventLevel.Error)).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task Short_Frame_Preserves_A_Queued_Response_And_Stops_Later_Commands(int payloadLength)
    {
        await using InMemoryChatConnection connection = InMemoryChatConnection.Start();

        byte[] pingFrame = InMemoryChatConnection.Frame(CommandPayload(ChatProtocol.Bidirectional.NET_CHAT_PING));
        byte[] shortFrame = InMemoryChatConnection.Frame(new byte[payloadLength]);

        await connection.WriteBytes([.. pingFrame, .. shortFrame, .. pingFrame]);

        await connection.WaitForCompletion();

        byte[] response = await connection.ReadFrame();

        using (Assert.Multiple())
        {
            await Assert.That(BitConverter.ToUInt16(response, 0)).IsEqualTo((ushort) ChatProtocol.Bidirectional.NET_CHAT_PONG);
            await Assert.That(connection.HasCompletedOutput).IsTrue();
            await Assert.That(connection.LogEvents.Count(logEvent => logEvent.Level is LogEventLevel.Warning)).IsEqualTo(1);
            await Assert.That(connection.LogEvents.Count(logEvent => logEvent.Level >= LogEventLevel.Error)).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Completing_The_Input_Ends_The_Session_Cleanly()
    {
        await using InMemoryChatConnection connection = InMemoryChatConnection.Start();

        await connection.CompleteInput();

        // Once The Peer Stops Sending, The Session's RunAsync Completes Without Faulting
        await connection.WaitForCompletion();
    }

    private static byte[] CommandPayload(int command) => BitConverter.GetBytes((ushort) command);
}

/// <summary>
///     Drives a <see cref="ClientChatSession"/> over a pair of in-memory pipes, standing in for the duplex pipe that Kestrel would otherwise supply.
///     Bytes written via <see cref="WriteBytes"/> reach the session's read loop; frames the session sends are read back via <see cref="ReadFrame"/>.
/// </summary>
file sealed class InMemoryChatConnection : IAsyncDisposable
{
    public List<LogEvent> LogEvents { get; }

    public bool HasCompletedOutput
    {
        get
        {
            if (Outbound.Reader.TryRead(out ReadResult result) is false)
                return false;

            bool completed = result.IsCompleted && result.Buffer.IsEmpty;

            Outbound.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);

            return completed;
        }
    }

    private WebApplication Host { get; }

    private Pipe Inbound { get; }

    private Pipe Outbound { get; }

    private Task SessionTask { get; }

    private InMemoryChatConnection(WebApplication host, Pipe inbound, Pipe outbound, Task sessionTask, List<LogEvent> logEvents)
    {
        Host = host;
        Inbound = inbound;
        Outbound = outbound;
        SessionTask = sessionTask;
        LogEvents = logEvents;
    }

    public static InMemoryChatConnection Start()
    {
        WebApplication host = WebApplication.CreateSlimBuilder().Build();

        // The Chat Server's Static Logger Facade Throws Until Initialised; A Sink-Less Serilog Logger Satisfies It Without Producing Output
        Log.Initialise(new Serilog.LoggerConfiguration().CreateLogger());

        Pipe inbound = new ();
        Pipe outbound = new ();

        DefaultConnectionContext connection = new ()
        {
            Transport = new InMemoryDuplexPipe(inbound.Reader, outbound.Writer)
        };

        List<LogEvent> logEvents = [];

        Serilog.ILogger logger = new Serilog.LoggerConfiguration()
            .WriteTo.Sink(new InMemoryChatLogSink(logEvents))
            .CreateLogger();

        Task sessionTask = new InMemoryClientChatSession(connection, host.Services, logger).RunAsync();

        return new InMemoryChatConnection(host, inbound, outbound, sessionTask, logEvents);
    }

    public static byte[] Frame(byte[] payload)
    {
        byte[] frame = new byte[2 + payload.Length];

        BitConverter.GetBytes((ushort) payload.Length).CopyTo(frame, 0);

        payload.CopyTo(frame, 2);

        return frame;
    }

    public async Task WriteBytes(byte[] bytes) => await Inbound.Writer.WriteAsync(bytes);

    public Task WriteFrame(byte[] payload) => WriteBytes(Frame(payload));

    public async Task<byte[]> ReadFrame()
    {
        using CancellationTokenSource timeout = new (TimeSpan.FromSeconds(5));

        while (true)
        {
            ReadResult result = await Outbound.Reader.ReadAsync(timeout.Token);
            ReadOnlySequence<byte> buffer = result.Buffer;

            if (buffer.Length >= 2)
            {
                ushort length = BitConverter.ToUInt16(buffer.Slice(0, 2).ToArray(), 0);

                if (buffer.Length >= 2 + length)
                {
                    byte[] payload = buffer.Slice(2, length).ToArray();

                    Outbound.Reader.AdvanceTo(buffer.GetPosition(2 + length));

                    return payload;
                }
            }

            Outbound.Reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
                throw new InvalidOperationException("The Connection Closed Before A Complete Frame Was Received");
        }
    }

    public async Task CompleteInput() => await Inbound.Writer.CompleteAsync();

    public async Task WaitForCompletion() => await SessionTask.WaitAsync(TimeSpan.FromSeconds(5));

    public async ValueTask DisposeAsync()
    {
        await Inbound.Writer.CompleteAsync();

        try { await SessionTask.WaitAsync(TimeSpan.FromSeconds(5)); }

        catch { }

        await Host.DisposeAsync();
    }
}

file sealed class InMemoryClientChatSession(ConnectionContext connection, IServiceProvider serviceProvider, Serilog.ILogger logger) : ClientChatSession(connection, serviceProvider)
{
    protected override void OnConnected()
    {
        base.OnConnected();

        Logger = logger.ForContext("SessionID", ID);
    }
}

file sealed class InMemoryChatLogSink(List<LogEvent> logEvents) : ILogEventSink
{
    public void Emit(LogEvent logEvent) => logEvents.Add(logEvent);
}

file sealed class InMemoryDuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
{
    public PipeReader Input { get; } = input;

    public PipeWriter Output { get; } = output;
}
