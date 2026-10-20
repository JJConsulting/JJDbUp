namespace JJConsulting.DbUp.Cli;

// Messages must describe the problem without echoing configuration values or SQL.
internal sealed class CliException(string message) : ArgumentException(message);
