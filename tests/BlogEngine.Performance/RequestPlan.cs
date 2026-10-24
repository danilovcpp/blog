using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlogEngine.Performance;

public sealed record RequestTemplate(string Kind, string Sort, int Size, int Page, string Shape, string Tier, string Outcome, bool Bearer, bool Slug, int BodyBytes);
public sealed record FilterCandidate(int[] Categories, int[] Tags, int[] Matches);
public sealed record PlannedRequest(string Path, RequestTemplate Template, int[] Expected, bool HasNextPage, int? Publication, int User);

public sealed class RequestPlan
{
    private readonly SeedDataset dataset;
    private readonly Dictionary<(string Shape, string Tier, int Page, int Size, string Outcome), FilterCandidate[]> catalog = [];
    private readonly Dictionary<int, int[]> bodies;
    private readonly int[][] categoryMembers;
    private readonly int[][] tagMembers;
    private readonly HashSet<int>[] tagSets;
    private readonly Dictionary<string, int[]> ordered;
    private readonly Dictionary<(FilterCandidate Candidate, string Sort), int[]> candidateOrders = [];
    private readonly List<RequestTemplate> templates = [];
    public IReadOnlyList<RequestTemplate> Templates => templates;
    public string CatalogChecksum { get; }
    public string TemplateChecksum { get; }

    public async Task SaveAsync(string output, CancellationToken ct)
    {
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "request-plan.json"), JsonSerializer.Serialize(new { CatalogChecksum, TemplateChecksum, Templates }, BenchmarkManifest.Json), ct);
        await File.WriteAllTextAsync(Path.Combine(output, "request-catalog.json"), JsonSerializer.Serialize(catalog.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal)
            .Select(p => new { template = p.Key.ToString(), candidates = p.Value.Select(c => new { c.Categories, c.Tags, matchedCount = c.Matches.Length, expectedOrdinalsChecksum = BenchmarkManifest.Hash(JsonSerializer.Serialize(c.Matches)) }) }), BenchmarkManifest.Json), ct);
    }

    public PlannedRequest ExpectedFeed(string path, string sort, int page, int size, int[] categories, int[] tags)
    {
        var candidates = categories.Length == 0 ? Enumerable.Range(0, SeedDataset.PublicationCount) : categories.SelectMany(c => categoryMembers[c]).Distinct();
        var matched = candidates.Where(n => tags.All(t => tagSets[t].Contains(n))).ToHashSet();
        var orderedMatches = ordered[sort].Where(matched.Contains).ToArray(); var offset = (page - 1) * size;
        return new(path, new("feed", sort, size, page, categories.Length == 0 && tags.Length == 0 ? "none" : "inspection", "none", "inspection", false, false, 0), orderedMatches.Skip(offset).Take(size).ToArray(), orderedMatches.Length > offset + size, null, 0);
    }

    public RequestPlan(SeedDataset dataset)
    {
        this.dataset = dataset;
        var categories = Enumerable.Range(0, 200).Select(_ => new List<int>()).ToArray();
        var tags = Enumerable.Range(0, 2000).Select(_ => new List<int>()).ToArray();
        for (var n = 0; n < SeedDataset.PublicationCount; n++)
        { foreach (var c in dataset.Categories(n)) { categories[c].Add(n); } foreach (var t in dataset.Tags(n)) { tags[t].Add(n); } }
        categoryMembers = categories.Select(c => c.ToArray()).ToArray(); tagMembers = tags.Select(t => t.ToArray()).ToArray();
        tagSets = tagMembers.Select(t => t.ToHashSet()).ToArray();
        bodies = Enumerable.Range(0, SeedDataset.PublicationCount).GroupBy(dataset.BodyBytes).ToDictionary(g => g.Key, g => g.OrderBy(n => SeedDataset.Id("publication", n)).ToArray());
        ordered = new() { ["recent"] = Order(Enumerable.Range(0, SeedDataset.PublicationCount), "recent"), ["popular"] = Order(Enumerable.Range(0, SeedDataset.PublicationCount), "popular") };
        var cells = new List<(int Page, string Shape, string Tier, string Outcome)>();
        void Cell(int page, string shape, string tier, string outcome, int count)
        { cells.AddRange(Enumerable.Repeat((page, shape, tier, outcome), count)); }
        Cell(1, "C", "p", "nonempty", 6); Cell(1, "C", "r", "nonempty", 1); Cell(1, "T", "p", "nonempty", 1); Cell(1, "T", "m", "nonempty", 1); Cell(1, "T", "r", "nonempty", 1);
        Cell(1, "CT", "m", "zero", 8); Cell(1, "CT", "r", "zero", 7);
        Cell(10, "C", "m", "nonempty", 8); Cell(10, "T", "m", "nonempty", 2); Cell(10, "CT", "p", "nonempty", 10); Cell(10, "T", "m", "zero", 3); Cell(10, "T", "r", "zero", 2);
        Cell(100, "C", "p", "nonempty", 15); Cell(100, "T", "p", "nonempty", 5); Cell(100, "T", "m", "zero", 2); Cell(100, "T", "r", "zero", 3);
        Cell(1000, "C", "p", "beyond", 10); Cell(1000, "C", "m", "beyond", 5); Cell(1000, "C", "r", "beyond", 5); Cell(1000, "T", "p", "beyond", 3); Cell(1000, "T", "m", "beyond", 1); Cell(1000, "T", "r", "beyond", 1);
        var authFeed = AuthSlots(0); var authDetail = AuthSlots(1);
        for (var i = 0; i < 100; i++)
        {
            var page = new[] { 1, 10, 100, 1000 }[i / 25];
            templates.Add(new("feed", i % 4 < 2 ? "recent" : "popular", i % 2 == 0 ? 20 : 100, page, "none", "none", "nonempty", authFeed.Contains(i), false, 0));
        }
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i]; var size = i % 2 == 0 ? 20 : 100;
            templates.Add(new("feed", i % 4 < 2 ? "recent" : "popular", size, cell.Page, cell.Shape, cell.Tier, cell.Outcome, authFeed.Contains(i + 100), false, 0));
            var key = (cell.Shape, cell.Tier, cell.Page, size, cell.Outcome);
            if (!catalog.ContainsKey(key))
            {
                var candidates = Candidates(cell.Shape, cell.Tier).Where(c => cell.Outcome switch
                {
                    "nonempty" => c.Matches.Length > (cell.Page - 1) * size,
                    "zero" => c.Matches.Length == 0,
                    _ => c.Matches.Length > 0 && c.Matches.Length <= (cell.Page - 1) * size,
                }).Take(256).ToArray();
                if (candidates.Length == 0) { throw new InvalidOperationException("No candidate for " + key); }
                catalog.Add(key, candidates);
            }
        }
        for (var i = 0; i < 200; i++)
        { templates.Add(new("detail", "none", 0, 0, "none", "none", "nonempty", authDetail.Contains(i), i % 2 == 1, i < 140 ? 2048 : i < 190 ? 16384 : 131072)); }
        ValidateQuotas();
        foreach (var candidates in catalog.Values)
        { foreach (var candidate in candidates) { foreach (var sort in new[] { "recent", "popular" }) { candidateOrders.TryAdd((candidate, sort), Order(candidate.Matches, sort)); } } }
        TemplateChecksum = BenchmarkManifest.Hash(JsonSerializer.Serialize(templates));
        CatalogChecksum = BenchmarkManifest.Hash(JsonSerializer.Serialize(catalog.OrderBy(p => p.Key).Select(p => new { key = p.Key.ToString(), candidates = p.Value })));
    }
    private static HashSet<int> AuthSlots(int counter) => Enumerable.Range(0, 200).OrderBy(i => Convert.ToHexString(SeedDataset.H("request-auth", i, counter)), StringComparer.Ordinal).Take(20).ToHashSet();
    private static IEnumerable<int> TierIds(string tier, bool category) => (category, tier) switch
    { (true, "p") => Enumerable.Range(0, 10), (true, "m") => Enumerable.Range(10, 40), (true, _) => Enumerable.Range(50, 150), (false, "p") => Enumerable.Range(0, 20), (false, "m") => Enumerable.Range(20, 180), _ => Enumerable.Range(200, 1800) };
    private IEnumerable<FilterCandidate> Candidates(string shape, string tier)
    {
        if (shape == "C")
        {
            var ids = TierIds(tier, true).ToArray();
            foreach (var id in ids) { yield return new([id], [], categoryMembers[id]); }
            for (var i = 0; i < ids.Length; i++)
            { var second = ids[(i + 1) % ids.Length]; yield return new([ids[i], second], [], categoryMembers[ids[i]].Union(categoryMembers[second]).Order().ToArray()); }
            yield break;
        }
        var tagIds = TierIds(tier, false).ToArray();
        var tagChoices = tagIds.Select(id => new[] { id }).Concat(tagIds.Select(id => new[] { id, tagIds[(Array.IndexOf(tagIds, id) + 11) % tagIds.Length] }));
        foreach (var selected in tagChoices)
        {
            var matching = selected.Length == 1 ? tagMembers[selected[0]] : tagMembers[selected[0]].Where(tagSets[selected[1]].Contains).ToArray();
            if (shape == "T") { yield return new([], selected, matching); continue; }
            foreach (var category in TierIds(tier, true))
            { yield return new([category], selected, matching.Intersect(categoryMembers[category]).Order().ToArray()); }
        }
    }
    private int[] Order(IEnumerable<int> source, string sort) => (sort == "popular" ? source.OrderByDescending(dataset.LikeCount).ThenByDescending(dataset.PublishedAt).ThenByDescending(n => SeedDataset.Id("publication", n))
        : source.OrderByDescending(dataset.PublishedAt).ThenByDescending(n => SeedDataset.Id("publication", n))).ToArray();
    public PlannedRequest Get(int block, int slot)
    {
        var template = templates[slot]; var hash = SeedDataset.H("request-choice", block, slot);
        var choice = BinaryPrimitives.ReadUInt32BigEndian(hash); var user = (int)(BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(4)) % 10000);
        if (template.Kind == "detail")
        {
            var pool = bodies[template.BodyBytes]; var n = pool[choice % pool.Length];
            return new(template.Slug ? "/api/v1/publications/by-slug/article-" + n : "/api/v1/publications/" + SeedDataset.Id("publication", n), template, [n], false, n, user);
        }
        var matches = ordered[template.Sort]; var path = $"/api/v1/publications?sort={template.Sort}&page={template.Page}&pageSize={template.Size}";
        if (template.Shape != "none")
        {
            var candidates = catalog[(template.Shape, template.Tier, template.Page, template.Size, template.Outcome)];
            var selected = candidates[choice % candidates.Length]; matches = candidateOrders[(selected, template.Sort)];
            foreach (var c in selected.Categories) { path += "&categoryIds=" + SeedDataset.Id("categories", c); }
            foreach (var t in selected.Tags) { path += "&tagIds=" + SeedDataset.Id("tags", t); }
        }
        var offset = (template.Page - 1) * template.Size;
        return new(path, template, matches.Skip(offset).Take(template.Size).ToArray(), matches.Length > offset + template.Size, null, user);
    }
    public void ValidateResponse(PlannedRequest request, JsonElement json)
    {
        void Demand(bool condition) { if (!condition) { throw new InvalidDataException("Unexpected benchmark response."); } }
        void Publication(JsonElement item, int n, bool detail)
        {
            var allowed = new HashSet<string>(["id", "title", "slug", "description", "coverUrl", "author", "createdAt", "updatedAt", "firstPublishedAt", "categories", "tags", "likeCount", "viewCount", "isLikedByCurrentUser"]);
            if (detail) { allowed.Add("markdown"); }
            Demand(item.EnumerateObject().All(p => allowed.Contains(p.Name)) && item.EnumerateObject().Count() == allowed.Count);
            Demand(item.GetProperty("id").GetGuid() == SeedDataset.Id("publication", n));
            Demand(item.GetProperty("title").GetString() == "Article " + n);
            Demand(item.GetProperty("slug").GetString() == "article-" + n);
            Demand(item.GetProperty("description").ValueKind == JsonValueKind.Null && item.GetProperty("coverUrl").ValueKind == JsonValueKind.Null);
            Demand(item.GetProperty("author").GetProperty("id").GetGuid() == SeedDataset.Id("user", dataset.Rank("authors", n) % 1000));
            foreach (var (field, expected) in new[] { ("createdAt", dataset.PublishedAt(n).AddDays(-1)), ("updatedAt", dataset.PublishedAt(n)), ("firstPublishedAt", dataset.PublishedAt(n)) })
            { Demand(item.GetProperty(field).GetDateTime() == expected && item.GetProperty(field).GetString()!.EndsWith('Z')); }
            foreach (var (kind, ordinals) in new[] { ("categories", dataset.Categories(n)), ("tags", dataset.Tags(n)) })
            {
                var actual = item.GetProperty(kind).EnumerateArray().ToArray();
                var expected = ordinals.OrderBy(t => "Topic " + (t / 2).ToString("D5", System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal).ThenBy(t => SeedDataset.Id(kind, t)).ToArray();
                Demand(actual.Length == expected.Length);
                for (var i = 0; i < expected.Length; i++)
                {
                    var ordinal = expected[i];
                    Demand(actual[i].GetProperty("id").GetGuid() == SeedDataset.Id(kind, ordinal));
                    Demand(actual[i].GetProperty("name").GetString() == "Topic " + (ordinal / 2).ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
                    Demand(actual[i].GetProperty("slug").GetString() == kind + "-" + ordinal);
                    Demand(actual[i].GetProperty("version").GetGuid() == SeedDataset.Id(kind + "-version", ordinal));
                }
            }
            Demand(item.GetProperty("likeCount").GetInt64() == dataset.LikeCount(n));
            Demand(item.GetProperty("viewCount").GetInt64() == 10 + 9 * dataset.LikeCount(n));
            Demand(item.GetProperty("isLikedByCurrentUser").GetBoolean() == (request.Template.Bearer && dataset.LikedBy(n, request.User)));
            Demand(!item.TryGetProperty("version", out _) && !item.TryGetProperty("state", out _));
            if (detail)
            { Demand(item.GetProperty("markdown").GetString() == SeedDataset.Body("body", n, dataset.BodyBytes(n))); }
            else { Demand(!item.TryGetProperty("markdown", out _)); }
        }
        if (request.Publication is { } publication) { Publication(json, publication, true); return; }
        var items = json.GetProperty("items").EnumerateArray().ToArray(); Demand(items.Length == request.Expected.Length);
        Demand(json.GetProperty("page").GetInt32() == request.Template.Page && json.GetProperty("pageSize").GetInt32() == request.Template.Size);
        Demand(json.GetProperty("hasNextPage").GetBoolean() == request.HasNextPage);
        for (var i = 0; i < items.Length; i++) { Publication(items[i], request.Expected[i], false); }
    }
    private void ValidateQuotas()
    {
        var feeds = templates.Where(t => t.Kind == "feed").ToArray(); var filtered = feeds.Where(t => t.Shape != "none").ToArray();
        var detail = templates.Where(t => t.Kind == "detail").ToArray();
        if (templates.Count != 400 || feeds.Length != 200 || filtered.Length != 100 || detail.Length != 200 || templates.Count(t => t.Bearer) != 40
            || filtered.Count(t => t.Shape == "C") != 50 || filtered.Count(t => t.Shape == "T") != 25 || filtered.Count(t => t.Shape == "CT") != 25
            || filtered.Count(t => t.Tier == "p") != 50 || filtered.Count(t => t.Tier == "m") != 30 || filtered.Count(t => t.Tier == "r") != 20
            || filtered.Count(t => t.Outcome == "nonempty") != 50 || filtered.Count(t => t.Outcome == "zero") != 25 || filtered.Count(t => t.Outcome == "beyond") != 25)
        { throw new InvalidOperationException("Invalid joint request quotas."); }
        foreach (var variant in feeds.GroupBy(t => (t.Sort, t.Size, Filtered: t.Shape != "none")))
        { if (variant.Count() != 25 || (variant.Key.Filtered && (!variant.Any(t => t.Outcome == "nonempty") || !variant.Any(t => t.Outcome != "nonempty")))) { throw new InvalidOperationException("A feed variant is missing."); } }
        if (feeds.GroupBy(t => t.Page).Any(g => g.Count() != 50) || detail.Count(t => t.BodyBytes == 2048) != 140 || detail.Count(t => t.BodyBytes == 16384) != 50 || detail.Count(t => t.BodyBytes == 131072) != 10)
        { throw new InvalidOperationException("Invalid page/body quotas."); }
    }
}
