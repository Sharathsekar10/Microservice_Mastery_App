using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace NotificationService.Messaging
{
    // The state machine you designed on paper in Day 7, Segment 5 - now real.
    public enum EventProcessingState
    {
        Processing,
        Completed
    }

    public class EventRecord
    {
        public required string EventId { get; init; }
        public EventProcessingState State { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    // Day 10 (Saga): consumes OrderResult events from the order-events topic /
    // notification-sub subscription - the saga's FINAL step. OrderResult is published
    // by OrderService only after it already knows the real outcome (StockReserved or
    // ReservationFailed from InventoryService), so this is genuinely the first moment
    // anyone tells the customer anything definitive - the original CreateOrder request
    // only ever got a 202.
    //
    // The in-memory dictionary below is a simplification for this learning exercise - in
    // production this idempotency store needs to survive a restart, so it belongs in a
    // real datastore (exactly what you designed in Segment 5, and exactly what Day 10
    // built as a real EF-backed Inbox table on OrderService's side). Kept in-memory here
    // so today's hands-on stays fast to run.
    //
    // NOTE (Day 7, Segment 6): the staleness-timeout reclaim below is a DELIBERATE
    // simplification. It does not fully close the race you found - a worker that is
    // merely slow (not dead) can still finish and double-complete after being
    // "reclaimed". That was triggered on purpose back on Day 7; still true today.
    public class OrderEventConsumer : BackgroundService
    {
        private static readonly ConcurrentDictionary<string, EventRecord> ProcessedEvents = new();
        private static readonly TimeSpan StaleProcessingThreshold = TimeSpan.FromSeconds(30);

        private readonly ServiceBusClient _client;
        private readonly ServiceBusProcessor _processor;
        private readonly ILogger<OrderEventConsumer> _logger;
        private readonly TimeSpan _simulatedWorkDelay;

        public OrderEventConsumer(IConfiguration configuration, ILogger<OrderEventConsumer> logger)
        {
            _logger = logger;
            var connectionString = configuration["ServiceBus:ConnectionString"]
                ?? throw new InvalidOperationException("ServiceBus:ConnectionString is not configured. Set it via the ServiceBus__ConnectionString environment variable.");
            var topicName = configuration["ServiceBus:TopicName"] ?? "order-events";
            var subscriptionName = configuration["ServiceBus:SubscriptionName"] ?? "notification-sub";

            _client = new ServiceBusClient(connectionString);
            _processor = _client.CreateProcessor(topicName, subscriptionName, new ServiceBusProcessorOptions
            {
                AutoCompleteMessages = false, // we decide explicitly when a message is "done"
                MaxConcurrentCalls = 1        // single-instance baseline - we'll raise this for the competing-consumers exercise
            });

            _processor.ProcessMessageAsync += HandleMessageAsync;
            _processor.ProcessErrorAsync += HandleErrorAsync;

            // Configurable so we can deliberately widen this past the 60s Service Bus lock
            // duration for the Day 7 "force a duplicate delivery" exercise, without hardcoding
            // an unrealistic delay into normal behavior. Override via
            // Notification__SimulatedWorkSeconds in .env / docker-compose - defaults to 0.5s.
            _simulatedWorkDelay = TimeSpan.FromSeconds(
                configuration.GetValue<double?>("Notification:SimulatedWorkSeconds") ?? 0.5);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _processor.StartProcessingAsync(stoppingToken);
            _logger.LogInformation("NotificationService is now listening for OrderResult events.");

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
            if (args.Message.Subject != "OrderResult")
            {
                _logger.LogWarning("Ignoring unexpected event type {Subject} on notification-sub", args.Message.Subject);
                await args.CompleteMessageAsync(args.Message);
                return;
            }

            var eventId = args.Message.MessageId;
            var now = DateTime.UtcNow;

            var record = ProcessedEvents.GetOrAdd(eventId, _ => new EventRecord
            {
                EventId = eventId,
                State = EventProcessingState.Processing,
                UpdatedAtUtc = now
            });

            var isNewClaim = record.UpdatedAtUtc == now;

            if (!isNewClaim)
            {
                if (record.State == EventProcessingState.Completed)
                {
                    _logger.LogWarning("Duplicate delivery for event {EventId} - already Completed. Skipping reprocessing, acknowledging message.", eventId);
                    await args.CompleteMessageAsync(args.Message);
                    return;
                }

                var age = now - record.UpdatedAtUtc;
                if (age < StaleProcessingThreshold)
                {
                    _logger.LogWarning(
                        "Event {EventId} is already being processed (claimed {AgeSeconds:F1}s ago, below the {ThresholdSeconds}s staleness threshold). Skipping.",
                        eventId, age.TotalSeconds, StaleProcessingThreshold.TotalSeconds);
                    await args.CompleteMessageAsync(args.Message);
                    return;
                }

                _logger.LogWarning("Event {EventId} was stuck in Processing for {AgeSeconds:F1}s - reclaiming it.", eventId, age.TotalSeconds);
                record.UpdatedAtUtc = now;
            }

            try
            {
                var body = JsonSerializer.Deserialize<JsonElement>(args.Message.Body.ToString());
                var orderId = body.GetProperty("OrderId").GetGuid();
                var status = body.GetProperty("Status").GetString();
                var reason = body.TryGetProperty("Reason", out var reasonProp) && reasonProp.ValueKind != JsonValueKind.Null
                    ? reasonProp.GetString()
                    : null;

                // Simulate doing the actual notification work (e.g. sending an email/SMS).
                _logger.LogInformation("Started processing event {EventId} - simulating {DelaySeconds}s of work...", eventId, _simulatedWorkDelay.TotalSeconds);
                await Task.Delay(_simulatedWorkDelay, args.CancellationToken);

                if (status == "Completed")
                {
                    _logger.LogInformation(
                        "Notification sent: order {OrderId} confirmed (event {EventId})", orderId, eventId);
                }
                else
                {
                    _logger.LogInformation(
                        "Notification sent: order {OrderId} could not be fulfilled - {Reason} (event {EventId})",
                        orderId, reason ?? "unknown reason", eventId);
                }

                record.State = EventProcessingState.Completed;
                record.UpdatedAtUtc = DateTime.UtcNow;

                await args.CompleteMessageAsync(args.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process OrderResult event {EventId}. Abandoning so the broker can redeliver.", eventId);
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
