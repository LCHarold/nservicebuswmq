using System;
using System.Configuration;

namespace NServiceBus.Config
{
    /// <summary>
    /// Contains the properties representing the WmqTransport configuration section.
    /// </summary>
    public class WmqTransportConfig : ConfigurationSection
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
        /// The queue to receive messages from
        /// </summary>
        [ConfigurationProperty("InputQueue", IsRequired = true)]
        public string InputQueue
        {
            get
            {
                return this["InputQueue"] as string;
            }
            set
            {
                this["InputQueue"] = value;
            }
        }

        /// <summary>
        /// The queue to which to forward messages that could not be processed
        /// in the format "queue@machine".
        /// </summary>
        [ConfigurationProperty("ErrorQueue", IsRequired = true)]
        public string ErrorQueue
        {
            get
            {
                return this["ErrorQueue"] as string;
            }
            set
            {
                this["ErrorQueue"] = value;
            }
        }

        /// <summary>
        /// The number of worker threads that can process messages in parallel.
        /// </summary>
        [ConfigurationProperty("NumberOfWorkerThreads", IsRequired = true)]
        public int NumberOfWorkerThreads
        {
            get
            {
                return (int)this["NumberOfWorkerThreads"];
            }
            set
            {
                this["NumberOfWorkerThreads"] = value;
            }
        }

        /// <summary>
        /// The maximum number of times to retry processing a message
        /// when it fails before moving it to the error queue.
        /// </summary>
        [ConfigurationProperty("MaxRetries", IsRequired = true)]
        public int MaxRetries
        {
            get
            {
                return (int)this["MaxRetries"];
            }
            set
            {
                this["MaxRetries"] = value;
            }
        }

        /// <summary>
        /// The Microsoft Queue name to failover to if the WebSphere Queue is inaccesible
        /// </summary>
        [ConfigurationProperty("FailoverQueueName", IsRequired = false, DefaultValue="")]
        public string FailoverQueueName
        {
            get
            {
                return this["FailoverQueueName"] as string;
            }
            set
            {
                this["FailoverQueueName"] = value;
            }
        }

        /// <summary>
        /// The numberof seconds to wait before retrying to send the message to WebSphere MQ after a failure
        /// </summary>
        [ConfigurationProperty("FailoverRetryInterval", IsRequired = false, DefaultValue = "15")]
        public int FailoverRetryInterval
        {
            get
            {
                return (int)this["FailoverRetryInterval"];
            }
            set
            {
                this["FailoverRetryInterval"] = value;
            }
        }

        /// <summary>
        /// The numberof seconds to wait before sending the message to the failover queue. Useful for throttling 
        /// messages writing messages to failover queue when the primary queue is down.
        /// </summary>
        [ConfigurationProperty("FailoverWaitInterval", IsRequired = false, DefaultValue = "0")]
        public int FailoverWaitInterval
        {
            get
            {
                return (int)this["FailoverWaitInterval"];
            }
            set
            {
                this["FailoverWaitInterval"] = value;
            }
        }
    }
}