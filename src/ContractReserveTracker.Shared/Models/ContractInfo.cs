using System.Text.Json.Serialization;

namespace ContractReserveTracker.Shared.Models;

/// <summary>
/// Smart contract information loaded from Qubic API
/// </summary>
public class ContractInfo
{
    [JsonPropertyName("contractIndex")]
    public int ContractIndex { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    [JsonPropertyName("website")]
    public string? Website { get; set; }

    [JsonPropertyName("githubUrl")]
    public string? GithubUrl { get; set; }

    /// <summary>
    /// Returns display name in format "Name (Label)" or just "Name" if Label is same as Name
    /// </summary>
    public string DisplayName =>
        string.Equals(Name, Label, StringComparison.OrdinalIgnoreCase)
            ? Name
            : $"{Name} ({Label})";
}
