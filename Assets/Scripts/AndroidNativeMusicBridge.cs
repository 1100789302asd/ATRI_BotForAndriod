using System;
using System.IO;
using UnityEngine;
using UnityEngine.Events;

public sealed class AndroidNativeMusicBridge : MonoBehaviour
{
    private const string ServiceClassName = "com.atri.desktoppet.DesktopPetMusicService";
    private static AndroidNativeMusicBridge instance;

    public UnityEvent<string> MusicStarted;
    public UnityEvent MusicCompleted;
    public UnityEvent<string> MusicError;

    public static AndroidNativeMusicBridge Instance
    {
        get
        {
            if (instance != null)
            {
                return instance;
            }

            instance = FindFirstObjectByType<AndroidNativeMusicBridge>();
            if (instance != null)
            {
                return instance;
            }

            var gameObject = new GameObject("AndroidNativeMusicBridge");
            DontDestroyOnLoad(gameObject);
            instance = gameObject.AddComponent<AndroidNativeMusicBridge>();
            return instance;
        }
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        gameObject.name = "AndroidNativeMusicBridge";
        DontDestroyOnLoad(gameObject);
    }

    public void Play(string filePath, string title)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            AppendMusicLog("Play skipped. Missing file: " + filePath);
            MusicError?.Invoke("Missing native music file: " + filePath);
            return;
        }

        SendServiceAction("com.atri.desktoppet.action.PLAY", filePath, title);
#endif
    }

    public void Pause()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        SendServiceAction("com.atri.desktoppet.action.PAUSE", "", "");
#endif
    }

    public void Resume()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        SendServiceAction("com.atri.desktoppet.action.RESUME", "", "");
#endif
    }

    public void Stop()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        SendServiceAction("com.atri.desktoppet.action.STOP", "", "");
#endif
    }

    public bool IsPlaying()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var serviceClass = new AndroidJavaClass(ServiceClassName);
            return serviceClass.CallStatic<bool>("isPlaying");
        }
        catch (Exception exception)
        {
            AppendMusicLog("IsPlaying exception: " + exception);
            return false;
        }
#else
        return false;
#endif
    }

    public void OnAndroidMusicStarted(string title)
    {
        AppendMusicLog("Started: " + title);
        MusicStarted?.Invoke(title);
    }

    public void OnAndroidMusicCompleted(string unused)
    {
        AppendMusicLog("Completed");
        MusicCompleted?.Invoke();
    }

    public void OnAndroidMusicError(string error)
    {
        AppendMusicLog("Error: " + error);
        MusicError?.Invoke(error);
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private static void SendServiceAction(string action, string filePath, string title)
    {
        try
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            using var classClass = new AndroidJavaClass("java.lang.Class");
            using var serviceClass = classClass.CallStatic<AndroidJavaObject>("forName", ServiceClassName);
            using var intent = new AndroidJavaObject("android.content.Intent", activity, serviceClass);
            intent.Call<AndroidJavaObject>("setAction", action);
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                intent.Call<AndroidJavaObject>("putExtra", "path", filePath);
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                intent.Call<AndroidJavaObject>("putExtra", "title", title);
            }

            using var buildVersion = new AndroidJavaClass("android.os.Build$VERSION");
            var sdkInt = buildVersion.GetStatic<int>("SDK_INT");
            if (sdkInt >= 26)
            {
                activity.Call<AndroidJavaObject>("startForegroundService", intent);
            }
            else
            {
                activity.Call<AndroidJavaObject>("startService", intent);
            }

            AppendMusicLog("Service action sent: " + action + ", path=" + filePath + ", title=" + title);
        }
        catch (Exception exception)
        {
            AppendMusicLog("Service action exception: " + exception);
        }
    }
#endif

    private static void AppendMusicLog(string text)
    {
        try
        {
            var path = Path.Combine(Application.persistentDataPath, "DesktopPetRenderDebug.txt");
            File.AppendAllText(path, "\n\n[NativeMusic] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\n" + text);
        }
        catch
        {
        }
    }
}
