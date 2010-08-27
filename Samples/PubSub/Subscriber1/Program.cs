using System;
using Common.Logging;
using Messages;
using NServiceBus;
using NServiceBus.Unicast.Subscriptions.Wmq;
using NServiceBus.Unicast.Subscriptions.Wmq.Config;
using NServiceBus.Unicast.Transport.Wmq;
using NServiceBus.Unicast.Transport.Wmq.Config;

namespace Subscriber1
{
    class Program
    {
        static void Main()
        {
            LogManager.GetLogger("hello").Debug("Started.");

            var bus = NServiceBus.Configure.With()
                .SpringBuilder()
                .XmlSerializer()
                .WmqTransport()
                    .IsTransactional(true)
                    .PurgeOnStartup(false)
                .UnicastBus()
                    .ImpersonateSender(false)
                    .LoadMessageHandlers()
                .CreateBus()
                .Start();

            Console.WriteLine("Listening for events. To exit, press 'q' and then 'Enter'.");
            while (Console.ReadLine().ToLower() != "q")
            {
            }
        }
    }
}
