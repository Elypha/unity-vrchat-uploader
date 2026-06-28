using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Elypha.VRChatUploader
{
    [MovedFrom(true, "Elypha.VRChatUploader", null, "ElyphaVRChatUploader")]
    public sealed class VRChatUploader : EditorWindow
    {
        private const string WindowTitle = "VRChatUploader";
        private const string MenuPath = "Elypha/VRChatUploader";
        private const int DefaultAttempts = 5;
        private const float DefaultRetryDelaySeconds = 5f;
        private const float LabelWidth = 155f;
        private const float CompactLabelWidth = 112f;

        private static readonly string[] ReleaseStatusOptions = {"private", "public"};
        private static readonly Color TitleColour = new(230f / 255f, 194f / 255f, 153f / 255f);
        private static readonly Color SubtitleColour = new(210f / 255f, 210f / 255f, 210f / 255f);
        private static readonly Color SelectedCacheColour = new(102f / 255f, 153f / 255f, 255f / 255f);

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

        private readonly VRChatUploaderLog log = new();
        private List<CachedAvatarBundleManifest> cacheEntries = new();
        private CancellationTokenSource cancellation;
        private Vector2 mainScroll;
        private Vector2 logScroll;
        private Vector2 cacheScroll;
        private bool busy;
        private string currentStatus = "Idle";
        private float currentProgress;

        [MenuItem(MenuPath, false, 1)]
        public static void Open()
        {
            var window = GetWindow<VRChatUploader>(WindowTitle);
            window.minSize = new Vector2(520, 520);
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
            mainScroll = EditorGUILayout.BeginScrollView(mainScroll, false, false, GUILayout.ExpandWidth(true));

            using (new EditorGUI.DisabledScope(busy))
            {
                DrawAvatarSection();
                DrawBuildCacheSection();
                DrawCacheManagementSection();
                DrawUploadSection();
                DrawCombinedOperationsSection();
            }

            DrawOperationSection();
            EditorGUILayout.EndScrollView();
        }

        private void DrawAvatarSection()
        {
            DrawTitle("Avatar");

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Avatar Root", GUILayout.Width(LabelWidth));
                avatarRoot = (GameObject)EditorGUILayout.ObjectField(avatarRoot, typeof(GameObject), true);
                if (GUILayout.Button("Use Selection", GUILayout.Width(120)))
                {
                    avatarRoot = Selection.activeGameObject;
                    if (avatarRoot != null && string.IsNullOrWhiteSpace(newAvatarName))
                    {
                        newAvatarName = avatarRoot.name;
                    }
                }
            }

            DrawSubtitle("Metadata");
            newAvatarName = DrawTextRow("Name", string.IsNullOrWhiteSpace(newAvatarName) && avatarRoot != null ? avatarRoot.name : newAvatarName);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Description", GUILayout.Width(LabelWidth));
                newAvatarDescription = EditorGUILayout.TextArea(newAvatarDescription ?? "", GUILayout.MinHeight(42));
            }

            var releaseIndex = Array.IndexOf(ReleaseStatusOptions, newAvatarReleaseStatus);
            if (releaseIndex < 0)
            {
                releaseIndex = 0;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Release Status", GUILayout.Width(LabelWidth));
                releaseIndex = EditorGUILayout.Popup(releaseIndex, ReleaseStatusOptions);
                newAvatarReleaseStatus = ReleaseStatusOptions[releaseIndex];
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Cover", GUILayout.Width(LabelWidth));
                coverImagePath = EditorGUILayout.TextField(coverImagePath);
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

        private void DrawBuildCacheSection()
        {
            DrawTitle("Build Cache");

            if (GUILayout.Button("Build Cache"))
            {
                _ = RunExclusive("build-cache", async token =>
                {
                    var manifest = await CreateWorkflow().BuildCache(token);
                    selectedBundlePath = manifest.bundlePath;
                });
            }
        }

        private void DrawCacheManagementSection()
        {
            DrawTitle("Cache Management");

            EditorGUILayout.LabelField("Cache Folder", AvatarBuildCache.CacheDirectory);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Selected Bundle", GUILayout.Width(LabelWidth));
                selectedBundlePath = EditorGUILayout.TextField(selectedBundlePath);
                if (GUILayout.Button("Pick", GUILayout.Width(56)))
                {
                    var picked = EditorUtility.OpenFilePanel("Pick cached avatar bundle", AvatarBuildCache.CacheDirectory, "vrca");
                    if (!string.IsNullOrWhiteSpace(picked))
                    {
                        selectedBundlePath = picked;
                    }
                }

                if (GUILayout.Button("Clear", GUILayout.Width(56)))
                {
                    selectedBundlePath = "";
                }
            }

            if (GUILayout.Button("Refresh Cache"))
            {
                RefreshCacheEntries();
            }

            DrawSubtitle("Cached Bundles");
            cacheScroll = EditorGUILayout.BeginScrollView(cacheScroll, GUILayout.MinHeight(96), GUILayout.MaxHeight(170));
            if (cacheEntries.Count == 0)
            {
                EditorGUILayout.LabelField("No cached bundles found.", EditorStyles.miniLabel);
            }

            foreach (var entry in cacheEntries)
            {
                DrawCacheEntry(entry);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawCacheEntry(CachedAvatarBundleManifest entry)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var selected = IsSamePath(selectedBundlePath, entry.bundlePath);
                var originalColour = GUI.color;
                if (selected)
                {
                    GUI.color = SelectedCacheColour;
                }

                var label = $"{entry.avatarName} | {entry.platform} | {AvatarFileUtil.FormatBytes(entry.sizeBytes)} | {entry.createdAtLocal}";
                GUILayout.Label(label, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
                GUI.color = originalColour;

                if (GUILayout.Button("Select", GUILayout.Width(56)))
                {
                    selectedBundlePath = entry.bundlePath;
                }

                if (GUILayout.Button("Delete", GUILayout.Width(56)))
                {
                    DeleteCacheEntry(entry);
                }
            }
        }

        private void DrawUploadSection()
        {
            DrawTitle("Upload");

            DrawSubtitle("Shared Settings");
            uploadAttempts = DrawIntSliderRow("Upload Attempts", uploadAttempts, 1, 20);
            retryDelaySeconds = DrawSliderRow("Retry Delay", retryDelaySeconds, 1f, 60f);

            DrawSubtitle("Upload Path");
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(240)))
                {
                    DrawSubtitle("Concurrent Path");
                    concurrentWorkers = DrawIntSliderRow("Workers", concurrentWorkers, 1, 8, CompactLabelWidth);
                    concurrentPartSizeMiB = DrawIntSliderRow("Part Size MiB", concurrentPartSizeMiB, 8, 100, CompactLabelWidth);

                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(selectedBundlePath)))
                    {
                        if (GUILayout.Button("Upload Cache"))
                        {
                            _ = RunExclusive("concurrent-upload-cached", async token =>
                            {
                                await CreateWorkflow().UploadCachedConcurrent(token);
                            });
                        }
                    }
                }

                GUILayout.Space(10);

                using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(220)))
                {
                    DrawSubtitle("Official SDK Path");
                    DrawReadOnlyRow("Path Settings", "SDK default", CompactLabelWidth);

                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(selectedBundlePath)))
                    {
                        if (GUILayout.Button("Upload Cache"))
                        {
                            _ = RunExclusive("official-upload-cached", async token =>
                            {
                                await CreateWorkflow().UploadCachedOfficial(token);
                            });
                        }
                    }
                }
            }
        }

        private void DrawCombinedOperationsSection()
        {
            DrawTitle("Combined Operations");

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Build + Concurrent Upload"))
                {
                    _ = RunExclusive("build-cache-concurrent-upload", async token =>
                    {
                        var manifest = await CreateWorkflow().BuildCacheAndUploadConcurrent(token);
                        selectedBundlePath = manifest.bundlePath;
                    });
                }

                if (GUILayout.Button("Build + Official SDK Upload"))
                {
                    _ = RunExclusive("build-cache-official-upload", async token =>
                    {
                        var manifest = await CreateWorkflow().BuildCacheAndUploadOfficial(token);
                        selectedBundlePath = manifest.bundlePath;
                    });
                }
            }
        }

        private void DrawOperationSection()
        {
            DrawTitle("Operation");

            using (new EditorGUI.DisabledScope(!busy))
            {
                if (GUILayout.Button("Cancel Current Operation"))
                {
                    cancellation?.Cancel();
                    log.Info("Cancellation requested.");
                }
            }

            EditorGUILayout.LabelField("Status", currentStatus);
            var progressRect = EditorGUILayout.GetControlRect(false, 18);
            EditorGUI.ProgressBar(progressRect, currentProgress, $"{Mathf.RoundToInt(currentProgress * 100f)}%");

            DrawSubtitle("Log");
            logScroll = EditorGUILayout.BeginScrollView(logScroll, GUILayout.MinHeight(160));
            foreach (var line in log.Lines)
            {
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedLabel);
            }

            EditorGUILayout.EndScrollView();
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
                if (IsSamePath(selectedBundlePath, entry.bundlePath))
                {
                    selectedBundlePath = "";
                }

                log.Info("Deleted cached bundle: " + entry.bundlePath);
                RefreshCacheEntries();
            }
            catch (Exception ex)
            {
                log.Info("Failed to delete cached bundle: " + ex.Message);
                Debug.LogException(ex);
            }
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

        private static string DrawTextRow(string label, string value, float labelWidth = LabelWidth)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(labelWidth));
                return EditorGUILayout.TextField(value);
            }
        }

        private static int DrawIntSliderRow(string label, int value, int leftValue, int rightValue, float labelWidth = LabelWidth)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(labelWidth));
                return EditorGUILayout.IntSlider(value, leftValue, rightValue);
            }
        }

        private static float DrawSliderRow(string label, float value, float leftValue, float rightValue, float labelWidth = LabelWidth)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(labelWidth));
                return EditorGUILayout.Slider(value, leftValue, rightValue);
            }
        }

        private static void DrawReadOnlyRow(string label, string value, float labelWidth = LabelWidth)
        {
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(labelWidth));
                EditorGUILayout.TextField(value);
            }
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
                normal = {textColor = colour}
            };

            EditorGUILayout.LabelField(label, style, GUILayout.MinWidth(32), GUILayout.MaxWidth(500), GUILayout.ExpandWidth(true));
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

        private static bool IsSamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
