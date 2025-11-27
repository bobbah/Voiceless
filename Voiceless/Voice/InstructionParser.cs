using System.Text.RegularExpressions;

namespace Voiceless.Voice;

/// <summary>
/// Parses voice instructions from message text.
/// Instructions are enclosed in percent signs, e.g., %sensually%.
/// </summary>
public static partial class InstructionParser
{
    [GeneratedRegex(@"%(?<instructions>[^%]+)%", RegexOptions.None, "en-US")]
    private static partial Regex InstructionsPattern();

    /// <summary>
    /// Extracts the first voice instruction from the message text and returns the remaining text.
    /// </summary>
    /// <param name="message">The original message text</param>
    /// <returns>A tuple containing the cleaned message text and the extracted instructions (or null if none found)</returns>
    public static (string Text, string? Instructions) ExtractInstructions(string message)
    {
        var match = InstructionsPattern().Match(message);
        if (!match.Success)
        {
            return (message, null);
        }

        var instructions = match.Groups["instructions"].Value;
        var cleanedText = message.Remove(match.Index, match.Length).Trim();
        return (cleanedText, instructions);
    }
}
