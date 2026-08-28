using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace SnapShotKit.Portal;

/// <summary>
/// Wraps org.freedesktop.portal.ScreenCast.
///
/// Unlike the Screenshot portal this is a session: create it, describe the sources, start it, and
/// then pull frames from PipeWire for as long as the session stays open. Starting a session shows a
/// consent dialog, but a session started with <see cref="PersistMode.UntilRevoked"/> hands back a
/// restore token that suppresses the dialog on every later start.
/// </summary>
public sealed class ScreenCastPortal(PortalClient portal)
{
    const string Interface = "org.freedesktop.portal.ScreenCast";

    public Task<uint> GetVersionAsync() => portal.GetVersionAsync(Interface);

    public async Task<string> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        var sessionToken = $"snapshotkit_session_{Environment.ProcessId}";

        var response = await portal.CallRequestAsync((ref MessageWriter writer, string handleToken) =>
        {
            writer.WriteMethodCallHeader(PortalClient.PortalService, PortalClient.PortalObject, Interface, "CreateSession", "a{sv}");
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String(handleToken),
                ["session_handle_token"] = VariantValue.String(sessionToken)
            });
        }, cancellationToken);

        Expect(response, "CreateSession");

        return response.Results.TryGetValue("session_handle", out var handle)
            ? handle.GetString()
            : throw new InvalidOperationException("CreateSession succeeded but returned no session_handle.");
    }

    /// <param name="restoreToken">A token from a previous session, or null to ask for consent afresh.</param>
    public async Task SelectSourcesAsync(string sessionHandle, string? restoreToken, CancellationToken cancellationToken = default)
    {
        var response = await portal.CallRequestAsync((ref MessageWriter writer, string handleToken) =>
        {
            var options = new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String(handleToken),
                ["types"] = VariantValue.UInt32((uint)SourceType.Monitor),
                ["multiple"] = VariantValue.Bool(false),
                ["cursor_mode"] = VariantValue.UInt32((uint)CursorMode.Hidden),
                ["persist_mode"] = VariantValue.UInt32((uint)PersistMode.UntilRevoked)
            };

            if (restoreToken is not null)
            {
                options["restore_token"] = VariantValue.String(restoreToken);
            }

            writer.WriteMethodCallHeader(PortalClient.PortalService, PortalClient.PortalObject, Interface, "SelectSources", "oa{sv}");
            writer.WriteObjectPath(sessionHandle);
            writer.WriteDictionary(options);
        }, cancellationToken);

        Expect(response, "SelectSources");
    }

    public async Task<ScreenCastSession> StartAsync(string sessionHandle, CancellationToken cancellationToken = default)
    {
        var response = await portal.CallRequestAsync((ref MessageWriter writer, string handleToken) =>
        {
            writer.WriteMethodCallHeader(PortalClient.PortalService, PortalClient.PortalObject, Interface, "Start", "osa{sv}");
            writer.WriteObjectPath(sessionHandle);
            writer.WriteString(string.Empty); // parent_window
            writer.WriteDictionary(new Dictionary<string, VariantValue>
            {
                ["handle_token"] = VariantValue.String(handleToken)
            });
        }, cancellationToken);

        Expect(response, "Start");

        PortalClient.Trace($"Start results: {string.Join(", ", response.Results.Select(pair => $"{pair.Key}={pair.Value.Type}"))}");

        // Read the restore token before anything that can throw. Losing it means the user is asked
        // for consent all over again, which is the one thing this spike is trying to avoid.
        var restoreToken = response.Results.TryGetValue("restore_token", out var token) ? token.GetString() : null;

        // streams is a(ua{sv}): each entry pairs a PipeWire node id with its properties.
        if (!response.Results.TryGetValue("streams", out var streams) || streams.Count == 0)
        {
            throw new PortalStreamException("Start succeeded but returned no streams.", restoreToken);
        }

        var stream = streams.GetItem(0);
        var nodeId = stream.GetItem(0).GetUInt32();
        var size = ReadSize(stream.GetItem(1));

        return new ScreenCastSession(sessionHandle, nodeId, size, restoreToken);
    }

    /// <summary>
    /// Returns a file descriptor for the PipeWire remote backing this session. The caller owns it.
    /// </summary>
    /// <remarks>
    /// The descriptor is duplicated inside the reader, while the message still holds the original.
    /// ReadHandleRaw does not transfer ownership: the fd it returns is closed whenever the message
    /// is disposed, at a moment nobody here controls. The duplicate is ours outright, and dup also
    /// leaves close-on-exec clear, which the capture helper depends on to inherit it across exec.
    /// </remarks>
    public Task<int> OpenPipeWireRemoteAsync(string sessionHandle)
    {
        return portal.CallMethodAsync(
            (ref MessageWriter writer) =>
            {
                writer.WriteMethodCallHeader(PortalClient.PortalService, PortalClient.PortalObject, Interface, "OpenPipeWireRemote", "oa{sv}");
                writer.WriteObjectPath(sessionHandle);
                writer.WriteDictionary(new Dictionary<string, VariantValue>());
            },
            static (Message message, object? _) =>
            {
                var borrowed = (int)message.GetBodyReader().ReadHandleRaw();
                var owned = dup(borrowed);

                return owned >= 0
                    ? owned
                    : throw new InvalidOperationException($"Could not duplicate the PipeWire descriptor: errno {Marshal.GetLastPInvokeError()}.");
            });
    }

    [DllImport("libc", SetLastError = true)]
    static extern int dup(int fd);

    /// <summary>Reads the optional "size" property, which arrives as a struct of two int32s.</summary>
    static (int Width, int Height)? ReadSize(VariantValue properties)
    {
        for (var i = 0; i < properties.Count; i++)
        {
            var entry = properties.GetDictionaryEntry(i);
            if (entry.Key.GetString() != "size")
            {
                continue;
            }

            // Tmds unwraps the variant for us, so the value is the struct itself.
            var value = entry.Value;
            if (value.Type == VariantValueType.Variant)
            {
                value = value.GetVariantValue();
            }

            return value.Count >= 2 ? (value.GetItem(0).GetInt32(), value.GetItem(1).GetInt32()) : null;
        }

        return null;
    }

    /// <summary>
    /// Closes the session. The portal also tears it down when the connection drops, but a daemon
    /// that opens sessions repeatedly should not rely on that.
    /// </summary>
    public Task CloseSessionAsync(string sessionHandle)
    {
        return portal.CallMethodAsync(
            (ref MessageWriter writer) =>
            {
                writer.WriteMethodCallHeader(PortalClient.PortalService, sessionHandle, "org.freedesktop.portal.Session", "Close", null);
            },
            static (Message _, object? _) => 0);
    }

    static void Expect(PortalResponse response, string step)
    {
        if (response.Status != PortalResponseStatus.Success)
        {
            throw new PortalRefusedException(step, response.Status);
        }
    }
}

public sealed class PortalRefusedException(string step, PortalResponseStatus status)
    : Exception($"{step} returned {status}.")
{
    public PortalResponseStatus Status { get; } = status;
}

/// <summary>
/// Thrown when a session started but its streams could not be read. Carries the restore token so a
/// caller can still persist it and avoid asking the user for consent again.
/// </summary>
public sealed class PortalStreamException(string message, string? restoreToken) : Exception(message)
{
    public string? RestoreToken { get; } = restoreToken;
}

public readonly record struct ScreenCastSession(string SessionHandle, uint NodeId, (int Width, int Height)? Size, string? RestoreToken);

[Flags]
public enum SourceType : uint
{
    Monitor = 1,
    Window = 2,
    Virtual = 4
}

[Flags]
public enum CursorMode : uint
{
    Hidden = 1,
    Embedded = 2,
    Metadata = 4
}

public enum PersistMode : uint
{
    DoNotPersist = 0,
    WhileAppRuns = 1,
    UntilRevoked = 2
}
