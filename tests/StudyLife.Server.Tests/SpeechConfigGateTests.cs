using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using StudyLife.Stt;
using StudyLife.Tts;

namespace StudyLife.Server.Tests;

/// <summary>
/// Speech:Enabled (Program.cs) gates whether TTS (PiperVoiceRegistry/EspeakPhonemizer) and STT
/// (WhisperTranscriber) get registered at all - audit finding O6: the k8s WORKER Deployment
/// (k8s/05-worker.yaml) sets this to "false" because it never serves user traffic (no Service in
/// front of it) and therefore has no legitimate way to reach TtsController/DictationController,
/// yet used to construct the same PiperVoiceRegistry/WhisperTranscriber wrapper singletons as the
/// web pod. Both wrappers already load their actual model bytes LAZILY on first use (see their
/// class comments), so this default-off registration is defense-in-depth (nothing on the worker
/// can accidentally trigger a model load) rather than a fix for a measured eager-startup cost.
/// Default true = today's behavior everywhere else (single-container Pi, docker-compose, the k8s
/// WEB Deployment), unchanged.
/// </summary>
public class SpeechConfigGateTests
{
    private sealed class SpeechDisabledFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Speech:Enabled", "false");
        }
    }

    [Fact]
    public void SpeechDisabled_DoesNotRegisterTtsOrSttSingletons()
    {
        using var factory = new SpeechDisabledFactory();
        using var client = factory.CreateClient(); // forces host startup

        Assert.Null(factory.Services.GetService<PiperVoiceRegistry>());
        Assert.Null(factory.Services.GetService<EspeakPhonemizer>());
        Assert.Null(factory.Services.GetService<WhisperTranscriber>());
    }

    [Fact]
    public void SpeechEnabledByDefault_RegistersTtsAndSttSingletons()
    {
        // No Speech:Enabled override - pins the default (unset = true) that every deploy shape
        // other than the k8s worker relies on.
        using var factory = new CustomWebApplicationFactory();
        using var client = factory.CreateClient();

        Assert.NotNull(factory.Services.GetService<PiperVoiceRegistry>());
        Assert.NotNull(factory.Services.GetService<EspeakPhonemizer>());
        Assert.NotNull(factory.Services.GetService<WhisperTranscriber>());
    }

    [Fact]
    public async Task SpeechDisabled_TtsEndpoint_Returns404_NotServerError()
    {
        using var factory = new SpeechDisabledFactory();
        using var client = factory.CreateClient();

        // The gate check runs before the note lookup (see TtsController.Synthesize), so this
        // 404s the same way regardless of whether note id 1 exists in this factory's temp DB.
        var response = await client.GetAsync("/api/notes/1/tts?lang=en");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SpeechDisabled_DictationEndpoint_Returns404_NotServerError()
    {
        using var factory = new SpeechDisabledFactory();
        using var client = factory.CreateClient();
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent([1, 2, 3]), "audio", "audio.wav" },
        };

        var response = await client.PostAsync("/api/dictate", content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
