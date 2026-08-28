namespace Microservice_Mastery_App.Interface
{
    public interface IBlobServiceClient
    {
        Task<string> GetContainerNameAsync();
    }
}
