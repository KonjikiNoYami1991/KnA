using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TnA.App.Platform;

/// <summary>Command line: files/folders plus <c>--preset NAME</c> and <c>--start</c>.</summary>
public sealed record LaunchArgs(IReadOnlyList<string> Paths, string? Preset, bool Start)
{
    public static LaunchArgs Parse(IReadOnlyList<string> args)
    {
        var paths = new List<string>();
        string? preset = null;
        bool start = false;
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--preset" or "-p" && i + 1 < args.Count) preset = args[++i];
            else if (a.StartsWith("--preset=", StringComparison.Ordinal)) preset = a["--preset=".Length..];
            else if (a is "--start" or "-s") start = true;
            else if (!a.StartsWith("--", StringComparison.Ordinal)) paths.Add(Path.GetFullPath(a));
        }
        return new(paths, string.IsNullOrWhiteSpace(preset) ? null : preset, start);
    }
}

/// <summary>
/// One TnA window per user: a second launch (Send To with many files, double click…) forwards its
/// arguments to the running instance through a named pipe and exits.
/// </summary>
public static class SingleInstance
{
    private static string PipeName => "TnA-" + string.Concat(Environment.UserName.Where(char.IsLetterOrDigit));

    /// <summary>True when another instance received the arguments (this process should exit).</summary>
    public static bool TryForward(string[] args)
    {
        if (Environment.GetEnvironmentVariable("TNA_MULTI_INSTANCE") == "1") return false;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(400);
            var json = JsonSerializer.Serialize(args);
            var bytes = Encoding.UTF8.GetBytes(json);
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listens for forwarded launches; <paramref name="received"/> runs on a background thread.</summary>
    public static void Listen(Action<string[]> received, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, ct);
                    var args = JsonSerializer.Deserialize<string[]>(ms.ToArray()) ?? [];
                    received(args);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    await Task.Delay(500, CancellationToken.None);
                }
            }
        }, ct);
    }
}
