using System.Text.Json.Serialization;

namespace QubicTransferWatcher.Models;

public class BundleData
{
    [JsonPropertyName("address_labels")]
    public List<AddressLabel> AddressLabels { get; set; } = new();

    [JsonPropertyName("exchanges")]
    public List<ExchangeInfo> Exchanges { get; set; } = new();

    [JsonPropertyName("smart_contracts")]
    public List<SmartContractInfo> SmartContracts { get; set; } = new();

    [JsonPropertyName("tokens")]
    public List<TokenInfo> Tokens { get; set; } = new();
}

public class AddressLabel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

public class ExchangeInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

public class SmartContractInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("githubUrl")]
    public string? GithubUrl { get; set; }

    [JsonPropertyName("contractIndex")]
    public int? ContractIndex { get; set; }

    [JsonPropertyName("procedures")]
    public List<object>? Procedures { get; set; }

    [JsonPropertyName("website")]
    public string? Website { get; set; }
}

public class TokenInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("issuer")]
    public string Issuer { get; set; } = "";

    [JsonPropertyName("website")]
    public string? Website { get; set; }
}
