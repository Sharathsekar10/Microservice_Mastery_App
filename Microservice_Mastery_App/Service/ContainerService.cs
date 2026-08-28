using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microservice_Mastery_App.Interface;

namespace Microservice_Mastery_App.Service
{
    public class ContainerService : IContainerService
    {
        private readonly IConfiguration _config;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly ILogger _logger;

        public ContainerService(IConfiguration config, BlobServiceClient blobServiceClient,ILogger<ContainerService> logger)
        {
            _config = config;
            _blobServiceClient = blobServiceClient;
            _logger = logger;
        }
        public async Task<BlobContainerClient> CreateContainerIfNotExistAsync()
        {
            try
            {
                var containerName = _config.GetSection("ContainerName").Value;
                var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
                var container = await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
                _logger.LogInformation($"Blob container created or already exists. Container Name:{containerClient.Name}");
                return containerClient;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred while creating the blob container.");
                throw;
            }
           
        }
    }
}
