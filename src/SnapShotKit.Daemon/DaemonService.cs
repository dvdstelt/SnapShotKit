using System.Diagnostics;
using SnapShotKit.Contracts;
using SixLabors.ImageSharp;
using Tmds.DBus.Protocol;

namespace SnapShotKit.Daemon;

/// <summary>Exposes the daemon on the session bus. This is the only way in.</summary>
public sealed class DaemonService(CaptureEngine engine) : IPathMethodHandler
{
    // One capture at a time. The helper writes every frame into the same shared file, so starting a
    // second capture while an overlay is still open would change the picture under the user.
    readonly SemaphoreSlim gate = new(1, 1);

    public string Path => SnapShotKitDBus.ObjectPath;

    public bool HandlesChildPaths => false;

    public async ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([IntrospectionXml.DBusIntrospectable], []);
            return;
        }

        if (context.Request.InterfaceAsString != SnapShotKitDBus.Interface)
        {
            context.ReplyUnknownMethodError();
            return;
        }

        switch (context.Request.MemberAsString)
        {
            case SnapShotKitDBus.Capture:
                await HandleCaptureAsync(context, withOverlay: true);
                break;

            case SnapShotKitDBus.CaptureFullScreen:
                await HandleCaptureAsync(context, withOverlay: false);
                break;

            case SnapShotKitDBus.CaptureDelayed:
                await HandleDelayedAsync(context);
                break;

            case SnapShotKitDBus.OpenEditor:
                EditorLauncher.TryOpen();
                ReplyEmpty(context);
                break;

            case SnapShotKitDBus.OpenSnapshots:
                EditorLauncher.TryOpenFolder(SnapShotKitPaths.Snapshots);
                ReplyEmpty(context);
                break;

            case SnapShotKitDBus.Status:
                HandleStatus(context);
                break;

            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    /// <summary>
    /// Waits, then captures.
    ///
    /// The wait is here rather than in the caller so that whatever asked for it can get on with
    /// closing itself: the reason to delay a capture is almost always to photograph a menu or a
    /// hover that would vanish the moment you touched the keyboard.
    /// </summary>
    async ValueTask HandleDelayedAsync(MethodContext context)
    {
        var seconds = context.Request.GetBodyReader().ReadUInt32();

        // Bounded. A delay measured in hours is a mistake rather than a request, and the daemon
        // would sit on the capture gate for the whole of it.
        var wait = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));

        Log.Info($"Capturing in {wait.TotalSeconds:F0} s");
        await Task.Delay(wait, context.RequestAborted);

        await HandleCaptureAsync(context, withOverlay: true);
    }

    static void ReplyEmpty(MethodContext context)
    {
        using var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    async ValueTask HandleCaptureAsync(MethodContext context, bool withOverlay)
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(30), context.RequestAborted))
        {
            context.ReplyError($"{SnapShotKitDBus.Interface}.Busy", "Another capture is still in progress.");
            return;
        }

        try
        {
            string savedPath;

            try
            {
                var capture = await engine.CaptureAsync(context.RequestAborted);

                CaptureRegion? region = null;

                if (withOverlay)
                {
                    var answer = await OverlayClient.AskAsync(CaptureEngine.FramePath,
                        capture.Width, capture.Height, capture.Stride, context.RequestAborted);

                    if (answer.Choice == OverlayChoice.Cancelled)
                    {
                        Log.Info("Capture cancelled");
                        Reply(context, string.Empty);
                        return;
                    }

                    region = answer.Region;

                    if (answer.Choice == OverlayChoice.Copy)
                    {
                        using var copied = CaptureWriter.Compose(capture, region);
                        using var encoded = new MemoryStream();
                        await copied.SaveAsPngAsync(encoded, context.RequestAborted);

                        if (WaylandClipboard.TryCopyPng(encoded.ToArray(), out var clipboardError))
                        {
                            Log.Info($"Captured via {engine.Backend} in {capture.Elapsed.TotalMilliseconds:F0} ms, " +
                                $"{answer.Region.Width}x{answer.Region.Height} copied to the clipboard");
                        }
                        else
                        {
                            Log.Warn($"Could not copy to the clipboard: {clipboardError}");
                        }

                        // Nothing was written, so there is no path to hand back.
                        Reply(context, string.Empty);
                        return;
                    }

                    if (answer.Choice == OverlayChoice.Edit)
                    {
                        var editing = Stopwatch.StartNew();
                        using var image = CaptureWriter.Compose(capture, region);
                        savedPath = await SnapshotWriter.WriteAsync(image, answer.Region,
                            capture.Width, capture.Height, context.RequestAborted);
                        editing.Stop();

                        Log.Info($"Captured via {engine.Backend} in {capture.Elapsed.TotalMilliseconds:F0} ms, " +
                            $"{answer.Region.Width}x{answer.Region.Height} snapshot written in {editing.ElapsedMilliseconds} ms -> {savedPath}");
                        if (EditorLauncher.TryOpen(savedPath))
                        {
                            Log.Info("Opened the snapshot in the editor");
                        }

                        Reply(context, savedPath);
                        return;
                    }
                }

                var saving = Stopwatch.StartNew();
                savedPath = await CaptureWriter.SaveAsync(capture, region, context.RequestAborted);
                saving.Stop();

                Log.Info($"Captured via {engine.Backend} in {capture.Elapsed.TotalMilliseconds:F0} ms, " +
                    $"{(region is { } r ? $"cropped to {r.Width}x{r.Height}, " : "full screen, ")}" +
                    $"saved in {saving.ElapsedMilliseconds} ms -> {savedPath}");
            }
            catch (Exception exception)
            {
                Log.Error($"Capture failed: {exception.Message}");
                context.ReplyError($"{SnapShotKitDBus.Interface}.CaptureFailed", exception.Message);
                return;
            }

            Reply(context, savedPath);
        }
        finally
        {
            gate.Release();
        }

        // MessageWriter is a ref struct and cannot cross an await, so replies are built here.
        static void Reply(MethodContext context, string path)
        {
            using var writer = context.CreateReplyWriter("s");
            writer.WriteString(path);
            context.Reply(writer.CreateMessage());
        }
    }

    void HandleStatus(MethodContext context)
    {
        using var writer = context.CreateReplyWriter("a{sv}");

        writer.WriteDictionary(new Dictionary<string, VariantValue>
        {
            ["backend"] = VariantValue.String(engine.Backend.ToString()),
            ["fast"] = VariantValue.Bool(engine.Backend == CaptureBackend.ScreenCast),
            ["consent_not_remembered"] = VariantValue.Bool(engine.ConsentNotRemembered),
            ["stream_size"] = VariantValue.String(engine.StreamSize is { } size ? $"{size.Width}x{size.Height}" : "unknown"),
            ["session_held"] = VariantValue.Bool(engine.SessionHeld),
            ["exports"] = VariantValue.String(SnapShotKitPaths.Exports),
            ["snapshots"] = VariantValue.String(SnapShotKitPaths.Snapshots),
            ["last_error"] = VariantValue.String(engine.LastError ?? string.Empty)
        });

        context.Reply(writer.CreateMessage());
    }
}
