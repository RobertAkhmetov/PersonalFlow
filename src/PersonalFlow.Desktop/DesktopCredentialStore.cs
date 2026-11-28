using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace PersonalFlow.Desktop;

internal interface IDesktopCredentialStore
{
    string? Read(string key);
    void Write(string key, string value);
    void Delete(string key);
}

internal sealed class DesktopCredentialStore : IDesktopCredentialStore
{
    const string WindowsTargetPrefix = "PersonalFlow:";
    const string MacService = "PersonalFlow Desktop";
    static readonly byte[] MacServiceBytes = Encoding.UTF8.GetBytes(MacService);

    public string? Read(string key) => OperatingSystem.IsWindows()
        ? ReadWindows(key)
        : OperatingSystem.IsMacOS() ? ReadMac(key) : throw UnsupportedPlatform();

    public void Write(string key, string value)
    {
        if (OperatingSystem.IsWindows()) WriteWindows(key, value);
        else if (OperatingSystem.IsMacOS()) WriteMac(key, value);
        else throw UnsupportedPlatform();
    }

    public void Delete(string key)
    {
        if (OperatingSystem.IsWindows()) DeleteWindows(key);
        else if (OperatingSystem.IsMacOS()) DeleteMac(key);
        else throw UnsupportedPlatform();
    }

    static string? ReadWindows(string key)
    {
        if (!CredRead(WindowsTargetPrefix + key, GenericCredentialType, 0, out var credential))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null; // ERROR_NOT_FOUND
            throw new Win32Exception(error, "Could not read the PersonalFlow credential from Windows Credential Manager.");
        }

        try
        {
            var value = Marshal.PtrToStructure<NativeCredential>(credential);
            if (value.CredentialBlob == IntPtr.Zero || value.CredentialBlobSize == 0) return null;
            var bytes = new byte[value.CredentialBlobSize];
            Marshal.Copy(value.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { CredFree(credential); }
    }

    static void WriteWindows(string key, string value)
    {
        var target = Marshal.StringToHGlobalUni(WindowsTargetPrefix + key);
        var username = Marshal.StringToHGlobalUni("PersonalFlow owner");
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 5_120) throw new ArgumentOutOfRangeException(nameof(value), "The refresh credential exceeds the Windows vault size limit.");
        var blob = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, blob, bytes.Length);
        try
        {
            var credential = new NativeCredential
            {
                Type = GenericCredentialType,
                TargetName = target,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = blob,
                Persist = CredentialPersist.LocalMachine,
                UserName = username
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not store the PersonalFlow credential in Windows Credential Manager.");
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
            Marshal.FreeHGlobal(username);
            Marshal.FreeHGlobal(target);
        }
    }

    static void DeleteWindows(string key)
    {
        if (!CredDelete(WindowsTargetPrefix + key, GenericCredentialType, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new Win32Exception(error, "Could not remove the PersonalFlow credential from Windows Credential Manager.");
        }
    }

    static string? ReadMac(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)MacServiceBytes.Length, MacServiceBytes,
            (uint)account.Length, account, out var length, out var data, out var item);
        if (status == ItemNotFound) return null;
        EnsureMacSuccess(status, "read");
        try
        {
            var bytes = new byte[length];
            if (length > 0) Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    static void WriteMac(string key, string value)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var password = Encoding.UTF8.GetBytes(value);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)MacServiceBytes.Length, MacServiceBytes,
            (uint)account.Length, account, out _, out var oldPassword, out var item);
        if (status == ItemNotFound)
        {
            status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)MacServiceBytes.Length, MacServiceBytes,
                (uint)account.Length, account, (uint)password.Length, password, out item);
        }
        else if (status == 0)
        {
            try { status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password); }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, oldPassword);
                if (item != IntPtr.Zero) CFRelease(item);
                item = IntPtr.Zero;
            }
        }
        EnsureMacSuccess(status, "write");
        if (item != IntPtr.Zero) CFRelease(item);
    }

    static void DeleteMac(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)MacServiceBytes.Length, MacServiceBytes,
            (uint)account.Length, account, out var length, out var data, out var item);
        if (status == ItemNotFound) return;
        EnsureMacSuccess(status, "find for deletion");
        try { EnsureMacSuccess(SecKeychainItemDelete(item), "delete"); }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    static void EnsureMacSuccess(int status, string operation)
    {
        if (status != 0) throw new InvalidOperationException($"macOS Keychain could not {operation} the PersonalFlow credential (status {status}).");
    }

    static PlatformNotSupportedException UnsupportedPlatform() =>
        new("Desktop sign-in is supported only on Windows and macOS, where PersonalFlow can use the operating system's credential vault.");

    const int ItemNotFound = -25300;
    const uint GenericCredentialType = 1;

    enum CredentialPersist : uint { LocalMachine = 2 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersist Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
    static extern void CredFree(IntPtr buffer);

    const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainFindGenericPassword")]
    static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint passwordLength, out IntPtr passwordData, out IntPtr item);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainAddGenericPassword")]
    static extern int SecKeychainAddGenericPassword(IntPtr keychainOrArray, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint passwordLength, byte[] passwordData, out IntPtr item);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainItemModifyAttributesAndData")]
    static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint passwordLength, byte[] passwordData);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainItemDelete")]
    static extern int SecKeychainItemDelete(IntPtr item);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainItemFreeContent")]
    static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);

    [DllImport(CoreFoundationFramework, EntryPoint = "CFRelease")]
    static extern void CFRelease(IntPtr value);
}
