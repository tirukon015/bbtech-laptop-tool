using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace BBTech.LaptopTool.Services
{
    /// <summary>
    /// Checks that a file carries a valid Authenticode signature (Windows WinVerifyTrust, the same check
    /// Windows uses for "Verified publisher") and returns the signer's subject. Used so that an installer
    /// without a configured SHA-256 is only run when it is genuinely signed by an approved publisher.
    /// </summary>
    public static class AuthenticodeVerifier
    {
        /// <summary>Null when the file is validly signed by a publisher whose subject contains <paramref name="requiredPublisher"/>; otherwise the reason.</summary>
        public static string Check(string path, string requiredPublisher)
        {
            int rc = Verify(path);
            if (rc != 0) return string.Format("The installer is not validly signed (code 0x{0:X8}).", rc);
            X500DistinguishedName subject;
            try
            {
#pragma warning disable SYSLIB0057
                subject = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)).SubjectName;
#pragma warning restore SYSLIB0057
            }
            catch (Exception ex) { return "The installer's signer could not be read: " + ex.Message; }

            // Exact attribute match (not a substring): "O=Microsoft Corporation Fake Ltd" must not pass.
            if (!SignerMatches(subject, requiredPublisher))
                return "The installer is signed by \"" + subject.Name + "\", not by " + requiredPublisher + ".";
            return null;
        }

        /// <summary>True when the subject's O= and CN= attributes are both exactly <paramref name="publisher"/>.</summary>
        public static bool SignerMatches(X500DistinguishedName subject, string publisher)
        {
            string o = null, cn = null;
            foreach (var line in subject.Format(true).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim().Trim('"');
                if (key == "O") { if (o != null) return false; o = value; }       // repeated attribute: refuse
                else if (key == "CN") { if (cn != null) return false; cn = value; }
            }
            return string.Equals(o, publisher, StringComparison.Ordinal) &&
                   string.Equals(cn, publisher, StringComparison.Ordinal);
        }

        private static int Verify(string path)
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                pcwszFilePath = path,
            };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            IntPtr pData = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2,               // WTD_UI_NONE
                    fdwRevocationChecks = 0,      // WTD_REVOKE_NONE (works offline)
                    dwUnionChoice = 1,            // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,            // WTD_STATEACTION_IGNORE
                    dwProvFlags = 0x00000080,     // WTD_REVOCATION_CHECK_NONE
                };
                pData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_DATA)));
                Marshal.StructureToPtr(data, pData, false);
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
                return WinVerifyTrust(new IntPtr(-1), ref action, pData);
            }
            finally
            {
                if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
                Marshal.FreeHGlobal(pFile);
            }
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, IntPtr data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
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
    }
}
