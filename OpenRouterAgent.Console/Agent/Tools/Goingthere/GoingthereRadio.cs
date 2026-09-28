using System.Text.RegularExpressions;

namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Goingthere;

public static partial class GoingthereRadio
{
    public static string? Classify(string hint)
    {
        var text = " " + Whitespace().Replace(hint.ToLowerInvariant(), " ").Replace("-", " ", StringComparison.Ordinal) + " ";
        text = RightAt().Replace(text, " directly ");
        text = text.Replace("without turning", "going straight", StringComparison.Ordinal)
            .Replace("not beside the hull", "both sides are clear", StringComparison.Ordinal)
            .Replace("beside you", "on both sides", StringComparison.Ordinal)
            .Replace("not before you", "danger in front", StringComparison.Ordinal)
            .Replace("port and starboard", "both sides", StringComparison.Ordinal)
            .Replace("starboard and port", "both sides", StringComparison.Ordinal);

        var scores = new Dictionary<string, int>
        {
            ["port"] = 0,
            ["starboard"] = 0,
            ["front"] = 0
        };
        var lastLateral = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sentence in SplitParts(SentenceSplit(), text))
        {
            var pending = new HashSet<string>(StringComparer.Ordinal);
            foreach (var piece in SplitParts(PieceSplit(), sentence))
            {
                var sides = FindSides(piece);
                var polarity = Polarity(piece);
                if (polarity == "danger" && (piece.Contains("other side", StringComparison.Ordinal) || piece.Contains("opposite", StringComparison.Ordinal)))
                {
                    if (lastLateral.SetEquals(["starboard"]))
                    {
                        sides = ["port"];
                    }
                    else if (lastLateral.SetEquals(["port"]))
                    {
                        sides = ["starboard"];
                    }
                }

                if (polarity is not null && sides.Count == 0 && pending.Count > 0)
                {
                    sides = pending;
                    pending = new HashSet<string>(StringComparer.Ordinal);
                }

                if (sides.Count > 0 && polarity is null)
                {
                    pending = sides;
                    var lateral = Lateral(sides);
                    if (lateral.Count > 0)
                    {
                        lastLateral = lateral;
                    }

                    continue;
                }

                if (sides.Count == 0 || polarity is null)
                {
                    continue;
                }

                var delta = polarity == "danger" ? 2 : -2;
                foreach (var side in sides)
                {
                    scores[side] += delta;
                }

                var scoredLateral = Lateral(sides);
                if (scoredLateral.Count > 0)
                {
                    lastLateral = scoredLateral;
                }
            }
        }

        var ranked = scores.OrderByDescending(pair => pair.Value).ToArray();
        if (ranked[0].Value > 0 && ranked[0].Value > ranked[1].Value)
        {
            return ranked[0].Key;
        }

        if (scores["port"] < 0 && scores["starboard"] < 0 && scores["front"] > Math.Max(scores["port"], scores["starboard"]))
        {
            return "front";
        }

        return null;
    }

    public static bool RunSelfTest(TextWriter output)
    {
        var failed = 0;
        foreach (var (hint, expected) in Examples)
        {
            var actual = Classify(hint);
            if (actual != expected)
            {
                failed++;
                output.WriteLine($"FAIL expected={expected} got={actual ?? "null"} :: {hint}");
            }
        }

        output.WriteLine($"{Examples.Length - failed}/{Examples.Length} radio hints passed");
        return failed == 0;
    }

    private static HashSet<string> FindSides(string clause)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (BothSides().IsMatch(clause))
        {
            found.Add("port");
            found.Add("starboard");
        }

        if (PortSide().IsMatch(clause))
        {
            found.Add("port");
        }

        if (StarboardSide().IsMatch(clause))
        {
            found.Add("starboard");
        }

        if (FrontSide().IsMatch(clause))
        {
            found.Add("front");
        }

        if (clause.Contains("other side", StringComparison.Ordinal))
        {
            if (found.Contains("port") && !found.Contains("starboard"))
            {
                found.Add("starboard");
            }
            else if (found.Contains("starboard") && !found.Contains("port"))
            {
                found.Remove("starboard");
                found.Add("port");
            }
        }

        return found;
    }

    private static string? Polarity(string clause)
    {
        if (SafeIdiom().IsMatch(clause))
        {
            return "safe";
        }

        var negated = Negation().IsMatch(clause);
        var danger = DangerWords().IsMatch(clause);
        var safe = SafeWords().IsMatch(clause);
        if (negated && danger)
        {
            return "safe";
        }

        if (danger)
        {
            return "danger";
        }

        if (negated && !safe)
        {
            return "danger";
        }

        if (safe)
        {
            return "safe";
        }

        return null;
    }

    private static HashSet<string> Lateral(IEnumerable<string> sides) =>
        sides.Where(side => side is "port" or "starboard").ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> SplitParts(Regex pattern, string text) =>
        pattern.Split(text).Select(part => part.Trim()).Where(part => part.Length > 0);

    private static readonly (string Hint, string Expected)[] Examples =
    [
        ("There is breathing room on either flank. The danger waits squarely in front.", "front"),
        ("Nothing sits off either wing. The rock occupies the path straight ahead.", "front"),
        ("Port gives room, starboard gives room, and straight flight does not. A rock is in front.", "front"),
        ("Both edges of the route are empty. The center is occupied by a rock.", "front"),
        ("You can dodge either way, because the obstruction is directly ahead of the bow.", "front"),
        ("The danger is waiting in front of the rocket's nose. Neither side is blocked.", "front"),
        ("No warning lights show on the sides. The central path is blocked by a rock.", "front"),
        ("There is no problem in front of you, and the starboard lane is empty. The blockage sits beside your port flank.", "port"),
        ("The rock is on the same line as your current trajectory. The sides are open.", "front"),
        ("The obstruction is directly beyond the cockpit glass, not beside the hull.", "front"),
        ("You do not need to fear the space ahead or the starboard edge. Watch the port side instead.", "port"),
        ("Forward looks clear, and the space to your right is clean. The rock is hanging off the other side of the hull.", "port"),
        ("The only side you should distrust is port. Forward and starboard look clear.", "port"),
        ("Your forward corridor is empty, and the starboard side is no concern. The hazard is fixed off port.", "port"),
        ("No impact risk appears ahead, and the right side stays free. The solid mass is stationed beside the port side.", "port"),
        ("The safe space is beside you, not before you. A rock is directly in front of the craft.", "front"),
        ("Your left flank is harmless, and the route ahead is still passable. The rock has taken up position by starboard.", "starboard"),
        ("Port and starboard stay friendly. The bow, however, is aimed right at a stone.", "front"),
        ("The path beside either wing is harmless. The bow faces a rock.", "front"),
        ("Port is open, starboard is open, and the center lane is the bad choice. That stone is dead ahead.", "front"),
        ("You are clear on both sides, but continuing without turning would send you into the rock ahead.", "front"),
        ("The hazard sits where a ship would call port, while the nose still has room to travel.", "port"),
        ("Nothing blocks the sides of the rocket. The blockage is straight in front of you.", "front"),
        ("The nose can keep moving and starboard is harmless for now. The danger is hugging the port side of the rocket.", "port"),
        ("Your flanks are clean for now. The impact risk lies straight along the current heading.", "front"),
        ("Both flanks look usable, but the nose of the rocket is pointed straight at trouble.", "front"),
        ("The empty side is port, and the path ahead is also safe. The rock marks the starboard side.", "starboard"),
        ("Ahead offers space, and the port field is clean. The rock is waiting beside the starboard hull.", "starboard"),
        ("You do not need to fear the space ahead or the port edge. Watch the starboard side instead.", "starboard"),
        ("Flight data shows safe passage ahead and to port. The obstruction is the one riding off starboard.", "starboard"),
        ("Starboard is the side that currently carries the risk. The bow and port side remain clear.", "starboard"),
        ("Your forward corridor is empty, and the port side is no concern. The hazard is fixed off starboard.", "starboard"),
        ("Forward looks clear, and the space to your left is clean. The rock is hanging off the other side of the hull.", "starboard"),
        ("Your route remains open down the middle, and the left side gives you breathing room. A rock is posted beside the starboard side.", "starboard"),
        ("The rocket has breathing room in front and to the left. The problem is attached to the starboard-side view.", "starboard"),
        ("The bow can keep going, and port gives no warning. The obstacle is resting beside starboard.", "starboard"),
        ("There is safe air before you and along port. The rough chunk is off the starboard side.", "starboard"),
        ("Straight flight is still possible, and the port side stays empty. The obstacle is stationed off starboard.", "starboard"),
        ("The front view shows nothing alarming, and starboard stays open. All the trouble is gathered beside port.", "port"),
        ("Ahead offers space, and the starboard field is clean. The rock is waiting beside the port hull.", "port"),
        ("The hazard is not trailing your wings. It is waiting in the exact path of the bow.", "front"),
        ("The danger is not in front and not toward port. It is posted on the starboard side of the craft.", "starboard"),
        ("The danger is not in front and not toward starboard. It is posted on the port side of the craft.", "port"),
        ("The hazard sits where a ship would call starboard, while the nose still has room to travel.", "starboard"),
        ("The craft is not being crowded from either side. The problem is sitting straight ahead.", "front"),
        ("You have room on both sides, but not in the direction the craft is already facing.", "front"),
        ("No issue hugs either side of the craft. The obstacle is planted right in front.", "front"),
        ("Straight flight is still possible, and the starboard side stays empty. The obstacle is stationed off port.", "port"),
        ("The clean space is in front of the cockpit and out to starboard. The stone is sitting on the port side.", "port"),
        ("There is no problem in front of you, and the port lane is empty. The blockage sits beside your starboard flank.", "starboard"),
        ("The path on your starboard side stays open, and nothing blocks the nose of the craft. The trouble is sitting off your port wing.", "port"),
        ("Nothing blocks the bow, and starboard remains quiet. The rock is the thing shadowing the port side.", "port"),
        ("You have room ahead and also on the right-hand side. The obstruction is lurking beside the opposite window.", "port"),
    ];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\bright (at|in|ahead|before)\b")]
    private static partial Regex RightAt();

    [GeneratedRegex(@"[.;]| but | while ")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@",|\band not\b|\band\b")]
    private static partial Regex PieceSplit();

    [GeneratedRegex(@"either (side|flank|wing)|both (sides|flanks|edges)|neither side|the sides|your wings|either side of|both sides|flanks are|from either")]
    private static partial Regex BothSides();

    [GeneratedRegex(@"\b(port|larboard|left)\b")]
    private static partial Regex PortSide();

    [GeneratedRegex(@"\b(starboard|right)\b")]
    private static partial Regex StarboardSide();

    [GeneratedRegex(@"\b(ahead|front|bow|nose|straight|middle|center|centre|heading|forward|cockpit|trajectory)\b|already facing|exact path|dead ahead|in front|before you|same line|central path|center lane|down the middle")]
    private static partial Regex FrontSide();

    [GeneratedRegex(@"no (problem|issue|concern|warning|impact)|nothing (blocks|sits|alarming)|gives no warning|do not need to fear|not blocked|not being crowded|nothing alarming|not trailing")]
    private static partial Regex SafeIdiom();

    [GeneratedRegex(@"\b(not|no|n't|nothing|neither)\b")]
    private static partial Regex Negation();

    [GeneratedRegex(@"\b(rocks?|stones?|danger|hazard|obstruct\w*|block\w*|trouble|risk|problems?|obstacles?|mass|chunks?|posted|waiting|sitting|stationed|hugging|aimed|pointed|occup\w*|gathered|planted|crowded|watch|distrust|avoid|hanging|fixed off|riding off|bad choice|carries the risk|rough chunks?|solid mass)\b")]
    private static partial Regex DangerWords();

    [GeneratedRegex(@"\b(clears?|open|empty|safe|harmless|friendly|clean|free|rooms?|passable|alarming|no issue|no problem|no concern|no warning|no impact|not blocked|stays open|offers space|still possible|keep moving|keep going|usable|breathing|nothing alarming|gives no warning|remain clear|stays empty|stays free|looks clear|look clear|is clean|are clean|are empty|are open|is open|is empty|not being crowded)\b")]
    private static partial Regex SafeWords();
}
