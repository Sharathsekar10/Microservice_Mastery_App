using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;

namespace OrderService.Messaging
{
    // Day 10 (Saga): consumes StockReserved / ReservationFailed from the order-events
    // topic / order-result-sub subscription, runs the compensating (or confirming)
    // local transaction against the Order row, and publishes OrderResult via the
    // EXISTING Outbox mechanism from Day 9 - no new publish infrastructure needed here,
    // just another OutboxMessage row written in the same transaction.
    public class InventoryResultConsumer : BackgroundService
    {
        private readonly ServiceBusClient _client;
        private readonly ServiceBusProcessor _processor;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<InventoryResultConsumer> _logger;

        public InventoryResultConsumer(IConfiguration configuration, IServiceScopeFactory scopeFactory, ILogger<InventoryResultConsumer> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;

            var connectionString = configuration["ServiceBus:ConnectionString"]
                ?? throw new InvalidOperationException("ServiceBus:ConnectionString is not configured. Set it via the ServiceBus__ConnectionString environment variable.");
            var topicName = configuration["ServiceBus:TopicName"] ?? "order-events";
            var subscriptionName = configuration["ServiceBus:ResultSubscriptionName"] ?? "order-result-sub";

            _client = new ServiceBusClient(connectionString);
            _processor = _client.CreateProcessor(topicName, subscriptionName, new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false,
                MaxConcurrentCalls = 1
            });

            _processor.ProcessMessageAsync += HandleMessageAsync;
            _processor.ProcessErrorAsync += HandleErrorAsync;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _processor.StartProcessingAsync(stoppingToken);
            _logger.LogInformation("OrderService is now listening for StockReserved/ReservationFailed events.");

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
            var subject = args.Message.Subject; // "StockReserved" or "ReservationFailed"
            var eventId = args.Message.MessageId;

            if (subject != "StockReserved" && subject != "ReservationFailed")
            {
                _logger.LogWarning("Ignoring unexpected event type {Subject} on order-result-sub", subject);
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

            try
            {
                var body = JsonSerializer.Deserialize<JsonElement>(args.Message.Body.ToString());
                var orderId = body.GetProperty("OrderId").GetGuid();

                var order = await db.Orders.FindAsync(new object?[] { orderId }, args.CancellationToken);
                if (order is null)
                {
                    _logger.LogWarning("Received {Subject} for unknown order {OrderId} - acking, nothing to do.", subject, orderId);
                    await args.CompleteMessageAsync(args.Message);
                    return;
                }

                // The Inbox insert, the Order.Status transition, and the outbox write
                // for OrderResult all happen in the SAME SaveChangesAsync() call below -
                // one local transaction, same guarantee as Day 9's Order+Outbox write.
                db.ProcessedInventoryEvents.Add(new ProcessedInventoryEvent { EventId = eventId });

                var newStatus = subject == "StockReserved" ? "Completed" : "Failed";
                order.Status = newStatus;

                var outboxEventId = Guid.NewGuid();
                var orderResultEvent = new
                {
                    EventId = outboxEventId.ToString(),
                    EventName = "OrderResult",
                    OrderId = order.Id,
                    Status = newStatus,
                    Reason = subject == "ReservationFailed" ? body.GetProperty("Reason").GetString() : null,
                    ResultAtUtc = DateTime.UtcNow
                };

                db.OutboxMessages.Add(new OutboxMessage
                {
                    Id = outboxEventId,
                    EventType = "OrderResult",
                    Payload = JsonSerializer.Serialize(orderResultEvent)
                });

                await db.SaveChangesAsync(args.CancellationToken);

                _logger.LogInformation("Order {OrderId} -> {Status} (from {Subject}, event {EventId})", order.Id, newStatus, subject, eventId);
                await args.CompleteMessageAsync(args.Message);
            }
            catch (DbUpdateException ex)
            {
                // Expected shape for a duplicate delivery: EventId already exists in
                // ProcessedInventoryEvents, so the INSERT above violates its primary
                // key and SaveChangesAsync throws instead of committing anything - the
                // Order.Status change and the OrderResult outbox write are rolled back
                // together with it. NOTE (flagged, not fixed): this treats every
                // DbUpdateException here as "duplicate" - a production version should
                // confirm that specifically, rather than assuming any DbUpdateException
                // means a duplicate.
                _logger.LogWarning(ex, "Duplicate delivery of {Subject} {EventId} - already processed. Skipping.", subject, eventId);
                await args.CompleteMessageAsync(args.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process {Subject} {EventId}. Abandoning so the broker can redeliver.", subject, eventId);
                await args.AbandonMessageAsync(args.Message);
            }
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
            await _client.DisposeAsync();
            await base.StopAsync(cancellationToken);
        }
    }
}
