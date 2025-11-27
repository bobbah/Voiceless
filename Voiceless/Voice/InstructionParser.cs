using System.Text.RegularExpressions;

namespace Voiceless.Voice;

/// <summary>
/// Parses voice instructions from message text.
/// Instructions are enclosed in percent signs, e.g., %sensually%.
/// </summary>
public static partial class InstructionParser
{
    // Matches %instructions% where instructions:
    // - Cannot start or end with whitespace
    // - Must contain at least one alphabetic character (to avoid matching numbers like %50%)
    // - Cannot contain percent signs
    [GeneratedRegex(@"%(?<instructions>(?=[^%]*[a-zA-Z])[^%\s][^%]*[^%\s]|[a-zA-Z])%", RegexOptions.None, "en-US")]
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
        
        // Remove the matched instruction and clean up surrounding whitespace
        var beforeMatch = message[..match.Index].TrimEnd();
        var afterMatch = message[(match.Index + match.Length)..].TrimStart();
        var cleanedText = beforeMatch.Length > 0 && afterMatch.Length > 0 
            ? $"{beforeMatch} {afterMatch}" 
            : $"{beforeMatch}{afterMatch}";
        
        return (cleanedText.Trim(), instructions);
    }
}
