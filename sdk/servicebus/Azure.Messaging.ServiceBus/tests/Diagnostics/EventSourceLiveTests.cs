﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading.Tasks;
using System.Transactions;
using Azure.Core.TestFramework;
using Azure.Messaging.ServiceBus.Diagnostics;
using NUnit.Framework;

namespace Azure.Messaging.ServiceBus.Tests.Diagnostics
{
    [NonParallelizable]
    public class EventSourceLiveTests : ServiceBusLiveTestBase
    {
        private TestEventListener _listener;

        [SetUp]
        public void Setup()
        {
            _listener = new TestEventListener();
            _listener.EnableEvents(ServiceBusEventSource.Log, EventLevel.Verbose);
        }

        [TearDown]
        public void TearDown()
        {
            _listener.Dispose();
        }

        [Test]
        public async Task LogsEvents()
        {
            await using (var scope = await ServiceBusScope.CreateWithQueue(enablePartitioning: false, enableSession: false))
            {
                await using var client = GetNoRetryClient();
                _listener.SingleEventById(ServiceBusEventSource.ClientCreateStartEvent, e => e.Payload.Contains(nameof(ServiceBusClient)) && e.Payload.Contains(client.FullyQualifiedNamespace));
                var messageCount = 10;

                ServiceBusSender sender = client.CreateSender(scope.QueueName);
                _listener.EventsById(ServiceBusEventSource.ClientCreateStartEvent).Where(e => e.Payload.Contains(nameof(ServiceBusSender)) && e.Payload.Contains(sender.FullyQualifiedNamespace) && e.Payload.Contains(sender.EntityPath));
                using ServiceBusMessageBatch batch = await sender.CreateBatchAsync();
                _listener.SingleEventById(ServiceBusEventSource.CreateMessageBatchStartEvent, e => e.Payload.Contains(sender.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.CreateMessageBatchCompleteEvent, e => e.Payload.Contains(sender.Identifier));

                IEnumerable<ServiceBusMessage> messages = AddMessages(batch, messageCount).AsEnumerable<ServiceBusMessage>();

                await sender.SendAsync(batch);
                _listener.SingleEventById(ServiceBusEventSource.CreateSendLinkStartEvent, e => e.Payload.Contains(sender.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.CreateSendLinkCompleteEvent, e => e.Payload.Contains(sender.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.SendMessageStartEvent, e => e.Payload.Contains(sender.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.SendMessageCompleteEvent, e => e.Payload.Contains(sender.Identifier));

                Assert.That(
                    async () => await client.CreateSessionReceiverAsync(scope.QueueName),
                    Throws.InstanceOf<InvalidOperationException>());
                _listener.SingleEventById(ServiceBusEventSource.ClientCreateStartEvent, e => e.Payload.Contains(nameof(ServiceBusSessionReceiver)) && e.Payload.Contains(client.FullyQualifiedNamespace) && e.Payload.Contains(scope.QueueName));
                _listener.SingleEventById(ServiceBusEventSource.ClientCreateExceptionEvent, e => e.Payload.Contains(nameof(ServiceBusSessionReceiver)) && e.Payload.Contains(client.FullyQualifiedNamespace) && e.Payload.Contains(scope.QueueName));

                var receiver = client.CreateReceiver(scope.QueueName);
                _listener.SingleEventById(ServiceBusEventSource.ClientCreateStartEvent, e => e.Payload.Contains(nameof(ServiceBusReceiver)) && e.Payload.Contains(client.FullyQualifiedNamespace));

                var messageEnum = messages.GetEnumerator();
                var remainingMessages = messageCount;
                while (remainingMessages > 0)
                {
                    foreach (var item in await receiver.ReceiveBatchAsync(remainingMessages))
                    {
                        remainingMessages--;
                        messageEnum.MoveNext();
                        Assert.AreEqual(messageEnum.Current.MessageId, item.MessageId);
                        Assert.AreEqual(item.DeliveryCount, 1);
                    }
                }
                _listener.SingleEventById(ServiceBusEventSource.CreateReceiveLinkStartEvent, e => e.Payload.Contains(receiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.CreateReceiveLinkCompleteEvent, e => e.Payload.Contains(receiver.Identifier));
                Assert.IsTrue(_listener.EventsById(ServiceBusEventSource.ReceiveMessageStartEvent).Any());
                Assert.IsTrue(_listener.EventsById(ServiceBusEventSource.ReceiveMessageCompleteEvent).Any());
                Assert.AreEqual(0, remainingMessages);
                messageEnum.Reset();

                foreach (var item in await receiver.PeekBatchAsync(messageCount))
                {
                    messageEnum.MoveNext();
                    Assert.AreEqual(messageEnum.Current.MessageId, item.MessageId);
                }

                _listener.SingleEventById(ServiceBusEventSource.CreateManagementLinkStartEvent, e => e.Payload.Contains(receiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.CreateManagementLinkCompleteEvent, e => e.Payload.Contains(receiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.PeekMessageStartEvent, e => e.Payload.Contains(receiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.PeekMessageCompleteEvent, e => e.Payload.Contains(receiver.Identifier));

                var seq = await sender.ScheduleMessageAsync(new ServiceBusMessage(), DateTimeOffset.UtcNow.AddMinutes(1));
                _listener.SingleEventById(ServiceBusEventSource.ScheduleMessageStartEvent, e => e.Payload.Contains(sender.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.ScheduleMessageCompleteEvent, e => e.Payload.Contains(sender.Identifier));

                await sender.CancelScheduledMessageAsync(seq);
                _listener.SingleEventById(ServiceBusEventSource.CancelScheduledMessageStartEvent, e => e.Payload.Contains(sender.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.CancelScheduledMessageCompleteEvent, e => e.Payload.Contains(sender.Identifier));

                await receiver.DisposeAsync();
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeStartEvent, e => e.Payload.Contains(nameof(ServiceBusReceiver)) && e.Payload.Contains(receiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeCompleteEvent, e => e.Payload.Contains(nameof(ServiceBusReceiver)) && e.Payload.Contains(receiver.Identifier));

                await sender.DisposeAsync();
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeStartEvent, e => e.Payload.Contains(nameof(ServiceBusSender)) && e.Payload.Contains(sender.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeCompleteEvent, e => e.Payload.Contains(nameof(ServiceBusSender)) && e.Payload.Contains(sender.Identifier));

                await client.DisposeAsync();
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeStartEvent, e => e.Payload.Contains(nameof(ServiceBusClient)) && e.Payload.Contains(client.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeCompleteEvent, e => e.Payload.Contains(nameof(ServiceBusClient)) && e.Payload.Contains(client.Identifier));
            }
        }

        [Test]
        public async Task LogsSessionEvents()
        {
            await using (var scope = await ServiceBusScope.CreateWithQueue(enablePartitioning: false, enableSession: true))
            {
                await using var client = GetNoRetryClient();
                _listener.SingleEventById(ServiceBusEventSource.ClientCreateStartEvent, e => e.Payload.Contains(nameof(ServiceBusClient)) && e.Payload.Contains(client.FullyQualifiedNamespace));
                var messageCount = 10;

                ServiceBusSender sender = client.CreateSender(scope.QueueName);
                _listener.SingleEventById(ServiceBusEventSource.ClientCreateStartEvent, e => e.Payload.Contains(nameof(ServiceBusSender)) && e.Payload.Contains(sender.FullyQualifiedNamespace) && e.Payload.Contains(sender.EntityPath));
                using ServiceBusMessageBatch batch = await sender.CreateBatchAsync();
                _listener.SingleEventById(ServiceBusEventSource.CreateMessageBatchStartEvent, e => e.Payload.Contains(sender.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.CreateMessageBatchCompleteEvent, e => e.Payload.Contains(sender.Identifier));

                IEnumerable<ServiceBusMessage> messages = AddMessages(batch, messageCount, "sessionId").AsEnumerable<ServiceBusMessage>();

                await sender.SendAsync(batch);
                _listener.SingleEventById(ServiceBusEventSource.CreateSendLinkStartEvent, e => e.Payload.Contains(sender.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.CreateSendLinkCompleteEvent, e => e.Payload.Contains(sender.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.SendMessageStartEvent, e => e.Payload.Contains(sender.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.SendMessageCompleteEvent, e => e.Payload.Contains(sender.Identifier));

                var receiver = client.CreateReceiver(scope.QueueName);
                _listener.SingleEventById(ServiceBusEventSource.ClientCreateStartEvent, e => e.Payload.Contains(nameof(ServiceBusReceiver)) && e.Payload.Contains(client.FullyQualifiedNamespace));

                // can't use a non-session receiver for session queue
                Assert.That(
                    async () => await receiver.ReceiveAsync(),
                    Throws.InstanceOf<InvalidOperationException>());

                _listener.SingleEventById(ServiceBusEventSource.CreateReceiveLinkStartEvent, e => e.Payload.Contains(receiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.CreateReceiveLinkExceptionEvent, e => e.Payload.Contains(receiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.ReceiveMessageStartEvent, e => e.Payload.Contains(receiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.ReceiveMessageExceptionEvent, e => e.Payload.Contains(receiver.Identifier));

                var sessionReceiver = await client.CreateSessionReceiverAsync(scope.QueueName);
                _listener.EventsById(ServiceBusEventSource.ClientCreateStartEvent).Where(e => e.Payload.Contains(nameof(ServiceBusSessionReceiver)) && e.Payload.Contains(client.FullyQualifiedNamespace)).Any();
                _listener.SingleEventById(ServiceBusEventSource.CreateReceiveLinkStartEvent, e => e.Payload.Contains(sessionReceiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.CreateReceiveLinkCompleteEvent, e => e.Payload.Contains(sessionReceiver.Identifier));

                var msg = await sessionReceiver.ReceiveAsync();
                _listener.SingleEventById(ServiceBusEventSource.ReceiveMessageStartEvent, e => e.Payload.Contains(sessionReceiver.Identifier));

                msg = await sessionReceiver.PeekAsync();
                _listener.SingleEventById(ServiceBusEventSource.CreateManagementLinkStartEvent, e => e.Payload.Contains(sessionReceiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.CreateManagementLinkCompleteEvent, e => e.Payload.Contains(sessionReceiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.PeekMessageStartEvent, e => e.Payload.Contains(sessionReceiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.PeekMessageCompleteEvent, e => e.Payload.Contains(sessionReceiver.Identifier));

                await receiver.DisposeAsync();
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeStartEvent, e => e.Payload.Contains(nameof(ServiceBusReceiver)) && e.Payload.Contains(receiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeCompleteEvent, e => e.Payload.Contains(nameof(ServiceBusReceiver)) && e.Payload.Contains(receiver.Identifier));

                await sessionReceiver.DisposeAsync();
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeStartEvent, e => e.Payload.Contains(nameof(ServiceBusSessionReceiver)) && e.Payload.Contains(sessionReceiver.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeCompleteEvent, e => e.Payload.Contains(nameof(ServiceBusSessionReceiver)) && e.Payload.Contains(sessionReceiver.Identifier));

                await sender.DisposeAsync();
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeStartEvent, e => e.Payload.Contains(nameof(ServiceBusSender)) && e.Payload.Contains(sender.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeCompleteEvent, e => e.Payload.Contains(nameof(ServiceBusSender)) && e.Payload.Contains(sender.Identifier));

                await client.DisposeAsync();
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeStartEvent, e => e.Payload.Contains(nameof(ServiceBusClient)) && e.Payload.Contains(client.Identifier));
                _listener.SingleEventById(ServiceBusEventSource.ClientDisposeCompleteEvent, e => e.Payload.Contains(nameof(ServiceBusClient)) && e.Payload.Contains(client.Identifier));
            }
        }

        [Test]
        public async Task LogsTransactionEvents()
        {
            await using (var scope = await ServiceBusScope.CreateWithQueue(enablePartitioning: false, enableSession: false))
            {
                var client = new ServiceBusClient(TestEnvironment.ServiceBusConnectionString);
                ServiceBusSender sender = client.CreateSender(scope.QueueName);


                ServiceBusMessage message = GetMessage();

                using (var ts = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
                {
                    await sender.SendAsync(message);
                    ts.Complete();
                }
                // Adding delay since transaction Commit/Rollback is an asynchronous operation.
                await Task.Delay(TimeSpan.FromSeconds(2));
                _listener.SingleEventById(ServiceBusEventSource.TransactionDeclaredEvent);
                _listener.SingleEventById(ServiceBusEventSource.TransactionDischargedEvent);
            };
        }
    }
}
