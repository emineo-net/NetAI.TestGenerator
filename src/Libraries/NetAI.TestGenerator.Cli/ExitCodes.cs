namespace NetAI.TestGenerator.Cli;

public static class ExitCodes
{
    public const int Success = 0;
    public const int ConfigurationError = 1;
    public const int GenerationFailed = 2;
    public const int AnalysisFailed = 3;
    public const int Cancelled = 130; // 128 + SIGINT
    public const int UnhandledError = 99;
}