using SnapShotKit.Contracts;
using Tmds.DBus.Protocol;

namespace SnapShotKit.Cli;

/// <summary>Talks to snapshotkitd. Deliberately thin: this process is on the capture hot path.</summary>
internal sealed class DaemonClient : IDisposable
{
    readonly DBusConnection connection;

    DaemonClient(DBusConnection connection) => this.connection = connection;

    public static async Task<DaemonClient> ConnectAsync()
    {
        var connection = new DBusConnection(DBusAddress.Session
            ?? throw new InvalidOperationException("No session bus address. Is this a desktop session?"));

        await connection.ConnectAsync();
        return new DaemonClient(connection);
    }

    public Task<string> CaptureAsync()
    {
        return connection.CallMethodAsync(
            Build(SnapShotKitDBus.Capture),
            static (Message message, object? _) => message.GetBodyReader().ReadString(),
            null);
    }

    public Task<Dictionary<string, VariantValue>> StatusAsync()
    {
        return connection.CallMethodAsync(
            Build(SnapShotKitDBus.Status),
            static (Message message, object? _) => message.GetBodyReader().ReadDictionaryOfStringToVariantValue(),
            null);
    }

    MessageBuffer Build(string member)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(SnapShotKitDBus.ServiceName, SnapShotKitDBus.ObjectPath, SnapShotKitDBus.Interface, member);
        return writer.CreateMessage();
    }

    public void Dispose() => connection.Dispose();
}
