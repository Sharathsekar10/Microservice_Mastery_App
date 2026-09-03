using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using InventoryService.Services;

namespace InventoryService.Messaging
{
    // Day 10 (Saga): consumes OrderCreated from the order-events topic / inventory-sub
    // subscription, reserves stock, and publishes StockReserved or ReservationFailed
    // back onto the SAME topic - subscription filters (on the Subject property) route
    // each event type to whoever actually needs it. See the Day 10 notes on the Service
    // Bus topology this needs before it can run end to end.
    //
    // KNOWN, DELIBERATE GAP (flagged, not forgotten): this publishes directly via
    // ServiceBusSender, with no Outbox behind it. If this process crashes after
    // TryReserve() succeeds but before the publish call completes, the reservation is
    // real but nobody downstream ever finds out - the exact dual-write problem from
    // Day 9, reappeared in a different service. Left open deliberately rather than
    // building a second full Outbox+DB today; a strong candidate for a later day.
    public class InventoryEventConsumer : BackgroundService
    {
        private record Decision(Guid OutgoingEventId, ReservationOutcome Outcome);
        private record OrderCreatedEvent(Guid OrderId, int ProductId, int Quantity);

        // In-memory Inbox guard - the same deliberate simplification
        // NotificationService's consumer already uses (Day 7): a real production
        // version needs this to survive a restart, which means a durable table, not a
        // dictionary. Kept in-memory here so today's hands-on stays fast to build.
        // Caching the OUTCOME (not just "seen it") is what lets a duplicate delivery
        // republish the exact same decision instead of reserving twice.
        private static readonly ConcurrentDictionary<string, Decision> ProcessedEvents = new();

        private readonly ServiceBusClient _client;
        private readonly ServiceBusProcessor _processor;
        private readonly ServiceBusSender _sender;
        private readonly InventoryStore _store;
        private readonly ILogger<InventoryEventConsumer> _logger;

        public InventoryEventConsumer(IConfiguration configuration, InventoryStore store, ILogger<InventoryEventConsumer> logger)
        {
            _store = store;
            _logger = logger;

            var connectionString = configuration["ServiceBus:ConnectionString"]
                ?? throw new InvalidOperationException("ServiceBus:ConnectionString is not configured. Set it via the ServiceBus__ConnectionString environment variable.");
            var topicName = configuration["ServiceBus:TopicName"] ?? "order-events";
            var subscriptionName = configuration["ServiceBus:SubscriptionName"] ?? "inventory-sub";

            _client = new ServiceBusClient(connectionString);
            _processor = _client.CreateProcessor(topicName, subscriptionName, new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 1
            });
            _sender = _client.CreateSender(topicName);

            _processor.ProcessMessageAsync += HandleMessageAsync;
            _processor.ProcessErrorAsync += HandleErrorAsync;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _processor.StartProcessingAsync(stoppingToken);
            _logger.LogInformation("InventoryService is now listening for OrderCreated events.");

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // expected during shutdown
            }
        }

        private async Task HandleMessageAsync(ProcessMessageEventArgs args)
        {
            if (args.Message.Subject != "OrderCreated")
            {
                // Defensive: shouldn't normally arrive here once the subscription's SQL
                // filter is set up, but acknowledging (not abandoning) an event we don't
                // understand is the right call - abandoning it just gets it redelivered
                // forever.
                _logger.LogWarning("Ignoring unexpected event type {Subject} on inventory-sub", args.Message.Subject);
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            var eventId = args.Message.MessageId;

            try
            {
                var order = ParseOrderCreated(args);

                if (ProcessedEvents.TryGetValue(eventId, out var cached))
                {
                    _logger.LogWarning(
                        "Duplicate delivery of OrderCreated {EventId} - reusing cached decision ({Outcome}) instead of reserving again.",
                        eventId, cached.Outcome);
                    await PublishOutcomeAsync(cached, order, args.CancellationToken);
                    await args.CompleteMessageAsync(args.Message);
                    return;
                }

                // The atomic reservation attempt - see InventoryStore.TryReserve.
                var outcome = _store.TryReserve(order.ProductId, order.Quantity);
                var decision = new Decision(Guid.NewGuid(), outcome);
                ProcessedEvents[eventId] = decision;

                _logger.LogInformation("OrderCreated {EventId} for order {OrderId} -> {Outcome}", eventId, order.OrderId, outcome);

                await PublishOutcomeAsync(decision, order, args.CancellationToken);
                await args.CompleteMessageAsync(args.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process OrderCreated {EventId}. Abandoning so the broker can redeliver.", eventId);
                await args.AbandonMessageAsync(args.Message);
            }
        }

        private static OrderCreatedEvent ParseOrderCreated(ProcessMessageEventArgs args)
        {
            var body = JsonSerializer.Deserialize<JsonElement>(args.Message.Body.ToString());
            return new OrderCreatedEvent(
                body.GetProperty("OrderId").GetGuid(),
                body.GetProperty("ProductId").GetInt32(),
                body.GetProperty("Quantity").GetInt32());
        }

        private async Task PublishOutcomeAsync(Decision decision, OrderCreatedEvent order, CancellationToken ct)
        {
            var (eventType, payload) = decision.Outcome switch
            {
                ReservationOutcome.Reserved => ("StockReserved", (object)new
                {
                    EventId = decision.OutgoingEventId.ToString(),
                    EventName = "StockReserved",
                    OrderId = order.OrderId,
                    order.ProductId,
                    order.Quantity,
                    ReservedAtUtc = DateTime.UtcNow
                }),
                _ => ("ReservationFailed", (object)new
                {
                    EventId = decision.OutgoingEventId.ToString(),
                    EventName = "ReservationFailed",
                    OrderId = order.OrderId,
                    order.ProductId,
                    order.Quantity,
                    Reason = decision.Outcome.ToString(), // "InsufficientStock" or "ProductNotFound"
                    FailedAtUtc = DateTime.UtcNow
                })
            };

            var message = new ServiceBusMessage(JsonSerializer.Serialize(payload))
            {
                // Stable across every republish of THIS decision (duplicate inbound
                // delivery, or a future retry) - same identity-for-redelivery principle
                // as OrderService's own OutboxMessage.Id from Day 9.
                MessageId = decision.OutgoingEventId.ToString(),
                Subject = eventType,
                ContentType = "application/json"
            };

            await _sender.SendMessageAsync(message, ct);
            _logger.LogInformation("Published {EventType} {EventId} for order {OrderId}", eventType, decision.OutgoingEventId, order.OrderId);
        }

        private Task HandleErrorAsync(ProcessErrorEventArgs args)
        {
            _logger.LogError(args.Exception, "Service Bus processor error in {ErrorSource}", args.ErrorSource);
            return Task.CompletedTask;
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
            await _sender.DisposeAsync();
            await _client.DisposeAsync();
            await base.StopAsync(cancellationToken);
        }
    }
}
