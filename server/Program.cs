// LaunchDarkly SDK Quickstart - server side.
//
// ASP.NET Core minimal API using the LaunchDarkly *server-side* .NET SDK.
//
// The two SDKs in this repo differ in ways that matter:
//   * this service uses a SERVER SDK key (sdk-...), which must stay on the
//     server. It is the only one of the three credentials that is secret.
//   * the app in ../mobile uses a mobile key or a client-side ID, both of which
//     ship to the client and are public by design.
// A server-side SDK holds the whole ruleset in memory and evaluates locally, so
// it also adds NO SDK metadata to the evaluation context. Whatever attributes
// you set are exactly what the rules see - nothing more.
//
// Five flag archetypes, one file each, in Scenarios/:
//   release      /combo-upsell          boolean, rule on a custom attribute
//   operational  /loyalty/points        permanent kill switch for a dependency
//   remote-config/orders/queue-config   JSON flag carrying tuning values
//   migration    /checkout              multivariate string + guarded-rollout metrics
//   entitlement  /staff/discount        boolean gated on a reusable segment

using LaunchDarkly.Logging;                  // Logs
// LaunchDarkly and ASP.NET both define LogLevel, so alias the LaunchDarkly one.
using LdLogLevel = LaunchDarkly.Logging.LogLevel;
using LaunchDarkly.Sdk.Server;               // LdClient, Configuration, Components
using LaunchDarkly.Sdk.Server.Interfaces;    // ILdClient
using LdQuickstart.Server.Scenarios;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// 1. Configuration. The SDK key is never hard-coded: it comes from the
//    environment (LD_SDK_KEY) or appsettings/user-secrets. No key, no start.
// ---------------------------------------------------------------------------
var sdkKey =
    builder.Configuration["LD_SDK_KEY"] ??
    builder.Configuration["LaunchDarkly:SdkKey"];

if (string.IsNullOrWhiteSpace(sdkKey))
{
    Console.Error.WriteLine(
        "No server SDK key found. Set it before starting, e.g.\n" +
        "  export LD_SDK_KEY='sdk-...'\n" +
        "Find it under Project settings > Environments > Test > SDK key.");
    return 1;
}

// TryParse, not GetValue<bool>: an empty or malformed value means "off", it
// should not take the service down on start-up.
var demoUnreachable =
    bool.TryParse(builder.Configuration["LD_DEMO_UNREACHABLE"], out var du) && du;

// ---------------------------------------------------------------------------
// 2. THE FALLBACK VALUE.
//
//    Every variation call takes a default. It is not a "probably unused"
//    argument - it is the value your feature actually has whenever the SDK
//    cannot give you a real one. It is served when:
//
//      a. the client has not finished initialising yet (reason ERROR /
//         CLIENT_NOT_READY) - including the window inside StartWaitTime;
//      b. LaunchDarkly is unreachable and there is no cached ruleset;
//      c. the flag key does not exist, or was archived AND deleted
//         (ERROR / FLAG_NOT_FOUND) - also what a typo in the key looks like;
//      d. the flag is the wrong type for the call you made (ERROR / WRONG_TYPE);
//      e. the SDK is in offline mode (Configuration.Offline(true)).
//
//    Each scenario in Scenarios/ declares its own fallback and says why it
//    chose that value. They are not all `false`: the kill switch defaults to
//    bypassed, remote config defaults to a real working configuration, and the
//    migration defaults to the incumbent gateway.
//
//    To SEE it happen, start with LD_DEMO_UNREACHABLE=true. That points the
//    SDK at a dead port, so start-up times out and every evaluation falls back
//    - with the reason to prove it.
// ---------------------------------------------------------------------------

// Set LD_LOG_LEVEL=Debug to watch the SDK work: the streaming connection, each
// ruleset update, and every batch of analytics and metric events it delivers.
// Worth showing in the session - "is it even sending my metrics?" is answerable.
var ldLogLevel = Enum.TryParse<LdLogLevel>(
    builder.Configuration["LD_LOG_LEVEL"], ignoreCase: true, out var lvl)
        ? lvl
        : LdLogLevel.Info;

var ldConfig = Configuration.Builder(sdkKey)
    // How long `new LdClient(...)` blocks waiting for the first ruleset.
    // Default is 10s; 0 returns immediately and evaluations fall back until the
    // ruleset lands. 5s is a reasonable web-service value.
    .StartWaitTime(TimeSpan.FromSeconds(demoUnreachable ? 2 : 5))
    .Logging(Components.Logging(Logs.ToConsole).Level(ldLogLevel));

if (demoUnreachable)
{
    // The same API you would use for the Relay Proxy, the EU instance or a
    // federal instance - just pointed somewhere that does not answer.
    ldConfig.ServiceEndpoints(Components.ServiceEndpoints()
        .Streaming("http://localhost:1")
        .Polling("http://localhost:1")
        .Events("http://localhost:1"));
}

// ---------------------------------------------------------------------------
// 3. ONE client for the whole process, as a singleton.
//
//    The client keeps a streaming connection and an in-memory ruleset. Build it
//    once. Creating one per request would reconnect every request and evaluate
//    against a cold cache - the most common server-side mistake there is.
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<ILdClient>(_ => new LdClient(ldConfig.Build()));

var app = builder.Build();
var log = app.Logger;
var ldClient = app.Services.GetRequiredService<ILdClient>();

log.LogInformation("LaunchDarkly client initialised: {Initialized}{Demo}",
    ldClient.Initialized,
    demoUnreachable ? " (DEMO: pointed at an unreachable host)" : "");

// Useful on stage: toggle a flag in the UI and watch this line appear.
ldClient.FlagTracker.FlagChanged += (_, e) =>
    log.LogInformation("Flag '{Key}' changed in LaunchDarkly", e.Key);

// ---------------------------------------------------------------------------
// 4. Graceful shutdown. Dispose() shuts the client down and tries to deliver
//    pending analytics events first - which includes the metric events the
//    guarded rollout depends on, so losing them on exit would quietly skew it.
//    FlushAndWait bounds that delivery and tells you whether it finished.
// ---------------------------------------------------------------------------
app.Lifetime.ApplicationStopping.Register(() =>
{
    var delivered = ldClient.FlushAndWait(TimeSpan.FromSeconds(2));
    log.LogInformation("Flushed pending LaunchDarkly events: {Delivered}", delivered);
});

// ---------------------------------------------------------------------------
// 5. The scenarios.
// ---------------------------------------------------------------------------
ReleaseScenario.Map(app);
OperationalScenario.Map(app);
RemoteConfigScenario.Map(app);
MigrationScenario.Map(app);
EntitlementScenario.Map(app);

app.MapGet("/health", (ILdClient ld) => Results.Ok(new
{
    status = ld.Initialized ? "ready" : "degraded - serving fallbacks",
    sdkInitialised = ld.Initialized,
}));

app.MapGet("/", () => Results.Text(
    """
    LaunchDarkly SDK Quickstart - server side (.NET server SDK)

      release       GET /combo-upsell?userKey=amy&storeRegion=north
                      -> true (RULE_MATCH); storeRegion=south -> false (FALLTHROUGH)

      operational   GET /loyalty/points?userKey=amy
                      -> points; turn 'loyalty-api-enabled' off to bypass

      remote config GET /orders/queue-config?userKey=amy
                      -> batchSize / timeoutMs / maxRetries from a JSON flag

      migration     GET /checkout?userKey=amy
                      -> which gateway, measured latency, and the metric events
                         a guarded rollout reads

      entitlement   GET /staff/discount?userKey=amy&role=manager
                      -> true via the store-managers segment; role=cashier -> false

      GET /health

    Start with LD_DEMO_UNREACHABLE=true to watch every call fall back instead.
    """, "text/plain"));

app.Run();
return 0;
