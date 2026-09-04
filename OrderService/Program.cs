using OrderService.Data;
using OrderService.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;
using Polly.CircuitBreaker;
using Polly.Timeout;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Day 10 NOTE: as of the Saga rewrite, OrderController.CreateOrder no longer calls
// InventoryService synchronously at all - that call, and everything below (retry +
// circuit breaker + timeout), is currently UNUSED by any endpoint. Left in place
// deliberately rather than deleted: it's real, correct Day 8 resilience config, and
// exactly the kind of thing a future synchronous read (e.g. a live "check current
// stock" UI call, which is NOT part of the saga) would reuse as-is. Flagging this
// explicitly so it doesn't look like an oversight - dead code that isn't explained
// is indistinguishable from a bug.
builder.Services.AddHttpClient("InventoryService", client =>
{
    client.BaseAddress = new Uri("http://inventoryservice:8080/api/Inventory/");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    // NOTE: no client.Timeout here anymore. That was one blunt, ungoverned
    // 5-second cutoff for the whole call, picked by feel (Day 8, Segment 2).
    // The resilience pipeline below replaces it with a per-ATTEMPT timeout,
    // combined with retry + circuit breaker, so each concern is explicit
    // and independently tunable.
})
.AddResilienceHandler("InventoryServicePipeline", pipelineBuilder =>
{
    // Pipeline order matters: Retry (outermost) wraps CircuitBreaker wraps
    // Timeout (innermost). Each retry attempt gets its own fresh timeout,
    // and the circuit breaker observes the outcome of every individual
    // attempt (so it can trip based on real attempt-level failures).

    // --- Retry (Day 8, Segment 1) ---
    pipelineBuilder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
    {
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        Delay = TimeSpan.FromMilliseconds(500),
        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .HandleResult(response => (int)response.StatusCode >= 500)
    });

    // --- Circuit Breaker (Day 8, Segment 3) ---
    pipelineBuilder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
    {
        FailureRatio = 0.5,
        MinimumThroughput = 8,
        SamplingDuration = TimeSpan.FromSeconds(10),
        BreakDuration = TimeSpan.FromSeconds(15),
        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
            .Handle<HttpRequestException>()
            .Handle<TimeoutRejectedException>()
            .HandleResult(response => (int)response.StatusCode >= 500)
    });

    // --- Timeout (Day 8, Segment 2) ---
    pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(2));
});

// Day 9: the Order + Outbox durable store. SQLite for now - deliberately thin,
// zero external infra for a learning project (production would target Azure SQL
// Database instead, via a provider swap only).
builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("OrderDb")));

// Publishes events (OrderConfirmed originally, OrderCreated/OrderResult as of Day 10).
// Singleton because ServiceBusClient/Sender are meant to be long-lived and reused
// across requests, not created per-request.
builder.Services.AddSingleton<IOrderEventPublisher, OrderEventPublisher>();

// Day 9: the Outbox Dispatcher. Runs for the lifetime of the app, on its own timer,
// completely independent of any HTTP request.
builder.Services.AddHostedService<OutboxDispatcher>();

// Day 10 (Saga): consumes StockReserved/ReservationFailed and drives the Order's
// state machine forward - Pending -> Completed or Pending -> Failed.
builder.Services.AddHostedService<InventoryResultConsumer>();

var app = builder.Build();

// Day 9: apply pending EF Core migrations on startup. Deliberate, narrow trade-off -
// safe ONLY because exactly one OrderService instance ever runs in this docker-compose
// setup; becomes a separate one-time migration step (Job/init container) once this
// scales to multiple replicas in Kubernetes (Block G).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
