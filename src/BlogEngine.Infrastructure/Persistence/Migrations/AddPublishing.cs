using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlogEngine.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddPublishing : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "publishing");

        migrationBuilder.EnsureSchema(
            name: "engagement");

        migrationBuilder.CreateTable(
            name: "categories",
            schema: "publishing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                slug = table.Column<string>(type: "text", nullable: false),
                version = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_categories", x => x.id);
                table.UniqueConstraint("uq_categories_slug", x => x.slug);
                table.CheckConstraint("ck_categories_name", "name = btrim(name, U&'\\0009\\000a\\000b\\000c\\000d\\0020\\0085\\00a0\\1680\\2000\\2001\\2002\\2003\\2004\\2005\\2006\\2007\\2008\\2009\\200a\\2028\\2029\\202f\\205f\\3000') AND char_length(name) BETWEEN 1 AND 80");
                table.CheckConstraint("ck_categories_slug", "char_length(slug) BETWEEN 1 AND 120 AND slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
            });

        migrationBuilder.CreateTable(
            name: "publications",
            schema: "publishing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                author_id = table.Column<Guid>(type: "uuid", nullable: false),
                title = table.Column<string>(type: "text", nullable: false),
                slug = table.Column<string>(type: "text", nullable: false),
                markdown = table.Column<string>(type: "text", nullable: false),
                description = table.Column<string>(type: "text", nullable: true),
                cover_url = table.Column<string>(type: "text", nullable: true),
                state = table.Column<short>(type: "smallint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                first_published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                version = table.Column<Guid>(type: "uuid", nullable: false),
                like_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                view_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_publications", x => x.id);
                table.CheckConstraint("ck_publications_counters", "like_count >= 0 AND view_count >= 0");
                table.CheckConstraint("ck_publications_cover_url", "cover_url IS NULL OR (char_length(cover_url) BETWEEN 1 AND 2048 AND cover_url ~* '^https://')");
                table.CheckConstraint("ck_publications_description", "description IS NULL OR char_length(description) <= 500");
                table.CheckConstraint("ck_publications_markdown", "octet_length(markdown) <= 1048576");
                table.CheckConstraint("ck_publications_published", "state <> 1 OR (first_published_at IS NOT NULL AND char_length(btrim(markdown, U&'\\0009\\000a\\000b\\000c\\000d\\0020\\0085\\00a0\\1680\\2000\\2001\\2002\\2003\\2004\\2005\\2006\\2007\\2008\\2009\\200a\\2028\\2029\\202f\\205f\\3000')) > 0)");
                table.CheckConstraint("ck_publications_slug", "char_length(slug) BETWEEN 1 AND 120 AND slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                table.CheckConstraint("ck_publications_state", "state IN (0, 1)");
                table.CheckConstraint("ck_publications_title", "title = btrim(title, U&'\\0009\\000a\\000b\\000c\\000d\\0020\\0085\\00a0\\1680\\2000\\2001\\2002\\2003\\2004\\2005\\2006\\2007\\2008\\2009\\200a\\2028\\2029\\202f\\205f\\3000') AND char_length(title) BETWEEN 1 AND 200");
                table.ForeignKey(
                    name: "fk_publications_local_users",
                    column: x => x.author_id,
                    principalSchema: "accounts",
                    principalTable: "local_users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "tags",
            schema: "publishing",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "text", nullable: false, collation: "C"),
                slug = table.Column<string>(type: "text", nullable: false),
                version = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_tags", x => x.id);
                table.UniqueConstraint("uq_tags_slug", x => x.slug);
                table.CheckConstraint("ck_tags_name", "name = btrim(name, U&'\\0009\\000a\\000b\\000c\\000d\\0020\\0085\\00a0\\1680\\2000\\2001\\2002\\2003\\2004\\2005\\2006\\2007\\2008\\2009\\200a\\2028\\2029\\202f\\205f\\3000') AND char_length(name) BETWEEN 1 AND 80");
                table.CheckConstraint("ck_tags_slug", "char_length(slug) BETWEEN 1 AND 120 AND slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
            });

        migrationBuilder.CreateTable(
            name: "likes",
            schema: "engagement",
            columns: table => new
            {
                publication_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_likes", x => new { x.publication_id, x.user_id });
                table.ForeignKey(
                    name: "fk_likes_local_users",
                    column: x => x.user_id,
                    principalSchema: "accounts",
                    principalTable: "local_users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_likes_publications",
                    column: x => x.publication_id,
                    principalSchema: "publishing",
                    principalTable: "publications",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "publication_categories",
            schema: "publishing",
            columns: table => new
            {
                publication_id = table.Column<Guid>(type: "uuid", nullable: false),
                category_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_publication_categories", x => new { x.publication_id, x.category_id });
                table.ForeignKey(
                    name: "fk_publication_categories_categories",
                    column: x => x.category_id,
                    principalSchema: "publishing",
                    principalTable: "categories",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_publication_categories_publications",
                    column: x => x.publication_id,
                    principalSchema: "publishing",
                    principalTable: "publications",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "publication_tags",
            schema: "publishing",
            columns: table => new
            {
                publication_id = table.Column<Guid>(type: "uuid", nullable: false),
                tag_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_publication_tags", x => new { x.publication_id, x.tag_id });
                table.ForeignKey(
                    name: "fk_publication_tags_publications",
                    column: x => x.publication_id,
                    principalSchema: "publishing",
                    principalTable: "publications",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_publication_tags_tags",
                    column: x => x.tag_id,
                    principalSchema: "publishing",
                    principalTable: "tags",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_categories_name_id",
            schema: "publishing",
            table: "categories",
            columns: new[] { "name", "id" });

        migrationBuilder.CreateIndex(
            name: "ix_likes_user_id",
            schema: "engagement",
            table: "likes",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ix_publication_categories_reverse",
            schema: "publishing",
            table: "publication_categories",
            columns: new[] { "category_id", "publication_id" });

        migrationBuilder.CreateIndex(
            name: "ix_publication_tags_reverse",
            schema: "publishing",
            table: "publication_tags",
            columns: new[] { "tag_id", "publication_id" });

        migrationBuilder.CreateIndex(
            name: "ix_publications_popular",
            schema: "publishing",
            table: "publications",
            columns: new[] { "like_count", "first_published_at", "id" },
            descending: new bool[0],
            filter: "state = 1 AND deleted_at IS NULL");

        migrationBuilder.CreateIndex(
            name: "ix_publications_recent",
            schema: "publishing",
            table: "publications",
            columns: new[] { "first_published_at", "id" },
            descending: new bool[0],
            filter: "state = 1 AND deleted_at IS NULL");

        migrationBuilder.CreateIndex(
            name: "ix_publications_workspace_admin",
            schema: "publishing",
            table: "publications",
            columns: new[] { "updated_at", "id" },
            descending: new bool[0],
            filter: "deleted_at IS NULL");

        migrationBuilder.CreateIndex(
            name: "ix_publications_workspace_author",
            schema: "publishing",
            table: "publications",
            columns: new[] { "author_id", "updated_at", "id" },
            descending: new[] { false, true, true },
            filter: "deleted_at IS NULL");

        migrationBuilder.CreateIndex(
            name: "uq_publications_slug",
            schema: "publishing",
            table: "publications",
            column: "slug",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_tags_name_id",
            schema: "publishing",
            table: "tags",
            columns: new[] { "name", "id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "likes",
            schema: "engagement");

        migrationBuilder.DropTable(
            name: "publication_categories",
            schema: "publishing");

        migrationBuilder.DropTable(
            name: "publication_tags",
            schema: "publishing");

        migrationBuilder.DropTable(
            name: "categories",
            schema: "publishing");

        migrationBuilder.DropTable(
            name: "publications",
            schema: "publishing");

        migrationBuilder.DropTable(
            name: "tags",
            schema: "publishing");
    }
}
