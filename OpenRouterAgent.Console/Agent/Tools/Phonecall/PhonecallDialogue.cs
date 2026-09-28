namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Phonecall;

public static class PhonecallDialogue
{
    public const int IdentityConfirmed = 120;
    public const int RoadStatusDelivered = 150;
    public const int PasswordRequired = 160;

    public static bool IsComplete(PhonecallReply reply) => reply.HasFlag;

    public static string? ChooseFollowUp(PhonecallReply reply, string? operatorTranscript)
    {
        if (IsComplete(reply))
        {
            return null;
        }

        var blob = $"{reply.Message}\n{reply.Hint}\n{operatorTranscript}".ToLowerInvariant();
        if (reply.Code == PasswordRequired ||
            blob.Contains("hasło", StringComparison.Ordinal) ||
            blob.Contains("haslo", StringComparison.Ordinal) ||
            blob.Contains("password", StringComparison.Ordinal))
        {
            return PhonecallLines.Password;
        }

        if (blob.Contains("dlaczego", StringComparison.Ordinal) ||
            blob.Contains("po co", StringComparison.Ordinal) ||
            blob.Contains("uzasadn", StringComparison.Ordinal))
        {
            return PhonecallLines.Food;
        }

        return null;
    }

    public static bool RunSelfTest(TextWriter output)
    {
        var failures = new List<string>();

        void Expect(bool condition, string name)
        {
            if (!condition)
            {
                failures.Add(name);
            }
        }

        Expect(PhonecallLines.SpokenLineMatches(PhonecallLines.Intro, "Dzień dobry, z tej strony Tymon Gajewski."), "intro");
        Expect(PhonecallLines.SpokenLineMatches(PhonecallLines.Roads, "status RD 224, RD 472 oraz RD 820 dla generała Zygfryda"), "roads");
        Expect(!PhonecallLines.SpokenLineMatches(PhonecallLines.Roads, "status RD 224, RD 472 oraz RD 820"), "roads require zygfryd");
        Expect(PhonecallLines.SpokenLineMatches(PhonecallLines.DisableMonitoring, "wyłączyć monitoring na RD 820, operacja Zygfryda"), "disable");
        Expect(PhonecallLines.SpokenLineMatches(PhonecallLines.Password, "Hasło brzmi Barbakan."), "password");

        var passwordAsk = new PhonecallReply { Code = PasswordRequired, Message = "Password required." };
        Expect(ChooseFollowUp(passwordAsk, "muszę usłyszeć hasło") == PhonecallLines.Password, "follow password");

        var why = new PhonecallReply { Code = 170, Message = "Question." };
        Expect(ChooseFollowUp(why, "Dlaczego chcesz wyłączyć monitoring?") == PhonecallLines.Food, "follow food");

        var done = new PhonecallReply { Code = 0, Message = "{FLG:EXAMPLE}" };
        Expect(IsComplete(done), "flag completes");
        Expect(ChooseFollowUp(done, "Monitoring wyłączony.") is null, "no follow-up after flag");

        var unexpected = new PhonecallReply { Code = 150, Message = "Road status delivered." };
        Expect(ChooseFollowUp(unexpected, "Jedź drogą RD820.") is null, "status is not a follow-up");

        if (failures.Count == 0)
        {
            output.WriteLine("phonecall self-test passed");
            return true;
        }

        foreach (var failure in failures)
        {
            output.WriteLine($"FAIL {failure}");
        }

        return false;
    }
}
