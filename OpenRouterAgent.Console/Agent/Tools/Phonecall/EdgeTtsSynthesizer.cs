using System.Diagnostics;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Phonecall;

public sealed class EdgeTtsSynthesizer
{
    public const string Voice = "pl-PL-MarekNeural";

    private const string PythonScript = """
        import asyncio, sys
        import edge_tts
        text = sys.stdin.read()
        out = sys.argv[1]
        asyncio.run(edge_tts.Communicate(text, "pl-PL-MarekNeural").save(out))
        """;

    public async Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"phonecall-{Guid.NewGuid():N}.mp3");
        var startInfo = new ProcessStartInfo
        {
            FileName = "python3",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(PythonScript);
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start python3 for edge-tts.");

        await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        try
        {
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new InvalidOperationException(
                    "Polish TTS failed. Install edge-tts with `pip install edge-tts` and retry. " + stderr.Trim());
            }

            return await File.ReadAllBytesAsync(outputPath, cancellationToken);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
