namespace Voiceless.Configuration;

public record TargetUser
{
    public ulong User { get; init; }
    public string Voice { get; init; }
    public List<TargetServer> Servers { get; init; }
}