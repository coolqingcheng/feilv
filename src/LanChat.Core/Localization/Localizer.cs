using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LanChat.Core.Localization;

/// <summary>AOT-safe, embedded translations. Protocol/status values remain invariant in storage and on the wire.</summary>
public static class Localizer
{
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    public static IReadOnlyDictionary<string, string[]> Catalog { get; } = Load();
    private static readonly Dictionary<string, string> English = Catalog.Values.ToDictionary(x => x[0], x => x[1], StringComparer.Ordinal);
    private static readonly Template[] Templates = Catalog.Values.Where(x => x[0].Contains('{'))
        .Select(x => new Template(x[0], x[1])).OrderByDescending(x => x.LiteralLength).ToArray();
    public static string Preference { get; private set; } = "System";
    public static string Language => ResolveLanguage(Preference, SystemCulture);
    public static event Action? Changed;

    public static string Normalize(string? preference) => preference is "zh-CN" or "en-US" ? preference : "System";
    public static string ResolveLanguage(string? preference, CultureInfo systemCulture) => Normalize(preference) switch
    {
        "zh-CN" => "zh-CN", "en-US" => "en-US", _ => systemCulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en-US"
    };
    public static void SetPreference(string? preference)
    {
        var value = Normalize(preference);
        if (Preference == value) return;
        Preference = value;
        Changed?.Invoke();
    }

    public static string Text(string source) => Text(source, Language);
    public static string Text(string source, string language) => language == "zh-CN" ? source : English.GetValueOrDefault(source, source);

    /// <summary>Localizes presentation-only status strings, including legacy backend errors. Captured names/paths are opaque;
    /// only the explicit {error} and {state} placeholders contain other translatable application text.</summary>
    public static string Translate(string? source) => Translate(source, Language);
    public static string Translate(string? source, string language) => Translate(source ?? "", language, 0);
    private static string Translate(string source, string language, int depth)
    {
        if (language == "zh-CN" || source.Length == 0 || depth > 4) return source;
        if (English.TryGetValue(source, out var exact)) return exact;
        foreach (var template in Templates)
            if (template.TryTranslate(source, language, depth, out var result)) return result;
        return source;
    }

    private static Dictionary<string, string[]> Load()
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream("LanChat.Core.Localization.Strings.json")!;
        return JsonSerializer.Deserialize(stream, TranslationJsonContext.Default.DictionaryStringStringArray)!;
    }

    private sealed class Template
    {
        private readonly string[] _literals;
        private readonly string[] _names;
        private readonly string _target;
        public int LiteralLength { get; }
        public Template(string source, string target)
        {
            var literals = new List<string>(); var names = new List<string>(); var offset = 0;
            while (source.IndexOf('{', offset) is var open && open >= 0)
            {
                var close = source.IndexOf('}', open);
                literals.Add(source[offset..open]); names.Add(source[(open + 1)..close]); offset = close + 1;
            }
            literals.Add(source[offset..]); _literals = literals.ToArray(); _names = names.ToArray();
            _target = target; LiteralLength = _literals.Sum(x => x.Length);
        }
        public bool TryTranslate(string source, string language, int depth, out string result)
        {
            result = "";
            if (!source.StartsWith(_literals[0], StringComparison.Ordinal) || !source.EndsWith(_literals[^1], StringComparison.Ordinal)) return false;
            var values = new string[_names.Length]; var offset = _literals[0].Length;
            for (var i = 0; i < values.Length; i++)
            {
                var next = _literals[i + 1];
                var end = i == values.Length - 1 ? source.Length - next.Length : source.IndexOf(next, offset, StringComparison.Ordinal);
                if (end < offset) return false;
                var value = source[offset..end];
                values[i] = _names[i] is "error" or "state" ? Translate(value, language, depth + 1) : value;
                offset = end + next.Length;
            }
            // Render once: braces or template-like text inside filenames can never become substitutions.
            var builder = new StringBuilder(); offset = 0;
            while (_target.IndexOf('{', offset) is var open && open >= 0)
            {
                var close = _target.IndexOf('}', open);
                builder.Append(_target.AsSpan(offset, open - offset));
                builder.Append(values[Array.IndexOf(_names, _target[(open + 1)..close])]); offset = close + 1;
            }
            result = builder.Append(_target.AsSpan(offset)).ToString(); return true;
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, string[]>))]
internal partial class TranslationJsonContext : JsonSerializerContext;
