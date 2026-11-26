using System.IO;
using CSharpFunctionalExtensions;
using Deskbar.Core.Control;
using Deskbar.Windows.Control;

namespace Deskbar.Windows.Tests.Control;

// THE PERMISSION HOOK'S CALL REACHES DESKBAR ITSELF (the re-review's N1; PipeTrust.CheckServerImage). A program of the
// same user can serve Deskbar's pipe name while Deskbar is closed; owner and label cannot tell it apart. For
// `claude-permission` the client therefore also requires the pipe's server process to be its own executable (or
// Deskbar.exe beside it, the Store's deskbar-cli case) before it writes a byte. The process lookup is a fake here; the
// real lookup is exercised by the in-process server, whose process is this test host, the client's own executable.
public class PermissionPipeTrustTests
{
    static string UniquePipe() => $"Deskbar.Control.Test.{Guid.NewGuid():N}";

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    const string Own = @"C:\Users\x\AppData\Local\Deskbar\Deskbar.exe";

    static readonly string[] Call = ["claude-permission", "--slot", "req-1-2", "--json", "{}"];

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Deskbar\Deskbar.exe", true)]
    [InlineData(@"c:\users\X\appdata\local\deskbar\DESKBAR.EXE", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Deskbar\.\Deskbar.exe", true)]
    [InlineData(@"C:\Users\x\AppData\Local\Deskbar\evil.exe", false)]
    [InlineData(@"C:\Users\x\Downloads\Deskbar.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_clients_own_executable_counts(string? server, bool same) =>
        Assert.Equal(same, PipeTrust.SameImage(server, PipeTrust.TrustedImages(Own)));

    // The Store package: the client is deskbar-cli.exe, the server Deskbar.exe in the same package folder.
    [Fact]
    public void Deskbar_beside_the_store_client_counts() =>
        Assert.True(PipeTrust.SameImage(@"C:\Program Files\WindowsApps\Deskbar_1\Deskbar.exe",
            PipeTrust.TrustedImages(@"C:\Program Files\WindowsApps\Deskbar_1\deskbar-cli.exe")));

    [Fact]
    public void A_path_that_cannot_be_read_on_either_side_is_refused()
    {
        Assert.True(PipeTrust.CheckServerImage(Result.Failure<string>("access denied"), Own).IsFailure);
        Assert.True(PipeTrust.CheckServerImage(Own, null).IsFailure);
        Assert.True(PipeTrust.CheckServerImage(Own, "").IsFailure);
        Assert.True(PipeTrust.CheckServerImage(Own, Own).IsSuccess);
    }

    // A server of the right name and user but another program: the client refuses before writing the request (the server
    // never hears the slot), and the verb prints nothing on stdout, so the script falls to the terminal dialog.
    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\squatter.exe")]
    [InlineData(null)]
    public void A_server_that_is_not_deskbar_is_refused_and_the_verb_prints_nothing(string? image)
    {
        var pipe = UniquePipe();
        var heard = 0;
        using var server = TestPipes.Serving(pipe, _ =>
        {
            Interlocked.Increment(ref heard);
            return Task.FromResult(new ControlReply(ControlReply.Ok, "0123456789abcdef0123456789abcdef\r\n"));
        });
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = ControlConsole.Run(Call, pipe,
            (name, args) => ControlClient.SendAsync(name, args, Patience, serverImage: _ => image is null ? Result.Failure<string>("access denied") : image, ownImage: Own),
            stdout, stderr, callerFolder: @"C:\repos\x");

        Assert.Equal("", stdout.ToString());
        Assert.NotEqual(ControlReply.Ok, exit);
        Assert.Equal(0, heard);
    }

    // The real lookup, against the in-process server: its process is this test host, the client's own executable.
    [Fact]
    public async Task The_real_server_process_is_accepted()
    {
        var pipe = UniquePipe();
        using var server = TestPipes.Serving(pipe, _ => Task.FromResult(new ControlReply(ControlReply.Ok, "0123456789abcdef0123456789abcdef\r\n")));

        var reply = await ControlClient.SendAsync(pipe, Call, Patience);

        Assert.Equal(ControlReply.Ok, reply.ExitCode);
        Assert.StartsWith("0123456789abcdef", reply.Output);
        Assert.True(PipeTrust.SameImage(Environment.ProcessPath, PipeTrust.TrustedImages(Environment.ProcessPath)));
    }

    // Every other verb keeps the owner and label check alone: a server of another image still answers `windows`.
    [Fact]
    public async Task Other_verbs_are_unchanged()
    {
        var pipe = UniquePipe();
        using var server = TestPipes.Serving(pipe, _ => Task.FromResult(new ControlReply(ControlReply.Ok, "fine")));

        var reply = await ControlClient.SendAsync(pipe, ["windows"], Patience, serverImage: _ => @"C:\elsewhere\other.exe", ownImage: Own);

        Assert.Equal("fine", reply.Output);
    }

}
