using CSharpFunctionalExtensions;

namespace Deskbar.Core.Tasks;

// THE ONE DOOR TO THE `claude` PROGRAM. The tasks feature calls it four ways and no others (`claude --bg <message>`,
// `claude --bg --resume <id> [message]`, `claude agents --json`, and `claude attach`, which is a terminal's
// business, not this interface's). Behind an interface so the code that decides WHEN to call it is tested with a
// fake and no test ever starts Claude.
public interface IClaudeCli
{
    // Runs claude with these arguments (one by one, so a message with spaces and quotes arrives whole) in this
    // folder (null: Deskbar's own). Success is what the program wrote to stdout, trimmed of trailing whitespace,
    // when it exited with 0. Any other end is a failure whose text carries the exit code, stdout and stderr:
    // `claude --bg` in a folder that was never trusted exits 1 with nothing on stdout and its reason on stderr,
    // so the reason has to travel. A program that is missing, cannot start, or runs past `timeout` is a failure
    // too, never an exception.
    Task<Result<string>> Run(IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout);
}
