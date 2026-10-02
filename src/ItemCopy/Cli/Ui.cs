namespace ItemCopy.Cli;

/// <summary>Console output and prompts. Thread-safe so parallel chunk copies can log.</summary>
public sealed class Ui
{
    private readonly TextWriter _out;
    private readonly TextReader _in;
    private readonly bool _color;
    private readonly Lock _lock = new();

    public Ui(TextWriter output, TextReader input, bool interactive, bool verbose, bool color)
    {
        _out = output;
        _in = input;
        IsInteractive = interactive;
        IsVerbose = verbose;
        _color = color;
    }

    public static Ui ForConsole(bool verbose)
    {
        var color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
        return new Ui(Console.Out, Console.In, interactive: !Console.IsInputRedirected, verbose, color);
    }

    public bool IsInteractive { get; }

    public bool IsVerbose { get; }

    public void Info(string message) => Log(message, null);

    public void Step(string message) => Log(message, Ansi.Bold);

    public void Success(string message) => Log(message, Ansi.Green);

    public void Warn(string message) => Log($"WARNING: {message}", Ansi.Yellow);

    public void Error(string message) => Log($"ERROR: {message}", Ansi.Red);

    public void Debug(string message)
    {
        if (IsVerbose)
            Log(message, Ansi.Dim);
    }

    /// <summary>Writes a line without a timestamp, for plans and summaries.</summary>
    public void Plain(string message = "", string? style = null)
    {
        lock (_lock)
            _out.WriteLine(Style(message, style));
    }

    public string? Prompt(string question)
    {
        lock (_lock)
        {
            _out.Write(Style(question, Ansi.Bold) + " ");
            _out.Flush();
        }
        return _in.ReadLine();
    }

    private void Log(string message, string? style)
    {
        lock (_lock)
            _out.WriteLine($"{Style($"[{DateTime.Now:HH:mm:ss}]", Ansi.Dim)} {Style(message, style)}");
    }

    private string Style(string text, string? style) => _color && style is not null ? $"{style}{text}{Ansi.Reset}" : text;

    public static class Ansi
    {
        public const string Reset = "\e[0m";
        public const string Bold = "\e[1m";
        public const string Dim = "\e[2m";
        public const string Red = "\e[31m";
        public const string Green = "\e[32m";
        public const string Yellow = "\e[33m";
        public const string BoldRed = "\e[1;31m";
    }
}
