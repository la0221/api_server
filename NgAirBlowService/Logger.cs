namespace NgAirBlowService;

public static class Logger
{
    private static readonly object WriteLock = new();
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        Console.WriteLine(line);

        lock (WriteLock)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                var filePath = Path.Combine(LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
                File.AppendAllText(filePath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Logger] Failed to write log file: {ex.Message}");
            }
        }
    }
}
