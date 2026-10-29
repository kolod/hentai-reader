using System.Runtime.InteropServices;

namespace HentaiReader.App.Services;

/// <summary>Deletes files to the Windows Recycle Bin (FR-014), so the app needs no undo of its own.</summary>
public static partial class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004, FofNoConfirmation = 0x0010, FofAllowUndo = 0x0040, FofNoErrorUi = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int ShFileOperation(ref ShFileOpStruct op);

    /// <summary>Moves a file to the Recycle Bin; throws <see cref="IOException"/> when the shell refuses.</summary>
    public static void Delete(string path)
    {
        var op = new ShFileOpStruct
        {
            Func = FoDelete,
            From = path + "\0\0",   // double-null-terminated list
            Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
        };
        var result = ShFileOperation(ref op);
        if (result != 0 || op.AnyOperationsAborted != 0) throw new IOException($"could not move {path} to the Recycle Bin (0x{result:X})");
    }
}
