using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text;
using Azure.Storage.Blobs;

namespace Microbot.RevisionChecker;

public sealed class CheckJagexVersion
{
    private static string? lastProductionId = null;
    private static string? lastStagingId = null;

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public CheckJagexVersion(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClientFactory.CreateClient();
        _logger = loggerFactory.CreateLogger<CheckJagexVersion>();
    }

     [Function("CheckJagexVersion")]
    public async Task Run([TimerTrigger("0 */30 * * * *", RunOnStartup = true)] TimerInfo timerInfo)
    {
        try
        {
            _logger.LogInformation($"Checking launcher version at: {DateTime.UtcNow}");

            var jwt = await _httpClient.GetStringAsync(AppSettings.MetaUrl);
            string json = DecodeJwtPayload(jwt);
            using var doc = JsonDocument.Parse(json);
            var environments = doc.RootElement.GetProperty("environments");

            string productionId = environments.GetProperty("production").GetProperty("id").GetString()!;
            string productionVersion = environments.GetProperty("production").GetProperty("version").GetString()!;
            string productionPreviousVersion = environments.GetProperty("production-last").GetProperty("version").GetString()!;
            string stagingId = environments.GetProperty("staging").GetProperty("id").GetString()!;
            string stagingVersion = environments.GetProperty("staging").GetProperty("version").GetString()!;

            var blobClient = new BlobContainerClient(AppSettings.BlobConnectionString, AppSettings.ContainerName);
            await blobClient.CreateIfNotExistsAsync();
            var blob = blobClient.GetBlobClient(AppSettings.BlobName);

            VersionState? currentState = null;

            if (await blob.ExistsAsync())
            {
                using var stream = await blob.OpenReadAsync();
                currentState = await JsonSerializer.DeserializeAsync<VersionState>(stream);
            }

            bool updated = false;
            var newState = new VersionState
            {
                LastProductionId = productionId,
                LastStagingId = stagingId
            };

            if (currentState is null || currentState.LastProductionId != productionId)
            {
                await NotifyDiscord($"🚀 New Native Client **Production** version detected!\n`{productionPreviousVersion}` -> `{productionVersion}`");
                updated = true;
            }

            if (currentState is null || currentState.LastStagingId != stagingId)
            {
                await NotifyDiscord($"🧪 New Native Client **Staging** version detected!\n`{stagingVersion}`");
                updated = true;
            }

            if (updated)
            {
                using var ms = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(newState));
                await blob.UploadAsync(ms, overwrite: true);
                _logger.LogInformation("Updated version state in blob.");
            }
            else
            {
                _logger.LogInformation("No updates detected.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to check versions: {ex}");
        }
    }

    private async Task NotifyDiscord(string message)
    {
        var json = JsonSerializer.Serialize(new { content = message });
        await _httpClient.PostAsync(AppSettings.DiscordWebhook, new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static string DecodeJwtPayload(string jwt)
    {
        string base64 = jwt.Split('.')[1];
        int pad = 4 - (base64.Length % 4);
        if (pad < 4) base64 += new string('=', pad);
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private class VersionState
    {
        public string LastProductionId { get; set; } = string.Empty;
        public string LastStagingId { get; set; } = string.Empty;
    }
}
