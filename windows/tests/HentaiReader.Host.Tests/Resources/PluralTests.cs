using System.Runtime.InteropServices;

namespace HentaiReader.Host.Tests.Resources;

/// <summary>The system ICU (Windows 10 1903+) gives the CLDR categories the resource files are keyed by.</summary>
public partial class PluralTests
{
    [LibraryImport("icu.dll", EntryPoint = "uplrules_open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Open(string locale, ref int status);

    [LibraryImport("icu.dll", EntryPoint = "uplrules_select")]
    private static unsafe partial int Select(IntPtr rules, double number, char* keyword, int capacity, ref int status);

    [LibraryImport("icu.dll", EntryPoint = "uplrules_close")]
    private static partial void Close(IntPtr rules);

    [Theory]
    [InlineData("uk", 1, "one")]
    [InlineData("uk", 2, "few")]
    [InlineData("uk", 5, "many")]
    [InlineData("uk", 21, "one")]
    [InlineData("en", 1, "one")]
    [InlineData("en", 2, "other")]
    [InlineData("ja", 1, "other")]
    [InlineData("fr", 0, "one")]
    [InlineData("fr", 1_000_000, "many")]
    [InlineData("es", 1, "one")]
    [InlineData("de", 2, "other")]
    public void CategoryFor(string language, double number, string expected)
    {
        var status = 0;
        var rules = Open(language, ref status);
        Assert.True(status <= 0 && rules != IntPtr.Zero, $"uplrules_open({language}) failed: {status}");
        try
        {
            var buffer = new char[32];
            int length;
            unsafe
            {
                fixed (char* p = buffer) length = Select(rules, number, p, buffer.Length, ref status);
            }
            Assert.True(status <= 0, $"uplrules_select failed: {status}");
            Assert.Equal(expected, new string(buffer, 0, length));
        }
        finally
        {
            Close(rules);
        }
    }
}
