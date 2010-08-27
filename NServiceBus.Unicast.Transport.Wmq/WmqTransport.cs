using System;
using System.Collections.Generic;
using System.Threading;
using System.Transactions;
using Common.Logging;
using NServiceBus.Serialization;
using System.Xml.Serialization;
using System.IO;
using NServiceBus.Utils;
using NServiceBus.ObjectBuilder;
using NServiceBus.Utils.Wmq;
using IBM.WMQ;

namespace NServiceBus.Unicast.Transport.Wmq
{
    /// <summary>
    /// An WMQ implementation of <see cref="ITransport"/> for use with
    /// NServiceBus.
    /// </summary>
    /// <remarks>
    /// A transport is used by NServiceBus as a high level abstraction from the 
    /// underlying messaging service being used to transfer messages.
    /// </remarks>
    public class WmqTransport : WmqTransportBase, ITransport
    {
        /// <summary>
        /// Sends a message to the specified destination.
        /// </summary>
        /// <param name="m">The message to send.</param>
        /// <param name="destination">The address of the destination to send the message to.</param>
        public void Send(TransportMessage m, string destination)
        {
            System.Text.ASCIIEncoding encoding = new System.Text.ASCIIEncoding();

            MQMessage queueMessage = Convert(m);

            try
            {
                var failover = WmqTransportFailover.GetInstance();

                // Delay sending to the failover queue if it's still processing and the user has configured a wait time.
                if (failover.IsFailoverEnabled && failover.Processing && failover.FailoverWaitTime > 0)
                {
                    Thread.Sleep(failover.FailoverWaitTime);
                }

                // Attempt to send message if failover feature is not enabled 
                // else confirm there is not already a known issue sending 
                // to the primary destination queue.
                // If the primary queue is online and we are still processing it, 
                // put message on failover queue anyway to preserve order.
                if (!IsFailoverEnabled || (IsFailoverEnabled && WmqTransportFailover.IsPrimaryQueueOnline && !WmqTransportFailover.IsProcessing))
                {
                    Send(queueMessage, destination);
                }
                else // Is Failover feature is enabled and an issue has already been 
                     // identified sending to the primary destination queue 
                {
                    WmqTransportFailover.Failover(m, destination);
                }

                m.Id = encoding.GetString(queueMessage.MessageId);
            }
            catch (Exception ex)
            {
                logger.Error("Error sending message to " + destination, ex);

                if (IsFailoverEnabled)
                {
                    WmqTransportFailover.Failover(m, destination);
                }
                else
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Re-queues a message for processing at another time.
        /// </summary>
        /// <param name="m">The message to process later.</param>
        /// <remarks>
        /// This method will place the message onto the back of the queue
        /// which may break message ordering.
        /// </remarks>
        public void ReceiveMessageLater(TransportMessage m)
        {
            this.Send(m, base.Address);
        }
    }
}