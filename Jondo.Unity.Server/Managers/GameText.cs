using System;
using Jondo.Unity.Launcher;
using Jondo.Unity.World.Client;

namespace Jondo.Unity.Server.Managers
{
    /// <summary>
    /// The names the server writes itself (chat lines, quotes, the F9/F10 windows) in the player's
    /// language. world.db's Translations table is Spanish; the client ships every language in
    /// Content/I18n/{lang}.bin with the same keys, so a key is looked up there first (French by
    /// default) and the database is only the fallback.
    /// </summary>
    public static class GameText
    {
        /// <summary>The client text table to use: "fr", "en", "es", "de" or "pt".</summary>
        public static string Language { get; set; } = "fr";

        private static ClientText? _table;
        private static bool _tried;
        private static readonly object Gate = new();

        private static ClientText? Table
        {
            get
            {
                if (_tried) return _table;
                lock (Gate)
                {
                    if (_tried) return _table;
                    try
                    {
                        var language = Language switch
                        {
                            "en" => GameLanguage.English, "es" => GameLanguage.Spanish, "de" => GameLanguage.German,
                            "pt" => GameLanguage.Portuguese, _ => GameLanguage.French,
                        };
                        _table = ClientText.Open(Paths.ClientTextFile(Language), language,
                                                 m => Console.WriteLine($"[GameText] {m}"));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GameText] The client text table could not be opened: {ex.Message}");
                    }
                    _tried = true;
                    return _table;
                }
            }
        }

        /// <summary>The text of a key in the player's language, or <paramref name="fallback"/> (the database's) when missing.</summary>
        public static string Of(long key, string? fallback)
        {
            string text = Table?.Of(key) ?? ClientText.Missing;
            return text.Length > 0 ? text : fallback ?? "";
        }
    }
}
