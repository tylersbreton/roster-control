using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RosterControl.Core.Abstractions;
using RosterControl.Core.GameData;
using RosterControl.Core.Rules;
using RosterControl.Integrations;

namespace RosterControl.Tests;

/// <summary>Serves canned responses by URL and records requests (no network).</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);
    public List<HttpRequestMessage> Requests { get; } = [];
    public bool Offline { get; set; }

    public void Json(string url, string json) => Routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
    public void File(string url, string fixture) => Json(url, System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)));
    public int Count(string fragment) => Requests.Count(r => r.RequestUri!.ToString().Contains(fragment, StringComparison.Ordinal));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (Offline) throw new HttpRequestException("offline");
        var url = request.RequestUri!.ToString();
        return Task.FromResult(Routes.TryGetValue(url, out var route) ? route(request) : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public class CachingTests
{
    private string _root = "";
    private AppPaths _paths = null!;
    private FakeHttp _http = null!;

    private const string Dd = "https://ddragon.leagueoflegends.com/";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "rostercontrol-cache-tests-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _http = new FakeHttp();
    }

    [TearDown]
    public void TearDown()
    {
        _http.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private GameDataClient Client() => new(new HttpClient(_http), _paths, NullLogger<GameDataClient>.Instance);
    private AssetCache Images() => new(new HttpClient(_http), _paths, NullLogger<AssetCache>.Instance);
    private GameDataService Service() => new(Client(), Images(), NullLogger<GameDataService>.Instance);

    /// <summary>Two patches; the newer one adds Ambessa (simulated by removing her from the older champion list).</summary>
    private void ServeTwoPatches(string newest = "16.24.1", string previous = "16.23.1")
    {
        _http.Json(Dd + "api/versions.json", JsonSerializer.Serialize(new[] { newest, previous, "16.22.1" }));
        foreach (var v in new[] { newest, previous })
            foreach (var f in new[] { "item.json", "runesReforged.json", "summoner.json" })
                _http.File($"{Dd}cdn/{v}/data/en_US/{f}", f);
        _http.Json($"{Dd}cdn/{newest}/data/en_US/champion.json", WithExtraChampion("Ambessa"));
        _http.File($"{Dd}cdn/{previous}/data/en_US/champion.json", "champion.json");
        _http.File(GameDataClient.CommunityDragonBase + "perkstyles.json", "perkstyles.json");
        _http.File(GameDataClient.CommunityDragonBase + "perks.json", "perks.json");
        _http.Json(GameDataClient.MerakiChampions.ToString(), """{"Ahri":{"positions":["MIDDLE"]},"Darius":{"positions":["TOP"]}}""");
    }

    private static string WithExtraChampion(string id)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "champion.json")))!;
        var ahri = doc["data"]!["Ahri"]!.DeepClone();
        ahri["id"] = id;
        ahri["name"] = id;
        doc["data"]![id] = ahri;
        return doc.ToJsonString();
    }

    // ── Portable data location ─────────────────────────────────────────────────────────────────────

    [Test]
    public void Self_updating_releases_keep_data_outside_the_replaced_app_folder()
    {
        var root = Path.Combine(_root, "Roster Control");
        var current = Path.Combine(root, "current");
        Directory.CreateDirectory(current);
        Assert.That(AppPaths.PortableRoot(current + Path.DirectorySeparatorChar), Is.EqualTo(current), "plain folder named current");
        File.WriteAllText(Path.Combine(root, "Update.exe"), "");
        Assert.That(AppPaths.PortableRoot(current + Path.DirectorySeparatorChar), Is.EqualTo(root), "Velopack layout: data goes to the root");
        Assert.That(AppPaths.PortableRoot(root), Is.EqualTo(root));
    }

    // ── Item changes between patches ───────────────────────────────────────────────────────────────

    private static ItemInfo Item(int gold, string description = "<stats>40 AD</stats>", string name = "Sword", params string[] from) =>
        new("1", name, description, "", gold, [], from, [], "1.png", true, true, ItemTier.Legendary);

    [Test]
    public void Item_changes_are_described()
    {
        Assert.That(ItemCatalog.DescribeChange(Item(3000), Item(3000)), Is.Null);
        Assert.That(ItemCatalog.DescribeChange(Item(3000, "<stats>40  AD</stats>"), Item(3000, "<stats>40 AD</stats>")), Is.Null, "whitespace only");
        Assert.That(ItemCatalog.DescribeChange(Item(3000), Item(3100)), Is.EqualTo("Cost 3,000 → 3,100"));
        Assert.That(ItemCatalog.DescribeChange(Item(3000), Item(3000, "<stats>45 AD</stats>")), Is.EqualTo("Stats or effects changed"));
        Assert.That(ItemCatalog.DescribeChange(Item(3000, from: ["1036", "1037"]), Item(3000, from: ["1037", "1036"])), Is.Null, "recipe order only");
        Assert.That(ItemCatalog.DescribeChange(Item(3000, from: ["1036"]), Item(3000, from: ["1038"])), Is.EqualTo("Recipe changed"));
        Assert.That(ItemCatalog.DescribeChange(null, Item(3000)), Is.Null, "nothing to compare with");
    }

    // ── Images ─────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Versioned_and_unversioned_image_urls_are_recognised()
    {
        Assert.That(AssetCache.IsVersioned(AssetUrls.Item("16.23.1", "3031")), Is.True);
        Assert.That(AssetCache.IsVersioned(AssetUrls.ChampionSquare("16.23.1", "Ahri.png")), Is.True);
        Assert.That(AssetCache.IsVersioned(AssetUrls.ChampionCentered("Ahri")), Is.False);
        Assert.That(AssetCache.IsVersioned(AssetUrls.Rune("perk-images/Styles/Domination/Electrocute/Electrocute.png")), Is.False);
        Assert.That(AssetCache.IsVersioned(AssetUrls.PositionIcon(Core.Model.Position.Top)), Is.False);
    }

    [Test]
    public async Task Old_patch_images_are_pruned()
    {
        var cache = Images();
        foreach (var v in new[] { "16.24.1", "16.23.1", "16.20.1" })
        {
            var url = AssetUrls.Item(v, "3031");
            _http.Routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            Assert.That(await cache.GetAsync(url, CancellationToken.None), Is.Not.Null);
        }
        Assert.That(cache.PruneVersions(["16.24.1", "16.23.1"]), Is.EqualTo(1));
        Assert.That(cache.TryGetCachedPath(AssetUrls.Item("16.20.1", "3031")), Is.Null);
        Assert.That(cache.TryGetCachedPath(AssetUrls.Item("16.24.1", "3031")), Is.Not.Null);
    }

    [Test]
    public async Task Stale_unversioned_images_are_revalidated_with_their_etag()
    {
        var url = AssetUrls.ChampionCentered("Ahri");
        var served = 0;
        _http.Routes[url] = request =>
        {
            if (request.Headers.IfNoneMatch.Any(t => t.Tag == "\"v1\"")) return new HttpResponseMessage(HttpStatusCode.NotModified);
            served++;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return response;
        };
        var cache = Images();
        var path = await cache.GetAsync(url, CancellationToken.None);
        Assert.That(path, Is.Not.Null);
        Assert.That(served, Is.EqualTo(1));

        // Fresh: served from disk without any request.
        var before = _http.Requests.Count;
        Assert.That(cache.TryGetCachedPath(url), Is.EqualTo(path));
        Assert.That(_http.Requests.Count, Is.EqualTo(before));

        // Stale: the cached file is returned immediately and revalidated in the background (304 → kept, clock reset).
        File.SetLastWriteTimeUtc(path!, DateTime.UtcNow - AssetCache.UnversionedMaxAge - TimeSpan.FromDays(1));
        Assert.That(cache.TryGetCachedPath(url), Is.EqualTo(path));
        for (var i = 0; i < 100 && DateTime.UtcNow - File.GetLastWriteTimeUtc(path!) > TimeSpan.FromMinutes(1); i++) await Task.Delay(20);
        Assert.That(DateTime.UtcNow - File.GetLastWriteTimeUtc(path!), Is.LessThan(TimeSpan.FromMinutes(1)), "revalidated");
        Assert.That(served, Is.EqualTo(1), "304: the image was not downloaded again");
        Assert.That(File.ReadAllBytes(path!), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public async Task Versioned_images_are_never_revalidated()
    {
        var url = AssetUrls.Item("16.23.1", "3031");
        _http.Routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        var cache = Images();
        var path = await cache.GetAsync(url, CancellationToken.None);
        File.SetLastWriteTimeUtc(path!, DateTime.UtcNow - TimeSpan.FromDays(400));
        var before = _http.Requests.Count;
        Assert.That(cache.TryGetCachedPath(url), Is.EqualTo(path));
        await Task.Delay(100);
        Assert.That(_http.Requests.Count, Is.EqualTo(before));
    }

    // ── Champion positions ─────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Positions_are_refreshed_when_a_new_champion_is_missing_from_them()
    {
        _http.Json(GameDataClient.MerakiChampions.ToString(), """{"Ahri":{"positions":["MIDDLE"]}}""");
        var client = Client();
        await client.GetPositionsAsync(false, ["Ahri"], CancellationToken.None);
        Assert.That(_http.Count("merakianalytics"), Is.EqualTo(1));

        // Cached and complete: no download.
        await client.GetPositionsAsync(false, ["Ahri"], CancellationToken.None);
        Assert.That(_http.Count("merakianalytics"), Is.EqualTo(1));

        // A new champion appears, but the cache was refreshed recently: wait (Meraki may not have it yet).
        await client.GetPositionsAsync(false, ["Ahri", "Ambessa"], CancellationToken.None);
        Assert.That(_http.Count("merakianalytics"), Is.EqualTo(1));

        // After the retry window the positions are downloaded again.
        File.SetLastWriteTimeUtc(Path.Combine(client.CacheRoot, "positions.json"), DateTime.UtcNow - TimeSpan.FromHours(13));
        _http.Json(GameDataClient.MerakiChampions.ToString(), """{"Ahri":{"positions":["MIDDLE"]},"Ambessa":{"positions":["TOP"]}}""");
        var map = await client.GetPositionsAsync(false, ["Ahri", "Ambessa"], CancellationToken.None);
        Assert.That(_http.Count("merakianalytics"), Is.EqualTo(2));
        Assert.That(map["Ambessa"], Is.EqualTo(new[] { "TOP" }));
    }

    // ── Patches ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Newest_patch_is_loaded_new_champions_are_detected_and_old_patches_pruned()
    {
        Directory.CreateDirectory(Path.Combine(_paths.CacheDirectory, "data", "16.10.1", "en_US"));
        ServeTwoPatches();
        var service = Service();
        var game = await service.LoadAsync(false, null, CancellationToken.None);
        Assert.That(game.Version, Is.EqualTo("16.24.1"));
        Assert.That(service.Status!.Source, Is.EqualTo(GameDataSource.Online));
        Assert.That(game.NewChampionIds, Is.EquivalentTo(new[] { "Ambessa" }));
        Assert.That(game.ShardRows, Has.Count.EqualTo(3));
        Assert.That(Directory.Exists(Path.Combine(_paths.CacheDirectory, "data", "16.10.1")), Is.False, "old patch pruned");
        Assert.That(Directory.Exists(Path.Combine(_paths.CacheDirectory, "data", "16.23.1")), Is.True, "previous patch kept");
    }

    [Test]
    public async Task Patch_data_is_downloaded_once_and_the_patch_list_is_always_rechecked()
    {
        ServeTwoPatches();
        await Service().LoadAsync(false, null, CancellationToken.None);
        var itemDownloads = _http.Count("16.24.1/data/en_US/item.json");
        await Service().LoadAsync(false, null, CancellationToken.None);
        Assert.That(_http.Count("16.24.1/data/en_US/item.json"), Is.EqualTo(itemDownloads), "immutable per patch");
        Assert.That(_http.Count("api/versions.json"), Is.EqualTo(2), "patch list checked every load");
        Assert.That(_http.Count("perkstyles.json"), Is.EqualTo(1), "shards cached within the same patch");
    }

    [Test]
    public async Task Stat_shards_are_refreshed_when_the_patch_changes()
    {
        ServeTwoPatches("16.23.1", "16.22.1");
        _http.File($"{Dd}cdn/16.22.1/data/en_US/champion.json", "champion.json");
        await Service().LoadAsync(false, null, CancellationToken.None);
        ServeTwoPatches("16.24.1", "16.23.1");
        await Service().LoadAsync(false, null, CancellationToken.None);
        Assert.That(_http.Count("perkstyles.json"), Is.EqualTo(2));
    }

    [Test]
    public async Task Offline_start_uses_the_cache_and_reports_it()
    {
        ServeTwoPatches();
        await Service().LoadAsync(false, null, CancellationToken.None);
        _http.Offline = true;
        var service = Service();
        var game = await service.LoadAsync(false, null, CancellationToken.None);
        Assert.That(game.Version, Is.EqualTo("16.24.1"));
        Assert.That(service.Status!.Source, Is.EqualTo(GameDataSource.Cache));
        Assert.That(await service.CheckForNewPatchAsync(CancellationToken.None), Is.Null, "no false alarms offline");
    }

    [Test]
    public async Task A_new_patch_released_while_running_is_detected()
    {
        ServeTwoPatches();
        var service = Service();
        await service.LoadAsync(false, null, CancellationToken.None);
        Assert.That(await service.CheckForNewPatchAsync(CancellationToken.None), Is.Null);
        _http.Json(Dd + "api/versions.json", """["16.25.1","16.24.1"]""");
        Assert.That(await service.CheckForNewPatchAsync(CancellationToken.None), Is.EqualTo("16.25.1"));
        Assert.That(GameDataService.IsNewer("16.10.1", "16.9.1"), Is.True, "numeric, not alphabetical");
    }

    [Test]
    public async Task Items_of_an_older_patch_can_be_loaded_for_comparison()
    {
        ServeTwoPatches();
        var service = Service();
        await service.LoadAsync(false, null, CancellationToken.None);
        var old = await service.GetItemsForVersionAsync("16.23.1", CancellationToken.None);
        Assert.That(old!["3031"].Name, Is.EqualTo("Infinity Edge"));
        Assert.That(await service.GetItemsForVersionAsync("9.9.9", CancellationToken.None), Is.Null);
    }
}
