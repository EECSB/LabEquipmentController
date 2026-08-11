using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LabEquipmentController;

/// <summary>
/// Encrypts a secret so it can be written to the settings file without being readable there.
///
/// Uses DPAPI (<c>CryptProtectData</c>) scoped to the current Windows user: the ciphertext
/// can only be decrypted by the same user on the same machine, so a settings.json that is
/// copied, synced or backed up somewhere else carries nothing usable. That is the whole
/// point — the file is roaming plain JSON and an API key in it is a key on every machine
/// the profile touches.
///
/// P/Invoked rather than taken from the System.Security.Cryptography.ProtectedData package:
/// it is forty lines against a dependency, and this project has kept its dependency list to
/// the one thing that genuinely needed a library. Lives in the app rather than Core because
/// Core targets plain net10.0 and must stay free of Windows-only APIs.
/// </summary>
internal static class SecretStore
{
    /// <summary>Encrypt for this user. Returns null for null or empty input.</summary>
    public static string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return null;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
            return Convert.ToBase64String(Transform(bytes, protect: true));
        }
        catch
        {
            // Better to hold nothing than to write the key out in the clear.
            return null;
        }
    }

    /// <summary>
    /// Decrypt something produced by <see cref="Protect"/>. Returns null when it cannot be
    /// read — a settings file copied from another machine or user decrypts to nothing, which
    /// the caller treats the same as "no key set" rather than as an error.
    /// </summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(protectedBase64);
            return Encoding.UTF8.GetString(Transform(bytes, protect: false));
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------------ dpapi

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new DataBlob();
        var outBlob = new DataBlob();
        IntPtr buffer = Marshal.AllocHGlobal(input.Length);
        try
        {
            Marshal.Copy(input, 0, buffer, input.Length);
            inBlob.cbData = input.Length;
            inBlob.pbData = buffer;

            bool ok = protect
                ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                   CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                     CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);

            if (!ok) throw new InvalidOperationException("DPAPI refused the request.");

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    /// <summary>Never prompt: this runs on a UI thread during load and save.</summary>
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input, string? description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input, IntPtr description, IntPtr optionalEntropy, IntPtr reserved,
        IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
