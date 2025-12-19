using System.Text.Json;
using QubicTransferWatcher.Models;
using Serilog;

namespace QubicTransferWatcher.Services;

/// <summary>
/// Service to resolve wallet addresses to human-readable labels
/// </summary>
public class AddressLabelService
{
    private readonly ILogger _log = Log.ForContext<AddressLabelService>();
    private readonly HttpClient _httpClient;
    private readonly string _bundleUrl;
    private readonly Dictionary<string, string> _addressLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private DateTime _lastUpdate = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromHours(1);

    // Burn addresses
    public const string BurnAddress = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public const string BurnAddressQutil = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFXIB";

    private static readonly HashSet<string> BurnAddresses = new(StringComparer.OrdinalIgnoreCase)
    {
        BurnAddress,
        BurnAddressQutil
    };

    public AddressLabelService(HttpClient httpClient, string bundleUrl)
    {
        _httpClient = httpClient;
        _bundleUrl = bundleUrl;
    }

    public async Task InitializeAsync()
    {
        await RefreshLabelsAsync();
    }

    public async Task RefreshLabelsAsync()
    {
        try
        {
            _log.Information("Fetching address labels from {BundleUrl}...", _bundleUrl);

            var response = await _httpClient.GetStringAsync(_bundleUrl);
            var bundle = JsonSerializer.Deserialize<BundleData>(response);

            if (bundle == null)
            {
                _log.Warning("Failed to deserialize bundle data");
                return;
            }

            lock (_lock)
            {
                _addressLabels.Clear();

                // Add address labels
                foreach (var label in bundle.AddressLabels)
                {
                    if (!string.IsNullOrEmpty(label.Address))
                    {
                        _addressLabels[label.Address] = label.Label ?? label.Name;
                    }
                }

                // Add exchanges with # prefix
                foreach (var exchange in bundle.Exchanges)
                {
                    if (!string.IsNullOrEmpty(exchange.Address))
                    {
                        _addressLabels[exchange.Address] = $"#{exchange.Name}";
                    }
                }

                // Add smart contracts
                foreach (var contract in bundle.SmartContracts)
                {
                    if (!string.IsNullOrEmpty(contract.Address))
                    {
                        _addressLabels[contract.Address] = $"[{contract.Name}]";
                    }
                }

                // Add token issuers
                foreach (var token in bundle.Tokens)
                {
                    if (!string.IsNullOrEmpty(token.Issuer) && !_addressLabels.ContainsKey(token.Issuer))
                    {
                        _addressLabels[token.Issuer] = $"${token.Name} Issuer";
                    }
                }

                // Add burn addresses
                _addressLabels[BurnAddress] = "🔥 BURN";
                _addressLabels[BurnAddressQutil] = "🔥 BURN";

                _lastUpdate = DateTime.UtcNow;
            }

            _log.Information("Loaded {Count} address labels", _addressLabels.Count);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error fetching address labels from {BundleUrl}", _bundleUrl);
        }
    }

    public string GetLabel(string address)
    {
        if (string.IsNullOrEmpty(address))
            return "Unknown";

        lock (_lock)
        {
            if (_addressLabels.TryGetValue(address, out var label))
                return label;
        }

        // Return shortened address if no label found
        return ShortenAddress(address);
    }

    public string FormatAddressWithLabel(string address)
    {
        if (string.IsNullOrEmpty(address))
            return "Unknown";

        lock (_lock)
        {
            if (_addressLabels.TryGetValue(address, out var label))
                return label;
        }

        return ShortenAddress(address);
    }

    public bool IsBurnAddress(string address)
    {
        return !string.IsNullOrEmpty(address) && BurnAddresses.Contains(address);
    }

    public bool IsExchange(string address)
    {
        lock (_lock)
        {
            if (_addressLabels.TryGetValue(address, out var label))
                return label.StartsWith("#");
        }
        return false;
    }

    private static string ShortenAddress(string address)
    {
        if (string.IsNullOrEmpty(address) || address.Length <= 8)
            return address;

        return $"{address[..4]}...";
    }

    public async Task EnsureFreshDataAsync()
    {
        if (DateTime.UtcNow - _lastUpdate > _cacheExpiry)
        {
            await RefreshLabelsAsync();
        }
    }
}
