using Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Api.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<PendingAction> PendingActions => Set<PendingAction>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<UsageRecord> UsageRecords => Set<UsageRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users");
            user.HasKey(u => u.Id);
            user.HasIndex(u => u.Email).IsUnique();
            user.Property(u => u.Email).HasMaxLength(200);
            user.Property(u => u.DisplayName).HasMaxLength(200);
            user.Property(u => u.PasswordHash).HasMaxLength(400);
            user.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<Customer>(customer =>
        {
            customer.ToTable("customers");
            customer.HasKey(c => c.Id);
            customer.HasIndex(c => c.Name);
            customer.Property(c => c.Name).HasMaxLength(200);
            customer.Property(c => c.TaxId).HasMaxLength(50);
            customer.Property(c => c.Email).HasMaxLength(200);
        });

        modelBuilder.Entity<Invoice>(invoice =>
        {
            invoice.ToTable("invoices");
            invoice.HasKey(i => i.Id);
            invoice.HasIndex(i => i.Number).IsUnique();
            invoice.Property(i => i.Number).HasMaxLength(20);
            invoice.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            invoice.Property(i => i.Subtotal).HasPrecision(18, 2);
            invoice.Property(i => i.VatRate).HasPrecision(5, 4);
            invoice.Property(i => i.VatAmount).HasPrecision(18, 2);
            invoice.Property(i => i.Total).HasPrecision(18, 2);

            invoice.HasOne(i => i.Customer)
                .WithMany()
                .HasForeignKey(i => i.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            // Lines are only reachable through the aggregate, so EF writes the backing field
            // directly and callers cannot bypass Invoice's recalculation.
            invoice.HasMany(i => i.Lines)
                .WithOne()
                .HasForeignKey(l => l.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);
            invoice.Metadata.FindNavigation(nameof(Invoice.Lines))!
                .SetPropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<InvoiceLine>(line =>
        {
            line.ToTable("invoice_lines");
            line.HasKey(l => l.Id);
            line.Property(l => l.Description).HasMaxLength(500);
            line.Property(l => l.Quantity).HasPrecision(18, 3);
            line.Property(l => l.UnitPrice).HasPrecision(18, 2);
            line.Property(l => l.Amount).HasPrecision(18, 2);
        });

        modelBuilder.Entity<Conversation>(conversation =>
        {
            conversation.ToTable("conversations");
            conversation.HasKey(c => c.Id);
            conversation.HasIndex(c => c.UserId);
            conversation.Property(c => c.SystemPromptHash).HasMaxLength(64);

            conversation.HasMany(c => c.Messages)
                .WithOne()
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Message>(message =>
        {
            message.ToTable("messages");
            message.HasKey(m => m.Id);
            message.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<AuditEvent>(audit =>
        {
            audit.ToTable("audit_events");
            audit.HasKey(a => a.Id);

            // The two questions asked of this table are "what happened in this conversation?" and
            // "did anything execute?", so both are indexed.
            audit.HasIndex(a => a.ConversationId);
            audit.HasIndex(a => a.Decision);
            audit.Property(a => a.Action).HasMaxLength(50);
            audit.Property(a => a.ToolName).HasMaxLength(50);
            audit.Property(a => a.Decision).HasConversion<string>().HasMaxLength(20);
            audit.Property(a => a.PayloadJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<PendingAction>(action =>
        {
            action.ToTable("pending_actions");
            action.HasKey(p => p.Id);
            action.HasIndex(p => p.UserId);
            action.Property(p => p.ToolName).HasMaxLength(50);
            action.Property(p => p.Summary).HasMaxLength(500);
            action.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            action.Property(p => p.ArgsJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<IdempotencyRecord>(record =>
        {
            record.ToTable("idempotency_keys");
            record.HasKey(r => r.Id);

            // Unique per user, so a duplicate is refused by the database rather than by whichever
            // request happened to check first.
            record.HasIndex(r => new { r.UserId, r.Key }).IsUnique();
            record.Property(r => r.Key).HasMaxLength(100);
            record.Property(r => r.Operation).HasMaxLength(200);
            record.Property(r => r.ResponseJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<UsageRecord>(usage =>
        {
            usage.ToTable("usage_records");
            usage.HasKey(u => u.Id);

            // The two questions asked of this table: "what did this conversation cost?" for the
            // usage endpoints, and "what has today cost?" for the daily kill switch.
            usage.HasIndex(u => u.ConversationId);
            usage.HasIndex(u => u.CreatedAt);
            usage.Property(u => u.Model).HasMaxLength(100);

            // Six decimal places: a single small call costs fractions of a cent, and rounding
            // those to two places would make the daily sum drift from what was actually spent.
            usage.Property(u => u.CostEur).HasPrecision(12, 6);
        });

        // Identifiers are created in the domain, never by the database. Saying so explicitly also
        // stops EF from reading a pre-set key on a brand new entity as "this row already exists",
        // which would turn the insert into an update of nothing.
        foreach (var key in modelBuilder.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.IsPrimaryKey() && property.ClrType == typeof(Guid)))
        {
            key.ValueGenerated = ValueGenerated.Never;
        }
    }
}
