namespace STS2Weaver.Phase3.MatrixDependency;

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
