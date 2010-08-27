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
using ConfigurationException=System.Configuration.ConfigurationException;

namespace NServiceBus.Unicast.Transport.Wmq
{
    public enum FailoverProcessing
    {
        Nothing,
        Purge,
        Process,
    }

    /// <summary>
    /// An WMQ implementation of <see cref="ITransport"/> for use with
    /// NServiceBus.
    /// </summary>
    /// <remarks>
    /// A transport is used by NServiceBus as a high level abstraction from the 
    /// underlying messaging service being used to transfer messages.
    /// </remarks>
    public abstract class WmqTransportBase
    {
        #region config info

        /// <summary>
        /// The channel info used to connect to the MQ Queue Manager
        /// </summary>
        public virtual string ChannelInfo { get; set; }

        /// <summary>
        /// The WebSphere MQ Queue Manager
        /// </summary>
        public virtual string QueueManager { get; set; }

        /// <summary>
        /// The path to the queue the transport will read from.
        /// </summary>
        public virtual string InputQueue { get; set; }

        /// <summary>
        /// Sets the path to the queue the transport will transfer
        /// errors to.
        /// </summary>
        public virtual string ErrorQueue { get; set; }

        /// <summary>
        /// Sets the path to the queue the transport will failover to
        /// errors to.
        /// </summary>
        public virtual string FailoverQueueName { get; set; }

        /// <summary>
        /// Sets whether or not the transport is transactional.
        /// </summary>
        public virtual bool IsTransactional { get; set; }

        /// <summary>
        /// Sets whether or not the transport should failover to a msmq.
        /// </summary>
        public virtual bool IsFailoverEnabled
        {
            get { return !string.IsNullOrEmpty(FailoverQueueName); }
        }

        /// <summary>
        /// Overrides the default wait time between attempts to relay messages that have failed
        /// </summary>
        public virtual int FailoverRetryInterval { get; set; }

        /// <summary>
        /// Overrides the default wait time between attempts to relay messages that have failed
        /// </summary>
        public virtual int FailoverWaitInterval { get; set; }

        /// <summary>
        /// Sets whether or not the transport should deserialize
        /// the body of the message placed on the queue.
        /// </summary>
        public virtual bool SkipDeserialization { get; set; }

        protected bool purgeOnStartup = false;

        /// <summary>
        /// Sets whether or not the transport should purge the input
        /// queue when it is started.
        /// </summary>
        public virtual bool PurgeOnStartup
        {
            set
            {
                purgeOnStartup = value;
            }
        }

        protected FailoverProcessing _failoverProcessingOnStartup;

        public virtual FailoverProcessing FailoverProcessingOnStartup
        {
            set
            {
                _failoverProcessingOnStartup = value;
            }
        }

        protected int maxRetries = 5;

        /// <summary>
        /// Sets the maximum number of times a message will be retried
        /// when an exception is thrown as a result of handling the message.
        /// This value is only relevant when <see cref="IsTransactional"/> is true.
        /// </summary>
        /// <remarks>
        /// Default value is 5.
        /// </remarks>
        public virtual int MaxRetries
        {
            get { return maxRetries; }
            set { maxRetries = value; }
        }

        protected int secondsToWaitForMessage = 10;

        /// <summary>
        /// Sets the maximum interval of time for when a thread thinks there is a message in the queue
        /// that it tries to receive, until it gives up.
        /// 
        /// Default value is 10.
        /// </summary>
        public virtual int SecondsToWaitForMessage
        {
            get { return secondsToWaitForMessage; }
            set { secondsToWaitForMessage = value; }
        }

        /// <summary>
        /// Property for getting/setting the period of time when the transaction times out.
        /// Only relevant when <see cref="IsTransactional"/> is set to true.
        /// </summary>
        public virtual TimeSpan TransactionTimeout { get; set; }

        /// <summary>
        /// Property for getting/setting the isolation level of the transaction scope.
        /// Only relevant when <see cref="IsTransactional"/> is set to true.
        /// </summary>
        public virtual IsolationLevel IsolationLevel { get; set; }

        /// <summary>
        /// Property indicating that queues will not be created on startup
        /// if they do not already exist.
        /// </summary>
        public virtual bool DoNotCreateQueues { get; set; }

        protected IMessageSerializer messageSerializer;

        /// <summary>
        /// Sets the object which will be used to serialize and deserialize messages.
        /// </summary>
        public virtual IMessageSerializer MessageSerializer
        {
            get { return this.messageSerializer; }
            set { this.messageSerializer = value; }
        }

        /// <summary>
        /// Gets/sets the builder that will be used to create message modules.
        /// </summary>
        public virtual IBuilder Builder { get; set; }

        #endregion

        #region ITransport Members

        /// <summary>
        /// Event which indicates that message processing has started.
        /// </summary>
        public event EventHandler StartedMessageProcessing;

        /// <summary>
        /// Event which indicates that message processing has completed.
        /// </summary>
        public event EventHandler FinishedMessageProcessing;

        /// <summary>
        /// Gets/sets the number of concurrent threads that should be
        /// created for processing the queue.
        /// 
        /// Get returns the actual number of running worker threads, which may
        /// be different than the originally configured value.
        /// 
        /// When used as a setter, this value will be used by the <see cref="Start"/>
        /// method only and will have no effect if called afterwards.
        /// 
        /// To change the number of worker threads at runtime, call <see cref="ChangeNumberOfWorkerThreads"/>.
        /// </summary>
        public virtual int NumberOfWorkerThreads
        {
            get
            {
                lock (this.workerThreads)
                    return this.workerThreads.Count;
            }
            set
            {
                _numberOfWorkerThreads = value;
            }
        }
        protected int _numberOfWorkerThreads;


        /// <summary>
        /// Event raised when a message has been received in the input queue.
        /// </summary>
        public event EventHandler<TransportMessageReceivedEventArgs> TransportMessageReceived;

        /// <summary>
        /// Gets the address of the input queue.
        /// </summary>
        public string Address
        {
            get
            {
                return InputQueue;
            }
        }

        /// <summary>
        /// Sets a list of the message types the transport will receive.
        /// </summary>
        public virtual IList<Type> MessageTypesToBeReceived
        {
            set { this.messageSerializer.Initialize(GetExtraTypes(value)); }
        }

        /// <summary>
        /// Changes the number of worker threads to the given target,
        /// stopping or starting worker threads as needed.
        /// </summary>
        /// <param name="targetNumberOfWorkerThreads"></param>
        public void ChangeNumberOfWorkerThreads(int targetNumberOfWorkerThreads)
        {
            lock (this.workerThreads)
            {
                int current = this.workerThreads.Count;

                if (targetNumberOfWorkerThreads == current)
                    return;

                if (targetNumberOfWorkerThreads < current)
                {
                    for (int i = targetNumberOfWorkerThreads; i < current; i++)
                        this.workerThreads[i].Stop();

                    return;
                }

                if (targetNumberOfWorkerThreads > current)
                {
                    for (int i = current; i < targetNumberOfWorkerThreads; i++)
                        this.AddWorkerThread().Start();

                    return;
                }
            }
        }

        /// <summary>
        /// Starts the transport.
        /// </summary>
        public void Start()
        {
            InitializeQueueManager(receiveResourceManager);
            InitializeInputQueue(receiveResourceManager);

            InitializeQueueManager(sendResourceManager);
            InitializeInputQueue(sendResourceManager);

            //CheckConfiguration();
            //CreateQueuesIfNecessary();

            if (this.purgeOnStartup)
                PurgeInputQueue();

            IEnumerable<IMessageModule> mods = Builder.BuildAll<IMessageModule>();
            if (mods != null)
                this.modules.AddRange(mods);

            for (int i = 0; i < this._numberOfWorkerThreads; i++)
                this.AddWorkerThread().Start();

            if (IsFailoverEnabled)
            {
                WmqTransportFailover.Inititialize( IsTransactional, messageSerializer, ChannelInfo
                                                 , FailoverQueueName, FailoverRetryInterval, FailoverWaitInterval);
                
                if (_failoverProcessingOnStartup == FailoverProcessing.Process)
                {
                    WmqTransportFailover.Process();
                }
                else if (_failoverProcessingOnStartup == FailoverProcessing.Purge)
                {
                    WmqTransportFailover.Purge();
                }
            }
        }

        protected void InitializeQueueManager(WmqResourceManager resourceManager)
        {
            char[] separator = { '/' };
            string[] channelParams = ChannelInfo.Split(separator);
            if (channelParams.Length < 3)
            {
                throw new ApplicationException("ChannelInfo config parameter is not in the proper format.  It must include two /'s and must be in this format: channel/transport type/connection.  Example: CHANNEL1/TCP/mqwind.ttx.com(1444)" );
            }
            string channelName = channelParams[0];
            string transportType = channelParams[1];
            string connectionName = channelParams[2];

            try
            {
                resourceManager.QueueManager = new MQQueueManager(QueueManager, channelName, connectionName);
            }
            catch (Exception ex)
            {
                logger.Error("Exception in WmqTransportBase.InitializeQueueManager trying to connect to: " +
                    QueueManager + " using channel " + channelName + " and connection " + connectionName +
                    Environment.NewLine + ex.Message);
                throw;
            }
            return;
            
        }

        protected void InitializeInputQueue(WmqResourceManager resourceManager)
        {
            MQQueue queue = resourceManager.QueueManager.AccessQueue(InputQueue, MQC.MQOO_INPUT_AS_Q_DEF +
                                                                                 MQC.MQOO_FAIL_IF_QUIESCING +
                                                                                 MQC.MQOO_BROWSE);
            resourceManager.Queue = queue;
            return;
        }

        void PurgeInputQueue()
        {
            MQGetMessageOptions getMessageOptions = new MQGetMessageOptions();
            getMessageOptions.Options |= MQC.MQGMO_NO_WAIT;

            bool isContinue = true;
            while (isContinue)
            {
                MQMessage message = new MQMessage();
                try
                {
                    receiveResourceManager.GetMessage(message, getMessageOptions);
                }
                catch (MQException mqe)
                {
                    // report reason, if any
                    if (mqe.Reason == MQC.MQRC_NO_MSG_AVAILABLE)
                    {
                        isContinue = false;
                    }
                    else
                    {
                        // general report for other reasons
                        throw new ApplicationException("MQQueue::Get ended with " + mqe.Message);
                    }
                }
            }
        }

        //private void CheckConfiguration()
        //{
        //    string machine = GetMachineNameFromLogicalName(InputQueue);

        //    if (machine.ToLower() != Environment.MachineName.ToLower())
        //        throw new InvalidOperationException("Input queue must be on the same machine as this process.");
        //}

        //private void CreateQueuesIfNecessary()
        //{
        //    if (!DoNotCreateQueues)
        //    {
        //        string iq = GetFullPathWithoutPrefix(InputQueue);
        //        logger.Debug("Checking if input queue exists.");
        //        if (!MessageQueue.Exists(iq))
        //        {
        //            logger.Warn("Input queue " + InputQueue + " does not exist.");
        //            logger.Debug("Going to create input queue: " + InputQueue);

        //            MessageQueue.Create(iq, true);

        //            logger.Debug("Input queue created.");
        //        }

        //        if (!string.IsNullOrEmpty(ErrorQueue))
        //        {
        //            logger.Debug("Checking if error queue exists.");
        //            string errorMachine = GetMachineNameFromLogicalName(ErrorQueue);

        //            if (errorMachine != Environment.MachineName)
        //            {
        //                logger.Debug("Error queue is on remote machine.");
        //                logger.Debug("If this does not succeed (if the remote machine is disconnected), processing will continue.");
        //            }

        //            try
        //            {
        //                string eq = GetFullPathWithoutPrefix(ErrorQueue);
        //                if (!MessageQueue.Exists(eq))
        //                {
        //                    logger.Warn("Error queue " + ErrorQueue + " does not exist.");
        //                    logger.Debug("Going to create error queue: " + ErrorQueue);

        //                    MessageQueue.Create(eq, true);

        //                    logger.Debug("Error queue created.");
        //                }
        //            }
        //            catch (Exception ex)
        //            {
        //                logger.Error("Could not create error queue or check its existence. Processing will still continue.", ex);
        //            }
        //        }

        //    }
        //}

        #region Method: Send(mqMessage, destination)
        /// <summary>
        /// Send a constructed MQMessage to a specified destination
        /// </summary>
        /// <param name="queueMessage"></param>
        /// <param name="destination"></param>
        public void Send(MQMessage queueMessage, string destination)
        {
            // checked to make sure we didn't loose connection to the queue manager.
            // reconnect if we did lose connection.
            if (!sendResourceManager.IsConnected)
            {
                InitializeQueueManager(sendResourceManager);
                InitializeInputQueue(sendResourceManager);
            }

            MQPutMessageOptions queuePutMessageOptions = new MQPutMessageOptions();
            sendResourceManager.PutMessage(destination, queueMessage, queuePutMessageOptions);
        }
        #endregion

        /// <summary>
        /// Returns the number of messages in the queue.
        /// </summary>
        /// <returns></returns>
        public int GetNumberOfPendingMessages()
        {
            return receiveResourceManager.Queue.CurrentDepth;
        }

        #endregion

        #region helper methods

        protected WorkerThread AddWorkerThread()
        {
            lock (this.workerThreads)
            {
                WorkerThread result = new WorkerThread(this.Receive);

                this.workerThreads.Add(result);

                result.Stopped += delegate(object sender, EventArgs e)
                {
                    WorkerThread wt = sender as WorkerThread;
                    lock (this.workerThreads)
                        this.workerThreads.Remove(wt);
                };

                return result;
            }
        }

        /// <summary>
        /// Waits for a message to become available on the input queue
        /// and then receives it.
        /// </summary>
        /// <remarks>
        /// If the queue is transactional the receive operation will be wrapped in a 
        /// transaction.
        /// </remarks>
        protected void Receive()
        {
            try
            {
                // checked to make sure we didn't loose connection to the queue manager.
                // reconnect if we did lose connection.
                if (!receiveResourceManager.IsConnected)
                {
                    InitializeQueueManager(receiveResourceManager);
                    InitializeInputQueue(receiveResourceManager);
                }

                MQGetMessageOptions getMessageOptions = new MQGetMessageOptions();
                getMessageOptions.Options |= MQC.MQGMO_WAIT | 
                                             MQC.MQGMO_FAIL_IF_QUIESCING | 
                                             MQC.MQGMO_BROWSE_FIRST;
                getMessageOptions.WaitInterval = 1000;
                
                MQMessage queueMessage = new MQMessage();

                receiveResourceManager.GetMessage(queueMessage, getMessageOptions);
            }
            catch (MQException mqe)
            {
                if (mqe.ReasonCode == MQC.MQRC_NO_MSG_AVAILABLE ||
                    mqe.ReasonCode == MQC.MQRC_CONNECTION_QUIESCING)
                {
                    return;
                }

                throw;
            }

            needToAbort = false;

            try
            {
                if (this.IsTransactional)
                    new TransactionWrapper().RunInTransaction(this.ReceiveFromQueue, IsolationLevel, TransactionTimeout);
                else
                    this.ReceiveFromQueue();
            }
            catch (AbortHandlingCurrentMessageException)
            {
                //in case AbortHandlingCurrentMessage occurred
            }
        }

        /// <summary>
        /// Receives a message from the input queue.
        /// </summary>
        /// <remarks>
        /// If a message is received the <see cref="TransportMessageReceived"/> event will be raised.
        /// </remarks>
        public void ReceiveFromQueue()
        {
            string messageId = string.Empty;

            try
            {
                MQGetMessageOptions getMessageOptions = new MQGetMessageOptions();
                getMessageOptions.Options |= MQC.MQGMO_FAIL_IF_QUIESCING;

                MQMessage queueMessage = new MQMessage();

                receiveResourceManager.GetMessage(queueMessage, getMessageOptions);

                messageId = ByteArrayToString(queueMessage.MessageId);

                foreach (IMessageModule module in this.modules)
                    module.HandleBeginMessage();

                this.OnStartedMessageProcessing();

                if (this.IsTransactional)
                {
                    if (MessageHasFailedMaxRetries(queueMessage))
                    {
                        MoveToErrorQueue(queueMessage);

                        ActivateEndMethodOnMessageModules();

                        this.OnFinishedMessageProcessing();

                        return;
                    }
                }

                TransportMessage result = ConvertToTransportMessage(queueMessage);

                if (this.SkipDeserialization)
                    result.BodyStream = new MemoryStream(queueMessage.ReadBytes(queueMessage.MessageLength));
                else
                {
                    try
                    {
                        result.Body = Extract(queueMessage);
                    }
                    catch (Exception e)
                    {
                        logger.Error("Could not extract message data.", e);

                        MoveToErrorQueue(queueMessage);

                        return; // deserialization failed - no reason to try again, so don't throw
                    }
                }

                List<Exception> exceptions = new List<Exception>();

                if (this.TransportMessageReceived != null)
                    try
                    {
                        this.TransportMessageReceived(this, new TransportMessageReceivedEventArgs(result));
                    }
                    catch (Exception e)
                    {
                        exceptions.Add(e);
                        logger.Error("Failed raising transport message received event.", e);
                    }

                exceptions.AddRange( ActivateEndMethodOnMessageModules() );

                if (exceptions.Count > 0)
                    throw new ApplicationException(string.Format("{0} exceptions occured while processing message.", exceptions.Count));
            }
            catch (MQException mqe)
            {
                if (mqe.ReasonCode == MQC.MQRC_NO_MSG_AVAILABLE ||
                    mqe.ReasonCode == MQC.MQRC_CONNECTION_QUIESCING)
                {
                    return;
                }

                throw;
            }
            catch
            {
                if (this.IsTransactional)
                {
                    throw;
                }
                else
                {
                    this.OnFinishedMessageProcessing();

                    throw;
                }
            }

            if (needToAbort)
                throw new AbortHandlingCurrentMessageException();

            this.OnFinishedMessageProcessing();

            return;
        }

        protected bool MessageHasFailedMaxRetries(MQMessage m)
        {
            return m.BackoutCount >= this.maxRetries &&
                   m.BackoutCount > 0;
        }

        /// <summary>
        /// Calls the "HandleEndMessage" on all message modules
        /// aggregating exceptions thrown and returning them.
        /// </summary>
        /// <returns></returns>
        protected IList<Exception> ActivateEndMethodOnMessageModules()
        {
            IList<Exception> result = new List<Exception>();

            foreach (IMessageModule module in this.modules)
                try
                {
                    module.HandleEndMessage();
                }
                catch (Exception e)
                {
                    result.Add(e);
                    logger.Error(
                        string.Format("Failure in HandleEndMessage of message module: {0}",
                                      module.GetType().FullName), e);
                }

            return result;
        }

        /// <summary>
        /// Moves the given message to the configured error queue.
        /// </summary>
        /// <param name="m"></param>
        protected void MoveToErrorQueue(MQMessage m)
        {
            m.ApplicationIdData = m.ApplicationIdData +
                      string.Format("<{0}>{1}</{0}>", FAILEDQUEUE, InputQueue);

            if (!string.IsNullOrEmpty(this.ErrorQueue))
            {
                MQPutMessageOptions queuePutMessageOptions = new MQPutMessageOptions();
                receiveResourceManager.PutMessage(ErrorQueue, m, queuePutMessageOptions);
            }
        }

        /// <summary>
        /// Causes the processing of the current message to be aborted.
        /// </summary>
        public void AbortHandlingCurrentMessage()
        {
            needToAbort = true;
        }

        /// <summary>
        /// Checks whether or not a queue is local by its path.
        /// </summary>
        /// <param name="value">The path to the queue to check.</param>
        /// <returns>true if the queue is local, otherwise false.</returns>
        public static bool QueueIsLocal(string value)
        {
            string machineName = Environment.MachineName.ToLower();

            value = value.ToLower().Replace(PREFIX.ToLower(), "");
            int index = value.IndexOf('\\');

            string queueMachineName = value.Substring(0, index).ToLower();

            return (machineName == queueMachineName || queueMachineName == "localhost" || queueMachineName == ".");
        }

        #region Method: Convert(transportMessage)
        /// <summary>
        /// Convert from a TransportMessage to a MQMessage
        /// </summary>
        /// <param name="transportMessage"></param>
        /// <returns></returns>
        public MQMessage Convert(TransportMessage transportMessage)
        {
            MQMessage queueMessage = new MQMessage();
            MQPutMessageOptions queuePutMessageOptions = new MQPutMessageOptions();
            Stream messageStream = new MemoryStream();
            System.Text.ASCIIEncoding encoding = new System.Text.ASCIIEncoding();

            if (transportMessage.Body == null && transportMessage.BodyStream != null)
                messageStream = transportMessage.BodyStream;
            else
            {
                // convert the message body to a stream
                this.messageSerializer.Serialize(transportMessage.Body, messageStream);
            }

            //write content to the message
            StreamReader sr = new StreamReader(messageStream);
            messageStream.Position = 0;
            queueMessage.WriteString(sr.ReadToEnd());
            queueMessage.Format = MQC.MQFMT_STRING;

            if (transportMessage.CorrelationId != null)
            {
                queueMessage.GroupId = encoding.GetBytes(transportMessage.CorrelationId);
            }

            //TODO Set the recoverable and response queue on the message being sent
            //toSend.Recoverable = m.Recoverable;
            //toSend.ResponseQueue = new MessageQueue(GetFullPath(m.ReturnAddress));
            queueMessage.ReplyToQueueName = transportMessage.ReturnAddress;
            FillApplicationIdData(queueMessage, transportMessage);

            //TODO Can we set the timeout on a message being sent?
            //if (m.TimeToBeReceived < MessageQueue.InfiniteTimeout)
            //    toSend.TimeToBeReceived = m.TimeToBeReceived;

            //TODO How can we pass header information on the message
            //if (m.Headers != null && m.Headers.Count > 0)
            //{
            //    MemoryStream stream = new MemoryStream();
            //    headerSerializer.Serialize(stream, m.Headers);
            //    toSend.Extension = stream.GetBuffer();
            //}

            return queueMessage;
        }
        #endregion

        #region Method: ConvertToTransportMessage(mqMessage)
        /// <summary>
        /// Converts an WMQ <see cref="MQMessage"/> into an NServiceBus message.
        /// </summary>
        /// <param name="mqMessage">The WMQ message to convert.</param>
        /// <returns>An NServiceBus message.</returns>
        public TransportMessage ConvertToTransportMessage(MQMessage mqMessage)
        {
            TransportMessage result = new TransportMessage();
            result.Id = ByteArrayToString(mqMessage.MessageId);
            result.CorrelationId = (ByteArrayToString(mqMessage.CorrelationId).Equals("00000000-0000-0000-0000-000000000000\\0", StringComparison.InvariantCulture) ? null : ByteArrayToString(mqMessage.CorrelationId));
            // TODO Can we get recoverable and time to be received properties from Wmq?
            //result.Recoverable = mqMessage.Recoverable;
            //result.TimeToBeReceived = mqMessage.TimeToBeReceived;

            result.ReturnAddress = mqMessage.ReplyToQueueName;

            FillIdForCorrelationAndWindowsIdentity(result, mqMessage);

            if (string.IsNullOrEmpty(result.IdForCorrelation))
                result.IdForCorrelation = result.Id;

            //TODO Does Wmq have a place to store header information
            //if (mqMessage.Extension != null)
            //    if (mqMessage.Extension.Length > 0)
            //    {
            //        MemoryStream stream = new MemoryStream(mqMessage.Extension);
            //        object o = headerSerializer.Deserialize(stream);
            //        result.Headers = o as List<HeaderInfo>;
            //    }



            return result;
        }
        #endregion

        /// <summary>
        /// Returns the queue whose process failed processing the given message
        /// by accessing the label of the message.
        /// </summary>
        /// <param name="m"></param>
        /// <returns></returns>
        public static string GetFailedQueue(MQMessage m)
        {
            if (string.IsNullOrEmpty(m.ApplicationIdData))
                return null;

            if (!m.ApplicationIdData.Contains(FAILEDQUEUE))
                return null;

            int startIndex = m.ApplicationIdData.IndexOf(string.Format("<{0}>", FAILEDQUEUE)) + FAILEDQUEUE.Length + 2;
            int count = m.ApplicationIdData.IndexOf(string.Format("</{0}>", FAILEDQUEUE)) - startIndex;

            return m.ApplicationIdData.Substring(startIndex, count);
        }

        /// <summary>
        /// Gets the label of the message stripping out the failed queue.
        /// </summary>
        /// <param name="m"></param>
        /// <returns></returns>
        public static string GetLabelWithoutFailedQueue(MQMessage m)
        {
            if (string.IsNullOrEmpty(m.ApplicationIdData))
                return null;

            if (!m.ApplicationIdData.Contains(FAILEDQUEUE))
                return m.ApplicationIdData;

            int startIndex = m.ApplicationIdData.IndexOf(string.Format("<{0}>", FAILEDQUEUE));
            int endIndex = m.ApplicationIdData.IndexOf(string.Format("</{0}>", FAILEDQUEUE));
            endIndex += FAILEDQUEUE.Length + 3;

            return m.ApplicationIdData.Remove(startIndex, endIndex - startIndex);
        }

        protected static void FillIdForCorrelationAndWindowsIdentity(TransportMessage result, MQMessage queueMessage)
        {
            string applicationIdData = queueMessage.ApplicationIdData;

            if (applicationIdData == null)
                return;

            if (applicationIdData.Contains(IDFORCORRELATION))
            {
                int idStartIndex = applicationIdData.IndexOf(string.Format("<{0}>", IDFORCORRELATION)) + IDFORCORRELATION.Length + 2;
                int idCount = applicationIdData.IndexOf(string.Format("</{0}>", IDFORCORRELATION)) - idStartIndex;

                result.IdForCorrelation = applicationIdData.Substring(idStartIndex, idCount);
            }

            if (applicationIdData.Contains(WINDOWSIDENTITYNAME))
            {
                int winStartIndex = applicationIdData.IndexOf(string.Format("<{0}>", WINDOWSIDENTITYNAME)) + WINDOWSIDENTITYNAME.Length + 2;
                int winCount = applicationIdData.IndexOf(string.Format("</{0}>", WINDOWSIDENTITYNAME)) - winStartIndex;

                result.WindowsIdentityName = applicationIdData.Substring(winStartIndex, winCount);
            }
        }

        protected static void FillApplicationIdData(MQMessage queueMessage, TransportMessage m)
        {
            queueMessage.ApplicationIdData = string.Format("<{0}>{2}</{0}><{1}>{3}</{1}>", IDFORCORRELATION, WINDOWSIDENTITYNAME, m.IdForCorrelation, m.WindowsIdentityName);
        }

        /// <summary>
        /// Extracts the messages from an WMQ <see cref="MQMessage"/>.
        /// </summary>
        /// <param name="message">The WMQ message to extract from.</param>
        /// <returns>An array of handleable messages.</returns>
        protected IMessage[] Extract(MQMessage message)
        {
//            return this.messageSerializer.Deserialize((MemoryStream)message.ReadObject());
            MemoryStream stream = new MemoryStream(StringToByteArray(message.ReadString(message.MessageLength)));
            return this.messageSerializer.Deserialize(stream);
        }

        // TODO Deal with this transaction stuff
        ///// <summary>
        ///// Gets the transaction type to use when receiving a message from the queue.
        ///// </summary>
        ///// <returns>The transaction type to use.</returns>
        //private MessageQueueTransactionType GetTransactionTypeForReceive()
        //{
        //    if (this.IsTransactional)
        //        return MessageQueueTransactionType.Automatic;
        //    else
        //        return MessageQueueTransactionType.None;
        //}

        ///// <summary>
        ///// Gets the transaction type to use when sending a message.
        ///// </summary>
        ///// <returns>The transaction type to use.</returns>
        //private MessageQueueTransactionType GetTransactionTypeForSend()
        //{
        //    if (this.IsTransactional)
        //    {
        //        if (Transaction.Current != null)
        //            return MessageQueueTransactionType.Automatic;
        //        else
        //            return MessageQueueTransactionType.Single;
        //    }
        //    else
        //        return MessageQueueTransactionType.Single;
        //}

        /// <summary>
        /// Get a list of serializable types from the list of types provided.
        /// </summary>
        /// <param name="value">A list of types process.</param>
        /// <returns>A list of serializable types.</returns>
        protected static Type[] GetExtraTypes(IEnumerable<Type> value)
        {
            List<Type> types = new List<Type>(value);
            if (!types.Contains(typeof(List<object>)))
                types.Add(typeof(List<object>));

            return types.ToArray();
        }

        protected void OnStartedMessageProcessing()
        {
            if (this.StartedMessageProcessing != null)
                this.StartedMessageProcessing(this, null);
        }

        protected void OnFinishedMessageProcessing()
        {
            if (this.FinishedMessageProcessing != null)
                this.FinishedMessageProcessing(this, null);
        }

        // convert byte array to string
        protected static string ByteArrayToString(byte[] byteArray)
        {
//            System.Text.UnicodeEncoding encoding = new System.Text.UnicodeEncoding();
            System.Text.ASCIIEncoding encoding = new System.Text.ASCIIEncoding();
            return encoding.GetString(byteArray);
        }

        // convert a string to a byte array.
        public static byte[] StringToByteArray(string str)
        {
            System.Text.ASCIIEncoding encoding = new System.Text.ASCIIEncoding();
            return encoding.GetBytes(str);
        }
        #endregion

        #region static conversion methods

        ///// <summary>
        ///// Resolves a destination WMQ queue address.
        ///// </summary>
        ///// <param name="destination">The WMQ address to resolve.</param>
        ///// <returns>The direct format name of the queue.</returns>
        //public static string Resolve(string destination)
        //{
        //    string dest = destination.ToLower().Replace(PREFIX.ToLower(), "");

        //    string[] arr = dest.Split('\\');
        //    if (arr.Length == 1)
        //        dest = Environment.MachineName + "\\private$\\" + dest;

        //    MessageQueue q = new MessageQueue(dest);
        //    if (q.MachineName.ToLower() == Environment.MachineName.ToLower())
        //        q.MachineName = Environment.MachineName;

        //    return PREFIX + q.Path;
        //}

        /// <summary>
        /// Turns a '@' separated value into a full msmq path.
        /// Format is 'queue@machine'.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static string GetFullPath(string value)
        {
            return PREFIX + GetFullPathWithoutPrefix(value);
        }

        /// <summary>
        /// Returns the full path without Format or direct os
        /// from a '@' separated path.
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static string GetFullPathWithoutPrefix(string value)
        {
            return GetMachineNameFromLogicalName(value) + PRIVATE + GetQueueNameFromLogicalName(value);
        }

        /// <summary>
        /// Returns the queue name from a '@' separated full logical name.
        /// </summary>
        /// <param name="logicalName"></param>
        /// <returns></returns>
        public static string GetQueueNameFromLogicalName(string logicalName)
        {
            string[] arr = logicalName.Split('@');

            if (arr.Length >= 1)
                return arr[0];

            return null;
        }

        /// <summary>
        /// Returns the machine name from a '@' separated full logical name,
        /// or the Environment.MachineName otherwise.
        /// </summary>
        /// <param name="logicalName"></param>
        /// <returns></returns>
        public static string GetMachineNameFromLogicalName(string logicalName)
        {
            string[] arr = logicalName.Split('@');

            string machine = Environment.MachineName;

            if (arr.Length >= 2)
                if (arr[1] != "." && arr[1].ToLower() != "localhost")
                    machine = arr[1];

            return machine;
        }

        ///// <summary>
        ///// Gets an independent address for the queue in the form:
        ///// queue@machine.
        ///// </summary>
        ///// <param name="q"></param>
        ///// <returns></returns>
        //public static string GetIndependentAddressForQueue(MessageQueue q)
        //{
        //    if (q == null)
        //        return null;

        //    string[] arr = q.FormatName.Split('\\');
        //    string queueName = arr[arr.Length - 1];

        //    int directPrefixIndex = arr[0].IndexOf(DIRECTPREFIX);
        //    if (directPrefixIndex >= 0)
        //    {
        //        return queueName + '@' + arr[0].Substring(directPrefixIndex + DIRECTPREFIX.Length);
        //    }

        //    try
        //    {
        //        // the pessimistic approach failed, try the optimistic approach
        //        arr = q.QueueName.Split('\\');
        //        queueName = arr[arr.Length - 1];
        //        return queueName + '@' + q.MachineName;
        //    }
        //    catch
        //    {
        //        throw new Exception(string.Concat("MessageQueueException: '",
        //        DIRECTPREFIX, "' is missing. ",
        //        "FormatName='", q.FormatName, "'"));
        //    }


        //}

        #endregion

        #region members

        protected static readonly string DIRECTPREFIX = "DIRECT=OS:";
        protected static readonly string PREFIX = "FormatName:" + DIRECTPREFIX;
        protected static readonly string PRIVATE = "\\private$\\";
        protected static readonly string IDFORCORRELATION = "CorrId";
        protected static readonly string WINDOWSIDENTITYNAME = "WinIdName";
        protected static readonly string FAILEDQUEUE = "FailedQ";

        protected readonly IList<WorkerThread> workerThreads = new List<WorkerThread>();

        // the MQ Queue Manger does not support multiple threads, so we are instantiating two instances
        // of the queue manager so the receiving thread doesn't hold up the sending thread.
        protected WmqResourceManager receiveResourceManager = new WmqResourceManager();
        protected WmqResourceManager sendResourceManager = new WmqResourceManager();

        /// <summary>
        /// The list of message modules.
        /// </summary>
        protected readonly List<IMessageModule> modules = new List<IMessageModule>();

        [ThreadStatic]
        protected static volatile bool needToAbort;

        protected static readonly ILog logger = LogManager.GetLogger(typeof(WmqTransport));

        protected XmlSerializer headerSerializer = new XmlSerializer(typeof(List<HeaderInfo>));
        #endregion

        #region IDisposable Members

        /// <summary>
        /// Stops all worker threads and disposes the WMQ queue.
        /// </summary>
        public void Dispose()
        {
            lock (this.workerThreads)
                for (int i = 0; i < workerThreads.Count; i++)
                    this.workerThreads[i].Stop();

            if (receiveResourceManager != null)
            {
                receiveResourceManager.CloseWMQConnections();
                receiveResourceManager = null;
            }

            if (sendResourceManager != null)
            {
                sendResourceManager.CloseWMQConnections();
                sendResourceManager = null;
            }
        }

        #endregion
    }
}
