namespace QubicTransferWatcher.Models;

/// <summary>
/// Qubic WebSocket log type constants
/// </summary>
public static class QubicLogTypes
{
    /// <summary>
    /// QU_TRANSFER - Regular transfer between addresses
    /// </summary>
    public const int QuTransfer = 0;

    /// <summary>
    /// BURNING - Token burn event
    /// </summary>
    public const int Burning = 8;
}
