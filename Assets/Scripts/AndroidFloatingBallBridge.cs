using UnityEngine;
using System;
using System.IO;

/// <summary>
/// Unity bridge for the Android system overlay ball.
/// On Android, call ShowFloatingBall after overlay permission is granted.
/// </summary>
public sealed class AndroidFloatingBallBridge : MonoBehaviour
{
    [Header("Overlay Ball")]
    [SerializeField] private bool showOnStart = true;
    [SerializeField] private bool showWhenAppGoesBackground = true;
    [SerializeField] private int sizeDp = 64;
    [SerializeField] private int xDp = 24;
    [SerializeField] private int yDp = 160;

    private const string OverlayClassName = "com.atri.desktoppet.DesktopPetOverlay";
    private static AndroidFloatingBallBridge instance;
    private static bool overlayPermissionAskedThisSession;
    private static bool overlayPermissionRequestInFlight;
    private bool pendingShowAfterPermission;

    public static AndroidFloatingBallBridge Instance
    {
        get
        {
            if (instance != null)
            {
                return instance;
            }

            instance = FindFirstObjectByType<AndroidFloatingBallBridge>();
            if (instance != null)
            {
                return instance;
            }

            var gameObject = new GameObject("AndroidFloatingBallBridge");
            DontDestroyOnLoad(gameObject);
            instance = gameObject.AddComponent<AndroidFloatingBallBridge>();
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
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        if (showOnStart)
        {
            ShowFloatingBall();
        }
    }

    public static void ShowGlobalFloatingBall()
    {
        Instance.ShowFloatingBall();
    }

    public static void HideGlobalFloatingBall()
    {
        Instance.HideFloatingBall();
    }

    public static void RequestGlobalOverlayPermission()
    {
        Instance.RequestOverlayPermission();
    }

    public bool HasOverlayPermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using var activity = GetUnityActivity();
            using var overlayClass = new AndroidJavaClass(OverlayClassName);
            var result = overlayClass.CallStatic<bool>("canDrawOverlays", activity);
            AppendOverlayLog("HasOverlayPermission=" + result);
            return result;
        }
        catch (Exception exception)
        {
            AppendOverlayLog("HasOverlayPermission exception: " + exception);
            return false;
        }
#else
        return false;
#endif
    }

    public void RequestOverlayPermission()
    {
        RequestOverlayPermission(true);
    }

    private void RequestOverlayPermission(bool force)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!force && (overlayPermissionAskedThisSession || overlayPermissionRequestInFlight))
        {
            AppendOverlayLog("RequestOverlayPermission skipped. asked=" + overlayPermissionAskedThisSession + ", inFlight=" + overlayPermissionRequestInFlight);
            return;
        }

        try
        {
            AppendOverlayLog("RequestOverlayPermission");
            using var activity = GetUnityActivity();
            using var overlayClass = new AndroidJavaClass(OverlayClassName);
            overlayPermissionAskedThisSession = true;
            overlayPermissionRequestInFlight = true;
            overlayClass.CallStatic("requestPermission", activity);
        }
        catch (Exception exception)
        {
            overlayPermissionRequestInFlight = false;
            AppendOverlayLog("RequestOverlayPermission exception: " + exception);
        }
#endif
    }

    public void ShowFloatingBall()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        AppendOverlayLog("ShowFloatingBall called.");
        if (!HasOverlayPermission())
        {
            pendingShowAfterPermission = true;
            RequestOverlayPermission(false);
            return;
        }

        pendingShowAfterPermission = false;
        overlayPermissionRequestInFlight = false;
        try
        {
            using var activity = GetUnityActivity();
            using var overlayClass = new AndroidJavaClass(OverlayClassName);
            overlayClass.CallStatic("show", activity, sizeDp, xDp, yDp);
            AppendOverlayLog("ShowFloatingBall java show done.");
        }
        catch (Exception exception)
        {
            AppendOverlayLog("ShowFloatingBall exception: " + exception);
        }
#endif
    }

    public void HideFloatingBall()
    {
        pendingShowAfterPermission = false;
#if UNITY_ANDROID && !UNITY_EDITOR
        using var overlayClass = new AndroidJavaClass(OverlayClassName);
        overlayClass.CallStatic("hide");
#endif
    }

    private void OnApplicationFocus(bool hasFocus)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!hasFocus)
        {
            return;
        }

        overlayPermissionRequestInFlight = false;
        if (pendingShowAfterPermission && HasOverlayPermission())
        {
            ShowFloatingBall();
        }
#endif
    }

    private void OnApplicationPause(bool paused)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        AppendOverlayLog("OnApplicationPause paused=" + paused + ", pending=" + pendingShowAfterPermission);
        if (paused && showWhenAppGoesBackground)
        {
            ShowFloatingBall();
        }

        if (paused)
        {
            return;
        }

        overlayPermissionRequestInFlight = false;
        if (pendingShowAfterPermission && HasOverlayPermission())
        {
            ShowFloatingBall();
        }
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private static AndroidJavaObject GetUnityActivity()
    {
        using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        return unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
    }
#endif

    private static void AppendOverlayLog(string text)
    {
        try
        {
            var path = Path.Combine(Application.persistentDataPath, "DesktopPetRenderDebug.txt");
            File.AppendAllText(path, "\n\n[Overlay] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\n" + text);
        }
        catch
        {
        }
    }
}
