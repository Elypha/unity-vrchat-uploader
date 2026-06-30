using System.Collections.Generic;
using UnityEngine;
using VRC.Core;
using VRC.SDKBase;
using VRC.SDKBase.Editor.Api;

namespace Elypha.VRChatUploader
{
    internal sealed class AvatarUploadContext
    {
        public const int CopyrightAgreementVersion = 1;
        public const string CopyrightAgreementCode = "content.copyright.owned";

        public PipelineManager PipelineManager;
        public string AvatarId;
        public VRCAvatar Avatar;
        public bool FirstTimeUpload;
        public bool ReservedDuringOperation;
        public string PreparedThumbnailPath;
        public string ImageUrl;

        public static AvatarUploadContext ForExisting(PipelineManager pipelineManager, VRCAvatar avatar)
        {
            return new AvatarUploadContext
            {
                PipelineManager = pipelineManager,
                AvatarId = avatar.ID,
                Avatar = avatar,
                FirstTimeUpload = false,
                ReservedDuringOperation = false,
                ImageUrl = avatar.ImageUrl
            };
        }

        public static AvatarUploadContext ForPending(PipelineManager pipelineManager, VRCAvatar avatar, string thumbnailPath, bool reservedDuringOperation)
        {
            return new AvatarUploadContext
            {
                PipelineManager = pipelineManager,
                AvatarId = avatar.ID,
                Avatar = avatar,
                FirstTimeUpload = true,
                ReservedDuringOperation = reservedDuringOperation,
                PreparedThumbnailPath = thumbnailPath,
                ImageUrl = avatar.ImageUrl
            };
        }

        public Dictionary<string, object> CreateFinalizeRequest(string assetUrl)
        {
            var request = new Dictionary<string, object>
            {
                {"assetUrl", assetUrl},
                {"platform", VRC.Tools.Platform.ToString()},
                {"unityVersion", VRC.Tools.UnityVersion.ToString()},
                {"assetVersion", 1}
            };

            if (FirstTimeUpload)
            {
                request["name"] = Avatar.Name;
                request["tags"] = Avatar.Tags ?? new List<string>();
                request["releaseStatus"] = string.IsNullOrWhiteSpace(Avatar.ReleaseStatus) ? "private" : Avatar.ReleaseStatus;
                if (!string.IsNullOrWhiteSpace(ImageUrl))
                {
                    request["imageUrl"] = ImageUrl;
                }
            }

            return request;
        }
    }
}
