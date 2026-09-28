namespace OpenRouterAgent.ConsoleApp.Agent.Tools.Goingthere;

public static class GoingthereNavigator
{
    public const int Columns = 12;
    public const int MinRow = 1;
    public const int MaxRow = 3;

    public static string? ChooseMove(int row, int column, int stoneRow, int baseRow, string? danger)
    {
        int? dangerDelta = danger switch
        {
            "port" => -1,
            "front" => 0,
            "starboard" => 1,
            _ => null
        };

        string? selected = null;
        var best = (CanFinish: 1, Distance: int.MaxValue, Turn: int.MaxValue);
        foreach (var (name, delta) in Moves())
        {
            if (dangerDelta is not null && delta == dangerDelta)
            {
                continue;
            }

            var nextRow = row + delta;
            if (nextRow < MinRow || nextRow > MaxRow)
            {
                continue;
            }

            if (delta != 0 && CrossesCurrentStone(row, nextRow, delta, stoneRow))
            {
                continue;
            }

            var remainingAfter = Columns - (column + 1);
            var canFinish = Math.Abs(baseRow - nextRow) <= remainingAfter ? 0 : 1;
            var candidate = (CanFinish: canFinish, Distance: Math.Abs(baseRow - nextRow), Turn: Math.Abs(delta));
            if (candidate.CompareTo(best) < 0)
            {
                best = candidate;
                selected = name;
            }
        }

        return best.CanFinish == 0 ? selected : null;
    }

    public static bool RunSelfTest(TextWriter output)
    {
        var failed = false;
        (int Row, int Column, int Stone, int Base, string Danger, string? Expected)[] cases =
        [
            (2, 1, 1, 3, "front", "right"),
            (2, 1, 3, 1, "front", "left"),
            (3, 2, 2, 3, "port", "go"),
            (1, 11, 2, 1, "starboard", "go"),
            (1, 2, 2, 1, "front", null),
            (3, 11, 1, 3, "port", "go")
        ];

        foreach (var test in cases)
        {
            var actual = ChooseMove(test.Row, test.Column, test.Stone, test.Base, test.Danger);
            if (actual != test.Expected)
            {
                output.WriteLine($"FAIL move r{test.Row}c{test.Column} stone={test.Stone} base={test.Base} {test.Danger} expected={test.Expected ?? "null"} got={actual ?? "null"}");
                failed = true;
            }
        }

        output.WriteLine(failed ? "navigator self-test failed" : "navigator self-test passed");
        return !failed;
    }

    private static bool CrossesCurrentStone(int row, int nextRow, int delta, int stoneRow)
    {
        var step = delta > 0 ? 1 : -1;
        for (var crossed = row + step; crossed != nextRow + step; crossed += step)
        {
            if (crossed == stoneRow)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(string Name, int Delta)> Moves()
    {
        yield return ("left", -1);
        yield return ("go", 0);
        yield return ("right", 1);
    }
}
