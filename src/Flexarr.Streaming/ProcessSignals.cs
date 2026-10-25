using System.ComponentModel;
using System.Diagnostics;

namespace Flexarr.Streaming;

/// <summary>Stops ffmpeg and whatever it started.</summary>
internal static class ProcessSignals
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

}
