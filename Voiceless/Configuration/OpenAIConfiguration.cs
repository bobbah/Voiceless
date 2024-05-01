namespace Voiceless.Configuration;

public class OpenAIConfiguration
{
    public string Token { get; set; }
    public string Model { get; set; }
    public string Voice { get; set; }
    public string AttachmentDetail { get; set; }
    public int MaxAttachmentTokens { get; set; }
    public bool DescribeAttachments { get; set; }
    public string AttachmentPrompt { get; set; }
    public string FlavorPrompt { get; set; }
}