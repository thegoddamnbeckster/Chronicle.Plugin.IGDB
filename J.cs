using System.Text.Json;

namespace Chronicle.Plugin.IGDB;

/// <summary>
/// Tolerant readers for IGDB's JSON. Every accessor checks the kind of the value before reading it and answers null / empty when
/// the property is missing or of another kind, because the host turns a thrown <see cref="KeyNotFoundException"/> into "clear
/// the stored id" and a thrown <see cref="InvalidOperationException"/> into a permanent "Skipped". A parsing slip must never
/// become a permanent state. Presence is tested by kind, never by value: the Main Game type has id 0.
/// </summary>
internal static class J
{
    public static bool TryGet(this JsonElement e, string name, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value);
    }

    public static string? Str(this JsonElement e, string name) =>
        e.TryGet(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long? Long(this JsonElement e, string name) =>
        e.TryGet(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    public static double? Dbl(this JsonElement e, string name) =>
        e.TryGet(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    public static bool? Bool(this JsonElement e, string name) =>
        e.TryGet(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;

    /// <summary>The elements of an array property that are objects (an unexpanded reference is a bare number and is skipped).</summary>
    public static IEnumerable<JsonElement> Objs(this JsonElement e, string name)
    {
        if (e.TryGet(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object) yield return item;
    }

    /// <summary>A property that is an object (an expanded reference), or null.</summary>
    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.TryGet(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static bool Has(this JsonElement e, string name) =>
        e.TryGet(name, out var v) && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined;

    /// <summary>The names of an expanded array property (<c>genres</c> -> each element's <c>name</c>), blank and repeated ones removed.</summary>
    public static List<string> Names(this JsonElement e, string array, string field = "name")
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in e.Objs(array))
        {
            var n = o.Str(field)?.Trim();
            if (!string.IsNullOrEmpty(n) && seen.Add(n)) list.Add(n);
        }
        return list;
    }
}
