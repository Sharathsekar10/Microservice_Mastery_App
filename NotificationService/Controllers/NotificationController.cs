using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NotificationService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class NotificationController : ControllerBase
    {
        [HttpGet("health")]
        public async Task<IActionResult> GetHealth()
        {
            return Ok(new { StatusCode = 200, Message = "Notification Service is healthy" });
        }
    }
}
