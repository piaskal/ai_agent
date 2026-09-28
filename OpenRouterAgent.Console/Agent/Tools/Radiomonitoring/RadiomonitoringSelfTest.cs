using System.Text;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Radiomonitoring;

public static class RadiomonitoringSelfTest
{
    public static bool Run(TextWriter output)
    {
        var failures = new List<string>();

        Expect(failures, "noise-static", SignalRouter.IsNoise("krzzzzzzzzzzzz"), true);
        Expect(failures, "noise-short", SignalRouter.IsNoise("ok"), true);
        Expect(failures, "keep-polish", SignalRouter.IsNoise("Miasto ocalałych, nazywane Syjonem, leży nad rzeką."), false);

        var jsonCapture = new RadioCapture
        {
            Code = 100,
            Message = "Signal captured.",
            Meta = "application/json",
            Attachment = Convert.ToBase64String("""{"city":"Testowo","area":12.345}"""u8.ToArray()),
            Filesize = 32
        };
        var jsonRouted = SignalRouter.Route(1, jsonCapture);
        Expect(failures, "json-kind", jsonRouted.Kind == RouteKind.StructuredText, true);
        Expect(failures, "json-local", jsonRouted.LocalText?.Contains("Testowo", StringComparison.Ordinal) == true, true);

        var pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        var pngCapture = new RadioCapture
        {
            Code = 100,
            Message = "Signal captured.",
            Meta = "application/octet-stream",
            Attachment = Convert.ToBase64String(pngHeader),
            Filesize = pngHeader.Length
        };
        var pngRouted = SignalRouter.Route(2, pngCapture);
        Expect(failures, "png-kind", pngRouted.Kind == RouteKind.Image, true);
        Expect(failures, "png-mime", pngRouted.DetectedMime == "image/png", true);

        var done = new RadioCapture { Code = 1, Message = "Masz już wystarczająco dużo danych do analizy." };
        Expect(failures, "session-complete", SignalRouter.IsSessionComplete(done), true);

        Expect(failures, "round-up", ReportFormatter.FormatArea(12.345m) == "12.35", true);
        Expect(failures, "round-down", ReportFormatter.FormatArea(12.344m) == "12.34", true);
        Expect(failures, "two-decimals", ReportFormatter.FormatArea(12.3m) == "12.30", true);

        var draft = ReportExtractor.ParseDraft(
            """{"cityName":"Testowo","cityAreaRaw":"12,345","areaLength":null,"areaWidth":null,"warehousesCount":3,"warehouseNames":["A","B","C"],"phoneNumber":"+48 123-456-789"}""");
        var report = ReportFormatter.FromDraft(draft);
        Expect(failures, "draft-area", report.CityArea == "12.35", true);
        Expect(failures, "draft-warehouses", report.WarehousesCount == 3, true);
        Expect(failures, "draft-phone", report.PhoneNumber == "+48123456789", true);

        var dimensions = ReportFormatter.FromDraft(new RadioReportDraft
        {
            CityName = "Testowo",
            AreaLength = 3.14m,
            AreaWidth = 2m,
            WarehousesCount = 1,
            PhoneNumber = "123456789"
        });
        Expect(failures, "area-multiply", dimensions.CityArea == "6.28", true);

        if (failures.Count == 0)
        {
            output.WriteLine("radiomonitoring self-test: passed");
            return true;
        }

        output.WriteLine("radiomonitoring self-test: failed");
        foreach (var failure in failures)
        {
            output.WriteLine(" - " + failure);
        }

        return false;
    }

    private static void Expect(List<string> failures, string name, bool actual, bool expected)
    {
        if (actual != expected)
        {
            failures.Add($"{name}: expected {expected}, got {actual}");
        }
    }

    public static string RunToString()
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder);
        Run(writer);
        return builder.ToString();
    }
}
