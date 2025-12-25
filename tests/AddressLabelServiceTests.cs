using System.Net;
using Moq;
using Moq.Protected;
using QubicTransferWatcher.Services;

namespace QubicTransferWatcher.Tests;

public class AddressLabelServiceTests
{
    private const string BundleJson ="";
    // private const string BundleJson = """
    // {
    //     "address_labels": [
    //         { "address": "TESTADDRESS1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "label": "Test Wallet", "name": "Test" }
    //     ],
    //     "exchanges": [
    //         { "address": "MEXCADDRESS1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "name": "MEXC" }
    //     ],
    //     "smart_contracts": [
    //         { "address": "CONTRACTADDR1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "name": "Qx", "label": "Qx" }
    //     ],
    //     "tokens": [
    //         { "name": "QFT", "issuer": "QFTISSUERADDR1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" }
    //     ]
    // }
    // """;


    private static HttpClient CreateMockHttpClient(string responseContent)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseContent)
            });

        return new HttpClient(handlerMock.Object);
    }

    [Fact]
    public async Task InitializeAsync_LoadsAddressLabels()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(BundleJson);
        var service = new AddressLabelService(httpClient, "https://example.com/bundle.json");

        // Act
        await service.InitializeAsync();

        // Assert
        var label = service.GetLabel("TESTADDRESS1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        Assert.Equal("Test Wallet", label);
    }

    [Fact]
    public async Task GetLabel_ReturnsExchangeWithHashPrefix()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(BundleJson);
        var service = new AddressLabelService(httpClient, "https://example.com/bundle.json");
        await service.InitializeAsync();

        // Act
        var label = service.GetLabel("MEXCADDRESS1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        // Assert
        Assert.Equal("#MEXC", label);
    }

    [Fact]
    public async Task GetLabel_ReturnsContractWithBrackets()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(BundleJson);
        var service = new AddressLabelService(httpClient, "https://example.com/bundle.json");
        await service.InitializeAsync();

        // Act
        var label = service.GetLabel("CONTRACTADDR1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        // Assert
        Assert.Equal("[Qx]", label);
    }

    [Fact]
    public async Task GetLabel_ReturnsTokenIssuerWithDollarPrefix()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(BundleJson);
        var service = new AddressLabelService(httpClient, "https://example.com/bundle.json");
        await service.InitializeAsync();

        // Act
        var label = service.GetLabel("QFTISSUERADDR1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        // Assert
        Assert.Equal("$QFT Issuer", label);
    }

    [Fact]
    public async Task GetLabel_ReturnsBurnLabelForBurnAddress()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(BundleJson);
        var service = new AddressLabelService(httpClient, "https://example.com/bundle.json");
        await service.InitializeAsync();

        // Act
        var label = service.GetLabel(AddressLabelService.BurnAddress);

        // Assert
        Assert.Contains("BURN", label);
    }

    [Fact]
    public async Task GetLabel_ReturnsBurnLabelForQutilBurnAddress()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(BundleJson);
        var service = new AddressLabelService(httpClient, "https://example.com/bundle.json");
        await service.InitializeAsync();

        // Act
        var label = service.GetLabel(AddressLabelService.BurnAddressQutil);

        // Assert
        Assert.Contains("BURN", label);
    }

    [Fact]
    public async Task GetLabel_ReturnsShortenedAddressForUnknown()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(BundleJson);
        var service = new AddressLabelService(httpClient, "https://example.com/bundle.json");
        await service.InitializeAsync();

        // Act
        var label = service.GetLabel("UNKNOWNADDRESSAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        // Assert
        Assert.Equal("UNKN...", label);
    }

    [Fact]
    public void IsBurnAddress_ReturnsTrueForBurnAddress()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(BundleJson);
        var service = new AddressLabelService(httpClient, "https://example.com/bundle.json");

        // Act & Assert
        Assert.True(service.IsBurnAddress(AddressLabelService.BurnAddress));
        Assert.True(service.IsBurnAddress(AddressLabelService.BurnAddressQutil));
    }

    [Fact]
    public void IsBurnAddress_ReturnsFalseForNormalAddress()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(BundleJson);
        var service = new AddressLabelService(httpClient, "https://example.com/bundle.json");

        // Act & Assert
        Assert.False(service.IsBurnAddress("TESTADDRESS1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }

    [Fact]
    public async Task IsExchange_ReturnsTrueForExchangeAddress()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(BundleJson);
        var service = new AddressLabelService(httpClient, "https://example.com/bundle.json");
        await service.InitializeAsync();

        // Act & Assert
        Assert.True(service.IsExchange("MEXCADDRESS1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }

    [Fact]
    public async Task IsExchange_ReturnsFalseForNonExchangeAddress()
    {
        // Arrange
        var httpClient = CreateMockHttpClient(BundleJson);
        var service = new AddressLabelService(httpClient, "https://example.com/bundle.json");
        await service.InitializeAsync();

        // Act & Assert
        Assert.False(service.IsExchange("TESTADDRESS1AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }
}
