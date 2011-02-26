using System;
using NServiceBus.ObjectBuilder;
using System.Configuration;
using System.Transactions;
using NServiceBus.Config;

namespace NServiceBus.Unicast.Transport.Wmq.Config
{
    /// <summary>
    /// Extends the base Configure class with WmqTransport specific methods.
    /// Reads administrator set values from the WmqTransportConfig section
    /// of the app.config.
    /// </summary>
    public class ConfigWmqTransport : Configure
    {
        /// <summary>
        /// Wraps the given configuration object but stores the same 
        /// builder and configurer properties.
        /// </summary>
        /// <param name="config"></param>
        public void Configure(Configure config)
        {
            this.Builder = config.Builder;
            this.Configurer = config.Configurer;

            transport = this.Configurer.ConfigureComponent<WmqTransport>(ComponentCallModelEnum.Singleton);

            WmqTransportConfig cfg =
                ConfigurationManager.GetSection("WmqTransportConfig") as WmqTransportConfig;

            if (cfg == null)
                throw new ConfigurationErrorsException("Could not find configuration section for Wmq Transport.");

            transport.ChannelInfo = cfg.ChannelInfo;
            transport.QueueManager = cfg.QueueManager;
            transport.InputQueue = cfg.InputQueue;
            transport.NumberOfWorkerThreads = cfg.NumberOfWorkerThreads;
            transport.ErrorQueue = cfg.ErrorQueue;
            transport.MaxRetries = cfg.MaxRetries;
            transport.FailoverQueueName = cfg.FailoverQueueName;
            transport.FailoverRetryInterval = cfg.FailoverRetryInterval;
            transport.FailoverWaitInterval = cfg.FailoverWaitInterval;
        }

        private WmqTransport transport;

        /// <summary>
        /// Sets the transactionality of the endpoint.
        /// If true, the endpoint will not lose messages when exceptions occur.
        /// If false, the endpoint may lose messages when exceptions occur.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public ConfigWmqTransport IsTransactional(bool value)
        {
            transport.IsTransactional = value;
            return this;
        }

        /// <summary>
        /// Requests that the incoming queue be purged of all messages when the bus is started.
        /// All messages in this queue will be deleted if this is true.
        /// Setting this to true may make sense for certain smart-client applications, 
        /// but rarely for server applications.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public ConfigWmqTransport PurgeOnStartup(bool value)
        {
            transport.PurgeOnStartup = value;
            return this;
        }

        /// <summary>
        /// Sets the processing to be done on startup with the failover queue
        /// </summary>
        /// <param name="value">Failover processing value.</param>
        /// <returns></returns>
        public ConfigWmqTransport FailoverProcessingOnStartup(FailoverProcessing value)
        {
            transport.FailoverProcessingOnStartup = value;
            return this;
        }

        /// <summary>
        /// Sets the isolation level that database transactions on this endpoint will run at.
        /// This value is only relevant when IsTransactional has been set to true.
        /// 
        /// Higher levels like RepeatableRead and Serializable promise a higher level
        /// of consistency, but at the cost of lower parallelism and throughput.
        /// 
        /// If you wish to run sagas on this endpoint, RepeatableRead is the suggested value
        /// and is the default value.
        /// </summary>
        /// <param name="isolationLevel"></param>
        /// <returns></returns>
        public ConfigWmqTransport IsolationLevel(IsolationLevel isolationLevel)
        {
            transport.IsolationLevel = isolationLevel;
            return this;
        }

        /// <summary>
        /// If queues configured do not exist, will not cause them
        /// to be created on startup.
        /// </summary>
        /// <returns></returns>
        public ConfigWmqTransport DoNotCreateQueues()
        {
            transport.DoNotCreateQueues = true;
            return this;
        }

        /// <summary>
        /// Sets the time span where a transaction will timeout.
        /// 
        /// Most endpoints should leave it at the default.
        /// </summary>
        /// <param name="transactionTimeout"></param>
        /// <returns></returns>
        public ConfigWmqTransport TransactionTimeout(TimeSpan transactionTimeout)
        {
            transport.TransactionTimeout = transactionTimeout;
            return this;
        }
    }
}