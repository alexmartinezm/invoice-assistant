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
