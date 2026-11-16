using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Beachtro.Infrastructure.Persistence;

internal static class ModelBuilderHelpers
{
    /// <summary>Restrict-delete foreign key without navigation properties (history is never cascade-deleted).</summary>
    public static EntityTypeBuilder<TDependent> Fk<TDependent, TPrincipal>(
        this EntityTypeBuilder<TDependent> builder,
        Expression<Func<TDependent, object?>> foreignKey)
        where TDependent : class
        where TPrincipal : class
    {
        builder.HasOne<TPrincipal>().WithMany().HasForeignKey(foreignKey).OnDelete(DeleteBehavior.Restrict);
        return builder;
    }

    /// <summary>Named CHECK constraint: CK_{table}_{name}.</summary>
    public static TableBuilder<T> Check<T>(this TableBuilder<T> table, string name, string sql)
        where T : class
    {
        table.HasCheckConstraint($"CK_{table.Name}_{name}", sql);
        return table;
    }

    /// <summary>ISO business date text YYYY-MM-DD.</summary>
    public static string IsoDate(string column) =>
        $"\"{column}\" GLOB '[0-9][0-9][0-9][0-9]-[0-1][0-9]-[0-3][0-9]'";

    /// <summary>Lowercase hex SHA-256.</summary>
    public static string Sha256(string column) =>
        $"length(\"{column}\") = 64 AND \"{column}\" NOT GLOB '*[^0-9a-f]*'";

    public static string NonNegative(params string[] columns) =>
        string.Join(" AND ", columns.Select(c => $"\"{c}\" >= 0"));

    public static string NotBlank(string column) => $"length(trim(\"{column}\")) > 0";
}
