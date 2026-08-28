using System.Diagnostics;
using SnapShotKit.Contracts;
using SnapShotKit.Portal;
using Tmds.DBus.Protocol;

namespace SnapShotKit.Daemon;

/// <summary>
/// Owns the capture pipeline: the ScreenCast session, the parked PipeWire stream, and the fallback.
///
/// Everything expensive happens in <see cref="StartAsync"/> so that capturing costs only a stream
/// activation. See docs/spikes/003-pipewire-shim.md for why that is about 35 ms rather than 700.
/// </summary>
public sealed class CaptureEngine : IAsyncDisposable
{
    /// <summary>
    /// How long a ScreenCast session is held after a capture.
    ///
    /// GNOME shows the screen-sharing indicator for as long as a session exists, so holding one
    /// permanently means the desktop claims SnapShotKit is recording all day. Releasing it means the
    /// next capture pays roughly 300 ms to re-establish, which is why it is a timeout rather than an
    /// immediate release: a burst of captures stays fast, and the indicator disappears shortly after
    /// you stop.
    /// </summary>
    static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long to wait for a session before giving up on it.
    ///
    /// Establishing normally takes about 100 ms. It takes minutes only when consent is being asked
    /// for and nobody is at the keyboard, and a capture that blocks forever waiting for a dialog the
    /// user may never see is worse than a slow one.
    /// </summary>
    static readonly TimeSpan EstablishTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long to stay on the slow path after the fast one failed, before trying it again.
    ///
    /// Long enough that a genuinely broken session does not throw a consent dialog at every
    /// capture, short enough that one failed establish does not degrade the rest of the day.
    /// </summary>
    static readonly TimeSpan FastPathRetryCooldown = TimeSpan.FromMinutes(5);

    readonly SemaphoreSlim gate = new(1, 1);
    readonly CaptureThread captureThread = new();
    readonly Timer idleTimer;

    PortalClient? portal;
    ScreenCastPortal? screenCast;
    ScreenshotPortal? screenshot;
    CaptureHelper? helper;
    DBusConnection? connection;
    string? sessionHandle;
    byte[]? buffer;

    DateTime lastUsed = DateTime.UtcNow;
    DateTime lastFastPathFailure = DateTime.MinValue;

    public CaptureEngine() => idleTimer = new Timer(_ => ReleaseIfIdle(), null, IdleTimeout, IdleTimeout);

    public CaptureBackend Backend { get; private set; } = CaptureBackend.Unavailable;

    /// <summary>True while a ScreenCast session is held, which is when the desktop shows the sharing indicator.</summary>
    public bool SessionHeld => helper is not null;

    public string? LastError { get; private set; }

    /// <summary>True when consent was granted but not remembered, so the session cannot be restored silently.</summary>
    public bool ConsentNotRemembered { get; private set; }

    /// <summary>The shared file the helper writes frames into, which the overlay maps directly.</summary>
    public static string FramePath => Path.Combine(SnapShotKitPaths.Runtime, "frame.raw");

    public (int Width, int Height)? StreamSize { get; private set; }

    /// <summary>
    /// Connects to the portal. Fast and silent: no session, no dialogs, nothing that can block.
    /// </summary>
    public async Task InitialiseAsync(DBusConnection connection)
    {
        SnapShotKitPaths.EnsureCreated();

        // Deliberately reusing the daemon's connection: a second one breaks PipeWire capture.
        this.connection = connection;
        portal = await PortalClient.AdoptAsync(connection);
        screenshot = new ScreenshotPortal(portal);
        screenCast = new ScreenCastPortal(portal);

        // The fallback needs nothing else, so capture already works from this point on.
        Backend = CaptureBackend.ScreenshotPortal;
    }

    /// <summary>
    /// Gets the fast path ready. Separate from initialising because this one can show a consent
    /// dialog and therefore block for as long as nobody clicks it, and the daemon has to be
    /// answering D-Bus by then or `snapshotkit status` cannot say what is going on.
    /// </summary>
    /// <summary>
    /// Decides which path capture will take, without doing anything the user has to answer.
    ///
    /// Nothing is established here. With a token, doing so would light the sharing indicator at
    /// login for no reason. Without one, consent is needed, and a dialog that appears at login is a
    /// dialog nobody is expecting; asking on the first capture puts it in front of someone who just
    /// pressed a key and is therefore watching.
    /// </summary>
    public Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(SnapShotKitPaths.RestoreTokenFile))
        {
            Backend = CaptureBackend.ScreenCast;
            Log.Info("Session will be established on first use, so the desktop shows no sharing indicator while idle");
        }
        else
        {
            Backend = CaptureBackend.ScreenCast;
            LastError = "No screen sharing consent on record. The first capture will ask for it, "
                + "and ticking the remember box is what keeps the fast path.";
            Log.Info(LastError);
        }

        return Task.CompletedTask;
    }

    /// <summary>Establishes a session, bounded so a pending consent dialog cannot block a capture forever.</summary>
    async Task<bool> TryEstablishAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(EstablishTimeout);

        try
        {
            await StartScreenCastAsync(deadline.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LastError = "Screen sharing consent was not granted, so capture is using the slow fallback. "
                + "Accept the dialog and tick the remember box to get the fast path back.";
            Log.Warn(LastError);
        }
        catch (Exception exception)
        {
            LastError = $"Could not establish a capture session: {exception.Message}";
            Log.Warn(LastError);
        }

        await TearDownScreenCastAsync();
        return false;
    }

    void ReleaseIfIdle()
    {
        if (!SessionHeld || DateTime.UtcNow - lastUsed < IdleTimeout)
        {
            return;
        }

        if (!gate.Wait(TimeSpan.Zero))
        {
            // A capture is in flight. The next tick can deal with it.
            return;
        }

        try
        {
            TearDownScreenCastAsync().GetAwaiter().GetResult();
            Log.Info("Session released after being idle, sharing indicator cleared");
        }
        catch (Exception exception)
        {
            Log.Warn($"Could not release the idle session: {exception.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    async Task StartScreenCastAsync(CancellationToken cancellationToken)
    {
        var restoreToken = File.Exists(SnapShotKitPaths.RestoreTokenFile)
            ? (await File.ReadAllTextAsync(SnapShotKitPaths.RestoreTokenFile, cancellationToken)).Trim()
            : null;

        sessionHandle = await screenCast!.CreateSessionAsync(cancellationToken);
        await screenCast.SelectSourcesAsync(sessionHandle, restoreToken, cancellationToken);
        var session = await screenCast.StartAsync(sessionHandle, cancellationToken);

        if (session.RestoreToken is { } issued)
        {
            await File.WriteAllTextAsync(SnapShotKitPaths.RestoreTokenFile, issued, cancellationToken);
            ConsentNotRemembered = false;
        }
        else
        {
            // Start succeeded and simply omitted the token, which means the remember box was not
            // ticked. Nothing failed, but every restart will prompt again.
            ConsentNotRemembered = true;
        }

        StreamSize = session.Size;

        var fd = await screenCast.OpenPipeWireRemoteAsync(sessionHandle);
        var size = session.Size ?? throw new InvalidOperationException("The portal did not report a stream size.");
        helper = CaptureHelper.Start(fd, session.NodeId, size.Width, size.Height);

        buffer = new byte[(long)size.Width * size.Height * 4];

        // The first grab pays for format negotiation, about 70 ms. Spending it here means the user
        // never does.
        helper.Grab(buffer);
    }

    public async Task<CaptureResult> CaptureAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            lastUsed = DateTime.UtcNow;

            // A failed establish demotes to the slow path, but not forever: with a restore token
            // on record the fast path can be retried without bothering anyone, so it is, once the
            // cooldown has passed.
            if (Backend == CaptureBackend.ScreenshotPortal
                && File.Exists(SnapShotKitPaths.RestoreTokenFile)
                && DateTime.UtcNow - lastFastPathFailure > FastPathRetryCooldown)
            {
                Log.Info("Retrying the fast capture path");
                Backend = CaptureBackend.ScreenCast;
            }

            if (Backend == CaptureBackend.ScreenCast)
            {
                if (helper is null && !await TryEstablishAsync(cancellationToken))
                {
                    Demote();
                    return await CaptureViaScreenshotPortalAsync(stopwatch, cancellationToken);
                }

                if (TryGrab(stopwatch) is { } frame)
                {
                    return frame;
                }

                // A session that has gone stale is the normal case here, not an exception: leaving
                // the screen idle for half an hour is enough. Rebuild it once before giving up,
                // because falling back costs the user the overlay and six times the latency.
                Log.Warn($"Fast capture failed ({LastError}), rebuilding the session");

                try
                {
                    await TearDownScreenCastAsync();
                    await StartScreenCastAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    LastError = $"Could not rebuild the capture session: {exception.Message}";
                    Log.Warn(LastError);
                    Demote();
                    return await CaptureViaScreenshotPortalAsync(stopwatch, cancellationToken);
                }

                if (TryGrab(stopwatch) is { } retried)
                {
                    Log.Info("Session rebuilt, fast capture restored");
                    LastError = null;
                    return retried;
                }

                LastError = $"Fast capture still failing after a rebuild: {LastError}";
                Demote();
            }

            return await CaptureViaScreenshotPortalAsync(stopwatch, cancellationToken);
        }
        finally
        {
            lastUsed = DateTime.UtcNow;
            gate.Release();
        }
    }

    /// <summary>Falls back to the slow path, remembering when so the fast one is retried after the cooldown.</summary>
    void Demote()
    {
        Backend = CaptureBackend.ScreenshotPortal;
        lastFastPathFailure = DateTime.UtcNow;
    }

    /// <summary>Attempts one grab, recording why it failed rather than throwing.</summary>
    CaptureResult? TryGrab(Stopwatch stopwatch)
    {
        if (helper is null || buffer is null)
        {
            return null;
        }

        try
        {
            return CaptureResult.Raw(buffer, helper.Grab(buffer), stopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            return null;
        }
    }

    async Task<CaptureResult> CaptureViaScreenshotPortalAsync(Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        if (screenshot is null)
        {
            throw new InvalidOperationException("The capture engine was never started.");
        }

        var capture = await screenshot.CaptureAsync(interactive: false, cancellationToken);
        if (!capture.IsSuccess)
        {
            throw new InvalidOperationException($"The Screenshot portal returned {capture.Status}.");
        }

        // The portal writes into the user's Pictures folder and offers no way to stop it. Move the
        // file out immediately, into tmpfs, so Pictures only ever gains deliberate saves.
        var staged = Path.Combine(SnapShotKitPaths.Runtime, $"capture-{Guid.NewGuid():N}.png");
        File.Move(capture.Path!, staged, overwrite: true);

        try
        {
            // Decode it into the same shared frame the fast path produces. Skipping this would save
            // about a second, at the cost of the overlay silently not appearing whenever capture is
            // degraded, which is a far worse trade than it looks.
            return await FrameFile.MaterialiseAsync(staged, FramePath, stopwatch, cancellationToken);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    async Task TearDownScreenCastAsync()
    {
        helper?.Dispose();
        helper = null;

        if (screenCast is not null && sessionHandle is not null)
        {
            try
            {
                await screenCast.CloseSessionAsync(sessionHandle);
            }
            catch
            {
                // The session is being abandoned either way.
            }
        }

        sessionHandle = null;
        buffer = null;
    }

    public async ValueTask DisposeAsync()
    {
        await idleTimer.DisposeAsync();
        await TearDownScreenCastAsync();
        // The connection is owned by the daemon, not by us.
        captureThread.Dispose();
        gate.Dispose();
    }
}

/// <summary>A capture as raw BGRA pixels, however it was obtained.</summary>
public sealed class CaptureResult
{
    CaptureResult(TimeSpan elapsed) => Elapsed = elapsed;

    public TimeSpan Elapsed { get; }

    /// <summary>BGRA pixels, valid until the next capture.</summary>
    public byte[] Pixels { get; private init; } = [];

    public int Width { get; private init; }
    public int Height { get; private init; }
    public int Stride { get; private init; }

    public static CaptureResult Raw(byte[] pixels, Frame frame, TimeSpan elapsed) => new(elapsed)
    {
        Pixels = pixels,
        Width = frame.Width,
        Height = frame.Height,
        Stride = frame.Stride
    };

}
