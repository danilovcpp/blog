using BlogEngine.Core.Accounts.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogEngine.Infrastructure.Persistence.Accounts;

public sealed class LocalUserConfiguration : IEntityTypeConfiguration<LocalUser>
{
    public void Configure(EntityTypeBuilder<LocalUser> builder)
    {
        builder.ToTable("local_users", "accounts", table => table.HasCheckConstraint("ck_local_users_issuer", "char_length(issuer) > 0"));
        builder.HasKey(user => user.Id).HasName("pk_local_users");
        builder.Property(user => user.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(user => user.Issuer).HasColumnName("issuer").IsRequired();
        builder.Property(user => user.Subject).HasColumnName("subject").IsRequired();
        builder.Property(user => user.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.HasAlternateKey(user => new { user.Issuer, user.Subject }).HasName("uq_local_users_issuer_subject");
    }
}
