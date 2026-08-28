using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace InventoryService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class InventoryController : ControllerBase
    {
        private readonly Dictionary<int, int> productQuantity = new Dictionary<int, int>()
        {
            {1,5},
            { 2,10 }
        };


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
                
                IActionResult result;
                if(productQuantity.TryGetValue(productId,out var quantites))
                {
                    result = Ok(new { ProductId = productId, Stock = quantites, StatusCode = 200 });
                }
                else
                {
                    result = NotFound(new { StatusCode = 404, Message = "Product not found" });
                }

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                return Task.FromResult<IActionResult>(Problem(detail: ex.Message, statusCode: 500));
            }
        }
    }
}
