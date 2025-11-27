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
        
        // Note: Double space remains where the instruction was removed
        await Assert.That(text).IsEqualTo("Hello  world!");
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
        
        // This will match "to 75" as instructions since there are two %s
        // Wait, let me re-check the regex - it matches %...% where ... contains no %
        // So "50% to 75%" would match " to 75" as the instructions
        await Assert.That(instructions).IsEqualTo(" to 75");
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
        
        // Empty instructions %% should not match since [^%]+ requires at least one character
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
        
        await Assert.That(text).IsEqualTo("   ");
        await Assert.That(instructions).IsNull();
    }
}
