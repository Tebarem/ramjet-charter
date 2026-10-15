using System;

namespace Charter.Tests;

/// <summary>A small harness: named tests, Check and Expect. The exit code is the number of failed checks.
/// An optional argument runs only the tests whose name contains it.</summary>
internal static class Program
{
    private static int _checks;
    private static int _failed;
    private static string _current = "";
    private static string _filter = "";

    public static void Check(bool condition, string message)
    {
        _checks++;
        if (condition) return;
        _failed++;
        Console.WriteLine($"  FAIL [{_current}] {message}");
    }

    /// <summary>Passes when <paramref name="body"/> throws <typeparamref name="TException"/>.</summary>
    public static void Expect<TException>(Action body, string message) where TException : Exception
    {
        Exception? thrown = null;
        try
        {
            body();
        }
        catch (Exception e)
        {
            thrown = e;
        }
        if (thrown is TException) Check(true, message);
        else if (thrown is null) Check(false, $"{message} (nothing thrown)");
        else Check(false, $"{message} (threw {thrown.GetType().Name}: {thrown.Message})");
    }

    public static void Run(string name, Action body)
    {
        if (_filter.Length > 0 && !name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) return;
        _current = name;
        try
        {
            body();
        }
        catch (Exception e)
        {
            _failed++;
            Console.WriteLine($"  EXCEPTION [{name}] {e.GetType().Name}: {e.Message}");
        }
    }

    private static int Main(string[] args)
    {
        _filter = args.Length > 0 ? args[0] : "";
        ImportTests.All();
        ResolverTests.All();
        Console.WriteLine($"{_checks} checks, {_failed} failed");
        return _failed;
    }
}
