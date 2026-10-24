using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlogEngine.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddRoleChangeAudit : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "role_change_audit",
            schema: "accounts",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                command_correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                target_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<short>(type: "smallint", nullable: false),
                action = table.Column<short>(type: "smallint", nullable: false),
                changed = table.Column<bool>(type: "boolean", nullable: false),
                actor = table.Column<string>(type: "text", nullable: false),
                reason = table.Column<string>(type: "text", nullable: false),
                occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_role_change_audit", x => x.id);
                table.UniqueConstraint("uq_role_change_audit_correlation", x => x.command_correlation_id);
                table.CheckConstraint("ck_role_change_audit_action", "action IN (1, 2)");
                table.CheckConstraint("ck_role_change_audit_actor", "char_length(btrim(actor, U&'\\0009\\000a\\000b\\000c\\000d\\0020\\0085\\00a0\\1680\\2000\\2001\\2002\\2003\\2004\\2005\\2006\\2007\\2008\\2009\\200a\\2028\\2029\\202f\\205f\\3000')) > 0");
                table.CheckConstraint("ck_role_change_audit_reason", "reason = btrim(reason, U&'\\0009\\000a\\000b\\000c\\000d\\0020\\0085\\00a0\\1680\\2000\\2001\\2002\\2003\\2004\\2005\\2006\\2007\\2008\\2009\\200a\\2028\\2029\\202f\\205f\\3000') AND char_length(reason) BETWEEN 1 AND 512");
                table.CheckConstraint("ck_role_change_audit_role", "role IN (1, 2, 3)");
                table.ForeignKey(
                    name: "fk_role_change_audit_local_users",
                    column: x => x.target_user_id,
                    principalSchema: "accounts",
                    principalTable: "local_users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "ix_role_change_audit_target_time",
            schema: "accounts",
            table: "role_change_audit",
            columns: new[] { "target_user_id", "occurred_at", "id" },
            descending: new[] { false, true, true });

    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "role_change_audit",
            schema: "accounts");
    }
}
