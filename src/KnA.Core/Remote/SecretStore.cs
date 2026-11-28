using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KnA.Core.Localization;

namespace KnA.Core.Remote;

/// <summary>
/// The system's own keychain for the KnA server password, never a file of KnA: Windows Credential Manager, the macOS
/// Keychain, the Secret Service on Linux (GNOME Keyring, KWallet) through libsecret's secret-tool.
/// </summary>
public interface ISecretStore
{
    /// <summary>"Windows Credential Manager", "macOS Keychain"…</summary>
    string Name { get; }
    /// <summary>Null when it can be used; otherwise why not (said in Settings: the password is then asked at each start).</summary>
    string? Unavailable { get; }
    string? Read(string key);
    void Write(string key, string secret);
    void Delete(string key);
}

public static class SecretStores
{
    public const string Service = "KnA";

    /// <summary>The keychain entry of an account on a server.</summary>
    public static string Key(string url, string user) => $"{url.TrimEnd('/')}|{user}";

    public static ISecretStore ForThisSystem() =>
        OperatingSystem.IsWindows() ? new WindowsCredentials()
        : OperatingSystem.IsMacOS() ? new MacKeychain()
        : new LinuxSecretTool();
}

/// <summary>Windows Credential Manager (generic credentials of the signed-in user, "KnA:…").</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentials : ISecretStore
{
    public string Name => "Windows Credential Manager";
    public string? Unavailable => null;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    private const uint Generic = 1, LocalMachine = 2;

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private static string Target(string key) => SecretStores.Service + ":" + key;

    public string? Read(string key)
    {
        if (!CredRead(Target(key), Generic, 0, out var ptr)) return null;
        try
        {
            var c = Marshal.PtrToStructure<Credential>(ptr);
            return c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2);
        }
        finally { CredFree(ptr); }
    }

    public void Write(string key, string secret)
    {
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var c = new Credential
            {
                Type = Generic, TargetName = Target(key), CredentialBlob = blob, CredentialBlobSize = (uint)secret.Length * 2,
                Persist = LocalMachine, UserName = key.Split('|').Last(), Comment = "KnA server password",
            };
            if (!CredWrite(ref c, 0)) throw new InvalidOperationException(L.T("{0} refused to keep the password (error {1}).", Name, Marshal.GetLastWin32Error()));
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    public void Delete(string key) => CredDelete(Target(key), Generic, 0);
}

/// <summary>The macOS login keychain (generic password, service "KnA"), through Security.framework.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychain : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ItemNotFound = -25300;

    public string Name => "macOS Keychain";
    public string? Unavailable => null;

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        uint passwordLength, byte[] password, IntPtr item);
    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account,
        out uint passwordLength, out IntPtr password, out IntPtr item);
    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private (int Status, string? Secret, IntPtr Item) Find(string key)
    {
        byte[] svc = B(SecretStores.Service), acc = B(key);
        int st = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)svc.Length, svc, (uint)acc.Length, acc, out var len, out var data, out var item);
        if (st != 0) return (st, null, IntPtr.Zero);
        try
        {
            var bytes = new byte[len];
            Marshal.Copy(data, bytes, 0, (int)len);
            return (0, System.Text.Encoding.UTF8.GetString(bytes), item);
        }
        finally { SecKeychainItemFreeContent(IntPtr.Zero, data); }
    }

    public string? Read(string key)
    {
        var (st, secret, item) = Find(key);
        if (item != IntPtr.Zero) CFRelease(item);
        return st == 0 ? secret : null;
    }

    public void Write(string key, string secret)
    {
        var pw = B(secret);
        var (st, _, item) = Find(key);
        if (st == 0)
        {
            try { st = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)pw.Length, pw); }
            finally { CFRelease(item); }
        }
        else if (st == ItemNotFound)
        {
            byte[] svc = B(SecretStores.Service), acc = B(key);
            st = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)svc.Length, svc, (uint)acc.Length, acc, (uint)pw.Length, pw, IntPtr.Zero);
        }
        if (st != 0) throw new InvalidOperationException(L.T("{0} refused to keep the password (error {1}).", Name, st));
    }

    public void Delete(string key)
    {
        var (st, _, item) = Find(key);
        if (st != 0) return;
        try { SecKeychainItemDelete(item); }
        finally { CFRelease(item); }
    }
}

/// <summary>
/// The Secret Service (GNOME Keyring, KWallet, KeePassXC…) through libsecret's secret-tool: the password goes in
/// through standard input, never on a command line.
/// </summary>
internal sealed class LinuxSecretTool : ISecretStore
{
    private readonly string? _tool = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
        .Select(d => Path.Combine(d, "secret-tool")).FirstOrDefault(File.Exists);

    public string Name => "Secret Service (secret-tool)";
    public string? Unavailable => _tool is null
        ? L.T("secret-tool (libsecret) is not installed, so the password can't be kept in the system's keyring: install libsecret-tools (Debian/Ubuntu) or libsecret (Fedora/Arch), or type the password at each start.")
        : null;

    private static string[] Attributes(string key) => ["service", SecretStores.Service, "account", key];

    private (int Exit, string Out, string Err) Run(string[] args, string? stdin = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(_tool!) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        if (stdin is not null) p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000)) { try { p.Kill(); } catch (InvalidOperationException) { } return (-1, "", L.T("no answer from the keyring")); }
        return (p.ExitCode, output.Result, error.Result);
    }

    public string? Read(string key)
    {
        if (_tool is null) return null;
        var (exit, output, _) = Run(["lookup", .. Attributes(key)]);
        return exit == 0 && output.Length > 0 ? output.TrimEnd('\n') : null;
    }

    public void Write(string key, string secret)
    {
        if (_tool is null) throw new InvalidOperationException(Unavailable);
        var (exit, _, err) = Run(["store", "--label=KnA server password", .. Attributes(key)], secret);
        if (exit != 0) throw new InvalidOperationException(L.T("{0} refused to keep the password: {1}", Name, err.Trim()));
    }

    public void Delete(string key)
    {
        if (_tool is not null) Run(["clear", .. Attributes(key)]);
    }
}
