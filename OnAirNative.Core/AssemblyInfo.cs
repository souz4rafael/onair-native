using System.Runtime.CompilerServices;

// Exposes internal (not private) test-seam members — ScriptParser/ConfigService are already
// fully public and need no exposure; UpdateService.IsNewer/NormalizeForParse and
// AiChatService.ProviderParams were widened from private to internal specifically so
// OnAirNative.Tests can unit-test this pure logic directly instead of only indirectly through a
// real network call. No other assembly should depend on this — it's a test seam, not a public
// extensibility point.
[assembly: InternalsVisibleTo("OnAirNative.Tests")]
