using NServiceBus.Unicast.Subscriptions.Wmq.Config;

namespace NServiceBus
{
    /// <summary>
    /// Contains extension methods to NServiceBus.Configure.
    /// </summary>
    public static class ConfigureWmqSubscriptionStorage
    {
        /// <summary>
        /// Stores subscription data using WMQ.
        /// If multiple machines need to share the same list of subscribers,
        /// you should not choose this option - prefer the DbSubscriptionStorage
        /// in that case.
        /// </summary>
        /// <param name="config"></param>
        /// <returns></returns>
        public static ConfigWmqSubscriptionStorage WmqSubscriptionStorage(this Configure config)
        {
            ConfigWmqSubscriptionStorage cfg = new ConfigWmqSubscriptionStorage();
            cfg.Configure(config);

            return cfg;
        }
    }
}
