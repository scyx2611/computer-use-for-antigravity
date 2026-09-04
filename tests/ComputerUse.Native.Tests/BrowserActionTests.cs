using System.Text.Json.Nodes;

namespace ComputerUse.Native.Tests;

internal static class BrowserActionTests
{
    public static void Run()
    {
        SemanticRoleNameResolutionIsDeterministic();
        DuplicateSemanticTargetsFailClosed();
        PlaceholderTextAndStateBoundResolutionWork();
    }

    private static void SemanticRoleNameResolutionIsDeterministic()
    {
        var state = State(
            Handle(1, "Submit", "button", htmlId: "submit"),
            Handle(2, "Submit settings", "button", htmlId: "settings"));
        var target = new BrowserElementResolver().ResolveSemantic(
            state,
            TargetDescriptor.FromAction(new JsonObject
            {
                ["target"] = new JsonObject { ["role"] = "button", ["name"] = "Submit" }
            }),
            allowFocusedEditable: false);

        TestAssert.Equal(1, target.Element!.ElementId, "exact role/name should beat a contains match");
        TestAssert.Equal("role_name_exact", target.Resolution!["method"]!.GetValue<string>(), "resolution method");
    }

    private static void DuplicateSemanticTargetsFailClosed()
    {
        var state = State(
            Handle(1, "Save", "button"),
            Handle(2, "Save", "button"));
        var error = TestAssert.Throws<ComputerUseException>(
            () => new BrowserElementResolver().ResolveSemantic(
                state,
                TargetDescriptor.FromAction(new JsonObject
                {
                    ["target"] = new JsonObject { ["role"] = "button", ["name"] = "Save" }
                }),
                allowFocusedEditable: false),
            "duplicate browser targets must fail closed");

        TestAssert.Equal("AMBIGUOUS_TARGET", error.Code, "browser ambiguity code");
        TestAssert.Equal(2, error.Details!["candidates"]!.AsArray().Count, "browser ambiguity candidates");
    }

    private static void PlaceholderTextAndStateBoundResolutionWork()
    {
        var first = Handle(
            1,
            "Name",
            "textbox",
            placeholder: "Enter your name",
            testId: "name-field",
            htmlId: "name",
            editable: true,
            focused: true);
        var second = Handle(2, "Email", "textbox", placeholder: "Enter your email", editable: true);
        var state = State(first, second);
        var resolver = new BrowserElementResolver();

        var placeholder = resolver.ResolveSemantic(
            state,
            TargetDescriptor.FromAction(new JsonObject
            {
                ["target"] = new JsonObject { ["placeholder"] = "Enter your name" }
            }),
            allowFocusedEditable: false);
        TestAssert.Equal(1, placeholder.Element!.ElementId, "placeholder should resolve the matching textbox");

        var focused = resolver.ResolveSemantic(
            state,
            TargetDescriptor.FromAction(new JsonObject()),
            allowFocusedEditable: true);
        TestAssert.Equal(1, focused.Element!.ElementId, "focused editable browser element");

        var byId = resolver.ResolveSemantic(
            state,
            TargetDescriptor.FromAction(new JsonObject { ["element_id"] = 2 }),
            allowFocusedEditable: false);
        TestAssert.Equal(2, byId.Element!.ElementId, "state-bound browser element id");
    }

    private static BrowserStateMetadata State(params BrowserElementHandle[] handles)
    {
        return new BrowserStateMetadata
        {
            SessionId = "session",
            TargetId = "target",
            DocumentGeneration = "document",
            ElementHandles = handles.ToDictionary(handle => handle.ElementId)
        };
    }

    private static BrowserElementHandle Handle(
        int id,
        string name,
        string role,
        string? placeholder = null,
        string? testId = null,
        string? htmlId = null,
        bool editable = false,
        bool focused = false)
    {
        return new BrowserElementHandle
        {
            ElementId = id,
            BackendNodeId = id + 100,
            Placeholder = placeholder,
            TestId = testId,
            HtmlId = htmlId,
            IsEditable = editable,
            IsFocused = focused,
            Snapshot = new UiElementSnapshot
            {
                Id = id,
                Name = name,
                Role = role,
                Bounds = [id * 10, id * 10, 100, 30],
                IsEnabled = true,
                RuntimeId = $"browser-target-{id}",
                Placeholder = placeholder,
                TestId = testId,
                Text = name
            }
        };
    }
}
