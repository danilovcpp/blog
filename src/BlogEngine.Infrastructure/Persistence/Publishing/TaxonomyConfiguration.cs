using BlogEngine.Core.Publishing.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogEngine.Infrastructure.Persistence.Publishing;

public sealed class TaxonomyConfiguration : IEntityTypeConfiguration<Category>, IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Category> b) => ConfigureTable(b, "categories");
    public void Configure(EntityTypeBuilder<Tag> b) => ConfigureTable(b, "tags");
    private static void ConfigureTable<T>(EntityTypeBuilder<T> b, string tableName) where T : class
    {
        b.ToTable(tableName, "publishing", table =>
        {
            table.HasCheckConstraint("ck_" + tableName + "_name", $"name = {PublishingSql.Trim("name")} AND char_length(name) BETWEEN 1 AND 80");
            table.HasCheckConstraint("ck_" + tableName + "_slug", PublishingSql.SlugCheck);
        });
        b.HasKey("Id").HasName("pk_" + tableName);
        b.Property<Guid>("Id").ValueGeneratedNever();
        b.Property<string>("Name").IsRequired().UseCollation("C"); b.Property<string>("Slug").IsRequired();
        b.Property<Guid>("Version").IsConcurrencyToken().ValueGeneratedNever();
        b.HasAlternateKey("Slug").HasName("uq_" + tableName + "_slug");
        b.HasIndex("Name", "Id").HasDatabaseName("ix_" + tableName + "_name_id");
        PublishingSql.Columns(b);
    }
}
