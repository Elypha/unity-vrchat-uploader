using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Elypha.VRChatUploader
{
    internal enum AvatarUploadProgressStage
    {
        Prepare,
        Build,
        Upload,
        Finalize,
        Verify
    }

    internal delegate void AvatarUploadProgressHandler(AvatarUploadProgressStage stage, string status, float percentage);
    internal delegate void AvatarUploadWorkerProgressHandler(int workerIndex, string status, float percentage);

    internal sealed class VRChatUploaderLog
    {
        private readonly List<string> lines = new();
        private string activeLogPath;

        public IReadOnlyList<string> Lines => lines;

        public void Begin(string operationName)
        {
            Directory.CreateDirectory(AvatarBuildCache.CacheDirectory);
            activeLogPath = Path.Combine(
                AvatarBuildCache.CacheDirectory,
                $"{DateTime.Now:yyyyMMdd-HHmmss}_{AvatarFileUtil.SanitizeFileName(operationName)}.log");
            lines.Clear();
            Info("Operation started: " + operationName);
            Info("Log file: " + activeLogPath);
        }

        public void End()
        {
            activeLogPath = null;
        }

        public void Info(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            lines.Add(line);
            while (lines.Count > 240)
            {
                lines.RemoveAt(0);
            }

            if (!string.IsNullOrWhiteSpace(activeLogPath))
            {
                try
                {
                    File.AppendAllText(activeLogPath, line + Environment.NewLine);
                }
                catch
                {
                    // Log IO must not break an upload operation.
                }
            }
        }
    }

    internal static class AvatarFileUtil
    {
        public static async Task<string> GenerateSignatureFile(string sourcePath, CancellationToken token)
        {
            var signaturePath = Path.Combine(
                AvatarBuildCache.CacheDirectory,
                Path.GetFileNameWithoutExtension(sourcePath) + "_" + Guid.NewGuid().ToString("N") + ".sig");

            await using var fileStream = File.OpenRead(sourcePath);
            await using var inStream = librsync.net.Librsync.ComputeSignature(fileStream);
            await using var signatureStream = File.Open(signaturePath, FileMode.Create, FileAccess.Write);
            await inStream.CopyToAsync(signatureStream);
            token.ThrowIfCancellationRequested();
            return signaturePath;
        }

        public static async Task<byte[]> ReadFileRange(string path, long offset, int length, CancellationToken token)
        {
            var buffer = new byte[length];
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
            stream.Seek(offset, SeekOrigin.Begin);

            var totalRead = 0;
            while (totalRead < length)
            {
                var read = await stream.ReadAsync(buffer, totalRead, length - totalRead, token);
                if (read == 0)
                {
                    throw new EndOfStreamException($"Unexpected end of file while reading {path} at {offset + totalRead}.");
                }

                totalRead += read;
            }

            return buffer;
        }

        public static byte[] ComputeMd5Bytes(string path)
        {
            using var md5 = MD5.Create();
            using var stream = File.OpenRead(path);
            return md5.ComputeHash(stream);
        }

        public static string ComputeMd5Base64(string path)
        {
            return Convert.ToBase64String(ComputeMd5Bytes(path));
        }

        public static string GetMimeTypeFromExtension(string extension)
        {
            switch ((extension ?? "").ToLowerInvariant())
            {
                case ".vrca":
                    return "application/x-avatar";
                case ".sig":
                    return "application/x-rsync-signature";
                default:
                    return "application/octet-stream";
            }
        }

        public static string FormatBytes(double bytes)
        {
            string[] units = {"B", "KB", "MB", "GB"};
            var value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value:F2} {units[unit]}";
        }

        public static string SanitizeFileName(string value)
        {
            value ??= "";
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(c, '_');
            }

            return value;
        }
    }
}
