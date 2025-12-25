using System.Text.Json;
using System.Text.Json.Serialization;
using ContractReserveTracker.Shared.Models;
using Microsoft.Extensions.Logging;

namespace ContractReserveTracker.Shared.Services;

/// <summary>
/// Wrapper for the smart_contracts.json response
/// </summary>
internal class SmartContractsResponse
{
    [JsonPropertyName("smart_contracts")]
    public List<ContractInfo> SmartContracts { get; set; } = new();
}

public interface IContractInfoService
{
    Task InitializeAsync();
    ContractInfo? GetByIndex(int contractIndex);
    string GetDisplayName(int contractIndex);
    IReadOnlyList<ContractInfo> GetAll();
}

public class ContractInfoService : IContractInfoService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ContractInfoService> _logger;
    private readonly string _smartContractsUrl;
    private readonly TimeSpan _refreshInterval;
    private readonly Timer _refreshTimer;

    private Dictionary<int, ContractInfo> _contracts = new();
    private readonly object _lock = new();

    public ContractInfoService(
        HttpClient httpClient,
        ILogger<ContractInfoService> logger,
        string smartContractsUrl = "https://static.qubic.org/v1/general/data/smart_contracts.json",
        int refreshIntervalMinutes = 60)
    {
        _httpClient = httpClient;
        _logger = logger;
        _smartContractsUrl = smartContractsUrl;
        _refreshInterval = TimeSpan.FromMinutes(refreshIntervalMinutes);

        _refreshTimer = new Timer(
            async _ => await RefreshAsync(),
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public async Task InitializeAsync()
    {
        await RefreshAsync();

        // Start periodic refresh
        _refreshTimer.Change(_refreshInterval, _refreshInterval);
    }

    private async Task RefreshAsync()
    {
        try
        {
            _logger.LogInformation("Fetching smart contracts from {Url}", _smartContractsUrl);

            var response = await _httpClient.GetStringAsync(_smartContractsUrl);
            var wrapper = JsonSerializer.Deserialize<SmartContractsResponse>(response);
            var contracts = wrapper?.SmartContracts;

            if (contracts != null && contracts.Count > 0)
            {
                lock (_lock)
                {
                    _contracts = contracts.ToDictionary(c => c.ContractIndex);
                }
                _logger.LogInformation("Loaded {Count} smart contracts", contracts.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch smart contracts");
        }
    }

    public ContractInfo? GetByIndex(int contractIndex)
    {
        lock (_lock)
        {
            return _contracts.TryGetValue(contractIndex, out var info) ? info : null;
        }
    }

    public string GetDisplayName(int contractIndex)
    {
        var info = GetByIndex(contractIndex);
        return info?.DisplayName ?? $"Contract #{contractIndex}";
    }

    public IReadOnlyList<ContractInfo> GetAll()
    {
        lock (_lock)
        {
            return _contracts.Values.OrderBy(c => c.ContractIndex).ToList();
        }
    }
}
