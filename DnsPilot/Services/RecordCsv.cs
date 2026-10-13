using System.Text;
using DnsPilot.ViewModels;

namespace DnsPilot.Services;

/// <summary>
/// CSV-Export der Eintragstabelle. Semikolon als Trenner, weil ein deutsches
/// Excel ein Komma nicht als Spaltentrenner erkennt.
/// </summary>
public static class RecordCsv
{
    public static string Build(IEnumerable<RecordRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("Name;Typ;Daten;Zone;TTL;Zeitstempel;Erreichbarkeit;Auflösung;Hinweise").Append("\r\n");

        foreach (var row in rows)
        {
            sb.Append(Field(row.Name)).Append(';')
              .Append(Field(row.TypeText)).Append(';')
              .Append(Field(row.Data)).Append(';')
              .Append(Field(row.Zone)).Append(';')
              .Append(row.Record.Ttl).Append(';')
              .Append(Field(row.TimestampText)).Append(';')
              .Append(Field(row.ReachabilityText)).Append(';')
              .Append(Field(row.ResolveText ?? string.Empty)).Append(';')
              .Append(Field(row.FindingsText)).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>Anführungszeichen, wenn Trenner, Zeilenumbruch oder Anführungszeichen vorkommen.</summary>
    internal static string Field(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
}
