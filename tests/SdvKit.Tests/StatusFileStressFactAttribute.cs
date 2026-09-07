namespace SdvKit.Tests;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class StatusFileStressFactAttribute : FactAttribute
{
    public StatusFileStressFactAttribute()
    {
        bool isCi = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        if (!isCi && Environment.GetEnvironmentVariable("SDVKIT_RUN_STATUS_FILE_STRESS") != "1")
        {
            Skip = "Local status-file stress is opt-in while #136 remains unresolved. Set SDVKIT_RUN_STATUS_FILE_STRESS=1 to run; CI always runs this test.";
        }
    }
}
