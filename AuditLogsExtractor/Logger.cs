using System;

public static class Logger
{
    private static readonly object _lock = new object();
    public static void Info(string mensaje, ConsoleColor color = ConsoleColor.Gray)
    {
        lock (_lock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(string.Format("[{0:HH:mm:ss}] INFO: {1}", DateTime.Now, mensaje));
            Console.ResetColor();
        }
    }

    public static void Ok(string mensaje)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(string.Format("[{0:HH:mm:ss}] ✅ {1}", DateTime.Now, mensaje));
            Console.ResetColor();
        }
    }

    public static void Warning(string mensaje)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(string.Format("[{0:HH:mm:ss}] ⚠️  {1}", DateTime.Now, mensaje));
            Console.ResetColor();
        }
    }

    public static void Error(string mensaje)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(string.Format("[{0:HH:mm:ss}] ❌ {1}", DateTime.Now, mensaje));
            Console.ResetColor();
        }
    }

    public static void Trace(string message)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"      {message}");
        Console.ResetColor();
    }

    public static void Header(string msg) => Info($"======== {msg} ========");
    public static void Step(string msg) => Console.WriteLine($"   > {msg}");
    public static void Summary(string msg) => Console.WriteLine($"📊 {msg}");
}
