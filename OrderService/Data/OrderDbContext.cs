using Microsoft.EntityFrameworkCore;

namespace OrderService.Data
{
    public class OrderDbContext : DbContext
    {
        public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options)
        {
        }

        public DbSet<Order> Orders => Set<Order>();

        public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // The dispatcher's poll query filters on Sent (and will filter on ClaimedAt
            // once we write it). Without this index, that query does a full table scan
            // on every poll cycle - fine at today's toy scale, but the kind of thing
            // that quietly becomes a production problem once the outbox table has
            // millions of historical rows in it. Cheap to add now, easy to forget later.
            modelBuilder.Entity<OutboxMessage>()
                .HasIndex(o => o.Sent);
        }
    }
}
