using System.Runtime.CompilerServices;

namespace Encore.Tests;

internal static class TestEnvironment
{
    // Program validates the environment at start, so tests that boot the host need valid values.
    // A test that changes these (or any ENCORE_* variable) must join ProcessEnvironmentCollection,
    // or a host booting in parallel can read a half-written environment and exit.
    [ModuleInitializer]
    internal static void Init()
    {
        Environment.SetEnvironmentVariable("JELLYFIN_URL", "http://jellyfin.test:8096");
        Environment.SetEnvironmentVariable("JELLYFIN_API_KEY", "test-key-not-real");
        Environment.SetEnvironmentVariable("ENCORE_ALLOWED_USERS", "tester");
        Environment.SetEnvironmentVariable("ENCORE_DATA_DIR", Path.Combine(Path.GetTempPath(), "encore-tests-host"));
    }
}
