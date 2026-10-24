using System.Globalization;
using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Core.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogEngine.Infrastructure.Persistence.Accounts;

public sealed class RoleChangeAuditConfiguration : IEntityTypeConfiguration<RoleChangeAudit>
{
    public void Configure(EntityTypeBuilder<RoleChangeAudit> builder)
    {
        var whiteSpace = "U&'" + string.Concat(TextRules.WhiteSpace.Select(c => "\\" + ((int)c).ToString("x4", CultureInfo.InvariantCulture))) + "'";
        builder.ToTable("role_change_audit", "accounts", table =>
        {
            table.HasCheckConstraint("ck_role_change_audit_role", "role IN (1, 2, 3)");
            table.HasCheckConstraint("ck_role_change_audit_action", "action IN (1, 2)");
            table.HasCheckConstraint("ck_role_change_audit_actor", $"char_length(btrim(actor, {whiteSpace})) > 0");
            table.HasCheckConstraint("ck_role_change_audit_reason", $"reason = btrim(reason, {whiteSpace}) AND char_length(reason) BETWEEN 1 AND 512");
        });
        builder.HasKey(a => a.Id).HasName("pk_role_change_audit");
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.CommandCorrelationId).HasColumnName("command_correlation_id");
        builder.Property(a => a.TargetUserId).HasColumnName("target_user_id");
        builder.Property(a => a.Role).HasColumnName("role").HasConversion<short>();
        builder.Property(a => a.Action).HasColumnName("action").HasConversion<short>();
        builder.Property(a => a.Changed).HasColumnName("changed");
        builder.Property(a => a.Actor).HasColumnName("actor").IsRequired();
        builder.Property(a => a.Reason).HasColumnName("reason").IsRequired();
        builder.Property(a => a.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamp with time zone");
        builder.HasAlternateKey(a => a.CommandCorrelationId).HasName("uq_role_change_audit_correlation");
        builder.HasIndex(a => new { a.TargetUserId, a.OccurredAt, a.Id }).IsDescending(false, true, true).HasDatabaseName("ix_role_change_audit_target_time");
        builder.HasOne<LocalUser>().WithMany().HasForeignKey(a => a.TargetUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_role_change_audit_local_users");
    }
}
