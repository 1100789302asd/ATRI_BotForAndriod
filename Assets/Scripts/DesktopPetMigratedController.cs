using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using Live2D.Cubism.Framework.Expression;
using Live2D.Cubism.Framework.Motion;
using Live2D.Cubism.Framework.Raycasting;
using Live2D.Cubism.Rendering;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Unity-side migration of the old Python desktop-pet template.
/// Attach this to the Live2D model root. Drag motions, audio clips, and expressions in the Inspector.
/// </summary>
public sealed class DesktopPetMigratedController : MonoBehaviour, ICubismUpdatable
{
    [Serializable]
    public sealed class MoodProfile
    {
        public string mood = "normal";
        public string expressionName = "normal";
        [Range(0f, 1f)]
        public float mouthOpen = 0.6f;
        public float shakeRange = 0f;
    }

    [Header("Live2D")]
    [SerializeField] private CubismModel model;
    [SerializeField] private CubismMotionController motionController;
    [SerializeField] private CubismExpressionController expressionController;
    [SerializeField] private CubismRaycaster raycaster;

    [Header("Motions")]
    [SerializeField] private AnimationClip idleMotion;
    [SerializeField] private AnimationClip[] tapBodyMotions;

    [Header("Audio")]
    [SerializeField] private AudioSource voiceSource;
    [SerializeField] private AudioSource musicSource;
    [SerializeField] private NeteaseCloudMusicClient neteaseMusicClient;
    [SerializeField] private AudioClip[] prefabVoiceClips;
    [SerializeField] private AudioClip[] musicClips;
    [SerializeField] private bool keepUnityAudioAliveInBackground = true;

    [Header("Audio Files")]
    [SerializeField] private bool loadAudioFilesOnStart = true;
    [SerializeField] private string prefabAudioFolder = "config/prefab_audio";
    [SerializeField] private string initMusicFolder = "init_musics";
    [SerializeField] private float musicVolume = 0.2f;
    [SerializeField] private bool randomizeInitialMusic = true;

    [Header("Mood")]
    [SerializeField] private string currentMood = "happy";
    [SerializeField] private MoodProfile[] moodProfiles =
    {
        new MoodProfile { mood = "happy", expressionName = "happy", mouthOpen = 0.8f, shakeRange = 8f },
        new MoodProfile { mood = "sad", expressionName = "sad", mouthOpen = 0.4f, shakeRange = 0f },
        new MoodProfile { mood = "normal", expressionName = "normal", mouthOpen = 0.6f, shakeRange = 0f },
        new MoodProfile { mood = "shy", expressionName = "shy", mouthOpen = 0.3f, shakeRange = 2f },
        new MoodProfile { mood = "confuse", expressionName = "confuse", mouthOpen = 0.6f, shakeRange = 0f },
        new MoodProfile { mood = "得意", expressionName = "得意", mouthOpen = 0.8f, shakeRange = 8f },
        new MoodProfile { mood = "气急败坏", expressionName = "气急败坏", mouthOpen = 0.8f, shakeRange = 0f },
        new MoodProfile { mood = "赌气", expressionName = "赌气", mouthOpen = 0.8f, shakeRange = 0f },
        new MoodProfile { mood = "彻底坏掉", expressionName = "彻底黑化", mouthOpen = 0.3f, shakeRange = 0f }
    };
    public bool isBodyShaking;
    [Header("Parameters")]
    [SerializeField] private string mouthOpenParameter = "ParamMouthOpenY";
    [SerializeField] private string bodyAngleZParameter = "ParamBodyAngleZ";
    [SerializeField] private string headHitAreaName = "Head";
    [SerializeField] private string bodyHitAreaName = "Body";

    [Header("Custom Hit Areas")]
    [SerializeField] private bool useCustomHitAreas = true;

    [Header("Mood Motion")]
    [SerializeField] private float bodyShakeStep = 0.06f;
    [SerializeField] private float bodyShakeReturnThreshold = 0.1f;
    [SerializeField] private float bodyShakeReferenceFrameRate = 60f;

    [Header("Mouth Motion")]
    [SerializeField] private float mouthAnalysisWindowMs = 100f;

    [Header("State")]
    [SerializeField] private bool voiceInputMode;
    [SerializeField] private bool musicEnabled = true;
    [SerializeField] private bool autoPlayNextMusic = true;
    [SerializeField] private string displayedText = "";

    [Header("Debug Android Render")]
    [SerializeField] private bool logRenderStateOnStart = true;
    [SerializeField] private bool writeRenderStateToFile = true;
    [SerializeField] private bool repairCubismMeshesOnStart = true;
    [SerializeField] private float renderStateLogDelay = 1f;
    [SerializeField] private string renderStateLogFileName = "DesktopPetRenderDebug.txt";

    [Header("External Hooks")]
    public UnityEvent<string> UserTextSubmitted;
    public UnityEvent<string> DisplayTextChanged;
    public UnityEvent<string> MoodChanged;

    private readonly CubismRaycastHit[] raycastResults = new CubismRaycastHit[4];
    private readonly float[] audioSamples = new float[256];
    private int prefabVoiceIndex;
    private int musicIndex;
    private bool audioFilesLoaded;
    private bool audioFilesLoading;
    private float shakeUntil;
    private float bodyShakeDirection = 1f;
    private float currentBodyShake;
    private float[] voiceMouthTimeline = Array.Empty<float>();
    private float voiceMouthWindowSeconds = 0.1f;
    private bool externalMusicProviderActive;
    private bool renderStateLogged;
    private string lastRenderRepairLog = "";
    private bool renderRepairCoroutineStarted;
    private bool unityLogHooked;
    private bool musicShouldResumeAfterFocus;
    private float musicTimeBeforeBackground;
    private AudioClip trackedMusicClip;
    private float trackedMusicStartedAt = -1f;
    private float trackedMusicLength;
    private readonly List<string> recentUnityLogs = new List<string>();

    public bool HasUpdateController { get; set; }

    [Header("主动模式")]
    public float lastSpeakTime,speakInterval;
    public bool isInitiative;
    public bool isUserSpeakingTurn;
    public bool AudioFilesLoaded
    {
        get { return audioFilesLoaded; }
    }

    public string CurrentMood
    {
        get { return currentMood; }
    }

    public bool IsMusicPlaying
    {
        get { return musicSource != null && musicSource.isPlaying; }
    }

    public bool IsMusicFinished
    {
        get
        {
            if (musicSource == null || musicSource.clip == null)
            {
                return false;
            }

            var clipLength = musicSource.clip.length > 0f ? musicSource.clip.length : trackedMusicLength;
            if (clipLength <= 0f)
            {
                return false;
            }

            if (musicSource.isPlaying)
            {
                return false;
            }

            var finishTime = Mathf.Max(0f, clipLength - 0.15f);
            if (musicSource.time >= finishTime)
            {
                return true;
            }

            return trackedMusicClip == musicSource.clip &&
                   trackedMusicStartedAt >= 0f &&
                   Time.unscaledTime - trackedMusicStartedAt >= finishTime;
        }
    }

    public bool HasPausableExternalMusic
    {
        get
        {
            return musicSource != null &&
                   musicSource.clip != null &&
                   externalMusicProviderActive &&
                   musicSource.time > 0f &&
                   !IsMusicFinished;
        }
    }

    public int ExecutionOrder
    {
        get { return CubismUpdateExecutionOrder.CubismLookController + 1; }
    }

    public bool NeedsUpdateOnEditing
    {
        get { return false; }
    }

    private void Reset()
    {
        CacheComponents();
    }

    private void Awake()
    {
        CacheComponents();
        Application.runInBackground = true;
        AudioListener.pause = false;
        ConfigureAudioSourcesForBackground();
        HookUnityLogFile();
        WriteRenderStateBootLog("Awake");
    }

    private void OnEnable()
    {
        HookUnityLogFile();
        CacheComponents();
        HasUpdateController = GetComponent<CubismUpdateController>() != null;
        RefreshCubismUpdateController();
    }

    private void OnDisable()
    {
        UnhookUnityLogFile();
    }

    private void OnApplicationPause(bool paused)
    {
        HandleApplicationBackgroundState(paused);
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        HandleApplicationBackgroundState(!hasFocus);
    }

    private void Start()
    {
        Application.runInBackground = true;
        AudioListener.pause = false;
        ConfigureAudioSourcesForBackground();
        HasUpdateController = GetComponent<CubismUpdateController>() != null;
        RefreshCubismUpdateController();
        PlayIdle();

        if (loadAudioFilesOnStart)
        {
            StartCoroutine(LoadAudioFilesThenStartMusic());
        }
        else if (musicEnabled)
        {
            PlayNextMusic();
        }

        renderRepairCoroutineStarted = true;
        StartCoroutine(RepairCubismMeshesAfterStartup());

        if (logRenderStateOnStart)
        {
            StartCoroutine(LogRenderStateAfterDelay());
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidFloatingBallBridge.Instance.ShowFloatingBall();
#endif
    }

    private void HandleApplicationBackgroundState(bool isBackground)
    {
        if (!keepUnityAudioAliveInBackground)
        {
            return;
        }

        AudioListener.pause = false;

        if (musicSource == null)
        {
            return;
        }

        if (isBackground)
        {
            musicShouldResumeAfterFocus = musicSource.clip != null && musicEnabled && externalMusicProviderActive && musicSource.isPlaying;
            musicTimeBeforeBackground = musicSource.time;
            return;
        }

        if (musicShouldResumeAfterFocus && musicSource.clip != null && !musicSource.isPlaying)
        {
            if (musicTimeBeforeBackground > 0f && musicTimeBeforeBackground < musicSource.clip.length)
            {
                musicSource.time = musicTimeBeforeBackground;
            }

            musicSource.UnPause();
            musicSource.Play();
        }

        musicShouldResumeAfterFocus = false;
    }

    private void Update()
    {
        HandlePointerTap();
        if (!HasUpdateController)
        {
            ApplyLive2DParameters();
        }

        UpdateMusicLoop();
    }

    public void OnLateUpdate()
    {
        ApplyLive2DParameters();
    }

    public void ToggleChatInitative()
    {
        isInitiative=!isInitiative;
        if(isInitiative)
        {
            SetUserSpeakingStatus();
        }
    }

    public void SetChatEnabled(bool enabled)
    {
    }

    public void ToggleVoiceInputMode()
    {
        SetVoiceInputMode(!voiceInputMode);
    }

    public void SetVoiceInputMode(bool enabled)
    {
        voiceInputMode = enabled;
    }

    public void ToggleMusic()
    {
        if (ShouldRouteMusicToNetease())
        {
            neteaseMusicClient.ToggleMusic();
            musicEnabled = neteaseMusicClient.MusicOn;
            return;
        }

        SetMusicEnabled(!musicEnabled);
    }

    public void SetMusicEnabled(bool enabled)
    {
        if (ShouldRouteMusicToNetease())
        {
            musicEnabled = enabled;
            neteaseMusicClient.SetMusicEnabled(enabled);
            return;
        }

        musicEnabled = enabled;
        if (musicSource == null)
        {
            return;
        }

        if (musicEnabled)
        {
            if (!musicSource.isPlaying)
            {
                PlayNextMusic();
            }
            else
            {
                musicSource.UnPause();
            }
        }
        else
        {
            musicSource.Pause();
        }
    }
    public void ToggleInitivate()
    {
        
    }
    public void ToggleAutoPlayMusic()
    {
        SetAutoPlayNextMusic(!autoPlayNextMusic);
    }

    public void SetAutoPlayNextMusic(bool enabled)
    {
        autoPlayNextMusic = enabled;
    }

    public void ReloadAudioFiles()
    {
        if (!audioFilesLoading)
        {
            StartCoroutine(LoadAudioFilesThenStartMusic());
        }
    }

    public void RandomExpression()
    {
        if (expressionController == null ||
            expressionController.ExpressionsList == null ||
            expressionController.ExpressionsList.CubismExpressionObjects == null ||
            expressionController.ExpressionsList.CubismExpressionObjects.Length == 0)
        {
            return;
        }

        expressionController.CurrentExpressionIndex = UnityEngine.Random.Range(
            0,
            expressionController.ExpressionsList.CubismExpressionObjects.Length);
    }

    public void RandomMotion()
    {
        PlayRandomMotion(tapBodyMotions);
    }

    public void PlayNextMusic()
    {
        if (musicSource == null || musicClips == null || musicClips.Length == 0)
        {
            return;
        }

        musicSource.clip = musicClips[musicIndex];
        musicSource.volume = musicVolume;
        musicSource.Play();
        TrackMusicPlayback(musicSource.clip);
        musicIndex = (musicIndex + 1) % musicClips.Length;
    }

    public void PlayRandomMusic()
    {
        if (ShouldRouteMusicToNetease())
        {
            neteaseMusicClient.PlayRandomMusic();
            return;
        }

        if (musicSource == null || musicClips == null || musicClips.Length == 0)
        {
            return;
        }

        musicIndex = UnityEngine.Random.Range(0, musicClips.Length);
        PlayNextMusic();
    }

    public void SetExternalMusicProviderActive(bool active)
    {
        externalMusicProviderActive = active;
    }

    public void SetNeteaseMusicClient(NeteaseCloudMusicClient client)
    {
        neteaseMusicClient = client;
    }

    public void PauseExternalMusic()
    {
        if (musicSource != null)
        {
            musicSource.Pause();
        }
    }

    public void ResumeExternalMusic()
    {
        if (musicSource != null)
        {
            musicSource.UnPause();
        }
    }

    public void StopUnityMusicSource()
    {
        if (musicSource == null)
        {
            return;
        }

        musicSource.Stop();
        musicSource.clip = null;
        ClearMusicPlaybackTracking();
    }

    public void PlayExternalMusic(AudioClip clip, string displayName = "")
    {
        if (musicSource == null || clip == null)
        {
            if (musicSource == null)
            {
                Debug.LogWarning("Music AudioSource is missing; cannot play external music.", this);
            }

            if (clip == null)
            {
                Debug.LogWarning("External music clip is empty.", this);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            clip.name = displayName;
        }

        musicSource.clip = clip;
        musicSource.volume = musicVolume;
        musicSource.ignoreListenerPause = true;
        externalMusicProviderActive = true;
        musicSource.Play();
        TrackMusicPlayback(musicSource.clip);
    }

    public void SubmitUserText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        UserTextSubmitted?.Invoke(text);
    }

    public void ReceiveAiAnswer(string answer)
    {

        ParseOldTemplateAnswer(answer, out var cnText, out var mood, out _);
        SetDisplayedText(cnText);
       
    }
    public void SetUserSpeakingStatus()
    {
        lastSpeakTime=Time.time;
        isUserSpeakingTurn=true;
    }

    public void Speak(AudioClip clip, string text = "", string mood = "", bool playMoodMotion = true)
    {
        if (!string.IsNullOrEmpty(text))
        {
            SetDisplayedText(text);
        }

        if (!string.IsNullOrEmpty(mood))
        {
            SetMood(mood);
        }

        if (voiceSource == null || clip == null)
        {
            if (voiceSource == null)
            {
                Debug.LogWarning("Voice AudioSource is missing; cannot play voice.", this);
            }

            if (clip == null)
            {
                Debug.LogWarning("Voice clip is empty; cannot play voice.", this);
            }

            return;
        }
        SetUserSpeakingStatus();
        voiceSource.clip = clip;
        PrepareVoiceMouthTimeline(clip);
        voiceSource.Play();
        if (playMoodMotion)
        {
            StartMoodMotion(clip.length);
        }
    }

    public void PlayPrefabVoice()
    {
        if (prefabVoiceClips == null || prefabVoiceClips.Length == 0)
        {
            return;
        }

        var clip = prefabVoiceClips[prefabVoiceIndex];
        prefabVoiceIndex = (prefabVoiceIndex + 1) % prefabVoiceClips.Length;
        Speak(clip, clip.name, currentMood, playMoodMotion: false);
    }

    public void SetMood(string mood)
    {
        mood = NormalizeMoodText(mood);
        if (string.IsNullOrEmpty(mood))
        {
            return;
        }

        currentMood = mood;
        var profile = GetMoodProfile(mood);
        if (!ApplyExpression(profile.expressionName))
        {
            Debug.LogWarning("Mood expression not found. mood=\"" + mood + "\", expression=\"" + profile.expressionName + "\".", this);
        }

        MoodChanged?.Invoke(currentMood);
    }

    private void CacheComponents()
    {
        if (model == null)
        {
            model = GetComponent<CubismModel>();
        }

        if (motionController == null)
        {
            motionController = GetComponent<CubismMotionController>();
        }

        if (expressionController == null)
        {
            expressionController = GetComponent<CubismExpressionController>();
        }

        if (raycaster == null)
        {
            raycaster = GetComponent<CubismRaycaster>();
        }

        if (voiceSource == null)
        {
            voiceSource = GetComponent<AudioSource>();
        }

        ConfigureAudioSourcesForBackground();

        if (neteaseMusicClient == null)
        {
            neteaseMusicClient = GetComponent<NeteaseCloudMusicClient>();
        }

        if (neteaseMusicClient == null)
        {
            neteaseMusicClient = GetComponentInParent<NeteaseCloudMusicClient>();
        }

        if (neteaseMusicClient == null)
        {
            neteaseMusicClient = GetComponentInChildren<NeteaseCloudMusicClient>();
        }
    }

    private void ConfigureAudioSourcesForBackground()
    {
        if (voiceSource != null)
        {
            voiceSource.ignoreListenerPause = true;
        }

        if (musicSource != null)
        {
            musicSource.ignoreListenerPause = true;
        }
    }

    private IEnumerator LogRenderStateAfterDelay()
    {
        if (renderStateLogged)
        {
            yield break;
        }

        renderStateLogged = true;
        yield return new WaitForSeconds(Mathf.Max(0f, renderStateLogDelay));
        LogRenderState();
    }

    private IEnumerator RepairCubismMeshesAfterStartup()
    {
        AppendUnityLogFile("Repair coroutine entered.");
        yield return null;
        AppendUnityLogFile("Repair coroutine after first frame.");
        yield return null;
        AppendUnityLogFile("Repair coroutine after second frame.");

        var renderController = GetComponent<CubismRenderController>();
        var cubismRenderers = GetComponentsInChildren<CubismRenderer>(true);
        var repairLog = "DesktopPetRenderRepair " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
            "\nlogFile=" + GetRenderStateLogFilePath() +
            "\nrepairEnabledInspector=" + repairCubismMeshesOnStart +
            "\nrenderController=" + (renderController != null ? "yes" : "no") +
            "\nmodel=" + (model != null ? model.name : "<null>") +
            "\nmodelRevived=" + (model != null ? model.IsRevived.ToString() : "<null>") +
            "\nmodelDrawables=" + (model != null && model.Drawables != null ? model.Drawables.Length.ToString() : "<null>") +
            "\ncubismRenderers=" + cubismRenderers.Length +
            "\nallMeshesNullBefore=" + AllCubismMeshesAreNull(cubismRenderers);

        lastRenderRepairLog = repairLog;
        AppendUnityLogFile(repairLog);

        if (renderController == null || cubismRenderers.Length == 0)
        {
            lastRenderRepairLog += "\nstatus=skipped-missing-controller-or-renderers";
            AppendUnityLogFile(lastRenderRepairLog);
            LogRenderState();
            yield break;
        }

        if (!AllCubismMeshesAreNull(cubismRenderers))
        {
            lastRenderRepairLog += "\nstatus=skipped-meshes-already-present";
            AppendUnityLogFile(lastRenderRepairLog);
            LogRenderState();
            yield break;
        }

        Debug.LogWarning("[DesktopPetRenderDebug] Cubism meshes are all null. Running startup mesh repair.", this);

        try
        {
            if (model != null)
            {
                model.ForceUpdateNow();
            }
            repairLog += "\nforceUpdateBefore=True";

            ResetCubismRenderControllerCaches(renderController);
            repairLog += "\nresetControllerCaches=True";

            var drawables = model != null ? model.Drawables : null;
            var bindCount = 0;
            var firstDrawableVertexCount = "<null>";

            if (drawables != null)
            {
                if (drawables.Length > 0 && drawables[0] != null)
                {
                    try
                    {
                        var vertices = drawables[0].VertexPositions;
                        firstDrawableVertexCount = vertices != null ? vertices.Length.ToString() : "<null>";
                    }
                    catch (Exception exception)
                    {
                        firstDrawableVertexCount = "exception:" + exception.GetType().Name;
                    }
                }

                for (var i = 0; i < drawables.Length && i < cubismRenderers.Length; i++)
                {
                    var renderer = cubismRenderers[i];

                    if (renderer == null)
                    {
                        continue;
                    }

                    renderer.DrawObjectType = CubismModelTypes.DrawObjectType.Drawable;
                    renderer.Drawable = drawables[i];
                    bindCount++;
                }
            }

            repairLog += "\nmanualDrawableBindCount=" + bindCount;
            repairLog += "\nfirstDrawableVertexCount=" + firstDrawableVertexCount;

            renderController.TryInitialize();
            repairLog += "\ntryInitializeDone=True";
            repairLog += "\nrenderControllerInitializedAfterTry=" + renderController.IsInitialized;
            renderController.OnLateUpdate();

            for (var i = 0; i < cubismRenderers.Length; i++)
            {
                var renderer = cubismRenderers[i];
                if (renderer == null)
                {
                    continue;
                }

                renderer.TryInitialize(renderController);

                if (renderer.Mesh != null)
                {
                    renderer.SwapMeshes();
                }
            }

            if (model != null)
            {
                model.ForceUpdateNow();
            }

            repairLog += "\nallMeshesNullAfter=" + AllCubismMeshesAreNull(cubismRenderers);
            repairLog += "\nstatus=completed";
        }
        catch (Exception exception)
        {
            repairLog += "\nexception=" + exception;
            repairLog += "\nstatus=exception";
            AppendUnityLogFile("Repair exception:\n" + exception);
            Debug.LogWarning("[DesktopPetRenderDebug] Cubism mesh repair failed: " + exception.Message, this);
        }

        lastRenderRepairLog = repairLog;
        AppendUnityLogFile(repairLog);
        yield return null;

        LogRenderState();
    }

    private static bool AllCubismMeshesAreNull(CubismRenderer[] cubismRenderers)
    {
        var checkedCount = 0;

        for (var i = 0; i < cubismRenderers.Length; i++)
        {
            var renderer = cubismRenderers[i];
            if (renderer == null || renderer.DrawObjectType != CubismModelTypes.DrawObjectType.Drawable)
            {
                continue;
            }

            checkedCount++;

            if (renderer.Mesh != null)
            {
                return false;
            }
        }

        return checkedCount > 0;
    }

    private static void ResetCubismRenderControllerCaches(CubismRenderController renderController)
    {
        if (renderController == null)
        {
            return;
        }

        SetPrivateField(renderController, "_renderers", null);
        SetPrivateField(renderController, "_drawableRenderers", null);
        SetPrivateField(renderController, "_offscreenRenderers", null);
        SetPrivateField(renderController, "_isInitialized", false);
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        if (target == null)
        {
            return;
        }

        var type = target.GetType();

        while (type != null)
        {
            var field = type.GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }

            type = type.BaseType;
        }
    }

    public void LogRenderState()
    {
        CacheComponents();

        var mainCamera = Camera.main;
        var renderController = GetComponent<CubismRenderController>();
        var cubismRenderers = GetComponentsInChildren<CubismRenderer>(true);
        var meshRenderers = GetComponentsInChildren<MeshRenderer>(true);
        var enabledMeshRendererCount = 0;
        var activeMeshRendererCount = 0;
        var firstMaterialName = "<none>";
        var firstShaderName = "<none>";
        var firstTextureName = "<none>";
        var firstBounds = "<none>";
        var cubismEnabledMeshCount = 0;
        var cubismActiveMeshCount = 0;
        var cubismSkippedCount = 0;
        var cubismMissingMainTextureCount = 0;
        var cubismMissingDrawMaterialCount = 0;
        var cubismNullMeshCount = 0;
        var cubismZeroVertexMeshCount = 0;
        var cubismPositiveVertexMeshCount = 0;
        var cubismDrawableCount = 0;
        var cubismOffscreenCount = 0;
        var firstCubismName = "<none>";
        var firstCubismDrawObjectType = "<none>";
        var firstCubismMainTextureName = "<none>";
        var firstCubismDrawMaterialName = "<none>";
        var firstCubismDrawShaderName = "<none>";
        var firstCubismSharedMaterialName = "<none>";
        var firstCubismSharedShaderName = "<none>";
        var firstCubismMeshEnabled = "<none>";
        var firstCubismActive = "<none>";
        var firstCubismSkipRendering = "<none>";
        var firstCubismSortingLayer = "<none>";
        var firstCubismSortingOrder = "<none>";
        var firstCubismBoundsCenter = "<none>";
        var firstCubismBoundsSize = "<none>";
        var firstCubismVertexCount = "<none>";
        var firstEnabledCubismName = "<none>";
        var firstEnabledCubismMainTextureName = "<none>";
        var firstEnabledCubismDrawMaterialName = "<none>";
        var firstEnabledCubismDrawShaderName = "<none>";
        var firstEnabledCubismSkipRendering = "<none>";
        var firstEnabledCubismBoundsCenter = "<none>";
        var firstEnabledCubismBoundsSize = "<none>";
        var firstEnabledCubismVertexCount = "<none>";

        for (var i = 0; i < meshRenderers.Length; i++)
        {
            var renderer = meshRenderers[i];
            if (renderer == null)
            {
                continue;
            }

            if (renderer.enabled)
            {
                enabledMeshRendererCount++;
            }

            if (renderer.gameObject.activeInHierarchy)
            {
                activeMeshRendererCount++;
            }

            if (firstMaterialName == "<none>" && renderer.sharedMaterial != null)
            {
                var material = renderer.sharedMaterial;
                firstMaterialName = material.name;
                firstShaderName = material.shader != null ? material.shader.name : "<missing shader>";
                var texture = material.mainTexture;
                firstTextureName = texture != null ? texture.name : "<none>";
                firstBounds = renderer.bounds.ToString();
            }
        }

        for (var i = 0; i < cubismRenderers.Length; i++)
        {
            var renderer = cubismRenderers[i];
            if (renderer == null)
            {
                continue;
            }

            var meshRenderer = renderer.MeshRenderer;
            var mainTexture = renderer.MainTexture;
            var drawMaterial = renderer.DrawMaterial;
            var sharedMaterial = meshRenderer != null ? meshRenderer.sharedMaterial : null;
            var mesh = renderer.Mesh;

            if (renderer.DrawObjectType == CubismModelTypes.DrawObjectType.Drawable)
            {
                cubismDrawableCount++;
            }
            else if (renderer.DrawObjectType == CubismModelTypes.DrawObjectType.Offscreen)
            {
                cubismOffscreenCount++;
            }

            if (meshRenderer != null && meshRenderer.enabled)
            {
                cubismEnabledMeshCount++;
            }

            if (renderer.gameObject.activeInHierarchy)
            {
                cubismActiveMeshCount++;
            }

            if (renderer.SkipRendering)
            {
                cubismSkippedCount++;
            }

            if (mainTexture == null)
            {
                cubismMissingMainTextureCount++;
            }

            if (drawMaterial == null && renderer.DrawObjectType == CubismModelTypes.DrawObjectType.Drawable)
            {
                cubismMissingDrawMaterialCount++;
            }

            if (mesh == null)
            {
                cubismNullMeshCount++;
            }
            else if (mesh.vertexCount <= 0)
            {
                cubismZeroVertexMeshCount++;
            }
            else
            {
                cubismPositiveVertexMeshCount++;
            }

            if (firstCubismName == "<none>")
            {
                firstCubismName = renderer.name;
                firstCubismDrawObjectType = renderer.DrawObjectType.ToString();
                firstCubismMainTextureName = mainTexture != null ? mainTexture.name : "<null>";
                firstCubismDrawMaterialName = drawMaterial != null ? drawMaterial.name : "<null>";
                firstCubismDrawShaderName = drawMaterial != null && drawMaterial.shader != null ? drawMaterial.shader.name : "<null>";
                firstCubismSharedMaterialName = sharedMaterial != null ? sharedMaterial.name : "<null>";
                firstCubismSharedShaderName = sharedMaterial != null && sharedMaterial.shader != null ? sharedMaterial.shader.name : "<null>";
                firstCubismMeshEnabled = meshRenderer != null ? meshRenderer.enabled.ToString() : "<null>";
                firstCubismActive = renderer.gameObject.activeInHierarchy.ToString();
                firstCubismSkipRendering = renderer.SkipRendering.ToString();
                firstCubismSortingLayer = meshRenderer != null ? SortingLayer.IDToName(meshRenderer.sortingLayerID) : "<null>";
                firstCubismSortingOrder = meshRenderer != null ? meshRenderer.sortingOrder.ToString() : "<null>";
                firstCubismBoundsCenter = meshRenderer != null ? FormatVector3(meshRenderer.bounds.center) : "<null>";
                firstCubismBoundsSize = meshRenderer != null ? FormatVector3(meshRenderer.bounds.size) : "<null>";
                firstCubismVertexCount = mesh != null ? mesh.vertexCount.ToString() : "<null>";
            }

            if (firstEnabledCubismName == "<none>" && meshRenderer != null && meshRenderer.enabled)
            {
                firstEnabledCubismName = renderer.name;
                firstEnabledCubismMainTextureName = mainTexture != null ? mainTexture.name : "<null>";
                firstEnabledCubismDrawMaterialName = drawMaterial != null ? drawMaterial.name : "<null>";
                firstEnabledCubismDrawShaderName = drawMaterial != null && drawMaterial.shader != null ? drawMaterial.shader.name : "<null>";
                firstEnabledCubismSkipRendering = renderer.SkipRendering.ToString();
                firstEnabledCubismBoundsCenter = FormatVector3(meshRenderer.bounds.center);
                firstEnabledCubismBoundsSize = FormatVector3(meshRenderer.bounds.size);
                firstEnabledCubismVertexCount = mesh != null ? mesh.vertexCount.ToString() : "<null>";
            }
        }

        var modelInfo =
            "root=" + name +
            ", activeSelf=" + gameObject.activeSelf +
            ", activeInHierarchy=" + gameObject.activeInHierarchy +
            ", layer=" + gameObject.layer +
            ", position=" + transform.position +
            ", localScale=" + transform.localScale +
            ", model=" + (model != null ? model.name : "<null>") +
            ", renderController=" + (renderController != null ? "yes" : "no") +
            ", renderControllerEnabled=" + (renderController != null ? renderController.enabled.ToString() : "<null>") +
            ", renderControllerInitialized=" + (renderController != null ? renderController.IsInitialized.ToString() : "<null>") +
            ", repairCoroutineStarted=" + renderRepairCoroutineStarted +
            ", hasRepairLog=" + (!string.IsNullOrEmpty(lastRenderRepairLog)) +
            ", renderControllerOpacity=" + (renderController != null ? renderController.Opacity.ToString("0.###") : "<null>") +
            ", cubismRenderers=" + cubismRenderers.Length +
            ", meshRenderers=" + meshRenderers.Length +
            ", enabledMeshRenderers=" + enabledMeshRendererCount +
            ", activeMeshRenderers=" + activeMeshRendererCount +
            ", firstMaterial=" + firstMaterialName +
            ", firstShader=" + firstShaderName +
            ", firstTexture=" + firstTextureName +
            ", firstBounds=" + firstBounds;

        var cubismInfo =
            "cubismDrawableCount=" + cubismDrawableCount +
            ", cubismOffscreenCount=" + cubismOffscreenCount +
            ", cubismEnabledMesh=" + cubismEnabledMeshCount +
            ", cubismActive=" + cubismActiveMeshCount +
            ", cubismSkipRendering=" + cubismSkippedCount +
            ", cubismMissingMainTexture=" + cubismMissingMainTextureCount +
            ", cubismMissingDrawMaterial=" + cubismMissingDrawMaterialCount +
            ", cubismNullMesh=" + cubismNullMeshCount +
            ", cubismZeroVertexMesh=" + cubismZeroVertexMeshCount +
            ", cubismPositiveVertexMesh=" + cubismPositiveVertexMeshCount +
            ", firstCubism=" + firstCubismName +
            ", firstCubismType=" + firstCubismDrawObjectType +
            ", firstCubismMainTexture=" + firstCubismMainTextureName +
            ", firstCubismDrawMaterial=" + firstCubismDrawMaterialName +
            ", firstCubismDrawShader=" + firstCubismDrawShaderName +
            ", firstCubismSharedMaterial=" + firstCubismSharedMaterialName +
            ", firstCubismSharedShader=" + firstCubismSharedShaderName +
            ", firstCubismMeshEnabled=" + firstCubismMeshEnabled +
            ", firstCubismActive=" + firstCubismActive +
            ", firstCubismSkipRendering=" + firstCubismSkipRendering +
            ", firstCubismSortingLayer=" + firstCubismSortingLayer +
            ", firstCubismSortingOrder=" + firstCubismSortingOrder +
            ", firstCubismBoundsCenter=" + firstCubismBoundsCenter +
            ", firstCubismBoundsSize=" + firstCubismBoundsSize +
            ", firstCubismVertexCount=" + firstCubismVertexCount +
            ", firstEnabledCubism=" + firstEnabledCubismName +
            ", firstEnabledCubismMainTexture=" + firstEnabledCubismMainTextureName +
            ", firstEnabledCubismDrawMaterial=" + firstEnabledCubismDrawMaterialName +
            ", firstEnabledCubismDrawShader=" + firstEnabledCubismDrawShaderName +
            ", firstEnabledCubismSkipRendering=" + firstEnabledCubismSkipRendering +
            ", firstEnabledCubismBoundsCenter=" + firstEnabledCubismBoundsCenter +
            ", firstEnabledCubismBoundsSize=" + firstEnabledCubismBoundsSize +
            ", firstEnabledCubismVertexCount=" + firstEnabledCubismVertexCount;

        var cameraInfo = "";

        if (mainCamera != null)
        {
            cameraInfo =
                "camera=" + mainCamera.name +
                ", active=" + mainCamera.gameObject.activeInHierarchy +
                ", enabled=" + mainCamera.enabled +
                ", position=" + mainCamera.transform.position +
                ", orthographic=" + mainCamera.orthographic +
                ", orthographicSize=" + mainCamera.orthographicSize +
                ", near=" + mainCamera.nearClipPlane +
                ", far=" + mainCamera.farClipPlane +
                ", cullingMask=" + mainCamera.cullingMask +
                ", clearFlags=" + mainCamera.clearFlags +
                ", background=" + mainCamera.backgroundColor;
        }
        else
        {
            cameraInfo = "Camera.main is null.";
            Debug.LogWarning("[DesktopPetRenderDebug] " + cameraInfo, this);
        }

        Debug.Log("[DesktopPetRenderDebug] " + modelInfo, this);
        Debug.Log("[DesktopPetRenderDebug] " + cubismInfo, this);
        Debug.Log("[DesktopPetRenderDebug] " + cameraInfo, mainCamera != null ? mainCamera : this);

        var debugText =
            "DesktopPetRenderDebug " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
            "\nlogFile=" + GetRenderStateLogFilePath() +
            (!string.IsNullOrEmpty(lastRenderRepairLog) ? "\n\n[Repair]\n" + lastRenderRepairLog : "") +
            (recentUnityLogs.Count > 0 ? "\n\n[UnityLog]\n" + string.Join("\n\n", recentUnityLogs) : "") +
            "\n\n[Model]\n" + FormatDebugBlock(modelInfo) +
            "\n\n[Cubism]\n" + FormatDebugBlock(cubismInfo) +
            "\n\n[Camera]\n" + FormatDebugBlock(cameraInfo);

        WriteRenderStateLogFile(debugText, true);
    }

    private static string FormatVector3(Vector3 value)
    {
        return "(" + value.x.ToString("0.###") + "," + value.y.ToString("0.###") + "," + value.z.ToString("0.###") + ")";
    }

    private static string FormatDebugBlock(string text)
    {
        return string.IsNullOrEmpty(text) ? "<empty>" : text.Replace(", ", "\n");
    }

    private string GetRenderStateLogFilePath()
    {
        var fileName = string.IsNullOrWhiteSpace(renderStateLogFileName)
            ? "DesktopPetRenderDebug.txt"
            : renderStateLogFileName;

        return Path.Combine(Application.persistentDataPath, fileName);
    }

    private void HookUnityLogFile()
    {
        if (unityLogHooked)
        {
            return;
        }

        unityLogHooked = true;
        Application.logMessageReceived += OnUnityLogMessageReceived;
        AppendUnityLogFile("Unity log hook enabled. platform=" + Application.platform + ", unity=" + Application.unityVersion);
    }

    private void UnhookUnityLogFile()
    {
        if (!unityLogHooked)
        {
            return;
        }

        Application.logMessageReceived -= OnUnityLogMessageReceived;
        unityLogHooked = false;
    }

    private void OnUnityLogMessageReceived(string condition, string stackTrace, LogType type)
    {
        if (type != LogType.Exception && type != LogType.Error && type != LogType.Warning)
        {
            return;
        }

        AppendUnityLogFile("[" + type + "] " + condition + "\n" + stackTrace);
    }

    private void AppendUnityLogFile(string text)
    {
        recentUnityLogs.Add(DateTime.Now.ToString("HH:mm:ss.fff") + "\n" + text);

        while (recentUnityLogs.Count > 12)
        {
            recentUnityLogs.RemoveAt(0);
        }
    }

    private void WriteRenderStateBootLog(string phase)
    {
        var text =
            "DesktopPetRenderDebug boot " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
            "\nphase=" + phase +
            "\nroot=" + name +
            "\nscene=" + gameObject.scene.name +
            "\nlogFile=" + GetRenderStateLogFilePath() +
            "\npersistentDataPath=" + Application.persistentDataPath +
            "\nplatform=" + Application.platform +
            "\nunityVersion=" + Application.unityVersion;

        WriteRenderStateLogFile(text, true);
    }

    private void WriteRenderStateLogFile(string text, bool force = false)
    {
        if (!force && !writeRenderStateToFile)
        {
            return;
        }

        try
        {
            var path = GetRenderStateLogFilePath();
            var directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, text);
            Debug.Log("[DesktopPetRenderDebug] wrote file: " + path, this);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[DesktopPetRenderDebug] failed to write file: " + exception.Message, this);
        }
    }

    public void RefreshRenderStateDebugText()
    {
        LogRenderState();
    }

    private IEnumerator LoadAudioFilesThenStartMusic()
    {
        if (audioFilesLoading)
        {
            yield break;
        }

        audioFilesLoading = true;
        audioFilesLoaded = false;

        yield return LoadAudioClipsFromFolder(prefabAudioFolder, clips =>
        {
            if (clips.Count > 0)
            {
                prefabVoiceClips = clips.ToArray();
                prefabVoiceIndex = 0;
            }
        });

        yield return LoadAudioClipsFromFolder(initMusicFolder, clips =>
        {
            if (clips.Count > 0)
            {
                musicClips = clips.ToArray();
                musicIndex = randomizeInitialMusic ? UnityEngine.Random.Range(0, musicClips.Length) : 0;
            }
        });

        audioFilesLoaded = true;
        audioFilesLoading = false;

        if (musicEnabled && !externalMusicProviderActive)
        {
            PlayNextMusic();
        }
    }

    private IEnumerator LoadAudioClipsFromFolder(string relativeOrAbsoluteFolder, Action<List<AudioClip>> onLoaded)
    {
        var clips = new List<AudioClip>();
        List<string> files = null;
        yield return DesktopPetResourcePath.ListPackagedModelFolderFiles(relativeOrAbsoluteFolder, loaded => files = loaded);

        if (files == null || files.Count == 0)
        {
            Debug.LogWarning("Audio folder has no packaged files: " + relativeOrAbsoluteFolder, this);
            onLoaded?.Invoke(clips);
            yield break;
        }

        for (var i = 0; i < files.Count; i++)
        {
            var path = files[i];
            var type = GetAudioType(path);
            if (type == AudioType.UNKNOWN)
            {
                continue;
            }

            using var request = UnityWebRequestMultimedia.GetAudioClip(DesktopPetResourcePath.ToRequestUrl(path), type);
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning("Failed to load audio file: " + path + " " + request.error, this);
                continue;
            }

            var clip = DownloadHandlerAudioClip.GetContent(request);
            if (clip == null)
            {
                continue;
            }

            clip.name = GetAudioDisplayName(path);
            clip.hideFlags = HideFlags.None;
            clips.Add(clip);
        }

        onLoaded?.Invoke(clips);
    }

    private static AudioType GetAudioType(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".wav":
                return AudioType.WAV;
            case ".mp3":
                return AudioType.MPEG;
            case ".ogg":
                return AudioType.OGGVORBIS;
            case ".aif":
            case ".aiff":
                return AudioType.AIFF;
            default:
                return AudioType.UNKNOWN;
        }
    }

    private static string GetAudioDisplayName(string path)
    {
        var cleanPath = path;
        var queryIndex = cleanPath.IndexOf('?');
        if (queryIndex >= 0)
        {
            cleanPath = cleanPath.Substring(0, queryIndex);
        }

        cleanPath = cleanPath.Replace('\\', '/');
        var slashIndex = cleanPath.LastIndexOf('/');
        if (slashIndex >= 0)
        {
            cleanPath = cleanPath.Substring(slashIndex + 1);
        }

        return Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(cleanPath));
    }

    private void HandlePointerTap()
    {
        if (!WasPointerPressedThisFrame())
        {
            return;
        }

        var screenPosition = GetPointerScreenPosition();
        if (useCustomHitAreas && TryHandleCustomHitArea(screenPosition))
        {
            return;
        }

        if (raycaster == null || Camera.main == null)
        {
            PlayPrefabVoice();
            return;
        }

        var ray = Camera.main.ScreenPointToRay(screenPosition);
        var hitCount = raycaster.Raycast(ray, raycastResults);

        for (var i = 0; i < hitCount; i++)
        {
            var hitDrawable = raycastResults[i].Drawable;
            var hitArea = hitDrawable != null ? hitDrawable.GetComponent<CubismHitDrawable>() : null;

            if (hitArea == null)
            {
                continue;
            }

            if (string.Equals(hitArea.Name, headHitAreaName, StringComparison.OrdinalIgnoreCase))
            {
                RandomExpression();
                return;
            }

            if (string.Equals(hitArea.Name, bodyHitAreaName, StringComparison.OrdinalIgnoreCase))
            {
                PlayRandomMotion(tapBodyMotions);
                PlayPrefabVoice();
                return;
            }
        }
    }

    private bool TryHandleCustomHitArea(Vector2 screenPosition)
    {
        var camera = Camera.main;
        if (camera == null)
        {
            return false;
        }

        var hitBoxes = FindObjectsByType<HitBoxCustom>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        HitBoxCustom bestHit = null;
        for (var i = 0; i < hitBoxes.Length; i++)
        {
            var hitBox = hitBoxes[i];
            if (hitBox == null || !hitBox.ContainsScreenPoint(camera, screenPosition))
            {
                continue;
            }

            if (bestHit == null || hitBox.Priority > bestHit.Priority)
            {
                bestHit = hitBox;
            }
        }

        if (bestHit == null)
        {
            return false;
        }

        ExecuteCustomHitAction(bestHit.Action);
        return true;
    }

    private void ExecuteCustomHitAction(HitBoxCustom.HitAction action)
    {
        switch (action)
        {
            case HitBoxCustom.HitAction.Head:
                RandomExpression();
                break;
            case HitBoxCustom.HitAction.Body:
                PlayRandomMotion(tapBodyMotions);
                PlayPrefabVoice();
                break;
            case HitBoxCustom.HitAction.PrefabVoice:
                PlayPrefabVoice();
                break;
            case HitBoxCustom.HitAction.RandomExpression:
                RandomExpression();
                break;
            case HitBoxCustom.HitAction.RandomMotion:
                PlayRandomMotion(tapBodyMotions);
                break;
        }
    }

    private void UpdateVoiceMouth()
    {
        if (voiceSource == null || !voiceSource.isPlaying)
        {
            SetParameter(mouthOpenParameter, 0f);
            return;
        }

        var react = GetVoiceMouthReaction();
        var mouthValue = Mathf.Clamp01(react * GetMoodProfile(currentMood).mouthOpen);
        SetParameter(mouthOpenParameter, mouthValue);
    }

    private void UpdateMoodShake()
    {
        var profile = GetMoodProfile(currentMood);
        var parameter = FindParameter(bodyAngleZParameter);
        if (parameter == null)
        {
            return;
        }

        var frameScale = Mathf.Max(0f, Time.deltaTime * bodyShakeReferenceFrameRate);

        if (Time.time < shakeUntil && profile.shakeRange > 0f)
        {
            isBodyShaking=true;
            var delta = bodyShakeDirection * bodyShakeStep * (profile.shakeRange - Mathf.Abs(currentBodyShake) + 0.1f) * frameScale;
            currentBodyShake = Mathf.Clamp(currentBodyShake + delta, -profile.shakeRange, profile.shakeRange);

            if (currentBodyShake >= profile.shakeRange)
            {
                bodyShakeDirection = -1f;
            }
            else if (currentBodyShake <= -profile.shakeRange)
            {
                bodyShakeDirection = 1f;
            }

            parameter.AddToValue(currentBodyShake);
            return;
        }

        if (Mathf.Abs(currentBodyShake) > bodyShakeReturnThreshold)
        {
            var returnDirection = currentBodyShake > 0f ? -bodyShakeStep : bodyShakeStep;
            var delta = returnDirection * Mathf.Abs(currentBodyShake) * frameScale;
            if (Mathf.Sign(currentBodyShake) != Mathf.Sign(currentBodyShake + delta))
            {
                currentBodyShake = 0f;
            }
            else
            {
                currentBodyShake += delta;
            }

            parameter.AddToValue(currentBodyShake);
            return;
        }

        currentBodyShake = 0f;
    }

    private void PrepareVoiceMouthTimeline(AudioClip clip)
    {
        voiceMouthTimeline = Array.Empty<float>();
        voiceMouthWindowSeconds = Mathf.Max(0.01f, mouthAnalysisWindowMs / 1000f);

        if (clip == null || clip.samples <= 0 || clip.channels <= 0 || clip.frequency <= 0)
        {
            return;
        }

        var sampleData = new float[clip.samples * clip.channels];
        if (!clip.GetData(sampleData, 0))
        {
            return;
        }

        var monoSamples = new float[clip.samples];
        for (var frame = 0; frame < clip.samples; frame++)
        {
            var sum = 0f;
            var baseIndex = frame * clip.channels;
            for (var channel = 0; channel < clip.channels; channel++)
            {
                sum += sampleData[baseIndex + channel];
            }

            monoSamples[frame] = sum / clip.channels;
        }

        var windowSize = Mathf.Max(1, Mathf.RoundToInt(clip.frequency * voiceMouthWindowSeconds));
        var windowCount = Mathf.CeilToInt((float)monoSamples.Length / windowSize);
        voiceMouthTimeline = new float[windowCount];

        var maxRms = 0f;
        for (var window = 0; window < windowCount; window++)
        {
            var start = window * windowSize;
            var end = Mathf.Min(start + windowSize, monoSamples.Length);
            var sumSquares = 0f;

            for (var i = start; i < end; i++)
            {
                sumSquares += monoSamples[i] * monoSamples[i];
            }

            var length = Mathf.Max(1, end - start);
            var rms = Mathf.Sqrt(sumSquares / length);
            voiceMouthTimeline[window] = rms;
            if (rms > maxRms)
            {
                maxRms = rms;
            }
        }

        if (maxRms <= 0f)
        {
            return;
        }

        for (var i = 0; i < voiceMouthTimeline.Length; i++)
        {
            voiceMouthTimeline[i] = Mathf.Clamp01(voiceMouthTimeline[i] / maxRms);
        }
    }

    private float GetVoiceMouthReaction()
    {
        if (voiceMouthTimeline == null || voiceMouthTimeline.Length == 0 || voiceMouthWindowSeconds <= 0f)
        {
            return 0f;
        }

        var index = Mathf.FloorToInt(voiceSource.time / voiceMouthWindowSeconds);
        index = Mathf.Clamp(index, 0, voiceMouthTimeline.Length - 1);
        return voiceMouthTimeline[index];
    }

    private void UpdateMusicLoop()
    {
        if (!musicEnabled || !autoPlayNextMusic || externalMusicProviderActive || musicSource == null || audioFilesLoading)
        {
            return;
        }

        if (!musicSource.isPlaying || IsMusicFinished)
        {
            PlayNextMusic();
        }
    }

    private void TrackMusicPlayback(AudioClip clip)
    {
        trackedMusicClip = clip;
        trackedMusicStartedAt = Time.unscaledTime;
        trackedMusicLength = clip != null ? clip.length : 0f;
    }

    private void ClearMusicPlaybackTracking()
    {
        trackedMusicClip = null;
        trackedMusicStartedAt = -1f;
        trackedMusicLength = 0f;
    }

    private bool ShouldRouteMusicToNetease()
    {
        return neteaseMusicClient != null && neteaseMusicClient.CanHandleMusic;
    }

    private void ApplyLive2DParameters()
    {
        UpdateVoiceMouth();
        UpdateMoodShake();
    }

    private void RefreshCubismUpdateController()
    {
        var updateController = GetComponent<CubismUpdateController>();
        if (updateController != null)
        {
            updateController.Refresh();
        }
    }

    private void PlayIdle()
    {
        if (motionController != null && idleMotion != null)
        {
            motionController.PlayAnimation(idleMotion, priority: CubismMotionPriority.PriorityIdle);
        }
    }

    private void PlayRandomMotion(AnimationClip[] clips)
    {
        if (motionController == null || clips == null || clips.Length == 0)
        {
            return;
        }

        var clip = clips[UnityEngine.Random.Range(0, clips.Length)];
        motionController.PlayAnimation(clip, isLoop: false, priority: CubismMotionPriority.PriorityNormal);
    }

    private bool ApplyExpression(string expressionName)
    {
        if (expressionController == null ||
            expressionController.ExpressionsList == null ||
            expressionController.ExpressionsList.CubismExpressionObjects == null)
        {
            return false;
        }

        var expressions = expressionController.ExpressionsList.CubismExpressionObjects;
        var candidates = GetExpressionNameCandidates(expressionName);
        for (var i = 0; i < expressions.Length; i++)
        {
            if (expressions[i] == null)
            {
                continue;
            }

            var objectName = expressions[i].name;
            var strippedName = StripExpressionExtension(objectName);
            for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                var candidate = candidates[candidateIndex];
                if (string.Equals(objectName, candidate, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(strippedName, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    expressionController.CurrentExpressionIndex = i;
                    return true;
                }
            }
        }

        return false;
    }

    private void SetDisplayedText(string text)
    {
        displayedText = text ?? "";
        DisplayTextChanged?.Invoke(displayedText);
    }

    private void SetParameter(string parameterId, float value)
    {
        var parameter = FindParameter(parameterId);
        if (parameter != null)
        {
            parameter.OverrideValue(value);
        }
    }

    private CubismParameter FindParameter(string parameterId)
    {
        if (model == null || string.IsNullOrEmpty(parameterId))
        {
            return null;
        }

        return model.Parameters.FindById(parameterId);
    }

    private void StartMoodMotion(float seconds)
    {
        var profile = GetMoodProfile(currentMood);
        if (profile.shakeRange > 0f && seconds > 3f)
        {
            bodyShakeDirection = 1f;
            shakeUntil = Time.time + seconds;
            Debug.Log("Mood body shake started. mood=\"" + currentMood + "\", range=" + profile.shakeRange + ", seconds=" + seconds.ToString("0.00"), this);
        }
    }

    private MoodProfile GetMoodProfile(string mood)
    {
        mood = NormalizeMoodText(mood);
        if (moodProfiles != null)
        {
            for (var i = 0; i < moodProfiles.Length; i++)
            {
                if (moodProfiles[i] != null &&
                    string.Equals(NormalizeMoodText(moodProfiles[i].mood), mood, StringComparison.OrdinalIgnoreCase))
                {
                    return moodProfiles[i];
                }
            }
        }

        return new MoodProfile { mood = mood, expressionName = mood, mouthOpen = 0.6f, shakeRange = 0f };
    }

    private static void ParseOldTemplateAnswer(string answer, out string cnText, out string mood, out string jaText)
    {
        var parts = answer.Split('|');
        var left = parts.Length > 0 ? parts[0].Trim() : answer.Trim();
        jaText = parts.Length > 1 ? parts[1].Trim() : "";

        var moodStart = left.IndexOf('*');
        var moodEnd = moodStart >= 0 ? left.IndexOf('*', moodStart + 1) : -1;

        if (moodStart >= 0 && moodEnd > moodStart)
        {
            cnText = left.Substring(0, moodStart).Trim();
            mood = NormalizeMoodText(left.Substring(moodStart + 1, moodEnd - moodStart - 1));
            return;
        }

        cnText = left.Trim();
        mood = "normal";
    }

    private static string NormalizeMoodText(string mood)
    {
        mood = (mood ?? "").Trim().Trim('*').Trim();
        if (string.Equals(mood, "\u5F7B\u5E95\u9ED1\u5316", StringComparison.OrdinalIgnoreCase))
        {
            return "\u5F7B\u5E95\u574F\u6389";
        }

        return string.Equals(mood, "\u59D4\u5C48\u60F3\u54ED", StringComparison.OrdinalIgnoreCase) ? "sad" : mood;
    }

    private static string StripExpressionExtension(string expressionName)
    {
        expressionName = (expressionName ?? "").Trim();
        if (expressionName.EndsWith(".exp3.asset", StringComparison.OrdinalIgnoreCase))
        {
            return expressionName.Substring(0, expressionName.Length - ".exp3.asset".Length);
        }

        if (expressionName.EndsWith(".exp3", StringComparison.OrdinalIgnoreCase))
        {
            return expressionName.Substring(0, expressionName.Length - ".exp3".Length);
        }

        return expressionName;
    }

    private static string[] GetExpressionNameCandidates(string expressionName)
    {
        expressionName = NormalizeMoodText(expressionName);
        var stripped = StripExpressionExtension(expressionName);
        var candidates = new List<string>
        {
            expressionName,
            stripped,
            stripped + ".exp3"
        };

        AddMoodExpressionAliases(stripped, candidates);
        return candidates.ToArray();
    }

    private static void AddMoodExpressionAliases(string mood, List<string> candidates)
    {
        if (string.Equals(mood, "happy", StringComparison.OrdinalIgnoreCase))
        {
            AddExpressionCandidate(candidates, "开心");
            AddExpressionCandidate(candidates, "开心.exp3");
            return;
        }

        if (string.Equals(mood, "angry", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mood, "气急败坏", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mood, "赌气", StringComparison.OrdinalIgnoreCase))
        {
            AddExpressionCandidate(candidates, "angry");
            AddExpressionCandidate(candidates, "angry.exp3");
            return;
        }

        if (string.Equals(mood, "彻底坏掉", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mood, "彻底黑化", StringComparison.OrdinalIgnoreCase))
        {
            AddExpressionCandidate(candidates, "黑化");
            AddExpressionCandidate(candidates, "黑化.exp3");
            AddExpressionCandidate(candidates, "expression16");
            AddExpressionCandidate(candidates, "expression16.exp3");
            return;
        }

        if (string.Equals(mood, "得意", StringComparison.OrdinalIgnoreCase))
        {
            AddExpressionCandidate(candidates, "expression17");
            AddExpressionCandidate(candidates, "expression17.exp3");
        }
    }

    private static void AddExpressionCandidate(List<string> candidates, string value)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        candidates.Add(value);
    }

    private static bool WasPointerPressedThisFrame()
    {
#if ENABLE_INPUT_SYSTEM
        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
        {
            return true;
        }

        if (Pointer.current != null)
        {
            return Pointer.current.press.wasPressedThisFrame;
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.touchCount > 0 && Input.GetTouch(0).phase == UnityEngine.TouchPhase.Began)
        {
            return true;
        }

        return Input.GetMouseButtonDown(0);
#else
        return false;
#endif
    }

    private static Vector3 GetPointerScreenPosition()
    {
#if ENABLE_INPUT_SYSTEM
        if (Touchscreen.current != null)
        {
            return Touchscreen.current.primaryTouch.position.ReadValue();
        }

        if (Pointer.current != null)
        {
            return Pointer.current.position.ReadValue();
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.touchCount > 0)
        {
            return Input.GetTouch(0).position;
        }

        return Input.mousePosition;
#else
        return Vector3.zero;
#endif
    }
}
