using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using OrderService.Messaging;

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
                else
                {
                    return NotFound(new { StatusCode = 404, Message = "Product not available" });
                }
            }
            catch (Exception ex)
            {
                return Problem(detail: ex.Message, statusCode: 500);
            }
        }
    }
}
