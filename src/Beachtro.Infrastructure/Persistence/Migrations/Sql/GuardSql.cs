namespace Beachtro.Infrastructure.Persistence.Migrations.Sql;

/// <summary>
/// Frozen trigger SQL shipped with a migration. Each .sql file holds one statement per line and
/// never changes after release; later guard changes go in new migrations with new files.
/// </summary>
public static class GuardSql
{
    public const string BaselineFile = "0001_baseline_guards.sql";

    public static IReadOnlyList<string> Load(string fileName)
    {
        var assembly = typeof(GuardSql).Assembly;
        using var stream = assembly.GetManifestResourceStream("Beachtro.Sql." + fileName)
            ?? throw new InvalidOperationException($"Embedded SQL resource {fileName} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal))
            .ToArray();
    }
}
