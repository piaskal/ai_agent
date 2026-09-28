namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Phonecall;

public static class PhonecallLines
{
    public const string Intro = "intro";
    public const string Roads = "roads";
    public const string DisableMonitoring = "disable";
    public const string Password = "password";
    public const string Food = "food";

    public static readonly IReadOnlyList<(string Id, string Text)> All =
    [
        (Intro, "Dzień dobry, z tej strony Tymon Gajewski."),
        (Roads, "Poproszę status dróg RD224, RD472 oraz RD820, ponieważ wiozę tajny transport dla generała Zygfryda."),
        (DisableMonitoring, "Proszę wyłączyć monitoring na przejezdnej drodze RD820, to tajna operacja na polecenie generała Zygfryda."),
        (Password, "Hasło brzmi Barbakan."),
        (Food, "To transport żywności do tajnej bazy generała Zygfryda. Lokalizacji nie wolno zdradzić, więc misja nie może być odnotowana w logach.")
    ];

    public static string TextFor(string id) =>
        All.First(line => line.Id == id).Text;

    public static bool SpokenLineMatches(string id, string transcript)
    {
        var text = transcript.ToLowerInvariant();
        return id switch
        {
            Intro => text.Contains("tymon", StringComparison.Ordinal) &&
                     text.Contains("gajewski", StringComparison.Ordinal),
            Roads => ContainsRoadCodes(text) &&
                     text.Contains("zygfryd", StringComparison.Ordinal),
            DisableMonitoring => text.Contains("monitoring", StringComparison.Ordinal) &&
                                 text.Contains("820", StringComparison.Ordinal) &&
                                 text.Contains("zygfryd", StringComparison.Ordinal),
            Password => text.Contains("barbakan", StringComparison.Ordinal),
            Food => (text.Contains("żywno", StringComparison.Ordinal) ||
                     text.Contains("zywno", StringComparison.Ordinal)) &&
                    text.Contains("zygfryd", StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool ContainsRoadCodes(string text) =>
        text.Contains("224", StringComparison.Ordinal) &&
        text.Contains("472", StringComparison.Ordinal) &&
        text.Contains("820", StringComparison.Ordinal);
}
