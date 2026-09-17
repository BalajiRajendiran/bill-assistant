namespace BillAssistant.Core.Tests.Integration;

/// <summary>
/// A test that needs Ollama and Qdrant actually running. Skipped unless BILLS_INTEGRATION=1, so a
/// plain `dotnet test` stays fast and green on a machine with neither service up.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BILLS_INTEGRATION") != "1")
        {
            Skip = "Set BILLS_INTEGRATION=1 (with Ollama and Qdrant running) to include integration tests.";
        }
    }
}
