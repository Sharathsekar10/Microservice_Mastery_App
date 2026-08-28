using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Reflection.Metadata.Ecma335;
using System.Text.Json;

namespace OrderService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OrderController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        public OrderController(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
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
                        return Ok(new {StatusCode = 200, Message = "Order Confirmed" });
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
