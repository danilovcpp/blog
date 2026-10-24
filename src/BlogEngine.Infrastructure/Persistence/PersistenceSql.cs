using System.Globalization;
using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogEngine.Infrastructure.Persistence;

// Shared SQL representation of the scalar trim and physical column naming conventions.
internal static class PersistenceSql
{
    public static string Trim(string column) => $"btrim({column}, U&'{string.Concat(TextRules.WhiteSpace.Select(c => "\\" + ((int)c).ToString("x4", CultureInfo.InvariantCulture)))}')";
    public static void Columns<T>(EntityTypeBuilder<T> builder) where T : class
    {
        foreach (var property in builder.Metadata.GetProperties())
        {
            var name = string.Concat(property.Name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
            property.SetColumnName(name);
        }
    }
}
