#!/usr/bin/env bash
# Generate checkout traffic so a guarded rollout has something to measure.
#
# A guarded rollout needs a continuous stream of metric events across a spread
# of contexts. One person clicking around does not produce that, so this walks
# a pool of user keys through /checkout.
#
#   ./scripts/traffic.sh                 # 200 requests, 10 at a time
#   ./scripts/traffic.sh 1000 25         # more, faster
#   BASE=http://localhost:5080 ./scripts/traffic.sh
#
# Each request evaluates checkout-payment-provider and sends
# checkout-latency-ms (always) and checkout-error (on failure).
set -uo pipefail

REQUESTS="${1:-200}"
CONCURRENCY="${2:-10}"
BASE="${BASE:-http://localhost:5080}"

if ! curl -sf "$BASE/health" >/dev/null 2>&1; then
  echo "Nothing answering at $BASE - start it with 'make up' first." >&2
  exit 1
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Sending $REQUESTS requests to $BASE/checkout ($CONCURRENCY at a time)"
echo

one() {
  # A stable key per simulated user. Percentage and guarded rollouts bucket on
  # this, so reusing a pool of keys is what makes the split meaningful - random
  # keys every request would spread every user across every variation.
  local key="user-$(( RANDOM % 200 ))"
  curl -s -o "$tmp/body.$1" -w '%{http_code}' \
       "$BASE/checkout?userKey=$key" >> "$tmp/codes" 2>/dev/null
  echo >> "$tmp/codes"
  python3 - "$tmp/body.$1" >> "$tmp/rows" 2>/dev/null <<'PY'
import json, sys
try:
    d = json.load(open(sys.argv[1]))
    print(d.get("gateway", "?"), d.get("succeeded"), d.get("latencyMs", 0))
except Exception:
    pass
PY
  rm -f "$tmp/body.$1"
}

running=0
for i in $(seq 1 "$REQUESTS"); do
  one "$i" &
  running=$((running + 1))
  if [ "$running" -ge "$CONCURRENCY" ]; then wait -n 2>/dev/null || wait; running=$((running - 1)); fi
  if [ $((i % 50)) -eq 0 ]; then echo "  $i/$REQUESTS"; fi
done
wait

echo
python3 - "$tmp/rows" <<'PY'
import sys, collections
rows = []
for line in open(sys.argv[1]):
    parts = line.split()
    if len(parts) == 3:
        rows.append((parts[0], parts[1] == "True", float(parts[2])))

if not rows:
    print("No responses parsed - is the service healthy?")
    raise SystemExit(1)

by = collections.defaultdict(list)
for gw, ok, ms in rows:
    by[gw].append((ok, ms))

print(f"{len(rows)} requests\n")
print(f"{'gateway':<14}{'share':>8}{'errors':>9}{'mean ms':>10}")
print("-" * 41)
for gw in sorted(by):
    items = by[gw]
    errs = sum(1 for ok, _ in items if not ok)
    mean = sum(ms for _, ms in items) / len(items)
    print(f"{gw:<14}{len(items)/len(rows):>7.0%}{errs/len(items):>8.0%}{mean:>10.0f}")
print()
print("Metric events sent: checkout-latency-ms (every request),")
print("                    checkout-error (failures only).")
print("They appear in LaunchDarkly within a minute or so.")
PY
