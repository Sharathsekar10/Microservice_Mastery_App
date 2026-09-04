using System.ComponentModel.DataAnnotations;

namespace OrderService.Data
{
    // This is the durable "intent to publish" record. It gets written in the SAME
    // SaveChangesAsync() transaction as the Order row - that shared transaction is the
    // entire mechanism that fixes the dual-write problem. Nothing here is clever; the
    // atomicity comes from EF Core/the database, not from this class.
    public class OutboxMessage
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        // "OrderConfirmed" today. Kept as a string, not an enum, so new event types don't
        // require a schema/migration change later - this table is meant to carry ANY
        // future event type OrderService might need to publish, not just this one.
        public required string EventType { get; set; }

        // The FULL serialized event body, not just a flag. This directly closes the gap
        // from our theory discussion: a bare "Sent = false" bool tells the dispatcher
        // THAT something wasn't sent, but not WHAT to send. Storing the actual payload
        // means the dispatcher never has to reconstruct the event from the Order row -
        // it just reads this column and publishes it verbatim, unchanged, no matter how
        // long it's been sitting here unsent.
        public required string Payload { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        // Flips to true only after Service Bus confirms the send. Kept as a persisted
        // flag (rather than deleting the row on success) so you can actually SEE outbox
        // history while we're testing today - a real production system would likely
        // archive/delete sent rows on a schedule to bound table growth, but that's a
        // later optimization, not something we need for demonstrating the pattern.
        public bool Sent { get; set; } = false;

        public DateTime? SentAtUtc { get; set; }

        // --- Claim-based dispatch (from the multi-replica concurrency discussion) ---
        // A dispatcher instance writes its own id here via an atomic conditional UPDATE
        // before it's allowed to publish this row. If two replicas race for the same row,
        // exactly one UPDATE actually matches and wins - the loser sees zero rows affected
        // and moves on to the next candidate instead of double-publishing.
        public string? ClaimedBy { get; set; }

        // Paired with a lease timeout in the dispatcher's query: if a claim is older than
        // the lease window and the row still isn't Sent, another replica is allowed to
        // reclaim it. Without this, a dispatcher that claims a row and then crashes mid-
        // publish would leave that row claimed forever - a message stuck at "zero times
        // delivered," which breaks the at-least-once guarantee we're relying on.
        public DateTime? ClaimedAt { get; set; }
    }
}
