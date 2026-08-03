namespace StudyLife.Client.Services;

/// <summary>
/// Additive handoff point for calendar files shared from outside the app (same pattern as
/// INativeAppAuth/INativePush): the native app shell receives an .ics via the operating
/// system's share/open menu, stores it here and navigates to /calendar -
/// the calendar page picks it up during initialization and feeds it into the same
/// import review flow as the file upload. In the browser (NoNativeIcsIntake) there's
/// never anything to pick up, the web flow stays exactly unchanged.
/// </summary>
public interface INativeIcsIntake
{
    /// <summary>Retrieves the pending file exactly once (further calls: null).</summary>
    (string FileName, byte[] Content)? TakePending();
}

/// <summary>Default registration in the browser client.</summary>
public sealed class NoNativeIcsIntake : INativeIcsIntake
{
    public (string FileName, byte[] Content)? TakePending() => null;
}
