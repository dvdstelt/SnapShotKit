using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO.MemoryMappedFiles;
using SnapShotKit.Contracts;

namespace SnapShotKit.Daemon;

/// <summary>
/// Drives the out-of-process capture helper.
///
/// The helper exists because libpipewire and the .NET runtime cannot reliably share an address
/// space for this workload: in-process, a stream reaches STREAMING and then never receives a single
/// buffer, with no error reported anywhere. Out of process it works every time. See
/// docs/spikes/005-thread-pool-capture-failure.md.
///
/// Frames cross the boundary through a shared file in XDG_RUNTIME_DIR, which is tmpfs and therefore
/// RAM, rather than being pushed through a pipe.
/// </summary>
public sealed class CaptureHelper : IDisposable
{
    readonly Process process;
    readonly MemoryMappedFile frameFile;
    readonly MemoryMappedViewAccessor frameView;

    CaptureHelper(Process process, MemoryMappedFile frameFile, MemoryMappedViewAccessor frameView)
    {
        this.process = process;
        this.frameFile = frameFile;
        this.frameView = frameView;
    }

    /// <summary>Starts the helper and waits for it to report itself ready.</summary>
    /// <param name="pipeWireFd">
    /// A descriptor from OpenPipeWireRemote. It must not be close-on-exec, which is why the portal
    /// client duplicates it: dup clears CLOEXEC, so the duplicate is inherited across exec.
    /// </param>
    [DllImport("libc", SetLastError = true)]
    static extern int fcntl(int fd, int command, int argument);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    const int F_SETFD = 2;

    public static CaptureHelper Start(int pipeWireFd, uint nodeId, int width, int height)
    {
        // Clear close-on-exec so the helper inherits the descriptor across exec. dup already leaves
        // it clear, but nothing guarantees that stays true, and a silent failure here looks exactly
        // like PipeWire refusing the connection.
        if (fcntl(pipeWireFd, F_SETFD, 0) < 0)
        {
            throw new InvalidOperationException($"Could not clear close-on-exec on descriptor {pipeWireFd}: errno {Marshal.GetLastPInvokeError()}.");
        }

        var framePath = Path.Combine(SnapShotKitPaths.Runtime, "frame.raw");
        var executable = Locate();

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add(pipeWireFd.ToString());
        startInfo.ArgumentList.Add(nodeId.ToString());
        startInfo.ArgumentList.Add(framePath);
        startInfo.ArgumentList.Add(width.ToString());
        startInfo.ArgumentList.Add(height.ToString());

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");

        // The helper has its own copy of the descriptor from the exec, so ours has done its job.
        // Keeping it open would leak one descriptor per session rebuild.
        close(pipeWireFd);

        var ready = ReadReplyWithin(process, TimeSpan.FromSeconds(10));
        if (ready != "ready")
        {
            process.Kill();
            throw new InvalidOperationException($"The capture helper did not start: {ready ?? "no output"}.");
        }

        // The helper sized and mapped this file before saying it was ready.
        var frameFile = MemoryMappedFile.CreateFromFile(framePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var frameView = frameFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        return new CaptureHelper(process, frameFile, frameView);
    }

    /// <summary>Asks the helper for one frame and copies it into <paramref name="destination"/>.</summary>
    public Frame Grab(byte[] destination)
    {
        if (process.HasExited)
        {
            throw new InvalidOperationException($"The capture helper exited with code {process.ExitCode}.");
        }

        process.StandardInput.WriteLine("grab");
        process.StandardInput.Flush();

        var reply = ReadReplyWithin(process, TimeSpan.FromSeconds(10))
            ?? throw new InvalidOperationException("The capture helper closed its output.");

        var parts = reply.Split(' ');

        if (parts[0] != "ok")
        {
            throw new InvalidOperationException(reply.Length > 4 ? reply[4..] : reply);
        }

        var frame = new Frame(
            int.Parse(parts[1]),
            int.Parse(parts[2]),
            int.Parse(parts[3]),
            long.Parse(parts[4]));

        if (frame.Size > destination.Length)
        {
            throw new InvalidOperationException($"The frame is {frame.Size} bytes but the buffer holds {destination.Length}.");
        }

        frameView.ReadArray(0, destination, 0, (int)frame.Size);
        return frame;
    }

    /// <summary>
    /// Reads one reply line, bounded. The helper answers a grab within its own three second frame
    /// timeout, so a longer silence means it is wedged, and an unbounded read here would hang the
    /// daemon's capture gate forever: every later keypress would time out as busy. Killing the
    /// helper instead surfaces a failure the engine already knows how to recover from by
    /// rebuilding the session.
    /// </summary>
    static string? ReadReplyWithin(Process process, TimeSpan timeout)
    {
        var read = process.StandardOutput.ReadLineAsync();

        if (read.Wait(timeout))
        {
            return read.Result;
        }

        try
        {
            process.Kill();
        }
        catch
        {
            // It may have exited between the timeout and the kill.
        }

        throw new InvalidOperationException($"The capture helper did not answer within {timeout.TotalSeconds:F0} s.");
    }

    static string Locate()
    {
        const string fileName = "snapshotkit-capture";

        if (Environment.GetEnvironmentVariable("SNAPSHOTKIT_CAPTURE_HELPER") is { Length: > 0 } configured)
        {
            return configured;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(beside))
        {
            return beside;
        }

        // Walk up to the repo's native directory so a development build runs without a copy step.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "native", "snapshotkit-capture", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {fileName}. Build it with src/native/snapshotkit-capture/build.sh.");
    }

    public void Dispose()
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("quit");
                process.StandardInput.Flush();

                if (!process.WaitForExit(TimeSpan.FromSeconds(2)))
                {
                    process.Kill();
                }
            }
        }
        catch
        {
            // The helper is going away either way.
        }

        frameView.Dispose();
        frameFile.Dispose();
        process.Dispose();
    }
}

public readonly record struct Frame(int Width, int Height, int Stride, long Size);
