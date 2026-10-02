namespace ItemCopy.Config;

/// <summary>A problem with .env, the job file, or command-line input. Shown to the user as-is.</summary>
public sealed class ConfigException(string message) : Exception(message);
