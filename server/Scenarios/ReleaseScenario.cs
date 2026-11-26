using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server.Interfaces;

namespace LdQuickstart.Server.Scenarios;

/// <summary>
/// ARCHETYPE 1 - RELEASE.
///
/// The ordinary one: ship a change to some people before everyone. Temporary
/// by nature - once the banner is on for everybody, this flag should be
/// removed. That removal is the subject of the flag-lifecycle session.
///
/// Targeting in Test: store_region "north" gets it, everyone else does not.
/// </summary>
public static class ReleaseScenario
{
    public const string FlagKey = "combo-upsell-banner";

    // The safe state: no upsell. Showing a combo the kitchen cannot make is
    // worse than showing nothing.
    private const bool Fallback = false;

    public static void Map(WebApplication app) =>
        app.MapGet("/combo-upsell", (ILdClient ld, string? userKey, string? storeRegion) =>
        {
            var context = Contexts.ForUser(userKey, storeRegion: storeRegion ?? "south");

            EvaluationDetail<bool> detail =
                ld.BoolVariationDetail(FlagKey, context, Fallback);

            return Results.Ok(new
            {
                archetype = "release",
                context = new { key = context.Key, store_region = storeRegion ?? "south" },
                showComboUpsell = detail.Value,
                evaluation = Contexts.Describe(FlagKey, detail, Fallback),
            });
        }).WithName("ComboUpsell");
}
