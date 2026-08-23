using PatientIntakeApp.Data.Entities;

namespace PatientIntakeApp.Services.ExternalChecks;

public class ExternalCheckRunResult
{
    public ExternalCheckResultStatus Status { get; init; } = ExternalCheckResultStatus.Success;
    public string ResultJson { get; init; } = "{}";
}

public interface IExternalCheckProvider
{
    ExternalCheckType Type { get; }
    string ProviderName { get; }
    Task<ExternalCheckRunResult> RunAsync(ReferralEntity referral, CancellationToken cancellationToken);
}

// Stub/demo providers to prove end-to-end orchestration.
public class StubFinancialCheckProvider : IExternalCheckProvider
{
    public ExternalCheckType Type => ExternalCheckType.Financial;
    public string ProviderName => "StubFinancial";

    public Task<ExternalCheckRunResult> RunAsync(ReferralEntity referral, CancellationToken cancellationToken)
    {
        var ok = Random.Shared.NextDouble() > 0.15;
        var json = ok
            ? "{\"status\":\"ok\",\"balance\":\"unknown\"}"
            : "{\"status\":\"flag\",\"reason\":\"payment_plan\"}";
        return Task.FromResult(new ExternalCheckRunResult { Status = ExternalCheckResultStatus.Success, ResultJson = json });
    }
}

public class StubLitigationCheckProvider : IExternalCheckProvider
{
    public ExternalCheckType Type => ExternalCheckType.Litigation;
    public string ProviderName => "StubLitigation";

    public Task<ExternalCheckRunResult> RunAsync(ReferralEntity referral, CancellationToken cancellationToken)
    {
        var ok = Random.Shared.NextDouble() > 0.10;
        var json = ok
            ? "{\"status\":\"ok\"}"
            : "{\"status\":\"hit\",\"case\":\"example\"}";
        return Task.FromResult(new ExternalCheckRunResult { Status = ExternalCheckResultStatus.Success, ResultJson = json });
    }
}

public class StubCriminalCheckProvider : IExternalCheckProvider
{
    public ExternalCheckType Type => ExternalCheckType.Criminal;
    public string ProviderName => "StubCriminal";

    public Task<ExternalCheckRunResult> RunAsync(ReferralEntity referral, CancellationToken cancellationToken)
    {
        var ok = Random.Shared.NextDouble() > 0.05;
        var json = ok
            ? "{\"status\":\"ok\"}"
            : "{\"status\":\"hit\",\"severity\":\"low\"}";
        return Task.FromResult(new ExternalCheckRunResult { Status = ExternalCheckResultStatus.Success, ResultJson = json });
    }
}

