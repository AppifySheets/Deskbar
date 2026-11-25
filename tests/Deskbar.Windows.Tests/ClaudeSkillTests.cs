using System.IO;
using Deskbar.App;
using Deskbar.Windows.Control;

namespace Deskbar.Windows.Tests;

// The Claude Code skill Deskbar keeps in ~/.claude/skills/deskbar (ClaudeSkill). Petre: "add a skill for claude per
// user". Each test gets its own scratch home folder, so nothing touches the real ~/.claude.
public sealed class ClaudeSkillTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "deskbar-skill-" + Guid.NewGuid().ToString("N"));

    string Skill => Path.Combine(home, ".claude", "skills", "deskbar", "SKILL.md");

    // Where deskbar.cmd and the hook script live, inside the scratch home.
    string Deskbar => Path.Combine(home, "AppData", "Deskbar");

    string Settings => Path.Combine(home, ".claude", "settings.json");

    public ClaudeSkillTests() => Directory.CreateDirectory(home);

    // --- the hooks (ClaudeHooks) ----------------------------------------------------------------

    // The user's own hooks, the shape Petre's bell has.
    const string WithABell = """
        {
          "model": "opus",
          "hooks": {
            "Stop": [ { "matcher": "", "hooks": [ { "type": "command", "command": "cmd.exe /c C:\\bell.bat" } ] } ]
          }
        }
        """;

    static string[] Commands(string json, string hookEvent) =>
        System.Text.Json.Nodes.JsonNode.Parse(json)!["hooks"]?[hookEvent]?.AsArray()
            .SelectMany(e => e!["hooks"]!.AsArray().Select(h => h!["command"]!.GetValue<string>())).ToArray() ?? [];

    // Added beside the user's own hooks on both events, with the script they run written next to deskbar.cmd.
    [Fact]
    public void The_hooks_are_added_beside_the_users_own()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Settings, WithABell);

        ClaudeSkill.Sync(true, true, home, Deskbar);

        var json = File.ReadAllText(Settings);
        Assert.Equal(3, Commands(json, "Stop").Length);
        Assert.Contains(@"cmd.exe /c C:\bell.bat", Commands(json, "Stop"));
        Assert.Single(Commands(json, "Notification"));
        Assert.Contains("claude-hook.cmd", Commands(json, "Notification")[0]);
        Assert.Equal("opus", System.Text.Json.Nodes.JsonNode.Parse(json)!["model"]!.GetValue<string>());
        Assert.Contains("@exit /b 0", File.ReadAllText(Path.Combine(Deskbar, "claude-hook.cmd")));
        // The file as it was, kept once.
        Assert.Equal(WithABell, File.ReadAllText(Settings + ".deskbar-backup"));
    }

    // A second start changes nothing, so the file is not rewritten.
    [Fact]
    public void The_hooks_are_not_added_twice()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Settings, WithABell);
        ClaudeSkill.Sync(true, true, home, Deskbar);
        var first = File.ReadAllText(Settings);
        File.SetLastWriteTimeUtc(Settings, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        ClaudeSkill.Sync(true, true, home, Deskbar);

        Assert.Equal(first, File.ReadAllText(Settings));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Settings));
    }

    // Off takes out Deskbar's entries only, and an event left empty by that goes too.
    [Fact]
    public void Turning_it_off_removes_only_deskbars_hooks()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Settings, WithABell);
        ClaudeSkill.Sync(true, true, home, Deskbar);

        ClaudeSkill.Sync(false, true, home, Deskbar);

        var json = File.ReadAllText(Settings);
        Assert.Equal([@"cmd.exe /c C:\bell.bat"], Commands(json, "Stop"));
        Assert.Null(System.Text.Json.Nodes.JsonNode.Parse(json)!["hooks"]!["Notification"]);
    }

    // An entry an older Deskbar wrote, `cmd.exe /c "...claude-hook.cmd"`, never ran under Claude Code (see ClaudeSkill's
    // header). The next start swaps it for the current command, once, and leaves the bell beside it alone.
    [Fact]
    public void An_older_cmd_exe_hook_is_replaced()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        var old = $"cmd.exe /c \"{Path.Combine(Deskbar, "claude-hook.cmd")}\"";
        File.WriteAllText(Settings, $$"""
            {
              "hooks": {
                "Stop": [
                  { "matcher": "", "hooks": [ { "type": "command", "command": "cmd.exe /c C:\\bell.bat" } ] },
                  { "hooks": [ { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize(old)}} } ] }
                ]
              }
            }
            """);

        ClaudeSkill.Sync(true, true, home, Deskbar);

        var json = File.ReadAllText(Settings);
        // With the task events on, Stop also holds the comment waiter after the ask entry.
        Assert.Equal([@"cmd.exe /c C:\bell.bat", ClaudeSkill.HookCommand(Path.Combine(Deskbar, "claude-hook.cmd")),
                ClaudeSkill.HookCommand(Path.Combine(Deskbar, "claude-wait-hook.cmd"))],
            Commands(json, "Stop"));
    }

    // A Stop hook's JSON, cut down to the fields claude-hook reads.
    const string StopJson = """{"session_id":"92943450","cwd":"C:\\repos\\x","hook_event_name":"Stop"}""";

    // The hook command Deskbar wrote for one event.
    string HookCommandFor(string hookEvent) => Commands(File.ReadAllText(Settings), hookEvent).Single(c => c.Contains("claude-hook.cmd") || c.Contains("claude-task-hook.cmd"));

    // Runs a hook command the way Claude Code runs one on Windows: Git for Windows' bash, `-c <command>`, the hook's JSON
    // on stdin. `environment` sets a variable (a value) or takes it away (null), so the hook does not inherit the
    // CLAUDE_* values of whatever Claude session runs this test. A hook that does not finish is killed, tree and all,
    // so a broken run cannot leave a process behind on the test desktop.
    static (int ExitCode, string Stdout, string Stderr) RunThroughGitBash(string command, string stdin, IReadOnlyDictionary<string, string?> environment)
    {
        // Git for Windows' own bash, by path: a bare `bash` on PATH can be WSL's, which is not what Claude Code uses.
        var bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        Assert.True(File.Exists(bash), $"Git Bash not found at {bash}");
        var start = new System.Diagnostics.ProcessStartInfo(bash)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        environment.ToList().ForEach(variable =>
        {
            if (variable.Value is null) start.Environment.Remove(variable.Key);
            else start.Environment[variable.Key] = variable.Value;
        });
        using var hook = System.Diagnostics.Process.Start(start)!;
        var stdout = hook.StandardOutput.ReadToEndAsync();
        var stderr = hook.StandardError.ReadToEndAsync();
        hook.StandardInput.Write(stdin);
        hook.StandardInput.Close();
        if (!hook.WaitForExit(30_000))
        {
            hook.Kill(entireProcessTree: true);
            Assert.Fail("the hook did not finish");
        }
        return (hook.ExitCode, stdout.Result, stderr.Result);
    }

    // What a failed run looked like, for an assertion's message: the exit code and both streams. Without the streams a
    // CI failure said only "127" or "" (PR #423's first run), and nothing about why.
    static string Explain(int exitCode, string stdout, string stderr) =>
        $"exit {exitCode}; stdout: [{stdout}]; stderr: [{stderr}]";

    // The hook scripts send stderr to nul, so a hook that printed nothing says nothing about why. This runs the shim
    // directly through Git Bash with the same pipe and reports what it said, for an assertion's message.
    string ShimDiagnosis(string exe, string pipe)
    {
        var shim = Path.Combine(Deskbar, "deskbar.cmd");
        var (exitCode, stdout, stderr) = RunThroughGitBash($"\"{shim.Replace('\\', '/')}\" version", "",
            new Dictionary<string, string?> { ["DESKBAR_CONTROL_PIPE"] = pipe });
        return $"exe exists: {File.Exists(exe)} ({exe}); shim exists: {File.Exists(shim)} ({shim}); "
            + $"shim text: [{(File.Exists(shim) ? File.ReadAllText(shim) : "")}]; direct run: {Explain(exitCode, stdout, stderr)}";
    }

    // The command Deskbar writes, run the way Claude Code runs a hook. A stand-in deskbar.cmd records the arguments it
    // was called with and the stdin it was handed, and answers a line the way the real one answers a session. The old
    // `cmd.exe /c` command fails this: Git Bash turns /c into C:/, cmd.exe sits at a prompt eating stdin, and the
    // stand-in is never called. What this pins: the session's folder, entrypoint and attended flag reach Deskbar as
    // arguments, the hook's JSON reaches it as stdin through cmd's `call`, and what Deskbar prints reaches the session.
    [Fact]
    public void The_hook_reaches_deskbar_when_git_bash_runs_it()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        ClaudeSkill.Sync(true, true, home, Deskbar);
        var called = Path.Combine(home, "called.txt");
        var input = Path.Combine(home, "input.txt");
        File.WriteAllText(Path.Combine(Deskbar, "deskbar.cmd"),
            $"@echo %*> \"{called}\"\r\n@findstr \"^\" > \"{input}\"\r\n@echo context for the session\r\n");
        var project = Path.Combine(home, "My Project");

        var (exitCode, stdout, stderr) = RunThroughGitBash(HookCommandFor("Stop"), StopJson, new Dictionary<string, string?>
        {
            ["CLAUDE_PROJECT_DIR"] = project,
            ["CLAUDE_CODE_ENTRYPOINT"] = "claude-vscode",
            ["CLAUDE_CODE_SESSION_ATTENDED"] = "1",
        });

        Assert.True(exitCode == 0, Explain(exitCode, stdout, stderr));
        Assert.True(File.Exists(called), "the hook never called deskbar.cmd");
        Assert.Equal($"claude-hook --entry \"claude-vscode\" --attended \"1\" --job \"\" --from \"{project}\"", File.ReadAllText(called).Trim());
        Assert.Equal(StopJson, File.ReadAllText(input).Trim());
        Assert.Equal("context for the session", stdout.Trim());
    }

    // Review focus 1 of the plan: Deskbar is not running when a hook fires. The real client (the Deskbar.exe beside the
    // tests, through the shim Deskbar writes) asks a pipe nobody serves (DESKBAR_CONTROL_PIPE, ControlCommandLine), so
    // the session must see nothing at all and the hook must exit 0: a Stop hook that exits 2 tells Claude to keep going.
    [Theory]
    [InlineData("Stop")]
    [InlineData("SessionStart")]
    public void A_hook_with_deskbar_down_prints_nothing_and_exits_0(string hookEvent)
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        ClaudeSkill.Sync(true, true, home, Deskbar);
        var exe = Path.Combine(AppContext.BaseDirectory, "Deskbar.exe");
        Assert.True(File.Exists(exe), $"no Deskbar.exe beside the tests at {exe}");
        ControlCommandLine.WriteShim(Deskbar, exe);

        var (exitCode, stdout, stderr) = RunThroughGitBash(HookCommandFor(hookEvent),
            StopJson.Replace("\"Stop\"", $"\"{hookEvent}\""), new Dictionary<string, string?>
            {
                ["CLAUDE_PROJECT_DIR"] = Path.Combine(home, "My Project"),
                ["CLAUDE_CODE_ENTRYPOINT"] = "claude-vscode",
                ["CLAUDE_CODE_SESSION_ATTENDED"] = "1",
                ["DESKBAR_CONTROL_PIPE"] = $"Deskbar.Control.Test.{Guid.NewGuid():N}",
            });

        Assert.True(exitCode == 0, Explain(exitCode, stdout, stderr));
        Assert.Equal("", stdout);
    }

    // The whole way with the real client: Git Bash, the hook script, the shim Deskbar writes, the Deskbar.exe beside the
    // tests acting as `ctl`, and a pipe server standing in for the running app (DESKBAR_CONTROL_PIPE points the client
    // at it, so Petre's own Deskbar is never asked). What the stand-in does not prove, this does: a GUI exe started
    // through cmd's `call` really receives the hook's stdin, and its answer reaches the session's stdout.
    [Fact]
    public void The_real_client_hands_the_hook_to_deskbar_and_its_answer_to_the_session()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        ClaudeSkill.Sync(true, true, home, Deskbar);
        var exe = Path.Combine(AppContext.BaseDirectory, "Deskbar.exe");
        ControlCommandLine.WriteShim(Deskbar, exe);
        var pipe = $"Deskbar.Control.Test.{Guid.NewGuid():N}";
        IReadOnlyList<string>? received = null;
        using var server = TestPipes.Serving(pipe, args =>
        {
            received = args;
            return Task.FromResult(new global::Deskbar.Core.Control.ControlReply(0, "Your task is X."));
        });
        var project = Path.Combine(home, "My Project");

        var (exitCode, stdout, stderr) = RunThroughGitBash(HookCommandFor("SessionStart"), StopJson.Replace("Stop", "SessionStart"),
            new Dictionary<string, string?>
            {
                ["CLAUDE_PROJECT_DIR"] = project,
                ["CLAUDE_CODE_ENTRYPOINT"] = "claude-vscode",
                ["CLAUDE_CODE_SESSION_ATTENDED"] = "1",
                ["DESKBAR_CONTROL_PIPE"] = pipe,
            });

        // The diagnosis runs the shim, which asks the same test server, so it runs only once the hook has failed.
        if (exitCode != 0 || stdout.Trim() != "Your task is X.")
            Assert.Fail($"hook: {Explain(exitCode, stdout, stderr)}; {ShimDiagnosis(exe, pipe)}");
        Assert.Equal(["claude-hook", "--entry", "claude-vscode", "--attended", "1", "--job", "", "--from", project,
            "--json", StopJson.Replace("Stop", "SessionStart")], received);
    }

    // Review of PR B, I-1, the whole way: a session in Git Bash sends free text with quotes and cmd's operators through
    // the real shim and the real Deskbar.exe as `-` and a heredoc. It reaches the app as written, and cmd runs none of it
    // (on the command line it ran `echo INJECTED` and tried a redirect into pwned.txt).
    [Fact]
    public void Free_text_on_stdin_reaches_deskbar_intact_through_the_shim()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Deskbar.exe");
        ControlCommandLine.WriteShim(Deskbar, exe);
        var pipe = $"Deskbar.Control.Test.{Guid.NewGuid():N}";
        IReadOnlyList<string>? received = null;
        using var server = TestPipes.Serving(pipe, args =>
        {
            received = args;
            return Task.FromResult(new global::Deskbar.Core.Control.ControlReply(0, "{}"));
        });
        const string text = "checking \"x & echo INJECTED\" now | set \"a > pwned.txt\" first <in";
        var shim = Path.Combine(Deskbar, "deskbar.cmd").Replace('\\', '/');

        var (exitCode, stdout, stderr) = RunThroughGitBash($"cd \"{home.Replace('\\', '/')}\" && \"{shim}\" task comment - --session s1",
            text + "\n", new Dictionary<string, string?> { ["DESKBAR_CONTROL_PIPE"] = pipe });

        Assert.True(exitCode == 0, Explain(exitCode, stdout, stderr));
        Assert.DoesNotContain("INJECTED", stdout);
        Assert.False(File.Exists(Path.Combine(home, "pwned.txt")));
        Assert.NotNull(received);
        Assert.Equal(["task", "comment", text, "--session", "s1"], received!.Take(5));
    }

    // Review of PR B, I-3: cmd reads a batch file in the OEM code page and the scripts were written UTF-8, so a profile
    // path with a letter outside ASCII garbled the shim's absolute path and every hook died silently. The scripts call
    // the shim beside them by `%~dp0`, so neither holds a path at all.
    [Fact]
    public void The_hook_scripts_hold_no_absolute_path()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));

        ClaudeSkill.Sync(true, true, home, Deskbar);

        Assert.All(new[] { "claude-hook.cmd", "claude-task-hook.cmd" }, name =>
        {
            var script = File.ReadAllText(Path.Combine(Deskbar, name));
            Assert.Contains("\"%~dp0deskbar.cmd\"", script);
            Assert.DoesNotContain(":\\", script);
            Assert.DoesNotContain(":/", script);
            Assert.DoesNotContain(home, script, StringComparison.OrdinalIgnoreCase);
            Assert.True(script.All(c => c < 128), $"{name} is not ASCII");
        });
    }

    // A background session's hooks run with ATTENDED=0, so Deskbar needs its job directory to count it as attended
    // (ClaudeHookEvent). Both scripts pass CLAUDE_JOB_DIR on as --job.
    [Fact]
    public void The_hook_scripts_pass_the_job_directory()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));

        ClaudeSkill.Sync(true, true, home, Deskbar);

        Assert.All(new[] { "claude-hook.cmd", "claude-task-hook.cmd" }, name =>
            Assert.Contains("--job \"%CLAUDE_JOB_DIR%\"", File.ReadAllText(Path.Combine(Deskbar, name))));
    }

    static string[] Ours(string json, string hookEvent) =>
        Commands(json, hookEvent).Where(c => c.Contains("claude-hook.cmd") || c.Contains("claude-task-hook.cmd")).ToArray();

    static readonly string[] AskEvents = ["Notification", "Stop"];
    static readonly string[] TaskEvents = ["SessionStart", "UserPromptSubmit", "SessionEnd"];

    // The user's own hooks on a task event too, to show they stay put while Deskbar's come and go.
    const string WithABellAndAStartHook = """
        {
          "hooks": {
            "Stop": [ { "matcher": "", "hooks": [ { "type": "command", "command": "cmd.exe /c C:\\bell.bat" } ] } ],
            "SessionStart": [ { "hooks": [ { "type": "command", "command": "python title.py" } ] } ]
          }
        }
        """;

    // The setting "Claude Code sessions keep their tasks" (AppState.ClaudeTasks) adds and removes exactly the three task
    // events' entries; the two ask entries and the user's own hooks stay through both.
    [Fact]
    public void The_task_hooks_come_and_go_with_their_setting()
    {
        Settled(WithABellAndAStartHook);

        ClaudeSkill.Sync(true, true, home, Deskbar);
        var on = File.ReadAllText(Settings);
        Assert.All(AskEvents.Concat(TaskEvents), e => Assert.Single(Ours(on, e)));

        ClaudeSkill.Sync(true, false, home, Deskbar);
        var off = File.ReadAllText(Settings);
        Assert.All(AskEvents, e => Assert.Single(Ours(off, e)));
        Assert.All(TaskEvents, e => Assert.Empty(Ours(off, e)));
        Assert.Equal(["python title.py"], Commands(off, "SessionStart"));
        Assert.Contains(@"cmd.exe /c C:\bell.bat", Commands(off, "Stop"));
        Assert.Null(System.Text.Json.Nodes.JsonNode.Parse(off)!["hooks"]!["UserPromptSubmit"]);

        ClaudeSkill.Sync(true, true, home, Deskbar);
        Assert.All(TaskEvents, e => Assert.Single(Ours(File.ReadAllText(Settings), e)));
    }

    // A PULL REQUEST GOES ON ITS TASK (task e7aa49b1): with the task events come two PostToolUse entries, one per shell
    // tool, each filtered by Claude Code's `if` to `gh pr create`, on the task script; with the setting off both go, and a
    // second sync writes nothing new.
    [Fact]
    public void The_pull_request_hooks_come_and_go_with_the_task_events()
    {
        Settled(WithABellAndAStartHook);

        ClaudeSkill.Sync(true, true, home, Deskbar);
        var on = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Settings))!["hooks"]!["PostToolUse"]!.AsArray();
        Assert.Equal(["Bash", "PowerShell"], on.Select(e => (string)e!["matcher"]!));
        Assert.Equal(["Bash(gh pr create*)", "PowerShell(gh pr create*)"], on.Select(e => (string)e!["hooks"]![0]!["if"]!));
        Assert.All(on, e => Assert.Contains("claude-task-hook.cmd", (string)e!["hooks"]![0]!["command"]!));

        var before = File.ReadAllText(Settings);
        ClaudeSkill.Sync(true, true, home, Deskbar);
        Assert.Equal(before, File.ReadAllText(Settings));

        ClaudeSkill.Sync(true, false, home, Deskbar);
        Assert.Null(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Settings))!["hooks"]!["PostToolUse"]);
    }

    // "Teach Claude Code about Deskbar" off takes every entry of Deskbar's, the task ones included, whatever the task
    // setting says: both write into the same file, and the task hooks without the skill would run for nothing.
    [Fact]
    public void Turning_teach_off_takes_the_task_hooks_too()
    {
        Settled(WithABellAndAStartHook);
        ClaudeSkill.Sync(true, true, home, Deskbar);

        ClaudeSkill.Sync(false, true, home, Deskbar);

        var json = File.ReadAllText(Settings);
        Assert.All(AskEvents.Concat(TaskEvents), e => Assert.Empty(Ours(json, e)));
        Assert.Equal(["python title.py"], Commands(json, "SessionStart"));
        Assert.Equal([@"cmd.exe /c C:\bell.bat"], Commands(json, "Stop"));
        Assert.False(File.Exists(Path.Combine(Deskbar, "claude-task-hook.cmd")));
    }

    // How a Deskbar from before this change tells its entries: "claude-hook.cmd" and "Deskbar" in the command (its
    // ClaudeHooks.IsOurs). It rewrites those entries and claude-hook.cmd itself to run `attention`.
    static bool OlderBuildCallsItsOwn(string command) =>
        command.Contains("claude-hook.cmd", StringComparison.OrdinalIgnoreCase) && command.Contains("Deskbar", StringComparison.OrdinalIgnoreCase);

    // Ruling 35 (review of Task 7, I-3): swapped back to an older Deskbar, the task events must not end up running
    // `attention` and blinking the bar on every session start, prompt and end. So they point at claude-task-hook.cmd,
    // which an older build neither counts as its own nor rewrites; only Notification and Stop are its.
    [Fact]
    public void An_older_deskbar_sees_only_the_ask_entries_as_its_own()
    {
        Settled(WithABellAndAStartHook);

        ClaudeSkill.Sync(true, true, home, Deskbar);

        var json = File.ReadAllText(Settings);
        Assert.All(AskEvents, e => Assert.True(OlderBuildCallsItsOwn(Assert.Single(Ours(json, e)))));
        Assert.All(TaskEvents, e => Assert.False(OlderBuildCallsItsOwn(Assert.Single(Ours(json, e)))));
        Assert.All(TaskEvents, e => Assert.Contains("claude-task-hook.cmd", Assert.Single(Ours(json, e))));
        Assert.Contains("claude-hook --entry", File.ReadAllText(Path.Combine(Deskbar, "claude-task-hook.cmd")));
    }

    // A task entry this build's Task 7 wrote (on claude-hook.cmd) moves to claude-task-hook.cmd on the next start.
    [Fact]
    public void A_task_entry_on_the_ask_script_moves_to_the_task_script()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        var askCommand = ClaudeSkill.HookCommand(Path.Combine(Deskbar, "claude-hook.cmd"));
        File.WriteAllText(Settings, $$"""
            { "hooks": { "SessionStart": [ { "hooks": [ { "type": "command", "command": {{System.Text.Json.JsonSerializer.Serialize(askCommand)}} } ] } ] } }
            """);

        ClaudeSkill.Sync(true, true, home, Deskbar);

        Assert.Equal([ClaudeSkill.HookCommand(Path.Combine(Deskbar, "claude-task-hook.cmd"))], Commands(File.ReadAllText(Settings), "SessionStart"));
    }

    // The task script comes and goes with the task setting, and a file of that name Deskbar did not write stays.
    [Fact]
    public void The_task_script_comes_and_goes_with_its_setting()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        var script = Path.Combine(Deskbar, "claude-task-hook.cmd");

        ClaudeSkill.Sync(true, true, home, Deskbar);
        Assert.True(File.Exists(script));

        ClaudeSkill.Sync(true, false, home, Deskbar);
        Assert.False(File.Exists(script));
        Assert.True(File.Exists(Path.Combine(Deskbar, "claude-hook.cmd")));

        File.WriteAllText(script, "@echo mine");
        ClaudeSkill.Sync(true, false, home, Deskbar);
        Assert.Equal("@echo mine", File.ReadAllText(script));
    }

    // --- the comment waiter (a comment wakes its session; plan 2026-11-24-comment-wakes-session, Task 4) -------------

    static System.Text.Json.Nodes.JsonNode? WaitHook(string json) =>
        System.Text.Json.Nodes.JsonNode.Parse(json)!["hooks"]?["Stop"]?.AsArray()
            .Select(e => e!["hooks"]![0]!).FirstOrDefault(h => h["command"]!.GetValue<string>().Contains("claude-wait-hook.cmd"));

    // While sessions keep their tasks, Stop holds the user's own hooks, Deskbar's ask entry, and the waiter: asyncRewake,
    // so Claude Code wakes the session when it exits 2, and a month's timeout, so it outlives a long idle.
    [Fact]
    public void The_waiter_joins_stop_while_sessions_keep_tasks()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Settings, WithABell);

        ClaudeSkill.Sync(true, true, home, Deskbar);

        var wait = WaitHook(File.ReadAllText(Settings));
        Assert.NotNull(wait);
        Assert.True(wait!["asyncRewake"]!.GetValue<bool>());
        Assert.Equal(2592000, wait["timeout"]!.GetValue<long>());
        Assert.True(File.Exists(Path.Combine(Deskbar, "claude-wait-hook.cmd")));
        Assert.Contains("claude-wait", File.ReadAllText(Path.Combine(Deskbar, "claude-wait-hook.cmd")));
    }

    // Final review, I6: with a Claude Code not known to honour asyncRewake, no waiter is written (it would hold the session
    // after every turn), and one written before goes.
    [Fact]
    public void An_older_claude_code_gets_no_waiter()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Settings, WithABell);
        ClaudeSkill.Sync(true, true, home, Deskbar);

        ClaudeSkill.Sync(true, true, home, Deskbar, waiter: false);

        Assert.Null(WaitHook(File.ReadAllText(Settings)));
        Assert.Equal(2, Commands(File.ReadAllText(Settings), "Stop").Length);
        Assert.False(File.Exists(Path.Combine(Deskbar, "claude-wait-hook.cmd")));
    }

    [Fact]
    public void Without_the_task_events_the_waiter_and_its_script_go()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Settings, WithABell);
        ClaudeSkill.Sync(true, true, home, Deskbar);

        ClaudeSkill.Sync(true, false, home, Deskbar);

        var json = File.ReadAllText(Settings);
        Assert.Null(WaitHook(json));
        Assert.Equal(2, Commands(json, "Stop").Length);
        Assert.False(File.Exists(Path.Combine(Deskbar, "claude-wait-hook.cmd")));
    }

    [Fact]
    public void An_older_waiter_entry_is_replaced()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Settings, """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"\"C:/old/Deskbar/claude-wait-hook.cmd\""}]}]}}""");

        ClaudeSkill.Sync(true, true, home, Deskbar);

        var commands = Commands(File.ReadAllText(Settings), "Stop");
        Assert.Equal(2, commands.Length);
        Assert.DoesNotContain(commands, c => c.Contains("C:/old/"));
    }

    // --- the Read rule for pasted images (images in tasks) -------------------------------------------------------------
    //
    // docs/superpowers/specs/2026-11-24-task-images-design.md, "Can a session read the file?": a session in default
    // permission mode stops at a prompt before reading %APPDATA%\Deskbar\task-images (measured 2026-11-24), so while the
    // task events are on Deskbar keeps ONE allow rule for that folder in permissions.allow (Petre agreed the same day).

    const string Ask = "\"C:/x/Deskbar/claude-hook.cmd\"";
    const string TaskScript = "\"C:/x/Deskbar/claude-task-hook.cmd\"";
    const string Rule = "Read(//c/Users/petre/AppData/Roaming/Deskbar/task-images/**)";

    static string[] Allowed(string json) =>
        System.Text.Json.Nodes.JsonNode.Parse(json)!["permissions"]?["allow"]?.AsArray().Select(a => a!.GetValue<string>()).ToArray() ?? [];

    [Fact]
    public void ImagesRule_is_the_folder_in_claude_codes_absolute_path_form() =>
        Assert.Equal(Rule, ClaudeHooks.ImagesRule(@"C:\Users\petre\AppData\Roaming\Deskbar"));

    [Fact]
    public void The_images_rule_is_added_beside_the_users_own_permissions()
    {
        Settled("""{"permissions":{"allow":["Bash(git status)"],"deny":["Read(.env)"]}}""");

        Assert.True(ClaudeHooks.Sync(true, true, Settings, Ask, TaskScript, Rule));

        var json = File.ReadAllText(Settings);
        Assert.Equal(["Bash(git status)", Rule], Allowed(json));
        Assert.Equal("""["Read(.env)"]""", System.Text.Json.Nodes.JsonNode.Parse(json)!["permissions"]!["deny"]!.ToJsonString());
    }

    [Fact]
    public void The_images_rule_is_written_once()
    {
        Settled("{}");
        ClaudeHooks.Sync(true, true, Settings, Ask, TaskScript, Rule);
        var written = File.ReadAllText(Settings);
        var at = File.GetLastWriteTimeUtc(Settings);

        Assert.True(ClaudeHooks.Sync(true, true, Settings, Ask, TaskScript, Rule));

        Assert.Equal(written, File.ReadAllText(Settings));
        Assert.Equal(at, File.GetLastWriteTimeUtc(Settings));
        Assert.Single(Allowed(written));
    }

    [Fact]
    public void Without_the_task_events_the_images_rule_goes_and_the_users_stay()
    {
        Settled($$$"""{"permissions":{"allow":["Bash(git status)","{{{Rule}}}"]}}""");

        Assert.True(ClaudeHooks.Sync(true, false, Settings, Ask, TaskScript, Rule));

        Assert.Equal(["Bash(git status)"], Allowed(File.ReadAllText(Settings)));
    }

    [Fact]
    public void Taking_the_only_rule_out_takes_the_empty_permissions_it_made()
    {
        Settled("{}");
        ClaudeHooks.Sync(true, true, Settings, Ask, TaskScript, Rule);

        ClaudeHooks.Sync(false, true, Settings, Ask, TaskScript, Rule);

        Assert.Null(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Settings))!["permissions"]);
    }

    [Fact]
    public void An_images_rule_for_another_folder_is_replaced()
    {
        Settled("""{"permissions":{"allow":["Read(//d/Old/Deskbar/task-images/**)","Read(//d/Mine/task-images/**)"]}}""");

        ClaudeHooks.Sync(true, true, Settings, Ask, TaskScript, Rule);

        Assert.Equal(["Read(//d/Mine/task-images/**)", Rule], Allowed(File.ReadAllText(Settings)));
    }

    [Fact]
    public void A_permissions_value_that_is_not_an_object_is_left_alone()
    {
        Settled("""{"permissions":"mine"}""");

        Assert.False(ClaudeHooks.Sync(true, true, Settings, Ask, TaskScript, Rule));

        Assert.Equal("mine", System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Settings))!["permissions"]!.GetValue<string>());
    }

    // Review of Task 7, M-3: a "hooks" that is not an object, or an event that is not an array, is the user's and is
    // left as it is (Claude Code would not run it either), not replaced; the other events are still kept in step, and
    // Sync says the file is not as asked.
    [Fact]
    public void A_hooks_value_that_is_not_an_object_is_left_alone()
    {
        const string odd = """{"hooks":"mine"}""";
        Settled(odd);

        Assert.False(ClaudeHooks.Sync(true, true, Settings, "\"C:/x/Deskbar/claude-hook.cmd\"", "\"C:/x/Deskbar/claude-task-hook.cmd\""));

        Assert.Equal(odd, File.ReadAllText(Settings));
    }

    [Fact]
    public void An_event_that_is_not_an_array_is_left_alone_and_the_rest_kept_in_step()
    {
        Settled("""{"hooks":{"Stop":{"mine":true}}}""");

        Assert.False(ClaudeHooks.Sync(true, false, Settings, "\"C:/x/Deskbar/claude-hook.cmd\"", "\"C:/x/Deskbar/claude-task-hook.cmd\""));

        var hooks = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Settings))!["hooks"]!;
        Assert.Equal("""{"mine":true}""", hooks["Stop"]!.ToJsonString());
        Assert.Single(hooks["Notification"]!.AsArray());
    }

    // A file a parser would lose something from (comments) is not touched.
    [Fact]
    public void A_settings_file_with_comments_is_left_alone()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        const string commented = "{\n  // mine\n  \"model\": \"opus\"\n}";
        File.WriteAllText(Settings, commented);

        ClaudeSkill.Sync(true, true, home, Deskbar);

        Assert.Equal(commented, File.ReadAllText(Settings));
    }

    string Settled(string json)
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Settings, json);
        return json;
    }

    const string Repeated = """{"hooks":{"Stop":[]},"hooks":{"Notification":[]}}""";

    // A repeated key, anywhere in the file, counts as a file that does not parse: rewriting it would drop one of the
    // values. Sync says so by returning false, and nothing is written, not even the backup.
    [Theory]
    [InlineData(Repeated, true)]
    [InlineData(Repeated, false)]
    [InlineData("""{"a":1,"a":2}""", true)]
    [InlineData("""{"a":1,"a":2}""", false)]
    [InlineData("""{"x":{"a":1,"a":2}}""", true)]
    [InlineData("""{"x":{"a":1,"a":2}}""", false)]
    public void A_settings_file_that_does_not_parse_is_left_alone(string odd, bool on)
    {
        Settled(odd);

        var result = ClaudeHooks.Sync(on, false, Settings, "\"C:/x/claude-hook.cmd\"", "\"C:/x/claude-task-hook.cmd\"");

        Assert.False(result);
        Assert.Equal(odd, File.ReadAllText(Settings));
        Assert.False(File.Exists(Settings + ".deskbar-backup"));
        Assert.False(File.Exists(Settings + ".deskbar-new"));
    }

    const string ForeignCommandNumber = """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":5}]}]}}""";
    const string ForeignCommandObject = """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":{}}]}]}}""";
    const string ForeignString = """{"hooks":{"Stop":["x"]}}""";
    const string ForeignNull = """{"hooks":{"Stop":[null]}}""";
    const string ForeignHooksNotObject = """{"hooks":{"Stop":[{"hooks":[5]}]}}""";

    // The text of the foreign Stop entry, as it is written back: the entry itself is what must survive.
    static string ForeignEntryOf(string json) =>
        System.Text.Json.Nodes.JsonNode.Parse(json)!["hooks"]!["Stop"]!.AsArray()[0]?.ToJsonString() ?? "null";

    static int HookEntries(string json, string hookEvent) =>
        System.Text.Json.Nodes.JsonNode.Parse(json)!["hooks"]![hookEvent]!.AsArray()
            .Count(e => e?.ToJsonString().Contains("claude-hook.cmd") == true);

    // One odd entry that is not Deskbar's must not stop Deskbar's own from being added; it stays where it was.
    [Theory]
    [InlineData(ForeignCommandNumber)]
    [InlineData(ForeignCommandObject)]
    [InlineData(ForeignString)]
    [InlineData(ForeignNull)]
    [InlineData(ForeignHooksNotObject)]
    public void A_foreign_entry_of_an_odd_shape_is_kept_and_does_not_stop_the_hooks(string odd)
    {
        Settled(odd);

        var result = ClaudeHooks.Sync(true, false, Settings, "\"C:/x/claude-hook.cmd\"", "\"C:/x/claude-task-hook.cmd\"");

        Assert.True(result);
        var json = File.ReadAllText(Settings);
        Assert.Equal(ForeignEntryOf(odd), ForeignEntryOf(json));
        Assert.Equal(1, HookEntries(json, "Stop"));
        Assert.Equal(1, HookEntries(json, "Notification"));
        Assert.Equal(odd, File.ReadAllText(Settings + ".deskbar-backup"));
    }

    // Off, there is nothing of Deskbar's to take out, so the file is not touched.
    [Theory]
    [InlineData(ForeignCommandNumber)]
    [InlineData(ForeignCommandObject)]
    [InlineData(ForeignString)]
    [InlineData(ForeignNull)]
    [InlineData(ForeignHooksNotObject)]
    public void A_foreign_entry_of_an_odd_shape_is_not_touched_when_the_hooks_are_off(string odd)
    {
        Settled(odd);

        var result = ClaudeHooks.Sync(false, false, Settings, "\"C:/x/claude-hook.cmd\"", "\"C:/x/claude-task-hook.cmd\"");

        Assert.True(result);
        Assert.Equal(odd, File.ReadAllText(Settings));
        Assert.False(File.Exists(Settings + ".deskbar-backup"));
    }

    // Beside an entry of Deskbar's, a foreign odd one stays when Deskbar's goes.
    [Fact]
    public void Turning_the_hooks_off_leaves_a_foreign_odd_entry_beside_deskbars()
    {
        Settled("""{"hooks":{"Stop":["x",{"hooks":[{"type":"command","command":"\"C:/x/Deskbar/claude-hook.cmd\""}]}]}}""");

        var result = ClaudeHooks.Sync(false, false, Settings, "\"C:/x/Deskbar/claude-hook.cmd\"", "\"C:/x/Deskbar/claude-task-hook.cmd\"");

        Assert.True(result);
        var stop = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Settings))!["hooks"]!["Stop"]!.AsArray();
        Assert.Single(stop);
        Assert.Equal("x", stop[0]!.GetValue<string>());
    }

    // The skill must not claim hooks that are not there. With a settings file that cannot be parsed, the skill is still
    // written, says the hooks are missing, and points at `attention`; once the file is fixed the next sync brings the
    // claim back.
    [Fact]
    public void The_skill_does_not_claim_hooks_that_were_not_installed()
    {
        Settled(Repeated);

        ClaudeSkill.Sync(true, true, home, Deskbar);

        var text = File.ReadAllText(Skill);
        Assert.DoesNotContain("hooks already ask", text);
        Assert.Contains("attention", text);
        Assert.Equal(Repeated, File.ReadAllText(Settings));

        File.WriteAllText(Settings, "{}");
        ClaudeSkill.Sync(true, true, home, Deskbar);

        Assert.Contains("hooks already ask", File.ReadAllText(Skill));
    }

    // Plan Task 9: while sessions keep their tasks, the skill has a "Your task" section with the rules the briefing
    // names (join or create, the status line, ask through the task, comment in markdown, done and never close, leave the
    // user's notes alone). Off, or with the hooks not installed (no briefing gives a session its id), the section goes.
    [Fact]
    public void The_skill_explains_tasks_only_while_they_are_on()
    {
        Settled(WithABell);

        ClaudeSkill.Sync(true, true, home, Deskbar);
        var on = File.ReadAllText(Skill);
        Assert.Contains("## Your task", on);
        Assert.Contains("task join <id> --session", on);
        Assert.Contains("task new", on);
        Assert.Contains("task status-line", on);
        Assert.Contains("task ask", on);
        Assert.Contains("task comment", on);
        Assert.Contains("task done", on);
        // Ruling 49: the session resolves its own question.
        Assert.Contains("task answered --session", on);
        Assert.Contains("Never close", on);
        // Task 06c2538b: a pull request ready to merge waits on the user through `task ask`, never a comment and done.
        // Task 08cdb052: the example ask carries the pull request as a link, and the rule says to give one.
        Assert.Contains("Merge [PR 512](https://github.com/owner/repo/pull/512): CI is green", on);
        Assert.Contains("as a markdown link `[text](https://...)`", on);
        Assert.Contains("A pull request ready to merge is an ask, never a comment and `task done`", on);
        Assert.Contains("not while a pull request of yours waits to be merged", on);
        Assert.DoesNotContain("PR 512 is ready", on);
        Assert.DoesNotContain("—", on);
        // Review of PR B, I-1: free text on stdin, never on the command line. M-6: the description says to load the
        // skill for the task too.
        Assert.Contains("<<'EOF'", on);
        Assert.Contains("Never put free text with quotes or & | < > on the command line", on);
        Assert.Contains("task", on.Split("\n---")[0].Split("description:")[1]);

        ClaudeSkill.Sync(true, false, home, Deskbar);
        Assert.DoesNotContain("## Your task", File.ReadAllText(Skill));
        Assert.DoesNotContain("task", File.ReadAllText(Skill).Split("\n---")[0].Split("description:")[1]);

        File.WriteAllText(Settings, Repeated);
        ClaudeSkill.Sync(true, true, home, Deskbar);
        Assert.DoesNotContain("## Your task", File.ReadAllText(Skill));
    }

    [Fact]
    public void The_skill_says_the_hooks_ask_when_they_are_installed()
    {
        Settled(WithABell);

        ClaudeSkill.Sync(true, true, home, Deskbar);

        Assert.Contains("hooks already ask", File.ReadAllText(Skill));
    }

    public void Dispose() => Directory.Delete(home, recursive: true);

    // No Claude Code on the machine, no folder of its own: nothing is written.
    [Fact]
    public void Nothing_is_written_without_a_claude_folder()
    {
        ClaudeSkill.Sync(true, true, home, Deskbar);

        Assert.False(Directory.Exists(Path.Combine(home, ".claude")));
    }

    [Fact]
    public void The_skill_is_written_and_names_the_commands()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));

        ClaudeSkill.Sync(true, true, home, Deskbar);

        var text = File.ReadAllText(Skill);
        Assert.StartsWith("---\nname: deskbar\n", text.ReplaceLineEndings("\n"));
        Assert.Contains("deskbar.cmd open", text);
        Assert.Contains("--no-follow", text);
    }

    // Rewritten only when the text differs: an unchanged file keeps its timestamp.
    [Fact]
    public void An_unchanged_skill_is_not_rewritten()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        ClaudeSkill.Sync(true, true, home, Deskbar);
        var written = File.GetLastWriteTimeUtc(Skill);
        File.SetLastWriteTimeUtc(Skill, written.AddHours(-1));

        ClaudeSkill.Sync(true, true, home, Deskbar);

        Assert.Equal(written.AddHours(-1), File.GetLastWriteTimeUtc(Skill));
    }

    // Turning it off removes the file and the folder it made, and leaves the rest of ~/.claude alone.
    [Fact]
    public void Turning_it_off_removes_the_skill()
    {
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        ClaudeSkill.Sync(true, true, home, Deskbar);

        ClaudeSkill.Sync(false, true, home, Deskbar);

        Assert.False(Directory.Exists(Path.GetDirectoryName(Skill)));
        Assert.True(Directory.Exists(Path.Combine(home, ".claude", "skills")));
    }

    // A file Deskbar did not write is never deleted, even at its path.
    [Fact]
    public void A_skill_someone_else_wrote_is_left_alone()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Skill)!);
        File.WriteAllText(Skill, "my own notes");

        ClaudeSkill.Sync(false, true, home, Deskbar);

        Assert.Equal("my own notes", File.ReadAllText(Skill));
    }

    // The exe part of a browser's open command, quoted or not; only an exe that exists counts.
    [Fact]
    public void The_browser_exe_is_read_from_its_open_command()
    {
        var exe = Path.Combine(home, "browser.exe");
        File.WriteAllText(exe, "");

        Assert.Equal(exe, DefaultBrowser.ExeOf($"\"{exe}\" --single-argument %1").Value);
        Assert.True(DefaultBrowser.ExeOf("\"C:\\nowhere\\browser.exe\" %1").HasNoValue);
    }

    // Petre: "i only want the most important things to be here", "otherwise keep the task lean".
    [Fact]
    public void The_skill_teaches_lean_tasks()
    {
        Settled(WithABell);

        ClaudeSkill.Sync(true, true, home, Deskbar);
        var on = File.ReadAllText(Skill);
        Assert.Contains("say what needs the user, and nothing else", on);
        Assert.Contains("title is a few words", on);
        Assert.Contains("under about 60 characters", on);
        Assert.Contains("comments are rare", on);
        Assert.Contains("progress logs, measurements, spike notes or findings dumps", on);
        Assert.Contains("say exactly what", on);
        Assert.DoesNotContain("worth keeping", on);
    }

    [Fact]
    public void The_skill_says_answered_settles_the_questions_comments()
    {
        Settled(WithABell);

        ClaudeSkill.Sync(true, true, home, Deskbar);
        var on = File.ReadAllText(Skill);
        Assert.Contains("your comments written since you asked are settled", on);
        Assert.Contains("task comment-settle <comment-id> --session", on);
    }
}
