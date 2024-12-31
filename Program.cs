using Abot2.Core;
using Abot2.Poco;
using AngleSharp;
using Newtonsoft.Json;
using Serilog;
using System.Diagnostics;
using Bogus;
using AngleSharp.Dom;
using System.CommandLine;

public class Program
{
    public class Article
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public int TotalLikes { get; set; }
    }

    public class ArticleRanking
    {
        private List<Article> _articles = new List<Article>();

        public void AddArticle(Article article)
        {
            _articles.Add(article);
            Log.Information($"Added article: {article.TotalLikes} - {article.Title} - {article.Url}");
        }

        public List<Article> GetTopArticles()
        {
            return _articles.OrderByDescending(a => a.TotalLikes).ToList();
        }
    }

    public class VnExpressCategory
    {
        public string Name { get; set; }
        public int Id { get; set; }
        public string ClassName { get; set; }
        public string ShareUrl { get; set; }
    }

    public class BaseCrawler
    {
        protected readonly ArticleRanking _ranking;
        protected readonly DateTime _startDate;
        protected readonly DateTime _endDate;
        protected readonly IConfiguration _config;
        protected readonly IBrowsingContext _context;

        public BaseCrawler(ArticleRanking ranking, DateTime startDate, DateTime endDate)
        {
            _ranking = ranking;
            _startDate = startDate;
            _endDate = endDate;
            _config = Configuration.Default.WithDefaultLoader();
            _context = BrowsingContext.New(_config);
        }

        protected CrawlConfiguration GetCrawlConfig()
        {
            return new CrawlConfiguration
            {
                HttpServicePointConnectionLimit = 128,
            };
        }

        public PageRequester GetPagerRequester()
        {
            var faker = new Faker();
            var crawlConfig = GetCrawlConfig();
            crawlConfig.UserAgentString = faker.Internet.UserAgent();
            return new PageRequester(crawlConfig, new WebContentExtractor());
        }
    }

    public class VnExpressCrawler : BaseCrawler
    {
        private const string BaseUrl = "https://vnexpress.net";
        private const string CommentApiUrl = "https://usi-saas.vnexpress.net/index/get";
        private readonly SemaphoreSlim _pageSemaphore;
        private readonly SemaphoreSlim _categorySemaphore;
        private readonly int _maxConcurrentArticles;

        public VnExpressCrawler(ArticleRanking ranking, DateTime startDate, DateTime endDate,
            int maxConcurrentCategories = 1,
            int maxConcurrentPages = 1,
            int maxConcurrentArticles = 3)
            : base(ranking, startDate, endDate)
        {
            _categorySemaphore = new SemaphoreSlim(maxConcurrentCategories);
            _pageSemaphore = new SemaphoreSlim(maxConcurrentPages);
            _maxConcurrentArticles = maxConcurrentArticles;
        }

        public async Task CrawlAsync()
        {
            try
            {
                var categories = GetCategories();
                var categoryTasks = categories.Select(ProcessCategoryWithSemaphoreAsync);
                await Task.WhenAll(categoryTasks);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in main crawl process");
                throw;
            }
        }

        private async Task ProcessCategoryWithSemaphoreAsync(VnExpressCategory category)
        {
            await _categorySemaphore.WaitAsync();
            try
            {
                await ProcessCategoryAsync(category);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error processing category {category.Id}");
            }
            finally
            {
                _categorySemaphore.Release();
            }
        }

        private async Task ProcessCategoryAsync(VnExpressCategory category)
        {
            var startDateUnix = ((DateTimeOffset)_startDate).ToUnixTimeSeconds();
            var endDateUnix = ((DateTimeOffset)_endDate).ToUnixTimeSeconds();
            var pageProcessingTasks = new List<Task>();
            var pageNumber = 1;

            while (true)
            {
                await _pageSemaphore.WaitAsync();
                try
                {
                    var url =
                        $"{BaseUrl}/category/day/cateid/{category.Id}/fromdate/{startDateUnix}/todate/{endDateUnix}/allcate/0/page/{pageNumber}";
                    pageProcessingTasks.Add(ProcessPageAsync(url, category.Id, pageNumber));

                    var (exists, _) =
                        await CheckNextPageExistsAsync(category.Id, startDateUnix, endDateUnix, pageNumber + 1);
                    if (!exists) break;

                    pageNumber++;
                }
                finally
                {
                    _pageSemaphore.Release();
                }
            }

            await Task.WhenAll(pageProcessingTasks);
        }

        private async Task ProcessPageAsync(string url, int categoryId, int pageNumber)
        {
            try
            {
                var stopwatchPage = Stopwatch.StartNew();
                var pageRequester = GetPagerRequester();
                var crawledPage = await pageRequester.MakeRequestAsync(new Uri(url));

                if (!crawledPage.HttpResponseMessage.IsSuccessStatusCode)
                {
                    Log.Warning(
                        $"Failed to process page {pageNumber} for category {categoryId}. Status code: {crawledPage.HttpResponseMessage.StatusCode}");
                    return;
                }

                var document = await _context.OpenAsync(req => req.Content(crawledPage.Content.Text));
                var articles = document.QuerySelectorAll("article.item-news.item-news-common");

                if (articles.Length > 0)
                {
                    await ProcessArticlesInParallelAsync(articles, categoryId);
                }

                Log.Debug(
                    $"Processed page {pageNumber} for category {categoryId} in {stopwatchPage.Elapsed.TotalMilliseconds} ms");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error processing page {pageNumber} for category {categoryId}");
                throw;
            }
        }

        private async Task ProcessArticlesInParallelAsync(IHtmlCollection<IElement> articles, int categoryId)
        {
            await Parallel.ForEachAsync(
                articles,
                new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrentArticles },
                async (article, ct) =>
                {
                    var linkElement = article.QuerySelector("a");
                    var articleUrl = linkElement?.GetAttribute("href");
                    var title = linkElement?.GetAttribute("title");

                    if (string.IsNullOrEmpty(articleUrl) || string.IsNullOrEmpty(title)) return;

                    var stopwatchArticle = Stopwatch.StartNew();
                    try
                    {
                        await ProcessArticle(articleUrl, title);
                        Log.Debug(
                            $"Processed article '{title}' in category {categoryId} in {stopwatchArticle.Elapsed.TotalMilliseconds} ms");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Error processing article {title} in category {categoryId}");
                    }
                });
        }

        private async Task ProcessArticle(string url, string title)
        {
            var document = await _context.OpenAsync(url);
            if (document == null)
            {
                Log.Error("Failed to load document from {Url}", url);
                return;
            }

            var commentSection = document.QuerySelector("span.number_cmt.txt_num_comment.num_cmt_detail");
            if (commentSection == null)
            {
                Log.Warning("No comment section found for {Url}", url);
                AddArticleToRanking(title, url, 0);
                return;
            }

            var objectId = commentSection.GetAttribute("data-objectid");
            var objectType = commentSection.GetAttribute("data-objecttype");

            if (!string.IsNullOrEmpty(objectId) && !string.IsNullOrEmpty(objectType))
            {
                var stopwatchComment = Stopwatch.StartNew();
                await ProcessComments(objectId, objectType, title, url);
                Log.Debug(
                    $"Processed comments for article '{title}' in {stopwatchComment.Elapsed.TotalMilliseconds} ms.");
            }
        }

        private async Task ProcessComments(string objectId, string objectType, string title, string url, int offset = 0,
            int limit = 1000,
            int totalLikes = 0)
        {
            var apiUrl =
                $"{CommentApiUrl}?offset={offset}&limit={limit}&sort_by=like&objectid={objectId}&objecttype={objectType}&siteid=1000000";

            var pageRequester = GetPagerRequester();
            var crawledPage = await pageRequester.MakeRequestAsync(new Uri(apiUrl));

            if (string.IsNullOrEmpty(crawledPage.Content.Text))
            {
                Log.Error("Empty response from VnExpress API for url {Url}", url);
                return;
            }

            var commentData = JsonConvert.DeserializeObject<CommentResponse>(crawledPage.Content.Text);
            if (commentData?.Data?.Items == null || commentData.Data.Items.Count == 0)
            {
                AddArticleToRanking(title, url, totalLikes);
                return;
            }

            var currentPageLikes = commentData.Data.Items.Sum(comment => comment.UserLike);
            if (commentData.Data.Items.Count == limit)
            {
                await ProcessComments(objectId, objectType, title, url, offset + limit, limit + 1000,
                    totalLikes + currentPageLikes);
                return;
            }

            AddArticleToRanking(title, url, totalLikes + currentPageLikes);
        }

        private async Task<(bool exists, string content)> CheckNextPageExistsAsync(
            int categoryId,
            long startDateUnix,
            long endDateUnix,
            int nextPageNumber)
        {
            var nextUrl =
                $"{BaseUrl}/category/day/cateid/{categoryId}/fromdate/{startDateUnix}/todate/{endDateUnix}/allcate/0/page/{nextPageNumber}";
            var pageRequester = GetPagerRequester();
            var response = await pageRequester.MakeRequestAsync(new Uri(nextUrl));

            if (!response.HttpResponseMessage.IsSuccessStatusCode)
            {
                return (false, null);
            }

            var document = await _context.OpenAsync(req => req.Content(response.Content.Text));
            var hasArticles = document.QuerySelectorAll("article.item-news.item-news-common").Length > 0;

            return (hasArticles, response.Content.Text);
        }

        private void AddArticleToRanking(string title, string url, int totalLikes)
        {
            _ranking.AddArticle(new Article
            {
                Title = title,
                Url = url,
                TotalLikes = totalLikes
            });
        }

        private class CommentResponse
        {
            public CommentData Data { get; set; }
        }

        private class CommentData
        {
            [JsonProperty("items")] public List<Comment> Items { get; set; }
        }

        private class Comment
        {
            [JsonProperty("userlike")] public int UserLike { get; set; }
        }

        private List<VnExpressCategory> GetCategories()
        {
            return new List<VnExpressCategory>
            {
                new VnExpressCategory { Name = "Thời sự", Id = 1001005, ClassName = "thoisu", ShareUrl = "/thoi-su" },
                new VnExpressCategory
                    { Name = "Góc nhìn", Id = 1003450, ClassName = "gocnhin", ShareUrl = "/goc-nhin" },
                new VnExpressCategory
                    { Name = "Thế giới", Id = 1001002, ClassName = "thegioi", ShareUrl = "/the-gioi" },
                new VnExpressCategory
                    { Name = "Kinh doanh", Id = 1003159, ClassName = "kinhdoanh", ShareUrl = "/kinh-doanh" },
                new VnExpressCategory
                    { Name = "Podcasts", Id = 1004685, ClassName = "podcasts", ShareUrl = "/podcast" },
                new VnExpressCategory
                    { Name = "Bất động sản", Id = 1005628, ClassName = "kinhdoanh", ShareUrl = "/bat-dong-san" },
                new VnExpressCategory
                    { Name = "Khoa học", Id = 1001009, ClassName = "khoahoc", ShareUrl = "/khoa-hoc" },
                new VnExpressCategory
                    { Name = "Giải trí", Id = 1002691, ClassName = "giaitri", ShareUrl = "/giai-tri" },
                new VnExpressCategory
                    { Name = "Thể thao", Id = 1002565, ClassName = "thethao", ShareUrl = "/the-thao" },
                new VnExpressCategory
                    { Name = "Pháp luật", Id = 1001007, ClassName = "phapluat", ShareUrl = "/phap-luat" },
                new VnExpressCategory
                    { Name = "Giáo dục", Id = 1003497, ClassName = "giaoduc", ShareUrl = "/giao-duc" },
                new VnExpressCategory
                    { Name = "Sức khỏe", Id = 1003750, ClassName = "suckhoe", ShareUrl = "/suc-khoe" },
                new VnExpressCategory
                    { Name = "Đời sống", Id = 1002966, ClassName = "doisong", ShareUrl = "/doi-song" },
                new VnExpressCategory { Name = "Du lịch", Id = 1003231, ClassName = "dulich", ShareUrl = "/du-lich" },
                new VnExpressCategory { Name = "Số hóa", Id = 1002592, ClassName = "sohoa", ShareUrl = "/so-hoa" },
                new VnExpressCategory { Name = "Xe", Id = 1001006, ClassName = "xe", ShareUrl = "/oto-xe-may" },
                new VnExpressCategory { Name = "Ý kiến", Id = 1001012, ClassName = "ykien", ShareUrl = "/y-kien" },
                new VnExpressCategory { Name = "Tâm sự", Id = 1001014, ClassName = "tamsu", ShareUrl = "/tam-su" },
                new VnExpressCategory { Name = "Thư giãn", Id = 1001011, ClassName = "cuoi", ShareUrl = "/thu-gian" },
            };
        }
    }

    public class TuoiTreCrawler : BaseCrawler
    {
        private const string BaseUrl = "https://tuoitre.vn";
        private const string CommentApiUrl = "https://id.tuoitre.vn/api/getlist-comment.api";
        private readonly SemaphoreSlim _semaphore;
        private readonly int _maxConcurrentArticles;

        public TuoiTreCrawler(ArticleRanking ranking, DateTime startDate, DateTime endDate,
            int maxConcurrentPages = 2,
            int maxConcurrentArticles = 5)
            : base(ranking, startDate, endDate)
        {
            _semaphore = new SemaphoreSlim(maxConcurrentPages);
            _maxConcurrentArticles = maxConcurrentArticles;
        }

        public async Task CrawlAsync()
        {
            var currentDate = _startDate;
            while (currentDate <= _endDate)
            {
                try
                {
                    var dateStr = currentDate.ToString("dd-MM-yyyy");
                    await ProcessDateAsync(dateStr);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Error processing date {currentDate:dd-MM-yyyy}");
                }

                currentDate = currentDate.AddDays(1);
            }
        }

        private async Task ProcessDateAsync(string dateStr)
        {
            var pageNumber = 1;
            var pageProcessingTasks = new List<Task>();

            while (true)
            {
                await _semaphore.WaitAsync();
                try
                {
                    var url = $"{BaseUrl}/timeline-xem-theo-ngay/0/{dateStr}/trang-{pageNumber}.htm";
                    pageProcessingTasks.Add(ProcessPageAsync(url, pageNumber));

                    var (exists, _) = await CheckNextPageExistsAsync(dateStr, pageNumber + 1);
                    if (!exists) break;

                    pageNumber++;
                }
                finally
                {
                    _semaphore.Release();
                }
            }

            await Task.WhenAll(pageProcessingTasks);
        }

        private async Task ProcessPageAsync(string url, int pageNumber)
        {
            try
            {
                var stopwatchPage = Stopwatch.StartNew();
                var pageRequester = GetPagerRequester();
                var crawledPage = await pageRequester.MakeRequestAsync(new Uri(url));

                if (!crawledPage.HttpResponseMessage.IsSuccessStatusCode)
                {
                    Log.Warning(
                        $"Failed to process page {pageNumber}. Status code: {crawledPage.HttpResponseMessage.StatusCode}");
                    return;
                }

                var document = await _context.OpenAsync(req => req.Content(crawledPage.Content.Text));
                var articles = document.QuerySelectorAll("li.news-item");
                await ProcessArticlesInParallelAsync(articles);

                Log.Debug(
                    $"Processed page {pageNumber} with {articles.Length} articles in {stopwatchPage.Elapsed.TotalMilliseconds} ms");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"Error processing page {pageNumber}");
                throw;
            }
        }

        private async Task ProcessArticlesInParallelAsync(IHtmlCollection<IElement> articles)
        {
            await Parallel.ForEachAsync(
                articles,
                new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrentArticles },
                async (article, ct) =>
                {
                    var linkElement = article.QuerySelector("a");
                    var articleUrl = linkElement?.GetAttribute("href");
                    var title = linkElement?.GetAttribute("title");

                    if (string.IsNullOrEmpty(articleUrl) || string.IsNullOrEmpty(title)) return;

                    var stopwatchArticle = Stopwatch.StartNew();
                    try
                    {
                        var fullUrl = $"{BaseUrl}{articleUrl}";
                        await ProcessArticle(fullUrl, title);
                        Log.Debug($"Processed article '{title}' in {stopwatchArticle.Elapsed.TotalMilliseconds} ms");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"Error processing article {title}");
                    }
                });
        }

        private async Task<(bool exists, string content)> CheckNextPageExistsAsync(string dateStr, int nextPageNumber)
        {
            var nextUrl = $"{BaseUrl}/timeline-xem-theo-ngay/0/{dateStr}/trang-{nextPageNumber}.htm";
            var pageRequester = GetPagerRequester();
            var response = await pageRequester.MakeRequestAsync(new Uri(nextUrl));

            if (!response.HttpResponseMessage.IsSuccessStatusCode)
            {
                return (false, null);
            }

            var document = await _context.OpenAsync(req => req.Content(response.Content.Text));
            var hasArticles = document.QuerySelectorAll("li.news-item").Length > 0;

            return (hasArticles, response.Content.Text);
        }

        private async Task ProcessArticle(string url, string title)
        {
            var pageRequester = GetPagerRequester();
            var response = await pageRequester.MakeRequestAsync(new Uri(url));

            if (string.IsNullOrEmpty(response.Content.Text))
            {
                Log.Error("Empty response from TuoiTre for url {Url}", url);
                return;
            }

            var document = await _context.OpenAsync(req => req.Content(response.Content.Text));
            var commentSection = document.QuerySelector("section.comment-wrapper");

            if (commentSection == null)
            {
                Log.Warning("No comment section found for {Url}", url);
                AddArticleToRanking(title, url, 0);
                return;
            }

            var objectId = commentSection.GetAttribute("data-objectid");
            var objectType = commentSection.GetAttribute("data-objecttype");

            if (string.IsNullOrEmpty(objectId) || string.IsNullOrEmpty(objectType)) return;

            var stopwatchComment = Stopwatch.StartNew();
            await ProcessComments(objectId, objectType, title, url);
            Log.Debug($"Processed comments for article '{title}' in {stopwatchComment.Elapsed.TotalMilliseconds} ms.");
        }

        private async Task ProcessComments(string objectId, string objectType, string title, string url, int page = 1,
            int totalLikes = 0)
        {
            var apiUrl = $"{CommentApiUrl}?pageindex={page}&objId={objectId}&objType={objectType}&sort=2";
            var pageRequester = GetPagerRequester();
            var response = await pageRequester.MakeRequestAsync(new Uri(apiUrl));

            if (string.IsNullOrEmpty(response.Content.Text))
            {
                Log.Error("Empty response from TuoiTre API for url {Url}", apiUrl);
                AddArticleToRanking(title, url, totalLikes);
                return;
            }

            var commentData = JsonConvert.DeserializeObject<CommentResponse>(response.Content.Text);
            if (commentData?.Data == null)
            {
                Log.Warning("No comment data found for {Url}", url);
                AddArticleToRanking(title, url, totalLikes);
                return;
            }

            var comments = JsonConvert.DeserializeObject<List<Comment>>(commentData.Data);
            if (comments == null || comments.Count == 0)
            {
                AddArticleToRanking(title, url, totalLikes);
                return;
            }

            var currentPageLikes = comments.Sum(c => c.Reactions?.Values.Sum() ?? 0);
            await ProcessComments(objectId, objectType, title, url, page + 1, totalLikes + currentPageLikes);
        }

        private void AddArticleToRanking(string title, string url, int totalLikes)
        {
            _ranking.AddArticle(new Article
            {
                Title = title,
                Url = url,
                TotalLikes = totalLikes
            });
        }

        private class CommentResponse
        {
            public string Data { get; set; }
        }

        private class Comment
        {
            public Dictionary<string, int> Reactions { get; set; }
        }
    }

    private static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();
    }

    private static Command ConfigureCrawlerOptions()
    {
        var crawlerCommand = new Command("crawl", "Crawl articles from specified news source");

        var crawlerTypeOption = new Option<string>(
            aliases: new[] { "--type", "-t" },
            getDefaultValue: () => "vnexpress",
            description: "Type of crawler ('vnexpress' or 'tuoitre')");

        var startDateOption = new Option<DateTime?>(
            aliases: new[] { "--start", "-s" },
            description: "Start date for crawling (YYYY-MM-DD)");

        var endDateOption = new Option<DateTime?>(
            aliases: new[] { "--end", "-e" },
            description: "End date for crawling (YYYY-MM-DD)");

        crawlerCommand.AddOption(crawlerTypeOption);
        crawlerCommand.AddOption(startDateOption);
        crawlerCommand.AddOption(endDateOption);

        return crawlerCommand;
    }

    private static async Task ExecuteCrawlerAsync(string type, DateTime? startDate, DateTime? endDate)
    {
        var vietnamZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
        var currentDate = TimeZoneInfo.ConvertTime(DateTime.Now, vietnamZone);

        var resolvedEndDate = endDate ?? currentDate;
        var resolvedStartDate = startDate ?? resolvedEndDate.AddDays(-7);

        resolvedStartDate = NormalizeDateTime(resolvedStartDate, isStartOfDay: true);
        resolvedEndDate = NormalizeDateTime(resolvedEndDate, isStartOfDay: false);

        if (!ValidateCrawlerType(type))
        {
            return;
        }

        await ExecuteCrawlingProcess(type.ToLower(), resolvedStartDate, resolvedEndDate);
    }

    private static DateTime NormalizeDateTime(DateTime date, bool isStartOfDay)
    {
        return isStartOfDay
            ? new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified)
            : new DateTime(date.Year, date.Month, date.Day, 23, 59, 59, 999, DateTimeKind.Unspecified);
    }

    private static bool ValidateCrawlerType(string type)
    {
        if (type.ToLower() is not ("vnexpress" or "tuoitre"))
        {
            Log.Error("Invalid crawler type. Please specify 'vnexpress' or 'tuoitre'.");
            return false;
        }

        return true;
    }

    private static async Task ExecuteCrawlingProcess(string crawlerType, DateTime startDate, DateTime endDate)
    {
        Log.Information($"Crawl from {startDate} to {endDate} using {crawlerType} crawler.");

        var ranking = new ArticleRanking();
        var stopwatch = Stopwatch.StartNew();

        var crawler = CreateCrawler(crawlerType, ranking, startDate, endDate);
        if (crawler != null)
        {
            await crawler.CrawlAsync();
            stopwatch.Stop();
            Log.Information($"Crawling completed in {stopwatch.Elapsed.TotalSeconds} seconds.");

            var topArticles = ranking.GetTopArticles();
            Console.WriteLine($"Total articles: {topArticles.Count}");

            foreach (var article in topArticles.Take(10))
            {
                Console.WriteLine($"{article.TotalLikes} - {article.Title} | {article.Url}");
            }
        }
    }

    private static dynamic CreateCrawler(string crawlerType, ArticleRanking ranking, DateTime startDate,
        DateTime endDate)
    {
        return crawlerType switch
        {
            "vnexpress" => new VnExpressCrawler(ranking, startDate, endDate, 1, 1, 3),
            "tuoitre" => new TuoiTreCrawler(ranking, startDate, endDate, 1, 5),
            _ => null
        };
    }

    public static async Task<int> Main(string[] args)
    {
        ConfigureLogging();

        var rootCommand = new RootCommand("News article crawler for Vietnamese news sites");
        var crawlerCommand = ConfigureCrawlerOptions();

        var typeOption = crawlerCommand.Options.First(opt => opt.Name == "type") as Option<string>;
        var startOption = crawlerCommand.Options.First(opt => opt.Name == "start") as Option<DateTime?>;
        var endOption = crawlerCommand.Options.First(opt => opt.Name == "end") as Option<DateTime?>;

        crawlerCommand.SetHandler(
            ExecuteCrawlerAsync,
            typeOption!,
            startOption!,
            endOption!
        );

        rootCommand.Add(crawlerCommand);
        return await rootCommand.InvokeAsync(args);
    }
}