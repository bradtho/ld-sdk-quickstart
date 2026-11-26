using System.Diagnostics;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server.Interfaces;

namespace LdQuickstart.Server.Scenarios;

/// <summary>
/// ARCHETYPE 5 - MIGRATION (multivariate string) + the METRICS a GUARDED
/// ROLLOUT needs.
///
/// The flag answers "which payment gateway does checkout call?" with one of
/// three strings, so this is not a boolean on/off - it is a dial with named
/// positions.
///
/// This is also the flag the guarded rollout runs on. A guarded rollout shifts
/// traffic off 'legacy' while LaunchDarkly watches metrics, and rolls back on
/// its own if they get worse. For that to work the application has to SEND
/// those metrics - which is the code at the bottom of this file, and the part
/// people usually leave out.
///
/// Two metrics are defined in LaunchDarkly by scripts/provision.py:
///   * checkout-error      - occurrence, lower is better
///   * checkout-latency-ms - numeric, lower is better
///
/// The gateways behave differently on purpose, so the rollout has something
/// real to react to:
///   legacy      ~120ms,  1% errors   (the incumbent)
///   gateway-v2   ~70ms,  1% errors   (genuinely better - a clean rollout)
///   gateway-v3  ~450ms, 25% errors   (worse - a guarded rollout should
///                                     detect this and roll back)
/// </summary>
public static class MigrationScenario
{
    public const string FlagKey = "checkout-payment-provider";

    // Fall back to the incumbent. If we cannot reach LaunchDarkly, keep using
    // the gateway that was working yesterday - never fall back to the thing
    // you are still in the middle of migrating to.
    private const string Fallback = "legacy";

    private record GatewayProfile(int MedianMs, int JitterMs, double ErrorRate);

    private static readonly Dictionary<string, GatewayProfile> Gateways = new()
    {
        ["legacy"] = new GatewayProfile(120, 40, 0.01),
        ["gateway-v2"] = new GatewayProfile(70, 25, 0.01),
        ["gateway-v3"] = new GatewayProfile(450, 150, 0.25),
    };

    public static void Map(WebApplication app) =>
        app.MapMethods("/checkout", new[] { "GET", "POST" }, async (
            ILdClient ld,
            ILoggerFactory loggerFactory,
            string? userKey,
            string? storeRegion) =>
        {
            var log = loggerFactory.CreateLogger("Checkout");

            // The context must be built ONCE and used for both the evaluation
            // and the Track calls. If they differ, LaunchDarkly cannot attribute
            // the metric to the variation that produced it, and the guarded
            // rollout is reading noise.
            var context = Contexts.ForUser(userKey, storeRegion: storeRegion);

            EvaluationDetail<string> detail =
                ld.StringVariationDetail(FlagKey, context, Fallback);

            var gateway = detail.Value;

            // ---- the call this flag is choosing between ---------------------
            var stopwatch = Stopwatch.StartNew();
            var succeeded = await CallGatewayAsync(gateway);
            stopwatch.Stop();
            var latencyMs = stopwatch.Elapsed.TotalMilliseconds;

            // ---- THE METRICS -----------------------------------------------
            // This is what a guarded rollout consumes. Without these two calls
            // the rollout has nothing to guard and will happily promote a
            // gateway that is failing a quarter of its requests.
            //
            // Track(eventKey, context, data, metricValue):
            //   * eventKey must match the metric's event key in LaunchDarkly
            //     exactly - a typo here is silent, you just get no data;
            //   * context must be the same one you evaluated with;
            //   * metricValue is only read by NUMERIC metrics.

            // Numeric metric: the measured latency.
            ld.Track("checkout-latency-ms", context, LdValue.Null, latencyMs);

            // Occurrence metric: fired only when something actually failed, so
            // the metric counts failures rather than requests.
            if (!succeeded)
            {
                ld.Track("checkout-error", context);
                log.LogWarning("Checkout failed on {Gateway} after {Latency:F0}ms",
                    gateway, latencyMs);
            }

            var body = new
            {
                archetype = "migration",
                context = new { key = context.Key },
                gateway,
                succeeded,
                latencyMs = Math.Round(latencyMs, 1),
                metricsSent = succeeded
                    ? new[] { "checkout-latency-ms" }
                    : new[] { "checkout-latency-ms", "checkout-error" },
                evaluation = Contexts.Describe(FlagKey, detail, Fallback),
            };

            // A failed payment is a 502 to the caller, but the flag evaluation
            // and the metrics were still recorded correctly.
            return succeeded ? Results.Ok(body) : Results.Json(body, statusCode: 502);
        }).WithName("Checkout");

    /// <summary>
    /// Stands in for the real gateway call. The delay is genuine, so the
    /// latency the Stopwatch measures is genuine too - we are not inventing
    /// the number we send to LaunchDarkly.
    /// </summary>
    private static async Task<bool> CallGatewayAsync(string gateway)
    {
        var profile = Gateways.TryGetValue(gateway, out var p)
            ? p
            // An unknown variation means someone added a gateway in
            // LaunchDarkly that this build does not know about. Treat it as the
            // incumbent rather than crashing - new variations must not break
            // deployed code.
            : Gateways[Fallback];

        var jitter = Random.Shared.Next(-profile.JitterMs, profile.JitterMs + 1);
        await Task.Delay(Math.Max(1, profile.MedianMs + jitter));

        return Random.Shared.NextDouble() >= profile.ErrorRate;
    }
}
