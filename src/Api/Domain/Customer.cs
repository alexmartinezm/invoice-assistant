namespace Api.Domain;

public sealed class Customer
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public required string TaxId { get; set; }

    public required string Email { get; set; }
}
