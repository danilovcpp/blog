using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlogEngine.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddDiscussions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "discussions");

        migrationBuilder.CreateTable(
            name: "comments",
            schema: "discussions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                publication_id = table.Column<Guid>(type: "uuid", nullable: false),
                author_id = table.Column<Guid>(type: "uuid", nullable: false),
                parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                depth = table.Column<short>(type: "smallint", nullable: false),
                state = table.Column<short>(type: "smallint", nullable: false),
                text = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                version = table.Column<Guid>(type: "uuid", nullable: false),
                visible_descendant_count = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_comments", x => x.id);
                table.UniqueConstraint("uq_comments_publication_id", x => new { x.publication_id, x.id });
                table.CheckConstraint("ck_comments_count", "visible_descendant_count >= 0");
                table.CheckConstraint("ck_comments_depth", "depth BETWEEN 1 AND 5");
                table.CheckConstraint("ck_comments_root", "(parent_id IS NULL) = (depth = 1)");
                table.CheckConstraint("ck_comments_self_parent", "parent_id IS NULL OR parent_id <> id");
                table.CheckConstraint("ck_comments_state", "state IN (0,1,2)");
                table.CheckConstraint("ck_comments_text", "(state = 2 AND text IS NULL) OR (state IN (0,1) AND text IS NOT NULL AND text = btrim(text, U&'\\0009\\000a\\000b\\000c\\000d\\0020\\0085\\00a0\\1680\\2000\\2001\\2002\\2003\\2004\\2005\\2006\\2007\\2008\\2009\\200a\\2028\\2029\\202f\\205f\\3000') AND char_length(text) BETWEEN 1 AND 5000)");
                table.ForeignKey(
                    name: "fk_comments_author",
                    column: x => x.author_id,
                    principalSchema: "accounts",
                    principalTable: "local_users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_comments_parent",
                    columns: x => new { x.publication_id, x.parent_id },
                    principalSchema: "discussions",
                    principalTable: "comments",
                    principalColumns: new[] { "publication_id", "id" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_comments_publication",
                    column: x => x.publication_id,
                    principalSchema: "publishing",
                    principalTable: "publications",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_comments_author",
            schema: "discussions",
            table: "comments",
            column: "author_id");

        migrationBuilder.CreateIndex(
            name: "ix_comments_moderation",
            schema: "discussions",
            table: "comments",
            columns: new[] { "created_at", "id" },
            descending: new bool[0]);

        migrationBuilder.CreateIndex(
            name: "ix_comments_parent",
            schema: "discussions",
            table: "comments",
            columns: new[] { "publication_id", "parent_id" });

        migrationBuilder.CreateIndex(
            name: "ix_comments_public_parent",
            schema: "discussions",
            table: "comments",
            columns: new[] { "publication_id", "parent_id", "created_at", "id" },
            filter: "state = 0 OR visible_descendant_count > 0");

        migrationBuilder.CreateIndex(
            name: "ix_comments_publication_state",
            schema: "discussions",
            table: "comments",
            columns: new[] { "publication_id", "state", "created_at", "id" },
            descending: new[] { false, false, true, true });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "comments",
            schema: "discussions");
    }
}
