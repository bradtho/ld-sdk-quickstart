// LaunchDarkly SDK Quickstart - session 3, mobile side.
//
// Flutter app using the LaunchDarkly *client-side* SDK.
//
// Contrast with ../server:
//   * this app uses a MOBILE key (mob-...). It ships inside the app bundle, so
//     treat it as public: it can only read flag values for one environment.
//   * a client-side SDK does not hold your ruleset. It asks LaunchDarkly for
//     the already-evaluated values for one context, then streams updates.
//   * with AutoEnvAttributes.enabled the SDK adds its own ld_application and
//     ld_device contexts (app version, OS, locale...). The server-side SDK adds
//     nothing - that difference is worth saying out loud in the session.

import 'dart:async';

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:launchdarkly_flutter_client_sdk/launchdarkly_flutter_client_sdk.dart';

// ---------------------------------------------------------------------------
// Keys and config come from --dart-define, never from source.
//   flutter run --dart-define=LAUNCHDARKLY_MOBILE_KEY=mob-...
// const is required: fromEnvironment is resolved at compile time.
// ---------------------------------------------------------------------------
//
// Which credential you need depends on the platform you build for:
//   * web                              -> CLIENT-SIDE ID
//   * iOS, Android, macOS, Windows, Linux -> MOBILE KEY
// Both are public by design: they ship to the client and can only read one
// environment. The server SDK key is the one that must never leave the server.
const String _mobileKey = String.fromEnvironment('LAUNCHDARKLY_MOBILE_KEY');
const String _clientSideId =
    String.fromEnvironment('LAUNCHDARKLY_CLIENT_SIDE_ID');

/// The containerised demo builds for web, so it uses the client-side ID.
/// Build the same code for iOS and it picks up the mobile key instead.
const String _credential = kIsWeb ? _clientSideId : _mobileKey;
const String _credentialKind = kIsWeb ? 'client-side ID' : 'mobile key';

const String _flagKey =
    String.fromEnvironment('LD_FLAG_KEY', defaultValue: 'combo-upsell-banner');

// Fallback demo switch - see _fallbackValue below.
//   flutter run --dart-define=LD_DEMO_UNREACHABLE=true ...
const bool _demoUnreachable =
    bool.fromEnvironment('LD_DEMO_UNREACHABLE', defaultValue: false);

// ---------------------------------------------------------------------------
// THE FALLBACK VALUE.
//
// Same argument as on the server, and the same rule: pick the safe state of
// the feature. On a client it is served even more often than on a server,
// because a phone is offline far more often than a datacentre. It is served
// when:
//
//   a. start() has not finished yet - the very first frame after launch
//      (reason ERROR / CLIENT_NOT_READY);
//   b. the device has no network and there is no cached value from a previous
//      run, so the SDK never gets any values;
//   c. the flag key is wrong, or the flag was archived and deleted
//      (reason ERROR / FLAG_NOT_FOUND);
//   d. the flag is not marked available to client-side SDKs - a real and very
//      confusing one: the flag exists and works server-side, but this SDK has
//      never heard of it, so you get FLAG_NOT_FOUND;
//   e. the flag is the wrong type for the call (reason ERROR / WRONG_TYPE).
//
// Run with --dart-define=LD_DEMO_UNREACHABLE=true to watch (b) happen: the SDK
// is pointed at a dead host, start() times out, and the screen shows the
// fallback with the reason that produced it.
// ---------------------------------------------------------------------------
const bool _fallbackValue = false;

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  runApp(const ComboUpsellApp());
}

class ComboUpsellApp extends StatelessWidget {
  const ComboUpsellApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
        title: 'LaunchDarkly SDK Quickstart',
        theme: ThemeData(
          colorSchemeSeed: const Color(0xFFEE5B33),
          useMaterial3: true,
        ),
        home: const ComboUpsellScreen(),
      );
}

/// The one demo user we can switch between, to show `identify()`.
class DemoUser {
  const DemoUser(this.key, this.name, this.storeRegion);
  final String key;
  final String name;
  final String storeRegion;
}

const _users = <DemoUser>[
  // store_region "north" matches the targeting rule in the Test environment.
  DemoUser('amy', 'Amy (north store)', 'north'),
  DemoUser('ben', 'Ben (south store)', 'south'),
];

class ComboUpsellScreen extends StatefulWidget {
  const ComboUpsellScreen({super.key});

  @override
  State<ComboUpsellScreen> createState() => _ComboUpsellScreenState();
}

class _ComboUpsellScreenState extends State<ComboUpsellScreen> {
  LDClient? _client;
  StreamSubscription<FlagsChangedEvent>? _flagSubscription;

  DemoUser _user = _users.first;
  bool _showBanner = _fallbackValue; // start on the fallback, always
  LDEvaluationDetail<bool>? _detail;
  String _status = 'Starting the SDK...';
  bool _ready = false;

  @override
  void initState() {
    super.initState();
    _startLaunchDarkly();
  }

  // -------------------------------------------------------------------------
  // 1. Initialise: build a config with the mobile key, build the initial
  //    context, construct the client, then start() with a timeout.
  // -------------------------------------------------------------------------
  Future<void> _startLaunchDarkly() async {
    if (_credential.isEmpty) {
      setState(() => _status = kIsWeb
          ? 'No client-side ID. Rebuild with '
              '--dart-define=LAUNCHDARKLY_CLIENT_SIDE_ID=...'
          : 'No mobile key. Run with '
              '--dart-define=LAUNCHDARKLY_MOBILE_KEY=mob-...');
      return;
    }

    final config = LDConfig(
      _credential,
      // enabled => the SDK contributes ld_application / ld_device contexts.
      AutoEnvAttributes.enabled,
      serviceEndpoints: _demoUnreachable
          // Pointed at a dead port on purpose, for the fallback demo.
          ? ServiceEndpoints.custom(
              streaming: 'http://localhost:1',
              polling: 'http://localhost:1',
              events: 'http://localhost:1',
            )
          : null,
    );

    final client = LDClient(config, _contextFor(_user));

    // start() can take an indeterminate time - a phone in a lift, airplane
    // mode, a captive portal. Always bound it. Until it returns, every
    // evaluation gives you the fallback.
    final started = await client
        .start()
        .timeout(const Duration(seconds: 5), onTimeout: () => false);

    if (!mounted) {
      await client.close();
      return;
    }

    // -----------------------------------------------------------------------
    // 2. Listen for changes. This is what makes the screen update live when
    //    the flag is toggled in the LaunchDarkly UI - no restart, no polling.
    //    The event carries only the keys that changed; you re-evaluate to get
    //    the new value.
    // -----------------------------------------------------------------------
    _flagSubscription = client.flagChanges.listen((event) {
      if (event.keys.contains(_flagKey)) {
        _evaluate(note: 'Updated from LaunchDarkly');
      }
    });

    setState(() {
      _client = client;
      _ready = started;
      _status = started
          ? 'Connected'
          : _demoUnreachable
              ? 'Could not reach LaunchDarkly (on purpose) - serving the fallback'
              : 'start() timed out - serving the fallback';
    });

    _evaluate(note: started ? 'Evaluated at start-up' : null);
  }

  /// Context construction: one kind, a stable key, a name, and the one custom
  /// attribute the targeting rule reads.
  LDContext _contextFor(DemoUser user) {
    final builder = LDContextBuilder();
    builder
        .kind('user', user.key)
        .name(user.name)
        .setString('store_region', user.storeRegion);
    return builder.build();
  }

  // -------------------------------------------------------------------------
  // 3. Evaluate. boolVariationDetail gives the value and why - so you can tell
  //    a real "false" from a fallback "false".
  // -------------------------------------------------------------------------
  void _evaluate({String? note}) {
    final client = _client;
    if (client == null) return;

    final detail = client.boolVariationDetail(_flagKey, _fallbackValue);
    setState(() {
      _detail = detail;
      _showBanner = detail.value;
      if (note != null) _status = note;
    });
  }

  // -------------------------------------------------------------------------
  // 4. identify(): the user changed, so the context changed. The SDK fetches
  //    the values for the new context and the flag stream follows it.
  //    Call this on sign-in, sign-out, or any change to context attributes.
  // -------------------------------------------------------------------------
  Future<void> _switchUser(DemoUser user) async {
    final client = _client;
    if (client == null) return;

    setState(() {
      _user = user;
      _status = 'Identifying ${user.name}...';
    });

    await client.identify(_contextFor(user));
    if (!mounted) return;
    _evaluate(note: 'Identified ${user.name}');
  }

  // -------------------------------------------------------------------------
  // 5. Shut down: cancel the subscription and close the client.
  // -------------------------------------------------------------------------
  @override
  void dispose() {
    _flagSubscription?.cancel();
    _client?.close();
    super.dispose();
  }

  bool get _servedFallback => _detail?.variationIndex == null;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      appBar: AppBar(
        title: const Text('Counter order'),
        backgroundColor: theme.colorScheme.inversePrimary,
      ),
      body: SingleChildScrollView(
        padding: const EdgeInsets.all(20),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // ---- the flag-driven content -----------------------------------
            if (_showBanner)
              Card(
                color: theme.colorScheme.primaryContainer,
                child: const Padding(
                  padding: EdgeInsets.all(20),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text('Make it a combo?',
                          style: TextStyle(
                              fontSize: 22, fontWeight: FontWeight.bold)),
                      SizedBox(height: 8),
                      Text('Add a drink and a side for \$2.50 more.'),
                    ],
                  ),
                ),
              )
            else
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(20),
                  child: Text('No combo upsell for this order.',
                      style: theme.textTheme.bodyLarge),
                ),
              ),

            const SizedBox(height: 24),
            Text('Who is ordering', style: theme.textTheme.titleMedium),
            const SizedBox(height: 8),
            SegmentedButton<DemoUser>(
              segments: [
                for (final u in _users)
                  ButtonSegment(value: u, label: Text(u.key)),
              ],
              selected: {_user},
              onSelectionChanged: _client == null
                  ? null
                  : (s) => _switchUser(s.first),
            ),

            const SizedBox(height: 24),
            // ---- the teaching panel ----------------------------------------
            Card(
              color: theme.colorScheme.surfaceContainerHighest,
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text('What the SDK just did',
                        style: theme.textTheme.titleMedium),
                    const Divider(),
                    _row('credential', _credentialKind),
                    _row('flag', _flagKey),
                    _row('context', 'user:${_user.key}'),
                    _row('store_region', _user.storeRegion),
                    _row('value', '${_detail?.value ?? _fallbackValue}'),
                    _row('variation index', '${_detail?.variationIndex}'),
                    _row('reason', _describeReason(_detail?.reason)),
                    _row('served fallback', _servedFallback ? 'YES' : 'no'),
                    _row('fallback value', '$_fallbackValue'),
                    _row('SDK ready', _ready ? 'yes' : 'no'),
                    const Divider(),
                    Text(_status, style: theme.textTheme.bodySmall),
                  ],
                ),
              ),
            ),

            if (_servedFallback) ...[
              const SizedBox(height: 16),
              Card(
                color: theme.colorScheme.errorContainer,
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Text(
                    'This screen is showing the fallback value, not a value '
                    'from LaunchDarkly. The reason above says which of the '
                    'fallback cases you are looking at.',
                    style: TextStyle(color: theme.colorScheme.onErrorContainer),
                  ),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }

  Widget _row(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 3),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            SizedBox(
                width: 130,
                child: Text(label,
                    style: const TextStyle(fontWeight: FontWeight.w600))),
            Expanded(
                child: Text(value,
                    style: const TextStyle(fontFamily: 'monospace'))),
          ],
        ),
      );
}

/// The SDK gives you a machine-readable reason; this turns it into the one
/// line you want on screen while teaching.
String _describeReason(LDEvaluationReason? reason) {
  if (reason == null) return 'none (no evaluation yet)';
  return switch (reason.kind) {
    LDKind.off => 'OFF - targeting is off, serving the off variation',
    LDKind.fallthrough => 'FALLTHROUGH - no rule matched, default rule served',
    LDKind.targetMatch => 'TARGET_MATCH - this context is targeted individually',
    LDKind.ruleMatch => 'RULE_MATCH - matched rule ${reason.ruleIndex}',
    LDKind.prerequisiteFailed =>
      'PREREQUISITE_FAILED - ${reason.prerequisiteKey} is not satisfied',
    LDKind.error =>
      'ERROR / ${reason.errorKind?.name ?? 'unknown'} - this is the fallback',
  };
}
