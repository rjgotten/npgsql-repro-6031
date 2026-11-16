namespace Repro.Data;

public sealed class Translation
{
    public int Id { get; set; }

    public required string TenantId { get; set; }

    public required string Key { get; set; }

    public required string Value { get; set; }
}
