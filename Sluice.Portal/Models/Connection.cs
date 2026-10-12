namespace Sluice.Portal.Models;

public sealed record Connection
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ConnectionRole Role { get; init; }
    public required string Type { get; init; }
    public Dictionary<string, string?> Config { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}