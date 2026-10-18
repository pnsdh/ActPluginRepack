using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

// 게임 exe 가 스퀘어 에닉스가 서명한 진짜 파일인지 (Authenticode). 글로벌·한국·중국판 exe 모두
// O="SQUARE ENIX CO., LTD." 인증서로 서명되어 있다. 공식 패치 서버의 패치 파일은 http 로 오고 ZiPatch 의 CRC 는
// 전송 중 깨짐만 잡으므로, 중간에서 바꿔치기한 exe 를 스냅샷으로 남겨 분석·실행하지 않게 한다.
// 인증서 폐기 확인은 하지 않고 인터넷에서 아무것도 받지 않는다(오프라인에서도 되게). 타임스탬프가 있으면 서명 당시 기준
// (인증서가 만료된 옛 exe 도 통과).
internal static class GameExeSignature
{
    public const string Publisher = "SQUARE ENIX CO., LTD.";

    // 서명이 맞고 서명한 곳이 스퀘어 에닉스면 그대로, 아니면 ToolException. source: 안내에 쓸 출처.
    // 파일 이름(확장자)과 상관없이 내용으로 판단한다 (받는 중인 <exe>.tmp 도 확인할 수 있다)
    public static void VerifyFile(string path, string source)
    {
        if (!OperatingSystem.IsWindows()) throw new ToolException("게임 exe 의 서명 확인은 Windows 에서만 됩니다.");
        string? signer = null;
        int hr = Check(path, ref signer);
        if (hr != 0)
            throw new ToolException($"{source} 에서 받은 exe 에 스퀘어 에닉스의 올바른 서명이 없어 쓰지 않습니다 (0x{hr:X8}). " +
                                    "받는 도중 내용이 바뀌었을 수 있습니다. 다른 인터넷 연결에서 다시 시도하세요.");
        if (signer != Publisher)
            throw new ToolException($"{source} 에서 받은 exe 의 서명자가 스퀘어 에닉스가 아니라 쓰지 않습니다 ({signer ?? "알 수 없음"}).");
    }

    // WinVerifyTrust 결과(0 = 통과)와 서명 인증서의 O= 값
    private static int Check(string path, ref string? signer)
    {
        IntPtr pathPtr = Marshal.StringToHGlobalUni(path);
        IntPtr fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(new TrustFileInfo { Size = (uint)Marshal.SizeOf<TrustFileInfo>(), FilePath = pathPtr }, fileInfo, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(),
                UIChoice = 2,             // WTD_UI_NONE
                RevocationChecks = 0,     // WTD_REVOKE_NONE
                UnionChoice = 1,          // WTD_CHOICE_FILE
                File = fileInfo,
                StateAction = 1,          // WTD_STATEACTION_VERIFY (서명자를 읽은 뒤 CLOSE)
                ProvFlags = 0x1000 | 0x10, // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
            };
            var action = GenericVerifyV2;
            int hr = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            try
            {
                if (hr == 0) signer = Organization(data.StateData);
            }
            finally
            {
                data.StateAction = 2; // WTD_STATEACTION_CLOSE
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            return hr;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    // 확인한 서명의 첫 서명자 인증서에서 O= (없으면 null)
    private static string? Organization(IntPtr state)
    {
        IntPtr provider = WTHelperProvDataFromStateData(state);
        IntPtr sgnr = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        IntPtr provCert = sgnr == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(sgnr, 0);
        if (provCert == IntPtr.Zero) return null;
        IntPtr context = Marshal.ReadIntPtr(provCert, IntPtr.Size); // CRYPT_PROVIDER_CERT.pCert (cbStruct 다음, 포인터 정렬)
        if (context == IntPtr.Zero) return null;
        using var cert = new X509Certificate2(context);
        return cert.SubjectName.EnumerateRelativeDistinguishedNames()
            .Where(rdn => !rdn.HasMultipleElements && rdn.GetSingleElementType().Value == "2.5.4.10") // O
            .Select(rdn => rdn.GetSingleElementValue()).FirstOrDefault();
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFileInfo // WINTRUST_FILE_INFO
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData // WINTRUST_DATA
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SIPClientData;
        public uint UIChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr URLReference;
        public uint ProvFlags;
        public uint UIContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provData, uint signerIndex,
                                                                [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);
}
