using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public static class DesktopPetResourcePath
{
    private const string ManifestFileName = "file_manifest.txt";

    public static string GetWritableModelPath(string relativePath)
    {
#if UNITY_EDITOR
        if (!Path.IsPathRooted(relativePath))
        {
            return GetEditorModelPath(relativePath);
        }
#endif
        return Path.Combine(Application.persistentDataPath, GetModelRelativePath(relativePath));
    }

    public static string GetEditorModelPath(string relativePath)
    {
        return Path.Combine(Application.dataPath, GetModelRelativePath(relativePath));
    }

    public static string GetStreamingModelPath(string relativePath)
    {
        return CombineUrl(Application.streamingAssetsPath, GetModelRelativePath(relativePath));
    }

    public static string ToRequestUrl(string pathOrUrl)
    {
        if (IsUrl(pathOrUrl))
        {
            return pathOrUrl;
        }

        return new Uri(pathOrUrl).AbsoluteUri;
    }

    public static IEnumerator EnsureWritableModelFile(string relativePath, bool createIfMissing, Action<string> onReady)
    {
        if (Path.IsPathRooted(relativePath))
        {
            onReady?.Invoke(relativePath);
            yield break;
        }

        var writablePath = GetWritableModelPath(relativePath);
        if (File.Exists(writablePath))
        {
            onReady?.Invoke(writablePath);
            yield break;
        }

        var directory = Path.GetDirectoryName(writablePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var editorPath = GetEditorModelPath(relativePath);
        if (File.Exists(editorPath))
        {
            File.Copy(editorPath, writablePath, true);
            onReady?.Invoke(writablePath);
            yield break;
        }

        var streamingPath = GetStreamingModelPath(relativePath);
        using (var request = UnityWebRequest.Get(ToRequestUrl(streamingPath)))
        {
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success && request.downloadHandler.data != null)
            {
                File.WriteAllBytes(writablePath, request.downloadHandler.data);
                onReady?.Invoke(writablePath);
                yield break;
            }
        }

        if (createIfMissing)
        {
            File.WriteAllText(writablePath, "", System.Text.Encoding.UTF8);
            onReady?.Invoke(writablePath);
            yield break;
        }

        onReady?.Invoke("");
    }

    public static IEnumerator ReadPackagedModelText(string relativePath, Action<string> onLoaded)
    {
        if (Path.IsPathRooted(relativePath))
        {
            onLoaded?.Invoke(File.Exists(relativePath)
                ? File.ReadAllText(relativePath, System.Text.Encoding.UTF8)
                : "");
            yield break;
        }

        var editorPath = GetEditorModelPath(relativePath);
        if (File.Exists(editorPath))
        {
            onLoaded?.Invoke(File.ReadAllText(editorPath, System.Text.Encoding.UTF8));
            yield break;
        }

        var streamingPath = GetStreamingModelPath(relativePath);
        using (var request = UnityWebRequest.Get(ToRequestUrl(streamingPath)))
        {
            yield return request.SendWebRequest();
            onLoaded?.Invoke(request.result == UnityWebRequest.Result.Success ? request.downloadHandler.text : "");
        }
    }

    public static IEnumerator ListPackagedModelFolderFiles(string relativeOrAbsoluteFolder, Action<List<string>> onLoaded)
    {
        var files = new List<string>();
        if (Path.IsPathRooted(relativeOrAbsoluteFolder))
        {
            AddDirectFolderFiles(files, relativeOrAbsoluteFolder);
            onLoaded?.Invoke(files);
            yield break;
        }

        var editorFolder = GetEditorModelPath(relativeOrAbsoluteFolder);
        if (Directory.Exists(editorFolder))
        {
            AddDirectFolderFiles(files, editorFolder);
            onLoaded?.Invoke(files);
            yield break;
        }

        var streamingFolder = GetStreamingModelPath(relativeOrAbsoluteFolder);
        if (!IsUrl(streamingFolder) && Directory.Exists(streamingFolder))
        {
            AddDirectFolderFiles(files, streamingFolder);
            onLoaded?.Invoke(files);
            yield break;
        }

        var folderPrefix = NormalizeRelativePath(relativeOrAbsoluteFolder).TrimEnd('/') + "/";
        var manifestText = "";
        yield return ReadPackagedModelText(ManifestFileName, text => manifestText = text);

        if (!string.IsNullOrWhiteSpace(manifestText))
        {
            var lines = manifestText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var entry = NormalizeRelativePath(lines[i].Trim());
                if (entry.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(GetStreamingModelPath(entry));
                }
            }
        }

        files.Sort(StringComparer.Ordinal);
        onLoaded?.Invoke(files);
    }

    private static void AddDirectFolderFiles(List<string> files, string folder)
    {
        files.AddRange(Directory.GetFiles(folder));
        files.Sort(StringComparer.Ordinal);
    }

    private static string GetModelRelativePath(string relativePath)
    {
        return Path.Combine(ResCtrl.basepath, NormalizeRelativePath(relativePath));
    }

    private static string NormalizeRelativePath(string path)
    {
        return (path ?? "").Replace('\\', '/').TrimStart('/');
    }

    private static string CombineUrl(string left, string right)
    {
        return left.TrimEnd('/', '\\') + "/" + NormalizeRelativePath(right);
    }

    private static bool IsUrl(string path)
    {
        return path.IndexOf("://", StringComparison.Ordinal) >= 0 ||
               path.StartsWith("jar:", StringComparison.OrdinalIgnoreCase);
    }
}
