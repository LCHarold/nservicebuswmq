Allows using IBM WebSphere MQ as the queue mechanism for [nServiceBus version 1.9](http://sourceforge.net/projects/nservicebus/files/).

Supports failing over to a local MSMQ queue if publishing to MQ fails. This is important in environments where MQ is brought down in a scheduled manner to do maintenance. Once MQ comes back up, messages on the failover MSMQ queue are automatically published to MQ.

This adapter was written by Tim Sieberg and Neil Adams, with contributions by Adam Tybor.