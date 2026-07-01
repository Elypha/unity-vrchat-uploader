using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using VRC.SDKBase;

namespace Elypha.VRChatUploader
{
    internal static class AvatarBuildCache
    {
        private const string CacheFolderName = "Elypha-VRChatUploader";

        public static string CacheDirectory
        {
            get
            {
                var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                return Path.Combine(projectRoot, "Library", CacheFolderName);
            }
        }

        public static CachedAvatarBundleManifest StoreBuiltBundle(string builtBundlePath, AvatarUploadContext context)
        {
            if (string.IsNullOrWhiteSpace(builtBundlePath) || !File.Exists(builtBundlePath))
            {
                throw new FileNotFoundException("SDK build did not return an existing .vrca path.", builtBundlePath);
            }

            Directory.CreateDirectory(CacheDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var platform = AvatarFileUtil.SanitizeFileName(VRC.Tools.Platform);
            var cachePath = Path.Combine(CacheDirectory, $"{context.AvatarId}_{platform}_{stamp}.vrca");
            File.Copy(builtBundlePath, cachePath, true);

            var manifest = new CachedAvatarBundleManifest
            {
                avatarId = context.AvatarId,
                platform = VRC.Tools.Platform,
                unityVersion = Application.unityVersion,
                sdkVersion = VRC.Tools.SdkVersion,
                bundlePath = cachePath,
                sizeBytes = new FileInfo(cachePath).Length,
                md5Base64 = AvatarFileUtil.ComputeMd5Base64(cachePath),
                createdAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };
            ApplyLabels(manifest);

            File.WriteAllText(ManifestPathForBundle(cachePath), JsonUtility.ToJson(manifest, true));
            return manifest;
        }

        public static CachedAvatarBundleManifest LoadAndValidateForUpload(string bundlePath, AvatarUploadContext context)
        {
            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
            {
                throw new FileNotFoundException("Cached .vrca was not found.", bundlePath);
            }

            var manifestPath = ManifestPathForBundle(bundlePath);
            var manifest = LoadManifest(manifestPath);
            if (manifest == null)
            {
                throw new FileNotFoundException("Cached .vrca manifest was not found.", manifestPath);
            }

            if (!string.Equals(manifest.avatarId, context.AvatarId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Cached bundle avatar ID mismatch. cache={manifest.avatarId}, target={context.AvatarId}.");
            }

            if (!string.Equals(manifest.platform, VRC.Tools.Platform, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Cached bundle platform mismatch. cache={manifest.platform}, current={VRC.Tools.Platform}.");
            }

            var fileInfo = new FileInfo(bundlePath);
            if (fileInfo.Length != manifest.sizeBytes)
            {
                throw new InvalidOperationException($"Cached bundle size mismatch. manifest={manifest.sizeBytes}, actual={fileInfo.Length}.");
            }

            var md5 = AvatarFileUtil.ComputeMd5Base64(bundlePath);
            if (!string.Equals(md5, manifest.md5Base64, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Cached bundle MD5 mismatch. manifest={manifest.md5Base64}, actual={md5}.");
            }

            return manifest;
        }

        public static List<CachedAvatarBundleManifest> ListRecent(VRChatUploaderLog log = null)
        {
            if (!Directory.Exists(CacheDirectory))
            {
                return new List<CachedAvatarBundleManifest>();
            }

            var manifests = new List<CachedAvatarBundleManifest>();
            foreach (var manifestPath in Directory.EnumerateFiles(CacheDirectory, "*.vrca.json"))
            {
                try
                {
                    var manifest = LoadManifest(manifestPath);
                    if (manifest != null && File.Exists(manifest.bundlePath))
                    {
                        manifests.Add(manifest);
                    }
                }
                catch (Exception ex)
                {
                    log?.Warn($"Cache manifest load failed: {manifestPath}: {VRChatUploaderLog.FormatException(ex)}");
                }
            }

            return manifests
                .OrderByDescending(m => m.createdAtLocal)
                .Take(20)
                .ToList();
        }

        public static void Delete(CachedAvatarBundleManifest manifest)
        {
            if (manifest == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(manifest.bundlePath))
            {
                return;
            }

            var fullBundlePath = Path.GetFullPath(manifest.bundlePath);
            EnsurePathIsInCache(fullBundlePath);

            var manifestPath = ManifestPathForBundle(fullBundlePath);
            if (File.Exists(fullBundlePath))
            {
                File.Delete(fullBundlePath);
            }

            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }
        }

        private static CachedAvatarBundleManifest LoadManifest(string manifestPath)
        {
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            try
            {
                var manifest = JsonUtility.FromJson<CachedAvatarBundleManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.bundlePath))
                {
                    throw new InvalidDataException("Cache manifest is missing required fields: " + manifestPath);
                }

                ApplyLabels(manifest);
                return manifest;
            }
            catch (Exception ex)
            {
                if (ex is InvalidDataException)
                {
                    throw;
                }

                throw new InvalidDataException("Failed to load cache manifest: " + manifestPath, ex);
            }
        }

        private static string ManifestPathForBundle(string bundlePath)
        {
            return bundlePath + ".json";
        }

        private static void EnsurePathIsInCache(string path)
        {
            var cacheDirectory = Path.GetFullPath(CacheDirectory);
            if (!cacheDirectory.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                cacheDirectory += Path.DirectorySeparatorChar;
            }

            if (!path.StartsWith(cacheDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to delete a bundle outside the uploader cache: " + path);
            }
        }

        private static void ApplyLabels(CachedAvatarBundleManifest manifest)
        {
            if (manifest == null)
            {
                return;
            }

            manifest.platformLabel = CreatePlatformLabel(manifest.platform);
            manifest.sizeLabel = CreateSizeLabel(manifest.sizeBytes);
        }

        private static string CreatePlatformLabel(string platform)
        {
            if (string.Equals(platform, "standalonewindows", StringComparison.OrdinalIgnoreCase))
            {
                return "PC";
            }

            if (string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase))
            {
                return "Quest";
            }

            return platform;
        }

        private static string CreateSizeLabel(long sizeBytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            var value = (double)sizeBytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return $"{value,6:F2} {units[unit]}";
        }
    }

    [Serializable]
    internal sealed class CachedAvatarBundleManifest
    {
        public string avatarId;
        public string platform;
        public string unityVersion;
        public string sdkVersion;
        public string bundlePath;
        public long sizeBytes;
        public string md5Base64;
        public string createdAtLocal;
        [NonSerialized] public string platformLabel;
        [NonSerialized] public string sizeLabel;
    }
}
