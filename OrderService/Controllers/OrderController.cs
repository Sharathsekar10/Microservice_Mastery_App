using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text.Json;
using OrderService.Data;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace OrderService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OrderController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly OrderDbContext _dbContext;
        private readonly ILogger<OrderController> _logger;

        public OrderController(IHttpClientFactory httpClientFactory, OrderDbContext dbContext, ILogger<OrderController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _dbContext = dbContext;
            _logger = logger;
        }

        [HttpGet("health")]
        public async Task<IActionResult> GetHealth()
        {
            return Ok(new { StatusCode = 200, Message = "Order Service is healthy" });
        }

        [HttpPost("order")]
        public async Task<IActionResult> CreateOrder(int productId, int quantity)
        {
            try
            {
                var requestUri = $"products?productId={productId}";
                var GetProductResponse = await _httpClientFactory.CreateClient("InventoryService").GetAsync(requestUri);

                if (GetProductResponse.IsSuccessStatusCode)
                {
                    JsonElement product = await GetProductResponse.Content.ReadFromJsonAsync<JsonElement>();

                    if (product.TryGetProperty("stock", out JsonElement stockProp) && stockProp.GetInt32() >= quantity)
                    {
                        // Day 9 (Outbox pattern): no direct call to Service Bus here at all
                        // anymore. The order and the "intent to publish" are written together,
                        // in ONE SaveChangesAsync() - one transaction, both rows land or neither
                        // does. The OutboxDispatcher (running independently, on its own timer)
                        // is responsible for actually getting the event to Service Bus - whether
                        // that happens in the next 3 seconds or after this process crashes and
                        // restarts makes no difference to correctness. This IS the fix for the
                        // dual-write problem this whole session has been about.
                        var eventId = Guid.NewGuid();
                        var orderConfirmedEvent = new
                        {
                            EventId = eventId.ToString(),
                            EventName = "OrderConfirmed",
                            ProductId = productId,
                            Quantity = quantity,
                            ConfirmedAtUtc = DateTime.UtcNow
                        };

                        var order = new Order
                        {
                            ProductId = productId,
                            Quantity = quantity
                        };

                        var outboxMessage = new OutboxMessage
                        {
                            // SAME id used as the payload's EventId, and later as the Service
                            // Bus MessageId when the dispatcher sends it - one fixed identity,
                            // unchanged across every redelivery attempt, which is what lets
                            // NotificationService's idempotency store recognize a retry as a
                            // retry instead of a new event.
                            Id = eventId,
                            EventType = "OrderConfirmed",
                            Payload = JsonSerializer.Serialize(orderConfirmedEvent)
                        };

                        _dbContext.Orders.Add(order);
                        _dbContext.OutboxMessages.Add(outboxMessage);
                        await _dbContext.SaveChangesAsync();

                        return Ok(new { StatusCode = 200, Message = "Order Confirmed" });
                    }
                    else
                    {
                        return Conflict(new { StatusCode = 409, Message = "Insufficient stock" });
                    }
                }
                else if (GetProductResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    // A REAL 404 from InventoryService - it explicitly said "no such product".
                    // This is a legitimate business answer, not a failure, which is exactly
                    // why the retry policy in Program.cs deliberately excludes 404 from its
                    // ShouldHandle predicate - retrying this would be pointless.
                    return NotFound(new { StatusCode = 404, Message = "Product not available" });
                }
                else
                {
                    // Retries were exhausted but InventoryService still returned a real,
                    // non-2xx response (e.g. persistent 5xx). Inventory is reachable but
                    // unhealthy - that is NOT the same fact as "product doesn't exist",
                    // which the old code incorrectly collapsed into a single 404 branch.
                    _logger.LogWarning(
                        "InventoryService returned {StatusCode} (after retries) for product {ProductId}",
                        (int)GetProductResponse.StatusCode, productId);
                    return StatusCode(503, new { StatusCode = 503, Message = "Inventory service unavailable, please try again shortly" });
                }
            }
            catch (BrokenCircuitException)
            {
                // Circuit is OPEN - Polly failed this call immediately, with no network
                // attempt at all (Day 8, Segment 3). Inventory is known-unhealthy right now.
                _logger.LogWarning("InventoryService circuit breaker is OPEN - failing fast for product {ProductId}", productId);
                return StatusCode(503, new { StatusCode = 503, Message = "Inventory service unavailable, please try again shortly" });
            }
            catch (TimeoutRejectedException)
            {
                // Every retry attempt individually timed out (Day 8, Segment 2) and
                // retries were exhausted. Inventory was reached (probably) but never
                // responded in time - this is the 504 case per the Day 5 convention.
                _logger.LogWarning("InventoryService call timed out (after retries) for product {ProductId}", productId);
                return StatusCode(504, new { StatusCode = 504, Message = "Inventory service timed out, please try again" });
            }
            catch (Exception ex)
            {
                return Problem(detail: ex.Message, statusCode: 500);
            }
        }
    }
}
