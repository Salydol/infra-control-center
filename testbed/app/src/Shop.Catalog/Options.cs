namespace Shop.Catalog;

public sealed class CatalogOptions
{
    public int MaxPageSize { get; set; } = 50;
    public int RestockIntervalSeconds { get; set; } = 30;
    public int RestockThreshold { get; set; } = 50;
    public int RestockTo { get; set; } = 1000;
}

public sealed class CacheOptions
{
    public bool Enabled { get; set; } = true;
    public int TtlSeconds { get; set; } = 60;
}
