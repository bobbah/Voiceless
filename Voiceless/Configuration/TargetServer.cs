namespace Voiceless.Configuration;

public record TargetServer
{
    public ulong Server { get; init; }
    public List<ulong> Channels { get; init; }
}