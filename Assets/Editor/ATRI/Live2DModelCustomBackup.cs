using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

public sealed class Live2DModelCustomBackupAsset : ScriptableObject
{
    public string sourcePrefabGuid;
    public string sourcePrefabPath;
    public string createdAt;
    public GameObject backupPrefab;
    public List<Live2DModelCustomSubtreeRecord> customSubtrees = new List<Live2DModelCustomSubtreeRecord>();
    public List<Live2DModelCustomComponentRecord> customComponents = new List<Live2DModelCustomComponentRecord>();
}

[Serializable]
public sealed class Live2DModelCustomSubtreeRecord
{
    public string originalPath;
    public string parentPath;
    public string backupPath;
}

[Serializable]
public sealed class Live2DModelCustomComponentRecord
{
    public string ownerPath;
    public string holderPath;
    public MonoScript script;
}

public static class Live2DModelCustomBackup
{
    private const string DefaultLive2DPrefabPath = "Assets/model/atri/model_data/atri_8.prefab";
    private const string BackupDirectory = "Assets/Editor/ATRI/Live2DModelCustomBackups";
    private const string ExistingComponentRootName = "__existing_components";

    [MenuItem("Tools/ATRI/Live2D/Backup Selected Model Custom Components")]
    public static void BackupSelectedModel()
    {
        var prefabPath = GetSelectedOrDefaultPrefabPath();
        if (string.IsNullOrEmpty(prefabPath))
        {
            EditorUtility.DisplayDialog("Live2D Custom Backup", "Select a Live2D model prefab, or make sure the default model exists.", "OK");
            return;
        }

        Backup(prefabPath);
    }

    [MenuItem("Tools/ATRI/Live2D/Restore Selected Model Custom Components")]
    public static void RestoreSelectedModel()
    {
        var prefabPath = GetSelectedOrDefaultPrefabPath();
        if (string.IsNullOrEmpty(prefabPath))
        {
            EditorUtility.DisplayDialog("Live2D Custom Backup", "Select a Live2D model prefab, or make sure the default model exists.", "OK");
            return;
        }

        Restore(prefabPath);
    }

    [MenuItem("Tools/ATRI/Live2D/Backup ATRI 8 Custom Components")]
    public static void BackupDefaultModel()
    {
        Backup(DefaultLive2DPrefabPath);
    }

    [MenuItem("Tools/ATRI/Live2D/Restore ATRI 8 Custom Components")]
    public static void RestoreDefaultModel()
    {
        Restore(DefaultLive2DPrefabPath);
    }

    private static void Backup(string prefabPath)
    {
        EnsureBackupDirectory();

        var sourceRoot = PrefabUtility.LoadPrefabContents(prefabPath);
        try
        {
            var backupRoot = new GameObject(Path.GetFileNameWithoutExtension(prefabPath) + "_custom_backup");
            var backupAsset = ScriptableObject.CreateInstance<Live2DModelCustomBackupAsset>();
            backupAsset.sourcePrefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
            backupAsset.sourcePrefabPath = prefabPath;
            backupAsset.createdAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var customSubtreeRoots = FindTopLevelCustomSubtrees(sourceRoot.transform);
            foreach (var subtreeRoot in customSubtreeRoots)
            {
                var copy = UnityEngine.Object.Instantiate(subtreeRoot.gameObject);
                copy.name = subtreeRoot.name;
                copy.transform.SetParent(backupRoot.transform, false);

                var originalPath = GetPath(sourceRoot.transform, subtreeRoot);
                backupAsset.customSubtrees.Add(new Live2DModelCustomSubtreeRecord
                {
                    originalPath = originalPath,
                    parentPath = GetParentPath(originalPath),
                    backupPath = copy.name
                });
            }

            var componentHolderRoot = new GameObject(ExistingComponentRootName);
            componentHolderRoot.transform.SetParent(backupRoot.transform, false);

            foreach (var transform in GetAllTransforms(sourceRoot.transform))
            {
                if (IsInsideAny(transform, customSubtreeRoots))
                {
                    continue;
                }

                foreach (var component in transform.GetComponents<Component>())
                {
                    var script = GetProjectScript(component);
                    if (script == null)
                    {
                        continue;
                    }

                    var holder = UnityEngine.Object.Instantiate(transform.gameObject);
                    holder.name = MakeSafeName(GetPath(sourceRoot.transform, transform) + "_" + script.name);
                    holder.transform.SetParent(componentHolderRoot.transform, false);
                    RemoveChildren(holder.transform);

                    backupAsset.customComponents.Add(new Live2DModelCustomComponentRecord
                    {
                        ownerPath = GetPath(sourceRoot.transform, transform),
                        holderPath = ExistingComponentRootName + "/" + holder.name,
                        script = script
                    });
                }
            }

            if (backupAsset.customSubtrees.Count == 0 && backupAsset.customComponents.Count == 0)
            {
                UnityEngine.Object.DestroyImmediate(backupRoot);
                UnityEngine.Object.DestroyImmediate(backupAsset);
                EditorUtility.DisplayDialog("Live2D Custom Backup", "No project custom components were found on this Live2D prefab.", "OK");
                return;
            }

            var baseName = Path.GetFileNameWithoutExtension(prefabPath) + "_custom_components";
            var backupPrefabPath = BackupDirectory + "/" + baseName + ".prefab";
            var backupAssetPath = BackupDirectory + "/" + baseName + ".asset";

            if (File.Exists(backupAssetPath))
            {
                AssetDatabase.DeleteAsset(backupAssetPath);
            }

            backupAsset.backupPrefab = PrefabUtility.SaveAsPrefabAsset(backupRoot, backupPrefabPath);
            AssetDatabase.CreateAsset(backupAsset, backupAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            UnityEngine.Object.DestroyImmediate(backupRoot);

            Debug.Log("Live2D custom backup saved: " + backupAssetPath + " (subtrees: " + backupAsset.customSubtrees.Count + ", components: " + backupAsset.customComponents.Count + ")");
            EditorUtility.DisplayDialog("Live2D Custom Backup", "Backup completed.\n\n" + backupAssetPath, "OK");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(sourceRoot);
        }
    }

    private static void Restore(string prefabPath)
    {
        var backupAsset = FindBackupAsset(prefabPath);
        if (backupAsset == null || backupAsset.backupPrefab == null)
        {
            EditorUtility.DisplayDialog("Live2D Custom Backup", "No backup was found for this model. Run Backup first.", "OK");
            return;
        }

        var targetRoot = PrefabUtility.LoadPrefabContents(prefabPath);
        var backupPath = AssetDatabase.GetAssetPath(backupAsset.backupPrefab);
        var backupRoot = PrefabUtility.LoadPrefabContents(backupPath);
        try
        {
            foreach (var subtreeRecord in backupAsset.customSubtrees)
            {
                var existing = FindByPath(targetRoot.transform, subtreeRecord.originalPath);
                if (existing != null)
                {
                    UnityEngine.Object.DestroyImmediate(existing.gameObject);
                }

                var parent = FindByPath(targetRoot.transform, subtreeRecord.parentPath);
                var source = FindByPath(backupRoot.transform, subtreeRecord.backupPath);
                if (parent == null || source == null)
                {
                    Debug.LogWarning("Live2D custom subtree restore skipped: " + subtreeRecord.originalPath);
                    continue;
                }

                var copy = UnityEngine.Object.Instantiate(source.gameObject);
                copy.name = source.name;
                copy.transform.SetParent(parent, false);
            }

            foreach (var componentRecord in backupAsset.customComponents)
            {
                var owner = FindByPath(targetRoot.transform, componentRecord.ownerPath);
                var holder = FindByPath(backupRoot.transform, componentRecord.holderPath);
                if (owner == null || holder == null || componentRecord.script == null)
                {
                    Debug.LogWarning("Live2D custom component restore skipped: " + componentRecord.ownerPath);
                    continue;
                }

                RemoveComponentsWithScript(owner.gameObject, componentRecord.script);

                foreach (var sourceComponent in holder.GetComponents<Component>())
                {
                    if (GetProjectScript(sourceComponent) != componentRecord.script)
                    {
                        continue;
                    }

                    ComponentUtility.CopyComponent(sourceComponent);
                    ComponentUtility.PasteComponentAsNew(owner.gameObject);
                }
            }

            PrefabUtility.SaveAsPrefabAsset(targetRoot, prefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("Live2D custom backup restored to: " + prefabPath);
            EditorUtility.DisplayDialog("Live2D Custom Backup", "Restore completed.\n\n" + prefabPath, "OK");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(backupRoot);
            PrefabUtility.UnloadPrefabContents(targetRoot);
        }
    }

    private static List<Transform> FindTopLevelCustomSubtrees(Transform root)
    {
        var result = new List<Transform>();
        foreach (var child in GetAllTransforms(root))
        {
            if (child == root)
            {
                continue;
            }

            if (!HasProjectScriptInSubtree(child) || HasNonProjectScriptInSubtree(child))
            {
                continue;
            }

            if (child.parent != null && child.parent != root && HasProjectScriptInSubtree(child.parent) && !HasNonProjectScriptInSubtree(child.parent))
            {
                continue;
            }

            result.Add(child);
        }

        return result;
    }

    private static Live2DModelCustomBackupAsset FindBackupAsset(string prefabPath)
    {
        EnsureBackupDirectory();

        var guid = AssetDatabase.AssetPathToGUID(prefabPath);
        foreach (var assetGuid in AssetDatabase.FindAssets("t:Live2DModelCustomBackupAsset", new[] { BackupDirectory }))
        {
            var assetPath = AssetDatabase.GUIDToAssetPath(assetGuid);
            var asset = AssetDatabase.LoadAssetAtPath<Live2DModelCustomBackupAsset>(assetPath);
            if (asset != null && (asset.sourcePrefabGuid == guid || asset.sourcePrefabPath == prefabPath))
            {
                return asset;
            }
        }

        return null;
    }

    private static string GetSelectedOrDefaultPrefabPath()
    {
        var selected = Selection.activeObject;
        if (selected != null)
        {
            var path = AssetDatabase.GetAssetPath(selected);
            if (string.IsNullOrEmpty(path) && selected is GameObject selectedGameObject)
            {
                var source = PrefabUtility.GetCorrespondingObjectFromSource(selectedGameObject);
                path = AssetDatabase.GetAssetPath(source);
            }

            if (!string.IsNullOrEmpty(path) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return File.Exists(DefaultLive2DPrefabPath) ? DefaultLive2DPrefabPath : null;
    }

    private static void EnsureBackupDirectory()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Editor"))
        {
            AssetDatabase.CreateFolder("Assets", "Editor");
        }

        if (!AssetDatabase.IsValidFolder("Assets/Editor/ATRI"))
        {
            AssetDatabase.CreateFolder("Assets/Editor", "ATRI");
        }

        if (!AssetDatabase.IsValidFolder(BackupDirectory))
        {
            AssetDatabase.CreateFolder("Assets/Editor/ATRI", "Live2DModelCustomBackups");
        }
    }

    private static IEnumerable<Transform> GetAllTransforms(Transform root)
    {
        yield return root;
        for (var i = 0; i < root.childCount; i++)
        {
            foreach (var child in GetAllTransforms(root.GetChild(i)))
            {
                yield return child;
            }
        }
    }

    private static bool IsInsideAny(Transform transform, List<Transform> roots)
    {
        foreach (var root in roots)
        {
            if (transform == root || transform.IsChildOf(root))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasProjectScriptInSubtree(Transform root)
    {
        foreach (var transform in GetAllTransforms(root))
        {
            foreach (var component in transform.GetComponents<Component>())
            {
                if (GetProjectScript(component) != null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasNonProjectScriptInSubtree(Transform root)
    {
        foreach (var transform in GetAllTransforms(root))
        {
            foreach (var component in transform.GetComponents<Component>())
            {
                if (component is MonoBehaviour && GetScript(component) != null && GetProjectScript(component) == null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static MonoScript GetProjectScript(Component component)
    {
        var script = GetScript(component);
        if (script == null)
        {
            return null;
        }

        var scriptPath = AssetDatabase.GetAssetPath(script).Replace("\\", "/");
        return scriptPath.StartsWith("Assets/Scripts/", StringComparison.OrdinalIgnoreCase) ? script : null;
    }

    private static MonoScript GetScript(Component component)
    {
        var monoBehaviour = component as MonoBehaviour;
        return monoBehaviour == null ? null : MonoScript.FromMonoBehaviour(monoBehaviour);
    }

    private static void RemoveComponentsWithScript(GameObject gameObject, MonoScript script)
    {
        foreach (var component in gameObject.GetComponents<Component>())
        {
            if (GetProjectScript(component) == script)
            {
                UnityEngine.Object.DestroyImmediate(component);
            }
        }
    }

    private static void RemoveChildren(Transform transform)
    {
        for (var i = transform.childCount - 1; i >= 0; i--)
        {
            UnityEngine.Object.DestroyImmediate(transform.GetChild(i).gameObject);
        }
    }

    private static Transform FindByPath(Transform root, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return root;
        }

        var current = root;
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            current = FindDirectChild(current, part);
            if (current == null)
            {
                return null;
            }
        }

        return current;
    }

    private static Transform FindDirectChild(Transform parent, string name)
    {
        for (var i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child.name == name)
            {
                return child;
            }
        }

        return null;
    }

    private static string GetPath(Transform root, Transform target)
    {
        if (target == root)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var current = target;
        while (current != null && current != root)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts.ToArray());
    }

    private static string GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var index = path.LastIndexOf("/", StringComparison.Ordinal);
        return index < 0 ? string.Empty : path.Substring(0, index);
    }

    private static string MakeSafeName(string value)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalidChar, '_');
        }

        return value.Replace('/', '_').Replace('\\', '_');
    }
}
