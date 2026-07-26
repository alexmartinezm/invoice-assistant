namespace Api.Domain;

/// <summary>
/// Demo user. There is no sign-up: the three seeded users exist to show identity
/// propagation, so anything beyond email, role and a hashed password would be noise.
/// </summary>
public sealed class User
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public required string Email { get; init; }

    public required string DisplayName { get; init; }

    public required Role Role { get; init; }

    public required string PasswordHash { get; init; }
}
