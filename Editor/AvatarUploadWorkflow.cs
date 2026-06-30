using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.SDK3A.Editor;
using VRC.SDKBase;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;

namespace Elypha.VRChatUploader
{
    internal sealed class AvatarUploadRequest
    {
        public GameObject AvatarRoot;
        public string NewAvatarName;
        public string NewAvatarReleaseStatus;
        public string CoverImagePath;
        public string CachedBundlePath;
        public int UploadAttempts;
        public float RetryDelaySeconds;
        public int ConcurrentWorkers;
        public int ConcurrentPartSizeMiB;
    }

    internal sealed class AvatarUploadWorkflow
    {
        private static readonly string[] ReleaseStatusOptions = {"private", "public"};

        private readonly AvatarUploadRequest request;
        private readonly Action<string> log;
        private readonly AvatarUploadProgressHandler progress;
        private readonly AvatarUploadWorkerProgressHandler workerProgress;

        public AvatarUploadWorkflow(AvatarUploadRequest request, Action<string> log, AvatarUploadProgressHandler progress,
            AvatarUploadWorkerProgressHandler workerProgress = null)
        {
            this.request = request;
            this.log = log;
            this.progress = progress;
            this.workerProgress = workerProgress;
        }

        public async Task<CachedAvatarBundleManifest> BuildCache(CancellationToken token)
        {
            var context = await PrepareAvatarContext(needsUpload: false, token);
            return await BuildAndCache(context, token);
        }

        public async Task UploadCachedConcurrent(CancellationToken token)
        {
            var context = await PrepareAvatarContext(needsUpload: true, token);
            progress(AvatarUploadProgressStage.Prepare, "Loading cached bundle", 0.5f);
            var manifest = AvatarBuildCache.LoadAndValidateForUpload(request.CachedBundlePath, context);
            await UploadCachedConcurrent(context, manifest, token);
        }

        public async Task<CachedAvatarBundleManifest> BuildCacheAndUploadConcurrent(CancellationToken token)
        {
            var context = await PrepareAvatarContext(needsUpload: true, token);
            var manifest = await BuildAndCache(context, token);
            await UploadCachedConcurrent(context, manifest, token);
            return manifest;
        }

        public async Task<CachedAvatarBundleManifest> BuildCacheAndUploadOfficial(CancellationToken token)
        {
            var context = await PrepareAvatarContext(needsUpload: true, token);
            var manifest = await BuildAndCache(context, token);
            await UploadCachedOfficial(context, manifest, token);
            return manifest;
        }

        public async Task UploadCachedOfficial(CancellationToken token)
        {
            var context = await PrepareAvatarContext(needsUpload: true, token);
            progress(AvatarUploadProgressStage.Prepare, "Loading cached bundle", 0.5f);
            var manifest = AvatarBuildCache.LoadAndValidateForUpload(request.CachedBundlePath, context);
            await UploadCachedOfficial(context, manifest, token);
        }

        private async Task UploadCachedOfficial(AvatarUploadContext context, CachedAvatarBundleManifest manifest, CancellationToken token)
        {
            await EnsureCopyrightAgreement(context, token);

            var uploadWatch = System.Diagnostics.Stopwatch.StartNew();
            for (var attempt = 1; attempt <= request.UploadAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    log($"Official SDK upload attempt {attempt}/{request.UploadAttempts}; bundle MD5={manifest.md5Base64}.");
                    progress(AvatarUploadProgressStage.Upload, $"Official upload {attempt}/{request.UploadAttempts}", 0f);

                    if (context.FirstTimeUpload)
                    {
                        await VRCApi.CreateNewAvatar(context.AvatarId, context.Avatar, manifest.bundlePath,
                            context.PreparedThumbnailPath, ProgressForStage(AvatarUploadProgressStage.Upload), token);
                    }
                    else
                    {
                        await VRCApi.UpdateAvatarBundle(context.AvatarId, context.Avatar, manifest.bundlePath,
                            ProgressForStage(AvatarUploadProgressStage.Upload), token);
                    }

                    progress(AvatarUploadProgressStage.Verify, "Verifying avatar bundle", 0f);
                    var avatar = await VRCApi.GetAvatar(context.AvatarId, forceRefresh: true, cancellationToken: token);
                    if (await RemoteAvatarPointsAtBundle(avatar, manifest.md5Base64, token))
                    {
                        uploadWatch.Stop();
                        log($"Official upload verified: {uploadWatch.Elapsed.TotalSeconds:F2}s.");
                        progress(AvatarUploadProgressStage.Verify, "Avatar bundle verified", 1f);
                        return;
                    }

                    if (await TryFinalizeCompletedBundleVersion(context, manifest.md5Base64, token))
                    {
                        uploadWatch.Stop();
                        log($"Official upload finalized existing completed version: {uploadWatch.Elapsed.TotalSeconds:F2}s.");
                        progress(AvatarUploadProgressStage.Verify, "Avatar bundle verified", 1f);
                        return;
                    }

                    throw new UploadException("Official upload returned, but the remote avatar does not point at the cached bundle.");
                }
                catch (Exception ex) when (!token.IsCancellationRequested && attempt < request.UploadAttempts)
                {
                    log($"Official upload attempt {attempt} failed: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(request.RetryDelaySeconds), token);
                }
            }

            throw new UploadException($"Official upload failed after {request.UploadAttempts} attempts.");
        }

        private async Task<AvatarUploadContext> PrepareAvatarContext(bool needsUpload, CancellationToken token)
        {
            progress(AvatarUploadProgressStage.Prepare, "Preparing avatar context", 0f);
            var pipelineManager = ValidateAvatarRoot();
            if (APIUser.CurrentUser == null)
            {
                throw new InvalidOperationException("VRChat SDK is not logged in.");
            }

            if (!APIUser.CurrentUser.canPublishAvatars)
            {
                throw new InvalidOperationException("Current VRChat user cannot publish avatars.");
            }

            if (string.IsNullOrWhiteSpace(pipelineManager.blueprintId))
            {
                var name = string.IsNullOrWhiteSpace(request.NewAvatarName) && request.AvatarRoot != null
                    ? request.AvatarRoot.name
                    : request.NewAvatarName;
                name = (name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidOperationException("Avatar name cannot be empty for a new avatar.");
                }

                var thumbnailPath = "";
                if (needsUpload)
                {
                    if (string.IsNullOrWhiteSpace(request.CoverImagePath) || !File.Exists(request.CoverImagePath))
                    {
                        throw new FileNotFoundException("A cover image is required before uploading a new avatar.", request.CoverImagePath);
                    }

                    thumbnailPath = request.CoverImagePath;
                }

                var avatar = new VRCAvatar
                {
                    Name = name,
                    Description = "",
                    ReleaseStatus = NormalizeReleaseStatus(request.NewAvatarReleaseStatus),
                    Tags = new List<string>()
                };

                log($"Reserving new avatar ID for \"{avatar.Name}\".");
                progress(AvatarUploadProgressStage.Prepare, "Reserving avatar ID", 0.25f);
                avatar = await VRCApi.CreateAvatarRecord(avatar, ProgressForStage(AvatarUploadProgressStage.Prepare), token);
                if (string.IsNullOrWhiteSpace(avatar.ID))
                {
                    throw new UploadException("Failed to reserve a new avatar ID.");
                }

                Undo.RecordObject(pipelineManager, "Assigning avatar blueprint ID");
                pipelineManager.blueprintId = avatar.ID;
                EditorUtility.SetDirty(pipelineManager);

                log($"Reserved avatar ID {avatar.ID}; PipelineManager blueprint ID updated.");
                return AvatarUploadContext.ForPending(pipelineManager, avatar, thumbnailPath, reservedDuringOperation: true);
            }

            log("Loading remote avatar " + pipelineManager.blueprintId + ".");
            progress(AvatarUploadProgressStage.Prepare, "Loading remote avatar", 0.5f);
            var remoteAvatar = await VRCApi.GetAvatar(pipelineManager.blueprintId, forceRefresh: true, cancellationToken: token);
            if (string.IsNullOrWhiteSpace(remoteAvatar.ID))
            {
                throw new InvalidOperationException("Could not load remote avatar " + pipelineManager.blueprintId);
            }

            if (remoteAvatar.AuthorId != APIUser.CurrentUser.id)
            {
                throw new OwnershipException("Remote avatar belongs to a different user.");
            }

            if (!remoteAvatar.PendingUpload)
            {
                return AvatarUploadContext.ForExisting(pipelineManager, remoteAvatar);
            }

            var pendingThumbnailPath = "";
            if (needsUpload)
            {
                if (string.IsNullOrWhiteSpace(request.CoverImagePath) || !File.Exists(request.CoverImagePath))
                {
                    throw new FileNotFoundException("A cover image is required for the first upload of this pending avatar.", request.CoverImagePath);
                }

                pendingThumbnailPath = request.CoverImagePath;
            }

            return AvatarUploadContext.ForPending(pipelineManager, remoteAvatar, pendingThumbnailPath, reservedDuringOperation: false);
        }

        private async Task<CachedAvatarBundleManifest> BuildAndCache(AvatarUploadContext context, CancellationToken token)
        {
            try
            {
                var builder = await GetAvatarBuilder(token);
                log($"Starting SDK build for {request.AvatarRoot.name} ({context.AvatarId}).");
                progress(AvatarUploadProgressStage.Build, "Building avatar bundle", 0f);

                VRC_SdkBuilder.ActiveBuildType = VRC_SdkBuilder.BuildType.Publish;
                var buildWatch = System.Diagnostics.Stopwatch.StartNew();
                var builtBundlePath = await builder.Build(request.AvatarRoot);
                token.ThrowIfCancellationRequested();

                var manifest = AvatarBuildCache.StoreBuiltBundle(builtBundlePath, context);
                buildWatch.Stop();
                log($"Cached bundle: {manifest.bundlePath}");
                log($"Build cache result: {buildWatch.Elapsed.TotalSeconds:F2}s, {AvatarFileUtil.FormatBytes(manifest.sizeBytes)}, MD5={manifest.md5Base64}.");
                progress(AvatarUploadProgressStage.Build, "Build cached", 1f);
                return manifest;
            }
            catch
            {
                if (context.ReservedDuringOperation)
                {
                    log("Build failed after reserving a new avatar ID; deleting the pending avatar record.");
                    try
                    {
                        await VRCApi.DeleteAvatar(context.AvatarId);
                        Undo.RecordObject(context.PipelineManager, "Clearing avatar blueprint ID after failed build");
                        context.PipelineManager.blueprintId = "";
                        EditorUtility.SetDirty(context.PipelineManager);
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        log("Failed to clean up reserved avatar ID: " + ex.Message);
                    }
                }

                throw;
            }
        }

        private async Task UploadCachedConcurrent(AvatarUploadContext context, CachedAvatarBundleManifest manifest, CancellationToken token)
        {
            await EnsureCopyrightAgreement(context, token);

            var protocol = new AvatarUploadProtocol(
                request.ConcurrentWorkers,
                request.ConcurrentPartSizeMiB,
                log,
                progress,
                workerProgress);

            var uploadWatch = System.Diagnostics.Stopwatch.StartNew();
            Exception lastError = null;
            for (var attempt = 1; attempt <= request.UploadAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    log($"Concurrent upload attempt {attempt}/{request.UploadAttempts}; workers={protocol.Workers}, partSizeMiB={protocol.PartSizeMiB}, bundle MD5={manifest.md5Base64}.");
                    var bundleUrl = await protocol.UploadAvatarBundle(context, manifest.bundlePath, token);
                    progress(AvatarUploadProgressStage.Finalize, "Finalizing avatar record", 0f);
                    await FinalizeAvatarRecord(context, bundleUrl, token);

                    progress(AvatarUploadProgressStage.Verify, "Verifying avatar bundle", 0f);
                    var avatar = await VRCApi.GetAvatar(context.AvatarId, forceRefresh: true, cancellationToken: token);
                    if (await RemoteAvatarPointsAtBundle(avatar, manifest.md5Base64, token))
                    {
                        uploadWatch.Stop();
                        log($"Concurrent upload verified: {uploadWatch.Elapsed.TotalSeconds:F2}s.");
                        progress(AvatarUploadProgressStage.Verify, "Avatar bundle verified", 1f);
                        return;
                    }

                    throw new UploadException("Concurrent upload finalized, but the remote avatar does not point at the cached bundle.");
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    lastError = ex;
                    log($"Concurrent upload attempt {attempt} failed: {ex.Message}");

                    if (AvatarUploadProtocol.IsAlreadyUploadedError(ex) &&
                        await TryFinalizeCompletedBundleVersion(context, manifest.md5Base64, token))
                    {
                        uploadWatch.Stop();
                        log($"Concurrent upload recovered by finalizing the completed version: {uploadWatch.Elapsed.TotalSeconds:F2}s.");
                        progress(AvatarUploadProgressStage.Verify, "Avatar bundle verified", 1f);
                        return;
                    }

                    if (attempt < request.UploadAttempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(request.RetryDelaySeconds), token);
                    }
                }
            }

            throw new UploadException($"Concurrent upload failed after {request.UploadAttempts} attempts.", lastError);
        }

        private async Task FinalizeAvatarRecord(AvatarUploadContext context, string assetUrl, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(assetUrl))
            {
                throw new UploadException("Cannot finalize avatar without an asset URL.");
            }

            if (context.FirstTimeUpload && string.IsNullOrWhiteSpace(context.ImageUrl))
            {
                if (string.IsNullOrWhiteSpace(context.PreparedThumbnailPath) ||
                    !File.Exists(context.PreparedThumbnailPath))
                {
                    throw new FileNotFoundException("Prepared avatar cover image was not found.", context.PreparedThumbnailPath);
                }

                log("Uploading avatar cover through public VRCApi.UpdateAvatarImage.");
                var avatar = await VRCApi.UpdateAvatarImage(
                    context.AvatarId,
                    context.Avatar,
                    context.PreparedThumbnailPath,
                    ProgressForStage(AvatarUploadProgressStage.Finalize),
                    token);
                context.Avatar = avatar;
                context.ImageUrl = avatar.ImageUrl;

                if (string.IsNullOrWhiteSpace(context.ImageUrl))
                {
                    throw new UploadException("Cover upload returned an empty image URL.");
                }
            }

            var requestData = context.CreateFinalizeRequest(assetUrl);
            log("Finalizing avatar record with bundle URL.");
            progress(AvatarUploadProgressStage.Finalize, "Finalizing avatar record", 0f);
            var finalized = await VRCApi.Put<Dictionary<string, object>, VRCAvatar>(
                $"avatars/{context.AvatarId}", requestData, cancellationToken: token);
            context.Avatar = finalized;
            progress(AvatarUploadProgressStage.Finalize, "Avatar record finalized", 1f);
        }

        private async Task<bool> TryFinalizeCompletedBundleVersion(AvatarUploadContext context, string localMd5, CancellationToken token)
        {
            var currentAssetUrl = context.Avatar.GetLatestAssetUrlForPlatform(VRC.Tools.Platform);
            if (string.IsNullOrWhiteSpace(currentAssetUrl))
            {
                return false;
            }

            var latest = await GetLatestFileDescriptorForUrl(currentAssetUrl, token);
            if (latest == null || string.IsNullOrWhiteSpace(latest.URL))
            {
                return false;
            }

            if (!string.Equals(latest.MD5, localMd5, StringComparison.Ordinal))
            {
                return false;
            }

            await FinalizeAvatarRecord(context, latest.URL, token);
            return true;
        }

        private static async Task<bool> RemoteAvatarPointsAtBundle(VRCAvatar avatar, string localMd5, CancellationToken token)
        {
            var assetUrl = avatar.GetLatestAssetUrlForPlatform(VRC.Tools.Platform);
            if (string.IsNullOrWhiteSpace(assetUrl))
            {
                return false;
            }

            var latest = await GetLatestFileDescriptorForUrl(assetUrl, token);
            return latest != null &&
                   string.Equals(latest.MD5, localMd5, StringComparison.Ordinal) &&
                   string.Equals(latest.URL, assetUrl, StringComparison.Ordinal);
        }

        private static async Task<VRCFile.VersionEntry.FileDescriptor> GetLatestFileDescriptorForUrl(string assetUrl, CancellationToken token)
        {
            var fileId = ApiFile.ParseFileIdFromFileAPIUrl(assetUrl);
            if (string.IsNullOrWhiteSpace(fileId))
            {
                return null;
            }

            var file = await VRCApi.Get<VRCFile>("file/" + fileId, forceRefresh: true, cancellationToken: token);
            var latestIndex = file.GetLatestVersion();
            if (file.Versions == null || latestIndex < 0 || latestIndex >= file.Versions.Count)
            {
                return null;
            }

            return file.Versions[latestIndex]?.File;
        }

        private async Task EnsureCopyrightAgreement(AvatarUploadContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            log("Auto-accepting copyright ownership agreement for " + context.AvatarId + ".");
            var result = await VRCApi.ContentUploadConsent(new VRCAgreement
            {
                AgreementCode = AvatarUploadContext.CopyrightAgreementCode,
                AgreementFulltext = VRCCopyrightAgreement.AgreementText,
                ContentId = context.AvatarId,
                Version = AvatarUploadContext.CopyrightAgreementVersion
            });

            if (result.ContentId != context.AvatarId ||
                result.Version != AvatarUploadContext.CopyrightAgreementVersion ||
                result.AgreementCode != AvatarUploadContext.CopyrightAgreementCode)
            {
                throw new OwnershipException("Failed to accept copyright ownership agreement.");
            }

            log($"Copyright agreement accepted. contentId={context.AvatarId}, sdk={VRC.Tools.SdkVersion}.");
        }

        private PipelineManager ValidateAvatarRoot()
        {
            if (request.AvatarRoot == null)
            {
                throw new InvalidOperationException("Avatar Root is empty.");
            }

            if (!request.AvatarRoot.TryGetComponent<VRC_AvatarDescriptor>(out _))
            {
                throw new InvalidOperationException("Avatar Root does not have a VRCAvatarDescriptor.");
            }

            if (!request.AvatarRoot.TryGetComponent<PipelineManager>(out var pipelineManager))
            {
                throw new InvalidOperationException("Avatar Root does not have a PipelineManager.");
            }

            return pipelineManager;
        }

        private Action<string, float> ProgressForStage(AvatarUploadProgressStage stage) =>
            (status, percentage) => progress(stage, status, percentage);

        private static async Task<IVRCSdkAvatarBuilderApi> GetAvatarBuilder(CancellationToken token)
        {
            if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out var builder))
            {
                return builder;
            }

            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
            await Task.Delay(750, token);
            if (VRCSdkControlPanel.TryGetBuilder<IVRCSdkAvatarBuilderApi>(out builder))
            {
                return builder;
            }

            throw new InvalidOperationException("Could not get the VRChat avatar builder. Open the VRChat SDK Control Panel once, then retry.");
        }

        private static string NormalizeReleaseStatus(string value)
        {
            return Array.IndexOf(ReleaseStatusOptions, value) >= 0 ? value : "private";
        }
    }
}
