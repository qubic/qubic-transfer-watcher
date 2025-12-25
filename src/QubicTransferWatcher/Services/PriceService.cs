using System.Text.Json;
using Serilog;

namespace QubicTransferWatcher.Services;

/// <summary>
/// Service to fetch current QUBIC price in USD
/// </summary>
public class PriceService
{
    private readonly ILogger _log = Log.ForContext<PriceService>();
    private readonly HttpClient _httpClient;
    private readonly string _priceApiUrl;
    private decimal _currentPrice = 0;
    private DateTime _lastPriceUpdate = DateTime.MinValue;
    private readonly TimeSpan _priceUpdateInterval = TimeSpan.FromMinutes(5);
    private readonly object _lock = new();

    public PriceService(HttpClient httpClient, string priceApiUrl)
    {
        _httpClient = httpClient;
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/117.0.0.0 Safari/537.36"
        );
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7"
        );
        _priceApiUrl = priceApiUrl;
    }

    public async Task InitializeAsync()
    {
        await UpdatePriceAsync();
    }

    public async Task UpdatePriceAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync(_priceApiUrl);
            using var doc = JsonDocument.Parse(response);

            if (doc.RootElement.TryGetProperty("qubic-network", out var qubicData) &&
                qubicData.TryGetProperty("usd", out var usdPrice))
            {
                lock (_lock)
                {
                    _currentPrice = usdPrice.GetDecimal();
                    _lastPriceUpdate = DateTime.UtcNow;
                }
                _log.Information("Updated QUBIC price: ${Price:F6}", _currentPrice);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error fetching price from {PriceApiUrl}", _priceApiUrl);
        }
    }

    public async Task<decimal> GetCurrentPriceAsync()
    {
        if (DateTime.UtcNow - _lastPriceUpdate > _priceUpdateInterval)
        {
            await UpdatePriceAsync();
        }

        lock (_lock)
        {
            return _currentPrice;
        }
    }

    public async Task<decimal> CalculateUsdValue(long qubicAmount)
    {
        var price = await GetCurrentPriceAsync();
        return qubicAmount * price;
    }

    public async Task<string> FormatUsdValue(long qubicAmount)
    {
        var usdValue = await CalculateUsdValue(qubicAmount);
        return FormatNumber(usdValue);
    }

    public static string FormatNumber(decimal value)
    {
        return value.ToString("N0");
    }

    public static string FormatQubicAmount(long amount)
    {
        return amount.ToString("N0");
    }
}
