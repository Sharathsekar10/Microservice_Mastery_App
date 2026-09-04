using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using OrderService.Data;

namespace OrderService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OrderController : ControllerBase
    {
        private readonly OrderDbContext _dbContext;
        private readonly ILogger<OrderController> _logger;

        public OrderController(OrderDbContext dbContext, ILogger<OrderController> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        [HttpGet("health")]
        public async Task<IActionResult> GetHealth()
        {
            return Ok(new { StatusCode = 200, Message = "Order Service is healthy" });
        }

        // Day 10 (Saga): no synchronous call to InventoryService here anymore at all -
        // that was the Day 8 design. OrderService no longer knows or cares whether
        // InventoryService is up, slow, or fast right now. It commits its own local
        // transaction (Order + OutboxMessage, same SaveChangesAsync as Day 9) and
        // returns immediately. Whether the order actually succeeds is found out later,
        // asynchronously, via the saga's next steps.
        [HttpPost("order")]
        public async Task<IActionResult> CreateOrder(int productId, int quantity)
        {
            if (quantity <= 0)
            {
                return BadRequest(new { StatusCode = 400, Message = "Quantity must be greater than zero" });
            }

            try
            {
                var order = new Order
                {
                    ProductId = productId,
                    Quantity = quantity,
                    Status = "Pending"
                };

                // OrderId travels inside the event payload - this is the correlation key
                // that lets OrderService later match an incoming StockReserved/
                // ReservationFailed event back to the right Order row. Without it,
                // OrderService would have no way to know which pending order a given
                // Inventory response belongs to.
                var eventId = Guid.NewGuid();
                var orderCreatedEvent = new
                {
                    EventId = eventId.ToString(),
                    EventName = "OrderCreated",
                    OrderId = order.Id,
                    ProductId = productId,
                    Quantity = quantity,
                    CreatedAtUtc = order.CreatedAtUtc
                };

                var outboxMessage = new OutboxMessage
                {
                    Id = eventId,
                    EventType = "OrderCreated",
                    Payload = JsonSerializer.Serialize(orderCreatedEvent)
                };

                // One transaction, both rows land or neither does - same Day 9 guarantee,
                // just publishing a different event now.
                _dbContext.Orders.Add(order);
                _dbContext.OutboxMessages.Add(outboxMessage);
                await _dbContext.SaveChangesAsync();

                // 202 Accepted, not 200 - this is the honest status code. We have not
                // confirmed the order; we've accepted the REQUEST to place it. The
                // Location header points at where the caller can check the real outcome
                // once the saga completes.
                return AcceptedAtAction(nameof(GetOrderStatus), new { id = order.Id },
                    new { StatusCode = 202, OrderId = order.Id, Status = order.Status });
            }
            catch (Exception ex)
            {
                return Problem(detail: ex.Message, statusCode: 500);
            }
        }

        // Lets a client poll for the saga's outcome, since there is no second HTTP
        // response coming on the original request - the discussion from earlier today,
        // made real. Deliberately simple (no auth, no pagination) - this exists to
        // demonstrate the pattern, not to be a real order-tracking API.
        [HttpGet("order/{id}")]
        public async Task<IActionResult> GetOrderStatus(Guid id)
        {
            var order = await _dbContext.Orders.FindAsync(id);
            if (order is null)
            {
                return NotFound(new { StatusCode = 404, Message = "Order not found" });
            }

            return Ok(new { OrderId = order.Id, Status = order.Status, order.ProductId, order.Quantity });
        }
    }
}
