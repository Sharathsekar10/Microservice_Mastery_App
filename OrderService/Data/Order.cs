using System.ComponentModel.DataAnnotations;

namespace OrderService.Data
{
    // Deliberately thin. OrderFlow's domain stays minimal by design - this entity's only
    // job is to give us something durable to write in the SAME transaction as the outbox
    // event, so we can demonstrate the pattern. It is NOT a real order model (no status,
    // no line items, no pricing) - that would be scope creep this project explicitly avoids.
    public class Order
    {
        // Guid, not an int identity column. Deliberate: identity/auto-increment behavior
        // is one of the places SQLite and SQL Server genuinely differ under the hood.
        // A client-generated Guid sidesteps that entirely - same code, same behavior,
        // on either provider. One less thing to worry about when you migrate later.
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();

        public int ProductId { get; set; }

        public int Quantity { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
