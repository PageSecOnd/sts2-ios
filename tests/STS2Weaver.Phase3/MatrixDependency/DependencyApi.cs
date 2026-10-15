namespace STS2Weaver.Phase3.MatrixDependency;

public static class InitializationLog
{
    private static readonly List<string> Entries = [];
    public static IReadOnlyList<string> Values => Entries;
    public static void Add(string id) => Entries.Add(id);
}

public static class DependencyModEntry
{
    public static int Calls { get; private set; }
    public static void Initialize()
    {
        Calls++;
        InitializationLog.Add("dependency");
    }
}

public static class DependencyApi
{
    public static int InitializationCount { get; }
    public static int Bonus { get; }

    static DependencyApi()
    {
        InitializationCount++;
        Bonus = 5;
    }

    public static T Identity<T>(T value) => value;
}
