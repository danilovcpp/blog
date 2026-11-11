using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BlogEngine.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddMedia : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "media");

        migrationBuilder.CreateTable(
            name: "allowances",
            schema: "media",
            columns: table => new
            {
                owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                limit_bytes = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1073741824L),
                used_bytes = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_allowances", x => x.owner_id);
                table.CheckConstraint("ck_allowances_bytes", "limit_bytes>0 AND used_bytes>=0 AND used_bytes<=limit_bytes");
                table.ForeignKey(
                    name: "fk_allowances_owner",
                    column: x => x.owner_id,
                    principalSchema: "accounts",
                    principalTable: "local_users",
                    principalColumn: "id");
            });

        migrationBuilder.CreateTable(
            name: "avatar_states",
            schema: "media",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                current_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                version = table.Column<Guid>(type: "uuid", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                required_purpose = table.Column<short>(type: "smallint", nullable: true, computedColumnSql: "2::smallint", stored: true),
                required_state = table.Column<short>(type: "smallint", nullable: true, computedColumnSql: "1::smallint", stored: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_avatar_states", x => x.user_id);
                table.ForeignKey(
                    name: "fk_avatar_states_owner",
                    column: x => x.user_id,
                    principalSchema: "accounts",
                    principalTable: "local_users",
                    principalColumn: "id");
            });

        migrationBuilder.CreateTable(
            name: "files",
            schema: "media",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                purpose = table.Column<short>(type: "smallint", nullable: false),
                original_name = table.Column<string>(type: "text", nullable: false),
                declared_content_type = table.Column<string>(type: "text", nullable: false),
                original_size = table.Column<long>(type: "bigint", nullable: false),
                verified_content_type = table.Column<string>(type: "text", nullable: true),
                state = table.Column<short>(type: "smallint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                original_object_id = table.Column<Guid>(type: "uuid", nullable: true),
                version = table.Column<Guid>(type: "uuid", nullable: false),
                required_avatar256_kind = table.Column<short>(type: "smallint", nullable: true, computedColumnSql: "CASE WHEN state=1 AND purpose=2 THEN 2::smallint END", stored: true),
                required_avatar512_kind = table.Column<short>(type: "smallint", nullable: true, computedColumnSql: "CASE WHEN state=1 AND purpose=2 THEN 3::smallint END", stored: true),
                required_display_kind = table.Column<short>(type: "smallint", nullable: true, computedColumnSql: "CASE WHEN state=1 AND purpose=1 THEN 1::smallint END", stored: true),
                required_source_kind = table.Column<short>(type: "smallint", nullable: true, computedColumnSql: "0::smallint", stored: true),
                required_source_state = table.Column<short>(type: "smallint", nullable: true, computedColumnSql: "1::smallint", stored: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_files", x => x.id);
                table.CheckConstraint("ck_files_content_type", "declared_content_type ~ '^[a-z0-9!#$%&''*+.^_`|~-]+/[a-z0-9!#$%&''*+.^_`|~-]+$' AND (purpose=0 OR declared_content_type IN ('image/jpeg','image/png','image/webp')) AND (verified_content_type IS NULL OR (purpose=0 OR verified_content_type IN ('image/jpeg','image/png','image/webp')))");
                table.CheckConstraint("ck_files_name", "original_name = btrim(original_name, U&'\\0009\\000a\\000b\\000c\\000d\\0020\\0085\\00a0\\1680\\2000\\2001\\2002\\2003\\2004\\2005\\2006\\2007\\2008\\2009\\200a\\2028\\2029\\202f\\205f\\3000') AND char_length(original_name) BETWEEN 1 AND 255 AND original_name NOT IN ('.','..') AND original_name !~ '^[a-zA-Z]:' AND position('/' in original_name)=0 AND position(chr(92) in original_name)=0 AND original_name !~ '[[:cntrl:]]' AND original_name !~ U&'[\\0080-\\009f]'");
                table.CheckConstraint("ck_files_purpose", "purpose IN (0,1,2)");
                table.CheckConstraint("ck_files_size", "original_size > 0 AND original_size <= CASE purpose WHEN 0 THEN 52428800 WHEN 1 THEN 10485760 WHEN 2 THEN 5242880 ELSE 0 END");
                table.CheckConstraint("ck_files_state", "(state=0 AND ready_at IS NULL AND deleted_at IS NULL AND original_object_id IS NULL AND verified_content_type IS NULL) OR (state=1 AND ready_at IS NOT NULL AND deleted_at IS NULL AND verified_content_type IS NOT NULL AND ((purpose IN (0,1) AND original_object_id IS NOT NULL) OR (purpose=2 AND original_object_id IS NULL))) OR (state=2 AND deleted_at IS NOT NULL AND original_object_id IS NULL)");
                table.ForeignKey(
                    name: "fk_files_owner",
                    column: x => x.owner_id,
                    principalSchema: "accounts",
                    principalTable: "local_users",
                    principalColumn: "id");
            });

        migrationBuilder.CreateTable(
            name: "objects",
            schema: "media",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                upload_id = table.Column<Guid>(type: "uuid", nullable: false),
                file_id = table.Column<Guid>(type: "uuid", nullable: false),
                bucket_kind = table.Column<short>(type: "smallint", nullable: false),
                object_key = table.Column<string>(type: "text", nullable: false),
                attempt_generation = table.Column<long>(type: "bigint", nullable: false),
                kind = table.Column<short>(type: "smallint", nullable: false),
                state = table.Column<short>(type: "smallint", nullable: false),
                verified_length = table.Column<long>(type: "bigint", nullable: true),
                sha256 = table.Column<byte[]>(type: "bytea", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                generation = table.Column<long>(type: "bigint", nullable: false),
                lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                next_reconcile_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                last_reconciled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_objects", x => x.id);
                table.CheckConstraint("ck_objects_kind_bucket", "(kind=0 AND bucket_kind=0) OR (kind IN (1,2,3) AND bucket_kind=1)");
                table.CheckConstraint("ck_objects_state", "state IN (0,1,2,3) AND (state<>1 OR (verified_length>0 AND verified_length IS NOT NULL AND octet_length(sha256)=32 AND sha256 IS NOT NULL)) AND ((state IN (0,1) AND retired_at IS NULL) OR (state IN (2,3) AND retired_at IS NOT NULL))");
                table.CheckConstraint("ck_objects_values", "char_length(object_key) BETWEEN 1 AND 1024 AND attempt_generation>=0 AND generation>=0 AND (sha256 IS NULL OR octet_length(sha256)=32) AND (verified_length IS NULL OR verified_length>0)");
            });

        migrationBuilder.CreateTable(
            name: "publication_usages",
            schema: "media",
            columns: table => new
            {
                publication_id = table.Column<Guid>(type: "uuid", nullable: false),
                file_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<short>(type: "smallint", nullable: false),
                publication_owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                position = table.Column<int>(type: "integer", nullable: false),
                required_purpose = table.Column<short>(type: "smallint", nullable: true, computedColumnSql: "CASE WHEN role=2 THEN 0::smallint ELSE 1::smallint END", stored: true),
                required_state = table.Column<short>(type: "smallint", nullable: true, computedColumnSql: "1::smallint", stored: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_publication_usages", x => new { x.publication_id, x.role, x.file_id });
                table.CheckConstraint("ck_usages_role_position", "role IN (0,1,2) AND position>=0");
            });

        migrationBuilder.CreateTable(
            name: "representations",
            schema: "media",
            columns: table => new
            {
                file_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<short>(type: "smallint", nullable: false),
                object_id = table.Column<Guid>(type: "uuid", nullable: false),
                width = table.Column<int>(type: "integer", nullable: false),
                height = table.Column<int>(type: "integer", nullable: false),
                mime = table.Column<string>(type: "text", nullable: false),
                verified_length = table.Column<long>(type: "bigint", nullable: false),
                sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                required_state = table.Column<short>(type: "smallint", nullable: true, computedColumnSql: "1::smallint", stored: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_representations", x => new { x.file_id, x.kind });
                table.CheckConstraint("ck_representations_content", "mime='image/png' AND verified_length>0 AND octet_length(sha256)=32");
                table.CheckConstraint("ck_representations_dimensions", "width>0 AND height>0 AND ((kind=1 AND width<=2560 AND height<=2560) OR (kind=2 AND width=256 AND height=256) OR (kind=3 AND width=512 AND height=512))");
            });

        migrationBuilder.CreateTable(
            name: "uploads",
            schema: "media",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                file_id = table.Column<Guid>(type: "uuid", nullable: false),
                owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                purpose = table.Column<short>(type: "smallint", nullable: false),
                idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                request_fingerprint = table.Column<byte[]>(type: "bytea", nullable: false),
                state = table.Column<short>(type: "smallint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                reserved_bytes = table.Column<long>(type: "bigint", nullable: false),
                current_object_id = table.Column<Guid>(type: "uuid", nullable: true),
                content_sha256 = table.Column<byte[]>(type: "bytea", nullable: true),
                actual_size = table.Column<long>(type: "bigint", nullable: true),
                expected_avatar_version = table.Column<Guid>(type: "uuid", nullable: true),
                lease_generation = table.Column<long>(type: "bigint", nullable: false),
                lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                attempts = table.Column<int>(type: "integer", nullable: false),
                result = table.Column<string>(type: "jsonb", nullable: true),
                error_code = table.Column<string>(type: "text", nullable: true),
                required_source_kind = table.Column<short>(type: "smallint", nullable: true, computedColumnSql: "0::smallint", stored: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_uploads", x => x.id);
                table.CheckConstraint("ck_uploads_content", "reserved_bytes>0 AND ((current_object_id IS NULL AND actual_size IS NULL AND content_sha256 IS NULL) OR (current_object_id IS NOT NULL AND actual_size=reserved_bytes AND octet_length(content_sha256)=32 AND content_sha256 IS NOT NULL)) AND (state NOT IN (1,2,3) OR current_object_id IS NOT NULL)");
                table.CheckConstraint("ck_uploads_expiry", "expires_at=created_at+interval '24 hours'");
                table.CheckConstraint("ck_uploads_fingerprint", "octet_length(request_fingerprint)=32");
                table.CheckConstraint("ck_uploads_lease", "lease_generation>=0 AND attempts>=0");
                table.CheckConstraint("ck_uploads_purpose", "purpose IN (0,1,2) AND ((purpose=2 AND expected_avatar_version IS NOT NULL) OR (purpose<>2 AND expected_avatar_version IS NULL))");
                table.CheckConstraint("ck_uploads_state", "state BETWEEN 0 AND 6 AND ((state=3 AND result IS NOT NULL AND error_code IS NULL) OR (state IN (4,5,6) AND result IS NULL AND error_code IS NOT NULL) OR (state IN (0,1,2) AND result IS NULL AND error_code IS NULL))");
            });

        migrationBuilder.CreateTable(
            name: "cleanup_jobs",
            schema: "media",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                object_id = table.Column<Guid>(type: "uuid", nullable: false),
                not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                attempts = table.Column<int>(type: "integer", nullable: false),
                next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                generation = table.Column<long>(type: "bigint", nullable: false),
                lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                error_code = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_cleanup_jobs", x => x.id);
                table.CheckConstraint("ck_cleanup_jobs_values", "attempts>=0 AND generation>=0 AND due_at>=not_before AND next_attempt_at>=not_before");
                table.ForeignKey(
                    name: "fk_cleanup_jobs_object",
                    column: x => x.object_id,
                    principalSchema: "media",
                    principalTable: "objects",
                    principalColumn: "id");
            });

        migrationBuilder.CreateIndex(
            name: "uq_publications_owner",
            schema: "publishing",
            table: "publications",
            columns: new[] { "id", "author_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "uq_avatar_states_current",
            schema: "media",
            table: "avatar_states",
            column: "current_file_id",
            unique: true,
            filter: "current_file_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_cleanup_jobs_due",
            schema: "media",
            table: "cleanup_jobs",
            columns: new[] { "next_attempt_at", "id" },
            filter: "completed_at IS NULL");

        migrationBuilder.CreateIndex(
            name: "uq_cleanup_jobs_object",
            schema: "media",
            table: "cleanup_jobs",
            column: "object_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_files_library",
            schema: "media",
            table: "files",
            columns: new[] { "owner_id", "created_at", "id" },
            descending: new[] { false, true, true },
            filter: "state=1 AND purpose IN (0,1)");

        migrationBuilder.CreateIndex(
            name: "uq_files_owner_purpose",
            schema: "media",
            table: "files",
            columns: new[] { "id", "owner_id", "purpose" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "uq_files_owner_purpose_state",
            schema: "media",
            table: "files",
            columns: new[] { "id", "owner_id", "purpose", "state" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_objects_reconcile",
            schema: "media",
            table: "objects",
            columns: new[] { "next_reconcile_at", "id" },
            filter: "state IN (2,3)");

        migrationBuilder.CreateIndex(
            name: "uq_objects_file_kind",
            schema: "media",
            table: "objects",
            columns: new[] { "id", "file_id", "kind" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "uq_objects_file_kind_state",
            schema: "media",
            table: "objects",
            columns: new[] { "id", "file_id", "kind", "state" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "uq_objects_key",
            schema: "media",
            table: "objects",
            columns: new[] { "bucket_kind", "object_key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_usages_file",
            schema: "media",
            table: "publication_usages",
            column: "file_id");

        migrationBuilder.CreateIndex(
            name: "uq_usages_cover",
            schema: "media",
            table: "publication_usages",
            column: "publication_id",
            unique: true,
            filter: "role=0");

        migrationBuilder.CreateIndex(
            name: "uq_usages_position",
            schema: "media",
            table: "publication_usages",
            columns: new[] { "publication_id", "role", "position" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "uq_representations_object",
            schema: "media",
            table: "representations",
            column: "object_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_uploads_expiry",
            schema: "media",
            table: "uploads",
            columns: new[] { "expires_at", "id" },
            filter: "state IN (0,1,2)");

        migrationBuilder.CreateIndex(
            name: "ix_uploads_processing",
            schema: "media",
            table: "uploads",
            columns: new[] { "next_attempt_at", "id" },
            filter: "state=2");

        migrationBuilder.CreateIndex(
            name: "ix_uploads_reserves",
            schema: "media",
            table: "uploads",
            columns: new[] { "owner_id", "expires_at" },
            filter: "state IN (0,1,2)");

        migrationBuilder.CreateIndex(
            name: "uq_uploads_file",
            schema: "media",
            table: "uploads",
            column: "file_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "uq_uploads_id_file",
            schema: "media",
            table: "uploads",
            columns: new[] { "id", "file_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "uq_uploads_owner_key",
            schema: "media",
            table: "uploads",
            columns: new[] { "owner_id", "idempotency_key" },
            unique: true);

        // Mutable state is deliberately not an EF alternate key. PostgreSQL owns these FKs.
        migrationBuilder.Sql("""
                ALTER TABLE media.uploads ADD CONSTRAINT fk_uploads_file FOREIGN KEY(file_id,owner_id,purpose)
                    REFERENCES media.files(id,owner_id,purpose) ON DELETE NO ACTION;
                ALTER TABLE media.objects ADD CONSTRAINT fk_objects_upload FOREIGN KEY(upload_id,file_id)
                    REFERENCES media.uploads(id,file_id) ON DELETE NO ACTION;
                ALTER TABLE media.uploads ADD CONSTRAINT fk_uploads_source FOREIGN KEY(current_object_id,file_id,required_source_kind)
                    REFERENCES media.objects(id,file_id,kind) ON DELETE NO ACTION;
                ALTER TABLE media.files ADD CONSTRAINT fk_files_source FOREIGN KEY(original_object_id,id,required_source_kind,required_source_state)
                    REFERENCES media.objects(id,file_id,kind,state) ON DELETE NO ACTION;
                ALTER TABLE media.representations ADD CONSTRAINT fk_representations_object FOREIGN KEY(object_id,file_id,kind,required_state)
                    REFERENCES media.objects(id,file_id,kind,state) ON DELETE NO ACTION;
                ALTER TABLE media.files ADD CONSTRAINT fk_files_display FOREIGN KEY(id,required_display_kind)
                    REFERENCES media.representations(file_id,kind) ON DELETE NO ACTION;
                ALTER TABLE media.files ADD CONSTRAINT fk_files_avatar256 FOREIGN KEY(id,required_avatar256_kind)
                    REFERENCES media.representations(file_id,kind) ON DELETE NO ACTION;
                ALTER TABLE media.files ADD CONSTRAINT fk_files_avatar512 FOREIGN KEY(id,required_avatar512_kind)
                    REFERENCES media.representations(file_id,kind) ON DELETE NO ACTION;
                ALTER TABLE media.avatar_states ADD CONSTRAINT fk_avatar_states_file FOREIGN KEY(current_file_id,user_id,required_purpose,required_state)
                    REFERENCES media.files(id,owner_id,purpose,state) ON DELETE NO ACTION;
                ALTER TABLE media.publication_usages ADD CONSTRAINT fk_usages_file FOREIGN KEY(file_id,publication_owner_id,required_purpose,required_state)
                    REFERENCES media.files(id,owner_id,purpose,state) ON DELETE NO ACTION;
                ALTER TABLE media.publication_usages ADD CONSTRAINT fk_usages_publication FOREIGN KEY(publication_id,publication_owner_id)
                    REFERENCES publishing.publications(id,author_id) ON DELETE NO ACTION;
                CREATE TABLE media.schema_version(version integer PRIMARY KEY CHECK(version=1));
                INSERT INTO media.schema_version VALUES(1);
                """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Remove PostgreSQL-owned composite FKs before EF drops their targets.
        migrationBuilder.Sql("""
            ALTER TABLE media.files DROP CONSTRAINT fk_files_source, DROP CONSTRAINT fk_files_display,
                DROP CONSTRAINT fk_files_avatar256, DROP CONSTRAINT fk_files_avatar512;
            ALTER TABLE media.uploads DROP CONSTRAINT fk_uploads_file, DROP CONSTRAINT fk_uploads_source;
            ALTER TABLE media.objects DROP CONSTRAINT fk_objects_upload;
            ALTER TABLE media.avatar_states DROP CONSTRAINT fk_avatar_states_file;
            ALTER TABLE media.publication_usages DROP CONSTRAINT fk_usages_file, DROP CONSTRAINT fk_usages_publication;
            ALTER TABLE media.representations DROP CONSTRAINT fk_representations_object;
            DROP TABLE media.schema_version;
            """);
        migrationBuilder.DropTable(
            name: "allowances",
            schema: "media");

        migrationBuilder.DropTable(
            name: "avatar_states",
            schema: "media");

        migrationBuilder.DropTable(
            name: "cleanup_jobs",
            schema: "media");

        migrationBuilder.DropTable(
            name: "files",
            schema: "media");

        migrationBuilder.DropTable(
            name: "publication_usages",
            schema: "media");

        migrationBuilder.DropTable(
            name: "representations",
            schema: "media");

        migrationBuilder.DropTable(
            name: "uploads",
            schema: "media");

        migrationBuilder.DropTable(
            name: "objects",
            schema: "media");

        migrationBuilder.DropIndex(
            name: "uq_publications_owner",
            schema: "publishing",
            table: "publications");
    }
}
