using CSharpFunctionalExtensions;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Deskbar.Core.Control;

namespace Deskbar.Windows.Control;

// The asking end of the remote control: what `Deskbar.exe ctl ...` does instead of starting the app.
//
// It forwards the command line verbatim and prints what comes back (see ControlWire for why the client
// does not parse commands itself). The two things it does decide on its own are the two things only it
// can know: that nobody answered (exit 2), and how long to keep re-asking while the answer is "nothing
// matched yet" (--wait).
public static class ControlClient
{
    // How long to wait for the running app to accept the connection. Short, because the usual reason for
    // no answer is that Deskbar is not running, and an agent should hear that quickly.
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    // How long to wait for the reply once connected. Generous, because a move runs on the app's UI thread
    // and can sit behind a bar rebuild or a COM call to the shell.
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    // The same for `claude-hook`, and short: Claude Code waits for a hook before it goes on, so a UserPromptSubmit hook
    // that waited 30 s on a Deskbar whose UI thread is stuck would hold every prompt that long. Past this the hook gives
    // up, prints nothing and the session goes on without its context (plan Task 7, review focus 1: "Deskbar is not
    // running, or slow"). Five seconds is far more than an answer from memory needs.
    static readonly TimeSpan HookReplyTimeout = TimeSpan.FromSeconds(5);

    // And the connect wait for `claude-hook` (review of PR B, M-2). The server answers one request at a time on one pipe
    // instance, so while the app's UI thread is stuck on a request every later client finds the pipe busy and waits to
    // connect. For a hook that wait sits in front of the prompt, so it is half a second rather than 2 s: a stuck Deskbar
    // costs each later prompt that much at most, and a healthy one answers a busy pipe in milliseconds.
    static readonly TimeSpan HookConnectTimeout = TimeSpan.FromMilliseconds(500);

    // How long a missing pipe is given to appear before the client concludes nobody is there (PipeExistsAsync).
    // Measured against the running app with eight clients at once: between two replies the pipe was missing for up to
    // 16 ms. 100 ms leaves room for a busier app, and is what a down Deskbar costs instead of 2 s.
    static readonly TimeSpan PipeGap = TimeSpan.FromMilliseconds(100);

    // Between --wait attempts. A window that is opening takes about a second to appear; asking twice a
    // second costs the app nothing it would notice.
    static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);

    // The whole `ctl` command: strip --wait, then ask, re-asking on "nothing matched" until the wait runs
    // out. The last answer is the one returned, so a wait that expires reports what still did not match.
    public static async Task<ControlReply> RunAsync(string pipeName, IReadOnlyList<string> args)
    {
        var split = ControlArguments.SplitWait(args);
        if (split.IsFailure) return ControlReply.Fail(split.Error);

        var (forwarded, wait) = split.Value;
        var deadline = DateTime.UtcNow + wait;
        var hook = ControlArguments.IsClaudeHook(forwarded);
        var (connectTimeout, replyTimeout) = hook ? (HookConnectTimeout, HookReplyTimeout) : (ConnectTimeout, ReplyTimeout);
        var reply = await SendAsync(pipeName, forwarded, connectTimeout, replyTimeout).ConfigureAwait(false);
        while (reply.ExitCode == ControlReply.NothingMatched && DateTime.UtcNow + RetryInterval < deadline)
        {
            await Task.Delay(RetryInterval).ConfigureAwait(false);
            reply = await SendAsync(pipeName, forwarded, connectTimeout, replyTimeout).ConfigureAwait(false);
        }
        return reply;
    }

    // "5 seconds", or "50 ms" for the short waits tests use, rather than "0 seconds".
    static string Wording(TimeSpan wait) =>
        wait.TotalSeconds >= 1 ? $"{wait.TotalSeconds:0} seconds" : $"{wait.TotalMilliseconds:0} ms";

    static readonly ControlReply NotRunning = new(ControlReply.NotRunning,
        "Deskbar is not running (nothing answered on its control pipe). Start it and try again.");

    // One request, one reply. Every way of not getting an answer becomes a ControlReply rather than an
    // exception, so the caller has exactly one thing to print and one exit code to return. `replyTimeout`
    // defaults to ReplyTimeout.
    public static async Task<ControlReply> SendAsync(string pipeName, IReadOnlyList<string> args, TimeSpan connectTimeout, TimeSpan? replyTimeout = null)
    {
        // Asked first, because .NET's ConnectAsync cannot tell "no such pipe" from "busy": on a name nobody serves
        // it retries until the timeout runs out (measured: 2 s every time, see PipeExistsAsync).
        if (!await PipeExistsAsync(pipeName).ConfigureAwait(false)) return NotRunning;

        // CurrentUserOnly on the client too: it refuses a pipe of this name owned by someone else, so a
        // process squatting on the name cannot answer in Deskbar's place.
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return NotRunning;
        }

        try
        {
            using var timeout = new CancellationTokenSource(replyTimeout ?? ReplyTimeout);
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await using var writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            await writer.WriteLineAsync(ControlWire.EncodeRequest(args).AsMemory(), timeout.Token).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            return ControlWire.DecodeReply(line).Match(
                reply => reply,
                error => ControlReply.Fail($"Deskbar sent a reply that could not be read: {error}"));
        }
        catch (OperationCanceledException)
        {
            return ControlReply.Fail($"Deskbar accepted the request but did not answer within {Wording(replyTimeout ?? ReplyTimeout)}.");
        }
        catch (IOException e)
        {
            return ControlReply.Fail($"The connection to Deskbar broke: {e.Message}");
        }
    }

    // WHETHER ANYONE SERVES THE PIPE, answered in well under a millisecond when nobody does.
    //
    // Measured on 2026-11-22 (docs/superpowers/notes/2026-11-22-claude-hook-probes.md): ConnectAsync(2000) on a name
    // nobody serves came back after 1989 to 2006 ms, every time, because .NET keeps retrying until the timeout. Every
    // `ctl` with Deskbar down cost 2 s, and a Claude Code hook would put that in front of every prompt.
    // WaitNamedPipeW on the same missing name returned false with ERROR_FILE_NOT_FOUND in 0.02 to 0.05 ms.
    //
    // "Not found" is not trusted on the first ask, because a running Deskbar can briefly have no pipe at all:
    //
    //   * an OLDER running Deskbar. Up to this pull request the server made a new pipe instance after every reply, so
    //     between two replies there was a moment with no pipe. Measured against such an app with eight real clients at
    //     once: one answer every 20 ms or so, and the pipe missing for up to 16 ms of each, so in a crowd "missing" was
    //     the usual answer. This build's client still has to work against that server (the client can be newer).
    //   * this build's server making its instance again after a Disconnect that failed (ControlPipeServer.ServeOn),
    //     which it does at once, but not in zero time;
    //   * an update: the new copy starts while the old one's pipe is going, and takes the name a moment later.
    //
    // So a missing pipe is asked about again for PipeGap. Any other answer (the pipe exists, or it exists and is busy,
    // ERROR_SEM_TIMEOUT) means someone is there, and ConnectAsync takes over with the connect timeout.
    //
    // ASKED AGAIN AT ONCE, NOT ON A TIMER. The first version paused with Task.Delay(5), which Windows' 15.6 ms timer turns
    // into about 21 ms: the same period as the server's cycle, so a client could land in the gap on every ask, six in a
    // row, and give up. Measured: 18 of 800 real clients were told Deskbar was down, and every log written showed five
    // or six "missing" answers 21 ms apart. Task.Yield asks again as soon as the thread pool comes round (a WaitNamedPipeW on a
    // missing name takes a few microseconds), so no gap the length of the measured ones can hide between two asks, and
    // a client waiting holds no pool thread the server might need. The cost is one busy core for at most PipeGap while
    // Deskbar is down, against .NET's ConnectAsync, which spins for the whole 2 s.
    //
    // Declared CharSet.Unicode with the W name: without it the call would be marshalled as ANSI and could ask about a
    // different name, the trap CLAUDE.md records for FindWindowW.
    static async Task<bool> PipeExistsAsync(string pipeName)
    {
        var path = $@"\\.\pipe\{pipeName}";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            // 1 ms, not 0: 0 means "the pipe's default wait". This only asks; it does not wait for a busy pipe.
            if (WaitNamedPipeW(path, 1) || Marshal.GetLastPInvokeError() != ErrorFileNotFound) return true;
            if (clock.Elapsed >= PipeGap) return false;
            await Task.Yield();
        }
    }

    const int ErrorFileNotFound = 2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WaitNamedPipeW(string name, uint timeout);
}
