using System;
using System.Transactions;
using IBM.WMQ;

namespace NServiceBus.Utils.Wmq
{
    public class WmqResourceManager : IEnlistmentNotification, IDisposable
    {
        public MQQueueManager QueueManager { get; set; }
        public MQQueue Queue { get; set; }
        
        public bool IsConnected
        {
            get
            {
                return (QueueManager != null && QueueManager.IsConnected);
            }
        }

        public void GetMessage(MQMessage message, MQGetMessageOptions getOptions)
        {
            Transaction currentTx = Transaction.Current;
            if (currentTx != null)
            {
                getOptions.Options |= MQC.MQGMO_SYNCPOINT;
                currentTx.EnlistVolatile(this, EnlistmentOptions.None);
            }

            try
            {
                Queue.Get(message, getOptions);
            }
            catch (MQException mqe)
            {
                if (mqe.ReasonCode == MQC.MQRC_CONNECTION_BROKEN)
                {
                    // for some reason, the Close method on the Queue fails after
                    // a connection has been broken.

                    Queue = null;
                    QueueManager.Close();
                    QueueManager = null;
                }

                throw;
            }
        }

        public void PutMessage(MQMessage message, MQPutMessageOptions options)
        {
            PutMessage(Queue.Name, message, options);
        }

        public void PutMessage(string destination, MQMessage message, MQPutMessageOptions putOptions)
        {
            Transaction currentTx = Transaction.Current;
            if (currentTx != null)
            {
                putOptions.Options |= MQC.MQPMO_SYNCPOINT;
                currentTx.EnlistVolatile(this, EnlistmentOptions.None);
            }

            try
            {
                QueueManager.Put(destination, message, putOptions);
            }
            catch (MQException mqe)
            {
                if (mqe.ReasonCode == MQC.MQRC_CONNECTION_BROKEN)
                {
                    // for some reason, the Close method on the Queue fails after
                    // a connection has been broken.

                    Queue = null;
                    QueueManager.Close();
                    QueueManager = null;
                }

                throw;
            }
        }

        public void CloseWMQConnections()
        {
            try
            {
                if (Queue != null)
                {
                    Queue.Close();
                }
            }
            finally
            {
                Queue = null;
            }

            try
            {
                if (QueueManager != null)
                {
                    QueueManager.Disconnect();
                    QueueManager.Close();
                }
            }
            finally
            {
                QueueManager = null;
            }
        }

        #region IEnlistmentNotification Members

        public void Prepare(PreparingEnlistment preparingEnlistment)
        {
            preparingEnlistment.Prepared();
        }

        public void Commit(Enlistment enlistment)
        {
            QueueManager.Commit();
            enlistment.Done();  
        }

        public void InDoubt(Enlistment enlistment)
        {
            QueueManager.Backout();
            enlistment.Done();
        }

        public void Rollback(Enlistment enlistment)
        {
            QueueManager.Backout();
            enlistment.Done();  
        }

        #endregion

        #region IDisposable Members

        /// <summary>
        /// Stops all worker threads and disposes the WMQ queue.
        /// </summary>
        public void Dispose()
        {
            CloseWMQConnections();
        }

        #endregion
    }
}