using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OES_Leak_Monitor;

/// <summary>
/// A redacted copy of <c>settings.json</c>, written into the day folder beside the recordings it
/// applies to.
///
/// <para><b>Why.</b> The data lives wherever the operator pointed the logger — a fab machine's
/// <c>C:\DualOES</c> — and everything needed to read it lives in <c>%AppData%</c>: the save
/// threshold and trigger mode that decide which frames were even evaluated, the ratio definitions,
/// the wavelength corrections, the active Golden Run. Copying a day folder off the machine takes
/// the first and leaves the second, and the recordings are then close to unreadable. On
/// 2026-08-19 that cost a day's leak testing: the threshold had to be bounded by arithmetic on the
/// frame that opened the gate, and the acquisition mode inferred from the frame interval. Both
/// were sitting in a file nobody thought to copy.</para>
///
/// <para><b>Redacted.</b> <c>settings.json</c> carries the access-control user list, password
/// hashes included. Those must never travel with the data, so the whole section is removed, and
/// any property named <c>passwordHash</c> is stripped wherever it appears — the second rule is
/// there because the first depends on the shape staying as it is.</para>
///
/// <para>Everything else is copied wholesale rather than picked field by field: a snapshot that
/// lists what to include is a snapshot that silently omits whatever gets added next.</para>
/// </summary>
public static class ConfigSnapshot
{
    /// <summary>Filename prefix. Leading underscore so it sorts away from the recordings and can
    /// never be mistaken for one.</summary>
    public const string Prefix = "_config_";

    /// <summary>Top-level sections removed before writing.</summary>
    private static readonly string[] RedactedSections = { "accessControl" };

    /// <summary>Property names stripped wherever they appear in the tree.</summary>
    private static readonly string[] RedactedProperties = { "passwordHash" };

    /// <summary>
    /// Writes a snapshot into <paramref name="dayFolder"/> unless one with identical content is
    /// already the newest there — the settings usually do not change between recordings, and a
    /// copy per recording would bury the folder.
    /// </summary>
    /// <returns>The path written, or null when nothing needed writing. Failures are reported
    /// through <paramref name="error"/> rather than thrown: the recording is what matters.</returns>
    public static string? TryWrite(string dayFolder, AppSettings settings, DateTime nowLocal,
                                   out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(dayFolder) || settings is null) return null;
        try
        {
            var json = Redact(settings);
            if (json == NewestExisting(dayFolder)) return null;

            var path = Path.Combine(dayFolder, $"{Prefix}{nowLocal:HHmmss}.json");
            File.WriteAllText(path, json);
            return path;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>The snapshot text for these settings — <c>settings.json</c> minus the secrets.</summary>
    public static string Redact(AppSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, SettingsService.JsonOptions)
                   ?? throw new InvalidOperationException("settings did not serialise");
        return RedactNode(node);
    }

    /// <summary>
    /// The same two rules applied to settings JSON that is only ever text — the
    /// <c>settings.json.bak-*</c> files beside the live one, which the diagnostic bundle carries
    /// because "it worked last week" is only checkable against them.
    ///
    /// <para>They must not be deserialised into <see cref="AppSettings"/> first: a backup written
    /// by an older build may no longer round-trip, and a redactor that throws on the file it was
    /// pointed at fails open — it leaves the file out, or worse, someone reaches for the raw one.
    /// Working on the node means anything that parses as JSON can be redacted, whatever schema it
    /// happens to be.</para>
    /// </summary>
    /// <returns>The redacted text, or null when <paramref name="json"/> does not parse.</returns>
    public static string? RedactJsonText(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        return node is null ? null : RedactNode(node);
    }

    /// <summary>The one place the two rules are applied, whatever produced the node.</summary>
    private static string RedactNode(JsonNode node)
    {
        if (node is JsonObject root)
            foreach (var section in RedactedSections) root.Remove(section);
        Strip(node);
        return node.ToJsonString(SettingsService.JsonOptions);
    }

    /// <summary>Removes the redacted property names anywhere below <paramref name="node"/>.</summary>
    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key)
                                        .Where(k => RedactedProperties.Contains(k, StringComparer.OrdinalIgnoreCase))
                                        .ToList())
                    obj.Remove(name);
                foreach (var child in obj.ToList()) Strip(child.Value);
                break;
            case JsonArray arr:
                foreach (var child in arr) Strip(child);
                break;
        }
    }

    /// <summary>Content of the most recent snapshot already in the folder, or null.</summary>
    private static string? NewestExisting(string dayFolder)
    {
        if (!Directory.Exists(dayFolder)) return null;
        var newest = new DirectoryInfo(dayFolder).GetFiles($"{Prefix}*.json")
                                                 .OrderByDescending(f => f.Name)
                                                 .FirstOrDefault();
        if (newest is null) return null;
        try { return File.ReadAllText(newest.FullName); }
        catch { return null; }
    }
}
