# Slide snippets

Paste-ready code for session 3. Each block is short enough to read on a
projector. Every snippet says **which SDK key kind** it needs, because that is
the thing people get wrong first.

| | Flutter app | ASP.NET Core service |
|---|---|---|
| SDK | `launchdarkly_flutter_client_sdk` 4.24 | `LaunchDarkly.ServerSdk` 8.17 |
| Kind | client-side | server-side |
| Credential | **mobile key** `mob-...` on iOS/Android/desktop<br>**client-side ID** on web | **server SDK key** `sdk-...` |
| Secret? | No - it ships to the client | **Yes** - never leaves the server |
| Holds the ruleset? | No - receives evaluated values | Yes - evaluates locally |
| Adds SDK metadata to the context? | Yes (`ld_application`, `ld_device`) | **No** |

**Three credentials, one environment.** All three are issued per environment, so
a Test credential can never read Production. Only the server SDK key is secret:
the other two are designed to ship to untrusted clients and can only *read* one
environment's already-evaluated values.

> Which one the Flutter SDK wants depends on the build target, not on your code:
> **web → client-side ID**, **iOS / Android / macOS / Windows / Linux → mobile
> key**. Same `LDConfig` parameter either way.

Project `ld-sdk-quickstart`, environment `test`. Five flags, one per archetype:

| Archetype | Flag | Type |
|---|---|---|
| Release | `combo-upsell-banner` | boolean |
| Operational (kill switch) | `loyalty-api-enabled` | boolean, permanent |
| Remote config | `order-queue-tuning` | JSON |
| Migration | `checkout-payment-provider` | multivariate string |
| Entitlement | `staff-discount-override` | boolean, via segment |

---

## 1. Install

**.NET — server SDK key**

```bash
dotnet add package LaunchDarkly.ServerSdk --version 8.17.2
```

```csharp
using LaunchDarkly.Sdk;         // Context, EvaluationDetail<T>, EvaluationReason
using LaunchDarkly.Sdk.Server;  // LdClient, Configuration, Components
```

> The package is `LaunchDarkly.ServerSdk`; the namespaces are `LaunchDarkly.Sdk*`.
> They deliberately do not match.

**Flutter — mobile key or client-side ID**

```yaml
# pubspec.yaml
dependencies:
  launchdarkly_flutter_client_sdk: ^4.24.0
```

```dart
import 'package:launchdarkly_flutter_client_sdk/launchdarkly_flutter_client_sdk.dart';
```

---

## 2. Initialise

**.NET — server SDK key.** One client per process, as a singleton.

```csharp
var config = Configuration.Builder(sdkKey)       // sdk-... from the environment
    .StartWaitTime(TimeSpan.FromSeconds(5))      // how long the ctor blocks; default 10s
    .Build();

var client = new LdClient(config);               // build ONCE, never per request

if (!client.Initialized)
    logger.LogWarning("LaunchDarkly not ready - evaluations will fall back");
```

```csharp
// In ASP.NET Core, register it as a singleton and let DI dispose it:
builder.Services.AddSingleton<ILdClient>(_ => new LdClient(config));
```

**Flutter — mobile key / client-side ID.** The context is supplied at construction.

```dart
// Same parameter, different credential per build target:
const credential = kIsWeb
    ? String.fromEnvironment('LAUNCHDARKLY_CLIENT_SIDE_ID')  // web
    : String.fromEnvironment('LAUNCHDARKLY_MOBILE_KEY');     // iOS, Android, ...

final config = LDConfig(
  credential,                     // never hard-coded; --dart-define at build time
  AutoEnvAttributes.enabled,      // SDK adds ld_application / ld_device contexts
);

final client = LDClient(config, initialContext);

// start() can hang on a bad network. Always bound it.
final started = await client
    .start()
    .timeout(const Duration(seconds: 5), onTimeout: () => false);
```

---

## 3. Build a context

**.NET — server SDK key.** Nothing is added for you; what you set is what the
rules see.

```csharp
var context = Context.Builder("amy")   // stable key - drives percentage rollouts
    .Kind("user")                        // "user" is the default kind
    .Name("Counter staff")
    .Set("store_region", "north")        // custom attribute the rule targets
    .Build();
```

**Flutter — mobile key.** Builder per kind, then `build()`.

```dart
final builder = LDContextBuilder();
builder
    .kind('user', 'amy')
    .name('Amy (north store)')
    .setString('store_region', 'north');

final context = builder.build();
```

```dart
// Multi-context: call kind() again before build()
builder.kind('store', 'store-north-01').setString('format', 'kiosk');
```

---

## 4. Evaluate

**.NET — server SDK key.**

```csharp
// Value only:
bool showUpsell = client.BoolVariation("combo-upsell-banner", context, false);

// Value + why - use this while learning and debugging:
EvaluationDetail<bool> detail =
    client.BoolVariationDetail("combo-upsell-banner", context, false);

detail.Value;           // true / false
detail.VariationIndex;  // int? - NULL means you got your fallback back

detail.Reason.Kind;     // EvaluationReasonKind: Off | Fallthrough | TargetMatch
                        // | RuleMatch | PrerequisiteFailed | Error
detail.Reason.ToString();  // the documented form: "RULE_MATCH(0,<ruleId>)",
                           // "FALLTHROUGH", "ERROR(CLIENT_NOT_READY)"
```

> Watch out when you log these: `Reason.Kind.ToString()` gives the C# member
> name (`RuleMatch`), while `Reason.ToString()` gives the form you see in the
> LaunchDarkly docs and UI (`RULE_MATCH(0,...)`). Log the second one.

**Flutter — mobile key.** Same shape, no context argument: the client already
has the active context.

```dart
final showUpsell = client.boolVariation('combo-upsell-banner', false);

final detail = client.boolVariationDetail('combo-upsell-banner', false);
detail.value;           // true / false
detail.variationIndex;  // int? - null means fallback
detail.reason?.kind;    // LDKind.ruleMatch, LDKind.error, ...
detail.reason?.errorKind; // LDErrorKind.clientNotReady, .flagNotFound, ...
```

---

## 5. The fallback value

The last argument to every variation call. It is **the value your feature has
when the SDK cannot answer** — not a formality.

```csharp
//                                                    this one:
client.BoolVariationDetail("combo-upsell-banner", context, false);
```

It is served when:

| Cause | Reason you get back |
|---|---|
| SDK still starting up | `ERROR / CLIENT_NOT_READY` |
| LaunchDarkly unreachable, no cache | `ERROR / CLIENT_NOT_READY` |
| Flag key wrong, or archived **and deleted** | `ERROR / FLAG_NOT_FOUND` |
| Flag not marked available to client-side SDKs | `ERROR / FLAG_NOT_FOUND` |
| Wrong type for the call | `ERROR / WRONG_TYPE` |
| SDK in offline mode | `ERROR / CLIENT_NOT_READY` |

Archiving alone does **not** trigger it: an archived flag keeps serving its
last value to SDKs. It is deletion, or a key that never existed, that produces
`FLAG_NOT_FOUND`.

Pick it as the **safe state of the feature**. Here, "no upsell" is safe;
showing a combo the kitchen cannot make is not.

**Demonstrate it** by pointing the SDK somewhere that does not answer:

```csharp
.ServiceEndpoints(Components.ServiceEndpoints()
    .Streaming("http://localhost:1")     // dead port, on purpose
    .Polling("http://localhost:1")
    .Events("http://localhost:1"))
```

```dart
serviceEndpoints: ServiceEndpoints.custom(
  streaming: 'http://localhost:1',
  polling: 'http://localhost:1',
  events: 'http://localhost:1',
),
```

Both demos have this behind a switch — see the README.

---

## 6. Listen for changes

**Flutter — mobile key.** This is what makes the screen change without a
restart. The event carries only the changed keys; you re-evaluate for the value.

```dart
client.flagChanges.listen((event) {
  if (event.keys.contains('combo-upsell-banner')) {
    setState(() {
      showUpsell = client.boolVariation('combo-upsell-banner', false);
    });
  }
});
```

**.NET — server SDK key.** A server-side SDK streams the ruleset continuously,
so the next evaluation is simply current — usually you need no listener at all.
When you do want to react:

```csharp
client.FlagTracker.FlagChanged += (sender, e) =>
    logger.LogInformation("Flag {Key} changed", e.Key);
```

---

## 7. Change the context (client-side only)

**Flutter — mobile key.** The user signed in, or an attribute changed. The SDK
re-fetches values for the new context and the flag stream follows it.

```dart
final result = await client.identify(newContext);
// IdentifyComplete | IdentifyError | IdentifySuperseded
```

> There is no `identify()` on a server-side SDK. The context is an argument to
> every evaluation instead — one process serves many users at once.

---

## 8. Flags that are not booleans

**.NET — server SDK key.** A *multivariate string* for a migration:

```csharp
EvaluationDetail<string> detail = client.StringVariationDetail(
    "checkout-payment-provider", context, "legacy");   // fallback = the incumbent

switch (detail.Value)
{
    case "gateway-v2": return new GatewayV2Client();
    case "gateway-v3": return new GatewayV3Client();
    default:           return new LegacyGatewayClient();  // also catches unknown
}
```

> Handle the unknown variation. Someone can add a fourth gateway in
> LaunchDarkly tomorrow; deployed code must not crash on a value it has never
> seen.

A *JSON* flag as remote config:

```csharp
EvaluationDetail<LdValue> detail = client.JsonVariationDetail(
    "order-queue-tuning", context, LdValue.Null);

var batchSize = detail.Value.Get("batchSize").AsInt;   // parse defensively
```

> The fallback for a config flag should be **a real working configuration**, not
> an empty object. And give the flag a JSON schema in LaunchDarkly — a malformed
> variation is a production incident no compiler will catch.

---

## 9. Targeting a segment instead of an inline rule

The flag rule points at a **segment**; the segment holds the definition.

```
flag:    staff-discount-override
  rule:  context is in segment "store-managers"  ->  true
  default rule                                   ->  false

segment: store-managers
  rule:  role is one of ["manager"]
```

The application code is unchanged — it just evaluates a boolean:

```csharp
var mayOverride = client.BoolVariation("staff-discount-override", context, false);
```

What you get for it: the next flag needing the same audience references the same
segment instead of copying the rule. Change who counts as a manager once, and
every flag using it follows. Copied inline rules are how targeting drifts.

---

## 10. Metrics for a guarded rollout

A guarded rollout watches metrics and rolls back on its own. The application has
to send them.

**.NET — server SDK key.**

```csharp
// Numeric metric - the fourth argument is the value.
client.Track("checkout-latency-ms", context, LdValue.Null, latencyMs);

// Occurrence metric - no value; fire it only when it actually happened.
if (!succeeded) client.Track("checkout-error", context);
```

**Flutter — mobile key / client-side ID.**

```dart
client.track('checkout-latency-ms', metricValue: latencyMs);
client.track('checkout-error');
```

Three ways this silently produces nothing:

| Mistake | Symptom |
|---|---|
| Event key doesn't match the metric's event key | No data, no error |
| `Track` uses a different context than the evaluation | Metric can't be attributed to a variation |
| Process exits without flushing | Last batch lost, rollout skewed |

```csharp
// So, on shutdown:
client.FlushAndWait(TimeSpan.FromSeconds(2));
```

---

## 11. Shut down

**.NET — server SDK key.** `Dispose()` closes the stream and tries to deliver
pending analytics events first.

```csharp
client.FlushAndWait(TimeSpan.FromSeconds(2));  // bounded, returns whether it finished
client.Dispose();
```

**Flutter — mobile key.**

```dart
await subscription.cancel();
await client.close();
```
