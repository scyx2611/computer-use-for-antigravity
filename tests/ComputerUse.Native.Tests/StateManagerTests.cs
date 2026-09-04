namespace ComputerUse.Native.Tests;

internal static class StateManagerTests
{
    public static void Run()
    {
        var now = 1_000L;
        var states = new StateManager(5, () => now);
        var rect = new WindowRectData { X = 10, Y = 20, Width = 300, Height = 200 };
        var element = new UiElementSnapshot
        {
            Id = 1,
            Name = "Save",
            Role = "Button",
            Bounds = [20, 30, 100, 30],
            IsEnabled = true,
            RuntimeId = "1.2.3"
        };
        var state = states.Create(new IntPtr(123), rect, "Test", "hash", [element]);
        TestAssert.Equal(state.StateId, states.Require(state.StateId).StateId, "fresh state must be usable");

        now = 1_006;
        var expired = TestAssert.Throws<ComputerUseException>(
            () => states.Require(state.StateId),
            "expired state must fail safely");
        TestAssert.Equal("STALE_STATE", expired.Code, "expired state error code");

        var moved = new UiElementSnapshot
        {
            Id = element.Id,
            Name = element.Name,
            Role = element.Role,
            AutomationId = element.AutomationId,
            Bounds = [200, 300, 100, 30],
            IsEnabled = true,
            RuntimeId = element.RuntimeId
        };
        var drift = TestAssert.Throws<ComputerUseException>(
            () => StateManager.ValidateElementDrift(element, moved),
            "substantially moved elements must be stale");
        TestAssert.Equal("STALE_STATE", drift.Code, "drift error code");
    }
}
