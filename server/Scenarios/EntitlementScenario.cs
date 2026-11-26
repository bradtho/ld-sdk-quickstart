using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server.Interfaces;

namespace LdQuickstart.Server.Scenarios;

/// <summary>
/// ARCHETYPE 4 - ENTITLEMENT, gated on a SEGMENT.
///
/// The flag's rule does not spell out who the managers are. It points at the
/// "store-managers" SEGMENT, which is defined once in LaunchDarkly as
/// role == "manager".
///
/// Why that matters: the next flag that needs the same audience references the
/// same segment instead of copying the rule. When the definition of "manager"
/// changes you change it in one place, and every flag using it follows. Copied
/// inline rules are how targeting drifts out of sync.
///
/// Also note this flag is PERMANENT. An entitlement is not a release you
/// finish rolling out - it is how the product works.
/// </summary>
public static class EntitlementScenario
{
    public const string FlagKey = "staff-discount-override";

    // Deny by default. For anything permission-shaped the fallback must be the
    // closed state: if we cannot confirm the entitlement, we do not grant it.
    private const bool Fallback = false;

    public static void Map(WebApplication app) =>
        app.MapGet("/staff/discount", (ILdClient ld, string? userKey, string? role) =>
        {
            // `role` is what the SEGMENT matches on. The flag rule never
            // mentions it - the segment does.
            var context = Contexts.ForUser(userKey, role: role ?? "cashier");

            EvaluationDetail<bool> detail =
                ld.BoolVariationDetail(FlagKey, context, Fallback);

            return Results.Ok(new
            {
                archetype = "entitlement",
                context = new { key = context.Key, role = role ?? "cashier" },
                mayOverrideDiscount = detail.Value,
                note = detail.Value
                    ? "Matched the store-managers segment."
                    : "Not in the store-managers segment - override blocked.",
                evaluation = Contexts.Describe(FlagKey, detail, Fallback),
            });
        }).WithName("StaffDiscount");
}
