namespace Rakit.Core.Workflows;

/// <summary>
/// The links between steps. A step links to <see cref="WorkflowStep.Next"/>, or for a Condition to its two branches; a link
/// to an unknown code is ignored here (the validator reports it). The first step of the list is the entry.
/// </summary>
public static class WorkflowGraph
{
    public static IEnumerable<string> LinksOf(WorkflowStep step)
    {
        if (step.Type == StepType.Condition)
        {
            if (!string.IsNullOrWhiteSpace(step.Condition?.OnTrue)) yield return step.Condition.OnTrue.Trim();
            if (!string.IsNullOrWhiteSpace(step.Condition?.OnFalse)) yield return step.Condition.OnFalse.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(step.Next))
        {
            yield return step.Next.Trim();
        }
    }

    public static Dictionary<string, WorkflowStep> Index(IEnumerable<WorkflowStep> steps)
    {
        var index = new Dictionary<string, WorkflowStep>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps) index.TryAdd(step.Code.Trim(), step);
        return index;
    }

    /// <summary>The step that follows <paramref name="step"/>: for a Condition the branch of <paramref name="conditionResult"/>.</summary>
    public static string? NextOf(WorkflowStep step, bool conditionResult)
    {
        var next = step.Type == StepType.Condition
            ? (conditionResult ? step.Condition?.OnTrue : step.Condition?.OnFalse)
            : step.Next;
        return string.IsNullOrWhiteSpace(next) ? null : next.Trim();
    }

    /// <summary>True when following links can come back to a step already on the way.</summary>
    public static bool HasCycle(IReadOnlyList<WorkflowStep> steps)
    {
        var index = Index(steps);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 = on the way, 2 = done

        bool Visit(string code)
        {
            if (state.TryGetValue(code, out var s)) return s == 1;
            state[code] = 1;
            if (index.TryGetValue(code, out var step))
                foreach (var next in LinksOf(step))
                    if (index.ContainsKey(next) && Visit(next)) return true;
            state[code] = 2;
            return false;
        }

        return index.Keys.Any(Visit);
    }

    /// <summary>Codes reachable from <paramref name="startCode"/> by following links (the start itself only when <paramref name="includeStart"/>).</summary>
    public static HashSet<string> ReachableFrom(IReadOnlyList<WorkflowStep> steps, string startCode, bool includeStart)
    {
        var index = Index(steps);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        if (index.TryGetValue(startCode.Trim(), out var start))
            foreach (var next in LinksOf(start)) queue.Enqueue(next);

        while (queue.Count > 0)
        {
            var code = queue.Dequeue();
            if (!index.TryGetValue(code, out var step) || !seen.Add(code)) continue;
            foreach (var next in LinksOf(step)) queue.Enqueue(next);
        }
        if (includeStart) seen.Add(startCode.Trim());
        return seen;
    }

    /// <summary>For every step, the steps that can run before it (a path of links leads from them to it).</summary>
    public static Dictionary<string, HashSet<string>> AncestorsOf(IReadOnlyList<WorkflowStep> steps)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps) result[step.Code.Trim()] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
            foreach (var descendant in ReachableFrom(steps, step.Code, includeStart: false))
                if (result.TryGetValue(descendant, out var ancestors)) ancestors.Add(step.Code.Trim());
        return result;
    }

    /// <summary>The first step's code, or null for an empty list.</summary>
    public static string? EntryOf(IReadOnlyList<WorkflowStep> steps) => steps.Count == 0 ? null : steps[0].Code.Trim();
}
