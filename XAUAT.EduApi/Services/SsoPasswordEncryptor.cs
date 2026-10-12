using System.Security.Cryptography;
using System.Text;

namespace XAUAT.EduApi.Services;

/// <summary>
/// 统一认证密码加密：随机 64 位前缀 + 密码，salt 作为 AES-CBC 密钥，
/// PKCS7 填充后 Base64 输出（与 xauat_sso_login.py 的 encrypt_aes 一致）。
/// </summary>
internal static class SsoPasswordEncryptor
{
    private const string AesChars = "ABCDEFGHJKMNPQRSTWXYZabcdefhijkmnprstwxyz2345678";

    public static string Encrypt(string password, string? salt)
    {
        if (string.IsNullOrEmpty(salt))
        {
            return password;
        }

        try
        {
            var iv = Encoding.UTF8.GetBytes(RandomString(16));
            var message = Encoding.UTF8.GetBytes(RandomString(64) + password);

            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(salt);
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            var cipher = encryptor.TransformFinalBlock(message, 0, message.Length);
            return Convert.ToBase64String(cipher);
        }
        catch (Exception)
        {
            // 与 Python 版一致：加密不可用时回退到明文密码。
            return password;
        }
    }

    private static string RandomString(int length)
    {
        var buffer = new char[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = AesChars[RandomNumberGenerator.GetInt32(AesChars.Length)];
        }

        return new string(buffer);
    }
}
