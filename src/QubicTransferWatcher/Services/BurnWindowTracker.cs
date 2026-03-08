using Serilog;

namespace QubicTransferWatcher.Services;

/// <summary>
/// Tracks burns within a sliding time window and fires an alert when the
/// cumulative total exceeds a configured threshold.
/// </summary>
public class BurnWindowTracker
{
    private readonly ILogger _log = Log.ForContext<BurnWindowTracker>();
    private readonly TimeSpan _window;
    private readonly long _threshold;
    private readonly List<(DateTime Timestamp, long Amount)> _burns = new();
    private readonly object _lock = new();
    private bool _alertFired;

    public BurnWindowTracker(int windowMinutes, long threshold)
    {
        _window = TimeSpan.FromMinutes(windowMinutes);
        _threshold = threshold;
    }

    /// <summary>
    /// Records a burn event and returns an alert if the window threshold is
    /// crossed for the first time (resets when total drops back below).
    /// </summary>
    public BurnWindowAlert? RecordBurn(DateTime timestamp, long amount)
    {
        lock (_lock)
        {
            _burns.Add((timestamp, amount));
            Prune(timestamp);

            var total = _burns.Sum(b => b.Amount);
            var count = _burns.Count;

            if (total >= _threshold && !_alertFired)
            {
                _alertFired = true;
                _log.Information("Burn window threshold crossed: {Total} across {Count} burns in {Window} min",
                    total, count, _window.TotalMinutes);
                return new BurnWindowAlert(total, count, (int)_window.TotalMinutes);
            }

            if (total < _threshold && _alertFired)
            {
                _alertFired = false;
            }

            return null;
        }
    }

    private void Prune(DateTime now)
    {
        var cutoff = now - _window;
        _burns.RemoveAll(b => b.Timestamp < cutoff);
    }
}

public record BurnWindowAlert(long TotalAmount, int BurnCount, int WindowMinutes);
