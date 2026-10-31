using System.Globalization;
using System.Text;
using Sss.Web.Models;

namespace Sss.Web.Services;

public static class CsvWriter
{
    private static readonly string[] Header =
    [
        "Uid", "Resource", "Type", "DeliveryGroup", "Machine", "State", "Started", "Ended",
        "IdleSeconds", "ClientName", "ClientAddress", "IcaRttMs", "BandwidthKbps", "LogonSeconds",
        "LoadIndex", "ClientSideRating", "BackendRating",
    ];

    public static string Write(IEnumerable<SessionInfo> sessions)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', Header));
        foreach (var s in sessions)
        {
            var q = s.Quality;
            string[] cells =
            [
                s.Uid, s.ResourceName, s.ResourceType, s.DeliveryGroup, s.Machine, s.State.ToString(),
                s.StartTime.ToString("o", CultureInfo.InvariantCulture),
                s.EndTime?.ToString("o", CultureInfo.InvariantCulture) ?? "",
                N(s.IdleSeconds), s.ClientName ?? "", s.ClientAddress ?? "",
                N(q.IcaRttMs), N(q.BandwidthKbps), N(q.LogonSeconds), N(q.LoadIndex),
                q.Frontend.ToString(), q.Backend.ToString(),
            ];
            sb.AppendLine(string.Join(',', cells.Select(Escape)));
        }
        return sb.ToString();
    }

    private static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>RFC 4180 quoting plus neutralisation of spreadsheet formula injection.</summary>
    public static string Escape(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
