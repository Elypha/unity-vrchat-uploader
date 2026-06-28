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
                avatarName = context.AvatarName,
                platform = VRC.Tools.Platform,
                unityVersion = Application.unityVersion,
                sdkVersion = VRC.Tools.SdkVersion,
                bundlePath = cachePath,
                sizeBytes = new FileInfo(cachePath).Length,
                md5Base64 = AvatarFileUtil.ComputeMd5Base64(cachePath),
                createdAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            File.WriteAllText(ManifestPathForBundle(cachePath), JsonUtility.ToJson(manifest, true));
            return manifest;
        }

        public static CachedAvatarBundleManifest LoadAndValidateForUpload(string bundlePath, AvatarUploadContext context)
        {
            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
            {
                throw new FileNotFoundException("Cached .vrca was not found.", bundlePath);
            }

            var manifest = LoadManifestForBundle(bundlePath);
            if (manifest == null)
            {
                throw new FileNotFoundException("Cached .vrca manifest was not found.", ManifestPathForBundle(bundlePath));
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

        public static List<CachedAvatarBundleManifest> ListRecent()
        {
            if (!Directory.Exists(CacheDirectory))
            {
                return new List<CachedAvatarBundleManifest>();
            }

            return Directory.EnumerateFiles(CacheDirectory, "*.vrca.json")
                .Select(LoadManifest)
                .Where(m => m != null && File.Exists(m.bundlePath))
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

            DeleteBundle(manifest.bundlePath);
        }

        public static void DeleteBundle(string bundlePath)
        {
            if (string.IsNullOrWhiteSpace(bundlePath))
            {
                return;
            }

            var fullBundlePath = Path.GetFullPath(bundlePath);
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

        private static CachedAvatarBundleManifest LoadManifestForBundle(string bundlePath)
        {
            return LoadManifest(ManifestPathForBundle(bundlePath));
        }

        private static CachedAvatarBundleManifest LoadManifest(string manifestPath)
        {
            try
            {
                if (!File.Exists(manifestPath))
                {
                    return null;
                }

                return JsonUtility.FromJson<CachedAvatarBundleManifest>(File.ReadAllText(manifestPath));
            }
            catch
            {
                return null;
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
    }

    [Serializable]
    internal sealed class CachedAvatarBundleManifest
    {
        public string avatarId;
        public string avatarName;
        public string platform;
        public string unityVersion;
        public string sdkVersion;
        public string bundlePath;
        public long sizeBytes;
        public string md5Base64;
        public string createdAtLocal;
    }
}
