using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StudyLife.Server.Data;
using StudyLife.Server.Services;

namespace StudyLife.Server.Tests;

/// <summary>
/// BackgroundTaskService.RunCaptureEnrichmentAsync (studylife-capture browser extension S2):
/// course-match + tags/summary enrichment for notes with SourceUrl set. Same stub HTTP pattern
/// as AiProxyClientTests/LiveActivityPushTests - the DI-registered AiProxyClient in the test
/// host has no StudyLifeAi:* config and is therefore Enabled=false by default, so tests that
/// need a real call construct their own via CreateService(handler).
/// </summary>
public class BackgroundTaskServiceCaptureEnrichmentTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public BackgroundTaskServiceCaptureEnrichmentTests(CustomWebApplicationFactory factory) => _factory = factory;

    private (BackgroundTaskService Service, StubHttpHandler Handler) CreateService(
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var config = new Dictionary<string, string?>
        {
            ["StudyLifeAi:BaseUrl"] = "https://ai.test",
            ["StudyLifeAi:SharedSecret"] = "shared-secret",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var handler = new StubHttpHandler(responder ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"course_id\":null,\"course_confidence\":null,\"tags\":[],\"summary\":null}"),
        }));
        var aiProxyClient = new AiProxyClient(configuration, NullLogger<AiProxyClient>.Instance, new HttpClient(handler));
        return (BackgroundTaskServiceTestFactory.Create(_factory, aiProxyClient: aiProxyClient), handler);
    }

    private async Task<int> SeedNoteAsync(string? sourceUrl, DateTime? enrichedAt = null, int? courseId = null) =>
        await _factory.WithDbAsync(async db =>
        {
            var note = new NoteEntity
            {
                Title = "Captured note",
                Content = "Some captured content",
                SourceUrl = sourceUrl,
                EnrichedAt = enrichedAt,
                CourseId = courseId,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
            };
            db.Notes.Add(note);
            await db.SaveChangesAsync();
            return note.Id;
        });

    private Task<NoteEntity> ReloadNoteAsync(int id) =>
        _factory.WithDbAsync(db => db.Notes.AsNoTracking().FirstAsync(n => n.Id == id));

    [Fact]
    public async Task RunCaptureEnrichmentAsync_WhenDisabled_NeverCallsOutAndLeavesNoteUnenriched()
    {
        var noteId = await SeedNoteAsync("https://example.com/article");
        // Default DI-registered AiProxyClient (no StudyLifeAi:* config) - Enabled=false.
        var service = BackgroundTaskServiceTestFactory.Create(_factory);

        await _factory.WithDbAsync(db => service.RunCaptureEnrichmentAsync(db));

        var note = await ReloadNoteAsync(noteId);
        Assert.Null(note.EnrichedAt);
    }

    [Fact]
    public async Task RunCaptureEnrichmentAsync_NoteWithoutSourceUrl_NeverProcessed()
    {
        var noteId = await SeedNoteAsync(sourceUrl: null);
        var (service, handler) = CreateService();

        await _factory.WithDbAsync(db => service.RunCaptureEnrichmentAsync(db));

        Assert.Empty(handler.Requests);
        var note = await ReloadNoteAsync(noteId);
        Assert.Null(note.EnrichedAt);
    }

    [Fact]
    public async Task RunCaptureEnrichmentAsync_AlreadyEnrichedNote_NeverReprocessed()
    {
        var enrichedAt = DateTime.Now.AddMinutes(-5);
        var noteId = await SeedNoteAsync("https://example.com/article", enrichedAt: enrichedAt);
        var (service, handler) = CreateService();

        await _factory.WithDbAsync(db => service.RunCaptureEnrichmentAsync(db));

        Assert.Empty(handler.Requests);
        var note = await ReloadNoteAsync(noteId);
        Assert.Equal(enrichedAt, note.EnrichedAt);
    }

    [Fact]
    public async Task RunCaptureEnrichmentAsync_SuccessfulMatch_SetsCourseIdTagsSummaryAndEnrichedAt()
    {
        var noteId = await SeedNoteAsync("https://example.com/article");
        var (service, _) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"course_id\":3,\"course_confidence\":0.9,\"tags\":[\"eigenvalues\",\"matrices\"],\"summary\":\"A summary.\",\"related_note_ids\":[12,34]}"),
        });

        await _factory.WithDbAsync(db => service.RunCaptureEnrichmentAsync(db));

        var note = await ReloadNoteAsync(noteId);
        Assert.NotNull(note.EnrichedAt);
        Assert.Equal(3, note.CourseId);
        Assert.Equal("eigenvalues, matrices", note.Tags);
        Assert.Equal("A summary.", note.Summary);
        Assert.Equal("12,34", note.RelatedNoteIds);
    }

    [Fact]
    public async Task RunCaptureEnrichmentAsync_EmptyRelatedNoteIds_LeavesRelatedNoteIdsNull()
    {
        var noteId = await SeedNoteAsync("https://example.com/article");
        var (service, _) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"course_id\":null,\"course_confidence\":null,\"tags\":[],\"summary\":null,\"related_note_ids\":[]}"),
        });

        await _factory.WithDbAsync(db => service.RunCaptureEnrichmentAsync(db));

        var note = await ReloadNoteAsync(noteId);
        Assert.Null(note.RelatedNoteIds);
    }

    [Fact]
    public async Task RunCaptureEnrichmentAsync_NoteAlreadyHasACourse_DoesNotOverwriteIt()
    {
        var noteId = await SeedNoteAsync("https://example.com/article", courseId: 1);
        var (service, _) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"course_id\":3,\"course_confidence\":0.9,\"tags\":[],\"summary\":null}"),
        });

        await _factory.WithDbAsync(db => service.RunCaptureEnrichmentAsync(db));

        var note = await ReloadNoteAsync(noteId);
        Assert.Equal(1, note.CourseId);
        Assert.NotNull(note.EnrichedAt);
    }

    [Fact]
    public async Task RunCaptureEnrichmentAsync_UpstreamFailure_StillMarksEnrichedAt_NoRetryStorm()
    {
        var noteId = await SeedNoteAsync("https://example.com/article");
        var (service, _) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await _factory.WithDbAsync(db => service.RunCaptureEnrichmentAsync(db));

        var note = await ReloadNoteAsync(noteId);
        Assert.NotNull(note.EnrichedAt);
        Assert.Null(note.CourseId);
        Assert.Null(note.Tags);
        Assert.Null(note.Summary);
    }

    [Fact]
    public async Task RunCaptureEnrichmentAsync_EmptyTags_LeavesTagsNull()
    {
        var noteId = await SeedNoteAsync("https://example.com/article");
        var (service, _) = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"course_id\":null,\"course_confidence\":null,\"tags\":[],\"summary\":null}"),
        });

        await _factory.WithDbAsync(db => service.RunCaptureEnrichmentAsync(db));

        var note = await ReloadNoteAsync(noteId);
        Assert.Null(note.Tags);
    }

    /// <summary>Records all requests (including headers/body) and returns predefined responses -
    /// same deliberately plainly-built stub as AiProxyClientTests/ApnsPushTests/LiveActivityPushTests.</summary>
    private sealed class StubHttpHandler : HttpMessageHandler
    {
        public sealed record RecordedRequest(string Uri, Dictionary<string, string> Headers, string Body);

        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<RecordedRequest> Requests { get; } = [];

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers
                .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add(new RecordedRequest(request.RequestUri!.ToString(), headers, body));
            return _responder(request);
        }
    }
}
