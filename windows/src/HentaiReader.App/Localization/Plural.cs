using System.Runtime.InteropServices;

namespace HentaiReader.App.Localization;

/// <summary>
/// CLDR plural categories from the Windows system ICU (icu.dll, Windows 10 1903+). Resource files
/// have no plural support, so this is the platform mechanism Constitution IX asks for.
/// </summary>
public static partial class Plural
{
    [LibraryImport("icu.dll", EntryPoint = "uplrules_open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Open(string locale, ref int status);

    [LibraryImport("icu.dll", EntryPoint = "uplrules_select")]
    private static unsafe partial int Select(IntPtr rules, double number, char* keyword, int capacity, ref int status);

    [LibraryImport("icu.dll", EntryPoint = "uplrules_close")]
    private static partial void Close(IntPtr rules);

    private static readonly Dictionary<string, IntPtr> Rules = new();

    /// <summary>"zero", "one", "two", "few", "many" or "other" for a language tag and number.</summary>
    public static string Category(string language, double number)
    {
        try
        {
            IntPtr rules;
            lock (Rules)
            {
                if (!Rules.TryGetValue(language, out rules))
                {
                    var status = 0;
                    rules = Open(language.Replace('-', '_'), ref status);
                    if (status > 0 || rules == IntPtr.Zero) return "other";
                    Rules[language] = rules;
                }
            }
            Span<char> buffer = stackalloc char[32];
            var err = 0;
            int length;
            unsafe
            {
                fixed (char* p = buffer) length = Select(rules, number, p, buffer.Length, ref err);
            }
            return err > 0 || length <= 0 ? "other" : new string(buffer[..length]);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return "other";
        }
    }
}
