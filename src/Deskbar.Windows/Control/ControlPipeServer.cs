using CSharpFunctionalExtensions;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Deskbar.Core.Control;

namespace Deskbar.Windows.Control;

// The running app's end of the remote control: a named pipe that takes one command line per
// connection, hands it to `handle`, and writes the reply back.
//
// Why a pipe, and why in this process. The running instance owns state.json (last writer wins, see the
// single-instance comment in App.OnStartup), the virtual-desktop COM objects and the window list. A
// second process that edited any of those itself would race the first, so the only safe way for an
// outside caller to create a workspace or move a window is to ASK the running instance to do it.
//
// One connection at a time, served on a background task, on one pipe instance disconnected after every reply. The
// requests are a person or an agent typing commands, so there is nothing to gain from concurrency, and
// serialising them means the handler never sees two at once.
//
// WHO MAY CONNECT (Security, below). The pipe grants full control to this process's DEFAULT OWNER, and owns the pipe
// as it, which is exactly what .NET's PipeOptions.CurrentUserOnly does, so another account on the same machine cannot
// drive this one's desktops. The default owner is the user while Deskbar runs normally, but BUILTIN\Administrators
// while it runs elevated (security review of fb50f0b, I2), and then the user's own NON-elevated processes are refused
// too. That refusal is a UAC barrier worth keeping, because `start` and `restart-app` launch programs: a non-elevated
// process must not get an elevated Deskbar to start things for it. ANY FUTURE CHANGE TO THIS ACL MUST KEEP THAT
// BARRIER, so it must not grant the user's SID when the default owner is Administrators. The pipe also denies the
// NETWORK logon outright, so no remote client reaches it. The session id in the name (ControlWire.PipeName) is what
// keeps two signed-in users from colliding on the name itself.
//
// Never throws out of the loop. A malformed request, a handler that throws and a client that hangs up
// mid-way each cost that one request and nothing more, because a remote control that dies quietly
// leaves the caller with "not running" while the app is plainly on screen.
public sealed class ControlPipeServer : IDisposable
{
    // Long enough for any honest client to send one line; short enough that a client which connects and
    // then says nothing does not hold the only pipe instance for long.
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    readonly CancellationTokenSource stop = new();

    public ControlPipeServer(string pipeName, Func<IReadOnlyList<string>, Task<ControlReply>> handle, Action<string>? trace = null) =>
        _ = Task.Run(() => Serve(pipeName, handle, trace, stop.Token));

    // ONE INSTANCE FOR THE SERVER'S LIFE, disconnected between clients rather than disposed and made again.
    //
    // It used to be made again for every request, and after a failed one (a client that gave up before its answer,
    // which claude-hook's 5 s reply timeout causes, or one that hung up mid-way) the catch waited a second before
    // making the next. For that second, and for a moment after every reply, no pipe of the name existed at all, and the
    // client's quick "not running" answer (ControlClient.PipeExistsAsync, plan Task 7) told every caller Deskbar was
    // down: deskbar.cmd, deskbar-cli, an agent's `move --wait`, the hooks (review of Task 7, I-1; measured: a client
    // 450 ms after a broken request got exit 2 within 100 ms, where ConnectAsync alone connected after 216 ms). While
    // the one instance exists, a client between two requests finds it busy (ERROR_PIPE_BUSY), which WaitNamedPipe and
    // ConnectAsync both wait on, and never ERROR_FILE_NOT_FOUND.
    //
    // So: make the instance, then serve on it: wait for a client, answer it, Disconnect, wait again. Whatever one request
    // does wrong (a failed Answer included) costs that request only, and the instance stays. It is made again only when
    // it cannot be reused: at once when Disconnect failed, after a tenth of a second when waiting for a client failed
    // (ListenBackOff); the 1 s back-off is only for failing to MAKE it.
    //
    // maxNumberOfServerInstances stays 1, so a second Deskbar can never answer alongside this one. During an update
    // restart the new instance meets the old one's pipe for a moment; making it throws, the back-off waits a second,
    // and it gets the name once the old process has gone.
    // The pipe's security: what .NET's PipeOptions.CurrentUserOnly sets (full control to this process's default owner,
    // which also owns the pipe), plus a deny of everything to NETWORK, S-1-5-2 (security review of fb50f0b, M4), so a
    // client on another machine is refused whoever it logs on as. Built here rather than through CurrentUserOnly because
    // that option takes no extra rules. The header says why the default owner, and not the user, is the one allowed.
    internal static PipeSecurity Security()
    {
        using var me = WindowsIdentity.GetCurrent();
        var owner = me.Owner!;
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.SetOwner(owner);
        return security;
    }

    static async Task Serve(string pipeName, Func<IReadOnlyList<string>, Task<ControlReply>> handle, Action<string>? trace, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
            }
            catch (Exception e)
            {
                trace?.Invoke($"control pipe: not made: {e.GetType().Name}: {e.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            await using (pipe)
            {
                if (!await ServeOn(pipe, handle, trace, token).ConfigureAwait(false)) return;
            }
        }
    }

    // A pause before making the instance again after waiting for a client failed (review of PR B, M-8). Such a failure
    // is the pipe's own, not a client's, and an instance made again at once could fail the same way at once, turning the
    // loop into a busy spin with a trace line per turn. A tenth of a second caps that at ten tries a second.
    static readonly TimeSpan ListenBackOff = TimeSpan.FromMilliseconds(100);

    // Serves client after client on one instance. False when the server is stopping; true when the instance cannot be
    // reused and another must be made.
    static async Task<bool> ServeOn(NamedPipeServerStream pipe, Func<IReadOnlyList<string>, Task<ControlReply>> handle, Action<string>? trace, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception e)
            {
                trace?.Invoke($"control pipe: waiting for a client failed: {e.GetType().Name}: {e.Message}");
                try { await Task.Delay(ListenBackOff, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return false; }
                return true;
            }

            try
            {
                await Answer(pipe, handle, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception e)
            {
                trace?.Invoke($"control pipe: {e.GetType().Name}: {e.Message}");
            }

            // Back to listening for the next client. An instance that cannot be disconnected (broken some other way) is
            // made again.
            try
            {
                pipe.Disconnect();
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
            {
                trace?.Invoke($"control pipe: made again after {e.GetType().Name}: {e.Message}");
                return true;
            }
        }
        return false;
    }

    static async Task Answer(NamedPipeServerStream pipe, Func<IReadOnlyList<string>, Task<ControlReply>> handle, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(RequestTimeout);

        // UTF-8 without a BOM both ways: a BOM at the front of a line would make it not JSON.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var reader = new StreamReader(pipe, utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        var reply = await ControlWire.DecodeRequest(line).Match(
            args => Invoke(handle, args),
            error => Task.FromResult(ControlReply.Fail($"Deskbar could not read the request: {error}"))).ConfigureAwait(false);

        await writer.WriteLineAsync(ControlWire.EncodeReply(reply).AsMemory(), timeout.Token).ConfigureAwait(false);
        // Without this the reply can still be in the pipe's buffer when the stream is disposed, and the
        // client reads end-of-stream instead of an answer.
        pipe.WaitForPipeDrain();
    }

    // The handler is the app's code, so whatever it throws (synchronously or from its task) becomes a
    // refusal with the message in it rather than an exception in the loop.
    static async Task<ControlReply> Invoke(Func<IReadOnlyList<string>, Task<ControlReply>> handle, IReadOnlyList<string> args)
    {
        try
        {
            return await handle(args).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return ControlReply.Fail($"Deskbar failed while running the command: {e.Message}");
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
    }
}
