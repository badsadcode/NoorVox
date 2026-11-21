using System.Diagnostics;
using System.Text.Json;
using NoorVox.Cli;

namespace NoorVox.Tests;

/// <summary>Keeps the documented CLI contract honest.</summary>
public class DocsContractTests
{
    [Fact]
    public void Every_cli_command_is_documented_in_CLI_REFERENCE()
    {
        var doc = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs", "CLI_REFERENCE.md"));
        foreach (var c in CommandTable.All)
            Assert.Contains($"### `noorvox {string.Join(' ', c.Path)}", doc);
    }

    [Fact]
    public void Every_value_less_option_in_a_usage_string_is_a_parser_flag()
    {
        // In usage strings a value-less option looks like "[--no-engine]" or "--approve]"; one with a value like "[--model REF]".
        foreach (var c in CommandTable.All)
            foreach (var opt in c.Options)
            {
                var m = System.Text.RegularExpressions.Regex.Match(c.Usage, $@"--{System.Text.RegularExpressions.Regex.Escape(opt)}(?<next>[\]\)| ]|$)(?<after>\S*)");
                if (!m.Success) continue;
                bool takesValue = m.Groups["next"].Value == " " && m.Groups["after"].Value.Length > 0
                    && !m.Groups["after"].Value.StartsWith('[') && !m.Groups["after"].Value.StartsWith('|') && !m.Groups["after"].Value.StartsWith("--");
                if (!takesValue)
                    Assert.True(ParsedArgs.Flags.Contains(opt), $"'--{opt}' in '{string.Join(' ', c.Path)}' takes no value but is not in ParsedArgs.Flags");
            }
        var parsed = ParsedArgs.Parse(["models", "install", "x", "--no-engine", "--json"]);
        Assert.True(parsed.Has("json")); // regression: --json was once swallowed as the value of --no-engine
    }

    [Fact]
    public void Every_exit_code_is_documented()
    {
        var doc = File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "docs", "CLI_REFERENCE.md"));
        foreach (var e in Enum.GetValues<NoorVox.Core.ExitCode>())
            Assert.Contains($"| {(int)e} |", doc);
    }

    [Fact]
    public async Task Json_mode_writes_exactly_one_envelope_and_errors_are_nonzero()
    {
        using var home = new TestHome();
        var (code, stdout) = await Run(home, "version --json");
        Assert.Equal(0, code);
        using (var doc = JsonDocument.Parse(stdout))
        {
            Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        }

        (code, stdout) = await Run(home, "synth --text hi --json");
        Assert.Equal(4, code); // no model installed in an empty home
        using (var doc = JsonDocument.Parse(stdout))
        {
            Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("model_not_installed", doc.RootElement.GetProperty("code").GetString());
        }

        (code, _) = await Run(home, "workers stop --json --bogus 1");
        Assert.Equal(2, code);
    }

    static async Task<(int, string)> Run(TestHome home, string args)
    {
        var psi = new ProcessStartInfo(TestPaths.CliExe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.Environment["NOORVOX_HOME"] = home.Root;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(1));
        return (p.ExitCode, await stdout);
    }
}
