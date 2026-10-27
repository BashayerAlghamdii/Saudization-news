using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;

const string FeedUrl = "https://www.hrsd.gov.sa/rss.xml";
const string InternalHost = "http://backend-prod.backend.svc.cluster.local";
const string PublicHost = "https://www.hrsd.gov.sa";
var cacheTtl = TimeSpan.FromMinutes(15);

// كلمات تدل على أخبار التوطين (عدّلها كما تشاء)
string[] localizationKeywords =
{
    "توطين", "سعودة", "نطاقات", "الكوادر الوطنية", "الكفاءات الوطنية", "السعوديين"
};

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("hrsd", c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/xml, text/xml, */*");
    c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ar,en;q=0.8");
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    ConnectTimeout = TimeSpan.FromSeconds(10),
    SslOptions = new System.Net.Security.SslClientAuthenticationOptions
    {
        EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                            | System.Security.Authentication.SslProtocols.Tls13
    }
});

var app = builder.Build();

app.UseDefaultFiles();   // يخدم wwwroot/index.html
app.UseStaticFiles();

app.MapGet("/api/news", async (IHttpClientFactory http, IMemoryCache cache, ILogger<Program> log, IHostEnvironment env) =>
{
    const string key = "news";
    const string staleKey = "news-stale";

    if (cache.TryGetValue(key, out NewsResponse? cached) && cached is not null)
        return Results.Ok(cached);

    try
    {
        var xml = await http.CreateClient("hrsd").GetStringAsync(FeedUrl);
        var items = ParseFeed(xml, localizationKeywords);
        var result = new NewsResponse(DateTimeOffset.UtcNow, items);

        cache.Set(key, result, cacheTtl);
        cache.Set(staleKey, result); // نسخة احتياطية إن تعذّر الجلب لاحقاً
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "تعذّر جلب خلاصة RSS");
        if (cache.TryGetValue(staleKey, out NewsResponse? stale) && stale is not null)
            return Results.Ok(stale with { IsStale = true });

        var detail = "تعذّر جلب الأخبار من موقع الوزارة. حاول لاحقاً.";
        if (env.IsDevelopment())
            detail += $" [{ex.GetType().Name}] {ex.Message} {ex.InnerException?.Message}";
        return Results.Problem(detail, statusCode: 502);
    }
});

app.Run();

// ---------------------------------------------------------------

static List<NewsItem> ParseFeed(string xml, string[] keywords)
{
    var doc = XDocument.Parse(xml);
    var list = new List<NewsItem>();

    foreach (var item in doc.Descendants("item"))
    {
        var titleEl = item.Element("title");
        if (titleEl is null) continue;

        // العنوان يأتي داخل وسم <a> والرابط الصحيح في href
        var href = titleEl.Descendants("a").FirstOrDefault()?.Attribute("href")?.Value;
        var link = href ?? (item.Element("link")?.Value ?? "").Replace(InternalHost, PublicHost);

        var title = Clean(titleEl.Value);
        var summary = Clean(item.Element("description")?.Value ?? "");
        if (summary.Length > 300) summary = summary[..300].TrimEnd() + "…";

        var isLoc = keywords.Any(k => title.Contains(k) || summary.Contains(k));

        list.Add(new NewsItem(
            Id: item.Element("guid")?.Value ?? link,
            Title: title,
            Summary: summary,
            Link: link,
            Date: ParseDate(item.Element("pubDate")?.Value),
            IsLocalization: isLoc));
    }

    return list;
}

static string Clean(string html)
{
    var text = Regex.Replace(html, "<[^>]+>", " ");
    text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
    return Regex.Replace(text, @"\s+", " ").Trim();
}

// صيغة التاريخ في الخلاصة: "خميس, 10/15/2026 - 16:33" وقد يكون الحقل فارغاً
static DateTimeOffset? ParseDate(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    var m = Regex.Match(raw, @"(\d{2}/\d{2}/\d{4})\s*-\s*(\d{2}:\d{2})");
    if (!m.Success) return null;
    if (DateTime.TryParseExact($"{m.Groups[1].Value} {m.Groups[2].Value}", "MM/dd/yyyy HH:mm",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        return new DateTimeOffset(dt, TimeSpan.FromHours(3)); // توقيت الرياض
    return null;
}

record NewsItem(string Id, string Title, string Summary, string Link, DateTimeOffset? Date, bool IsLocalization);
record NewsResponse(DateTimeOffset FetchedAt, List<NewsItem> Items, bool IsStale = false);