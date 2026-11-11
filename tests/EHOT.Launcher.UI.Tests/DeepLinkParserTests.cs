using EHOT.Launcher.UI.Services;
using Xunit;

namespace EHOT.Launcher.UI.Tests;

/// <summary>
/// Разбор внешних ссылок ehot://… — то, что приходит из установщика, браузера и ярлыков.
/// Ошибка здесь означала бы «клик по приглашению ничего не делает» или запуск не того экрана.
/// </summary>
public sealed class DeepLinkParserTests
{
    [Theory]
    [InlineData("ehot://join/ABCD2345", "ABCD2345")]
    [InlineData("ehot://join/abcd2345", "ABCD2345")]
    [InlineData("ehot://join?code=ABCD2345", "ABCD2345")]
    [InlineData("EHOT://join/ABCD2345", "ABCD2345")]
    [InlineData("ABCD2345", "ABCD2345")]
    [InlineData("  abcd2345  ", "ABCD2345")]
    public void Parse_RecognizesJoinCode(string input, string expectedCode)
    {
        var link = DeepLinkParser.Parse(input);

        Assert.NotNull(link);
        Assert.Equal(DeepLinkKind.Join, link!.Kind);
        Assert.Equal(expectedCode, link.JoinCode);
    }

    [Fact]
    public void Parse_RoomKeepsName()
    {
        var link = DeepLinkParser.Parse("ehot://room/ABCD2345?name=%D0%92%D0%B5%D1%87%D0%B5%D1%80");

        Assert.NotNull(link);
        Assert.Equal(DeepLinkKind.Room, link!.Kind);
        Assert.Equal("ABCD2345", link.JoinCode);
        Assert.Equal("Вечер", link.RoomName);
    }

    [Theory]
    [InlineData("ehot://install?file=%2Fhome%2Fuser%2Fpack.mrpack", "/home/user/pack.mrpack")]
    [InlineData("ehot://modpack/C:/packs/evening.mrpack", "C:/packs/evening.mrpack")]
    public void Parse_RecognizesModpackInstall(string input, string expectedPath)
    {
        var link = DeepLinkParser.Parse(input);

        Assert.NotNull(link);
        Assert.Equal(DeepLinkKind.Install, link!.Kind);
        Assert.Equal(expectedPath, link.FilePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com")]
    [InlineData("ehot://join/SHORT")]
    [InlineData("ehot://join/ABCD0O11")] // 0 и O запрещены алфавитом кодов
    [InlineData("ehot://unknown/ABCD2345")]
    [InlineData("ehot://install")]
    public void Parse_RejectsGarbage(string input)
    {
        Assert.Null(DeepLinkParser.Parse(input));
    }

    [Theory]
    [InlineData("ABCD2345", true)]
    [InlineData("abcd2345", true)]
    [InlineData("ABCD234", false)]
    [InlineData("ABCD23456", false)]
    [InlineData("ABCD234O", false)]
    [InlineData("ABCD234I", false)]
    public void IsValidCode_MatchesInviteAlphabet(string code, bool expected)
    {
        Assert.Equal(expected, DeepLinkParser.IsValidCode(code));
    }
}
