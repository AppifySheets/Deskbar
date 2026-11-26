using System.Text.Json;
using Deskbar.Core.Tasks;

namespace Deskbar.Core.Tests.Tasks;

// The tester's findings M1 (secrets in a step's label) and M4 (Deskbar's own calls in every form), 2026-11-26. Every value
// below is made up. The forms are the ones the tester listed as left readable or left visible.
public class ClaudeChatMaskingTests
{
    static ChatStep StepOf(string tool, string command)
    {
        var line = "{\"type\":\"assistant\",\"uuid\":\"a\",\"timestamp\":\"2026-01-02T03:04:07Z\",\"message\":{\"id\":\"m\",\"model\":\"claude-test\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t\",\"name\":"
            + JsonSerializer.Serialize(tool) + ",\"input\":" + JsonSerializer.Serialize(new { command }) + "}]}}";
        var run = Assert.IsType<ChatSteps>(Assert.Single(ClaudeChat.Parse([line])));
        return Assert.Single(run.Steps);
    }

    // ---- M1: a secret never reaches a label ---------------------------------------------------------------------------

    [Theory]
    [InlineData("$env:API_TOKEN = \"fakeTokenValue\"", "fakeTokenValue")]
    [InlineData("$env:API_TOKEN = fakeTokenValue", "fakeTokenValue")]
    [InlineData("export DB_PASSWORD = madeUp1", "madeUp1")]
    [InlineData("API_KEY: madeUpKey1 run", "madeUpKey1")]
    [InlineData("client_secret : 'made up secret' run", "made up secret")]
    [InlineData("AUTH = zzTop42", "zzTop42")]
    [InlineData("my-credential= notReal7", "notReal7")]
    [InlineData("PGPWD=notReal8 psql", "notReal8")]
    [InlineData("mysql --password notReal9 -h db", "notReal9")]
    [InlineData("tool --token notReal10", "notReal10")]
    [InlineData("tool --secret 'not real 11'", "not real 11")]
    [InlineData("tool --api-key notReal12", "notReal12")]
    [InlineData("mysql -p notReal13 db", "notReal13")]
    [InlineData("mysql -P notReal14 db", "notReal14")]
    [InlineData("setx DB_PASSWORD notReal15", "notReal15")]
    [InlineData("setx MY_API_TOKEN \"not real 16\"", "not real 16")]
    [InlineData("curl -u alice:notReal17 https://example.test/a", "notReal17")]
    [InlineData("curl --user alice:notReal18 https://example.test/a", "notReal18")]
    [InlineData("curl -u \"alice:notReal19\" https://example.test/a", "notReal19")]
    [InlineData("git clone https://alice:notReal20@example.test/repo.git", "notReal20")]
    [InlineData("psql postgres://alice:notReal21@db.example.test/app", "notReal21")]
    [InlineData("curl -H \"X-Api-Key: notReal22\" https://example.test/a", "notReal22")]
    [InlineData("curl -d '{\"password\": \"notReal23\"}' https://example.test", "notReal23")]
    [InlineData("curl -d '{\"token\":\"notReal24\",\"x\":1}' https://example.test", "notReal24")]
    public void A_secret_in_any_of_these_forms_never_reaches_the_label(string command, string secret)
    {
        var label = StepOf("Bash", command).Label;

        Assert.DoesNotContain(secret, label);
        Assert.Contains("***", label);
    }

    [Fact]
    public void A_url_with_a_password_is_masked_in_a_web_fetch_label_too()
    {
        var line = "{\"type\":\"assistant\",\"uuid\":\"a\",\"message\":{\"id\":\"m\",\"model\":\"c\",\"content\":[{\"type\":\"tool_use\",\"id\":\"t\",\"name\":\"WebFetch\",\"input\":{\"url\":\"https://alice:notReal25@example.test/x\"}}]}}";
        var label = Assert.Single(Assert.IsType<ChatSteps>(Assert.Single(ClaudeChat.Parse([line]))).Steps).Label;

        Assert.DoesNotContain("notReal25", label);
        Assert.Contains("example.test", label);
    }

    [Theory]
    [InlineData("dotnet test tests/S.Tests -c Release --filter \"Name~Sample\"")]
    [InlineData("git log --oneline -5")]
    [InlineData("grep -rn \"monkey\" src")]
    [InlineData("curl -s https://example.test/api/items?page=2")]
    [InlineData("npm run build --workspace sample-app")]
    [InlineData("git commit -m \"fix the sample\"")]
    [InlineData("ls -la /tmp/work")]
    public void The_label_of_an_ordinary_command_stays_readable(string command) =>
        Assert.Equal(command, StepOf("Bash", command).Label);

    // ---- M4: Deskbar's own calls, in every form --------------------------------------------------------------------

    [Theory]
    // first, after a path, with each kind of slash and variable
    [InlineData("Bash", "deskbar.cmd task status-line 'x' --session s1")]
    [InlineData("Bash", "deskbar task status-line 'x' --session s1")]
    [InlineData("Bash", "\"$APPDATA/Deskbar/deskbar.cmd\" task comment - --session s1")]
    [InlineData("Bash", "$APPDATA/Deskbar/deskbar.cmd task done --session s1")]
    [InlineData("Bash", "/c/Users/someone/AppData/Roaming/Deskbar/deskbar.cmd task done --session s1")]
    [InlineData("PowerShell", "& \"$env:APPDATA\\Deskbar\\deskbar.cmd\" task status-line - --session s1")]
    [InlineData("PowerShell", "& $env:APPDATA\\Deskbar\\deskbar.cmd task status-line - --session s1")]
    [InlineData("PowerShell", "& \"C:\\Program Files\\Some Folder\\Deskbar\\deskbar.cmd\" task ask - --session s1")]
    [InlineData("PowerShell", "& 'C:\\Program Files\\Some Folder\\Deskbar\\deskbar.cmd' task ask - --session s1")]
    [InlineData("PowerShell", "%APPDATA%\\Deskbar\\deskbar.cmd task join abc --session s1")]
    // after a pipe, a variable, a here-string, a heredoc, echo
    [InlineData("PowerShell", "'a status line' | & \"$env:APPDATA\\Deskbar\\deskbar.cmd\" task status-line - --session s1")]
    [InlineData("PowerShell", "$text = 'hello'\n$text | & \"C:\\x\\deskbar.cmd\" task comment - --session s1")]
    [InlineData("PowerShell", "@'\nA comment with deskbar.cmd task inside and git push\n'@ | & \"C:\\x\\deskbar.cmd\" task comment - --session s1")]
    [InlineData("PowerShell", "$body = @\"\nline one\nline two\n\"@\n$body | & 'C:\\x\\deskbar.cmd' task comment - --session s1")]
    [InlineData("Bash", "echo 'status' | \"$APPDATA/Deskbar/deskbar.cmd\" task status-line - --session s1")]
    [InlineData("Bash", "echo x | deskbar.cmd task status-line - --session s1")]
    [InlineData("Bash", "printf '%s' \"text\" | deskbar.cmd task comment - --session s1")]
    [InlineData("Bash", "cat <<'EOF' | deskbar.cmd task comment - --session s1\nthe comment\nwith git push inside\nEOF")]
    [InlineData("Bash", "deskbar.cmd task comment - --session s1 <<'EOF'\nthe comment\nEOF")]
    [InlineData("Bash", "cd /w/repo && deskbar.cmd task join abc --session s1")]
    [InlineData("Bash", "cd /w/repo; echo done | deskbar.cmd task done --session s1 2>&1")]
    [InlineData("Bash", "deskbar.cmd task status-line x --session s1 > /dev/null 2>&1")]
    [InlineData("PowerShell", "Write-Output 'x' | & \"C:\\x\\deskbar.cmd\" task status-line - --session s1")]
    [InlineData("PowerShell", "DESKBAR.CMD TASK done --session s1")]
    public void A_command_whose_only_program_is_deskbars_shim_is_housekeeping(string tool, string command) =>
        Assert.True(StepOf(tool, command).Housekeeping, command);

    [Theory]
    [InlineData("git log --oneline")]
    [InlineData("dotnet test")]
    [InlineData("echo deskbar.cmd task is the shim")]
    [InlineData("cat notes-on-deskbar.cmd")]
    [InlineData("deskbar.cmd status")]
    [InlineData("git push; deskbar.cmd task done --session s1")]
    [InlineData("dotnet build && deskbar.cmd task status-line x --session s1")]
    [InlineData("deskbar.cmd task done --session s1 && git push")]
    [InlineData("curl https://example.test | deskbar.cmd task comment -")]
    [InlineData("notdeskbar.cmd task x")]
    public void A_command_with_another_real_program_is_a_step_as_ever(string command) =>
        Assert.False(StepOf("Bash", command).Housekeeping, command);
}
