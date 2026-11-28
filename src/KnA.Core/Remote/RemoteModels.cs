using System.Text.Json.Serialization;
using KnA.Core.Encoding;
using KnA.Core.Queue;

namespace KnA.Core.Remote;

/// <summary>The desktop app's connection to a KnA server (Settings → "Encode on a KnA server").</summary>
public sealed record RemoteServerSettings
{
    /// <summary>On: the queue, the encodes and the hardware are the server's; off: this computer (one click back).</summary>
    public bool Enabled { get; init; }
    /// <summary>Address of the server, e.g. http://192.168.1.10:8585 or https://kna.example.com/kna.</summary>
    public string? Url { get; init; }
    public string? User { get; init; }
    /// <summary>The password is kept in the system's keychain (never in the settings file).</summary>
    public bool Remember { get; init; } = true;
    /// <summary>SHA-256 of a self-signed HTTPS certificate accepted once (pinned).</summary>
    public string? PinnedCertificate { get; init; }
}

/// <summary>/api/version: what a server is and which API it speaks.</summary>
public sealed record ServerInfo(string App, string Version, int ApiVersion, string Platform, string Name)
{
    /// <summary>The API this build of the desktop app speaks; a server with a lower one must be updated.</summary>
    public const int ClientApiVersion = 1;
}

/// <summary>A KnA server found on the local network (discovery answer).</summary>
public sealed record FoundServer(string Name, string Url, string Version, string Platform);

/// <summary>A job of the server as the API sends it (the fields the desktop app shows).</summary>
public sealed record RemoteJob
{
    public Guid Id { get; init; }
    public string FileName { get; init; } = "";
    public string Folder { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public JobSettings Settings { get; init; } = new();
    public JobEdit Edit { get; init; } = new();
    public JobStatus Status { get; init; }
    public string? Stage { get; init; }
    public string? Message { get; init; }
    public double Percent { get; init; }
    public string? MediaSummary { get; init; }
    public string? ProbeError { get; init; }
    public string? OutputPath { get; init; }
    public double? Fps { get; init; }
    public double? Speed { get; init; }
    public long? Size { get; init; }
    public double? ElapsedSeconds { get; init; }
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public bool Encoding { get; init; }
    public bool Indeterminate { get; init; }
    public double? StagePercent { get; init; }
}

public sealed record RemoteQueueState
{
    public bool Running { get; init; }
    public bool Paused { get; init; }
    public Guid? CurrentJob { get; init; }
    public bool OtherAccount { get; init; }
}

/// <summary>/api/state, the parts the desktop app uses.</summary>
public sealed record RemoteState
{
    public string Version { get; init; } = "";
    public string Platform { get; init; } = "";
    public List<RemoteJob> Jobs { get; init; } = [];
    public RemoteQueueState Queue { get; init; } = new();
    [JsonPropertyName("settings")] public RemoteSettingsPart? Settings { get; init; }
}

public sealed record RemoteSettingsPart
{
    public Tools.FfmpegCapabilities? Capabilities { get; init; }
}
