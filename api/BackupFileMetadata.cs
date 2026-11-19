using System.Globalization;
using System.Text.RegularExpressions;

public static class BackupFileMetadata
{
    public static bool SafeName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 240
        && !name.Contains('/') && !name.Contains('\\') && !name.Contains("..") && !name.Any(char.IsControl);
    public static bool CanDownload(string name) => SafeName(name) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    public static (string Type, DateTimeOffset? CreatedAt) Parse(string name)
    {
        var match = Regex.Match(name, @"_(\d{8}_\d{6})_[a-fA-F0-9]{32}_(full|differential|log)\.zip(?:\.uploading)?$", RegexOptions.IgnoreCase);
        if (!match.Success) {
            var legacy=Regex.Match(name,@"-\d{14}-\([a-fA-F0-9-]{36}\)-(Full|Diff|Differential|Log)\.zip$",RegexOptions.IgnoreCase);
            return legacy.Success?(legacy.Groups[1].Value.ToLowerInvariant() switch{"full"=>"Full","diff" or "differential"=>"Differential",_=>"Log"},null):("Unknown",null);
        }
        DateTimeOffset? time = DateTimeOffset.TryParseExact(match.Groups[1].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
        return (match.Groups[2].Value.ToLowerInvariant() switch { "full" => "Full", "differential" => "Differential", _ => "Log" }, time);
    }
}
