using System.Diagnostics;
using System.Runtime.InteropServices;
using Deskbar.Core.Control;
using Deskbar.Windows.Control;

namespace Deskbar.Windows.Tests;

// A CONTROL PIPE SERVER FOR A TEST, returned only once its pipe exists.
//
// ControlPipeServer makes its pipe on a Task.Run, so for a moment after the constructor returns there is no pipe of
// that name. The client gives a missing pipe 100 ms (ControlClient.PipeGap) before it answers "Deskbar is not running"
// (exit 2), which is the right call against a real Deskbar, whose server has been up since start. A test makes the
// server and sends at once, so on a busy CI runner the Task.Run could lose that race: ControlPipeTests'
// Without_wait_nothing_matched_is_answered_at_once got 2 for 3 in both the CI run of #430 and the Release run of its
// merge, and the release did not go out. Waiting here for the pipe takes the race out of the tests and leaves the
// client's rule as it is.
static class TestPipes
{
    // Long enough for any runner; a pipe that has not appeared by then is a real failure, and says so.
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public static ControlPipeServer Serving(string pipe, Func<IReadOnlyList<string>, Task<ControlReply>> handle)
    {
        var server = new ControlPipeServer(pipe, handle);
        var path = $@"\\.\pipe\{pipe}";
        var clock = Stopwatch.StartNew();
        // The question the client asks (ControlClient.PipeExistsAsync): only "not found" means the pipe is not there
        // yet. 1 ms, not 0, because 0 means the pipe's default wait; this only asks and never takes the instance.
        while (!WaitNamedPipeW(path, 1) && Marshal.GetLastPInvokeError() == ErrorFileNotFound)
        {
            if (clock.Elapsed > Limit)
            {
                server.Dispose();
                throw new TimeoutException($"The test's control pipe {pipe} did not appear within {Limit.TotalSeconds} s.");
            }
            Thread.Sleep(1);
        }
        return server;
    }

    const int ErrorFileNotFound = 2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WaitNamedPipeW(string name, uint timeout);
}
