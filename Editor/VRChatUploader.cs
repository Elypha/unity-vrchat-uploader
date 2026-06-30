using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using VRC.Core;
using VRC.SDKBase;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;

namespace Elypha.VRChatUploader
{
    [MovedFrom(true, "Elypha.VRChatUploader", null, "ElyphaVRChatUploader")]
    public sealed partial class VRChatUploader : EditorWindow
    {
        private const string WindowTitle = "VRChatUploader";
        private const string MenuPath = "Elypha/VRChatUploader";
        private const int DefaultAttempts = 8;
        private const float DefaultRetryDelaySeconds = 5f;
        private const float LabelWidth = 155f;
        private const float CacheActionsWidth = 188f;
        private const int CacheVisibleRows = 6;
        private const int MaxWorkerProgressBars = 8;

        private static readonly string[] ReleaseStatusOptions = { "private", "public" };
        private static readonly Color TitleColour = new(230f / 255f, 194f / 255f, 153f / 255f);
        private static readonly Color SubtitleColour = new(210f / 255f, 210f / 255f, 210f / 255f);
        private static readonly Color SelectedCacheColour = new(102f / 255f, 153f / 255f, 255f / 255f, 0.32f);

        private static readonly AvatarUploadProgressStage[] BuildCacheProgressStages =
        {
            AvatarUploadProgressStage.Prepare,
            AvatarUploadProgressStage.Build
        };

        private static readonly AvatarUploadProgressStage[] UploadProgressStages =
        {
            AvatarUploadProgressStage.Prepare,
            AvatarUploadProgressStage.Upload,
            AvatarUploadProgressStage.Finalize,
            AvatarUploadProgressStage.Verify
        };

        private static readonly AvatarUploadProgressStage[] BuildUploadProgressStages =
        {
            AvatarUploadProgressStage.Prepare,
            AvatarUploadProgressStage.Build,
            AvatarUploadProgressStage.Upload,
            AvatarUploadProgressStage.Finalize,
            AvatarUploadProgressStage.Verify
        };

        private static GUIStyle _cacheEntryLabelStyle;
        private static GUIStyle _cacheEntryButtonStyle;

        [SerializeField] private string _newAvatarReleaseStatus = "private";
        [SerializeField] private string _coverImagePath;
        [SerializeField] private int _uploadAttempts = DefaultAttempts;
        [SerializeField] private float _retryDelaySeconds = DefaultRetryDelaySeconds;
        [SerializeField] private int _concurrentWorkers = AvatarUploadProtocol.DefaultWorkers;
        [SerializeField] private int _concurrentPartSizeMiB = AvatarUploadProtocol.DefaultPartSizeMiB;

        private string _newAvatarName;
        private string _selectedBundlePath;

        private readonly AvatarSelectionState _avatar = new();
        private readonly RemoteAvatarCheckState _remoteCheck = new();
        private readonly OperationState _operation = new(MaxWorkerProgressBars);
        private readonly VRChatUploaderLog _log = new();
        private List<CachedAvatarBundleManifest> _allCacheEntries = new();
        private Vector2 _mainScroll;
        private Vector2 _logScroll;
        private Vector2 _cacheScroll;

        [MenuItem(MenuPath, false, 1)]
        public static void Open()
        {
            var window = GetWindow<VRChatUploader>(WindowTitle);
            window.minSize = new Vector2(550, 550);
        }

        private void OnEnable()
        {
            if (_avatar.Root == null
                && Selection.activeGameObject != null
                && TryGetAvatarDescriptorInParents(Selection.activeGameObject, out var avatarDescriptor))
            {
                UpdateAvatarInfo(avatarDescriptor);
            }

            RefreshCacheEntries();
        }

        private void OnDisable()
        {
            _operation.Cancellation?.Cancel();
            EditorUtility.ClearProgressBar();
        }

        private void OnGUI()
        {
            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll, false, false, GUILayout.ExpandWidth(true));

            using (new EditorGUI.DisabledScope(_operation.Busy))
            {
                DrawAvatarSection();
                DrawCacheSection();
            }

            DrawOperationSection();
            EditorGUILayout.EndScrollView();
        }

        private void DrawAvatarSection()
        {
            DrawTitle($"Avatar  →  {_avatar.StatusText}");
            var labelWidth = CalculateLabelWidth("Avatar Root", "Name", "Visibility", "Cover");

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                {
                    DrawAvatarSelector(labelWidth);

                    using (new EditorGUI.DisabledScope(_avatar.Root == null))
                    {
                        _newAvatarName = DrawTextRow("Name",
                            string.IsNullOrWhiteSpace(_newAvatarName) && _avatar.Root != null
                                ? _avatar.Root.name
                                : _newAvatarName,
                            labelWidth);

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.LabelField("Visibility", GUILayout.Width(labelWidth));
                            var releaseIndex = Array.IndexOf(ReleaseStatusOptions, _newAvatarReleaseStatus);
                            if (releaseIndex < 0) releaseIndex = 0;
                            releaseIndex = GUILayout.Toolbar(releaseIndex, ReleaseStatusOptions, GUILayout.ExpandWidth(true));
                            _newAvatarReleaseStatus = ReleaseStatusOptions[releaseIndex];
                        }

                        DrawCoverSelector(labelWidth);
                    }
                }

                GUILayout.Space(1);
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Space(2);
                    DrawVerticalSeparator(82, Color.grey);
                }

                GUILayout.Space(1);
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(96)))
                {
                    GUILayout.Space(2);
                    using (new EditorGUI.DisabledScope(!CanRetryRemoteCheck()))
                    {
                        var buttonText = _remoteCheck.Phase switch
                        {
                            RemoteAvatarCheckPhase.Checking => "Checking",
                            RemoteAvatarCheckPhase.Ok => "OK",
                            RemoteAvatarCheckPhase.Failed => "Retry Check",
                            _ => "No Blueprint",
                        };
                        if (GUILayout.Button(buttonText, GUILayout.Height(20)))
                        {
                            RunRemoteAvatarCheck(_avatar.BlueprintId);
                        }
                    }

                    GUILayout.Space(4);
                    using (new EditorGUI.DisabledScope(!CanBuildCache()))
                    {
                        if (GUILayout.Button("Build", GUILayout.Height(58)))
                        {
                            RunBuildCache();
                        }
                    }
                }
            }
        }

        private void DrawAvatarSelector(float labelWidth)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Avatar Root", GUILayout.Width(labelWidth));

                using var check = new EditorGUI.ChangeCheckScope();
                var selectedDescriptor = (VRC_AvatarDescriptor)EditorGUILayout.ObjectField(
                    _avatar.Descriptor,
                    typeof(VRC_AvatarDescriptor),
                    true,
                    GUILayout.ExpandWidth(true));

                if (check.changed)
                {
                    UpdateAvatarInfo(selectedDescriptor);
                }

                using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
                {
                    if (GUILayout.Button("Try Find", GUILayout.Width(60)))
                    {
                        if (TryGetAvatarDescriptorInParents(Selection.activeGameObject, out var foundDescriptor))
                        {
                            UpdateAvatarInfo(foundDescriptor);
                            EditorGUIUtility.PingObject(_avatar.Root);
                        }
                    }
                }

                using (new EditorGUI.DisabledScope(_avatar.Root == null))
                {
                    if (GUILayout.Button("Clear", GUILayout.Width(48)))
                    {
                        UpdateAvatarInfo(null);
                    }
                }
            }
        }

        private void DrawCoverSelector(float labelWidth)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!CanEditCover()))
                {
                    EditorGUILayout.LabelField("Cover", GUILayout.Width(labelWidth));
                    _coverImagePath = EditorGUILayout.TextField(_coverImagePath, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Pick", GUILayout.Width(48)))
                    {
                        var picked = EditorUtility.OpenFilePanel("Pick avatar cover", AvatarBuildCache.CacheDirectory, "png,jpg,jpeg");
                        if (!string.IsNullOrWhiteSpace(picked))
                        {
                            _coverImagePath = picked;
                        }
                    }

                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(_coverImagePath)))
                    {
                        if (GUILayout.Button("Clear", GUILayout.Width(48)))
                        {
                            _coverImagePath = "";
                        }
                    }
                }
            }
        }

        private void UpdateAvatarInfo(VRC_AvatarDescriptor avatarDescriptor)
        {
            if (avatarDescriptor == null)
            {
                ClearAvatarSelection();
                return;
            }

            _avatar.SelectDescriptor(avatarDescriptor);

            if (string.IsNullOrWhiteSpace(_newAvatarName))
                _newAvatarName = _avatar.Root.name;

            if (!_avatar.Root.TryGetComponent<PipelineManager>(out var pipelineManager))
            {
                MarkAvatarWithoutPipeline();
                return;
            }

            _avatar.SelectPipeline(pipelineManager);
            if (!_avatar.HasBlueprint)
            {
                MarkSelectedAvatarAsNew();
                return;
            }

            MarkSelectedAvatarAsExisting();
        }

        private void ClearAvatarSelection()
        {
            _avatar.Clear();
            _newAvatarName = null;
            ResetRemoteCheck();
            ClearInvalidCacheSelection();
        }

        private void MarkAvatarWithoutPipeline()
        {
            _avatar.MarkMissingPipeline();
            ResetRemoteCheck();
            ClearInvalidCacheSelection();
        }

        private void MarkSelectedAvatarAsNew()
        {
            _avatar.MarkNewAvatar();
            ResetRemoteCheck();
            ClearInvalidCacheSelection();
        }

        private void MarkSelectedAvatarAsExisting()
        {
            _avatar.MarkExistingAvatar();
            ClearInvalidCacheSelection();
            if (!string.Equals(_remoteCheck.BlueprintId, _avatar.BlueprintId, StringComparison.Ordinal))
            {
                RunRemoteAvatarCheck(_avatar.BlueprintId);
            }
        }

        private void ResetRemoteCheck()
        {
            _remoteCheck.Reset();
        }

        private void RunRemoteAvatarCheck(string blueprintId)
        {
            var serial = _remoteCheck.Begin(blueprintId);
            _avatar.IsNewAvatar = false;
            Repaint();

            _ = RunCheck();
            return;

            async Task RunCheck()
            {
                try
                {
                    var avatar = await VRCApi.GetAvatar(blueprintId, forceRefresh: true);

                    if (!IsCurrentRemoteCheck(blueprintId, serial)) return;
                    if (APIUser.CurrentUser == null)
                        throw new InvalidOperationException("VRChat user is not logged in.");
                    if (avatar.AuthorId != APIUser.CurrentUser.id)
                        throw new InvalidOperationException("Remote avatar belongs to a different user.");

                    _avatar.IsNewAvatar = avatar.PendingUpload;
                    _remoteCheck.Phase = RemoteAvatarCheckPhase.Ok;
                }
                catch (Exception ex)
                {
                    if (!IsCurrentRemoteCheck(blueprintId, serial)) return;

                    _avatar.IsNewAvatar = false;
                    _remoteCheck.Phase = RemoteAvatarCheckPhase.Failed;
                    _log.Info("Remote avatar check failed: " + ex.Message);
                }
                finally
                {
                    if (IsCurrentRemoteCheck(blueprintId, serial))
                    {
                        Repaint();
                    }
                }
            }
        }

        private void DrawCacheSection()
        {
            DrawTitle("Cache");

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                    DrawCacheListColumn();

                GUILayout.Space(1);
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Space(2);
                    DrawVerticalSeparator(166, Color.grey);
                }

                GUILayout.Space(1);
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(CacheActionsWidth)))
                    DrawCacheActionsColumn();
            }
        }

        private void DrawCacheListColumn()
        {
            var cacheEntries = GetCacheEntriesForSelectedAvatar();

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawSubtitle($"{cacheEntries.Count} / {_allCacheEntries.Count} bundles available");
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Open Folder", GUILayout.Width(88)))
                    OpenCacheFolder();
                if (GUILayout.Button("Refresh", GUILayout.Width(70)))
                    RefreshCacheEntries();
            }

            var listHeight = (EditorGUIUtility.singleLineHeight + 3f) * CacheVisibleRows + 6f;
            _cacheScroll = EditorGUILayout.BeginScrollView(_cacheScroll, GUILayout.Height(listHeight));
            if (cacheEntries.Count == 0)
            {
                EditorGUILayout.LabelField("No cache bundles for current avatar.", EditorStyles.miniLabel);
            }

            foreach (var entry in cacheEntries)
            {
                DrawCacheEntry(entry);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawCacheEntry(CachedAvatarBundleManifest entry)
        {
            var selected = IsSamePath(_selectedBundlePath, entry.bundlePath);
            var rowRect = EditorGUILayout.BeginHorizontal();
            if (Event.current.type == EventType.Repaint && selected)
            {
                EditorGUI.DrawRect(rowRect, SelectedCacheColour);
            }

            var label = $"{entry.platformLabel,-5}｜{entry.sizeLabel}｜{entry.createdAtLocal}";
            GUILayout.Label(label, GetCacheEntryLabelStyle(),
                GUILayout.Height(EditorGUIUtility.singleLineHeight),
                GUILayout.ExpandWidth(true));

            if (GUILayout.Button("Use", GetCacheEntryButtonStyle(), GUILayout.Width(36)))
            {
                _selectedBundlePath = entry.bundlePath;
            }

            if (GUILayout.Button("X", GetCacheEntryButtonStyle(), GUILayout.Width(16)))
            {
                DeleteCacheEntry(entry);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawCacheActionsColumn()
        {
            DrawSubtitle("Settings");
            _uploadAttempts = DrawIntSliderRow("Attempts", _uploadAttempts, 1, 20, 72f);
            _retryDelaySeconds = DrawSliderRow("Retry", _retryDelaySeconds, 1f, 60f, 72f);
            _concurrentWorkers = DrawIntSliderRow("Workers", _concurrentWorkers, 1, MaxWorkerProgressBars, 72f);
            _concurrentPartSizeMiB = DrawIntSliderRow("Part MiB", _concurrentPartSizeMiB, 8, 100, 72f);

            GUILayout.Space(6);
            var buttonGap = 4f;
            var buttonWidth = (CacheActionsWidth - buttonGap) / 2f;
            var buttonHeight = EditorGUIUtility.singleLineHeight * 1.1f;
            var tallButtonHeight = buttonHeight * 2f;

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!CanUploadCachedBundle()))
                {
                    if (GUILayout.Button("Upload\n(Concurrent)", GUILayout.Width(buttonWidth), GUILayout.Height(tallButtonHeight)))
                        RunConcurrentUploadCached();
                    if (GUILayout.Button("Upload\n(Official)", GUILayout.Width(buttonWidth), GUILayout.Height(tallButtonHeight)))
                        RunOfficialUploadCached();
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!IsAvatarInfoReady()))
                {
                    if (GUILayout.Button("Build & ⇧", GUILayout.Width(buttonWidth), GUILayout.Height(buttonHeight)))
                        RunBuildCacheAndUploadConcurrent();
                    if (GUILayout.Button("Build & ⇧", GUILayout.Width(buttonWidth), GUILayout.Height(buttonHeight)))
                        RunBuildCacheAndUploadOfficial();
                }
            }
        }

        private void DrawOperationSection()
        {
            DrawTitle("Operation");

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
                {
                    EditorGUILayout.LabelField("Status", _operation.StatusText);
                    var progressRect = EditorGUILayout.GetControlRect(false, 18);
                    EditorGUI.ProgressBar(progressRect, _operation.CurrentProgress,
                        $"{Mathf.RoundToInt(_operation.CurrentProgress * 100f)}%");
                    DrawWorkerProgressBars();
                }

                GUILayout.Space(8);
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(76)))
                {
                    GUILayout.Space(EditorGUIUtility.singleLineHeight + 2f);
                    using (new EditorGUI.DisabledScope(!_operation.Busy))
                    {
                        if (GUILayout.Button("Cancel", GUILayout.Height(28)))
                        {
                            _operation.Cancellation?.Cancel();
                            _log.Info("Cancellation requested.");
                        }
                    }
                }
            }

            DrawSubtitle("Log");
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.MinHeight(160));
            foreach (var line in _log.Lines)
            {
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawWorkerProgressBars()
        {
            for (var i = 0; i < _operation.ActiveWorkerProgressBars; i++)
            {
                var progressRect = EditorGUILayout.GetControlRect(false, 16);
                var status = string.IsNullOrWhiteSpace(_operation.WorkerStatusText[i])
                    ? $"Worker {i + 1}"
                    : $"Worker {i + 1}: {_operation.WorkerStatusText[i]}";
                EditorGUI.ProgressBar(progressRect, _operation.WorkerProgress[i], status);
            }
        }

        private void DeleteCacheEntry(CachedAvatarBundleManifest entry)
        {
            var bundleName = Path.GetFileName(entry.bundlePath);
            if (!EditorUtility.DisplayDialog("Delete Cached Bundle", $"Delete {bundleName}?", "Delete", "Cancel"))
            {
                return;
            }

            try
            {
                AvatarBuildCache.Delete(entry);
                if (IsSamePath(_selectedBundlePath, entry.bundlePath))
                {
                    _selectedBundlePath = "";
                }

                _log.Info("Deleted cached bundle: " + entry.bundlePath);
                RefreshCacheEntries();
            }
            catch (Exception ex)
            {
                _log.Info("Failed to delete cached bundle: " + ex.Message);
                Debug.LogException(ex);
            }
        }

        private void RunBuildCache() =>
            _ = RunExclusive("build-cache", BuildCacheProgressStages, 0, async token =>
            {
                var manifest = await CreateWorkflow().BuildCache(token);
                UpdateAvatarInfo(_avatar.Descriptor);
                _selectedBundlePath = manifest.bundlePath;
            });

        private void RunConcurrentUploadCached() =>
            _ = RunExclusive("concurrent-upload-cached", UploadProgressStages, GetConcurrentWorkerBars(),
                async token => { await CreateWorkflow().UploadCachedConcurrent(token); });

        private void RunOfficialUploadCached() =>
            _ = RunExclusive("official-upload-cached", UploadProgressStages, 0,
                async token => { await CreateWorkflow().UploadCachedOfficial(token); });

        private void RunBuildCacheAndUploadConcurrent() =>
            _ = RunExclusive("build-cache-concurrent-upload", BuildUploadProgressStages, GetConcurrentWorkerBars(),
                async token =>
                {
                    var manifest = await CreateWorkflow().BuildCacheAndUploadConcurrent(token);
                    UpdateAvatarInfo(_avatar.Descriptor);
                    _selectedBundlePath = manifest.bundlePath;
                });

        private void RunBuildCacheAndUploadOfficial() =>
            _ = RunExclusive("build-cache-official-upload", BuildUploadProgressStages, 0,
                async token =>
                {
                    var manifest = await CreateWorkflow().BuildCacheAndUploadOfficial(token);
                    UpdateAvatarInfo(_avatar.Descriptor);
                    _selectedBundlePath = manifest.bundlePath;
                });

        private async Task RunExclusive(
            string operationName,
            AvatarUploadProgressStage[] progressStages,
            int workerBars,
            Func<CancellationToken, Task> operation)
        {
            if (_operation.Busy)
            {
                _log.Info("Another operation is already running.");
                return;
            }

            _operation.Busy = true;
            _operation.Begin(progressStages, workerBars);
            _operation.Cancellation = new CancellationTokenSource();
            _log.Begin(operationName);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await operation(_operation.Cancellation.Token);
                SetProgress("Done", 1f);
            }
            catch (OperationCanceledException)
            {
                _log.Info("Operation cancelled.");
                SetProgress("Cancelled", 0f);
            }
            catch (Exception ex)
            {
                _log.Info("Failed: " + FormatException(ex));
                Debug.LogException(ex);
                SetProgress("Failed", 0f);
            }
            finally
            {
                watch.Stop();
                _log.Info($"Operation finished after {watch.Elapsed.TotalSeconds:F2}s.");
                _log.End();
                _operation.Cancellation.Dispose();
                _operation.Cancellation = null;
                _operation.Busy = false;
                EditorUtility.ClearProgressBar();
                RefreshCacheEntries();
                Repaint();
            }
        }

        private AvatarUploadWorkflow CreateWorkflow()
        {
            return new AvatarUploadWorkflow(new AvatarUploadRequest
            {
                AvatarRoot = _avatar.Root,
                NewAvatarName = _newAvatarName,
                NewAvatarReleaseStatus = _newAvatarReleaseStatus,
                CoverImagePath = _coverImagePath,
                CachedBundlePath = _selectedBundlePath,
                UploadAttempts = _uploadAttempts,
                RetryDelaySeconds = _retryDelaySeconds,
                ConcurrentWorkers = _concurrentWorkers,
                ConcurrentPartSizeMiB = _concurrentPartSizeMiB
            }, _log.Info, SetProgressSafe, SetWorkerProgressSafe);
        }

        private void RefreshCacheEntries()
        {
            _allCacheEntries = AvatarBuildCache.ListRecent();
            ClearInvalidCacheSelection();
            Repaint();
        }

        private void SetProgressSafe(AvatarUploadProgressStage stage, string status, float percentage)
        {
            _operation.SetStageProgress(stage, status, percentage);
            EditorApplication.delayCall += () =>
            {
                if (this == null || !_operation.Busy) return;

                EditorUtility.DisplayProgressBar(WindowTitle, _operation.StatusText, _operation.CurrentProgress);
                Repaint();
            };
        }

        private void SetWorkerProgressSafe(int workerIndex, string status, float percentage)
        {
            if (!_operation.SetWorkerProgress(workerIndex, status, percentage)) return;

            EditorApplication.delayCall += () =>
            {
                if (this == null || !_operation.Busy) return;

                Repaint();
            };
        }

        private void SetProgress(string status, float percentage)
        {
            _operation.SetProgress(status, percentage);
            EditorUtility.DisplayProgressBar(WindowTitle, _operation.StatusText, _operation.CurrentProgress);
            Repaint();
        }

        private int GetConcurrentWorkerBars() => _concurrentWorkers > 1
            ? Mathf.Clamp(_concurrentWorkers, 0, MaxWorkerProgressBars)
            : 0;

        private List<CachedAvatarBundleManifest> GetCacheEntriesForSelectedAvatar()
        {
            if (string.IsNullOrWhiteSpace(_avatar.BlueprintId))
            {
                return new List<CachedAvatarBundleManifest>();
            }

            return _allCacheEntries
                .Where(entry => string.Equals(entry.avatarId, _avatar.BlueprintId, StringComparison.Ordinal))
                .ToList();
        }

        private void ClearInvalidCacheSelection()
        {
            if (!GetCacheEntriesForSelectedAvatar().Any(entry => IsSamePath(entry.bundlePath, _selectedBundlePath)))
            {
                _selectedBundlePath = "";
            }
        }

        private static void OpenCacheFolder()
        {
            Directory.CreateDirectory(AvatarBuildCache.CacheDirectory);
            EditorUtility.RevealInFinder(AvatarBuildCache.CacheDirectory);
        }

        // semantic status
        // --------------------------------
        private bool IsBundlePathSelected() => !string.IsNullOrWhiteSpace(_selectedBundlePath);
        private bool CanEditCover() => _avatar.HasPipeline && _remoteCheck.IsReady && _avatar.IsNewAvatar;

        private bool CanBuildCache() =>
            IsAvatarInfoReady()
            && _avatar.HasBlueprint
            && !_avatar.IsNewAvatar;

        private bool IsAvatarInfoReady() =>
            _avatar.HasPipeline
            && _remoteCheck.IsReady
            && (!_avatar.IsNewAvatar || (!string.IsNullOrWhiteSpace(_newAvatarName) && !string.IsNullOrWhiteSpace(_coverImagePath)));

        private bool CanUploadCachedBundle() =>
            IsAvatarInfoReady()
            && _remoteCheck.Phase != RemoteAvatarCheckPhase.NotNeeded
            && IsBundlePathSelected();

        private bool IsCurrentRemoteCheck(string blueprintId, int serial) =>
            _remoteCheck.IsCurrent(blueprintId, serial);

        private bool CanRetryRemoteCheck() =>
            _remoteCheck.CanRetry;

        // function helper
        // --------------------------------
        private static bool TryGetAvatarDescriptorInParents(GameObject obj, out VRC_AvatarDescriptor avatarDescriptor)
        {
            avatarDescriptor = obj.GetComponentInParent<VRC_AvatarDescriptor>(true);
            return avatarDescriptor != null;
        }

        private static bool IsSamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string FormatException(Exception ex)
        {
            return ex is ApiErrorException apiError
                ? $"VRChat API error {apiError.StatusCode}: {apiError.ErrorMessage}"
                : ex.Message;
        }

        // UI helper
        // --------------------------------
        private static float CalculateLabelWidth(params string[] labels)
        {
            var width = labels.Aggregate(0f,
                (current, label) => Mathf.Max(current, EditorStyles.label.CalcSize(new GUIContent(label)).x));

            return Mathf.Ceil(width + 8f);
        }

        private static void DrawSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, 5);
            rect.height = 1;
            rect.y += 1;
            rect.x -= 2;
            rect.width += 6;
            EditorGUI.DrawRect(rect, Color.grey);
        }

        private static void DrawVerticalSeparator(float height, Color colour)
        {
            var rect = GUILayoutUtility.GetRect(1f, height, GUILayout.Width(1f), GUILayout.Height(height));
            EditorGUI.DrawRect(rect, colour);
        }

        private static void DrawTitle(string title, float spacePixels = 8)
        {
            GUILayout.Space(spacePixels);
            DrawBoldColoredLabel("# " + title, TitleColour);
            DrawSeparator();
        }

        private static void DrawSubtitle(string title)
        {
            DrawBoldColoredLabel(title, SubtitleColour);
        }

        private static void DrawBoldColoredLabel(string label, Color colour)
        {
            var style = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = colour }
            };

            EditorGUILayout.LabelField(label, style, GUILayout.MinWidth(32), GUILayout.MaxWidth(500), GUILayout.ExpandWidth(true));
        }

        private static GUIStyle GetCacheEntryLabelStyle()
        {
            if (_cacheEntryLabelStyle != null) return _cacheEntryLabelStyle;

            _cacheEntryLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                font = Font.CreateDynamicFontFromOSFont(
                    new[] { "Consolas", "Courier New", "Menlo", "Monaco" },
                    12),
                fontSize = 12,
                alignment = TextAnchor.MiddleLeft
            };
            return _cacheEntryLabelStyle;
        }

        private static GUIStyle GetCacheEntryButtonStyle()
        {
            if (_cacheEntryButtonStyle != null) return _cacheEntryButtonStyle;

            _cacheEntryButtonStyle = new GUIStyle(GUI.skin.button)
            {
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(3, 1, 2, 2)
            };
            return _cacheEntryButtonStyle;
        }

        private static string DrawTextRow(string label, string value, float labelWidth = LabelWidth)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(labelWidth));
                return EditorGUILayout.TextField(value, GUILayout.ExpandWidth(true));
            }
        }

        private static int DrawIntSliderRow(string label, int value, int leftValue, int rightValue,
            float labelWidth = LabelWidth)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(labelWidth));
                return EditorGUILayout.IntSlider(value, leftValue, rightValue);
            }
        }

        private static float DrawSliderRow(string label, float value, float leftValue, float rightValue,
            float labelWidth = LabelWidth)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(labelWidth));
                return EditorGUILayout.Slider(value, leftValue, rightValue);
            }
        }
    }
}
