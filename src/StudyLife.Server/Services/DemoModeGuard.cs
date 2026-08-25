namespace StudyLife.Server.Services;

/// <summary>
/// Single source of truth for "is demo mode actually enabled", shared by Program.cs (reseed +
/// write-block middleware), AuthController (demo discovery/auto-login) and SystemController
/// (backup-capability reporting) - so all four consumers can never disagree about whether this
/// is a demo instance.
///
/// DEMO_MODE=true alone is NOT enough: DemoSeeder.ReseedAsync wipes every table via
/// IgnoreQueryFilters().ExecuteDeleteAsync() on every startup (see O1 audit finding), so a
/// copy-pasted compose file or a stray env var on a production deployment would otherwise
/// destroy all user data on the next restart. A second, explicit, hard-to-typo-into-existence
/// value is required to actually arm it. Half-enabling only one side (e.g. wipe but no
/// write-block, or vice versa) would be worse than either extreme, so a missing/wrong
/// confirmation disables demo mode ENTIRELY - the instance just runs normally.
/// </summary>
public static class DemoModeGuard
{
    private const string ConfirmValue = "yes-delete-all-data";

    private static bool _warned;

    public static bool IsEnabled(IConfiguration config)
    {
        if (!string.Equals(config["DEMO_MODE"], "true", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(config["DEMO_MODE_CONFIRM_DATA_LOSS"], ConfirmValue, StringComparison.Ordinal))
            return true;

        // Logged once per process (not per request/check) to avoid spamming a misconfigured
        // instance's logs while still making the problem impossible to miss on startup.
        if (!_warned)
        {
            _warned = true;
            Console.WriteLine("[demo] DEMO_MODE=true was requested but DEMO_MODE_CONFIRM_DATA_LOSS is missing or " +
                $"incorrect (must be exactly \"{ConfirmValue}\") - refusing to enable demo mode. " +
                "This instance runs as a NORMAL instance (no reseed, no read-only write-block). " +
                $"Set DEMO_MODE_CONFIRM_DATA_LOSS={ConfirmValue} to actually run this as a demo instance.");
        }
        return false;
    }
}
