using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LegendaryExplorer.Tools.FaceFXEditor.ElevenLabs
{
    /// <summary>
    /// Small, dependency-free client for the ElevenLabs endpoints used by the FaceFX editor.
    /// </summary>
    public sealed class ElevenLabsApiClient : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _httpClient;
        private readonly bool _disposeClient;
        private readonly string _apiKey;

        public ElevenLabsApiClient(string apiKey)
            : this(apiKey, new HttpClient
            {
                BaseAddress = new Uri("https://api.elevenlabs.io/"),
                Timeout = TimeSpan.FromMinutes(10)
            }, true)
        {
        }

        public ElevenLabsApiClient(string apiKey, HttpClient httpClient, bool disposeClient = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _httpClient.BaseAddress ??= new Uri("https://api.elevenlabs.io/");
            _apiKey = apiKey.Trim();
            _disposeClient = disposeClient;
        }

        public async Task<ElevenLabsSubscription> GetSubscriptionAsync(CancellationToken cancellationToken = default)
        {
            using var response = await SendAsync(HttpMethod.Get, "v1/user/subscription", null, cancellationToken);
            return await DeserializeAsync<ElevenLabsSubscription>(response, cancellationToken);
        }

        public async Task<IReadOnlyList<ElevenLabsModel>> GetModelsAsync(CancellationToken cancellationToken = default)
        {
            using var response = await SendAsync(HttpMethod.Get, "v1/models", null, cancellationToken);
            var models = await DeserializeAsync<List<ElevenLabsModel>>(response, cancellationToken);
            return models
                .Where(model => model.CanDoTextToSpeech)
                .OrderBy(model => model.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public async Task<IReadOnlyList<ElevenLabsVoice>> GetVoicesAsync(CancellationToken cancellationToken = default)
        {
            var voices = new List<ElevenLabsVoice>();
            string nextPageToken = null;

            do
            {
                string path = "v2/voices?page_size=100&include_total_count=false&sort=name&sort_direction=asc";
                if (!string.IsNullOrWhiteSpace(nextPageToken))
                {
                    path += $"&next_page_token={Uri.EscapeDataString(nextPageToken)}";
                }

                using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
                var page = await DeserializeAsync<ElevenLabsVoicePage>(response, cancellationToken);
                if (page.Voices != null)
                {
                    voices.AddRange(page.Voices);
                }

                nextPageToken = page.HasMore ? page.NextPageToken : null;
            } while (!string.IsNullOrWhiteSpace(nextPageToken));

            return voices
                .Where(voice => !string.IsNullOrWhiteSpace(voice.VoiceId))
                .DistinctBy(voice => voice.VoiceId, StringComparer.Ordinal)
                .OrderBy(voice => voice.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public async Task<ElevenLabsVoiceSettings> GetVoiceSettingsAsync(string voiceId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
            using var response = await SendAsync(HttpMethod.Get,
                $"v1/voices/{Uri.EscapeDataString(voiceId)}/settings", null, cancellationToken);
            return await DeserializeAsync<ElevenLabsVoiceSettings>(response, cancellationToken);
        }

        public async Task<ElevenLabsSpeechResult> GenerateSpeechAsync(string voiceId,
            ElevenLabsSpeechRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(voiceId);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);

            string path = $"v1/text-to-speech/{Uri.EscapeDataString(voiceId)}" +
                          $"?output_format=mp3_44100_128&enable_logging={request.EnableLogging.ToString().ToLowerInvariant()}";
            if (!IsElevenV3(request.ModelId) && request.OptimizeStreamingLatency is >= 1 and <= 4)
            {
                path += $"&optimize_streaming_latency={request.OptimizeStreamingLatency.Value}";
            }

            var payload = new ElevenLabsSpeechPayload
            {
                Text = request.Text,
                ModelId = request.ModelId,
                LanguageCode = NullIfWhiteSpace(request.LanguageCode),
                VoiceSettings = GetCompatibleVoiceSettings(request.ModelId, request.VoiceSettings),
                Seed = request.Seed,
                PreviousText = NullIfWhiteSpace(request.PreviousText),
                NextText = NullIfWhiteSpace(request.NextText),
                ApplyTextNormalization = request.ApplyTextNormalization,
                ApplyLanguageTextNormalization = request.ApplyLanguageTextNormalization
            };

            using var response = await SendAsync(HttpMethod.Post, path, payload, cancellationToken);
            byte[] audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            int? creditCost = TryReadIntHeader(response, "character-cost");
            string requestId = TryReadHeader(response, "request-id");
            return new ElevenLabsSpeechResult(audio, creditCost, requestId);
        }

        private static bool IsElevenV3(string modelId) =>
            string.Equals(modelId, "eleven_v3", StringComparison.OrdinalIgnoreCase);

        private static ElevenLabsVoiceSettings GetCompatibleVoiceSettings(string modelId,
            ElevenLabsVoiceSettings settings)
        {
            if (settings == null || !IsElevenV3(modelId))
            {
                return settings;
            }

            // Eleven v3 does not support the legacy similarity, style, and speaker-boost controls.
            // Null values are omitted by JsonOptions instead of being sent as disabled values.
            return new ElevenLabsVoiceSettings
            {
                Stability = settings.Stability,
                SimilarityBoost = null,
                Style = null,
                UseSpeakerBoost = null,
                Speed = settings.Speed
            };
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object payload,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation("xi-api-key", _apiKey);
            request.Headers.Accept.ParseAdd("application/json, audio/mpeg, application/octet-stream");
            if (payload != null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8,
                    "application/json");
            }

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            HttpStatusCode statusCode = response.StatusCode;
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            string message = ExtractErrorMessage(body) ?? response.ReasonPhrase ?? "ElevenLabs request failed.";
            response.Dispose();
            throw new ElevenLabsApiException(statusCode, message);
        }

        private static async Task<T> DeserializeAsync<T>(HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                   ?? throw new ElevenLabsApiException(response.StatusCode,
                       "ElevenLabs returned an empty or invalid response.");
        }

        private static string ExtractErrorMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("detail", out JsonElement detail))
                {
                    if (detail.ValueKind == JsonValueKind.String)
                    {
                        return detail.GetString();
                    }

                    if (detail.ValueKind == JsonValueKind.Object &&
                        detail.TryGetProperty("message", out JsonElement detailMessage))
                    {
                        return detailMessage.GetString();
                    }
                }

                if (root.TryGetProperty("message", out JsonElement message))
                {
                    return message.GetString();
                }
            }
            catch (JsonException)
            {
                // Fall through to the short, non-JSON response below.
            }

            return body.Length <= 500 ? body : body[..500];
        }

        private static int? TryReadIntHeader(HttpResponseMessage response, string name)
        {
            string value = TryReadHeader(response, name);
            return int.TryParse(value, out int parsed) ? parsed : null;
        }

        private static string TryReadHeader(HttpResponseMessage response, string name)
        {
            return response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        }

        private static string NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

        public void Dispose()
        {
            if (_disposeClient)
            {
                _httpClient.Dispose();
            }
        }

        private sealed class ElevenLabsSpeechPayload
        {
            public string Text { get; set; }
            public string ModelId { get; set; }
            public string LanguageCode { get; set; }
            public ElevenLabsVoiceSettings VoiceSettings { get; set; }
            public uint? Seed { get; set; }
            public string PreviousText { get; set; }
            public string NextText { get; set; }
            public string ApplyTextNormalization { get; set; }
            public bool ApplyLanguageTextNormalization { get; set; }
        }
    }

    public sealed class ElevenLabsApiException : Exception
    {
        public ElevenLabsApiException(HttpStatusCode statusCode, string message)
            : base($"ElevenLabs returned {(int)statusCode} ({statusCode}): {message}")
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }

    public sealed class ElevenLabsSubscription
    {
        public string Tier { get; set; }
        public int CharacterCount { get; set; }
        public int CharacterLimit { get; set; }
        public long? NextCharacterCountResetUnix { get; set; }
        public int CreditsRemaining => Math.Max(0, CharacterLimit - CharacterCount);
    }

    public sealed class ElevenLabsVoicePage
    {
        public List<ElevenLabsVoice> Voices { get; set; } = [];
        public bool HasMore { get; set; }
        public string NextPageToken { get; set; }
    }

    public sealed class ElevenLabsVoice
    {
        public string VoiceId { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        public string PreviewUrl { get; set; }
        public Dictionary<string, string> Labels { get; set; } = [];

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                string details = string.Join(", ", new[]
                {
                    Labels?.GetValueOrDefault("gender"), Labels?.GetValueOrDefault("accent"), Category
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                string name = Name ?? VoiceId;
                return string.IsNullOrWhiteSpace(details) ? name : $"{name} — {details}";
            }
        }
    }

    public sealed class ElevenLabsModel
    {
        public string ModelId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool CanDoTextToSpeech { get; set; }
        public bool CanUseStyle { get; set; }
        public bool CanUseSpeakerBoost { get; set; }
        public int MaxCharactersRequestFreeUser { get; set; }
        public int MaxCharactersRequestSubscribedUser { get; set; }
        public int MaximumTextLengthPerRequest { get; set; }
        public List<ElevenLabsLanguage> Languages { get; set; } = [];
        public ElevenLabsModelRates ModelRates { get; set; }

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? ModelId : $"{Name} ({ModelId})";
    }

    public sealed class ElevenLabsLanguage
    {
        public string LanguageId { get; set; }
        public string Name { get; set; }

        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(LanguageId)
            ? Name
            : string.IsNullOrWhiteSpace(Name) ? LanguageId : $"{Name} ({LanguageId})";
    }

    public sealed class ElevenLabsModelRates
    {
        public double CharacterCostMultiplier { get; set; } = 1d;
        public double CostDiscountMultiplier { get; set; } = 1d;
    }

    public sealed class ElevenLabsVoiceSettings
    {
        public double? Stability { get; set; } = 0.5d;
        public double? SimilarityBoost { get; set; } = 0.75d;
        public double? Style { get; set; }
        public bool? UseSpeakerBoost { get; set; } = true;
        public double? Speed { get; set; } = 1d;
    }

    public sealed class ElevenLabsSpeechRequest
    {
        public string Text { get; set; }
        public string ModelId { get; set; }
        public string LanguageCode { get; set; }
        public ElevenLabsVoiceSettings VoiceSettings { get; set; }
        public uint? Seed { get; set; }
        public string PreviousText { get; set; }
        public string NextText { get; set; }
        public string ApplyTextNormalization { get; set; } = "auto";
        public bool ApplyLanguageTextNormalization { get; set; }
        public bool EnableLogging { get; set; } = true;
        public int? OptimizeStreamingLatency { get; set; }
    }

    public sealed record ElevenLabsSpeechResult(byte[] Audio, int? CreditCost, string RequestId);
}
