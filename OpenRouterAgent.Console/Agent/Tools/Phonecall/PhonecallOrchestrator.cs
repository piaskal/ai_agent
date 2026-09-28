using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Phonecall;

public sealed class PhonecallOrchestrator
{
    private const int MaxFollowUps = 4;

    private readonly PhonecallApiClient _apiClient;
    private readonly EdgeTtsSynthesizer _synthesizer;
    private readonly PhonecallTranscriber _transcriber;
    private readonly ILogger<PhonecallOrchestrator> _logger;

    public PhonecallOrchestrator(
        PhonecallApiClient apiClient,
        EdgeTtsSynthesizer synthesizer,
        PhonecallTranscriber transcriber,
        ILogger<PhonecallOrchestrator> logger)
    {
        _apiClient = apiClient;
        _synthesizer = synthesizer;
        _transcriber = transcriber;
        _logger = logger;
    }

    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var clips = new Dictionary<string, (string Text, byte[] Audio, string Heard)>(StringComparer.Ordinal);
        foreach (var (id, text) in PhonecallLines.All)
        {
            clips[id] = await SynthesizeVerifiedAsync(id, text, cancellationToken);
        }

        var turns = new List<object>();
        var start = await _apiClient.StartAsync(cancellationToken);
        turns.Add(start.ToPublicObject(null));
        if (PhonecallDialogue.IsComplete(start))
        {
            return FormatResult(start.Message, turns);
        }

        var intro = await SendAsync(PhonecallLines.Intro, clips, cancellationToken);
        turns.Add(intro.PublicTurn);
        Require(intro.Reply.Code == PhonecallDialogue.IdentityConfirmed, PhonecallLines.Intro, intro);

        var roads = await SendAsync(PhonecallLines.Roads, clips, cancellationToken);
        turns.Add(roads.PublicTurn);
        Require(roads.Reply.Code == PhonecallDialogue.RoadStatusDelivered, PhonecallLines.Roads, roads);

        var current = await SendAsync(PhonecallLines.DisableMonitoring, clips, cancellationToken);
        turns.Add(current.PublicTurn);
        var sent = new HashSet<string>(StringComparer.Ordinal)
        {
            PhonecallLines.Intro,
            PhonecallLines.Roads,
            PhonecallLines.DisableMonitoring
        };

        for (var attempt = 0; attempt < MaxFollowUps && !PhonecallDialogue.IsComplete(current.Reply); attempt++)
        {
            var nextId = PhonecallDialogue.ChooseFollowUp(current.Reply, current.OperatorTranscript);
            if (nextId is null)
            {
                throw new InvalidOperationException(
                    $"Phonecall stopped after '{current.LineId}'. Hub: {current.Reply.Describe()}. Operator: {current.OperatorTranscript}");
            }

            if (!sent.Add(nextId))
            {
                throw new InvalidOperationException($"Phonecall would repeat '{nextId}'. Hub: {current.Reply.Describe()}");
            }

            current = await SendAsync(nextId, clips, cancellationToken);
            turns.Add(current.PublicTurn);
        }

        if (!PhonecallDialogue.IsComplete(current.Reply))
        {
            throw new InvalidOperationException($"Phonecall finished without a flag. Last hub reply: {current.Reply.Describe()}");
        }

        return FormatResult(current.Reply.Message, turns);
    }

    private async Task<(string Text, byte[] Audio, string Heard)> SynthesizeVerifiedAsync(
        string id,
        string text,
        CancellationToken cancellationToken)
    {
        string? lastHeard = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var audio = await _synthesizer.SynthesizeAsync(text, cancellationToken);
            lastHeard = await _transcriber.TranscribeAsync(audio, cancellationToken);
            _logger.LogInformation("TTS {LineId} attempt {Attempt}: {Transcript}", id, attempt, lastHeard);
            if (PhonecallLines.SpokenLineMatches(id, lastHeard))
            {
                return (text, audio, lastHeard);
            }
        }

        throw new InvalidOperationException(
            $"TTS for '{id}' did not match the required wording. Heard: {lastHeard}");
    }

    private async Task<SpokenTurn> SendAsync(
        string id,
        IReadOnlyDictionary<string, (string Text, byte[] Audio, string Heard)> clips,
        CancellationToken cancellationToken)
    {
        var clip = clips[id];
        _logger.LogInformation("Sending {LineId}: {Text}", id, clip.Text);
        var reply = await _apiClient.SendAudioAsync(Convert.ToBase64String(clip.Audio), cancellationToken);
        string? operatorTranscript = null;
        if (!string.IsNullOrWhiteSpace(reply.AudioBase64))
        {
            var audio = Convert.FromBase64String(reply.AudioBase64);
            operatorTranscript = await _transcriber.TranscribeAsync(audio, cancellationToken);
            _logger.LogInformation("Operator ({LineId}): {Transcript}", id, operatorTranscript);
        }

        if (reply.Code is < 0)
        {
            throw new InvalidOperationException(
                $"Operator rejected '{clip.Text}'. {reply.Describe()}. Operator: {operatorTranscript}");
        }

        return new SpokenTurn(id, reply, operatorTranscript);
    }

    private static void Require(bool condition, string lineId, SpokenTurn turn)
    {
        if (condition || PhonecallDialogue.IsComplete(turn.Reply))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Unexpected reply after '{lineId}'. {turn.Reply.Describe()}. Operator: {turn.OperatorTranscript}");
    }

    private static string FormatResult(string? flag, List<object> turns) =>
        JsonSerializer.Serialize(
            new
            {
                flag,
                passableRoad = "RD820",
                turns
            },
            new JsonSerializerOptions { WriteIndented = true });

    private sealed record SpokenTurn(string LineId, PhonecallReply Reply, string? OperatorTranscript)
    {
        public object PublicTurn => Reply.ToPublicObject(OperatorTranscript);
    }
}
