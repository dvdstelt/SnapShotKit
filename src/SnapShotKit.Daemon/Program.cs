using System.Runtime.InteropServices;
using SnapShotKit.Contracts;
using SnapShotKit.Daemon;
using SnapShotKit.Portal;
using Tmds.DBus.Protocol;

// snapshotkitd: owns the capture pipeline so that pressing Print costs a stream activation rather than
// a portal handshake. Headless by design, see docs/spikes/004-process-model.md.

Log.Info("snapshotkitd starting");

var connection = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No session bus."));
await connection.ConnectAsync();

await using var engine = new CaptureEngine();

try
{
    await engine.InitialiseAsync(connection);
}
catch (Exception exception)
{
    Log.Error($"Could not reach the portal: {exception.Message}");
    return 1;
}

// Handler first, name second, and the name last of all.
//
// The name appearing on the bus is what tells everyone else the daemon is ready, and with D-Bus
// activation the call that started us arrives the instant it appears. Claiming it before the
// handler exists loses exactly that call, which is the first capture after a login: the one
// gesture that has to work.
connection.AddMethodHandler(new DaemonService(engine));

// Whoever takes the name owns the session. A second daemon exits here rather than racing for the
// PipeWire session and leaving two consent dialogs behind.
if (!await connection.TryRequestNameAsync(SnapShotKitDBus.ServiceName, RequestNameOptions.None))
{
    Log.Error($"{SnapShotKitDBus.ServiceName} is already owned. Another daemon is running.");
    return 1;
}

await engine.WarmUpAsync();

switch (engine.Backend)
{
    case CaptureBackend.ScreenCast:
        Log.Info($"Ready on the fast path, {engine.StreamSize?.Width}x{engine.StreamSize?.Height}, saving to {SnapShotKitPaths.Exports}");
        break;

    case CaptureBackend.ScreenshotPortal:
        Log.Warn($"Ready on the slow fallback path, about 700 ms per capture. Reason: {engine.LastError}");
        break;
}

if (engine.ConsentNotRemembered)
{
    Log.Warn("Screen sharing consent was granted but not remembered, so the next restart will ask again. Tick the remember box to avoid that.");
}

// Wait for a signal rather than exiting. systemd stops us with SIGTERM.
var shutdown = new TaskCompletionSource();
using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);

await shutdown.Task;

Log.Info("snapshotkitd stopping");
return 0;

void Stop(PosixSignalContext context)
{
    context.Cancel = true;
    shutdown.TrySetResult();
}
