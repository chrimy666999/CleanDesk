using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanDesk.App.Services;

public static class JsonStore
{
    private static readonly object SaveGate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static T? Load<T>(string path)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Options);
    }

    public static void Save<T>(string path, T value)
    {
        lock (SaveGate)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, value, Options);
                    stream.Flush(true);
                }

                for (var attempt = 0; attempt < 8; attempt++)
                {
                    try
                    {
                        File.Move(temp, path, true);
                        return;
                    }
                    catch (IOException) when (attempt < 7)
                    {
                        Thread.Sleep(40 + attempt * 25);
                    }
                    catch (UnauthorizedAccessException) when (attempt < 7)
                    {
                        Thread.Sleep(40 + attempt * 25);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, $"Failed to save JSON store: {path}");
                return;
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary cleanup must never surface as a UI exception.
        }
    }
}
