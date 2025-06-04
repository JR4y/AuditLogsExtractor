using System;

public static class Logger
{
    public static void Info(string mensaje)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(string.Format("[{0:HH:mm:ss}] INFO: {1}", DateTime.Now, mensaje));
        Console.ResetColor();
    }

    public static void Ok(string mensaje)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(string.Format("[{0:HH:mm:ss}] ✅ {1}", DateTime.Now, mensaje));
        Console.ResetColor();
    }

    public static void Warning(string mensaje)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(string.Format("[{0:HH:mm:ss}] ⚠️  {1}", DateTime.Now, mensaje));
        Console.ResetColor();
    }

    public static void Error(string mensaje)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(string.Format("[{0:HH:mm:ss}] ❌ {1}", DateTime.Now, mensaje));
        Console.ResetColor();
    }
}
