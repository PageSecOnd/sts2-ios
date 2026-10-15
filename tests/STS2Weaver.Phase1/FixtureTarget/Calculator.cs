namespace STS2Weaver.Phase1.FixtureTarget;

public sealed class Calculator
{
    public static int OriginalGetValueCalls { get; private set; }
    public static int AddCalls { get; private set; }
    public static int LastAddLeft { get; private set; }

    public static void Reset()
    {
        OriginalGetValueCalls = 0;
        AddCalls = 0;
        LastAddLeft = 0;
    }

    public int GetValue()
    {
        OriginalGetValueCalls++;
        return 7;
    }

    public int Add(int left, int right)
    {
        AddCalls++;
        LastAddLeft = left;
        return left + right;
    }

    public string Add(string left, string right) => left + right;

    public int Classify(int value)
    {
        if (value < 0) return -1;
        if (value == 0) return 0;
        return value % 2 == 0 ? 2 : 1;
    }
}
