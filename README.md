# Qubic Transfer Watcher

A C# application that monitors Qubic blockchain transfers via WebSocket and sends notifications to Discord.

## Features

- **Real-time monitoring** via WebSocket connection to Qubic log stream
- **Multi-server failover** - Automatically switches to backup servers if primary is behind or unavailable
- **Whale alerts** for transfers above configurable threshold (default: 1B QUBIC)
- **Burn notifications** for any token burns (except 1M fee payments to Qutil)
- **USD conversion** using live price from CoinGecko
- **Address labels** from Qubic bundle.json (exchanges, smart contracts, known wallets)
- **Explorer links** - Clickable links to addresses and ticks on Qubic Explorer
- **Auto-reconnect** on connection drops with state persistence
- **Docker support** for easy deployment

## Discord Message Format

```
💸 13,209,684,431 QUBIC (10,951 USD) transferred from HIUQ... to #Mexc
Tick: 40012940

🔥🔥 40,000,000,000 QUBIC (33,120 USD) burned from PYIA...
Tick: 40012941
```

## Configuration

Configure via environment variables or `appsettings.json`:

| Variable | Description | Default |
|----------|-------------|---------|
| `WebSocketUrls__0`, `WebSocketUrls__1`, ... | WebSocket URLs (in order of preference) | - |
| `MaxTickDelay` | Max tick delay before switching servers | `10` |
| `DiscordWebhookUrl` | Discord webhook URL (required) | - |
| `BundleJsonUrl` | URL for address labels | `https://static.qubic.org/v1/general/data/bundle.json` |
| `MinTransferAmount` | Min amount for transfer alerts | `1000000000` (1B) |
| `ReconnectDelaySeconds` | Delay before reconnecting | `5` |
| `ExplorerAddressUrl` | Base URL for address links | `https://explorer.qubic.org/network/address/` |
| `ExplorerTickUrl` | Base URL for tick links | `https://explorer.qubic.org/network/tick/` |
| `PriceApiUrl` | CoinGecko API URL for price | `https://api.coingecko.com/api/v3/simple/price?ids=qubic-network&vs_currencies=usd` |

### Example appsettings.json

```json
{
  "WebSocketUrls": [
    "ws://primary-server:40420/ws/logs",
    "wss://backup-server/ws/logs"
  ],
  "MaxTickDelay": 10,
  "DiscordWebhookUrl": "https://discord.com/api/webhooks/YOUR_WEBHOOK_ID/YOUR_WEBHOOK_TOKEN",
  "MinTransferAmount": 1000000000,
  "ReconnectDelaySeconds": 2
}
```

## Running

### Using .NET CLI

```bash
# Run the application
dotnet run
```

### Using Docker

```bash
# Build and run with docker-compose
docker-compose up -d

# Or pull from Docker Hub
docker pull j0et0m/qubic-transfer-watcher:prod
```

### Docker Compose Example

```yaml
version: '3.8'
services:
  transfer-watcher:
    image: j0et0m/qubic-transfer-watcher:prod
    restart: unless-stopped
    environment:
      WebSocketUrls__0: "ws://primary:40420/ws/logs"
      WebSocketUrls__1: "wss://backup/ws/logs"
      MaxTickDelay: "10"
      DiscordWebhookUrl: "https://discord.com/api/webhooks/..."
      MinTransferAmount: "1000000000"
    volumes:
      - ./data:/app/data  # Persist last processed logId and tick
```

### Using .env file

Create a `.env` file:

```env
WebSocketUrls__0=ws://primary:40420/ws/logs
WebSocketUrls__1=wss://backup/ws/logs
MaxTickDelay=10
DiscordWebhookUrl=https://discord.com/api/webhooks/...
MinTransferAmount=1000000000
```

## Multi-Server Failover

The application supports multiple WebSocket URLs with automatic failover:

1. Connects to the first URL in the list
2. On connection, receives a welcome message with `currentVerifiedTick`
3. Compares against last known tick - if server is more than `MaxTickDelay` ticks behind, switches to next URL
4. On connection failure or disconnect, automatically switches to next URL
5. Cycles through URLs in round-robin fashion

## Data Persistence

The application persists state to the `data/` directory:

- `last_logid.txt` - Last processed log ID (for resuming after restart)
- `last_tick.txt` - Last seen tick (for checking server freshness)

Mount this directory as a volume in Docker to persist state across container restarts.

## Project Structure

```
├── Program.cs                    # Application entry point
├── Models/
│   ├── AppSettings.cs           # Configuration model
│   ├── BundleData.cs            # Address label data models
│   └── QubicEvents.cs           # Transfer/burn event models
├── Services/
│   ├── AddressLabelService.cs   # Resolves addresses to labels
│   ├── DiscordService.cs        # Discord webhook notifications
│   ├── EventProcessor.cs        # Filters and processes events
│   ├── PriceService.cs          # QUBIC price from CoinGecko
│   └── QubicWebSocketClient.cs  # WebSocket connection manager
├── Tests/                       # Unit tests
└── appsettings.json             # Default configuration
```

## Notification Rules

- **Transfers**: Only notifies if amount >= 1B QUBIC (configurable via `MinTransferAmount`)
- **Burns**: Always notifies regardless of amount
- **Fee payments**: 1M transfers to Qutil burn address are ignored (SC fee payments)
- **Emojis** scale with amount:
  - 💸 for transfers
  - 🐋 for whale transfers (10B+)
  - 🔥 to 🔥🔥🔥🔥 for burns based on size

## Address Labels

Known addresses are labeled using Qubic's bundle.json:
- `#Mexc`, `#Gate.io` - Exchanges
- `[QX]`, `[QUTIL]` - Smart contracts
- `$QFT Issuer` - Token issuers
- `🔥 BURN` - Burn addresses

> [!NOTE]
> Large parts of this repository are AI generated. The Code is not well designed or structured.
