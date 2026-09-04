using Microsoft.AspNetCore.Mvc;
using InventoryService.Services;

namespace InventoryService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class InventoryController : ControllerBase
    {
        private readonly InventoryStore _store;

        public InventoryController(InventoryStore store)
        {
            _store = store;
        }

        [HttpGet("health")]
        public async Task<IActionResult> GetHealth()
        {
            return Ok(new { StatusCode = 200, Message = "Inventory Service is healthy" });
        }

        [HttpGet("products")]
        public Task<IActionResult> GetProducts(int productId)
        {
            try
            {
                IActionResult result = _store.TryGetStock(productId, out var quantity)
                    ? Ok(new { ProductId = productId, Stock = quantity, StatusCode = 200 })
                    : NotFound(new { StatusCode = 404, Message = "Product not found" });

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                return Task.FromResult<IActionResult>(Problem(detail: ex.Message, statusCode: 500));
            }
        }
    }
}
