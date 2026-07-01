using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using VRC.Core;
using VRC.SDKBase;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;

namespace Elypha.VRChatUploader
{
    // Mirrors VRChat SDK 3.10.4:
    // Packages/com.vrchat.base/Editor/VRCSDK/Dependencies/VRChat/API/VRCApi.cs
    // Source methods: UploadFile, UploadSimple, UploadMultipart.
    // Keep method order and stage names close to the SDK so API changes are easy to review in the IDE.
    internal sealed class AvatarUploadProtocol
    {
        public const int DefaultWorkers = 3;
        public const int DefaultPartSizeMiB = 64;

        private readonly int workers;
        private readonly int partSizeMiB;
        private readonly VRChatUploaderLog log;
        private readonly AvatarUploadProgressHandler progress;
        private readonly AvatarUploadWorkerProgressHandler workerProgress;

        public AvatarUploadProtocol(int workers, int partSizeMiB, VRChatUploaderLog log, AvatarUploadProgressHandler progress,
            AvatarUploadWorkerProgressHandler workerProgress = null)
        {
            this.workers = Mathf.Clamp(workers, 1, 8);
            this.partSizeMiB = Mathf.Clamp(partSizeMiB, 8, 100);
            this.log = log;
            this.progress = progress;
            this.workerProgress = workerProgress;
        }

        public int Workers => workers;
        public int PartSizeMiB => partSizeMiB;

        public async Task<string> UploadAvatarBundle(AvatarUploadContext context, string bundlePath, CancellationToken token)
        {
            var fileName = "Avatar - " + context.Avatar.Name + " - Asset bundle - " + Application.unityVersion + "_" +
                           ApiAvatar.VERSION.ApiVersion + "_" + VRC.Tools.Platform + "_" +
                           API.GetServerEnvironmentForApiUrl();
            var currentAssetUrl = context.Avatar.GetLatestAssetUrlForPlatform(VRC.Tools.Platform);
            var existingFileId = string.IsNullOrWhiteSpace(currentAssetUrl)
                ? ""
                : ApiFile.ParseFileIdFromFileAPIUrl(currentAssetUrl);

            return await UploadFile(bundlePath, existingFileId, fileName, "AvatarBundle", token);
        }

        public async Task<string> UploadAvatarImage(AvatarUploadContext context, string imagePath, CancellationToken token)
        {
            var fileName = "Avatar - " + context.Avatar.Name + " - Image - " + Application.unityVersion + "_" +
                           ApiAvatar.VERSION.ApiVersion + "_" + VRC.Tools.Platform + "_" +
                           API.GetServerEnvironmentForApiUrl();
            var currentImageUrl = string.IsNullOrWhiteSpace(context.ImageUrl)
                ? context.Avatar.ImageUrl
                : context.ImageUrl;
            var existingFileId = string.IsNullOrWhiteSpace(currentImageUrl)
                ? ""
                : ApiFile.ParseFileIdFromFileAPIUrl(currentImageUrl);

            return await UploadFile(imagePath, existingFileId, fileName, "AvatarImage", token);
        }

        private async Task<string> UploadFile(string filename, string fileId, string friendlyFileName, string contentKind, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(filename);
            var mimeType = AvatarFileUtil.GetMimeTypeFromExtension(extension);
            var creatingNewFile = string.IsNullOrWhiteSpace(fileId);
            VRCFile currentFile;

            if (creatingNewFile)
            {
                var requestData = new Dictionary<string, string>
                {
                    {"name", friendlyFileName},
                    {"mimeType", mimeType},
                    {"extension", extension}
                };
                currentFile = await VRCApi.Post<Dictionary<string, string>, VRCFile>("file", requestData, cancellationToken: token);
                log.Info($"VRCApi.UploadFile/{contentKind}: created file record {currentFile.ID}.");
            }
            else
            {
                log.Info($"VRCApi.UploadFile/{contentKind}: loading existing file record {fileId}.");
                currentFile = await VRCApi.Get<VRCFile>("file/" + fileId, forceRefresh: true, cancellationToken: token);
                log.Info($"VRCApi.UploadFile/{contentKind}: using file {currentFile.ID}, latest version {currentFile.GetLatestVersion()}.");
            }

            if (string.IsNullOrWhiteSpace(currentFile.ID))
            {
                throw new UploadException($"VRCApi.UploadFile/{contentKind}: failed to load or create file record.");
            }

            if (currentFile.HasQueuedOperation())
            {
                log.Info($"VRCApi.UploadFile/{contentKind}: latest version is queued; deleting it before retry. file={currentFile.ID}, version={currentFile.GetLatestVersion()}.");
                await VRCApi.Delete($"file/{currentFile.ID}/{currentFile.GetLatestVersion()}", cancellationToken: token);
                await Task.Delay(1000, token);
                currentFile = await VRCApi.Get<VRCFile>("file/" + currentFile.ID, forceRefresh: true, cancellationToken: token);
            }

            if (currentFile.IsLatestVersionErrored())
            {
                log.Info($"VRCApi.UploadFile/{contentKind}: latest version is errored; deleting it before retry. file={currentFile.ID}, version={currentFile.GetLatestVersion()}.");
                await VRCApi.Delete($"file/{currentFile.ID}/{currentFile.GetLatestVersion()}", cancellationToken: token);
                await Task.Delay(1000, token);
                currentFile = await VRCApi.Get<VRCFile>("file/" + currentFile.ID, forceRefresh: true, cancellationToken: token);
            }

            ReportUploadProgress("Processing file", 0.05f);
            var fileMd5Bytes = AvatarFileUtil.ComputeMd5Bytes(filename);
            var fileMd5Base64 = Convert.ToBase64String(fileMd5Bytes);
            var fileSize = new FileInfo(filename).Length;

            var signaturePath = await AvatarFileUtil.GenerateSignatureFile(filename, token);
            var signatureSize = new FileInfo(signaturePath).Length;
            var signatureMd5Bytes = AvatarFileUtil.ComputeMd5Bytes(signaturePath);
            var signatureMd5Base64 = Convert.ToBase64String(signatureMd5Bytes);
            var signatureMimeType = AvatarFileUtil.GetMimeTypeFromExtension(".sig");

            log.Info($"VRCApi.UploadFile/{contentKind}: local file size={AvatarFileUtil.FormatBytes(fileSize)}, MD5={fileMd5Base64}.");

            try
            {
                var versionAlreadyExists = false;
                if (currentFile.HasExistingOrPendingVersion())
                {
                    var latestVersion = currentFile.Versions[currentFile.GetLatestVersion()];
                    if (string.Equals(fileMd5Base64, latestVersion?.File?.MD5 ?? "", StringComparison.Ordinal))
                    {
                        if (!currentFile.IsLatestVersionWaiting())
                        {
                            throw new UploadException("This file was already uploaded");
                        }

                        var exactMatch = fileSize == latestVersion.File.SizeInBytes &&
                                         string.Equals(fileMd5Base64, latestVersion.File.MD5, StringComparison.Ordinal) &&
                                         signatureSize == latestVersion.Signature.SizeInBytes &&
                                         string.Equals(signatureMd5Base64, latestVersion.Signature.MD5, StringComparison.Ordinal);
                        if (exactMatch)
                        {
                            versionAlreadyExists = true;
                            log.Info($"VRCApi.UploadFile/{contentKind}: exact waiting version found; will resume. file={currentFile.ID}, version={currentFile.GetLatestVersion()}.");
                        }
                        else
                        {
                            log.Info($"VRCApi.UploadFile/{contentKind}: waiting version matched file MD5 only; deleting. file={currentFile.ID}, version={currentFile.GetLatestVersion()}.");
                            await VRCApi.Delete($"file/{currentFile.ID}/{currentFile.GetLatestVersion()}", cancellationToken: token);
                            await Task.Delay(1000, token);
                            currentFile = await VRCApi.Get<VRCFile>("file/" + currentFile.ID, forceRefresh: true, cancellationToken: token);
                        }
                    }
                    else if (currentFile.IsLatestVersionWaiting())
                    {
                        log.Info($"VRCApi.UploadFile/{contentKind}: latest waiting version belongs to another file; deleting. file={currentFile.ID}, version={currentFile.GetLatestVersion()}.");
                        await VRCApi.Delete($"file/{currentFile.ID}/{currentFile.GetLatestVersion()}", cancellationToken: token);
                        await Task.Delay(1000, token);
                        currentFile = await VRCApi.Get<VRCFile>("file/" + currentFile.ID, forceRefresh: true, cancellationToken: token);
                    }
                }

                if (!versionAlreadyExists)
                {
                    var requestData = new Dictionary<string, object>
                    {
                        {"signatureMd5", signatureMd5Base64},
                        {"signatureSizeInBytes", signatureSize},
                        {"fileMd5", fileMd5Base64},
                        {"fileSizeInBytes", fileSize}
                    };

                    log.Info($"VRCApi.UploadFile/{contentKind}: creating new file version. file={currentFile.ID}.");
                    var updatedFile = await VRCApi.Post<Dictionary<string, object>, VRCFile>(
                        $"file/{currentFile.ID}", requestData, cancellationToken: token);
                    if (string.IsNullOrWhiteSpace(updatedFile.ID))
                    {
                        throw new UploadException($"VRCApi.UploadFile/{contentKind}: failed to create new file version. file={currentFile.ID}");
                    }

                    log.Info($"VRCApi.UploadFile/{contentKind}: created version {currentFile.GetLatestVersion()} -> {updatedFile.GetLatestVersion()} for file={currentFile.ID}.");
                    currentFile = updatedFile;
                    await Task.Delay(1000, token);
                }

                var fileDescriptor = currentFile.Versions[currentFile.GetLatestVersion()].File;
                if (fileDescriptor == null)
                {
                    throw new UploadException($"VRCApi.UploadFile/{contentKind}: File descriptor is missing. file={currentFile.ID}, version={currentFile.GetLatestVersion()}.");
                }

                if (fileDescriptor.Status == "waiting")
                {
                    if (fileDescriptor.Category == "simple")
                    {
                        await UploadSimple(filename, FileUploadType.File, currentFile, mimeType, fileMd5Bytes, contentKind,
                            (status, percentage) => ReportUploadProgress(status, 0.15f + percentage * 0.75f), token);
                    }
                    else
                    {
                        await UploadMultipart(filename, FileUploadType.File, currentFile, contentKind,
                            (status, percentage) => ReportUploadProgress(status, 0.15f + percentage * 0.75f), token);
                    }
                }
                else
                {
                    log.Info($"VRCApi.UploadFile/{contentKind}: File upload skipped because status is {fileDescriptor.Status}. file={currentFile.ID}, version={currentFile.GetLatestVersion()}.");
                }

                var signatureDescriptor = currentFile.Versions[currentFile.GetLatestVersion()].Signature;
                if (signatureDescriptor == null)
                {
                    throw new UploadException($"VRCApi.UploadFile/{contentKind}: Signature descriptor is missing. file={currentFile.ID}, version={currentFile.GetLatestVersion()}.");
                }

                if (signatureDescriptor.Status == "waiting")
                {
                    if (signatureDescriptor.Category == "simple")
                    {
                        await UploadSimple(signaturePath, FileUploadType.Signature, currentFile, signatureMimeType,
                            signatureMd5Bytes, contentKind, (status, percentage) => ReportUploadProgress(status, 0.92f + percentage * 0.07f), token);
                    }
                    else
                    {
                        await UploadMultipart(signaturePath, FileUploadType.Signature, currentFile, contentKind,
                            (status, percentage) => ReportUploadProgress(status, 0.92f + percentage * 0.07f), token);
                    }
                }
                else
                {
                    log.Info($"VRCApi.UploadFile/{contentKind}: Signature upload skipped because status is {signatureDescriptor.Status}. file={currentFile.ID}, version={currentFile.GetLatestVersion()}.");
                }

                await Task.Delay(5000, token);
                currentFile = await VRCApi.Get<VRCFile>($"file/{currentFile.ID}", forceRefresh: true, cancellationToken: token);
                var latest = currentFile.Versions[currentFile.GetLatestVersion()];
                log.Info($"VRCApi.UploadFile/{contentKind}: final status file={currentFile.ID}, version={latest.Version}, status={latest.Status}, fileStatus={latest.File?.Status}, signatureStatus={latest.Signature?.Status}.");

                if (latest.File == null || string.IsNullOrWhiteSpace(latest.File.URL))
                {
                    throw new UploadException($"VRCApi.UploadFile/{contentKind}: final file URL is empty. file={currentFile.ID}, version={currentFile.GetLatestVersion()}.");
                }

                ReportUploadProgress("File upload finished", 1f);
                return latest.File.URL;
            }
            finally
            {
                TryDelete(signaturePath);
            }
        }

        private async Task UploadSimple(string filename, FileUploadType fileUploadType, VRCFile currentFile, string mimeType,
            byte[] fileContent, string contentKind, Action<string, float> onProgress, CancellationToken token)
        {
            var uploadKind = fileUploadType.ToString().ToLowerInvariant();
            var endpoint = $"file/{currentFile.ID}/{currentFile.GetLatestVersion()}/{uploadKind}/start";
            var startUploadResp = await VRCApi.Put<JObject>(endpoint, cancellationToken: token);
            var uploadUrl = startUploadResp.Value<string>("url");
            if (string.IsNullOrWhiteSpace(uploadUrl))
            {
                throw new UploadException($"VRCApi.UploadSimple/{contentKind}: upload URL is empty. endpoint={endpoint}, file={currentFile.ID}, version={currentFile.GetLatestVersion()}, uploadType={fileUploadType}.");
            }

            var fileData = await File.ReadAllBytesAsync(filename, token);
            log.Info($"VRCApi.UploadSimple/{contentKind}: uploading {fileUploadType}. file={currentFile.ID}, version={currentFile.GetLatestVersion()}, size={AvatarFileUtil.FormatBytes(fileData.LongLength)}.");
            await VRCApi.MakeRequestWithResponse<byte[], byte[]>(
                uploadUrl,
                HttpMethod.Put,
                body: fileData,
                contentType: mimeType,
                contentMD5: fileContent,
                timeout: 60 * 60,
                onProgress: percentage => onProgress?.Invoke($"Uploading {fileUploadType} ({percentage * 100f:F0}%)", percentage),
                cancellationToken: token);

            await VRCApi.Put<byte[]>($"file/{currentFile.ID}/{currentFile.GetLatestVersion()}/{uploadKind}/finish",
                cancellationToken: token);
            log.Info($"VRCApi.UploadSimple/{contentKind}: complete. file={currentFile.ID}, version={currentFile.GetLatestVersion()}, uploadType={fileUploadType}.");
        }

        private async Task UploadMultipart(string filename, FileUploadType fileUploadType, VRCFile currentFile,
            string contentKind, Action<string, float> onProgress, CancellationToken token)
        {
            var uploadKind = fileUploadType.ToString().ToLowerInvariant();
            var statusEndpoint = $"file/{currentFile.ID}/{currentFile.GetLatestVersion()}/{uploadKind}/status";
            var uploadStatus = await VRCApi.Get<JObject>(statusEndpoint, cancellationToken: token);

            var nextPartNumber = 1 + uploadStatus.Value<int>("nextPartNumber");
            var statusEtags = uploadStatus.Value<JArray>("etags")?.ToObject<List<string>>() ?? new List<string>();
            var etagsByPart = new Dictionary<int, string>();
            for (var i = 0; i < statusEtags.Count; i++)
            {
                etagsByPart[i + 1] = statusEtags[i];
            }

            var fileSize = new FileInfo(filename).Length;
            var partSizeBytes = partSizeMiB * 1024 * 1024;
            var parts = Math.Max(1, (int)Math.Ceiling((double)fileSize / partSizeBytes));
            log.Info($"VRCApi.UploadMultipart/{contentKind}: uploading {fileUploadType}. file={currentFile.ID}, version={currentFile.GetLatestVersion()}, parts={parts}, startPart={nextPartNumber}, partSize={AvatarFileUtil.FormatBytes(partSizeBytes)}, workers={workers}.");

            if (nextPartNumber > 1)
            {
                onProgress?.Invoke("Resuming multipart upload", Mathf.Clamp01((float)nextPartNumber / parts));
            }

            for (var batchStart = nextPartNumber; batchStart <= parts; batchStart += workers)
            {
                token.ThrowIfCancellationRequested();
                var batchEnd = Math.Min(parts, batchStart + workers - 1);
                var tasks = new List<Task<PartUploadResult>>();
                for (var workerIndex = 0; workerIndex < workers; workerIndex++)
                {
                    workerProgress?.Invoke(workerIndex, "", 0f);
                }

                for (var partNumber = batchStart; partNumber <= batchEnd; partNumber++)
                {
                    var workerIndex = partNumber - batchStart;
                    tasks.Add(UploadMultipartPart(filename, fileUploadType, currentFile, partNumber, parts, fileSize,
                        partSizeBytes, workerIndex, contentKind, onProgress, token));
                }

                var results = await Task.WhenAll(tasks);
                foreach (var result in results)
                {
                    etagsByPart[result.PartNumber] = result.ETag;
                }

                onProgress?.Invoke($"Uploaded {fileUploadType} parts {batchStart}-{batchEnd}/{parts}", (float)batchEnd / parts);
            }

            var orderedEtags = new List<string>();
            for (var partNumber = 1; partNumber <= parts; partNumber++)
            {
                if (!etagsByPart.TryGetValue(partNumber, out var etag) || string.IsNullOrWhiteSpace(etag))
                {
                    throw new UploadException($"VRCApi.UploadMultipart/{contentKind}: missing ETag. file={currentFile.ID}, version={currentFile.GetLatestVersion()}, uploadType={fileUploadType}, part={partNumber}/{parts}.");
                }

                orderedEtags.Add(etag);
            }

            log.Info($"VRCApi.UploadMultipart/{contentKind}: finishing multipart upload. file={currentFile.ID}, version={currentFile.GetLatestVersion()}, uploadType={fileUploadType}, parts={parts}.");
            await VRCApi.Put<Dictionary<string, List<string>>, byte[]>(
                $"file/{currentFile.ID}/{currentFile.GetLatestVersion()}/{uploadKind}/finish",
                new Dictionary<string, List<string>> {{"etags", orderedEtags}},
                cancellationToken: token);
            log.Info($"VRCApi.UploadMultipart/{contentKind}: complete. file={currentFile.ID}, version={currentFile.GetLatestVersion()}, uploadType={fileUploadType}, parts={parts}.");
        }

        private async Task<PartUploadResult> UploadMultipartPart(string filename, FileUploadType fileUploadType,
            VRCFile currentFile, int partNumber, int parts, long fileSize, int partSizeBytes,
            int workerIndex, string contentKind, Action<string, float> onProgress, CancellationToken token)
        {
            var uploadKind = fileUploadType.ToString().ToLowerInvariant();
            var startEndpoint = $"file/{currentFile.ID}/{currentFile.GetLatestVersion()}/{uploadKind}/start";
            var startUploadResp = await VRCApi.Put<JObject>(
                startEndpoint,
                queryParams: new Dictionary<string, string> {{"partNumber", partNumber.ToString()}},
                cancellationToken: token);
            var uploadUrl = startUploadResp.Value<string>("url");
            if (string.IsNullOrWhiteSpace(uploadUrl))
            {
                throw new UploadException($"VRCApi.UploadMultipart/{contentKind}: part upload URL is empty. endpoint={startEndpoint}, file={currentFile.ID}, version={currentFile.GetLatestVersion()}, uploadType={fileUploadType}, part={partNumber}/{parts}.");
            }

            var offset = (long)(partNumber - 1) * partSizeBytes;
            var bytesToRead = partNumber < parts ? partSizeBytes : fileSize - offset;
            if (bytesToRead <= 0 || bytesToRead > int.MaxValue)
            {
                throw new UploadException($"VRCApi.UploadMultipart/{contentKind}: invalid byte count {bytesToRead}. file={currentFile.ID}, version={currentFile.GetLatestVersion()}, uploadType={fileUploadType}, part={partNumber}/{parts}.");
            }

            var bytes = await AvatarFileUtil.ReadFileRange(filename, offset, (int)bytesToRead, token);
            var partStatus = $"{fileUploadType} part {partNumber}/{parts}";
            workerProgress?.Invoke(workerIndex, partStatus, 0f);
            var partProgressStart = (float)(partNumber - 1) / parts;
            var perPartProgress = 1f / parts;
            var result = await VRCApi.MakeRequestWithResponse<byte[], byte[]>(
                uploadUrl,
                HttpMethod.Put,
                body: bytes,
                timeout: 60 * 60,
                onProgress: percentage =>
                {
                    var total = partProgressStart + percentage * perPartProgress;
                    workerProgress?.Invoke(workerIndex, partStatus, percentage);
                    onProgress?.Invoke($"Uploading {fileUploadType} part {partNumber}/{parts} ({percentage * 100f:F0}%)", total);
                },
                cancellationToken: token);

            var etag = result.responseMessage.Headers.ETag?.Tag?.Trim('"', '\'');
            if (string.IsNullOrWhiteSpace(etag))
            {
                throw new UploadException($"VRCApi.UploadMultipart/{contentKind}: missing ETag. file={currentFile.ID}, version={currentFile.GetLatestVersion()}, uploadType={fileUploadType}, part={partNumber}/{parts}.");
            }

            workerProgress?.Invoke(workerIndex, partStatus, 1f);
            return new PartUploadResult(partNumber, etag);
        }

        private void ReportUploadProgress(string status, float percentage) =>
            progress?.Invoke(AvatarUploadProgressStage.Upload, status, percentage);

        public static bool IsAlreadyUploadedError(Exception ex)
        {
            var message = ex is ApiErrorException apiError ? apiError.ErrorMessage : ex.Message;
            return message?.IndexOf("already uploaded", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Temporary signature cleanup must not hide the upload result.
            }
        }

        private enum FileUploadType
        {
            File,
            Signature
        }

        private readonly struct PartUploadResult
        {
            public readonly int PartNumber;
            public readonly string ETag;

            public PartUploadResult(int partNumber, string etag)
            {
                PartNumber = partNumber;
                ETag = etag;
            }
        }
    }
}
