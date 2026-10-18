using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace MediaOrganizer.Core.Update;

/// <summary>Asks Windows whether a file carries a valid Authenticode signature,
/// and who signed it.
///
/// <para>The check goes through <c>WinVerifyTrust</c> rather than through
/// <see cref="X509Certificate"/> alone, because reading the embedded
/// certificate only proves that <em>a</em> certificate is attached. Whether it
/// chains to a trusted root, whether it has expired, whether it was revoked —
/// that is what the operating system's own trust provider answers, using the
/// same rules SmartScreen and the UAC prompt use.</para></summary>
[SupportedOSPlatform("windows")]
internal static class Authenticode
{
    /// <summary>Verifies <paramref name="path"/> and returns whether Windows
    /// trusts the signature, plus the signer's subject line (null when the file
    /// carries no signature at all).</summary>
    internal static (bool Trusted, string? Subject) Inspect(string path)
    {
        var trusted = Verify(path);

        string? subject = null;
        try
        {
            // CreateFromSignedFile is marked obsolete, but nothing non-obsolete
            // reads the Authenticode signer out of a PE file. The raw bytes it
            // hands back go through the current loader.
#pragma warning disable SYSLIB0057
            var raw = X509Certificate.CreateFromSignedFile(path).GetRawCertData();
#pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(raw);
            subject = certificate.Subject;
        }
        catch
        {
            // No embedded certificate. Trusted is false in that case anyway.
        }

        return (trusted, subject);
    }

    private static bool Verify(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = Marshal.StringToCoTaskMemUni(path),
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        Marshal.StructureToPtr(fileInfo, filePointer, fDeleteOld: false);

        var data = new WinTrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
            dwUIChoice = WtdUiNone,

            // The whole chain is checked for revocation. This costs a network
            // round trip, but the caller has just finished downloading over the
            // network anyway, and a revoked signing certificate is precisely the
            // case this check exists for.
            fdwRevocationChecks = WtdRevokeWholeChain,
            dwUnionChoice = WtdChoiceFile,
            pFile = filePointer,
            dwStateAction = WtdStateActionVerify,
            dwProvFlags = WtdSaferFlag,
        };

        var action = GenericVerifyV2;
        try
        {
            var result = WinVerifyTrust(InvalidHandleValue, ref action, ref data);

            // Every state opened with STATEACTION_VERIFY has to be closed again,
            // or the provider leaks the parsed certificate chain per call.
            data.dwStateAction = WtdStateActionClose;
            WinVerifyTrust(InvalidHandleValue, ref action, ref data);

            return result == 0;
        }
        catch (DllNotFoundException)
        {
            // wintrust.dll is part of Windows; its absence means we are not on
            // Windows, where this guard cannot apply.
            return false;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath);
            Marshal.FreeCoTaskMem(filePointer);
        }
    }

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdSaferFlag = 0x100;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2 — the policy that asks
    /// "is this Authenticode signature valid and trusted".</summary>
    private static Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);
}
