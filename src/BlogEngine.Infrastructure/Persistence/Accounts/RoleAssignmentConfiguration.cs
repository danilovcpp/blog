using BlogEngine.Core.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogEngine.Infrastructure.Persistence.Accounts;

public sealed class RoleAssignmentConfiguration : IEntityTypeConfiguration<RoleAssignment>
{
    public void Configure(EntityTypeBuilder<RoleAssignment> builder)
    {
        builder.ToTable("role_assignments", "accounts", table => table.HasCheckConstraint("ck_role_assignments_role", "role IN (1, 2, 3)"));
        builder.HasKey(role => new { role.UserId, role.Role }).HasName("pk_role_assignments");
        builder.Property(role => role.UserId).HasColumnName("user_id");
        builder.Property(role => role.Role).HasColumnName("role").HasConversion<short>();
        builder.Property(role => role.GrantedAt).HasColumnName("granted_at").HasColumnType("timestamp with time zone");
        builder.HasOne<LocalUser>().WithMany().HasForeignKey(role => role.UserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_role_assignments_local_users");
    }
}
