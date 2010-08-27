using System.Configuration;

namespace NServiceBus.Config
{
    /// <summary>
    /// Contains the properties representing the WmqSubscriptionStorage configuration section.
    /// </summary>
    public class WmqSubscriptionStorageConfig : ConfigurationSection
    {
        /// <summary>
        /// The channel info used to connect to the MQ Queue Manager
        /// </summary>
        [ConfigurationProperty("ChannelInfo", IsRequired = true)]
        public string ChannelInfo
        {
            get
            {
                return this["ChannelInfo"] as string;
            }
            set
            {
                this["ChannelInfo"] = value;
            }
        }

        /// <summary>
        /// The WebSphere MQ Queue Manager
        /// </summary>
        [ConfigurationProperty("QueueManager", IsRequired = true)]
        public string QueueManager
        {
            get
            {
                return this["QueueManager"] as string;
            }
            set
            {
                this["QueueManager"] = value;
            }
        }

        /// <summary>
        /// The queue where subscription data will be stored.
        /// </summary>
        [ConfigurationProperty("Queue", IsRequired = true)]
        public string Queue
        {
            get
            {
                return this["Queue"] as string;
            }
            set
            {
                this["Queue"] = value;
            }
        }
    }
}
