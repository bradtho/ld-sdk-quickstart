#!/usr/bin/env python3
"""
Provision the LaunchDarkly side of this demo: project, environments, flags,
a segment and the metrics a guarded rollout needs.

Idempotent - safe to re-run. Existing resources are left alone and reported.

Usage:
    export LD_API_TOKEN=api-...        # a Writer token is enough
    python3 scripts/provision.py
    python3 scripts/provision.py --project my-own-key   # override the key

Nothing here is specific to any customer: change PROJECT_KEY and run it.
"""
import argparse
import json
import os
import sys
import urllib.error
import urllib.request

API = "https://app.launchdarkly.com/api/v2"
PROJECT_KEY = "ld-sdk-quickstart"
PROJECT_NAME = "LaunchDarkly SDK Quickstart"

TOKEN = os.environ.get("LD_API_TOKEN", "")


def call(method, path, body=None, semantic_patch=False):
    """One API call. Returns (status, parsed_json_or_text)."""
    url = f"{API}{path}"
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", TOKEN)
    if data:
        ct = "application/json"
        if semantic_patch:
            ct += "; domain-model=launchdarkly.semanticpatch"
        req.add_header("Content-Type", ct)
    try:
        with urllib.request.urlopen(req) as r:
            return r.status, json.loads(r.read().decode() or "{}")
    except urllib.error.HTTPError as e:
        raw = e.read().decode()
        try:
            return e.code, json.loads(raw)
        except json.JSONDecodeError:
            return e.code, raw


def step(label, method, path, body=None, semantic_patch=False, ok_conflict=True):
    status, payload = call(method, path, body, semantic_patch)
    if 200 <= status < 300:
        print(f"  [created] {label}")
        return payload
    if status == 409 and ok_conflict:
        print(f"  [exists ] {label}")
        return None
    code = payload.get("code") if isinstance(payload, dict) else None
    msg_text = payload.get("message", "") if isinstance(payload, dict) else str(payload)
    # LaunchDarkly rejects a re-applied rule as an invalid patch rather than a
    # conflict. That means the rule is already there, which is what we wanted.
    already_there = (
        code == "conflict"
        or "duplicate" in msg_text.lower()
        or "already exists" in msg_text.lower()
    )
    if status in (400, 409) and already_there and ok_conflict:
        print(f"  [exists ] {label}")
        return None
    msg = payload.get("message") if isinstance(payload, dict) else payload
    print(f"  [FAILED ] {label}: HTTP {status} {code or ''} {msg}")
    return {"__failed__": True}


# --------------------------------------------------------------------------
# Flag definitions. One per LaunchDarkly flag archetype, so the session can
# show that "feature flag" is not one thing.
# --------------------------------------------------------------------------
def flag_defs():
    return [
        # 1. RELEASE - the ordinary "ship it to some people" boolean.
        dict(
            key="combo-upsell-banner",
            name="Combo upsell banner",
            description="RELEASE flag. Shows the combo upsell banner in the "
                        "client app and tells the order service whether to "
                        "attach a combo upsell to the order summary.",
            kind="boolean",
            temporary=True,
            tags=["archetype-release", "demo"],
            clientSideAvailability={"usingMobileKey": True, "usingEnvironmentId": True},
            variations=[
                {"value": True, "name": "Banner on"},
                {"value": False, "name": "Banner off"},
            ],
            defaults={"onVariation": 0, "offVariation": 1},
        ),
        # 2. OPERATIONAL - a kill switch for a dependency. Permanent.
        dict(
            key="loyalty-api-enabled",
            name="Loyalty API enabled",
            description="OPERATIONAL kill switch. Turn off to bypass the "
                        "loyalty provider instantly when it is slow or down - "
                        "orders still complete, just without points.",
            kind="boolean",
            temporary=False,
            tags=["archetype-operational", "demo"],
            variations=[
                {"value": True, "name": "Call the loyalty API"},
                {"value": False, "name": "Bypass the loyalty API"},
            ],
            defaults={"onVariation": 0, "offVariation": 1},
        ),
        # 3. REMOTE CONFIG - a JSON flag carrying tuning values.
        dict(
            key="order-queue-tuning",
            name="Order queue tuning",
            description="REMOTE CONFIG flag. Queue batch size, timeout and "
                        "retry count as JSON, so tuning the queue is a flag "
                        "change rather than a redeploy.",
            kind="multivariate",
            temporary=False,
            tags=["archetype-remote-config", "demo"],
            variations=[
                {"value": {"batchSize": 10, "timeoutMs": 2000, "maxRetries": 2},
                 "name": "Conservative",
                 "description": "Safe defaults for normal trading"},
                {"value": {"batchSize": 100, "timeoutMs": 500, "maxRetries": 5},
                 "name": "Aggressive",
                 "description": "Peak-hour throughput"},
            ],
            defaults={"onVariation": 0, "offVariation": 0},
        ),
        # 4. MIGRATION - multivariate string, the one a guarded rollout runs on.
        dict(
            key="checkout-payment-provider",
            name="Checkout payment provider",
            description="MIGRATION flag. Which payment gateway the checkout "
                        "calls. This is the flag the guarded rollout runs on: "
                        "shift traffic off 'legacy' while LaunchDarkly watches "
                        "the checkout error and latency metrics.",
            kind="multivariate",
            temporary=True,
            tags=["archetype-migration", "guarded-rollout", "demo"],
            variations=[
                {"value": "legacy", "name": "Legacy gateway"},
                {"value": "gateway-v2", "name": "Gateway v2"},
                {"value": "gateway-v3", "name": "Gateway v3"},
            ],
            defaults={"onVariation": 0, "offVariation": 0},
        ),
        # 5. ENTITLEMENT - gated on a reusable segment, not an inline rule.
        dict(
            key="staff-discount-override",
            name="Staff discount override",
            description="ENTITLEMENT flag. Lets store managers apply a "
                        "discount override. Targets the 'store-managers' "
                        "segment, so the same rule can be reused by other "
                        "flags instead of being copied.",
            kind="boolean",
            temporary=False,
            tags=["archetype-entitlement", "demo"],
            variations=[
                {"value": True, "name": "Override allowed"},
                {"value": False, "name": "Override blocked"},
            ],
            defaults={"onVariation": 0, "offVariation": 1},
        ),
    ]


# --------------------------------------------------------------------------
# Metrics. A guarded rollout needs something to guard against.
# --------------------------------------------------------------------------
METRICS = [
    dict(
        key="checkout-errors",
        name="Checkout errors",
        description="Fires once per failed checkout. Lower is better - this is "
                    "the metric that should roll a bad gateway back.",
        kind="custom",
        eventKey="checkout-error",
        isNumeric=False,
        successCriteria="LowerThanBaseline",
        randomizationUnits=["user"],
        tags=["guarded-rollout", "demo"],
    ),
    dict(
        key="checkout-latency-ms",
        name="Checkout latency (ms)",
        description="Milliseconds taken by the payment gateway call. Lower is "
                    "better. Sent as the metric value on the same event.",
        kind="custom",
        eventKey="checkout-latency-ms",
        isNumeric=True,
        unit="ms",
        successCriteria="LowerThanBaseline",
        randomizationUnits=["user"],
        tags=["guarded-rollout", "demo"],
    ),
]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--project", default=PROJECT_KEY)
    args = ap.parse_args()
    proj = args.project

    if not TOKEN:
        sys.exit("LD_API_TOKEN is not set. Export a LaunchDarkly API token "
                 "(Writer is enough) and re-run.")

    print(f"\nProject '{proj}'")
    step(
        f"project {proj}", "POST", "/projects",
        dict(
            key=proj, name=PROJECT_NAME, tags=["demo", "quickstart"],
            defaultClientSideAvailability={"usingMobileKey": True,
                                           "usingEnvironmentId": True},
            environments=[
                {"key": "test", "name": "Test", "color": "F5A623"},
                {"key": "production", "name": "Production", "color": "417505"},
            ],
        ),
    )

    print("\nSegment")
    step(
        "segment store-managers (test)", "POST", f"/segments/{proj}/test",
        dict(key="store-managers", name="Store managers",
             description="Reusable audience: anyone whose role attribute is "
                         "'manager'. Referenced by staff-discount-override."),
    )
    step(
        "segment rule role=manager", "PATCH", f"/segments/{proj}/test/store-managers",
        dict(comment="Target the manager role",
             instructions=[{
                 "kind": "addRule",
                 "clauses": [{"contextKind": "user", "attribute": "role",
                              "op": "in", "values": ["manager"], "negate": False}],
             }]),
        semantic_patch=True,
    )

    print("\nFlags")
    for f in flag_defs():
        step(f"flag {f['key']}", "POST", f"/flags/{proj}", f)

    print("\nTargeting (Test environment)")
    configure_targeting(proj)

    print("\nMetrics")
    for m in METRICS:
        step(f"metric {m['key']}", "POST", f"/metrics/{proj}", m)

    print("\nDone.\n")
    print(f"  Project : {proj}")
    print("  Keys    : LaunchDarkly > Project settings > Environments > Test")
    print("            SDK key (server), mobile key / client-side ID (client)")
    print(f"  Flags   : https://app.launchdarkly.com/{proj}/test/features\n")


def configure_targeting(proj):
    """Turn targeting on and set each flag's rules. Re-running is harmless:
    duplicate rules are reported as a conflict and skipped."""
    v = flag_variation_ids(proj)
    if not v:
        print("  [skipped] could not read variation ids")
        return

    def patch(flag, label, instructions):
        step(f"{flag}: {label}", "PATCH", f"/flags/{proj}/{flag}",
             dict(comment=label, environmentKey="test", instructions=instructions),
             semantic_patch=True)

    # 1. RELEASE: north-region stores get the banner; everyone else does not.
    patch("combo-upsell-banner", "rule store_region=north -> on", [
        {"kind": "addRule", "description": "North region stores",
         "variationId": v["combo-upsell-banner"][0],
         "clauses": [{"contextKind": "user", "attribute": "store_region",
                      "op": "in", "values": ["north"], "negate": False}]},
        {"kind": "updateFallthroughVariationOrRollout",
         "variationId": v["combo-upsell-banner"][1]},
        {"kind": "turnFlagOn"},
    ])

    # 2. OPERATIONAL: on by default. The demo is turning it OFF.
    patch("loyalty-api-enabled", "default on, off = bypass", [
        {"kind": "updateFallthroughVariationOrRollout",
         "variationId": v["loyalty-api-enabled"][0]},
        {"kind": "turnFlagOn"},
    ])

    # 3. REMOTE CONFIG: conservative by default.
    patch("order-queue-tuning", "default conservative", [
        {"kind": "updateFallthroughVariationOrRollout",
         "variationId": v["order-queue-tuning"][0]},
        {"kind": "turnFlagOn"},
    ])

    # 4. MIGRATION: everyone on legacy, ready for a guarded rollout off it.
    patch("checkout-payment-provider", "100% legacy, ready to roll", [
        {"kind": "updateFallthroughVariationOrRollout",
         "variationId": v["checkout-payment-provider"][0]},
        {"kind": "turnFlagOn"},
    ])

    # 5. ENTITLEMENT: segment match -> allowed, everyone else blocked.
    patch("staff-discount-override", "segment store-managers -> allowed", [
        {"kind": "addRule", "description": "Store managers",
         "variationId": v["staff-discount-override"][0],
         "clauses": [{"contextKind": "user", "attribute": "segmentMatch",
                      "op": "segmentMatch", "values": ["store-managers"],
                      "negate": False}]},
        {"kind": "updateFallthroughVariationOrRollout",
         "variationId": v["staff-discount-override"][1]},
        {"kind": "turnFlagOn"},
    ])


def flag_variation_ids(proj):
    status, payload = call("GET", f"/flags/{proj}?summary=0")
    if status != 200:
        return None
    return {f["key"]: [var["_id"] for var in f["variations"]]
            for f in payload.get("items", [])}


if __name__ == "__main__":
    main()
