using BBTech.Core.Integration;

namespace BBTech.LaptopTool.Services
{
    /// <summary>
    /// Factory providing the active ITMS connector.
    /// Uses the effective ITMS server URL and API key from ToolSettings or environment variables.
    /// </summary>
    public static class ItmsConnection
    {
        public static IItmsConnector Create()
        {
            var store = SettingsService.Store;
            return new HttpItmsConnector(
                () => store?.EffectiveItmsUrl,
                () => store?.EffectiveItmsApiKey);
        }
    }
}
