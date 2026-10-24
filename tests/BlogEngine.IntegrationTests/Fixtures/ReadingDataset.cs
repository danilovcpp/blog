using System.Globalization;
using System.Net.Http.Headers;
using BlogEngine.IntegrationTests.Accounts;
using Microsoft.EntityFrameworkCore;

namespace BlogEngine.IntegrationTests.Fixtures;

internal sealed class ReadingDataset
{
    public static Guid Id(int number) => Guid.Parse(number.ToString("D32", CultureInfo.InvariantCulture));
    public static Guid AuthorId => Id(1001);
    public static Guid FirstReaderId => Id(1002);
    public static Guid SecondReaderId => Id(1003);
    public static Guid Category(int index) => Id(2000 + index);
    public static Guid Tag(int index) => Id(3000 + index);
    public static bool IsPublic(int number) => number % 11 != 0 && number % 13 != 0;
    public static bool HasCategory(int number, int index) => index switch
    { 0 => number % 2 == 0, 1 => number % 3 == 0, 2 => number % 5 == 0, 3 => number % 7 == 0, 4 => number % 2 != 0, _ => false };
    public static bool HasTag(int number, int index) => index < 20 && number % (index + 1) == 0;
    public static int Likes(int number) => (number % 2 == 0 ? 1 : 0) + (number % 3 == 0 ? 1 : 0);

    public static IReadOnlyList<Guid> Expected(string sort, int[]? categories = null, int[]? tags = null)
    {
        var rows = Enumerable.Range(1, 250).Where(IsPublic)
            .Where(i => categories is null || categories.Length == 0 || categories.Any(c => HasCategory(i, c)))
            .Where(i => tags is null || tags.All(t => HasTag(i, t)));
        return (sort == "popular" ? rows.OrderByDescending(Likes).ThenByDescending(i => i / 10).ThenByDescending(i => i) :
            rows.OrderByDescending(i => i / 10).ThenByDescending(i => i)).Select(Id).ToArray();
    }

    public static async Task SeedAsync(PublishingFixture fixture, CancellationToken ct)
    {
        await using var db = BootstrapTests.Context(fixture.Database.ConnectionString);
        // Deterministic IDs, timestamps, ties, classifications and likes; each test owns its database.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO accounts.local_users (id,issuer,subject,created_at)
            SELECT lpad(i::text,32,'0')::uuid, {fixture.Identity.Issuer}, lpad((i+100)::text,32,'0')::uuid,
                TIMESTAMPTZ '2026-01-01 00:00:00+00' FROM generate_series(1001,1003) s(i);
            INSERT INTO publishing.categories (id,name,slug,version)
            SELECT lpad((2000+i)::text,32,'0')::uuid, 'Category '||i, 'category-'||i, lpad((4000+i)::text,32,'0')::uuid FROM generate_series(0,5) s(i);
            INSERT INTO publishing.tags (id,name,slug,version)
            SELECT lpad((3000+i)::text,32,'0')::uuid, 'Tag '||i, 'tag-'||i, lpad((5000+i)::text,32,'0')::uuid FROM generate_series(0,20) s(i);
            INSERT INTO publishing.publications
                (id,author_id,title,slug,markdown,description,cover_url,state,created_at,updated_at,first_published_at,deleted_at,version,like_count,view_count)
            SELECT lpad(i::text,32,'0')::uuid, {AuthorId}, 'Title '||i, 'reading-'||i,
                CASE WHEN i=1 THEN repeat('x',131072) ELSE '# untrusted <script>alert(1)</script>' END,
                CASE WHEN i%2=0 THEN 'Description '||i END, CASE WHEN i%2=0 THEN 'https://example.test/cover' END,
                CASE WHEN i%11=0 THEN 0 ELSE 1 END, TIMESTAMPTZ '2026-01-01 00:00:00+00',
                TIMESTAMPTZ '2026-01-02 00:00:00+00' + (i/10)*INTERVAL '1 day',
                CASE WHEN i%11<>0 THEN TIMESTAMPTZ '2026-01-02 00:00:00+00' + (i/10)*INTERVAL '1 day' END,
                CASE WHEN i%13=0 THEN TIMESTAMPTZ '2026-03-01 00:00:00+00' END,
                lpad((6000+i)::text,32,'0')::uuid, (i%2=0)::int + (i%3=0)::int, 3000000000::bigint+i
            FROM generate_series(1,250) s(i);
            INSERT INTO publishing.publication_categories
            SELECT lpad(i::text,32,'0')::uuid, lpad((2000+c)::text,32,'0')::uuid
            FROM generate_series(1,250) s(i) CROSS JOIN generate_series(0,4) t(c)
            WHERE (c=0 AND i%2=0) OR (c=1 AND i%3=0) OR (c=2 AND i%5=0) OR (c=3 AND i%7=0) OR (c=4 AND i%2<>0);
            INSERT INTO publishing.publication_tags
            SELECT lpad(i::text,32,'0')::uuid, lpad((3000+t)::text,32,'0')::uuid
            FROM generate_series(1,250) s(i) CROSS JOIN generate_series(0,19) v(t) WHERE i%(t+1)=0;
            INSERT INTO engagement.likes (publication_id,user_id,created_at)
            SELECT lpad(i::text,32,'0')::uuid, {FirstReaderId}, TIMESTAMPTZ '2026-02-01 00:00:00+00' FROM generate_series(1,250) s(i) WHERE i%2=0;
            INSERT INTO engagement.likes (publication_id,user_id,created_at)
            SELECT lpad(i::text,32,'0')::uuid, {SecondReaderId}, TIMESTAMPTZ '2026-02-01 00:00:00+00' FROM generate_series(1,250) s(i) WHERE i%3=0;
            ANALYZE publishing.publications;
            ANALYZE publishing.publication_categories;
            ANALYZE publishing.publication_tags;
            ANALYZE engagement.likes;
            """, ct);
    }

    public static string Query(string sort = "recent", int page = 1, int pageSize = 20, int[]? categories = null, int[]? tags = null) =>
        $"/api/v1/publications?sort={sort}&page={page}&pageSize={pageSize}" +
        string.Concat((categories ?? []).Select(i => "&categoryIds=" + Category(i))) +
        string.Concat((tags ?? []).Select(i => "&tagIds=" + Tag(i)));

    public static void Authenticate(HttpClient client, PublishingFixture fixture, bool second = false) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Identity.CreateToken(Id(second ? 1103 : 1102)));
}
