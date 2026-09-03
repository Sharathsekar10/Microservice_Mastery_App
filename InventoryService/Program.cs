using InventoryService.Messaging;
using InventoryService.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Day 10: singleton, shared, thread-safe stock store - see InventoryStore's own
// comments for why this replaced a per-request controller field.
builder.Services.AddSingleton<InventoryStore>();

// Day 10 (Saga): consumes OrderCreated, reserves stock, publishes
// StockReserved/ReservationFailed. Runs for the lifetime of the app, independent of
// any HTTP request - same shape as OrderService's OutboxDispatcher.
builder.Services.AddHostedService<InventoryEventConsumer>();

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
