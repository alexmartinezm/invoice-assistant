namespace Api.Domain;

/// <summary>
/// Ordered from least to most privileged so that rules can express "at least this role"
/// with a simple comparison. The policy engine (F2) relies on this ordering.
/// </summary>
public enum Role
{
    Viewer = 0,
    Accountant = 1,
    Admin = 2,
}
