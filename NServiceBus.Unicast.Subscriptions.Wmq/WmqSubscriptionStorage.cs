using System;
using System.Collections.Generic;
using NServiceBus.Unicast.Transport;
using NServiceBus.Unicast.Transport.Wmq;
using Common.Logging;
using IBM.WMQ;

namespace NServiceBus.Unicast.Subscriptions.Wmq
{
    /// <summary>
    /// Provides functionality for managing message subscriptions
    /// using WMQ.
    /// </summary>
    public class WmqSubscriptionStorage : ISubscriptionStorage
    {
        #region ISubscriptionStorage Members

        /// <summary>
        /// Initializes the storage - doesn't make use of the given message types.
        /// </summary>
        /// <param name="messageTypes"></param>
        public void Init(IList<Type> messageTypes)
        {
            InitializeQueueManager();
            InitializeInputQueue();

            MQGetMessageOptions getMessageOptions = new MQGetMessageOptions();
            getMessageOptions.Options |= MQC.MQGMO_NO_WAIT + MQC.MQGMO_BROWSE_NEXT;

            /// Reads subscription messages from the message queue, but does not remove them
            /// Loop until there is a failure, expecting an exception to indicate no more messages
            bool isContinue = true;
            while (isContinue)
            {
                MQMessage message = new MQMessage();
                try
                {
                    queue.Get(message, getMessageOptions);
                    string subscriber = message.ReplyToQueueName;
                    string messageType = message.ReadString(message.MessageLength);

                    this.entries.Add(new Entry(messageType, subscriber));
                    this.AddToLookup(subscriber, messageType, message.MessageId);
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

            CloseWMQConnections();
        
        }

        /// <summary>
        /// Gets a list of the addresses of subscribers for the specified message.
        /// </summary>
        /// <param name="messageType">The message type to get subscribers for.</param>
        /// <returns>A list of subscriber addresses.</returns>
        public IList<string> GetSubscribersForMessage(Type messageType)
        {
            List<string> result = new List<string>();

            lock (this.locker)
                foreach (Entry e in this.entries)
                    if (e.Matches(messageType))
                        result.Add(e.Subscriber);

            return result;
        }

        /// <summary>
        /// Attempts to handle a subscription message.
        /// </summary>
        /// <param name="msg">The message to attempt to handle.</param>
        /// <returns>true if the message was a valid subscription message, otherwise false.</returns>
        public void HandleSubscriptionMessage(TransportMessage msg)
        {
            this.HandleSubscriptionMessage(msg, true);
        }

        /// <summary>
        /// Attempts to handle a subscription message allowing specification of whether or not
        /// the subscription persistence store should be updated.
        /// </summary>
        /// <param name="msg">The message to attempt to handle.</param>
        /// <param name="updateQueue">Whether or not the subscription persistence store should be updated.</param>
        /// <returns>true if the message was a valid subscription message, otherwise false.</returns>
        private void HandleSubscriptionMessage(TransportMessage msg, bool updateQueue)
        {
            IMessage[] messages = msg.Body;
            if (messages == null)
                return;

            if (messages.Length != 1)
                return;

            SubscriptionMessage subMessage = messages[0] as SubscriptionMessage;

            if (subMessage != null)
            {
                if (subMessage.TypeName == null)
                {
                    log.Debug("Blank subscription message received.");
                    return;
                }

                //Type messageType = IgnoreAssemblyVersion.GetType(subMessage.TypeName, false);
                Type messageType = Type.GetType(subMessage.TypeName, false);
                if (messageType == null)
                {
                    log.Debug("Could not handle subscription for message type: " + subMessage.TypeName + ". Type not available on this endpoint.");
                    return;
                }

                if (updateQueue)
                    if (ConfigurationIsWrong())
                        throw new InvalidOperationException("This endpoint is not transactional. Processing subscriptions on a non-transactional endpoint is not supported. If you still wish to do so, please set the 'DontUseExternalTransaction' property of WmqSubscriptionStorage to 'true'.");

                this.HandleAddSubscription(msg, messageType, subMessage, updateQueue);
                this.HandleRemoveSubscription(msg, messageType, subMessage, updateQueue);
            }
        }

        /// <summary>
        /// Checks if configuration is wrong - endpoint isn't transactional and
        /// object isn't configured to handle own transactions.
        /// </summary>
        /// <returns></returns>
        private bool ConfigurationIsWrong()
        {
            return false;
        }

        /// <summary>
        /// Checks the subscription type, and if it is 'Add', then adds the subscriber.
        /// </summary>
        /// <param name="msg">The message to handle.</param>
        /// <param name="messageType">The message type being subscribed to.</param>
        /// <param name="subMessage">A subscription message.</param>
        /// <param name="updateQueue">Whether or not to update the subscription persistence store.</param>
        private void HandleAddSubscription(TransportMessage msg, Type messageType, SubscriptionMessage subMessage, bool updateQueue)
        {
            if (subMessage.SubscriptionType == SubscriptionType.Add)
            {
                lock (this.locker)
                {
                    // if already subscribed, do nothing
                    foreach (Entry e in this.entries)
                        if (e.Matches(messageType) && e.Subscriber == msg.ReturnAddress)
                            return;

                    if (updateQueue)
                        this.Add(msg.ReturnAddress, subMessage.TypeName);

                    this.entries.Add(new Entry(messageType, msg));

                    log.Debug("Subscriber " + msg.ReturnAddress + " added for message " + messageType.FullName + ".");
                }
            }
        }

        /// <summary>
        /// Handles a removing a subscription.
        /// </summary>
        /// <param name="msg">The message to handle.</param>
        /// <param name="messageType">The message type being subscribed to.</param>
        /// <param name="subMessage">A subscription message.</param>
        /// <param name="updateQueue">Whether or not to update the subscription persistence store.</param>
        private void HandleRemoveSubscription(TransportMessage msg, Type messageType, SubscriptionMessage subMessage, bool updateQueue)
        {
            if (subMessage.SubscriptionType == SubscriptionType.Remove)
            {
                lock (this.locker)
                {
                    foreach (Entry e in this.entries.ToArray())
                        if (e.Matches(messageType) && e.Subscriber == msg.ReturnAddress)
                        {
                            if (updateQueue)
                                this.Remove(e.Subscriber, e.MessageType);

                            this.entries.Remove(e);

                            log.Debug("Subscriber " + msg.ReturnAddress + " removed for message " + messageType.FullName + ".");
                        }
                }
            }
        }

        /// <summary>
        /// Adds a message to the subscription store.
        /// </summary>
        public void Add(string subscriber, string typeName)
        {
            MQMessage message = new MQMessage();        // MQMessage instance
            MQPutMessageOptions putMessageOptions;      // MQPutMessageOptions instance

            message.ReplyToQueueName = subscriber;
            message.WriteString(typeName);
            message.Format = MQC.MQFMT_STRING;

            putMessageOptions = new MQPutMessageOptions();
            putMessageOptions.Options |= MQC.MQPMO_NEW_MSG_ID;

            InitializeQueueManager();
            InitializeInputQueue();
            this.queue.Put(message, putMessageOptions);
            CloseWMQConnections();

            this.AddToLookup(subscriber, typeName, message.MessageId);
        }

        /// <summary>
        /// Removes a message from the subscription store.
        /// </summary>
        public void Remove(string subscriber, string typeName)
        {
            byte[] messageId = RemoveFromLookup(subscriber, typeName);

            if (messageId == null)
                return;

            MQMessage message = new MQMessage();
            message.MessageId = messageId;
            MQGetMessageOptions getMessageOptions = new MQGetMessageOptions(); 
            getMessageOptions.MatchOptions |= MQC.MQMO_MATCH_MSG_ID;

            InitializeQueueManager();
            InitializeInputQueue();
            this.queue.Get(message, getMessageOptions);
            CloseWMQConnections();
        }

        #endregion

        #region config info

        /// <summary>
        /// Gets/sets whether or not to use a trasaction started outside the 
        /// subscription store.
        /// </summary>
        public virtual bool DontUseExternalTransaction { get; set; }

        /// <summary>
        /// The channel info used to connect to the MQ Queue Manager
        /// </summary>
        public virtual string ChannelInfo { get; set; }

        /// <summary>
        /// The WebSphere MQ Queue Manager
        /// </summary>
        public virtual string QueueManagerName { get; set; }

        public MQQueueManager QueueManager
        {
            get 
            {
                if (queueManager == null)
                {
                    InitializeQueueManager();
                }
                return queueManager; 
            }
            set { queueManager = value; }
        }

        /// <summary>
        /// Sets the address of the queue where subscription messages will be stored.
        /// </summary>
        public virtual string Queue { get; set; }

        #endregion

        #region helper methods

        private void InitializeQueueManager()
        {
            char[] separator = { '/' };
            string[] channelParams = ChannelInfo.Split(separator);
            if (channelParams.Length < 3)
            {
                throw new ApplicationException("ChannelInfo config parameter is not in the proper format.  It must include two /'s and must be in this format: channel/transport type/connection.  Example: CHANNEL1/TCP/mqwind.ttx.com(1444)");
            }
            string channelName = channelParams[0];
            string transportType = channelParams[1];
            string connectionName = channelParams[2];

            try
            {
                queueManager = new MQQueueManager(QueueManagerName, channelName, connectionName);
            }
            catch (Exception ex)
            {
                log.Error("Exception in WmqSubscriptionStorage.InitializeQueueManager trying to connect to: " + 
                    QueueManagerName + " using channel " + channelName + " and connection " + connectionName + 
                    Environment.NewLine + ex.Message);
                throw;
            }
        }

        private void InitializeInputQueue()
        {
            if (QueueManager != null)
            {
                this.queue = this.queueManager.AccessQueue(Queue, MQC.MQOO_INPUT_SHARED +
                                                                  MQC.MQOO_OUTPUT +
                                                                  MQC.MQOO_FAIL_IF_QUIESCING +
                                                                  MQC.MQOO_BROWSE);
            }
            else
            {
                string errorMessage = "Unable to initialize Input Queue - QueueManager is null";
                log.Error(errorMessage);
                throw new NullReferenceException(errorMessage);
            }
            return;
        }

        /// <summary>
        /// Adds a message to the lookup to find message from
        /// subscriber, to message type, to message id
        /// </summary>
        private void AddToLookup(string subscriber, string typeName, byte[] messageId)
        {
            lock (this.lookup)
            {
                if (!this.lookup.ContainsKey(subscriber))
                    this.lookup.Add(subscriber, new Dictionary<string, byte[]>());

                if (!this.lookup[subscriber].ContainsKey(typeName))
                    this.lookup[subscriber].Add(typeName, messageId);
            }
        }

        private byte[] RemoveFromLookup(string subscriber, string typeName)
        {
            byte[] messageId = null;
            lock (this.lookup)
            {
                Dictionary<string, byte[]> endpoints;
                if (this.lookup.TryGetValue(subscriber, out endpoints))
                {
                    if (endpoints.TryGetValue(typeName, out messageId))
                    {
                        endpoints.Remove(typeName);
                        if (endpoints.Count == 0)
                        {
                            this.lookup.Remove(subscriber);
                        }
                    }
                }
            }
            return messageId;
        }

        private void CloseWMQConnections()
        {
            if (this.queue != null)
                this.queue.Close();

            if (this.queueManager != null)
            {
                this.queueManager.Disconnect();
                this.queueManager.Close();
            }
        }

        #endregion

        #region members

        private MQQueueManager queueManager;

        private MQQueue queue;

        /// <summary>
        /// lookup from subscriber, to message type, to message id
        /// </summary>
        private Dictionary<string, Dictionary<string, byte[]>> lookup = new Dictionary<string, Dictionary<string, byte[]>>();

        private List<Entry> entries = new List<Entry>();
        private object locker = new object();

        private ILog log = LogManager.GetLogger(typeof(ISubscriptionStorage));

        #endregion

    }
}
