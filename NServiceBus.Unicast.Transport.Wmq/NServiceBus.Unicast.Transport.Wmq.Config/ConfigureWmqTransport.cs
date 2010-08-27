using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NServiceBus.Unicast.Transport.Wmq.Config;

namespace NServiceBus
{
    /// <summary>
    /// Contains extension methods to NServiceBus.Configure.
    /// </summary>
    public static class ConfigureWmqTransport
    {
        /// <summary>
        /// Returns WmqTransport specific configuration settings.
        /// </summary>
        /// <param name="config"></param>
        /// <returns></returns>
        public static ConfigWmqTransport WmqTransport(this Configure config)
        {
            ConfigWmqTransport cfg = new ConfigWmqTransport();
            cfg.Configure(config);

            return cfg;
        }
    }
}
