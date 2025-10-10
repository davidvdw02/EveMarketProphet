using System;
using System.IO;
using System.Text;

namespace EveMarketProphet.Services
{
    public static class DiagnosticsLogger
    {
        private static readonly object Gate = new object();
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EveMarketProphet");
        private static readonly string LogFilePath = Path.Combine(LogDirectory, "diagnostics.log");

        public static string FilePath => LogFilePath;

        public static void Log(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var timestampedMessage = $"{DateTime.UtcNow:O} {message}";

            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(LogDirectory);
                    File.AppendAllText(LogFilePath, timestampedMessage + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Swallow all exceptions: diagnostics should never crash the UI.
            }
        }

        public static void LogException(string message, Exception ex)
        {
            Log($"{message}: {ex}");
        }
    }
}
