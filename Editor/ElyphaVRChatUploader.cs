using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Elypha.VRChatUploader
{
    public sealed class ElyphaVRChatUploader : EditorWindow
    {
        private const string WindowTitle = "VRChatUploader";
        private const string MenuPath = "Elypha/VRChatUploader";
        private const int DefaultAttempts = 5;
        private const float DefaultRetryDelaySeconds = 5f;

        private static readonly string[] ReleaseStatusOptions = {"private", "public"};

        [SerializeField] private GameObject avatarRoot;
        [SerializeField] private string newAvatarName;
        [SerializeField] private string newAvatarDescription = "";
        [SerializeField] private string newAvatarReleaseStatus = "private";
        [SerializeField] private string coverImagePath;
        [SerializeField] private string selectedBundlePath;
        [SerializeField] private int uploadAttempts = DefaultAttempts;
        [SerializeField] private float retryDelaySeconds = DefaultRetryDelaySeconds;
        [SerializeField] private int concurrentWorkers = AvatarUploadProtocol.DefaultWorkers;
        [SerializeField] private int concurrentPartSizeMiB = AvatarUploadProtocol.DefaultPartSizeMiB;
        [SerializeField] private bool showAdvanced;

        private readonly VRChatUploaderLog log = new();
        private List<CachedAvatarBundleManifest> cacheEntries = new();
        private CancellationTokenSource cancellation;
        private Vector2 logScroll;
        private Vector2 cacheScroll;
        private bool busy;
        private string currentStatus = "Idle";
        private float currentProgress;

        [MenuItem(MenuPath, false, 1)]
        public static void Open()
        {
            GetWindow<ElyphaVRChatUploader>(WindowTitle);
        }

        private void OnEnable()
        {
            if (avatarRoot == null && Selection.activeGameObject != null)
            {
                avatarRoot = Selection.activeGameObject;
            }

            RefreshCacheEntries();
        }

        private void OnDisable()
        {
            cancellation?.Cancel();
            EditorUtility.ClearProgressBar();
        }

        private void OnGUI()
        {
            using (new EditorGUI.DisabledScope(busy))
            {
                DrawAvatarSection();
                DrawCacheSection();
                DrawActions();
                DrawAdvanced();
            }

            using (new EditorGUI.DisabledScope(!busy))
            {
                if (GUILayout.Button("Cancel Current Operation"))
                {
                    cancellation?.Cancel();
                    log.Info("Cancellation requested.");
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Status", currentStatus);
            EditorGUILayout.Slider(currentProgress, 0f, 1f);

            DrawLog();
        }

        private void DrawAvatarSection()
        {
            EditorGUILayout.LabelField("Avatar", EditorStyles.boldLabel);
            avatarRoot = (GameObject)EditorGUILayout.ObjectField("Avatar Root", avatarRoot, typeof(GameObject), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Use Selection", GUILayout.Width(120)))
                {
                    avatarRoot = Selection.activeGameObject;
                    if (avatarRoot != null && string.IsNullOrWhiteSpace(newAvatarName))
                    {
                        newAvatarName = avatarRoot.name;
                    }
                }

                if (GUILayout.Button("Refresh Cache", GUILayout.Width(120)))
                {
                    RefreshCacheEntries();
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("New or Pending Avatar Fields", EditorStyles.boldLabel);
            newAvatarName = EditorGUILayout.TextField("Name", string.IsNullOrWhiteSpace(newAvatarName) && avatarRoot != null ? avatarRoot.name : newAvatarName);
            EditorGUILayout.LabelField("Description");
            newAvatarDescription = EditorGUILayout.TextArea(newAvatarDescription ?? "", GUILayout.MinHeight(38));

            var releaseIndex = Array.IndexOf(ReleaseStatusOptions, newAvatarReleaseStatus);
            if (releaseIndex < 0)
            {
                releaseIndex = 0;
            }

            releaseIndex = EditorGUILayout.Popup("Release Status", releaseIndex, ReleaseStatusOptions);
            newAvatarReleaseStatus = ReleaseStatusOptions[releaseIndex];

            using (new EditorGUILayout.HorizontalScope())
            {
                coverImagePath = EditorGUILayout.TextField("Cover", coverImagePath);
                if (GUILayout.Button("Pick", GUILayout.Width(56)))
                {
                    var picked = EditorUtility.OpenFilePanel("Pick avatar cover", AvatarBuildCache.CacheDirectory, "png,jpg,jpeg");
                    if (!string.IsNullOrWhiteSpace(picked))
                    {
                        coverImagePath = picked;
                    }
                }

                if (GUILayout.Button("Clear", GUILayout.Width(56)))
                {
                    coverImagePath = "";
                }
            }
        }

        private void DrawCacheSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Cached Bundle", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                selectedBundlePath = EditorGUILayout.TextField(selectedBundlePath);
                if (GUILayout.Button("Pick", GUILayout.Width(56)))
                {
                    var picked = EditorUtility.OpenFilePanel("Pick cached avatar bundle", AvatarBuildCache.CacheDirectory, "vrca");
                    if (!string.IsNullOrWhiteSpace(picked))
                    {
                        selectedBundlePath = picked;
                    }
                }
            }

            cacheScroll = EditorGUILayout.BeginScrollView(cacheScroll, GUILayout.MinHeight(90), GUILayout.MaxHeight(150));
            foreach (var entry in cacheEntries)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var label = $"{entry.avatarName} | {entry.platform} | {AvatarFileUtil.FormatBytes(entry.sizeBytes)} | {entry.createdAtLocal}";
                    if (GUILayout.Button(label, EditorStyles.miniButtonLeft))
                    {
                        selectedBundlePath = entry.bundlePath;
                    }

                    if (GUILayout.Button("Use", GUILayout.Width(42)))
                    {
                        selectedBundlePath = entry.bundlePath;
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawActions()
        {
            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Build Cache"))
                {
                    _ = RunExclusive("build-cache", async token =>
                    {
                        var manifest = await CreateWorkflow().BuildCache(token);
                        selectedBundlePath = manifest.bundlePath;
                    });
                }

                if (GUILayout.Button("Upload Cached"))
                {
                    _ = RunExclusive("concurrent-upload-cached", async token =>
                    {
                        await CreateWorkflow().UploadCachedConcurrent(token);
                    });
                }

                if (GUILayout.Button("Build Cache + Upload"))
                {
                    _ = RunExclusive("build-cache-concurrent-upload", async token =>
                    {
                        var manifest = await CreateWorkflow().BuildCacheAndUploadConcurrent(token);
                        selectedBundlePath = manifest.bundlePath;
                    });
                }
            }
        }

        private void DrawAdvanced()
        {
            EditorGUILayout.Space(6);
            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced");
            if (!showAdvanced)
            {
                return;
            }

            uploadAttempts = EditorGUILayout.IntSlider("Upload Attempts", uploadAttempts, 1, 20);
            retryDelaySeconds = EditorGUILayout.Slider("Retry Delay", retryDelaySeconds, 1f, 60f);
            concurrentWorkers = EditorGUILayout.IntSlider("Concurrent Workers", concurrentWorkers, 1, 8);
            concurrentPartSizeMiB = EditorGUILayout.IntSlider("Part Size MiB", concurrentPartSizeMiB, 8, 100);
            EditorGUILayout.LabelField("Cache", AvatarBuildCache.CacheDirectory);

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(selectedBundlePath)))
            {
                if (GUILayout.Button("Official SDK Upload Cached"))
                {
                    _ = RunExclusive("official-upload-cached", async token =>
                    {
                        await CreateWorkflow().UploadCachedOfficial(token);
                    });
                }
            }
        }

        private void DrawLog()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            logScroll = EditorGUILayout.BeginScrollView(logScroll, GUILayout.MinHeight(180));
            foreach (var line in log.Lines)
            {
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedLabel);
            }

            EditorGUILayout.EndScrollView();
        }

        private async Task RunExclusive(string operationName, Func<CancellationToken, Task> operation)
        {
            if (busy)
            {
                log.Info("Another operation is already running.");
                return;
            }

            busy = true;
            currentProgress = 0f;
            currentStatus = "Starting";
            cancellation = new CancellationTokenSource();
            log.Begin(operationName);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await operation(cancellation.Token);
                SetProgress("Done", 1f);
            }
            catch (OperationCanceledException)
            {
                log.Info("Operation cancelled.");
                SetProgress("Cancelled", 0f);
            }
            catch (Exception ex)
            {
                log.Info("Failed: " + ex.Message);
                Debug.LogException(ex);
                SetProgress("Failed", 0f);
            }
            finally
            {
                watch.Stop();
                log.Info($"Operation finished after {watch.Elapsed.TotalSeconds:F2}s.");
                log.End();
                cancellation.Dispose();
                cancellation = null;
                busy = false;
                EditorUtility.ClearProgressBar();
                RefreshCacheEntries();
                Repaint();
            }
        }

        private AvatarUploadWorkflow CreateWorkflow()
        {
            return new AvatarUploadWorkflow(new AvatarUploadRequest
            {
                AvatarRoot = avatarRoot,
                NewAvatarName = newAvatarName,
                NewAvatarDescription = newAvatarDescription,
                NewAvatarReleaseStatus = newAvatarReleaseStatus,
                CoverImagePath = coverImagePath,
                CachedBundlePath = selectedBundlePath,
                UploadAttempts = uploadAttempts,
                RetryDelaySeconds = retryDelaySeconds,
                ConcurrentWorkers = concurrentWorkers,
                ConcurrentPartSizeMiB = concurrentPartSizeMiB
            }, log.Info, SetProgressSafe);
        }

        private void RefreshCacheEntries()
        {
            cacheEntries = AvatarBuildCache.ListRecent();
            Repaint();
        }

        private void SetProgressSafe(string status, float percentage)
        {
            currentStatus = status;
            currentProgress = Mathf.Clamp01(percentage);
            EditorApplication.delayCall += () =>
            {
                if (this == null || !busy)
                {
                    return;
                }

                EditorUtility.DisplayProgressBar(WindowTitle, currentStatus, currentProgress);
                Repaint();
            };
        }

        private void SetProgress(string status, float percentage)
        {
            currentStatus = status;
            currentProgress = Mathf.Clamp01(percentage);
            EditorUtility.DisplayProgressBar(WindowTitle, currentStatus, currentProgress);
            Repaint();
        }
    }
}
