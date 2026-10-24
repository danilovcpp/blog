using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlogEngine.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddViewDeduplication : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "view_deduplication_records",
            schema: "engagement",
            columns: table => new
            {
                publication_id = table.Column<Guid>(type: "uuid", nullable: false),
                visitor_kind = table.Column<short>(type: "smallint", nullable: false),
                visitor_key = table.Column<byte[]>(type: "bytea", nullable: false),
                last_counted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_view_deduplication_records", x => new { x.publication_id, x.visitor_kind, x.visitor_key });
                table.CheckConstraint("ck_view_deduplication_key", "octet_length(visitor_key) = 32");
                table.CheckConstraint("ck_view_deduplication_kind", "visitor_kind IN (1,2)");
                table.ForeignKey(
                    name: "fk_view_deduplication_publications",
                    column: x => x.publication_id,
                    principalSchema: "publishing",
                    principalTable: "publications",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_view_deduplication_expiry",
            schema: "engagement",
            table: "view_deduplication_records",
            columns: new[] { "last_counted_at", "publication_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "view_deduplication_records",
            schema: "engagement");
    }
}
