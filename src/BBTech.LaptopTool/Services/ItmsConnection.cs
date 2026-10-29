using BBTech.Core.Integration;

namespace BBTech.LaptopTool.Services
{
    /// <summary>
    /// THE ONE PLACE where the live ITMS connection is plugged in.
    ///
    /// Today this returns <see cref="DisabledItmsConnector"/>: the "Send to ITMS" window works (payload
    /// preview, status), but nothing is sent and nothing is written. To go live, return a connector
    /// that implements <see cref="IItmsConnector"/> for the ITMS endpoint (address, authentication,
    /// answer format). Everything else (button, state handling, request id, retry, receipt) is ready.
    /// </summary>
    public static class ItmsConnection
    {
        public static IItmsConnector Create() => new DisabledItmsConnector();
    }
}
