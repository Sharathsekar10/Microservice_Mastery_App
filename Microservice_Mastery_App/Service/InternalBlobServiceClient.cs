using Microservice_Mastery_App.Interface;
using Azure.Storage.Blobs;

namespace Microservice_Mastery_App.Service
{
    public class InternalBlobServiceClient : IBlobServiceClient
    {
        private readonly ILogger _logger;
        private readonly IContainerService _containerService;
        public InternalBlobServiceClient(IContainerService containerService, ILogger<InternalBlobServiceClient> logger)
        {
            _containerService = containerService;
            _logger = logger;
        }
        public async Task<string> GetContainerNameAsync()
        {
            try
            {
                var blobContainer = await _containerService.CreateContainerIfNotExistAsync();

                return blobContainer?.Name != null ? blobContainer.Name : "No Container is available";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while fetching the container name.");
                throw;
            }
            
        }
    }
}
