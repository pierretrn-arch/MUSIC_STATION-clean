using System.Runtime.ExceptionServices;

namespace ToneDetector.Tests;

/// <summary>
/// Runs a test body on a dedicated STA thread. WPF's FrameworkElement (and anything deriving
/// from it, like PianoRollControl) requires STA for its first-construction InputManager/
/// KeyboardNavigation singleton setup, but xUnit runs tests on MTA threads by default.
/// </summary>
internal static class StaTestRunner
{
    public static void Run(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
            ExceptionDispatchInfo.Capture(captured).Throw();
    }
}
