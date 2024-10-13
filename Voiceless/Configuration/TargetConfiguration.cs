namespace Voiceless.Configuration;

public record TargetConfiguration
{
    public List<TargetUser> Users { get; init; }
}