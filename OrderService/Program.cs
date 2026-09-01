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
    // Bounded attempts, exponential backoff, WITH jitter so multiple
    // OrderService replicas retrying at once don't land in lockstep.
    // ShouldHandle deliberately does NOT match a real 404 - that's a
    // legitimate business answer from Inventory ("no such product"),
    // not a transient failure worth retrying.
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
    // Trips once at least 8 calls have been observed in a 10-second window
    // AND 50% or more of them failed. While open, calls fail immediately
    // (BrokenCircuitException) with NO network attempt at all - that's the
    // efficiency gain over retry+timeout alone. After 15s it moves to
    // Half-Open and lets a trial call through before fully resuming.
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
    // Placeholder value: we don't have real p99 latency data for
    // InventoryService yet (no monitoring wired up in this learning
    // project). 2 seconds is a reasoned starting point given normal
    // latency is ~50ms, NOT a final answer - in real production this
    // gets set from actual measured p99, then revisited with real data.
    pipelineBuilder.AddTimeout(TimeSpan.FromSeconds(2));
});

// Day 9: the Order + Outbox durable store. SQLite for now - deliberately thin,
// zero external infra for a learning project (see Day 9 theory: production would
// target Azure SQL Database instead, via a provider swap only - the DbContext,
// entities, and every LINQ query here stay identical either way).
builder.Services.AddDbContext<OrderDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("OrderDb")));

// Publishes OrderConfirmed events. Singleton because ServiceBusClient/Sender are meant to be
// long-lived and reused across requests, not created per-request.
builder.Services.AddSingleton<IOrderEventPublisher, OrderEventPublisher>();

// Day 9: the Outbox Dispatcher. Runs for the lifetime of the app, on its own timer,
// completely independent of any HTTP request - this is what catches "Service Bus had
// a transient blip while OrderService stayed healthy" (Gap 2), not just crash recovery.
builder.Services.AddHostedService<OutboxDispatcher>();

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
