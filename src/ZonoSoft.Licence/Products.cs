namespace ZonoSoft.Licence;

/// <summary>
/// ZonoSoft's software that licences are made for. Add a line here for each new app: its id goes into the licence
/// and its signed token ("prd"), so a key only works in the app it was made for.
/// </summary>
public static class Products
{
    public sealed record Product(string Id, string Name, string Covers);

    public const string Default = "shifenza";

    public static readonly IReadOnlyList<Product> All =
    [
        new("shifenza", "Shifenza", "Shifenza and the Shifenza Team app"),
    ];

    public static Product? Find(string? id) => All.FirstOrDefault(p => p.Id == (id ?? "").Trim().ToLowerInvariant());

    public static string NameOf(string? id) => Find(id)?.Name ?? id ?? "";
}
