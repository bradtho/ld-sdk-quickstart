using LaunchDarkly.Sdk;

namespace LdQuickstart.Server.Scenarios;

/// <summary>
/// Builds the evaluation context from a request.
///
/// Worth saying out loud in the session: a SERVER-side SDK adds nothing to
/// this. Whatever you set here is exactly what the targeting rules see. The
/// client-side SDKs add ld_application / ld_device for you; this one does not.
/// </summary>
public static class Contexts
{
    /// <param name="key">
    /// Stable identifier for the person. This is what percentage rollouts and
    /// guarded rollouts bucket on, so it must be the same value every time for
    /// the same person - not a request id, not a session id.
    /// </param>
    public static Context ForUser(
        string? key,
        string? storeRegion = null,
        string? role = null)
    {
        var builder = Context.Builder(string.IsNullOrWhiteSpace(key) ? "anonymous" : key)
            .Kind("user");   // "user" is the default kind; written out for clarity

        if (!string.IsNullOrWhiteSpace(storeRegion))
            builder.Set("store_region", storeRegion);

        if (!string.IsNullOrWhiteSpace(role))
            builder.Set("role", role);

        return builder.Build();
    }

    /// <summary>
    /// Shapes an EvaluationDetail into the response body. `servedFallback` is
    /// the honest answer to "did LaunchDarkly actually decide this?" -
    /// VariationIndex is null exactly when it did not.
    /// </summary>
    public static object Describe<T>(string flagKey, EvaluationDetail<T> detail, T fallback)
        => new
        {
            flagKey,
            value = detail.Value,
            variationIndex = detail.VariationIndex,
            reason = new
            {
                kind = detail.Reason.Kind.ToString(),
                ruleIndex = detail.Reason.RuleIndex,
                ruleId = detail.Reason.RuleId,
                full = detail.Reason.ToString(),
            },
            servedFallback = detail.VariationIndex is null,
            fallbackValue = fallback,
        };
}
