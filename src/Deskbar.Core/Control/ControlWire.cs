using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CSharpFunctionalExtensions;

namespace Deskbar.Core.Control;

// What travels over the control pipe: one line of JSON each way, then the connection closes.
//
// The client sends its command line VERBATIM and the running app parses it. That split is deliberate:
// the exe that asks may be a different build from the one running (a release exe in Downloads asking
// the dev build, or the other way round), and if only the running side understands the commands then
// a newer client can never send a request an older app half-understands. The client's whole job is to
// forward, wait and print.
public static partial class ControlWire
{
    // Pipe names are machine-wide, unlike the Local\ single-instance mutex, so the login session is in
    // the name: two users on one PC each reach their own Deskbar. The pipe is also created
    // CurrentUserOnly, which is what actually stops one user driving another's (see ControlPipeServer).
    public static string PipeName(int sessionId) => $"Deskbar.Control.{sessionId}";

    sealed record Request(IReadOnlyList<string> Args);
    sealed record Reply(int ExitCode, string Output);

    // SOURCE-GENERATED, not reflection, because the client is a process that starts, sends one line and exits, and
    // Claude Code's hooks start one on every prompt (plan Task 7). Reflection-based System.Text.Json builds its
    // converters on first use, at start-up, every time; the generated ones are compiled in. The same web defaults
    // (camelCase names, case-insensitive reading) as the reflection options this replaced, so the line on the pipe is
    // unchanged and an older or newer build at the other end still reads it.
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Request))]
    [JsonSerializable(typeof(Reply))]
    sealed partial class WireJson : JsonSerializerContext;

    public static string EncodeRequest(IReadOnlyList<string> args) => JsonSerializer.Serialize(new Request(args), WireJson.Default.Request);

    public static Result<IReadOnlyList<string>> DecodeRequest(string? line) =>
        Decode(line, WireJson.Default.Request).Map(request => request.Args ?? []);

    public static string EncodeReply(ControlReply reply) => JsonSerializer.Serialize(new Reply(reply.ExitCode, reply.Output), WireJson.Default.Reply);

    public static Result<ControlReply> DecodeReply(string? line) =>
        Decode(line, WireJson.Default.Reply).Map(reply => new ControlReply(reply.ExitCode, reply.Output ?? ""));

    // A malformed line is an expected situation (a stray client, a truncated write), so it is a Result
    // rather than an exception escaping into the pipe loop.
    static Result<T> Decode<T>(string? line, JsonTypeInfo<T> type) where T : class
    {
        if (string.IsNullOrWhiteSpace(line)) return Result.Failure<T>("empty message");
        try
        {
            return JsonSerializer.Deserialize(line, type) is { } value
                ? value
                : Result.Failure<T>("empty message");
        }
        catch (JsonException e)
        {
            return Result.Failure<T>($"malformed message: {e.Message}");
        }
    }
}
