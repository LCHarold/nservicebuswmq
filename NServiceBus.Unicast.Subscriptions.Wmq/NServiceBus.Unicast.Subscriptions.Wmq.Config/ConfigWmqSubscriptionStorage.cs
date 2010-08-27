using System.Configuration;
using NServiceBus.ObjectBuilder;
using NServiceBus.Config;

namespace NServiceBus.Unicast.Subscriptions.Wmq.Config
{
    /// <summary>
    /// Extends the base Configure class with WmqSubscriptionStorage specific methods.
    /// Reads administrator set values from the WmqSubscriptionStorageConfig section
    /// of the app.config.
    /// </summary>
    public class ConfigWmqSubscriptionStorage : Configure
    {

        /// <summary>
        /// Constructor needed since we have an additional constructor.
        /// </summary>
        public ConfigWmqSubscriptionStorage() : base() { }

        
        /// <summary>
        /// Wraps the given configuration object but stores the same 
        /// builder and configurer properties.
        /// </summary>
        /// <param name="config"></param>
        public void Configure(Configure config)
        {
            this.Builder = config.Builder;
            this.Configurer = config.Configurer;

            WmqSubscriptionStorageConfig cfg =
                ConfigurationManager.GetSection("WmqSubscriptionStorageConfig") as WmqSubscriptionStorageConfig;

            if (cfg == null)
                throw new ConfigurationErrorsException("Could not find configuration section for Wmq Subscription Storage.");

            WmqSubscriptionStorage storage = this.Configurer.ConfigureComponent<WmqSubscriptionStorage>(ComponentCallModelEnum.Singleton);
            storage.ChannelInfo = cfg.ChannelInfo;
            storage.QueueManagerName = cfg.QueueManager;
            storage.Queue = cfg.Queue;
        }
    }
}
