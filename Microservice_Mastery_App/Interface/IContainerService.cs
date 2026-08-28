using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Microservice_Mastery_App.Interface
{
    public interface IContainerService
    {
        Task<BlobContainerClient> CreateContainerIfNotExistAsync();
    }
}
