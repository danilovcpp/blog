using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlogEngine.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialAccounts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "accounts");

        migrationBuilder.CreateTable(
            name: "local_users",
            schema: "accounts",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                issuer = table.Column<string>(type: "text", nullable: false),
                subject = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_local_users", x => x.id);
                table.UniqueConstraint("uq_local_users_issuer_subject", x => new { x.issuer, x.subject });
                table.CheckConstraint("ck_local_users_issuer", "char_length(issuer) > 0");
            });

        migrationBuilder.CreateTable(
            name: "role_assignments",
            schema: "accounts",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<short>(type: "smallint", nullable: false),
                granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_role_assignments", x => new { x.user_id, x.role });
                table.CheckConstraint("ck_role_assignments_role", "role IN (1, 2, 3)");
                table.ForeignKey(
                    name: "fk_role_assignments_local_users",
                    column: x => x.user_id,
                    principalSchema: "accounts",
                    principalTable: "local_users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "role_assignments",
            schema: "accounts");

        migrationBuilder.DropTable(
            name: "local_users",
            schema: "accounts");
    }
}
