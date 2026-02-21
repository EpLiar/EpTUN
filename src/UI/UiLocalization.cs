using System.Text.Json;

namespace EpTUN;

internal enum UiLanguage
{
    English,
    ChineseSimplified
}

internal static class UiLanguageResolver
{
    public static UiLanguage ResolveFromConfigPath(string configPath)
    {
        try
        {
            var resolvedPath = Path.GetFullPath(configPath.Trim());
            if (!File.Exists(resolvedPath))
            {
                return UiLanguage.English;
            }

            var json = File.ReadAllText(resolvedPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions);
            return Resolve(config);
        }
        catch
        {
            return UiLanguage.English;
        }
    }

    public static UiLanguage Resolve(AppConfig? config)
    {
        var normalized = GeneralConfig.NormalizeLanguage(config?.General?.Language);
        return normalized == GeneralConfig.ChineseSimplified
            ? UiLanguage.ChineseSimplified
            : UiLanguage.English;
    }
}

internal readonly struct Localizer
{
    private readonly UiLanguage _language;

    public Localizer(UiLanguage language)
    {
        _language = language;
    }

    public bool IsChineseSimplified => _language == UiLanguage.ChineseSimplified;

    public string Text(string english, string chineseSimplified)
    {
        return IsChineseSimplified ? chineseSimplified : english;
    }
}
