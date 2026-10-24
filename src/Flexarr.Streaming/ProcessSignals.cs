using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Flexarr.Streaming;

/// <summary>Pauses and resumes ffmpeg (SIGSTOP/SIGCONT) so it doesn't transcode hours ahead of the viewer, and stops it.</summary>
internal static partial class ProcessSignals
{
    /// <summary>
    /// Kills a process and anything it started. Disposing a Process, or cancelling a wait on it, leaves the
    /// program running, so cancelled work must call this.
    /// </summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    public static bool Supported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public static void Pause(int pid) => Send(pid, OperatingSystem.IsMacOS() ? 17 : 19);

    public static void Resume(int pid) => Send(pid, OperatingSystem.IsMacOS() ? 19 : 18);

    private static void Send(int pid, int signal)
    {
        if (Supported)
        {
            _ = Kill(pid, signal);
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
