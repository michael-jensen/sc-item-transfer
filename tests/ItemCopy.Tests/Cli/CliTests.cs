using ItemCopy.Cli;
using ItemCopy.Config;
using ItemCopy.Pipeline;

namespace ItemCopy.Tests.Cli;

public class OptionsTests
{
    [Fact]
    public void Parses_run_with_all_options()
    {
        var options = Options.Parse(["run", "jobs/a.json", "--yes", "--confirm-env", "prod", "--dry-run", "--env-file", "x.env", "--timeout", "90s", "--verbose"]);

        Assert.Equal(new Options(Command.Run, "jobs/a.json", true, "prod", true, "x.env", TimeSpan.FromSeconds(90), true), options);
    }

    [Theory]
    [InlineData(new string[0], Command.Help)]
    [InlineData(new[] { "--help" }, Command.Help)]
    [InlineData(new[] { "run", "a.json", "-h" }, Command.Help)]
    [InlineData(new[] { "--version" }, Command.Version)]
    public void Recognises_help_and_version(string[] args, Command expected)
    {
        Assert.Equal(expected, Options.Parse(args).Command);
    }

    [Theory]
    [InlineData(new[] { "copy", "a.json" }, "Unknown command")]
    [InlineData(new[] { "run" }, "Missing job file")]
    [InlineData(new[] { "run", "a.json", "b.json" }, "Only one job file")]
    [InlineData(new[] { "run", "a.json", "--force" }, "Unknown option")]
    [InlineData(new[] { "run", "a.json", "--timeout" }, "needs a value")]
    [InlineData(new[] { "run", "a.json", "--confirm-env", "--yes" }, "needs a value")]
    public void Rejects_bad_arguments(string[] args, string expected)
    {
        var ex = Assert.Throws<ConfigException>(() => Options.Parse(args));
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("1.5h", 5400)]
    [InlineData("2H", 7200)]
    public void Parses_durations(string text, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), Options.ParseDuration(text));
    }

    [Theory]
    [InlineData("30")]
    [InlineData("0m")]
    [InlineData("10d")]
    [InlineData("abc")]
    public void Rejects_bad_durations(string text)
    {
        Assert.Throws<ConfigException>(() => Options.ParseDuration(text));
    }
}

public class ConfirmationTests
{
    private static (Ui Ui, StringWriter Output) MakeUi(string input = "", bool interactive = true)
    {
        var output = new StringWriter();
        return (new Ui(output, new StringReader(input), interactive, verbose: false, color: false), output);
    }

    private static Options Run(bool yes = false, string? confirmEnv = null) => new(Command.Run, "a.json", yes, confirmEnv);

    [Theory]
    [InlineData("y\n", true)]
    [InlineData("YES\n", true)]
    [InlineData("n\n", false)]
    [InlineData("\n", false)]
    [InlineData("", false)]
    public void Unprotected_destination_asks_y_n(string input, bool expected)
    {
        var (ui, _) = MakeUi(input);
        Assert.Equal(expected, Confirmation.Confirm(Run(), "SIT", isProtected: false, ui));
    }

    [Fact]
    public void Yes_skips_prompt_for_unprotected_destination()
    {
        var (ui, output) = MakeUi(interactive: false);
        Assert.True(Confirmation.Confirm(Run(yes: true), "SIT", isProtected: false, ui));
        Assert.DoesNotContain("Proceed", output.ToString());
    }

    [Fact]
    public void Non_interactive_without_yes_aborts()
    {
        var (ui, _) = MakeUi("y\n", interactive: false);
        Assert.False(Confirmation.Confirm(Run(), "SIT", isProtected: false, ui));
    }

    [Theory]
    [InlineData("prod\n", true)]
    [InlineData("PROD\n", true)]
    [InlineData("y\n", false)]
    [InlineData("sit\n", false)]
    public void Protected_destination_requires_typing_name(string input, bool expected)
    {
        var (ui, _) = MakeUi(input);
        Assert.Equal(expected, Confirmation.Confirm(Run(), "PROD", isProtected: true, ui));
    }

    [Fact]
    public void Yes_alone_does_not_skip_protected_confirmation()
    {
        var (ui, _) = MakeUi(interactive: false);
        Assert.False(Confirmation.Confirm(Run(yes: true), "PROD", isProtected: true, ui));
    }

    [Theory]
    [InlineData("prod", true)]
    [InlineData("sit", false)]
    public void Confirm_env_must_match_protected_destination(string confirmEnv, bool expected)
    {
        var (ui, _) = MakeUi(interactive: false);
        Assert.Equal(expected, Confirmation.Confirm(Run(yes: true, confirmEnv: confirmEnv), "PROD", isProtected: true, ui));
    }
}

public class ReportTests
{
    private static readonly SitecoreEnvironment Dev = new("DEV", "dev.test", "id", "secret");
    private static readonly SitecoreEnvironment Sit = new("SIT", "sit.test", "id", "secret");

    [Fact]
    public void Summary_explains_a_load_that_was_still_running_when_the_run_stopped()
    {
        var item = new ItemResult(new JobItem("/sitecore/content/Home", TransferScope.ItemAndDescendants, MergeStrategy.OverrideExistingItem), Guid.NewGuid())
        {
            Outcome = ItemOutcome.Unknown,
            UnfinishedLoad = "contentTransfer-a.raif",
            LoadTransferId = "consumed.20261002 010005 1.abc",
        };
        item.Blobs.Add("contentTransfer-a.raif");
        var output = new StringWriter();

        Report.Summary(new RunResult([item], cancelled: true), Dev, Sit, new Ui(output, TextReader.Null, interactive: false, verbose: false, color: false));

        var text = output.ToString();
        Assert.Contains("UNKNOWN  /sitecore/content/Home", text);
        Assert.Contains("SIT was still loading contentTransfer-a.raif (load ID consumed.20261002 010005 1.abc)", text);
        Assert.Contains("contentTransfer-a.raif (wait for its load to finish before deleting it)", text);
    }
}
