using BlogEngine.Core.Accounts.Domain;
using BlogEngine.Core.Media.Domain;
using BlogEngine.Core.Publishing.Domain;
using BlogEngine.Infrastructure.Persistence;
using BlogEngine.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BlogEngine.IntegrationTests.Media;

public sealed class MediaConstraintTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static BlogDbContext Context(string connection) => new(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(connection).Options);

    [Fact]
    public async Task MediaSchemaEnforcesOwnershipPurposeStateAndRequiredObjects()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await postgres.CreateDatabaseAsync(Context, ct);
        await using var db = Context(database.ConnectionString);
        var tables = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_tables WHERE schemaname='media' AND tablename <> 'schema_version'").SingleAsync(ct);
        Assert.Equal(8, tables);
        var now = DateTimeOffset.UtcNow;
        var owner = Guid.NewGuid(); var outsider = Guid.NewGuid();
        db.LocalUsers.AddRange(new LocalUser(owner, "https://fixture", Guid.NewGuid(), now), new LocalUser(outsider, "https://fixture", Guid.NewGuid(), now));
        await db.SaveChangesAsync(ct);
        async Task<(MediaFile File, MediaUpload Upload, MediaObject Source)> Pending(MediaPurpose purpose, Guid? user = null)
        {
            var file = MediaFile.Create(user ?? owner, purpose, "source.bin", purpose == MediaPurpose.Attachment ? "application/octet-stream" : "image/png", 10, now);
            db.MediaFiles.Add(file); await db.SaveChangesAsync(ct);
            var upload = MediaUpload.Create(file, Guid.NewGuid(), purpose == MediaPurpose.Avatar ? Guid.NewGuid() : null, now);
            db.MediaUploads.Add(upload); await db.SaveChangesAsync(ct);
            var source = MediaObject.Plan(upload, MediaObjectKind.Source, now);
            db.MediaObjects.Add(source); await db.SaveChangesAsync(ct);
            return (file, upload, source);
        }
        async Task Denied(FormattableString sql, string state)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql, ct));
            Assert.Equal(state, error.SqlState);
        }
        var attachment = await Pending(MediaPurpose.Attachment);
        var other = await Pending(MediaPurpose.Attachment, outsider);
        var image = await Pending(MediaPurpose.PublicationImage);
        var avatar = await Pending(MediaPurpose.Avatar);
        await Denied($"UPDATE media.uploads SET owner_id={outsider} WHERE id={attachment.Upload.Id}", "23503");
        await Denied($"UPDATE media.objects SET file_id={other.File.Id} WHERE id={attachment.Source.Id}", "23503");
        await Denied($"UPDATE media.uploads SET current_object_id={other.Source.Id},actual_size=10,content_sha256={new byte[32]} WHERE id={attachment.Upload.Id}", "23503");
        await Denied($"UPDATE media.files SET state=1,ready_at={now},verified_content_type='application/octet-stream' WHERE id={attachment.File.Id}", "23514");
        await Denied($"UPDATE media.files SET state=1,ready_at={now},verified_content_type='application/octet-stream',original_object_id={attachment.Source.Id} WHERE id={attachment.File.Id}", "23503");
        attachment.Source.MarkPresent(10, new byte[32]); image.Source.MarkPresent(10, new byte[32]);
        await db.SaveChangesAsync(ct);
        await Denied($"UPDATE media.files SET state=1,ready_at={now},verified_content_type='image/png',original_object_id={image.Source.Id} WHERE id={image.File.Id}", "23503");
        await Denied($"UPDATE media.files SET state=1,ready_at={now},verified_content_type='image/png' WHERE id={avatar.File.Id}", "23503");
        await Denied($"INSERT INTO media.representations(file_id,kind,object_id,width,height,mime,verified_length,sha256) VALUES({image.File.Id},1,{image.Source.Id},20,20,'image/png',10,{new byte[32]})", "23503");
        attachment.File.MakeReady(attachment.Source.Id, "application/octet-stream", now); await db.SaveChangesAsync(ct);
        await Denied($"UPDATE media.objects SET state=2,retired_at={now} WHERE id={attachment.Source.Id}", "23503");
        await Denied($"UPDATE media.files SET original_object_id={other.Source.Id} WHERE id={attachment.File.Id}", "23503");
        foreach (var invalid in new[] { attachment.File.Id, avatar.File.Id, other.File.Id })
        { await Denied($"INSERT INTO media.avatar_states(user_id,current_file_id,version,updated_at) VALUES({owner},{invalid},{Guid.NewGuid()},{now})", "23503"); }
        var publication = Publication.Create(owner, new("Title", "media-test", "Body", null, null, [], []), now);
        db.Publications.Add(publication); await db.SaveChangesAsync(ct);
        await Denied($"INSERT INTO media.publication_usages(publication_id,file_id,publication_owner_id,role,position) VALUES({publication.Id},{attachment.File.Id},{owner},0,0)", "23503");
        await Denied($"INSERT INTO media.publication_usages(publication_id,file_id,publication_owner_id,role,position) VALUES({publication.Id},{image.File.Id},{owner},1,0)", "23503");
        await Denied($"INSERT INTO media.publication_usages(publication_id,file_id,publication_owner_id,role,position) VALUES({publication.Id},{other.File.Id},{outsider},2,0)", "23503");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO media.publication_usages(publication_id,file_id,publication_owner_id,role,position) VALUES({publication.Id},{attachment.File.Id},{owner},2,0)", ct);
        await Denied($"INSERT INTO media.publication_usages(publication_id,file_id,publication_owner_id,role,position) VALUES({publication.Id},{attachment.File.Id},{owner},2,1)", "23505");
        await Denied($"UPDATE media.files SET state=2,deleted_at={now},original_object_id=NULL WHERE id={attachment.File.Id}", "23503");
        await Denied($"UPDATE media.files SET original_name={"bad\u0085name"} WHERE id={attachment.File.Id}", "23514");
        await Denied($"UPDATE media.uploads SET expected_avatar_version={Guid.NewGuid()} WHERE id={attachment.Upload.Id}", "23514");
        await Denied($"UPDATE media.uploads SET expires_at=created_at+interval '25 hours' WHERE id={attachment.Upload.Id}", "23514");

        // Valid mutable-state transitions pass through EF without alternate keys or disabled constraints.
        var display = MediaObject.Plan(image.Upload, MediaObjectKind.Display, now);
        db.MediaObjects.Add(display); await db.SaveChangesAsync(ct);
        await Denied($"INSERT INTO media.representations(file_id,kind,object_id,width,height,mime,verified_length,sha256) VALUES({image.File.Id},1,{display.Id},20,20,'image/png',12,{new byte[32]})", "23503");
        display.MarkPresent(12, new byte[32]); await db.SaveChangesAsync(ct);
        db.MediaRepresentations.Add(MediaRepresentation.Create(display, 20, 20)); await db.SaveChangesAsync(ct);
        await Denied($"UPDATE media.files SET state=1,ready_at={now},verified_content_type='image/png',original_object_id={display.Id} WHERE id={image.File.Id}", "23503");
        image.File.MakeReady(image.Source.Id, "image/png", now); await db.SaveChangesAsync(ct);
        await Denied($"UPDATE media.objects SET state=2,retired_at={now} WHERE id={display.Id}", "23503");
        var small = MediaObject.Plan(avatar.Upload, MediaObjectKind.Avatar256, now); small.MarkPresent(12, new byte[32]);
        var large = MediaObject.Plan(avatar.Upload, MediaObjectKind.Avatar512, now); large.MarkPresent(13, new byte[32]);
        db.MediaObjects.AddRange(small, large); await db.SaveChangesAsync(ct);
        db.MediaRepresentations.Add(MediaRepresentation.Create(small, 256, 256)); await db.SaveChangesAsync(ct);
        await Denied($"UPDATE media.files SET state=1,ready_at={now},verified_content_type='image/png' WHERE id={avatar.File.Id}", "23503");
        db.MediaRepresentations.Add(MediaRepresentation.Create(large, 512, 512)); await db.SaveChangesAsync(ct);
        avatar.File.MakeReady(null, "image/png", now); await db.SaveChangesAsync(ct);
        var state = AvatarState.Empty(owner, now); state.Replace(state.Version, avatar.File, now);
        db.AvatarStates.Add(state); await db.SaveChangesAsync(ct);
        state.Replace(state.Version, null, now); await db.SaveChangesAsync(ct);
        avatar.File.Delete(now); await db.SaveChangesAsync(ct);
        await db.MediaRepresentations.Where(r => r.FileId == avatar.File.Id).ExecuteDeleteAsync(ct);
        small.Retire(now); large.Retire(now); await db.SaveChangesAsync(ct);
    }
}
