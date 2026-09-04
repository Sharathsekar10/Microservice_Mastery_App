using Microsoft.EntityFrameworkCore;
using OrderService.Data;

namespace OrderService.Messaging
{
    // Runs continuously, independent of the API request pipeline and independent of
    // whether OrderService just restarted or has been perfectly healthy for weeks
    // (Day 9, Gap 2 - the "Service Bus had a 90-second blip while the service kept
    // running fine" scenario). Its only job: find OutboxMessage rows nobody has
    // successfully delivered yet, atomically claim the right to send each one (Gap 2's
    // multi-replica concurrency answer), and publish them.
    public class OutboxDispatcher : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

        // If a claim is older than this and the row still isn't Sent, the claiming
        // dispatcher is presumed dead (crashed mid-publish) and another instance is
        // allowed to take over. Without this, a crash right after claiming would strand
        // that row claimed forever - a message stuck at zero deliveries, which breaks
        // the at-least-once guarantee the whole pattern is supposed to provide.
        private static readonly TimeSpan ClaimLease = TimeSpan.FromSeconds(30);

        private const int BatchSize = 10;

        // One id per dispatcher PROCESS (not per poll cycle). This is what ends up in
        // ClaimedBy - once OrderService scales to multiple replicas (Block G), this is
        // how you'd tell, just by reading the data, which replica claimed a given row.
        private readonly string _instanceId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOrderEventPublisher _publisher;
        private readonly ILogger<OutboxDispatcher> _logger;

        public OutboxDispatcher(
            IServiceScopeFactory scopeFactory,
            IOrderEventPublisher publisher,
            ILogger<OutboxDispatcher> logger)
        {
            _scopeFactory = scopeFactory;
            _publisher = publisher;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "OutboxDispatcher {InstanceId} starting, polling every {Interval}",
                _instanceId, PollInterval);

            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await DispatchOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    // A failed poll cycle (transient DB or Service Bus issue) must not
                    // kill this background service - it just tries again next tick.
                    // This loop existing at all, on its own timer, IS the fix for Gap 2.
                    _logger.LogError(ex,
                        "OutboxDispatcher {InstanceId} poll cycle failed, will retry next tick",
                        _instanceId);
                }
            }
        }

        private async Task DispatchOnceAsync(CancellationToken ct)
        {
            // BackgroundService is a singleton for the lifetime of the app, but
            // DbContext is scoped - it is NOT safe to hold one DbContext instance across
            // every poll cycle for the app's entire lifetime. A fresh scope per cycle is
            // the correct lifetime match.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

            var now = DateTime.UtcNow;
            var leaseExpiry = now - ClaimLease;

            var candidateIds = await db.OutboxMessages
                .Where(o => !o.Sent && (o.ClaimedBy == null || o.ClaimedAt < leaseExpiry))
                .OrderBy(o => o.CreatedAtUtc)
                .Take(BatchSize)
                .Select(o => o.Id)
                .ToListAsync(ct);

            foreach (var id in candidateIds)
            {
                // THE atomic claim. ExecuteUpdateAsync compiles to a single SQL
                // UPDATE ... WHERE ... statement - no entity is loaded into memory first.
                // If two dispatcher instances race for the same row, the DATABASE
                // guarantees only one UPDATE actually matches; there is no lock we have
                // to manage in our own code.
                var claimed = await db.OutboxMessages
                    .Where(o => o.Id == id && !o.Sent && (o.ClaimedBy == null || o.ClaimedAt < leaseExpiry))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(o => o.ClaimedBy, _instanceId)
                        .SetProperty(o => o.ClaimedAt, now), ct);

                if (claimed == 0)
                {
                    // Someone else won the race for this row this cycle. Not an error -
                    // exactly the "competing consumers" outcome we discussed.
                    continue;
                }

                var message = await db.OutboxMessages.FirstAsync(o => o.Id == id, ct);

                try
                {
                    await _publisher.PublishRawAsync(
                        message.Id.ToString(), message.EventType, message.Payload, ct);

                    await db.OutboxMessages
                        .Where(o => o.Id == id)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(o => o.Sent, true)
                            .SetProperty(o => o.SentAtUtc, DateTime.UtcNow), ct);

                    _logger.LogInformation(
                        "OutboxDispatcher {InstanceId} published {EventType} {MessageId}",
                        _instanceId, message.EventType, message.Id);
                }
                catch (Exception ex)
                {
                    // Publish failed. Deliberately NOT clearing ClaimedBy/ClaimedAt here -
                    // leaving them means this row simply becomes eligible again once the
                    // lease expires, the same recovery path a crash would take. One retry
                    // mechanism, not two.
                    _logger.LogWarning(ex,
                        "OutboxDispatcher {InstanceId} failed to publish {MessageId}, will retry after lease expiry",
                        _instanceId, message.Id);
                }
            }
        }
    }
}
