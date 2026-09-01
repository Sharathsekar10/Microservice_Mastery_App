using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text.Json;
using OrderService.Messaging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace OrderService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OrderController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOrderEventPublisher _orderEventPublisher;
        private readonly ILogger<OrderController> _logger;

        public OrderController(IHttpClientFactory httpClientFactory, IOrderEventPublisher orderEventPublisher, ILogger<OrderController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _orderEventPublisher = orderEventPublisher;
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
                        // The order is confirmed regardless of what happens next with the event.
                        // NotificationService's fate must NOT determine whether the customer's
                        // order succeeded - that's the whole point of Day 7, Segment 1.
                        try
                        {
                            await _orderEventPublisher.PublishOrderConfirmedAsync(productId, quantity);
                        }
                        catch (Exception publishEx)
                        {
                            _logger.LogError(publishEx, "Failed to publish OrderConfirmed event for product {ProductId}. Order is still confirmed.", productId);
                        }

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
