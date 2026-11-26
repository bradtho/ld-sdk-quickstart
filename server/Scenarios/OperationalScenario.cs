using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server.Interfaces;

namespace LdQuickstart.Server.Scenarios;

/// <summary>
/// ARCHETYPE 2 - OPERATIONAL (kill switch).
///
/// Not a release gate. A permanent switch that lets you shed a dependency the
/// moment it misbehaves, without a deploy and without a code change. The
/// loyalty provider goes slow at peak; you turn this off, orders keep
/// completing, customers just do not earn points for an hour.
///
/// Two things make this different from a release flag:
///   * it is PERMANENT - it never gets cleaned up, and that is correct;
///   * the off variation is the safe degraded path, so turning targeting off
///     in a panic does the right thing.
/// </summary>
public static class OperationalScenario
{
    public const string FlagKey = "loyalty-api-enabled";

    // If we cannot reach LaunchDarkly we would rather skip loyalty than hang
    // the checkout on a provider we were about to bypass anyway.
    private const bool Fallback = false;

    public static void Map(WebApplication app) =>
        app.MapGet("/loyalty/points", (ILdClient ld, string? userKey) =>
        {
            var context = Contexts.ForUser(userKey);

            EvaluationDetail<bool> detail =
                ld.BoolVariationDetail(FlagKey, context, Fallback);

            // The flag wraps the CALL, not the response. When it is off we skip
            // the dependency entirely - we do not call it and discard the
            // result, which would leave the latency and the blast radius intact.
            if (!detail.Value)
            {
                return Results.Ok(new
                {
                    archetype = "operational",
                    loyaltyCalled = false,
                    points = 0,
                    note = "Loyalty provider bypassed by kill switch - the order "
                         + "still completes, the customer just earns no points.",
                    evaluation = Contexts.Describe(FlagKey, detail, Fallback),
                });
            }

            var points = CallLoyaltyProvider(context.Key);
            return Results.Ok(new
            {
                archetype = "operational",
                loyaltyCalled = true,
                points,
                note = "Loyalty provider called normally.",
                evaluation = Contexts.Describe(FlagKey, detail, Fallback),
            });
        }).WithName("LoyaltyPoints");

    /// <summary>Stand-in for the third-party call this flag protects.</summary>
    private static int CallLoyaltyProvider(string userKey)
        => Math.Abs(userKey.GetHashCode() % 500) + 10;
}
