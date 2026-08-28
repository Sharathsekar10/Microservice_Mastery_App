using Microservice_Mastery_App.Interface;
using Microservice_Mastery_App.Service;
using Azure.Storage.Blobs;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();

    var accountName = configuration["AccountName"] != null ? configuration["AccountName"] 
        : throw new Exception("AccountName is not configured");

    var serviceUri = new Uri($"https://{accountName}.blob.core.windows.net");

    return new BlobServiceClient(serviceUri, new DefaultAzureCredential());
});

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();


builder.Services.AddTransient<IContainerService, ContainerService>();
builder.Services.AddTransient<IBlobServiceClient, InternalBlobServiceClient>();


var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
