namespace OrderService.Messaging
{
    public interface IOrderEventPublisher
    {
        Task PublishOrderConfirmedAsync(int productId, int quantity, CancellationToken cancellationToken = default);
    }
}
