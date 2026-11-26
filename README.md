# LaunchDarkly SDK Quickstart

A runnable demo of the **LaunchDarkly .NET server-side SDK** and the
**LaunchDarkly Flutter client-side SDK**, built around five different *kinds*
of feature flag — because "feature flag" is not one thing — plus the metrics a
**guarded rollout** needs.

**Everything runs in containers.** Nothing is installed on the host but Podman.

| | What it is | SDK | Credential |
|---|---|---|---|
| [`server/`](server/) | ASP.NET Core minimal API, .NET 8 | `LaunchDarkly.ServerSdk` 8.17.2 (server-side) | **server SDK key** `sdk-…` — secret |
| [`mobile/`](mobile/) | Flutter app, one screen, built for web | `launchdarkly_flutter_client_sdk` 4.24.0 (client-side) | **client-side ID** — public |

Slide-ready snippets: [SAMPLES.md](SAMPLES.md).

---

## 1. The five flag archetypes

Each lives in its own file under [`server/Scenarios/`](server/Scenarios/), so you
can open one per scenario.

| # | Archetype | Flag | Endpoint | Teaches |
|---|---|---|---|---|
| 1 | **Release** | `combo-upsell-banner` | `/combo-upsell` | Boolean, targeting rule on a custom attribute. Temporary — it should be removed once rolled out. |
| 2 | **Operational** | `loyalty-api-enabled` | `/loyalty/points` | Kill switch for a dependency. **Permanent.** Off = safe degraded path. |
| 3 | **Remote config** | `order-queue-tuning` | `/orders/queue-config` | A **JSON** flag carrying tuning values. Config change, not a redeploy. |
| 4 | **Migration** | `checkout-payment-provider` | `/checkout` | **Multivariate string** + the **metrics a guarded rollout consumes**. |
| 5 | **Entitlement** | `staff-discount-override` | `/staff/discount` | Gated on a reusable **segment**, not an inline rule. Permanent. |

Note how the **fallback value differs per archetype** and is never just "false
because booleans default to false":

- release → `false` (don't show an upsell you can't fulfil)
- operational → `false` (skip the dependency you were about to bypass anyway)
- remote config → **a real working configuration**, not an empty object
- migration → `"legacy"` (the incumbent — never fall back to what you're migrating *to*)
- entitlement → `false` (deny by default: if you can't confirm it, don't grant it)

---

## 2. Setup

### Prerequisites

**Podman**, with a machine running:

```bash
podman machine list
```

> **Give the machine at least 4 GiB (6 GiB is comfortable).** The Flutter build
> is OOM-killed at 2 GiB — it dies with `exit status 137` partway through
> `flutter precache`, which is not an obvious error message. Fix with:
>
> ```bash
> podman machine stop && podman machine set --memory 6144 && podman machine start
> ```
>
> The .NET image builds fine at 2 GiB; only Flutter needs the room.

No .NET SDK, no Flutter SDK, no compose provider — the `Makefile` drives plain
`podman`. Built and tested on Podman 6.2.0, arm64, rootless.

### Create the LaunchDarkly side

[`scripts/provision.py`](scripts/provision.py) creates the project, all five
flags with their targeting, the `store-managers` segment and both metrics. It is
**idempotent** — re-running reports what already exists.

```bash
LD_API_TOKEN=api-... python3 scripts/provision.py
```

Change `PROJECT_KEY` at the top of the script, or pass `--project my-key`, to
provision it somewhere else. Nothing in this repo is specific to any customer.

### Credentials

```bash
cp .env.example .env
# fill in LD_SDK_KEY and LD_CLIENT_SIDE_ID from Project settings > Environments > Test
```

`.env` is gitignored. The three credentials are **not** interchangeable:

- **`LD_SDK_KEY`** (`sdk-…`) — **secret**. Passed to the server container at run
  time, never baked into an image, never shipped to a client.
- **`LD_CLIENT_SIDE_ID`** — **public by design**. Compiled into the web bundle,
  exactly as a mobile key ships inside an app bundle.
- **`LD_MOBILE_KEY`** (`mob-…`) — also public; only needed if you build the
  Flutter app for iOS/Android instead of web.

### Build and run

```bash
make build && make up
```

Service on **http://localhost:5080**, app on **http://localhost:8081**.
`make help` lists everything.

---

## 3. What working looks like

```bash
make check
```

walks all five scenarios. The release flag, for example:

```json
{
  "archetype": "release",
  "context": { "key": "amy", "store_region": "north" },
  "showComboUpsell": true,
  "evaluation": {
    "flagKey": "combo-upsell-banner",
    "value": true,
    "variationIndex": 0,
    "reason": { "kind": "RuleMatch", "ruleIndex": 0, "full": "RULE_MATCH(0,…)" },
    "servedFallback": false,
    "fallbackValue": false
  }
}
```

Every response carries the **evaluation reason** and `servedFallback`, so you can
always tell a real `false` from a fallback `false`.

### The app

Open **http://localhost:8081**. The screen shows a combo-upsell card driven by
`combo-upsell-banner`, a user switcher that calls `identify()`, and a panel with
the credential kind, the context, the value, the reason and whether the value is
the fallback.

**The moment that sells the session:** with the app open, toggle
`combo-upsell-banner` targeting **off** in the LaunchDarkly UI. The banner
disappears **in the browser with no reload**, and the reason flips to `OFF`.
That's the `client.flagChanges` stream in
[`mobile/lib/main.dart`](mobile/lib/main.dart).

> If the banner doesn't move, hard-refresh (`Cmd+Shift+R`). After
> `make fallback`/`make normal` an already-open tab keeps serving the previous
> bundle.

---

## 4. Guarded rollout (Guardian)

A guarded rollout shifts traffic between variations while LaunchDarkly watches
metrics, and **rolls back on its own** if they get worse. For that to work your
application has to *send* those metrics — which is the part people leave out.

`scripts/provision.py` creates two:

| Metric | Kind | Event key | Direction |
|---|---|---|---|
| `checkout-errors` | occurrence | `checkout-error` | lower is better |
| `checkout-latency-ms` | numeric | `checkout-latency-ms` | lower is better |

[`MigrationScenario.cs`](server/Scenarios/MigrationScenario.cs) sends them:

```csharp
// Numeric metric: the measured latency.
ld.Track("checkout-latency-ms", context, LdValue.Null, latencyMs);

// Occurrence metric: only on failure, so it counts failures not requests.
if (!succeeded) ld.Track("checkout-error", context);
```

Three things to get right, each of which fails silently:

1. **The event key must match the metric's event key exactly.** A typo gives you
   no data and no error.
2. **Track with the same context you evaluated with.** Otherwise LaunchDarkly
   can't attribute the metric to the variation that caused it.
3. **Flush on shutdown.** Metric events are batched; losing the last batch on
   exit quietly skews the rollout. `FlushAndWait` on `ApplicationStopping`
   handles it here.

### Running one

The gateways behave differently **on purpose**, so the rollout has something real
to react to:

| Variation | Latency | Errors | |
|---|---|---|---|
| `legacy` | ~120 ms | 1% | the incumbent |
| `gateway-v2` | ~70 ms | 1% | genuinely better — a clean rollout |
| `gateway-v3` | ~450 ms | **25%** | worse — **a guarded rollout should roll this back** |

```bash
make up && make traffic
```

```bash
make traffic REQUESTS=1000 CONCURRENCY=25
```

Then in the LaunchDarkly UI, on `checkout-payment-provider`, start a guarded
rollout from `legacy` to `gateway-v2` and watch it proceed — then try
`gateway-v3` and watch it roll back.

To confirm the events are really going out:

```bash
make down && LD_LOG_LEVEL=Debug make up && make logs
```

You'll see each batch posted with `response status 202`, including the
`{"kind":"custom","key":"checkout-latency-ms",…,"metricValue":…}` entries.

---

## 5. The fallback-value lesson

Every variation call takes a default. It's the value your feature *has* whenever
the SDK can't supply a real one: the SDK still starting, LaunchDarkly unreachable
with no cache, a flag key that doesn't exist or was deleted, a flag not available
to that SDK kind, or the wrong type for the call.

```bash
make fallback
```

rebuilds and restarts both pointed at a dead port. The same request that returned
`true` now returns:

```json
{
  "value": false,
  "variationIndex": null,
  "reason": { "kind": "Error", "full": "ERROR(CLIENT_NOT_READY)" },
  "servedFallback": true
}
```

```bash
make normal
```

puts it back. **`variationIndex: null` is the tell** — no variation was served,
so the value you're holding is the one you passed in. The service keeps serving
traffic; it just serves the safe state. The fallback is a product decision, not a
formality.

---

## 6. Removing it

```bash
make clean
```

removes containers and images; `podman image prune -f` clears the build cache.
Delete the directory and nothing remains on the machine.

---

## 7. Two things worth saying out loud

**The credentials are not interchangeable.** A server SDK key in a client
application leaks your whole ruleset — it's the only one of the three that's
secret. Mobile keys and client-side IDs are public by design and can only *read*
one environment. All three are per-environment, so a Test credential can never
reach Production.

**Server-side SDKs add nothing to the context.** The Flutter app, with
`AutoEnvAttributes.enabled`, contributes `ld_application` and `ld_device`
contexts — app version, OS, locale — so you can target them without writing any
code. The .NET service contributes nothing: the rules see exactly the attributes
you set. If a rule works on the client but not the backend, this is usually why.
