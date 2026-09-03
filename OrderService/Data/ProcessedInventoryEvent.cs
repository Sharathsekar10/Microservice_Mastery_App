using System.ComponentModel.DataAnnotations;

namespace OrderService.Data
{
    // Day 10 (Saga): the Inbox pattern - the mirror image of OutboxMessage. EventId is
    // the PRIMARY KEY, which means the database itself is what enforces "have I already
    // handled this exact incoming event?" - a second INSERT with the same EventId fails
    // with a constraint violation rather than silently succeeding. That's deliberate:
    // the check-for-duplicate and the effect (updating Order.Status, writing the
    // OrderResult outbox row) happen in ONE SaveChangesAsync() transaction together, so
    // there's no gap between "check" and "act" for a second delivery to slip through -
    // same atomicity principle as the outbox's claim mechanism, applied to the inbound
    // side of the pipe.
    public class ProcessedInventoryEvent
    {
        [Key]
        public required string EventId { get; set; }

        public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
