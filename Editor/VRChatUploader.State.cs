using System;
using System.Threading;
using UnityEngine;
using VRC.Core;
using VRC.SDKBase;

namespace Elypha.VRChatUploader
{
    public sealed partial class VRChatUploader
    {
        private sealed class AvatarSelectionState
        {
            public GameObject Root;
            public VRC_AvatarDescriptor Descriptor;
            public PipelineManager PipelineManager;
            public string BlueprintId = "";
            public string StatusText = "Not selected";
            public bool IsNewAvatar = true;

            public bool HasPipeline => Root != null && Descriptor != null && PipelineManager != null;
            public bool HasBlueprint => !string.IsNullOrWhiteSpace(BlueprintId);

            public void Clear()
            {
                Root = null;
                Descriptor = null;
                PipelineManager = null;
                BlueprintId = "";
                StatusText = "Not selected";
                IsNewAvatar = true;
            }

            public void SelectDescriptor(VRC_AvatarDescriptor descriptor)
            {
                Root = descriptor.gameObject;
                Descriptor = descriptor;
            }

            public void MarkMissingPipeline()
            {
                PipelineManager = null;
                BlueprintId = "";
                StatusText = "PipelineManager is missing";
            }

            public void SelectPipeline(PipelineManager pipelineManager)
            {
                PipelineManager = pipelineManager;
                BlueprintId = pipelineManager.blueprintId ?? "";
            }

            public void MarkNewAvatar()
            {
                IsNewAvatar = true;
                StatusText = "New avatar";
            }

            public void MarkExistingAvatar()
            {
                StatusText = BlueprintId;
            }
        }

        private enum RemoteAvatarCheckPhase
        {
            Ok,
            Failed,
            Checking,
            NotNeeded,
        }

        private sealed class RemoteAvatarCheckState
        {
            public RemoteAvatarCheckPhase Phase = RemoteAvatarCheckPhase.NotNeeded;
            public string BlueprintId = "";
            public int Serial;

            public bool IsReady => Phase is RemoteAvatarCheckPhase.NotNeeded or RemoteAvatarCheckPhase.Ok;
            public bool CanRetry => Phase == RemoteAvatarCheckPhase.Failed;

            public void Reset()
            {
                Serial++;
                BlueprintId = "";
                Phase = RemoteAvatarCheckPhase.NotNeeded;
            }

            public int Begin(string blueprintId)
            {
                Serial++;
                BlueprintId = blueprintId;
                Phase = RemoteAvatarCheckPhase.Checking;
                return Serial;
            }

            public bool IsCurrent(string blueprintId, int serial) =>
                serial == Serial
                && string.Equals(BlueprintId, blueprintId, StringComparison.Ordinal);
        }

        private sealed class OperationState
        {
            public bool Busy;
            public CancellationTokenSource Cancellation;
            public string StatusText = "Idle";
            public float CurrentProgress;
            public int ActiveWorkerProgressBars;

            public readonly float[] WorkerProgress;
            public readonly string[] WorkerStatusText;

            private AvatarUploadProgressStage[] _activeProgressStages = BuildCacheProgressStages;
            private int _activeProgressStep;

            public OperationState(int maxWorkerProgressBars)
            {
                WorkerProgress = new float[maxWorkerProgressBars];
                WorkerStatusText = new string[maxWorkerProgressBars];
            }

            public void Begin(AvatarUploadProgressStage[] progressStages, int workerBars)
            {
                _activeProgressStages = progressStages is { Length: > 0 } ? progressStages : BuildCacheProgressStages;
                _activeProgressStep = 0;
                CurrentProgress = 0f;
                StatusText = "Starting";
                ActiveWorkerProgressBars = workerBars > 1 ? workerBars : 0;

                for (var i = 0; i < WorkerProgress.Length; i++)
                {
                    WorkerProgress[i] = 0f;
                    WorkerStatusText[i] = "";
                }
            }

            public void SetProgress(string statusText, float percentage)
            {
                StatusText = statusText;
                CurrentProgress = Mathf.Clamp01(percentage);
            }

            public void SetStageProgress(AvatarUploadProgressStage stage, string statusText, float percentage)
            {
                StatusText = statusText;
                CurrentProgress = ResolveMainProgress(stage, percentage);
            }

            public bool SetWorkerProgress(int workerIndex, string statusText, float percentage)
            {
                if (workerIndex < 0 || workerIndex >= WorkerProgress.Length) return false;

                WorkerStatusText[workerIndex] = statusText;
                WorkerProgress[workerIndex] = Mathf.Clamp01(percentage);
                return true;
            }

            private float ResolveMainProgress(AvatarUploadProgressStage stage, float percentage)
            {
                var stageIndex = Array.IndexOf(_activeProgressStages, stage);
                if (stageIndex < 0 || stageIndex < _activeProgressStep)
                {
                    return CurrentProgress;
                }

                _activeProgressStep = stageIndex;

                var progress = (_activeProgressStep + Mathf.Clamp01(percentage)) / _activeProgressStages.Length;
                return Mathf.Clamp01(Math.Max(CurrentProgress, progress));
            }
        }
    }
}
