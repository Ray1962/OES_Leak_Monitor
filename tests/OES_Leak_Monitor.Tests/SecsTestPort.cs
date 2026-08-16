using System.IO;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace OES_Leak_Monitor.Tests;

/// <summary>Shared helpers for the socket-based SECS tests.</summary>
internal static class SecsTestPort
{
    /// <summary>
    /// A port the OS says is free. A fixed number risks both a busy port and the Windows
    /// excluded ranges, and these tests run in parallel with whatever else is on the machine.
    /// </summary>
    public static int Free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds. Both ends of the protocol work
    /// asynchronously, so a test waits for an outcome rather than sleeping a guessed interval.
    /// </summary>
    public static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 10)
    {
        for (var i = 0; i < seconds * 20; i++)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(50);
        }
        return condition();
    }
}

/// <summary>
/// Keeps the socket tests off each other's toes. They bind real ports and drive real protocol
/// state machines; running them concurrently makes a failure hard to attribute.
/// </summary>
[CollectionDefinition("network", DisableParallelization = true)]
public class NetworkCollection { }
