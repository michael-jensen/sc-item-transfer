using System.Reflection;
using ItemCopy.Api;
using ItemCopy.Cli;
using ItemCopy.Config;
using ItemCopy.Http;
using ItemCopy.Pipeline;

namespace ItemCopy;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ConfigException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        switch (options.Command)
        {
            case Command.Help:
                Console.WriteLine(Options.Usage);
                return 0;
            case Command.Version:
                Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
                return 0;
        }

        var ui = Ui.ForConsole(options.Verbose);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C cancels gracefully (cleanup still runs); a second one kills the process.
            if (cts.IsCancellationRequested)
                return;
            e.Cancel = true;
            ui.Warn("Cancelling... (press Ctrl+C again to exit immediately)");
            cts.Cancel();
        };

        try
        {
            return await RunAsync(options, ui, cts.Token);
        }
        catch (Exception ex) when (ex is ConfigException or AuthException or SitecoreApiException)
        {
            ui.Error(ex.Message);
            return 1;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            ui.Warn("Cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            // Anything unexpected (file I/O, etc.): report it plainly rather than as a stack trace.
            ui.Error($"{ex.GetType().Name}: {ex.Message}");
            ui.Debug(ex.ToString());
            return 1;
        }
    }

    private static async Task<int> RunAsync(Options options, Ui ui, CancellationToken ct)
    {
        var environments = EnvironmentRegistry.Load(
            options.EnvFile,
            [Path.Combine(Environment.CurrentDirectory, ".env"), Path.Combine(AppContext.BaseDirectory, ".env")],
            Environment.GetEnvironmentVariables());
        ui.Debug(environments.EnvFilePath is { } envPath ? $"Loaded {envPath}" : "No .env file found; using process environment only.");

        var validation = JobValidator.Validate(JobFile.Load(options.JobPath!), environments);
        if (!validation.IsValid)
        {
            ui.Error($"{options.JobPath} has {validation.Errors.Count} problem(s):");
            foreach (var error in validation.Errors)
                ui.Plain($"  - {error}", Ui.Ansi.Red);
            return 1;
        }

        var job = validation.Job!;
        var source = environments.Resolve(job.Source);
        var destination = environments.Resolve(job.Destination);
        var destinationProtected = environments.IsProtected(destination.Name);
        Report.Plan(job, source, destination, destinationProtected, validation.Warnings, ui);

        // No automatic decompression: chunk bytes must be forwarded exactly as received.
        using var httpClient = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.None })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"item-copy/{typeof(Program).Assembly.GetName().Version?.ToString(3)}");

        var tokens = new TokenProvider(httpClient, environments.AuthUrl, environments.AuthAudience, TimeProvider.System);
        foreach (var env in new[] { source, destination })
        {
            await tokens.GetTokenAsync(env, ct);
            ui.Info($"Authenticated with {env.Name}.");
        }

        if (options.DryRun)
        {
            ui.Success("Dry run OK: job is valid and both environments accepted their credentials. Nothing was transferred.");
            return 0;
        }

        if (!Confirmation.Confirm(options, destination.Name, destinationProtected, ui))
            return 1;

        var http = new SitecoreHttp(httpClient, tokens, new RetryPolicy(), ui);
        var runner = new TransferRunner(
            new ContentTransferClient(http, ui),
            new ItemTransferClient(http),
            ui,
            new RunnerSettings { Timeout = options.Timeout ?? Options.DefaultTimeout });

        var result = await runner.RunAsync(job, source, destination, ct);
        Report.Summary(result, source, destination, ui);
        return result.ExitCode;
    }
}
