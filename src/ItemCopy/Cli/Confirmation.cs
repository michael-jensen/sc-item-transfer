namespace ItemCopy.Cli;

public static class Confirmation
{
    /// <summary>
    /// Asks before writing to the destination. Protected destinations need the environment name
    /// typed (or <c>--confirm-env</c>); <c>--yes</c> alone only skips the y/N prompt.
    /// </summary>
    public static bool Confirm(Options options, string destination, bool isProtected, Ui ui)
    {
        if (isProtected)
        {
            if (options.ConfirmEnv is not null)
            {
                if (options.ConfirmEnv.Equals(destination, StringComparison.OrdinalIgnoreCase))
                    return true;
                ui.Error($"--confirm-env '{options.ConfirmEnv}' does not match the destination '{destination}'.");
                return false;
            }

            if (!ui.IsInteractive)
            {
                ui.Error($"{destination} is a protected environment. Run interactively, or pass --yes --confirm-env {destination}.");
                return false;
            }

            var typed = ui.Prompt($"{destination} is a protected environment. Type its name ({destination}) to continue:");
            if (typed?.Trim().Equals(destination, StringComparison.OrdinalIgnoreCase) == true)
                return true;
            ui.Info("Cancelled.");
            return false;
        }

        if (options.Yes)
            return true;

        if (!ui.IsInteractive)
        {
            ui.Error("Confirmation needed but input is not interactive. Pass --yes to proceed.");
            return false;
        }

        var answer = ui.Prompt("Proceed? [y/N]")?.Trim();
        if (answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase)))
            return true;
        ui.Info("Cancelled.");
        return false;
    }
}
