using System.Reflection;

var suite = new TestSuite();
foreach (var type in Assembly.GetExecutingAssembly().GetTypes().OrderBy(t => t.Name))
{
    var register = type.GetMethod("Register", BindingFlags.Static | BindingFlags.Public, [typeof(TestSuite)]);
    register?.Invoke(null, [suite]);
}
return await suite.RunAsync();

public sealed class TestSuite
{
    private readonly List<(string Name, Func<Task> Run)> tests = [];
    public void Add(string name, Func<Task> run) => tests.Add((name, run));
    public void Add(string name, Action run) => tests.Add((name, () => { run(); return Task.CompletedTask; }));
    public async Task<int> RunAsync()
    {
        var failed = 0;
        foreach (var (name, run) in tests)
        {
            try { await run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
        }
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} passed.");
        return failed == 0 && tests.Count > 0 ? 0 : 1;
    }
}

public static class Assert
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition) throw new InvalidOperationException(message ?? "Expected true.");
    }
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, actual {actual}.");
    }
    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
