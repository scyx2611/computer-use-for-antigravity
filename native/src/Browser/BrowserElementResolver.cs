using System.Text.Json.Nodes;

namespace ComputerUse.Native;

internal sealed class BrowserResolvedTarget
{
    public BrowserResolvedTarget(
        BrowserElementHandle? element,
        string? label,
        JsonObject? resolution)
    {
        Element = element;
        Label = label;
        Resolution = resolution;
    }

    public BrowserElementHandle? Element { get; }

    public string? Label { get; }

    public JsonObject? Resolution { get; }
}

/// <summary>
/// Resolves public browser selectors against one observed browser document.
/// It never returns a first match when multiple candidates are plausible.
/// </summary>
internal sealed class BrowserElementResolver
{
    private const int AmbiguityScoreDelta = 50;

    public BrowserResolvedTarget? Resolve(
        BrowserTargetContext targetContext,
        BrowserStateMetadata state,
        TargetDescriptor descriptor,
        bool allowFocusedEditable)
    {
        if (descriptor.ElementId is not null)
        {
            if (!state.ElementHandles.TryGetValue(descriptor.ElementId.Value, out var handle))
            {
                throw new ComputerUseException(
                    "ELEMENT_NOT_FOUND",
                    $"Browser element id {descriptor.ElementId.Value} is not present in state.",
                    new JsonObject
                    {
                        ["state_id"] = state.DocumentGeneration,
                        ["element_id"] = descriptor.ElementId.Value
                    });
            }

            return CreateResolved(handle, "element_id");
        }

        if (descriptor.Css is not null && descriptor.TestId is not null)
        {
            throw new ComputerUseException(
                "INVALID_TARGET",
                "A browser target may specify css or test_id, but not both.");
        }

        if (descriptor.Css is not null || descriptor.TestId is not null)
        {
            return ResolveDomSelector(targetContext, state, descriptor);
        }

        if (descriptor.HasSelector)
        {
            return ResolveSemantic(state, descriptor, allowFocusedEditable);
        }

        if (allowFocusedEditable)
        {
            var focused = state.ElementHandles.Values
                .Where(handle => handle.IsFocused && handle.IsEditable)
                .ToArray();
            if (focused.Length == 1)
            {
                return CreateResolved(focused[0], "focused_element");
            }

            if (focused.Length > 1)
            {
                throw Ambiguous(descriptor, focused.Select(handle => new BrowserTargetMatch(handle, 1, "focused_element")));
            }

            throw new ComputerUseException(
                "FOCUSED_ELEMENT_NOT_IN_TARGET",
                "No focused editable element belongs to the active browser target; provide an explicit target.");
        }

        return null;
    }

    internal BrowserResolvedTarget ResolveSemantic(
        BrowserStateMetadata state,
        TargetDescriptor descriptor,
        bool allowFocusedEditable)
    {
        if (descriptor.ElementId is not null)
        {
            if (!state.ElementHandles.TryGetValue(descriptor.ElementId.Value, out var handle))
            {
                throw new ComputerUseException(
                    "ELEMENT_NOT_FOUND",
                    $"Browser element id {descriptor.ElementId.Value} is not present in state.");
            }

            return CreateResolved(handle, "element_id");
        }

        if (!descriptor.HasSelector)
        {
            if (allowFocusedEditable)
            {
                var focusedOnly = state.ElementHandles.Values
                    .Where(handle => handle.IsFocused && handle.IsEditable)
                    .ToArray();
                if (focusedOnly.Length == 1)
                {
                    return CreateResolved(focusedOnly[0], "focused_element");
                }

                if (focusedOnly.Length > 1)
                {
                    throw Ambiguous(descriptor, focusedOnly.Select(handle => new BrowserTargetMatch(handle, 1, "focused_element")));
                }

                throw new ComputerUseException(
                    "FOCUSED_ELEMENT_NOT_IN_TARGET",
                    "No focused editable element belongs to the active browser target; provide an explicit target.");
            }

            throw new ComputerUseException("TARGET_REQUIRED", "A browser semantic target is required.");
        }

        var ranked = state.ElementHandles.Values
            .Select(handle => new BrowserTargetMatch(handle, Score(handle, descriptor), ResolutionMethod(handle, descriptor)))
            .Where(match => match.Score >= 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Element.ElementId)
            .ToArray();

        EnsureUnambiguous(ranked, descriptor);
        var best = ranked.FirstOrDefault();
        if (best is not null)
        {
            return CreateResolved(best.Element, best.Method);
        }

        if (allowFocusedEditable)
        {
            var focused = state.ElementHandles.Values
                .Where(handle => handle.IsFocused && handle.IsEditable)
                .ToArray();
            if (focused.Length == 1)
            {
                return CreateResolved(focused[0], "focused_element");
            }

            if (focused.Length > 1)
            {
                throw Ambiguous(descriptor, focused.Select(handle => new BrowserTargetMatch(handle, 1, "focused_element")));
            }

            throw new ComputerUseException(
                "FOCUSED_ELEMENT_NOT_IN_TARGET",
                "No focused editable element belongs to the active browser target; provide an explicit target.");
        }

        throw TargetNotFound(descriptor);
    }

    private static BrowserResolvedTarget ResolveDomSelector(
        BrowserTargetContext targetContext,
        BrowserStateMetadata state,
        TargetDescriptor descriptor)
    {
        var selector = descriptor.Css ?? BuildTestIdSelector(descriptor.TestId!);
        if (selector.Length > 512)
        {
            throw new ComputerUseException(
                "INVALID_TARGET",
                "Browser CSS selectors are limited to 512 characters.");
        }

        // DOM node ids are connection/document-scoped and Chromium may issue
        // a fresh root id after a renderer update. Refresh the root for an
        // explicit CSS lookup; the resolved backend id is still checked
        // against the observed state below.
        var document = targetContext.Connection.GetDocument();
        var documentNodeId = ReadDocumentNodeId(document);

        if (documentNodeId is null)
        {
            throw new ComputerUseException(
                "BROWSER_TARGET_NOT_FOUND",
                "The active browser document has no queryable DOM root.");
        }

        var response = targetContext.Connection.QuerySelectorAll(documentNodeId.Value, selector);
        var nodeIds = ReadNodeIds(response);
        if (nodeIds.Count == 0)
        {
            throw TargetNotFound(descriptor);
        }

        var matches = new List<BrowserTargetMatch>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            var backendNodeId = ReadBackendNodeId(targetContext.Connection, nodeId);
            if (backendNodeId is null)
            {
                continue;
            }

            var handle = state.ElementHandles.Values.FirstOrDefault(candidate =>
                candidate.BackendNodeId == backendNodeId.Value);
            if (handle is not null)
            {
                matches.Add(new BrowserTargetMatch(handle, 1_200, descriptor.Css is not null ? "css" : "test_id"));
            }
        }

        if (nodeIds.Count > 1)
        {
            throw Ambiguous(descriptor, matches);
        }

        if (matches.Count == 0)
        {
            throw new ComputerUseException(
                "TARGET_NOT_FOUND",
                $"The browser selector {descriptor.Describe()} matched a DOM node that is not an exposed actionable element.");
        }

        return CreateResolved(matches[0].Element, matches[0].Method);
    }

    private static int Score(BrowserElementHandle handle, TargetDescriptor descriptor)
    {
        var snapshot = handle.Snapshot;
        var score = 0;

        if (descriptor.Role is not null)
        {
            if (!string.Equals(snapshot.Role, descriptor.Role, StringComparison.OrdinalIgnoreCase))
            {
                return -1;
            }

            score += 250;
        }

        if (descriptor.AutomationId is not null)
        {
            if (string.Equals(handle.HtmlId, descriptor.AutomationId, StringComparison.Ordinal))
            {
                score += 1_100;
            }
            else if (string.Equals(handle.HtmlId, descriptor.AutomationId, StringComparison.OrdinalIgnoreCase))
            {
                score += 950;
            }
            else
            {
                return -1;
            }
        }

        if (descriptor.Name is not null)
        {
            var nameScore = TextScore(snapshot.Name, descriptor.Name);
            if (nameScore < 0)
            {
                return -1;
            }

            score += nameScore;
        }

        if (descriptor.Placeholder is not null)
        {
            var placeholderScore = TextScore(
                handle.Placeholder ?? snapshot.Placeholder ?? snapshot.Name,
                descriptor.Placeholder);
            if (placeholderScore < 0)
            {
                return -1;
            }

            score += placeholderScore + 20;
        }

        if (descriptor.Text is not null)
        {
            var textScore = TextScore(
                snapshot.Text ?? snapshot.Name,
                descriptor.Text);
            if (textScore < 0)
            {
                return -1;
            }

            score += textScore + 10;
        }

        return score;
    }

    private static int TextScore(string? candidate, string requested)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return -1;
        }

        if (string.Equals(candidate, requested, StringComparison.Ordinal))
        {
            return 1_000;
        }

        if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase))
        {
            return 900;
        }

        if (candidate.Contains(requested, StringComparison.OrdinalIgnoreCase)
            || requested.Contains(candidate, StringComparison.OrdinalIgnoreCase))
        {
            return 700;
        }

        var similarity = Similarity(candidate, requested);
        return similarity < 0.45 ? -1 : 500 + (int)(similarity * 150);
    }

    private static string ResolutionMethod(BrowserElementHandle handle, TargetDescriptor descriptor)
    {
        if (descriptor.AutomationId is not null)
        {
            return "automation_id";
        }

        if (descriptor.Placeholder is not null)
        {
            return TextMatchMethod(handle.Placeholder ?? handle.Snapshot.Placeholder, descriptor.Placeholder);
        }

        if (descriptor.Text is not null)
        {
            return TextMatchMethod(handle.Snapshot.Text, descriptor.Text);
        }

        if (descriptor.Name is not null)
        {
            return descriptor.Role is not null
                ? "role_name_exact"
                : TextMatchMethod(handle.Snapshot.Name, descriptor.Name);
        }

        return descriptor.Role is not null ? "role" : "semantic_selector";
    }

    private static string TextMatchMethod(string? candidate, string requested)
    {
        if (string.Equals(candidate, requested, StringComparison.Ordinal))
        {
            return "text_exact";
        }

        if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase))
        {
            return "case_insensitive";
        }

        if (candidate?.Contains(requested, StringComparison.OrdinalIgnoreCase) == true
            || requested.Contains(candidate ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return "contains";
        }

        return "fuzzy";
    }

    private static void EnsureUnambiguous(
        IReadOnlyList<BrowserTargetMatch> ranked,
        TargetDescriptor descriptor)
    {
        if (ranked.Count < 2)
        {
            return;
        }

        var bestScore = ranked[0].Score;
        var competitors = ranked
            .TakeWhile(match => bestScore - match.Score < AmbiguityScoreDelta)
            .ToArray();
        if (competitors.Length > 1)
        {
            throw Ambiguous(descriptor, competitors);
        }
    }

    private static ComputerUseException Ambiguous(
        TargetDescriptor descriptor,
        IEnumerable<BrowserTargetMatch> matches)
    {
        var candidates = new JsonArray();
        foreach (var match in matches.OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Element.ElementId))
        {
            var snapshot = match.Element.Snapshot;
            candidates.Add(new JsonObject
            {
                ["element_id"] = snapshot.Id,
                ["name"] = snapshot.Name,
                ["role"] = snapshot.Role,
                ["placeholder"] = match.Element.Placeholder,
                ["test_id"] = match.Element.TestId,
                ["bounds"] = JsonSerializerHelper.ToNode(snapshot.Bounds),
                ["is_enabled"] = snapshot.IsEnabled,
                ["score"] = match.Score
            });
        }

        return new ComputerUseException(
            "AMBIGUOUS_TARGET",
            $"The browser selector {descriptor.Describe()} matched multiple equally plausible elements; refine it with css, test_id, role, name, or element_id.",
            new JsonObject
            {
                ["selector"] = descriptor.Describe(),
                ["candidates"] = candidates
            });
    }

    private static BrowserResolvedTarget CreateResolved(BrowserElementHandle handle, string method)
    {
        var snapshot = handle.Snapshot;
        return new BrowserResolvedTarget(
            handle,
            $"{snapshot.Role} '{snapshot.Name}'",
            new JsonObject
            {
                ["method"] = method,
                ["element_id"] = snapshot.Id,
                ["name"] = snapshot.Name,
                ["role"] = snapshot.Role,
                ["placeholder"] = handle.Placeholder,
                ["test_id"] = handle.TestId
            });
    }

    private static ComputerUseException TargetNotFound(TargetDescriptor descriptor)
    {
        return new ComputerUseException(
            "TARGET_NOT_FOUND",
            $"No browser element matched {descriptor.Describe()}.");
    }

    private static string BuildTestIdSelector(string testId)
    {
        var escaped = testId.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"[data-testid=\"{escaped}\"], [data-test-id=\"{escaped}\"]";
    }

    private static int? ReadDocumentNodeId(JsonObject document)
    {
        return document["root"] is JsonObject root
            && root["nodeId"] is JsonValue value
            && value.TryGetValue<int>(out var nodeId)
            ? nodeId
            : null;
    }

    private static IReadOnlyList<int> ReadNodeIds(JsonObject response)
    {
        if (response["nodeIds"] is not JsonArray values)
        {
            return [];
        }

        return values
            .Select(value => value is JsonValue scalar && scalar.TryGetValue<int>(out var nodeId) ? nodeId : 0)
            .Where(nodeId => nodeId > 0)
            .ToArray();
    }

    private static int? ReadBackendNodeId(CdpConnection connection, int nodeId)
    {
        var response = connection.DescribeNodeByNodeId(nodeId);
        return response["node"] is JsonObject node
            && node["backendNodeId"] is JsonValue value
            && value.TryGetValue<int>(out var backendNodeId)
            ? backendNodeId
            : null;
    }

    private static double Similarity(string left, string right)
    {
        left = Normalize(left);
        right = Normalize(right);
        if (left == right)
        {
            return 1;
        }

        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        var distance = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
        {
            distance[index] = index;
        }

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            var diagonal = distance[0];
            distance[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var previous = distance[rightIndex];
                var cost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                distance[rightIndex] = Math.Min(
                    Math.Min(distance[rightIndex] + 1, distance[rightIndex - 1] + 1),
                    diagonal + cost);
                diagonal = previous;
            }
        }

        return 1.0 - (double)distance[^1] / Math.Max(left.Length, right.Length);
    }

    private static string Normalize(string value)
    {
        return string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private sealed record BrowserTargetMatch(
        BrowserElementHandle Element,
        int Score,
        string Method);
}
