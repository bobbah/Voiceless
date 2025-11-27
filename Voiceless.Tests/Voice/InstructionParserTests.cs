using Voiceless.Voice;

namespace Voiceless.Tests.Voice;

public class InstructionParserTests
{
    [Test]
    public async Task ExtractInstructions_NoInstructions_ReturnsOriginalMessage()
    {
        var message = "Hey bobbah...";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        await Assert.That(text).IsEqualTo("Hey bobbah...");
        await Assert.That(instructions).IsNull();
    }

    [Test]
    public async Task ExtractInstructions_InstructionsAtStart_ExtractsCorrectly()
    {
        var message = "%sensually% Hey bobbah...";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        await Assert.That(text).IsEqualTo("Hey bobbah...");
        await Assert.That(instructions).IsEqualTo("sensually");
    }

    [Test]
    public async Task ExtractInstructions_InstructionsAtEnd_ExtractsCorrectly()
    {
        var message = "Hey, what's up dude? %with great fervor%";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        await Assert.That(text).IsEqualTo("Hey, what's up dude?");
        await Assert.That(instructions).IsEqualTo("with great fervor");
    }

    [Test]
    public async Task ExtractInstructions_InstructionsInMiddle_ExtractsCorrectly()
    {
        var message = "Hello %excitedly% world!";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        // Whitespace is cleaned up properly
        await Assert.That(text).IsEqualTo("Hello world!");
        await Assert.That(instructions).IsEqualTo("excitedly");
    }

    [Test]
    public async Task ExtractInstructions_SinglePercent_DoesNotMatch()
    {
        var message = "I'm at 50% capacity today";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        await Assert.That(text).IsEqualTo("I'm at 50% capacity today");
        await Assert.That(instructions).IsNull();
    }

    [Test]
    public async Task ExtractInstructions_TwoSeparatePercents_DoesNotMatch()
    {
        var message = "I went from 50% to 75% today";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        // Should NOT match because " to 75" starts with a space (not a valid instruction format)
        await Assert.That(text).IsEqualTo("I went from 50% to 75% today");
        await Assert.That(instructions).IsNull();
    }

    [Test]
    public async Task ExtractInstructions_OnlyInstructions_ReturnsEmptyText()
    {
        var message = "%whisper%";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        await Assert.That(text).IsEqualTo("");
        await Assert.That(instructions).IsEqualTo("whisper");
    }

    [Test]
    public async Task ExtractInstructions_MultipleInstructions_ExtractsFirstOnly()
    {
        var message = "%quietly% Hello %loudly% World";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        // Should extract only the first instruction
        await Assert.That(instructions).IsEqualTo("quietly");
        await Assert.That(text).IsEqualTo("Hello %loudly% World");
    }

    [Test]
    public async Task ExtractInstructions_InstructionsWithSpaces_ExtractsCorrectly()
    {
        var message = "%with a soft and gentle tone% Good morning";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        await Assert.That(text).IsEqualTo("Good morning");
        await Assert.That(instructions).IsEqualTo("with a soft and gentle tone");
    }

    [Test]
    public async Task ExtractInstructions_EmptyInstructions_DoesNotMatch()
    {
        var message = "Hello %% World";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        // Empty instructions %% should not match
        await Assert.That(text).IsEqualTo("Hello %% World");
        await Assert.That(instructions).IsNull();
    }

    [Test]
    public async Task ExtractInstructions_EmptyString_ReturnsEmpty()
    {
        var message = "";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        await Assert.That(text).IsEqualTo("");
        await Assert.That(instructions).IsNull();
    }

    [Test]
    public async Task ExtractInstructions_WhitespaceOnly_ReturnsWhitespace()
    {
        var message = "   ";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        // Whitespace-only input with no instructions returns unchanged
        await Assert.That(text).IsEqualTo("   ");
        await Assert.That(instructions).IsNull();
    }

    [Test]
    public async Task ExtractInstructions_SingleLetter_MatchesCorrectly()
    {
        var message = "%A% Hello";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        await Assert.That(text).IsEqualTo("Hello");
        await Assert.That(instructions).IsEqualTo("A");
    }

    [Test]
    public async Task ExtractInstructions_NumbersOnly_DoesNotMatch()
    {
        var message = "%123% Hello";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        // Numbers-only should not match as they need at least one letter
        await Assert.That(text).IsEqualTo("%123% Hello");
        await Assert.That(instructions).IsNull();
    }

    [Test]
    public async Task ExtractInstructions_MixedAlphaNumeric_Matches()
    {
        var message = "%soft1% Hello";
        
        var (text, instructions) = InstructionParser.ExtractInstructions(message);
        
        await Assert.That(text).IsEqualTo("Hello");
        await Assert.That(instructions).IsEqualTo("soft1");
    }
}
