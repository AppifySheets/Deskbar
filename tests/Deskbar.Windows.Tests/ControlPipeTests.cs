using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Deskbar.Core.Control;
using Deskbar.Windows.Control;

namespace Deskbar.Windows.Tests;

// The transport under the remote control, over a REAL named pipe: a unique name per test so nothing
// here can reach the Deskbar Petre is running, and nothing touches a desktop or a window.
public class ControlPipeTests
{
    static string UniquePipe() => $"Deskbar.Control.Test.{Guid.NewGuid():N}";

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_command_line_reaches_the_server_and_its_reply_comes_back()
    {
        var pipe = UniquePipe();
        IReadOnlyList<string>? received = null;
        using var server = new ControlPipeServer(pipe, args =>
        {
            received = args;
            return Task.FromResult(new ControlReply(ControlReply.Ok, "{\"done\":true}"));
        });

        var reply = await ControlClient.SendAsync(pipe, ["move", "wt login", "--title", "wt-login"], Patience);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal("{\"done\":true}", reply.Output);
        Assert.Equal(["move", "wt login", "--title", "wt-login"], received);
    }

    // Exit code 2 is the client's own conclusion: there is nobody to ask.
    [Fact]
    public async Task With_no_server_the_client_says_Deskbar_is_not_running()
    {
        var reply = await ControlClient.SendAsync(UniquePipe(), ["windows"], TimeSpan.FromSeconds(1));

        Assert.Equal(ControlReply.NotRunning, reply.ExitCode);
    }

    // ...and says it at once, not after the connect timeout. Measured before this: .NET's ConnectAsync on a name nobody
    // serves retries until the timeout runs out (2 s for every `ctl`), which a Claude Code hook would put in front of
    // every prompt while Deskbar is down (docs/superpowers/notes/2026-11-22-claude-hook-probes.md).
    [Fact]
    public async Task With_no_server_the_client_says_so_at_once()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var reply = await ControlClient.SendAsync(UniquePipe(), ["windows"], TimeSpan.FromSeconds(10));

        Assert.Equal(ControlReply.NotRunning, reply.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.ElapsedMilliseconds} ms");
    }

    // The server has ONE pipe instance and makes a new one after each reply, so for a moment between two replies no
    // pipe of that name exists. A client that asked "is there a pipe?" in that moment must not conclude Deskbar is down:
    // two hooks firing together (Stop and Notification, or two sessions) would otherwise lose one ask.
    //
    // Eight clients, a few sessions' worth. Not forty: in ONE process, .NET's ConnectAsync blocks a pool thread per
    // waiting client, forty of them starved the pool the server needs to make its next pipe, and the gap grew to
    // hundreds of milliseconds, which real clients (each its own process) cannot do to the app. Eight still fails every
    // time when the client trusts the first "not found" (PipeGap of zero, tried while writing this).
    [Fact]
    public async Task Clients_arriving_together_are_all_answered()
    {
        var pipe = UniquePipe();
        using var server = new ControlPipeServer(pipe, _ => Task.FromResult(new ControlReply(ControlReply.Ok, "fine")));
        // The first answer proves the server is up before the crowd arrives.
        Assert.Equal("fine", (await ControlClient.SendAsync(pipe, ["windows"], Patience)).Output);

        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => ControlClient.SendAsync(pipe, ["windows"], Patience))));

        Assert.All(replies, reply => Assert.Equal(ControlReply.Ok, reply.ExitCode));
    }

    // Review of PR B, M-2: a hook waits at most about half a second to connect, not 2 s, so a Deskbar whose UI thread is
    // stuck on one request costs each later prompt that much at most. The stuck request here holds the one pipe
    // instance; the hook gives up connecting and the client says "not running", which the hook turns into silence.
    [Fact]
    public async Task A_hook_waits_about_half_a_second_for_a_busy_deskbar()
    {
        var pipe = UniquePipe();
        using var release = new ManualResetEventSlim();
        using var server = new ControlPipeServer(pipe, async args =>
        {
            if (args is ["stuck"]) await Task.Run(() => release.Wait(TimeSpan.FromSeconds(10)));
            return new ControlReply(ControlReply.Ok, "fine");
        });
        var stuck = ControlClient.SendAsync(pipe, ["stuck"], Patience);
        await Task.Delay(200);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var hook = await ControlClient.RunAsync(pipe, ["claude-hook", "--json", "{}"]);
        var waited = clock.Elapsed;
        release.Set();
        await stuck;

        Assert.Equal(ControlReply.NotRunning, hook.ExitCode);
        Assert.True(waited < TimeSpan.FromMilliseconds(1500), $"waited {waited.TotalMilliseconds:0} ms");
        Assert.True(waited >= TimeSpan.FromMilliseconds(300), $"waited only {waited.TotalMilliseconds:0} ms");
    }

    // Review of Task 7, I-1: a client that gives up before its answer (claude-hook's 5 s reply timeout, scaled down here)
    // makes the server's write fail. The server used to dispose its only pipe instance and wait a second before making
    // another, so for that second the pipe NAME was missing and the next client heard "Deskbar is not running" (measured:
    // a client 450 ms after the broken one, exit 2 after 100 ms). The server now keeps its one instance and disconnects
    // it, so the next client is answered.
    [Fact]
    public async Task A_client_after_one_that_gave_up_is_still_answered()
    {
        var pipe = UniquePipe();
        using var server = new ControlPipeServer(pipe, async args =>
        {
            if (args is ["slow"]) await Task.Delay(300);
            return new ControlReply(ControlReply.Ok, "fine");
        });

        var abandoned = await ControlClient.SendAsync(pipe, ["slow"], Patience, replyTimeout: TimeSpan.FromMilliseconds(50));
        Assert.Equal(ControlReply.Failed, abandoned.ExitCode);
        // The server's handler finishes at 300 ms and its write to the client that left fails.
        await Task.Delay(450);

        var next = await ControlClient.SendAsync(pipe, ["windows"], Patience);

        Assert.Equal(ControlReply.Ok, next.ExitCode);
        Assert.Equal("fine", next.Output);
    }

    // One server answers one request after another, on its one pipe instance, disconnected after every reply.
    [Fact]
    public async Task The_server_answers_more_than_one_request()
    {
        var pipe = UniquePipe();
        var count = 0;
        using var server = new ControlPipeServer(pipe, _ =>
            Task.FromResult(new ControlReply(ControlReply.Ok, $"{Interlocked.Increment(ref count)}")));

        var first = await ControlClient.SendAsync(pipe, ["windows"], Patience);
        var second = await ControlClient.SendAsync(pipe, ["windows"], Patience);

        Assert.Equal(["1", "2"], [first.Output, second.Output]);
    }

    // --wait: "nothing matched" is retried until it matches, which is the window an agent just asked
    // an editor to open turning up a moment later. --wait itself never reaches the server.
    [Fact]
    public async Task Wait_retries_nothing_matched_until_the_server_finds_the_window()
    {
        var pipe = UniquePipe();
        var attempts = new List<IReadOnlyList<string>>();
        using var server = new ControlPipeServer(pipe, args =>
        {
            attempts.Add(args);
            return Task.FromResult(attempts.Count < 3
                ? new ControlReply(ControlReply.NothingMatched, "not yet")
                : new ControlReply(ControlReply.Ok, "moved"));
        });

        var reply = await ControlClient.RunAsync(pipe, ["move", "x", "--title", "y", "--wait", "10"]);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.Equal(3, attempts.Count);
        Assert.All(attempts, args => Assert.DoesNotContain("--wait", args));
    }

    [Fact]
    public async Task Without_wait_nothing_matched_is_answered_at_once()
    {
        var pipe = UniquePipe();
        var attempts = 0;
        using var server = new ControlPipeServer(pipe, _ =>
        {
            attempts++;
            return Task.FromResult(new ControlReply(ControlReply.NothingMatched, "not yet"));
        });

        var reply = await ControlClient.RunAsync(pipe, ["move", "x", "--title", "y"]);

        Assert.Equal(ControlReply.NothingMatched, reply.ExitCode);
        Assert.Equal(1, attempts);
    }

    // A handler that throws must cost one reply, not the server: the next request still gets answered.
    [Fact]
    public async Task A_handler_that_throws_is_reported_and_the_server_keeps_serving()
    {
        var pipe = UniquePipe();
        var calls = 0;
        using var server = new ControlPipeServer(pipe, _ => ++calls == 1
            ? throw new InvalidOperationException("boom")
            : Task.FromResult(new ControlReply(ControlReply.Ok, "fine")));

        var broken = await ControlClient.SendAsync(pipe, ["windows"], Patience);
        var next = await ControlClient.SendAsync(pipe, ["windows"], Patience);

        Assert.Equal(ControlReply.Failed, broken.ExitCode);
        Assert.Contains("boom", broken.Output);
        Assert.Equal("fine", next.Output);
    }

    // A stray client writing garbage gets a refusal rather than taking the loop down.
    [Fact]
    public async Task A_malformed_request_is_refused_and_the_server_keeps_serving()
    {
        var pipe = UniquePipe();
        using var server = new ControlPipeServer(pipe, _ => Task.FromResult(new ControlReply(ControlReply.Ok, "fine")));

        await using (var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(5000);
            var bytes = Encoding.UTF8.GetBytes("this is not json\n");
            await raw.WriteAsync(bytes);
            await raw.FlushAsync();
            using var reader = new StreamReader(raw);
            var refusal = ControlWire.DecodeReply(await reader.ReadLineAsync());
            Assert.Equal(ControlReply.Failed, refusal.Value.ExitCode);
        }

        var next = await ControlClient.SendAsync(pipe, ["windows"], Patience);
        Assert.Equal("fine", next.Output);
    }
    // Security review of fb50f0b, I1: a Low-integrity process of this user can make a pipe of Deskbar's name. Its pipe
    // is owned by this user but carries a Low mandatory label (a Low process's objects get S:(ML;;NW;;;LW)), and the
    // client used to trust it: it sent the request and printed the squatter's reply with exit 0. Here the squatter is a
    // pipe in this process given that same label; the client must refuse it and send nothing.
    [Fact]
    public async Task A_pipe_labelled_low_integrity_is_refused_and_told_nothing()
    {
        var pipe = UniquePipe();
        await using var squatter = NamedPipeServerStreamAcl.Create(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, null, HandleInheritability.None,
            PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership);
        LowLabel.Apply(squatter.SafePipeHandle);
        string? received = null;
        var serving = Task.Run(async () =>
        {
            await squatter.WaitForConnectionAsync();
            using var reader = new StreamReader(squatter, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            var read = reader.ReadLineAsync();
            if (await Task.WhenAny(read, Task.Delay(2000)) == read) received = read.Result;
        });

        var reply = await ControlClient.SendAsync(pipe, ["windows"], Patience);
        await serving;

        Assert.Equal(ControlReply.Failed, reply.ExitCode);
        Assert.Contains("low integrity", reply.Output);
        Assert.Null(received);
    }

    // Security review M4: the server's pipe denies the NETWORK logon (S-1-5-2) outright, so no remote client reaches it,
    // and otherwise grants exactly what .NET's CurrentUserOnly grants: full control to this process's default owner,
    // which owns the pipe. Read back through a real connection, so this is the ACL Windows applied.
    [Fact]
    public async Task The_servers_pipe_denies_the_network_and_keeps_current_user_only()
    {
        var pipe = UniquePipe();
        using var server = new ControlPipeServer(pipe, _ => Task.FromResult(new ControlReply(ControlReply.Ok, "fine")));
        Assert.Equal("fine", (await ControlClient.SendAsync(pipe, ["windows"], Patience)).Output);
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);

        var security = client.GetAccessControl();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        using var me = WindowsIdentity.GetCurrent();

        Assert.Equal(me.Owner, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny
            && r.IdentityReference.Value == "S-1-5-2" && r.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
        var allowed = Assert.Single(rules, r => r.AccessControlType == AccessControlType.Allow);
        Assert.Equal(me.Owner, allowed.IdentityReference);
        Assert.True(allowed.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));

        // And the same allow as .NET's own CurrentUserOnly gives, read from such a pipe the same way.
        var reference = UniquePipe();
        await using var currentUserOnly = new NamedPipeServerStream(reference, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var waiting = currentUserOnly.WaitForConnectionAsync();
        await using var probe = new NamedPipeClientStream(".", reference, PipeDirection.InOut, PipeOptions.Asynchronous);
        await probe.ConnectAsync(5000);
        await waiting;
        var theirs = probe.GetAccessControl();
        var theirAllow = Assert.Single(theirs.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>());
        Assert.Equal((theirAllow.IdentityReference, theirAllow.PipeAccessRights), (allowed.IdentityReference, allowed.PipeAccessRights));
        Assert.Equal(theirs.GetOwner(typeof(SecurityIdentifier)), security.GetOwner(typeof(SecurityIdentifier)));
    }

    // Gives a pipe this process made the Low mandatory label a Low-integrity process's pipe would have.
    static class LowLabel
    {
        public static void Apply(SafeHandle pipe)
        {
            Assert.True(ConvertStringSecurityDescriptorToSecurityDescriptorW("S:(ML;;NW;;;LW)", 1, out var descriptor, out _),
                $"SDDL: {Marshal.GetLastWin32Error()}");
            try
            {
                Assert.True(GetSecurityDescriptorSacl(descriptor, out _, out var sacl, out _), $"SACL: {Marshal.GetLastWin32Error()}");
                Assert.Equal(0u, SetSecurityInfo(pipe, KernelObject, LabelInformation, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl));
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        const int KernelObject = 6;
        const uint LabelInformation = 0x10;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, out uint size);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, out bool present, out IntPtr sacl, out bool defaulted);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern uint SetSecurityInfo(SafeHandle handle, int objectType, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr LocalFree(IntPtr memory);
    }

}
