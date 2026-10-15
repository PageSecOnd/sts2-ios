using Godot;

namespace Phase4IosAotFixture;

public partial class Main : Node
{
    public override void _Ready()
    {
        GD.Print($"Phase4 iOS NativeAOT fixture; registered={RegistrationState.Order.Count}");
    }
}

public static class RegistrationState
{
    public static List<string> Order { get; } = [];
}

public static class FoundationModEntry
{
    public static void Initialize() => RegistrationState.Order.Add("foundation");
}

public static class DependentModEntry
{
    public static void Initialize()
    {
        if (!RegistrationState.Order.SequenceEqual(["foundation"]))
            throw new InvalidOperationException("foundation Mod must initialize first");
        RegistrationState.Order.Add("dependent");
    }
}

public sealed class ReflectionOnlyContent
{
    public string Id => "phase4.fixture.content";
}
