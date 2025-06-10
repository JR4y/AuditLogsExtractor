using System;

public static class Logger
{
    private static readonly object _lock = new object();

    #region Info and Success

    public static void Info(string message, ConsoleColor color = ConsoleColor.Gray)
    {
        lock (_lock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] INFO: {message}");
            Console.ResetColor();
        }
    }

    public static void Ok(string message)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ✅ {message}");
            Console.ResetColor();
        }
    }

    #endregion

    #region Warnings and Errors

    public static void Warning(string message)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ⚠️  {message}");
            Console.ResetColor();
        }
    }

    public static void Error(string message)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ {message}");
            Console.ResetColor();
        }
    }

    public static void ErrorWithStack(string message, Exception ex)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ❌ {message}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
        }
    }

    #endregion
}