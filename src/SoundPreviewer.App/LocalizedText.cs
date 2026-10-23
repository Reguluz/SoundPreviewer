global using static SoundPreviewer.App.LocalizedText;

namespace SoundPreviewer.App;

internal static class LocalizedText
{
    public static string T(string? text) => Core.Services.LanguageService.Current.Translate(text);
}
