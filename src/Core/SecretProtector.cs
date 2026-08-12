using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TextCascadeSharp.Core;

// DPAPI（CurrentUser 作用域）保护落盘敏感字段。
// 密文格式："dpapi:" + Base64(blob)。无前缀视为存量明文（迁移路径）。
internal static partial class SecretProtector
{
    private const string Prefix = "dpapi:";
    private const int CryptProtectUiForbidden = 0x1;
    // 固定 entropy：不追求机密性，只防止跨应用直接 Unprotect
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TextCascade.v1");

    [StructLayout(LayoutKind.Sequential)]
    internal struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial IntPtr LocalFree(IntPtr hMem);

    public static bool IsProtected(string stored) => stored.StartsWith(Prefix, StringComparison.Ordinal);

    // 空串→空串；否则 Prefix + Base64(protected)
    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }
        var input = Encoding.UTF8.GetBytes(plaintext);
        var output = ProtectCore(input, Entropy);
        return Prefix + Convert.ToBase64String(output);
    }

    // 无前缀→原样返回（存量明文迁移）；失败抛 CryptographicException
    public static string Unprotect(string stored)
    {
        if (!IsProtected(stored))
        {
            return stored;
        }
        var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
        var output = UnprotectCore(bytes, Entropy);
        return Encoding.UTF8.GetString(output);
    }

    // 迁移友好版：失败返回 false 且不抛，value 置空
    public static bool TryUnprotect(string stored, out string value)
    {
        value = string.Empty;
        if (!IsProtected(stored))
        {
            value = stored;
            return true;
        }
        try
        {
            value = Unprotect(stored);
            return true;
        }
        catch
        {
            value = string.Empty;
            return false;
        }
    }

    private static byte[] ProtectCore(byte[] input, byte[] entropy)
    {
        var inputBlob = new DataBlob { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        var entropyBlob = new DataBlob { cbData = entropy.Length, pbData = Marshal.AllocHGlobal(entropy.Length) };
        var outputBlob = new DataBlob();
        try
        {
            Marshal.Copy(input, 0, inputBlob.pbData, input.Length);
            Marshal.Copy(entropy, 0, entropyBlob.pbData, entropy.Length);
            if (!CryptProtectData(
                    ref inputBlob, null, ref entropyBlob,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob))
            {
                throw new CryptographicException($"DPAPI protect failed with error {Marshal.GetLastPInvokeError()}.");
            }
            var result = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            FreeBlob(inputBlob);
            FreeBlob(entropyBlob);
            // 输出缓冲区由 CryptProtectData 分配，必须 LocalFree
            if (outputBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outputBlob.pbData);
            }
        }
    }

    private static byte[] UnprotectCore(byte[] input, byte[] entropy)
    {
        var inputBlob = new DataBlob { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        var entropyBlob = new DataBlob { cbData = entropy.Length, pbData = Marshal.AllocHGlobal(entropy.Length) };
        var outputBlob = new DataBlob();
        try
        {
            Marshal.Copy(input, 0, inputBlob.pbData, input.Length);
            Marshal.Copy(entropy, 0, entropyBlob.pbData, entropy.Length);
            if (!CryptUnprotectData(
                    ref inputBlob, IntPtr.Zero, ref entropyBlob,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob))
            {
                throw new CryptographicException($"DPAPI unprotect failed with error {Marshal.GetLastPInvokeError()}.");
            }
            var result = new byte[outputBlob.cbData];
            Marshal.Copy(outputBlob.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            FreeBlob(inputBlob);
            FreeBlob(entropyBlob);
            if (outputBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outputBlob.pbData);
            }
        }
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(blob.pbData);
        }
    }
}
