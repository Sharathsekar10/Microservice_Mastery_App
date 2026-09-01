namespace OrderService.Messaging
{
    public interface IOrderEventPublisher
    {
        Task PublishOrderConfirmedAsync(int productId, int quantity, CancellationToken cancellationToken = default);

        // Day 9 (Outbox): publishes an already-built payload under an already-fixed
        // eventId, supplied by the caller rather than generated here. This is what lets
        // the OutboxDispatcher redeliver the SAME logical event on retry - same eventId
        // every time - so NotificationService's idempotency store can actually recognize
        // a redelivery as a duplicate instead of a new message.
        Task PublishRawAsync(string eventId, string eventType, string payloadJson, CancellationToken cancellationToken = default);
    }
}
