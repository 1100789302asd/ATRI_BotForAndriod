using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

[InitializeOnLoad]
public sealed class AtriHttpBuildGuard : IPreprocessBuildWithReport
{
    public int callbackOrder
    {
        get { return -1000; }
    }

    static AtriHttpBuildGuard()
    {
        EnsureHttpDownloadsAllowed();
    }

    public void OnPreprocessBuild(BuildReport report)
    {
        EnsureHttpDownloadsAllowed();
    }

    private static void EnsureHttpDownloadsAllowed()
    {
        if (PlayerSettings.insecureHttpOption == InsecureHttpOption.AlwaysAllowed)
        {
            return;
        }

        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
        EditorUtility.SetDirty(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
        AssetDatabase.SaveAssets();
    }
}
