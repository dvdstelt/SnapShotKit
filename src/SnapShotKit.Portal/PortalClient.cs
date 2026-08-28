using Tmds.DBus.Protocol;

namespace SnapShotKit.Portal;

/// <summary>
/// Minimal client for the XDG desktop portal request pattern: a method call returns a
/// request object path, and the actual result arrives later as a Response signal on that path.
/// </summary>
public sealed class PortalClient : IDisposable
{
    public const string PortalService = "org.freedesktop.portal.Desktop";
    public const string PortalObject = "/org/freedesktop/portal/desktop";

    readonly DBusConnection connection;
    readonly string senderToken;
    int tokenCounter;

    PortalClient(DBusConnection connection, string senderToken)
    {
        this.connection = connection;
        this.senderToken = senderToken;
    }

    /// <summary>
    /// Adopts an existing connection instead of opening one.
    /// </summary>
    /// <remarks>
    /// Sharing matters. A process that holds two D-Bus connections cannot receive PipeWire buffers:
    /// the stream reports STREAMING and nothing ever arrives, and opening the second connection
    /// first makes libpipewire abort outright in remove_from_poll. Bisected in
    /// docs/spikes/005-thread-pool-capture-failure.md. Until that is understood, SnapShotKit keeps
    /// exactly one connection per process.
    /// </remarks>
    public static async Task<PortalClient> AdoptAsync(DBusConnection connection)
    {
        var uniqueName = connection.UniqueName ?? throw new InvalidOperationException("The connection has no unique name.");
        Trace($"adopted {uniqueName}");
        return await Task.FromResult(new PortalClient(connection, uniqueName.TrimStart(':').Replace('.', '_')));
    }

    public static async Task<PortalClient> ConnectAsync()
    {
        var address = DBusAddress.Session ?? throw new InvalidOperationException("No session bus address. Is DBUS_SESSION_BUS_ADDRESS set?");
        var connection = new DBusConnection(address);
        await connection.ConnectAsync();

        // The unique name ":1.234" becomes the "1_234" segment of the request object path.
        var uniqueName = connection.UniqueName ?? throw new InvalidOperationException("Connected without a unique bus name.");
        var senderToken = uniqueName.TrimStart(':').Replace('.', '_');

        Trace($"connected as {uniqueName} (sender token {senderToken})");
        return new PortalClient(connection, senderToken);
    }

    /// <summary>
    /// Resolves the unique bus name currently owning the portal service.
    /// Signals arrive stamped with the sender's unique name, and match rules are compared against
    /// that field literally, so a rule naming the well-known name never fires. Resolving per request
    /// rather than caching keeps this correct across a portal restart.
    /// </summary>
    Task<string> GetPortalOwnerAsync()
    {
        return connection.CallMethodAsync(
            CreateMessage(),
            static (Message message, object? _) => message.GetBodyReader().ReadString(),
            null);

        MessageBuffer CreateMessage()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetNameOwner", "s");
            writer.WriteString(PortalService);
            return writer.CreateMessage();
        }
    }

    /// <summary>Invokes a portal method that replies directly rather than through the request pattern.</summary>
    public Task<T> CallMethodAsync<T>(PortalMessageBody writeBody, MessageValueReader<T> reader)
    {
        return connection.CallMethodAsync(CreateMessage(), reader, null);

        MessageBuffer CreateMessage()
        {
            // Not a using declaration: a using variable cannot be passed by ref.
            var writer = connection.GetMessageWriter();
            try
            {
                writeBody(ref writer);
                return writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    public Task<uint> GetVersionAsync(string portalInterface)
    {
        return connection.CallMethodAsync(
            CreateMessage(),
            static (Message message, object? _) => message.GetBodyReader().ReadVariantValue().GetUInt32(),
            null);

        MessageBuffer CreateMessage()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(PortalService, PortalObject, "org.freedesktop.DBus.Properties", "Get", "ss");
            writer.WriteString(portalInterface);
            writer.WriteString("version");
            return writer.CreateMessage();
        }
    }

    /// <summary>
    /// Invokes a portal method that follows the request pattern and waits for its Response signal.
    /// The response subscription is set up before the call is made, so the reply cannot be missed.
    /// </summary>
    public async Task<PortalResponse> CallRequestAsync(PortalRequestBody writeBody, CancellationToken cancellationToken = default)
    {
        var handleToken = $"snapshotkit_{Environment.ProcessId}_{Interlocked.Increment(ref tokenCounter)}";
        var expectedPath = $"{PortalObject}/request/{senderToken}/{handleToken}";

        var portalOwner = await GetPortalOwnerAsync();

        Trace($"expecting response on {expectedPath} from {portalOwner}");

        var completion = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = portalOwner,
            Path = expectedPath,
            Interface = "org.freedesktop.portal.Request",
            Member = "Response"
        };

        using var subscription = await connection.AddMatchAsync(
            rule,
            static (Message message, object? _) =>
            {
                var reader = message.GetBodyReader();
                var status = reader.ReadUInt32();
                var results = reader.ReadDictionaryOfStringToVariantValue();
                return new PortalResponse((PortalResponseStatus)status, results);
            },
            (Notification<PortalResponse> notification) =>
            {
                // Exception is only readable on a completion notification; reading it on a value
                // notification throws and takes the observer down with it.
                if (notification.IsCompletion)
                {
                    Trace($"observer completed: {notification.Type}");
                    completion.TrySetException(notification.Exception ?? new InvalidOperationException($"Portal response observer ended ({notification.Type}) before a response arrived."));
                    return;
                }

                if (notification.HasValue)
                {
                    Trace($"response received: {notification.Value.Status}");
                    completion.TrySetResult(notification.Value);
                }
            },
            emitOnCapturedContext: false,
            ObserverFlags.None,
            state: null);

        Trace("match rule registered, building message");

        var message = CreateMessage();

        Trace("message built, sending method call");

        var actualPath = await connection.CallMethodAsync(
            message,
            static (Message message, object? _) => message.GetBodyReader().ReadObjectPathAsString(),
            null);

        Trace($"method call returned '{actualPath}'");

        if (actualPath != expectedPath)
        {
            throw new InvalidOperationException($"Portal returned request path '{actualPath}' but '{expectedPath}' was predicted. The handle_token convention changed and the response would have been missed.");
        }

        Trace("awaiting response signal");

        await using var registration = cancellationToken.Register(static state => ((TaskCompletionSource<PortalResponse>)state!).TrySetCanceled(), completion);
        return await completion.Task;

        MessageBuffer CreateMessage()
        {
            // Not a using declaration: a using variable cannot be passed by ref.
            var writer = connection.GetMessageWriter();
            try
            {
                writeBody(ref writer, handleToken);
                return writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
        }
    }

    public static void Trace(string message)
    {
        if (Environment.GetEnvironmentVariable("SNAPSHOTKIT_TRACE") is not null)
        {
            Console.Error.WriteLine($"    trace [{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }

    public void Dispose() => connection.Dispose();
}

/// <summary>
/// Writes the body of a portal request, stamping in the handle token the caller must echo.
/// The writer is passed by reference because <see cref="MessageWriter"/> is a mutable ref struct:
/// pass it by value and every write lands in a copy, producing an empty message that is silently
/// never answered.
/// </summary>
public delegate void PortalRequestBody(ref MessageWriter writer, string handleToken);

/// <summary>Writes the body of a portal method call that does not use the request pattern.</summary>
public delegate void PortalMessageBody(ref MessageWriter writer);

public enum PortalResponseStatus : uint
{
    Success = 0,
    CancelledByUser = 1,
    Ended = 2
}

public readonly record struct PortalResponse(PortalResponseStatus Status, Dictionary<string, VariantValue> Results);
