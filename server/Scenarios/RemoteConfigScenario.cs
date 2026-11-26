using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server.Interfaces;

namespace LdQuickstart.Server.Scenarios;

/// <summary>
/// ARCHETYPE 3 - REMOTE CONFIG (a JSON flag).
///
/// A flag does not have to be a boolean, and it does not have to gate a
/// feature. Here it carries CONFIGURATION: queue batch size, timeout and
/// retry count. Tuning the queue for peak trading becomes a flag change
/// watched by the same approval and audit trail as everything else, instead
/// of an environment variable and a redeploy.
///
/// The trade-off worth naming: the JSON is unvalidated by default. Give the
/// flag a JSON schema in LaunchDarkly, and still parse defensively here,
/// because a malformed variation is a production incident that no compiler
/// will catch.
/// </summary>
public static class RemoteConfigScenario
{
    public const string FlagKey = "order-queue-tuning";

    /// <summary>
    /// The fallback is a real, working configuration - not an empty object.
    /// If LaunchDarkly is unreachable the queue must still run, so these are
    /// the values the service is happy to run on indefinitely.
    /// </summary>
    private static readonly QueueTuning Fallback = new(BatchSize: 10, TimeoutMs: 2000, MaxRetries: 2);

    public static void Map(WebApplication app) =>
        app.MapGet("/orders/queue-config", (ILdClient ld, string? userKey) =>
        {
            var context = Contexts.ForUser(userKey);

            // JsonVariationDetail hands back an LdValue - the SDK's own JSON
            // type - which you then read field by field.
            EvaluationDetail<LdValue> detail = ld.JsonVariationDetail(
                FlagKey, context, LdValue.Null);

            var tuning = Parse(detail.Value);

            return Results.Ok(new
            {
                archetype = "remote-config",
                tuning,
                usedFallbackConfig = tuning.Equals(Fallback) && detail.VariationIndex is null,
                raw = detail.Value.ToJsonString(),
                evaluation = Contexts.Describe(FlagKey, detail, LdValue.Null),
            });
        }).WithName("QueueConfig");

    /// <summary>
    /// Defensive parse: any missing or wrong-typed field falls back to the
    /// known-good value rather than throwing.
    /// </summary>
    private static QueueTuning Parse(LdValue value)
    {
        if (value.Type != LdValueType.Object) return Fallback;

        return new QueueTuning(
            BatchSize: value.Get("batchSize").AsInt is var b and > 0 ? b : Fallback.BatchSize,
            TimeoutMs: value.Get("timeoutMs").AsInt is var t and > 0 ? t : Fallback.TimeoutMs,
            MaxRetries: value.Get("maxRetries").AsInt is var r and >= 0 ? r : Fallback.MaxRetries);
    }

    public record QueueTuning(int BatchSize, int TimeoutMs, int MaxRetries);
}
