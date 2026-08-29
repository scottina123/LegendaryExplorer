using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LegendaryExplorer.Tools.FaceFXEditor.ElevenLabs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LegendaryExplorer.Tests.Tools.FaceFXEditor;

[TestClass]
public class ElevenLabsApiClientTests
{
    [TestMethod]
    public async Task ReadsSubscriptionCreditsAndFiltersModelsToTextToSpeech()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.AreEqual("test-key", request.Headers.GetValues("xi-api-key").Single());
            return request.RequestUri.AbsolutePath switch
            {
                "/v1/user/subscription" => JsonResponse("""
                    {"tier":"starter","character_count":1250,"character_limit":10000}
                    """),
                "/v1/models" => JsonResponse("""
                    [
                      {"model_id":"tts","name":"Speech","can_do_text_to_speech":true,"can_use_style":true,
                       "can_use_speaker_boost":true,"languages":[{"language_id":"en","name":"English"}]},
                      {"model_id":"music","name":"Music","can_do_text_to_speech":false}
                    ]
                    """),
                _ => throw new AssertFailedException($"Unexpected path {request.RequestUri}")
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.elevenlabs.io/") };
        using var client = new ElevenLabsApiClient("test-key", httpClient);

        ElevenLabsSubscription subscription = await client.GetSubscriptionAsync();
        IReadOnlyList<ElevenLabsModel> models = await client.GetModelsAsync();

        Assert.AreEqual(8750, subscription.CreditsRemaining);
        Assert.HasCount(1, models);
        Assert.AreEqual("tts", models[0].ModelId);
        Assert.AreEqual("en", models[0].Languages.Single().LanguageId);
    }

    [TestMethod]
    public async Task ReadsEveryVoicePaginationPage()
    {
        int requestCount = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                Assert.IsFalse(request.RequestUri.Query.Contains("next_page_token", StringComparison.Ordinal));
                return JsonResponse("""
                    {"voices":[{"voice_id":"b","name":"Beta"}],"has_more":true,"next_page_token":"next page"}
                    """);
            }

            Assert.IsTrue(request.RequestUri.Query.Contains("next_page_token=next%20page", StringComparison.Ordinal));
            return JsonResponse("""
                {"voices":[{"voice_id":"a","name":"Alpha"}],"has_more":false}
                """);
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.elevenlabs.io/") };
        using var client = new ElevenLabsApiClient("test-key", httpClient);

        IReadOnlyList<ElevenLabsVoice> voices = await client.GetVoicesAsync();

        Assert.AreEqual(2, requestCount);
        CollectionAssert.AreEqual(new[] { "Alpha", "Beta" }, voices.Select(voice => voice.Name).ToArray());
    }

    [TestMethod]
    public async Task SendsAllVoiceAndAdvancedSettingsAndReadsReportedCost()
    {
        string requestJson = null;
        Uri requestUri = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            requestUri = request.RequestUri;
            requestJson = await request.Content.ReadAsStringAsync();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
            response.Headers.TryAddWithoutValidation("character-cost", "42");
            response.Headers.TryAddWithoutValidation("request-id", "request-123");
            return response;
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.elevenlabs.io/") };
        using var client = new ElevenLabsApiClient("test-key", httpClient);
        var request = new ElevenLabsSpeechRequest
        {
            Text = "A test line.",
            ModelId = "eleven_multilingual_v2",
            LanguageCode = "en",
            Seed = 123,
            PreviousText = "Before.",
            NextText = "After.",
            ApplyTextNormalization = "on",
            ApplyLanguageTextNormalization = true,
            EnableLogging = false,
            OptimizeStreamingLatency = 2,
            VoiceSettings = new ElevenLabsVoiceSettings
            {
                Stability = 0.4,
                SimilarityBoost = 0.8,
                Style = 0.2,
                UseSpeakerBoost = true,
                Speed = 1.1
            }
        };

        ElevenLabsSpeechResult result = await client.GenerateSpeechAsync("voice/id", request);

        Assert.AreEqual("/v1/text-to-speech/voice%2Fid", requestUri.AbsolutePath);
        Assert.IsTrue(requestUri.Query.Contains("output_format=mp3_44100_128", StringComparison.Ordinal));
        Assert.IsTrue(requestUri.Query.Contains("enable_logging=false", StringComparison.Ordinal));
        Assert.IsTrue(requestUri.Query.Contains("optimize_streaming_latency=2", StringComparison.Ordinal));
        using JsonDocument document = JsonDocument.Parse(requestJson);
        JsonElement root = document.RootElement;
        Assert.AreEqual("eleven_multilingual_v2", root.GetProperty("model_id").GetString());
        Assert.AreEqual("en", root.GetProperty("language_code").GetString());
        Assert.AreEqual(123u, root.GetProperty("seed").GetUInt32());
        Assert.AreEqual("Before.", root.GetProperty("previous_text").GetString());
        Assert.AreEqual("After.", root.GetProperty("next_text").GetString());
        Assert.AreEqual("on", root.GetProperty("apply_text_normalization").GetString());
        Assert.IsTrue(root.GetProperty("apply_language_text_normalization").GetBoolean());
        JsonElement settings = root.GetProperty("voice_settings");
        Assert.AreEqual(0.4, settings.GetProperty("stability").GetDouble());
        Assert.AreEqual(0.8, settings.GetProperty("similarity_boost").GetDouble());
        Assert.AreEqual(0.2, settings.GetProperty("style").GetDouble());
        Assert.IsTrue(settings.GetProperty("use_speaker_boost").GetBoolean());
        Assert.AreEqual(1.1, settings.GetProperty("speed").GetDouble());
        Assert.AreEqual(42, result.CreditCost);
        Assert.AreEqual("request-123", result.RequestId);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, result.Audio);
    }

    [TestMethod]
    public async Task OmitsDeprecatedAndUnsupportedSettingsForElevenV3()
    {
        string requestJson = null;
        Uri requestUri = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            requestUri = request.RequestUri;
            requestJson = await request.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1])
            };
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.elevenlabs.io/") };
        using var client = new ElevenLabsApiClient("test-key", httpClient);
        var request = new ElevenLabsSpeechRequest
        {
            Text = "[flirty] Hello.",
            ModelId = "eleven_v3",
            OptimizeStreamingLatency = 3,
            VoiceSettings = new ElevenLabsVoiceSettings
            {
                Stability = 0.5,
                SimilarityBoost = 0.8,
                Style = 0.2,
                UseSpeakerBoost = true,
                Speed = 1.1
            }
        };

        await client.GenerateSpeechAsync("voice", request);

        Assert.IsFalse(requestUri.Query.Contains("optimize_streaming_latency", StringComparison.Ordinal));
        using JsonDocument document = JsonDocument.Parse(requestJson);
        JsonElement settings = document.RootElement.GetProperty("voice_settings");
        Assert.AreEqual(0.5, settings.GetProperty("stability").GetDouble());
        Assert.AreEqual(1.1, settings.GetProperty("speed").GetDouble());
        Assert.IsFalse(settings.TryGetProperty("similarity_boost", out _));
        Assert.IsFalse(settings.TryGetProperty("style", out _));
        Assert.IsFalse(settings.TryGetProperty("use_speaker_boost", out _));
    }

    [TestMethod]
    public async Task ReadsAudioAndCharacterAlignmentFromTimestampGeneration()
    {
        Uri requestUri = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            requestUri = request.RequestUri;
            var response = JsonResponse("""
                {
                  "audio_base64":"AQID",
                  "alignment":{
                    "characters":["A","B"],
                    "character_start_times_seconds":[0.1,0.2],
                    "character_end_times_seconds":[0.2,0.3]
                  }
                }
                """);
            response.Headers.TryAddWithoutValidation("character-cost", "12");
            return response;
        });
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.elevenlabs.io/") };
        using var client = new ElevenLabsApiClient("test-key", httpClient);

        ElevenLabsTimedSpeechResult result = await client.GenerateSpeechWithTimestampsAsync("voice",
            new ElevenLabsSpeechRequest { Text = "AB", ModelId = "eleven_multilingual_v2" });

        Assert.AreEqual("/v1/text-to-speech/voice/with-timestamps", requestUri.AbsolutePath);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, result.Audio);
        CollectionAssert.AreEqual(new[] { "A", "B" }, result.Alignment.Characters);
        Assert.AreEqual(0.2, result.Alignment.CharacterStartTimesSeconds[1]);
        Assert.AreEqual(12, result.CreditCost);
    }

    [TestMethod]
    public void BuildsImportReadyGenderedNamesWithoutTakeSuffix()
    {
        Assert.AreEqual("VO_1850000_f_take2.mp3",
            ElevenLabsGenerationDialog.BuildTakeFileName(1850000, true, 2));
        Assert.AreEqual("VO_1850000_f.mp3",
            ElevenLabsGenerationDialog.BuildImportFileName(1850000, true));
        Assert.AreEqual("VO_1850000_m.mp3",
            ElevenLabsGenerationDialog.BuildImportFileName(1850000, false));
        Assert.AreEqual("VO_1850000_m_take1.wav",
            ElevenLabsGenerationDialog.BuildTakeFileName(1850000, false, 1, ".wav"));
        Assert.AreEqual("VO_1850000_f.wav",
            ElevenLabsGenerationDialog.BuildImportFileName(1850000, true, ".wav"));
    }

    [TestMethod]
    public void BuildsDocumentedV3EmotionAndAccentTagsWithoutChangingStoredText()
    {
        Assert.AreEqual("We should go now.",
            ElevenLabsGenerationDialog.BuildPromptedText("  We should go now.  ", "Neutral", "None"));
        Assert.AreEqual("[strong French accent] [angry] We should go now.",
            ElevenLabsGenerationDialog.BuildPromptedText("We should go now.", "Angry", "French"));
        Assert.AreEqual("[mischievously] Perhaps.",
            ElevenLabsGenerationDialog.BuildPromptedText("Perhaps.", "Mischievous", "None"));
        Assert.AreEqual("[flirty] Hello, stranger.",
            ElevenLabsGenerationDialog.BuildPromptedText("Hello, stranger.", "Flirty", "None"));
        Assert.AreEqual("[strong Irish accent] [romantic] I missed you.",
            ElevenLabsGenerationDialog.BuildPromptedText("I missed you.", "Romantic", "Irish"));
    }

    [TestMethod]
    public void BuildsAndTimesRemovablePreV3AccentAndEmotionDirections()
    {
        ElevenLabsSpeechPrompt prompt = ElevenLabsGenerationDialog.BuildSpeechPrompt(
            "I missed you.", "Romantic", "Russian", isElevenV3: false);
        Assert.AreEqual(
            "They spoke in a Russian accent. They spoke with a romantic emotion. I missed you.",
            prompt.Text);
        Assert.IsTrue(prompt.RequiresPrefixTrim);
        Assert.AreEqual(prompt.Text.IndexOf("I missed you.", StringComparison.Ordinal),
            prompt.TrimPrefixCharacterCount);

        var alignment = new ElevenLabsSpeechAlignment
        {
            Characters = prompt.Text.Select(character => character.ToString()).ToList(),
            CharacterStartTimesSeconds = Enumerable.Range(0, prompt.Text.Length)
                .Select(index => index / 100d).ToList()
        };
        Assert.AreEqual(prompt.TrimPrefixCharacterCount / 100d,
            ElevenLabsGenerationDialog.GetTrimStartSeconds(alignment, prompt.TrimPrefixCharacterCount));

        ElevenLabsSpeechPrompt romanian = ElevenLabsGenerationDialog.BuildSpeechPrompt(
            "Bună.", "Angry", "Romanian", isElevenV3: false);
        Assert.AreEqual("They spoke in a Romanian accent. They spoke with an angry emotion. Bună.",
            romanian.Text);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this(request => Task.FromResult(handler(request)))
        {
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => _handler(request);
    }
}
