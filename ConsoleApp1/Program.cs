using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Reflection;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;

// Programa que embala um .exe em um PNG e cria um executável (loader) com o PNG anexado.
// O mesmo binário atua como builder (quando não há payload anexado) e como loader (quando existe).

class Program
{
    static readonly byte[] MARKER = Encoding.ASCII.GetBytes("<SHADEPNGv1>");

    static void Main(string[] args)
    {
        string selfPath = Assembly.GetEntryAssembly().Location;

        // Se o próprio executável contém um payload anexado, agir como loader
        if (TryGetEmbeddedInfo(selfPath, out long pngLength))
        {
            try
            {
                ExtractAndRun(selfPath, pngLength);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Erro no loader: " + ex.Message);
            }
            return;
        }

        // Modo builder: encontrar um .exe na mesma pasta (exceto este) e empacotar
        // obter pasta do stub
        string dir = Path.GetDirectoryName(selfPath);

        // se o usuário passou um argumento, usar como caminho do exe a empacotar
        string exeToPack = null;
        if (args.Length > 0)
        {
            exeToPack = Path.GetFullPath(args[0]);
            if (!File.Exists(exeToPack))
            {
                Console.WriteLine("Arquivo não encontrado: " + exeToPack);
                return;
            }
        }
        else
        {
            // pedir para usuário colar o caminho completo do .exe
            Console.Write("Cole o caminho completo do .exe a empacotar: ");
            string input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("Nenhum caminho fornecido.");
                return;
            }
            try
            {
                exeToPack = Path.GetFullPath(input.Trim().Trim('"'));
            }
            catch (Exception)
            {
                Console.WriteLine("Caminho inválido.");
                return;
            }
            if (!File.Exists(exeToPack))
            {
                Console.WriteLine("Arquivo não encontrado: " + exeToPack);
                return;
            }
        }

        // evitar empacotar o stub executando
        if (String.Equals(exeToPack, selfPath, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Não é possível empacotar o próprio stub. Escolha outro .exe.");
            return;
        }

        Console.WriteLine("Empacotando: " + exeToPack);

        // save PNG and final exe in the same directory as the target exe
        string targetDir = Path.GetDirectoryName(exeToPack) ?? dir;

        byte[] png = EncodeExeToPng(exeToPack);
        string pngPath = Path.Combine(targetDir, Path.GetFileName(exeToPack) + ".png");
        File.WriteAllBytes(pngPath, png);

        string finalExe = Path.Combine(targetDir, Path.GetFileNameWithoutExtension(exeToPack) + "_packed.exe");
        using (var outFs = File.Create(finalExe))
        {
            // copiar stub (este executável)
            using (var inFs = File.OpenRead(selfPath))
            {
                inFs.CopyTo(outFs);
            }

            // anexar PNG, tamanho (8 bytes) e marker
            outFs.Write(png, 0, png.Length);
            outFs.Write(BitConverter.GetBytes((long)png.Length), 0, 8);
            outFs.Write(MARKER, 0, MARKER.Length);
        }

        Console.WriteLine("Executável final gerado: " + finalExe);
    }

    static bool TryGetEmbeddedInfo(string path, out long pngLength)
    {
        pngLength = 0;
        const int lenBytes = 8;
        if (!File.Exists(path)) return false;
        using (var fs = File.OpenRead(path))
        {
            long min = MARKER.Length + lenBytes;
            if (fs.Length < min) return false;
            fs.Seek(-min, SeekOrigin.End);
            byte[] tail = new byte[min];
            fs.Read(tail, 0, tail.Length);
            // tail = [8 bytes length][marker]
            if (!tail.Skip(lenBytes).Take(MARKER.Length).SequenceEqual(MARKER)) return false;
            pngLength = BitConverter.ToInt64(tail, 0);
            if (pngLength <= 0 || pngLength > fs.Length) return false;
            return true;
        }
    }

    static void ExtractAndRun(string selfPath, long pngLength)
    {
        using (var fs = File.OpenRead(selfPath))
        {
            long start = fs.Length - (MARKER.Length + 8) - pngLength;
            if (start < 0) throw new InvalidDataException("Dados anexados inválidos.");
            fs.Seek(start, SeekOrigin.Begin);
            byte[] png = new byte[pngLength];
            int read = 0;
            while (read < png.Length)
            {
                int r = fs.Read(png, read, png.Length - read);
                if (r <= 0) throw new EndOfStreamException();
                read += r;
            }

            byte[] payload = DecodePngToPayload(png);

            // Novo formato: [2 bytes fileCount] then for each file: [2 bytes nameLen][name UTF8][8 bytes fileLen][file bytes]
            if (payload.Length < 2) throw new InvalidDataException("Payload muito pequeno.");
            ushort fileCount = BitConverter.ToUInt16(payload, 0);
            int offset = 2;
            string outDir = Path.Combine(Path.GetDirectoryName(selfPath), "restored");
            Directory.CreateDirectory(outDir);
            string mainExePath = null;

            for (int i = 0; i < fileCount; i++)
            {
                if (offset + 2 > payload.Length) throw new InvalidDataException("Payload corrompido (nameLen).");
                ushort nameLen = BitConverter.ToUInt16(payload, offset);
                offset += 2;
                if (offset + nameLen > payload.Length) throw new InvalidDataException("Payload corrompido (name).");
                string name = Encoding.UTF8.GetString(payload, offset, nameLen);
                offset += nameLen;
                if (offset + 8 > payload.Length) throw new InvalidDataException("Payload corrompido (fileLen).");
                long fileLen = BitConverter.ToInt64(payload, offset);
                offset += 8;
                if (fileLen < 0 || offset + fileLen > payload.Length) throw new InvalidDataException("Payload corrompido (file data).");

                byte[] fileBytes = new byte[fileLen];
                Array.Copy(payload, offset, fileBytes, 0, (int)fileLen);
                offset += (int)fileLen;

                string outPath = Path.Combine(outDir, name);
                File.WriteAllBytes(outPath, fileBytes);
                Console.WriteLine("Arquivo restaurado: " + outPath);

                // primeira entrada é o exe principal
                if (i == 0)
                {
                    mainExePath = outPath;
                }
            }

            if (mainExePath == null) throw new InvalidDataException("Nenhum exe encontrado no payload.");

            // executar o executável restaurado com working directory apontando para a pasta restaurada
            var psi = new ProcessStartInfo(mainExePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(mainExePath)
            };
            Process.Start(psi);
        }
    }

    static byte[] EncodeExeToPng(string exePath)
    {
        // Prefer using external ShadeOfColor2 if available. It provides metadata and SHA1 integrity.
        string dir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
        string shadeExe = Path.Combine(dir, "ShadeOfColor2.exe");
        if (File.Exists(shadeExe))
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "shadepack_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string inCopy = Path.Combine(tempDir, Path.GetFileName(exePath));
            string outPng = Path.Combine(tempDir, Path.GetFileName(exePath) + ".png");
            File.Copy(exePath, inCopy);

            var psi = new ProcessStartInfo(shadeExe, $"-crypt \"{inCopy}\" \"{outPng}\"")
            {
                UseShellExecute = true,
                CreateNoWindow = false,
                WorkingDirectory = tempDir
            };
            var p = Process.Start(psi);
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(outPng))
            {
                throw new InvalidOperationException("ShadeOfColor2 failed to crypt the file.");
            }

            byte[] png = File.ReadAllBytes(outPng);
            try { Directory.Delete(tempDir, true); } catch { }
            return png;
        }

        // Fallback: internal payload packing of exe + related sibling files (runtimeconfig, deps, etc.)
        string exeDir = Path.GetDirectoryName(exePath);
        string baseName = Path.GetFileNameWithoutExtension(exePath);

        // collect files: put the exe first, then sibling files that start with baseName + '.'
        var files = new List<string>();
        files.Add(exePath);
        try
        {
            if (exeDir != null)
            {
                var siblings = Directory.GetFiles(exeDir)
                    .Where(f => !String.Equals(f, exePath, StringComparison.OrdinalIgnoreCase)
                                && Path.GetFileName(f).StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                files.AddRange(siblings);
            }
        }
        catch { }

        // build payload: [2 bytes fileCount] then for each file: [2 bytes nameLen][name UTF8][8 bytes fileLen][file bytes]
        ushort fileCount = (ushort)Math.Min(files.Count, ushort.MaxValue);
        List<byte[]> fileDatas = new List<byte[]>();
        List<string> fileNames = new List<string>();
        long totalSize = 2; // fileCount
        foreach (var f in files.Take(fileCount))
        {
            byte[] data = File.ReadAllBytes(f);
            string name = Path.GetFileName(f);
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            fileDatas.Add(data);
            fileNames.Add(name);
            totalSize += 2 + nameBytes.Length + 8 + data.LongLength;
        }

        byte[] payload = new byte[totalSize];
        int off = 0;
        Array.Copy(BitConverter.GetBytes((ushort)fileCount), 0, payload, off, 2); off += 2;
        for (int i = 0; i < fileCount; i++)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(fileNames[i]);
            Array.Copy(BitConverter.GetBytes((ushort)nameBytes.Length), 0, payload, off, 2); off += 2;
            Array.Copy(nameBytes, 0, payload, off, nameBytes.Length); off += nameBytes.Length;
            Array.Copy(BitConverter.GetBytes((long)fileDatas[i].LongLength), 0, payload, off, 8); off += 8;
            Array.Copy(fileDatas[i], 0, payload, off, fileDatas[i].Length); off += fileDatas[i].Length;
        }

        // mapear payload em pixels RGB
        int totalPixels = (payload.Length + 2) / 3; // ceil
        if (totalPixels == 0) totalPixels = 1;
        int width = Math.Min(1024, totalPixels);
        int height = (totalPixels + width - 1) / width;

        using (var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb))
        {
            int idx = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte r = idx < payload.Length ? payload[idx++] : (byte)0;
                    byte g = idx < payload.Length ? payload[idx++] : (byte)0;
                    byte b = idx < payload.Length ? payload[idx++] : (byte)0;
                    bmp.SetPixel(x, y, Color.FromArgb(r, g, b));
                }
            }

            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    static byte[] DecodePngToPayload(byte[] pngData)
    {
        // If ShadeOfColor2 is available, write PNG to temp file and call it to decrypt.
        string dir = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
        string shadeExe = Path.Combine(dir, "ShadeOfColor2.exe");
        if (File.Exists(shadeExe))
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "shadeunpack_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string pngPath = Path.Combine(tempDir, "payload.png");
            File.WriteAllBytes(pngPath, pngData);

            var start = DateTime.UtcNow;
            var psi = new ProcessStartInfo(shadeExe, $"-decrypt \"{pngPath}\"")
            {
                UseShellExecute = true,
                CreateNoWindow = false,
                WorkingDirectory = tempDir
            };
            var p = Process.Start(psi);
            p.WaitForExit();

            // ShadeOfColor2 writes the restored file(s) into the working directory; pick newest file(s)
            var files = Directory.GetFiles(tempDir).Select(f => new FileInfo(f)).ToList();
            FileInfo found = null;
            if (files.Count > 0)
            {
                // If Shade produced files, assume it produced the decoded payload as files; pack them back into our payload format
                // We'll pick files ordered by name and build our payload structure
                var ordered = files.OrderBy(fi => fi.Name).ToList();
                // build payload same as internal packing
                ushort fileCount = (ushort)Math.Min(ordered.Count, ushort.MaxValue);
                List<byte[]> fileDatas = new List<byte[]>();
                List<string> fileNames = new List<string>();
                long totalSize = 2;
                foreach (var fi in ordered.Take(fileCount))
                {
                    fileDatas.Add(File.ReadAllBytes(fi.FullName));
                    fileNames.Add(fi.Name);
                    totalSize += 2 + Encoding.UTF8.GetByteCount(fi.Name) + 8 + fi.Length;
                }

                byte[] payload = new byte[totalSize];
                int off = 0;
                Array.Copy(BitConverter.GetBytes((ushort)fileCount), 0, payload, off, 2); off += 2;
                for (int i = 0; i < fileCount; i++)
                {
                    byte[] nameBytes = Encoding.UTF8.GetBytes(fileNames[i]);
                    Array.Copy(BitConverter.GetBytes((ushort)nameBytes.Length), 0, payload, off, 2); off += 2;
                    Array.Copy(nameBytes, 0, payload, off, nameBytes.Length); off += nameBytes.Length;
                    Array.Copy(BitConverter.GetBytes((long)fileDatas[i].LongLength), 0, payload, off, 8); off += 8;
                    Array.Copy(fileDatas[i], 0, payload, off, fileDatas[i].Length); off += fileDatas[i].Length;
                }

                try { Directory.Delete(tempDir, true); } catch { }
                return payload;
            }

            try { Directory.Delete(tempDir, true); } catch { }
            // fallback to internal decode if Shade didn't produce files
        }

        return InternalDecodePngToPayload(pngData);
    }

    static byte[] InternalDecodePngToPayload(byte[] pngData)
    {
        using (var ms = new MemoryStream(pngData))
        using (var bmp = new Bitmap(ms))
        {
            var bytes = new List<byte>(bmp.Width * bmp.Height * 3);
            for (int y = 0; y < bmp.Height; y++)
            {
                for (int x = 0; x < bmp.Width; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    bytes.Add(c.R);
                    bytes.Add(c.G);
                    bytes.Add(c.B);
                }
            }

            // recuperar header para saber tamanho real do payload (novo formato com fileCount)
            if (bytes.Count < 2) return bytes.ToArray();
            ushort fileCount = BitConverter.ToUInt16(bytes.Take(2).ToArray(), 0);
            int pos = 2;
            for (int i = 0; i < fileCount; i++)
            {
                if (pos + 2 > bytes.Count) return bytes.ToArray();
                ushort nameLen = BitConverter.ToUInt16(bytes.Skip(pos).Take(2).ToArray(), 0);
                pos += 2;
                if (pos + nameLen > bytes.Count) return bytes.ToArray();
                pos += nameLen;
                if (pos + 8 > bytes.Count) return bytes.ToArray();
                long fileLen = BitConverter.ToInt64(bytes.Skip(pos).Take(8).ToArray(), 0);
                pos += 8;
                if (fileLen < 0) return bytes.ToArray();
                pos += (int)fileLen;
                if (pos > bytes.Count) return bytes.ToArray();
            }

            int total = pos;
            return bytes.Take(total).ToArray();
        }
    }
}
