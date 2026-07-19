using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NexusMonach.Services;

/// <summary>
/// Stores the Matrix bot token outside settings.json. Generic credentials are
/// protected by Windows and are available only in the current Windows account.
/// </summary>
public static class WindowsCredentialStore
{
    private const string MatrixTokenTarget = "NexusMonach/Guardian/MatrixAccessToken";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    public static bool HasMatrixAccessToken() => !string.IsNullOrWhiteSpace(ReadMatrixAccessToken());

    public static string? ReadMatrixAccessToken() => Read(MatrixTokenTarget);

    public static void SaveMatrixAccessToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Matrix access token is empty.", nameof(token));
        Write(MatrixTokenTarget, token.Trim());
    }

    public static void DeleteMatrixAccessToken() => Delete(MatrixTokenTarget);

    private static void Write(string target, string secret)
    {
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > 2560)
            throw new ArgumentException("Credential is too long.", nameof(secret));

        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "Nexus Guardian Matrix bot"
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager rejected the Matrix token.");
        }
        finally
        {
            for (var i = 0; i < bytes.Length; i++) bytes[i] = 0;
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    private static string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.Unicode.GetString(bytes).TrimEnd('\0'); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    private static void Delete(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0)) return;
        const int ErrorNotFound = 1168;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
            throw new Win32Exception(error, "Windows Credential Manager could not remove the Matrix token.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
