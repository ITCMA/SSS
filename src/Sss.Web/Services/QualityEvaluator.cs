using Sss.Web.Models;

namespace Sss.Web.Services;

/// <summary>Turns raw metrics into the two traffic lights shown to the user.
/// Frontend = the user's device and network (ICA round-trip time, available bandwidth);
/// Backend = datacenter and VDA (logon duration, server load). A missing metric is "unknown"
/// and never counts as bad; the worst known rating of a pair wins.</summary>
public sealed class QualityEvaluator(QualityThresholds t)
{
    public SessionQuality Evaluate(int? rttMs, int? bandwidthKbps, int? logonSeconds, int? loadIndex) => new(
        rttMs, bandwidthKbps, logonSeconds, loadIndex,
        Combine(HigherIsWorse(rttMs, t.RttWarnMs, t.RttCritMs),
                LowerIsWorse(bandwidthKbps, t.BandwidthWarnKbps, t.BandwidthCritKbps)),
        Combine(HigherIsWorse(logonSeconds, t.LogonWarnSeconds, t.LogonCritSeconds),
                HigherIsWorse(loadIndex, t.LoadWarn, t.LoadCrit)));

    public static Health HigherIsWorse(int? value, int warn, int crit) => value switch
    {
        null => Health.Unknown,
        var v when v >= crit => Health.Critical,
        var v when v >= warn => Health.Warning,
        _ => Health.Good,
    };

    public static Health LowerIsWorse(int? value, int warn, int crit) => value switch
    {
        null => Health.Unknown,
        var v when v <= crit => Health.Critical,
        var v when v <= warn => Health.Warning,
        _ => Health.Good,
    };

    /// <summary>Worst known rating wins; Unknown only if nothing is known.</summary>
    public static Health Combine(params Health[] ratings)
    {
        var known = ratings.Where(r => r != Health.Unknown).ToArray();
        return known.Length == 0 ? Health.Unknown : known.Max();
    }
}
