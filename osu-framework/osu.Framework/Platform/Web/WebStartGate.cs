// xc-osu! web port (xc softworks): "click anywhere to start".
// The game loads everything behind the launch screen, then waits here before its intro so that the click which opens the
// gate can also enter fullscreen and unlock audio (browsers only allow both from a user gesture).

using System;
using osu.Framework.Logging;

namespace osu.Framework.Platform.Web
{
    public static class WebStartGate
    {
        private static readonly bool enabled = Environment.GetEnvironmentVariable("XC_START_GATE") == "1";
        private static bool requested, opened;

        /// <summary>
        /// Whether the game may proceed. The first closed check asks the page to show its prompt; the page opens the gate
        /// by writing the START_GATE slot of the shared block on the user's click.
        /// </summary>
        public static bool IsOpen
        {
            get
            {
                if (!enabled || opened) return true;

                if (WebShared.Instance.Read(WebShared.START_GATE) != 0)
                {
                    opened = true;
                    Logger.Log("start gate opened by the player");
                    return true;
                }

                if (!requested)
                {
                    requested = true;
                    Logger.Log("everything loaded, waiting for the player to click (start gate)");
                    WebNative.UiCall(UiOp.StartGateReady);
                }

                return false;
            }
        }
    }
}
