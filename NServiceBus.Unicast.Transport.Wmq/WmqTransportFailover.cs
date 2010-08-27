using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Messaging;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Transactions;
using System.Xml.Serialization;

using NServiceBus;
using NServiceBus.Serialization;
using NServiceBus.Unicast.Transport;

using IBM.WMQ;

namespace NServiceBus.Unicast.Transport.Wmq
{
    class WmqTransportFailover : WmqTransportBase
    {
        #region Class Variables

        private XmlSerializer _headerSerializer = new XmlSerializer(typeof(List<HeaderInfo>));

        // Using the volatile modifier ensures that one thread retrieves 
        // the most up-to-date value written by another thread.
        private volatile static WmqTransportFailover uniqueInstance;

        private static object syncRoot = new Object();
        private static object SyncRootQueue = new object();
        private static object syncRootProcessing = new Object();
        private static object syncRootPrimaryQueueOnline = new Object();

        private MessageQueue _failoverQueue;
        private bool _processing = false;
        private bool _primaryQueueOnline = true;

        #endregion

        #region Class Properties

        #region Property: FailoverQueue
        public MessageQueue FailoverQueue
        {
            get { return _failoverQueue; }
            set { _failoverQueue = value; }
        }
        #endregion

        #region Property: HeaderSerializer
        /// <summary>
        /// 
        /// </summary>
        protected XmlSerializer HeaderSerializer
        {
            get { return _headerSerializer; }
        }
        #endregion

        #region Property: LocalQueue
        public MessageQueue LocalQueue
        {
            get { return _failoverQueue; }
            set { _failoverQueue = value; }
        }
        #endregion

        #region Method: MessageQueueCount

        protected Message PeekWithoutTimeout(MessageQueue q)
        {
            using (Cursor cursor = q.CreateCursor())
            {
                return PeekWithoutTimeout(q, cursor, PeekAction.Current);
            }
        }

        protected Message PeekWithoutTimeout(MessageQueue q, Cursor cursor, PeekAction action)
        {
            Message ret = null;
            try
            {
                ret = q.Peek(new TimeSpan(1), cursor, action);
            }
            catch (MessageQueueException mqe)
            {
                if (mqe.MessageQueueErrorCode != MessageQueueErrorCode.IOTimeout)
                {
                    throw;
                }
            }
            return ret;
        }

        protected void PurgeLocalQueue()
        {
            LocalQueue.Purge();
            Processing = false;
        }

        protected int GetMessageCount(MessageQueue q)
        {
            int count = 0;
            using (Cursor cursor = q.CreateCursor())
            {
                Message m = PeekWithoutTimeout(q, cursor, PeekAction.Current);
                if (m != null)
                {
                    m.Dispose();
                    count = 1;
                    while ((m = PeekWithoutTimeout(q, cursor, PeekAction.Next)) != null)
                    {
                        count++;
                        m.Dispose();
                    }
                }
            }
            return count;
        }

        public int MessageQueueCount()
        {
            int messageCount = 0;

            try
            {
                return GetMessageCount(FailoverQueue);
            }
            catch (Exception ex)
            {
                logger.Error("Error: " + ex.Message);
            }

            return messageCount;
        }
        #endregion

        #region Property: Processing
        public bool Processing
        {
            get
            {   // todo: make sure this can NOT cause a dead-lock 
                lock (syncRootProcessing)
                {
                    return _processing;
                }
            }
            set
            {   // todo: make sure this can NOT cause a dead-lock 
                lock (syncRootProcessing)
                {
                    _processing = value;
                }
            }
        }
        #endregion

        #region Property: PrimaryQueueOnline
        public bool PrimaryQueueOnline
        {
            get
            {   // todo: make sure this can NOT cause a dead-lock 
                lock (syncRootPrimaryQueueOnline)
                {
                    return _primaryQueueOnline;
                }
            }
            set
            {   // todo: make sure this can NOT cause a dead-lock 
                lock (syncRootPrimaryQueueOnline)
                {
                    _primaryQueueOnline = value;
                }
            }
        }
        #endregion

        #region Property: WaitTime
        /// <summary>
        /// Wait time between attempts to relay message from failover queue to destination queue
        /// </summary>
        protected int WaitTime
        {
            get
            {
                return FailoverRetryInterval * 1000;
            }
        }

        /// <summary>
        /// Wait time before writing to the failover queue
        /// </summary>
        public int FailoverWaitTime
        {
            get
            {
                return FailoverWaitInterval*1000;
            }
        }
        #endregion

        #endregion

        #region Class Properties Static

        #region Property: IsProcessing
        public static bool IsProcessing
        {
            get
            {
                return GetInstance().Processing;
            }
        }
        #endregion

        #region Property: IsPrimaryQueueOnline
        public static bool IsPrimaryQueueOnline
        {
            get
            {
                return GetInstance().PrimaryQueueOnline;
            }
        }
        #endregion

        #region Property: MessagesInQueue
        public static int MessagesInQueue
        {
            get
            {
                return GetInstance().MessageQueueCount();
            }
        }
        #endregion

        #endregion

        #region Constructor: MsmqTransportFailover()
        private WmqTransportFailover()
        {
            //  WmqTransportFailover Singleton Created
        }
        #endregion

        #region Class Methods

        #region Method: ConvertToTransportMessage(message)
        /// <summary>
        /// Converts a msmq message <see cref="Message"/> into an NServiceBus TransportMessage.
        /// </summary>
        /// <param name="message">The MSMQ message to convert.</param>
        /// <returns>An NServiceBus message.</returns>
        public TransportMessage ConvertToTransportMessage(Message message)
        {
            TransportMessage result = new TransportMessage();
            result.Id = message.Id;
            result.CorrelationId = (message.CorrelationId == "00000000-0000-0000-0000-000000000000\\0" ? null : message.CorrelationId);
            result.Recoverable = message.Recoverable;
            result.TimeToBeReceived = message.TimeToBeReceived;

            result.ReturnAddress = GetIndependentAddressForQueue(message.ResponseQueue);

            FillIdForCorrelationAndWindowsIdentity(result, message);

            if (result.IdForCorrelation == null || result.IdForCorrelation == string.Empty)
                result.IdForCorrelation = result.Id;

            if (message.Extension != null)
                if (message.Extension.Length > 0)
                {
                    MemoryStream stream = new MemoryStream(message.Extension);
                    object o = HeaderSerializer.Deserialize(stream);
                    result.Headers = o as List<HeaderInfo>;
                }

            return result;
        }
        #endregion

        #region Method: Extract(message)
        /// <summary>
        /// Extracts the messages from an MSMQ <see cref="Message"/>.
        /// </summary>
        /// <param name="message">The MSMQ message to extract from.</param>
        /// <returns>An array of handleable messages.</returns>
        private IMessage[] Extract(Message message)
        {
            return MessageSerializer.Deserialize(message.BodyStream);
        }
        #endregion

        #region Method: FillIdForCorrelationAndWindowsIdentity(transportMessage, message)
        protected static void FillIdForCorrelationAndWindowsIdentity(TransportMessage result, Message m)
        {
            if (m.Label == null)
                return;

            if (m.Label.Contains(IDFORCORRELATION))
            {
                int idStartIndex = m.Label.IndexOf(string.Format("<{0}>", IDFORCORRELATION)) + IDFORCORRELATION.Length + 2;
                int idCount = m.Label.IndexOf(string.Format("</{0}>", IDFORCORRELATION)) - idStartIndex;

                result.IdForCorrelation = m.Label.Substring(idStartIndex, idCount);
            }

            if (m.Label.Contains(WINDOWSIDENTITYNAME))
            {
                int winStartIndex = m.Label.IndexOf(string.Format("<{0}>", WINDOWSIDENTITYNAME)) + WINDOWSIDENTITYNAME.Length + 2;
                int winCount = m.Label.IndexOf(string.Format("</{0}>", WINDOWSIDENTITYNAME)) - winStartIndex;

                result.WindowsIdentityName = m.Label.Substring(winStartIndex, winCount);
            }
        }
        #endregion

        #region Method: FillLabel(messageToSend, transportMessage)
        protected static void FillLabel(Message messageToSend, TransportMessage transportMessage)
        {
            messageToSend.Label = string.Format("<{0}>{2}</{0}><{1}>{3}</{1}>", IDFORCORRELATION, WINDOWSIDENTITYNAME, transportMessage.IdForCorrelation, transportMessage.WindowsIdentityName);
        }
        #endregion

        #region Method: GetIndependentAddressForQueue(MessageQueue messageQueue)
        /// <summary>
        /// Gets an independent address for the queue in the form:
        /// queue@machine.
        /// </summary>
        /// <param name="messageQueue"></param>
        /// <returns></returns>
        public static string GetIndependentAddressForQueue(MessageQueue messageQueue)
        {
            if (messageQueue == null)
                return null;

            string[] arr = messageQueue.FormatName.Split('\\');
            string queueName = arr[arr.Length - 1];

            int directPrefixIndex = arr[0].IndexOf(DIRECTPREFIX);
            if (directPrefixIndex >= 0)
            {
                return queueName + '@' + arr[0].Substring(directPrefixIndex + DIRECTPREFIX.Length);
            }

            try
            {
                // the pessimistic approach failed, try the optimistic approach
                arr = messageQueue.QueueName.Split('\\');
                queueName = arr[arr.Length - 1];
                return queueName + '@' + messageQueue.MachineName;
            }
            catch
            {
                throw new Exception(string.Concat("MessageQueueException: '",
                DIRECTPREFIX, "' is missing. ",
                "FormatName='", messageQueue.FormatName, "'"));
            }


        }
#endregion

        #region Method: GetTransactionTypeForSend()
        /// <summary>
        /// Gets the transaction type to use when sending a message.
        /// </summary>
        /// <returns>The transaction type to use.</returns>
        protected MessageQueueTransactionType GetTransactionTypeForSend()
        {
            if (this.IsTransactional)
            {
                if (Transaction.Current != null)
                    return MessageQueueTransactionType.Automatic;
                else
                    return MessageQueueTransactionType.Single;
            }
            else
                return MessageQueueTransactionType.Single;
        }
        #endregion

        #region Method: LocalInitialize(isTransactional, messageSerializer)
        public void LocalInitialize( bool isTransactional, IMessageSerializer messageSerializer, String channelInfo
                                   , string failoverQueueName, int failoverRetryInterval, int failoverWaitInterval)
        {
            base.IsTransactional = isTransactional;
            base.MessageSerializer = messageSerializer;
            ChannelInfo = channelInfo;

            FailoverQueueName = failoverQueueName;
            FailoverRetryInterval = failoverRetryInterval;
            FailoverWaitInterval = failoverWaitInterval;

            _failoverQueue = new MessageQueue(FailoverQueueName);

            MessagePropertyFilter messagePropertyFilter = new MessagePropertyFilter();
            messagePropertyFilter.SetAll();

            _failoverQueue.MessageReadPropertyFilter = messagePropertyFilter;

        }
        #endregion

        #region Method: ProcessFailoverMessages()
        /// <summary>
        /// Private method that can only be called from inside class - specifically by the method: StartProcessing
        /// </summary>
        private void ProcessFailoverMessages()
        {
            //  ProcessFailoverMessages Started
            int[] waitTimeProgession = new int[] {1, 1, 2, 3, 5, 8, 13, 21, 34, 55, 89, 144, 233, 377};

            int c = 1;

            try
            {
                Message failoverMessage = null;
                while ((failoverMessage = PeekWithoutTimeout(LocalQueue)) != null)
                {
                    //  Messages in Queue: " + messageQueueCount

                    Console.WriteLine("Attempt " + c + " to send message");
                    logger.Info("Attempt " + c + " to send message");

                    try
                    {
                        if (RelayFailoverMessage())
                        {
                            Console.WriteLine("Successfully relayed failover message to its destination.");
                            logger.Info("Successfully relayed failover message to its destination.");
                            PrimaryQueueOnline = true;
                        }
                        else
                        {
                            PrimaryQueueOnline = false;
                            Console.WriteLine("Unable to relay failover message to its destination.");
                            int timeToWait = WaitTime * waitTimeProgession[c%waitTimeProgession.Length];
                            logger.Info("Unable to relay failover message to its destination, waiting " + new TimeSpan(0, 0, 0, 0, timeToWait).TotalSeconds + " seconds to try again.");
                            Thread.Sleep(timeToWait);
                            c++;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error("Error: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error("Error processing failover messages", ex);
            }

            //  Finished ProcessFailoverMessages
            logger.Info("Finished reading messages in queue: ");
            Processing = false;
        }
        #endregion

        #region Method: HandleFailover()
        /// <summary>
        /// Push/Add object(message) to queue
        /// </summary>
        /// <param name="m"></param>
        /// <param name="destination"></param>
        public void HandleFailover(TransportMessage m, string destination)
        {
            PrimaryQueueOnline = false;

            // send message to failover Queue
            SendToFailoverQueue(m, destination);

            ProcessFailover();
        }
        #endregion

        /// <summary>
        /// Process messages from the failover queue
        /// </summary>
        public void ProcessFailover()
        {
            StartProcessing();
        }

        /// <summary>
        /// Purges the failover queue.
        /// </summary>
        public void PurgeFailover()
        {
            if ((!_processing) && (MessageQueueCount() > 0))
            {
                lock (syncRootProcessing)
                {
                    if (!_processing && MessageQueueCount() > 0) // double check
                    {
                        _processing = true;

                        Thread failoverQueueProcessor = new Thread(new ThreadStart(PurgeLocalQueue));

                        failoverQueueProcessor.Start();
                    }
                }
            }
        }

        #region Method: RelayFailoverMessage()
        protected bool RelayFailoverMessage()
        {
            using (MessageQueueTransaction mqt = new MessageQueueTransaction())
            {
                try
                {
                    mqt.Begin();

                    Message failoverMessage = LocalQueue.Receive(mqt);

                    TransportMessage transportMessage = ConvertToTransportMessage(failoverMessage);

                    if (this.SkipDeserialization)
                        transportMessage.BodyStream = failoverMessage.BodyStream;
                    else
                    {
                        try
                        {
                            transportMessage.Body = Extract(failoverMessage);
                        }
                        catch (Exception e)
                        {
                            logger.Error("Could not extract message data.", e);

                            base.MoveToErrorQueue(Convert(transportMessage));

                            // commit receive transaction from failoverQueue, ChannelInfo, QueueManager)
                            mqt.Commit();

                            return true; // deserialization failed - no reason to try again, so don't throw
                        }
                    }

                    // lookup destination queue name
                    string destination = "";
                    if (transportMessage.Headers != null)
                    {
                        foreach (HeaderInfo headerInfo in transportMessage.Headers)
                        {
                            if (headerInfo.Key.Equals("destination"))
                            {
                                destination = headerInfo.Value;
                                transportMessage.Headers.Remove(headerInfo);
                                break;
                            }
                        }
                    }

                    // make sure a destination queue name was found
                    if (destination.Equals(""))
                    {
                        // consider writing this message to the error queue 
                        // then commit transactional receive
                        // since application can not determine where to send it
                        throw new Exception("Destination Not found");
                    }


                    MQMessage mqMessage = base.Convert(transportMessage);

                    base.Send(mqMessage, destination);

                    // commit receive transaction from failoverQueue, ChannelInfo, QueueManager)
                    mqt.Commit();

                    return true;
                }
                catch (Exception ex)
                {
                    logger.Error("Exception in WmqTranportFailover.RelayFailoverMessage: " + ex.Message);

                    // roll back transaction
                    mqt.Abort();
                    return false;
                }
                finally
                {
                    mqt.Dispose();
                }
            }
        }
        #endregion

        #region Method: Send(message, destination)
        public void Send(Message message, string destination)
        {
            destination = ".\\Private$\\" + destination;

            // create connection to destination queue
            MessageQueue destinationQueue = new MessageQueue(destination);

            // Set properties filter
            MessagePropertyFilter messagePropertyFilter = new MessagePropertyFilter();
            messagePropertyFilter.SetAll();
            destinationQueue.MessageReadPropertyFilter = messagePropertyFilter;

            // send message to destination
            destinationQueue.Send(message, GetTransactionTypeForSend());
        }
        #endregion

        #region Method: SendToFailoverQueue(transportMessage, destination)
        private void SendToFailoverQueue( TransportMessage transportMessage, string destination)
        {
            Message toSend = new Message();

            if (transportMessage.Body == null && transportMessage.BodyStream != null)
                toSend.BodyStream = transportMessage.BodyStream;
            else
                MessageSerializer.Serialize(transportMessage.Body, toSend.BodyStream);

            if (transportMessage.CorrelationId != null)
                toSend.CorrelationId = transportMessage.CorrelationId;
            else
            {
                toSend.CorrelationId = toSend.Id;
                transportMessage.CorrelationId = toSend.Id;
                transportMessage.IdForCorrelation = toSend.Id;
            }

            toSend.Recoverable = transportMessage.Recoverable;
            toSend.ResponseQueue = new MessageQueue(GetFullPath(transportMessage.ReturnAddress));
            FillLabel(toSend, transportMessage);

            if (transportMessage.TimeToBeReceived < MessageQueue.InfiniteTimeout)
                toSend.TimeToBeReceived = transportMessage.TimeToBeReceived;

            if(transportMessage.Headers == null)
               transportMessage.Headers = new List<HeaderInfo>();

            transportMessage.Headers.Add(new HeaderInfo { Key = "destination", Value = destination });

            if (transportMessage.Headers != null && transportMessage.Headers.Count > 0)
            {
                MemoryStream stream = new MemoryStream();
                HeaderSerializer.Serialize(stream, transportMessage.Headers);
                toSend.Extension = stream.GetBuffer();
            }

            FailoverQueue.Send(toSend, GetTransactionTypeForSend());

        }
        #endregion

        #region Method: SetLocalQueue(q)
        /// <summary>
        /// Sets the queue on the transport to the specified MSMQ queue.
        /// </summary>
        /// <param name="q">The MSMQ queue to set.</param>
        protected void SetLocalQueue(MessageQueue q)
        {
            //q.MachineName = Environment.MachineName; // just in case we were given "localhost"
            if (!q.Transactional)
                throw new ArgumentException("Queue must be transactional (" + q.Path + ").");
            else
                LocalQueue = q;

            MessagePropertyFilter mpf = new MessagePropertyFilter();
            mpf.SetAll();

            LocalQueue.MessageReadPropertyFilter = mpf;
        }
        #endregion

        #region Method: StartProcessing()
        private void StartProcessing()
        {
            // check that the service is not already processing messages 
            // and that there are messages to process
            if ((!_processing) && (MessageQueueCount() > 0))
            {
                lock (syncRootProcessing)
                {
                    if (!_processing && MessageQueueCount() > 0) // double check
                    {
                        _processing = true;

                        Thread failoverQueueProcessor = new Thread(new ThreadStart(ProcessFailoverMessages));

                        failoverQueueProcessor.Start();

                    }
                }
            }
            else
            {
                //  Processing already started...
            }

        }
        #endregion

        #endregion

        #region Class Methods Static

        #region Method: GetInstance()
        public static WmqTransportFailover GetInstance()
        {
            //The approach below ensures that only one instance is created and only 
            //when the instance is needed. Also, the uniqueInstance variable is 
            //declared to be volatile to ensure that assignment to the instance variable 
            //completes before the instance variable can be accessed. Lastly, 
            //this approach uses a syncRoot instance to lock on, rather than 
            //locking on the type itself, to avoid deadlocks.

            if (uniqueInstance == null)
            {
                lock (syncRoot)
                {
                    if (uniqueInstance == null)
                    {
                        uniqueInstance = new WmqTransportFailover();
                    }
                }
            }
            return uniqueInstance;
        }
        #endregion

        #region Method: Inititialize(isTransactional, messageSerializer)
        public static void Inititialize( bool isTransactional, IMessageSerializer messageSerializer, String channelInfo
                                       , string failoverQueueName, int failoverRetryInterval, int failoverWaitInterval)
        {
            GetInstance().LocalInitialize(isTransactional, messageSerializer, channelInfo, failoverQueueName, failoverRetryInterval, failoverWaitInterval);
        }
        #endregion

        #region Method: Failover(TransportMessage, destination)
        public static void Failover(TransportMessage m, string destination)
        {
            GetInstance().HandleFailover(m, destination);
        }
        #endregion

        public static void Process()
        {
            GetInstance().ProcessFailover();
        }

        public static void Purge()
        {
            GetInstance().PurgeFailover();
        }

        #endregion
    }
}
