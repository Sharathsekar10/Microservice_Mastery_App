using Azure.Messaging.ServiceBus;

namespace OrderService.Messaging
{
    // Publishes a fact ("OrderConfirmed") to the order-events topic.
    // Deliberately does NOT know or care who - if anyone - is subscribed. See Day 7, Segment 2-3.
    public class OrderEventPublisher : IOrderEventPublisher, IAsyncDisposable
    {
        private readonly ServiceBusClient _client;
        private readonly ServiceBusSender _sender;
        private readonly ILogger<OrderEventPublisher> _logger;

        public OrderEventPublisher(IConfiguration configuration, ILogger<OrderEventPublisher> logger)
        {
            _logger = logger;
            var connectionString = configuration["ServiceBus:ConnectionString"]
                ?? throw new InvalidOperationException("ServiceBus:ConnectionString is not configured. Set it via the ServiceBus__ConnectionString environment variable.");
            var topicName = configuration["ServiceBus:TopicName"] ?? "order-events";

            _client = new ServiceBusClient(connectionString);
            _sender = _client.CreateSender(topicName);
        }

        public async Task PublishRawAsync(string eventId, string eventType, string payloadJson, CancellationToken cancellationToken = default)
        {
            var message = new ServiceBusMessage(payloadJson)
            {
                MessageId = eventId, // fixed by the caller (the OutboxMessage row's own Id) -
                                      // identical across every redelivery attempt, which is
                                      // the whole point (see Day 9 outbox discussion).
                Subject = eventType,
                ContentType = "application/json"
            };

            await _sender.SendMessageAsync(message, cancellationToken);
            _logger.LogInformation("Published {EventType} event {EventId} from outbox", eventType, eventId);
        }

        public async ValueTask DisposeAsync()
        {
            await _sender.DisposeAsync();
            await _client.DisposeAsync();
        }
    }
}
