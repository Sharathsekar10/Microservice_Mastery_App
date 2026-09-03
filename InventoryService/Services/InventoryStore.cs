namespace InventoryService.Services
{
    public enum ReservationOutcome
    {
        Reserved,
        InsufficientStock,
        ProductNotFound
    }

    // Day 10 (Saga hands-on): this morning's race-condition fix, made real.
    //
    // Registered as a SINGLETON in Program.cs - deliberately NOT a field on the
    // controller. Controllers are activated fresh per HTTP request by default, so a
    // mutable field initialized inline on a controller (as this was before today)
    // silently resets to its starting value on every single request. That was already
    // a live bug - GetProducts only ever "worked" because nothing ever decremented it,
    // so the reset was invisible. It would have broken immediately the moment we added
    // a real reservation, so it's fixed as part of this change, not a separate one.
    //
    // TryReserve is a single atomic operation: check-and-decrement happen inside one
    // lock, so two concurrent reservations for the last unit of the same product
    // cannot both succeed. This is the actual mechanism behind "row-level lock, only
    // one reservation at a time" from this morning's discussion - a real database
    // would use a row lock or a conditional UPDATE; an in-memory store uses a CLR
    // lock. Same principle, different medium.
    public class InventoryStore
    {
        private readonly Dictionary<int, int> _stock = new()
        {
            { 1, 5 },
            { 2, 10 }
        };

        private readonly object _lock = new();

        public ReservationOutcome TryReserve(int productId, int quantity)
        {
            lock (_lock)
            {
                if (!_stock.TryGetValue(productId, out var available))
                {
                    return ReservationOutcome.ProductNotFound;
                }

                if (available < quantity)
                {
                    return ReservationOutcome.InsufficientStock;
                }

                _stock[productId] = available - quantity;
                return ReservationOutcome.Reserved;
            }
        }

        public bool TryGetStock(int productId, out int quantity)
        {
            lock (_lock)
            {
                return _stock.TryGetValue(productId, out quantity);
            }
        }
    }
}
