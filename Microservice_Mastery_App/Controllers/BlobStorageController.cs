using Microservice_Mastery_App.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Microservice_Mastery_App.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BlobStorageController : ControllerBase
    {
        private readonly ILogger<BlobStorageController> _logger;
        private readonly IBlobServiceClient blogServiceClient;
        
        public BlobStorageController(IBlobServiceClient blogServiceClient, ILogger<BlobStorageController> logger)
        {
            this.blogServiceClient = blogServiceClient;
            _logger = logger;
        }


        [HttpGet]
        [Route("GetContainerName")]
        public async Task<IActionResult> GetContainerName()
        {
            try
            {
                var ContainerName = await blogServiceClient.GetContainerNameAsync();
                _logger.LogInformation($"Container Name retrieved successfully: {ContainerName}");
                return Ok(new { StatusCode = 200, Message = ContainerName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while getting the container name.");
                return BadRequest(new { StatusCode = 500, Message = $"Error: {ex.Message}" });
            }
        }
    }
}
