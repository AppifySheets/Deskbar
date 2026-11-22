using Deskbar.Core.Control;

namespace Deskbar.Core.Tests;

// The line on the control pipe (ControlWire). A client and the app running beside it can be different builds, so the
// format is a contract: these pin it as the reflection-based serializer wrote it before the wire was source-generated
// (plan Task 7, for the client's start-up time), and show both camelCase and other casings are still read.
public class ControlWireTests
{
    [Fact]
    public void A_request_is_the_args_in_camel_case() =>
        Assert.Equal("{\"args\":[\"claude-hook\",\"--json\",\"{\\u0022a\\u0022:1}\"]}", ControlWire.EncodeRequest(["claude-hook", "--json", """{"a":1}"""]));

    [Fact]
    public void A_reply_is_the_exit_code_and_output_in_camel_case() =>
        Assert.Equal("""{"exitCode":2,"output":"down"}""", ControlWire.EncodeReply(new ControlReply(2, "down")));

    [Fact]
    public void Both_ends_read_what_the_other_writes_in_any_case()
    {
        Assert.Equal(["a", "b c"], ControlWire.DecodeRequest("""{"Args":["a","b c"]}""").Value);
        Assert.Equal(new ControlReply(0, "ok"), ControlWire.DecodeReply("""{"ExitCode":0,"Output":"ok"}""").Value);
        Assert.Equal(new ControlReply(3, ""), ControlWire.DecodeReply("""{"exitCode":3}""").Value);
        Assert.True(ControlWire.DecodeReply("not json").IsFailure);
        Assert.True(ControlWire.DecodeRequest("").IsFailure);
    }
}
