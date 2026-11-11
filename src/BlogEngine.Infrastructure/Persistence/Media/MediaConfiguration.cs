using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Core.Media.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogEngine.Infrastructure.Persistence.Media;

public static class MediaConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var f = model.Entity<MediaFile>();
        f.ToTable("files", "media", t =>
        {
            t.HasCheckConstraint("ck_files_purpose", "purpose IN (0,1,2)");
            t.HasCheckConstraint("ck_files_size", "original_size > 0 AND original_size <= CASE purpose WHEN 0 THEN 52428800 WHEN 1 THEN 10485760 WHEN 2 THEN 5242880 ELSE 0 END");
            t.HasCheckConstraint("ck_files_name", $"original_name = {PersistenceSql.Trim("original_name")} AND char_length(original_name) BETWEEN 1 AND 255 AND original_name NOT IN ('.','..') AND original_name !~ '^[a-zA-Z]:' AND position('/' in original_name)=0 AND position(chr(92) in original_name)=0 AND original_name !~ '[[:cntrl:]]' AND original_name !~ U&'[\\0080-\\009f]'");
            t.HasCheckConstraint("ck_files_content_type", "declared_content_type ~ '^[a-z0-9!#$%&''*+.^_`|~-]+/[a-z0-9!#$%&''*+.^_`|~-]+$' AND (purpose=0 OR declared_content_type IN ('image/jpeg','image/png','image/webp')) AND (verified_content_type IS NULL OR (purpose=0 OR verified_content_type IN ('image/jpeg','image/png','image/webp')))");
            t.HasCheckConstraint("ck_files_state", "(state=0 AND ready_at IS NULL AND deleted_at IS NULL AND original_object_id IS NULL AND verified_content_type IS NULL) OR (state=1 AND ready_at IS NOT NULL AND deleted_at IS NULL AND verified_content_type IS NOT NULL AND ((purpose IN (0,1) AND original_object_id IS NOT NULL) OR (purpose=2 AND original_object_id IS NULL))) OR (state=2 AND deleted_at IS NOT NULL AND original_object_id IS NULL)");
        });
        f.HasKey(x => x.Id).HasName("pk_files");
        f.Property(x => x.Id).ValueGeneratedNever();
        f.Property(x => x.Purpose).HasConversion<short>(); f.Property(x => x.State).HasConversion<short>();
        f.Property(x => x.Version).IsConcurrencyToken().ValueGeneratedNever();
        f.HasOne<LocalUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_files_owner");
        f.HasIndex(x => new { x.Id, x.OwnerId, x.Purpose }).IsUnique().HasDatabaseName("uq_files_owner_purpose");
        f.HasIndex(x => new { x.Id, x.OwnerId, x.Purpose, x.State }).IsUnique().HasDatabaseName("uq_files_owner_purpose_state");
        f.HasIndex(x => new { x.OwnerId, x.CreatedAt, x.Id }).IsDescending(false, true, true).HasFilter("state=1 AND purpose IN (0,1)").HasDatabaseName("ix_files_library");
        Generated(f, "RequiredSourceKind", "0::smallint"); Generated(f, "RequiredSourceState", "1::smallint");
        Generated(f, "RequiredDisplayKind", "CASE WHEN state=1 AND purpose=1 THEN 1::smallint END");
        Generated(f, "RequiredAvatar256Kind", "CASE WHEN state=1 AND purpose=2 THEN 2::smallint END");
        Generated(f, "RequiredAvatar512Kind", "CASE WHEN state=1 AND purpose=2 THEN 3::smallint END");
        PersistenceSql.Columns(f);

        var u = model.Entity<MediaUpload>();
        u.ToTable("uploads", "media", t =>
        {
            t.HasCheckConstraint("ck_uploads_state", "state BETWEEN 0 AND 6 AND ((state=3 AND result IS NOT NULL AND error_code IS NULL) OR (state IN (4,5,6) AND result IS NULL AND error_code IS NOT NULL) OR (state IN (0,1,2) AND result IS NULL AND error_code IS NULL))");
            t.HasCheckConstraint("ck_uploads_expiry", "expires_at=created_at+interval '24 hours'");
            t.HasCheckConstraint("ck_uploads_fingerprint", "octet_length(request_fingerprint)=32");
            t.HasCheckConstraint("ck_uploads_purpose", "purpose IN (0,1,2) AND ((purpose=2 AND expected_avatar_version IS NOT NULL) OR (purpose<>2 AND expected_avatar_version IS NULL))");
            t.HasCheckConstraint("ck_uploads_content", "reserved_bytes>0 AND ((current_object_id IS NULL AND actual_size IS NULL AND content_sha256 IS NULL) OR (current_object_id IS NOT NULL AND actual_size=reserved_bytes AND octet_length(content_sha256)=32 AND content_sha256 IS NOT NULL)) AND (state NOT IN (1,2,3) OR current_object_id IS NOT NULL)");
            t.HasCheckConstraint("ck_uploads_lease", "lease_generation>=0 AND attempts>=0");
        });
        u.HasKey(x => x.Id).HasName("pk_uploads"); u.Property(x => x.Id).ValueGeneratedNever();
        u.Ignore(x => x.IsActive); u.Property(x => x.Purpose).HasConversion<short>(); u.Property(x => x.State).HasConversion<short>();
        u.Property(x => x.Result).HasColumnType("jsonb");
        u.HasIndex(x => x.FileId).IsUnique().HasDatabaseName("uq_uploads_file");
        u.HasIndex(x => new { x.Id, x.FileId }).IsUnique().HasDatabaseName("uq_uploads_id_file");
        u.HasIndex(x => new { x.OwnerId, x.IdempotencyKey }).IsUnique().HasDatabaseName("uq_uploads_owner_key");
        u.HasIndex(x => new { x.OwnerId, x.ExpiresAt }).HasFilter("state IN (0,1,2)").HasDatabaseName("ix_uploads_reserves");
        u.HasIndex(x => new { x.ExpiresAt, x.Id }).HasFilter("state IN (0,1,2)").HasDatabaseName("ix_uploads_expiry");
        u.HasIndex(x => new { x.NextAttemptAt, x.Id }).HasFilter("state=2").HasDatabaseName("ix_uploads_processing");
        Generated(u, "RequiredSourceKind", "0::smallint"); PersistenceSql.Columns(u);

        var a = model.Entity<StorageAllowance>();
        a.ToTable("allowances", "media", t => t.HasCheckConstraint("ck_allowances_bytes", "limit_bytes>0 AND used_bytes>=0 AND used_bytes<=limit_bytes"));
        a.HasKey(x => x.OwnerId).HasName("pk_allowances"); a.Property(x => x.OwnerId).ValueGeneratedNever();
        a.Property(x => x.LimitBytes).HasDefaultValue(MediaRules.DefaultAllowance);
        a.HasOne<LocalUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_allowances_owner");
        PersistenceSql.Columns(a);

        var av = model.Entity<AvatarState>();
        av.ToTable("avatar_states", "media"); av.HasKey(x => x.UserId).HasName("pk_avatar_states");
        av.Property(x => x.UserId).ValueGeneratedNever(); av.Property(x => x.Version).IsConcurrencyToken().ValueGeneratedNever();
        av.HasOne<LocalUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_avatar_states_owner");
        av.HasIndex(x => x.CurrentFileId).IsUnique().HasFilter("current_file_id IS NOT NULL").HasDatabaseName("uq_avatar_states_current");
        Generated(av, "RequiredPurpose", "2::smallint"); Generated(av, "RequiredState", "1::smallint"); PersistenceSql.Columns(av);

        var usage = model.Entity<PublicationUsage>();
        usage.ToTable("publication_usages", "media", t => t.HasCheckConstraint("ck_usages_role_position", "role IN (0,1,2) AND position>=0"));
        usage.HasKey(x => new { x.PublicationId, x.Role, x.FileId }).HasName("pk_publication_usages");
        usage.Property(x => x.Role).HasConversion<short>();
        usage.HasIndex(x => new { x.PublicationId, x.Role, x.Position }).IsUnique().HasDatabaseName("uq_usages_position");
        usage.HasIndex(x => x.PublicationId).IsUnique().HasFilter("role=0").HasDatabaseName("uq_usages_cover");
        usage.HasIndex(x => x.FileId).HasDatabaseName("ix_usages_file");
        Generated(usage, "RequiredPurpose", "CASE WHEN role=2 THEN 0::smallint ELSE 1::smallint END");
        Generated(usage, "RequiredState", "1::smallint"); PersistenceSql.Columns(usage);

        var o = model.Entity<MediaObject>();
        o.ToTable("objects", "media", t =>
        {
            t.HasCheckConstraint("ck_objects_kind_bucket", "(kind=0 AND bucket_kind=0) OR (kind IN (1,2,3) AND bucket_kind=1)");
            t.HasCheckConstraint("ck_objects_state", "state IN (0,1,2,3) AND (state<>1 OR (verified_length>0 AND verified_length IS NOT NULL AND octet_length(sha256)=32 AND sha256 IS NOT NULL)) AND ((state IN (0,1) AND retired_at IS NULL) OR (state IN (2,3) AND retired_at IS NOT NULL))");
            t.HasCheckConstraint("ck_objects_values", "char_length(object_key) BETWEEN 1 AND 1024 AND attempt_generation>=0 AND generation>=0 AND (sha256 IS NULL OR octet_length(sha256)=32) AND (verified_length IS NULL OR verified_length>0)");
        });
        o.HasKey(x => x.Id).HasName("pk_objects"); o.Property(x => x.Id).ValueGeneratedNever();
        o.Property(x => x.BucketKind).HasConversion<short>(); o.Property(x => x.Kind).HasConversion<short>(); o.Property(x => x.State).HasConversion<short>();
        o.HasIndex(x => new { x.BucketKind, x.ObjectKey }).IsUnique().HasDatabaseName("uq_objects_key");
        o.HasIndex(x => new { x.Id, x.FileId, x.Kind }).IsUnique().HasDatabaseName("uq_objects_file_kind");
        o.HasIndex(x => new { x.Id, x.FileId, x.Kind, x.State }).IsUnique().HasDatabaseName("uq_objects_file_kind_state");
        o.HasIndex(x => new { x.NextReconcileAt, x.Id }).HasFilter("state IN (2,3)").HasDatabaseName("ix_objects_reconcile");
        PersistenceSql.Columns(o);

        var r = model.Entity<MediaRepresentation>();
        r.ToTable("representations", "media", t =>
        {
            t.HasCheckConstraint("ck_representations_dimensions", "width>0 AND height>0 AND ((kind=1 AND width<=2560 AND height<=2560) OR (kind=2 AND width=256 AND height=256) OR (kind=3 AND width=512 AND height=512))");
            t.HasCheckConstraint("ck_representations_content", "mime='image/png' AND verified_length>0 AND octet_length(sha256)=32");
        });
        r.HasKey(x => new { x.FileId, x.Kind }).HasName("pk_representations"); r.Property(x => x.Kind).HasConversion<short>();
        r.HasIndex(x => x.ObjectId).IsUnique().HasDatabaseName("uq_representations_object");
        Generated(r, "RequiredState", "1::smallint"); PersistenceSql.Columns(r);

        var j = model.Entity<MediaCleanupJob>();
        j.ToTable("cleanup_jobs", "media", t => t.HasCheckConstraint("ck_cleanup_jobs_values", "attempts>=0 AND generation>=0 AND due_at>=not_before AND next_attempt_at>=not_before"));
        j.HasKey(x => x.Id).HasName("pk_cleanup_jobs"); j.Property(x => x.Id).ValueGeneratedNever();
        j.HasIndex(x => x.ObjectId).IsUnique().HasDatabaseName("uq_cleanup_jobs_object");
        j.HasOne<MediaObject>().WithMany().HasForeignKey(x => x.ObjectId).OnDelete(DeleteBehavior.NoAction).HasConstraintName("fk_cleanup_jobs_object");
        j.HasIndex(x => new { x.NextAttemptAt, x.Id }).HasFilter("completed_at IS NULL").HasDatabaseName("ix_cleanup_jobs_due");
        PersistenceSql.Columns(j);
    }

    private static void Generated<T>(EntityTypeBuilder<T> builder, string name, string sql) where T : class =>
        builder.Property<short?>(name).HasComputedColumnSql(sql, stored: true);
}
