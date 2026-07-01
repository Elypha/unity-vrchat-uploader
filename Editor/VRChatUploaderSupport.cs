using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VRC.SDKBase.Editor.Api;

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
        private bool logWriteWarningEmitted;

        public IReadOnlyList<string> Lines => lines;

        public void Begin(string operationName)
        {
            Directory.CreateDirectory(AvatarBuildCache.CacheDirectory);
            activeLogPath = Path.Combine(
                AvatarBuildCache.CacheDirectory,
                $"{DateTime.Now:yyyyMMdd-HHmmss}_{AvatarFileUtil.SanitizeFileName(operationName)}.log");
            logWriteWarningEmitted = false;
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
            Write("INFO", message);
        }

        public void Warn(string message)
        {
            Write("WARN", message);
        }

        public void Error(string message)
        {
            Write("ERROR", message);
        }

        public void Error(string prefix, Exception ex)
        {
            Error(prefix + ": " + FormatException(ex));
        }

        public static string FormatException(Exception ex)
        {
            if (ex == null)
            {
                return "";
            }

            if (ex is AggregateException aggregateException)
            {
                return string.Join("; ", aggregateException.Flatten().InnerExceptions.Select(FormatException));
            }

            string message;
            if (ex is ApiErrorException apiError)
            {
                message = string.IsNullOrWhiteSpace(apiError.ErrorMessage) ? apiError.Message : apiError.ErrorMessage;
                message = $"VRChat API error {apiError.StatusCode}: {message}";
            }
            else
            {
                message = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : $"{ex.GetType().Name}: {ex.Message}";
            }

            return ex.InnerException == null
                ? message
                : message + " Inner: " + FormatException(ex.InnerException);
        }

        private void Write(string level, string message)
        {
            var prefix = level == "INFO" ? "" : level + ": ";
            var line = $"[{DateTime.Now:HH:mm:ss}] {prefix}{message}";
            AddLine(line);

            if (!string.IsNullOrWhiteSpace(activeLogPath))
            {
                try
                {
                    File.AppendAllText(activeLogPath, line + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    if (logWriteWarningEmitted)
                    {
                        return;
                    }

                    logWriteWarningEmitted = true;
                    var warning = $"[{DateTime.Now:HH:mm:ss}] WARN: Log file write failed: {ex.Message}";
                    AddLine(warning);
                    Debug.LogWarning(warning);
                }
            }
        }

        private void AddLine(string line)
        {
            lines.Add(line);
            while (lines.Count > 240)
            {
                lines.RemoveAt(0);
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
                case ".jpg":
                case ".jpeg":
                    return "image/jpeg";
                case ".png":
                    return "image/png";
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
