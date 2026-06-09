using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

/// <summary>
/// Unity-side Netease Cloud Music adapter that mirrors the old Python desktop pet behavior.
/// It expects an api-enhanced/NeteaseCloudMusicApi compatible HTTP service and keeps playlist loading on the client.
/// </summary>
public sealed class NeteaseCloudMusicClient : MonoBehaviour
{
    [Serializable]
    public sealed class MoodPlaylist
    {
        public string mood = "normal";
        public string playlistName = "萝卜子日常";
    }

    private sealed class Track
    {
        public string id;
        public string name;
        public bool played;
    }

    private sealed class AcceptAllCertificateHandler : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData)
        {
            return true;
        }
    }

    [Header("References")]
    [SerializeField] private DesktopPetMigratedController petController;
    [SerializeField] private AndroidNativeMusicBridge androidMusicBridge;

    [Header("API Enhanced")]
    [SerializeField] private string apiBaseUrl = "http://127.0.0.1:3000";
    [SerializeField] private DesktopPetServerConfig serverConfig;
    [SerializeField] private bool loadServerConfigOnStart = true;
    [SerializeField] private string cookieFilePath = "config/netease_cookie.json";
    [SerializeField] private bool initializeOnStart = true;
    [SerializeField] private bool requestCaptchaWhenCookieMissing = true;
    [SerializeField] private string loginCellphone = "";
    [SerializeField] private string loginCountryCode = "86";
    [SerializeField] private bool autoPlayWhenMusicEnds = true;
    [SerializeField] private AudioType downloadedAudioType = AudioType.MPEG;
    [SerializeField] private string tempMusicFileName = "desktop_pet_netease_music.mp3";
    [SerializeField] private bool preferHttpsSongUrl = true;
    [SerializeField] private bool retryApiWithCertificateBypass = true;
    [SerializeField] private float resumeAutoNextDelay = 2f;
    [SerializeField] private float nativeCompletionPollInterval = 2f;

    [Header("Old Template Playlists")]
    [SerializeField] private string likedPlaylistKey = "红心歌单";
    [SerializeField] private string likedPlaylistNameSuffix = "喜欢的音乐";
    [SerializeField] private string dailyPlaylistKey = "日推歌单";
    [SerializeField] private MoodPlaylist[] moodPlaylists =
    {
        new MoodPlaylist { mood = "happy", playlistName = "萝卜子高兴" },
        new MoodPlaylist { mood = "normal", playlistName = "萝卜子日常" },
        new MoodPlaylist { mood = "sad", playlistName = "萝卜子伤心" },
        new MoodPlaylist { mood = "angry", playlistName = "萝卜子生气" },
        new MoodPlaylist { mood = "shy", playlistName = "萝卜子害羞" },
        new MoodPlaylist { mood = "confuse", playlistName = "日推歌单" }
    };

    [Header("State")]
    [SerializeField] private bool musicOn = true;
    [SerializeField] private bool inLike;
    [SerializeField] private bool inDaily = true;

    [Header("Events")]
    public UnityEvent<string> StatusChanged;
    public UnityEvent<string> SongChanged;
    public UnityEvent CaptchaSent;
    public UnityEvent LoginSucceeded;
    public UnityEvent<string> LoginFailed;

    private readonly Dictionary<string, List<Track>> allMusic = new Dictionary<string, List<Track>>();
    private readonly Dictionary<string, string> moodToPlaylistName = new Dictionary<string, string>();
    private readonly Dictionary<string, string> playlistNameToId = new Dictionary<string, string>();
    private readonly Dictionary<string, string> cookieValues = new Dictionary<string, string>();
    private string resolvedCookiePath = "";
    private string cookieHeader = "";
    private string userName = "";
    private bool isNeteaseLoaded;
    private bool isInitializing;
    private bool isRequestingCaptcha;
    private bool isSubmittingCaptcha;
    private bool isChangingMusic;
    private bool nativeMusicPlaying;
    private bool useApiCertificateCompatibility;
    private bool certificateCompatibilityNoticeShown;
    private string nowMusicName = "";
    private float ignoreAutoNextUntil;
    private float nextNativeCompletionPollAt;

    public bool IsNeteaseLoaded
    {
        get { return isNeteaseLoaded; }
    }

    public bool InLike
    {
        get { return inLike; }
    }

    public bool InDaily
    {
        get { return inDaily; }
    }

    public bool MusicOn
    {
        get { return musicOn; }
    }

    public bool CanHandleMusic
    {
        get { return isNeteaseLoaded; }
    }

    private void Reset()
    {
        CacheController();
        CacheServerConfig();
    }

    private void Awake()
    {
        CacheController();
        CacheAndroidMusicBridge();
        CacheServerConfig();
        if (petController != null)
        {
            petController.SetNeteaseMusicClient(this);
        }

        RebuildMoodMap();
    }

    private void OnEnable()
    {
        BindAndroidMusicBridge();
    }

    private void OnDisable()
    {
        UnbindAndroidMusicBridge();
    }

    private void Start()
    {
        if (loadServerConfigOnStart)
        {
            StartCoroutine(LoadServerConfigThenInitialize());
            return;
        }

        if (initializeOnStart)
        {
            InitializeFromCookie();
        }
    }

    private void Update()
    {
        if (!musicOn ||
            !autoPlayWhenMusicEnds ||
            !isNeteaseLoaded ||
            isChangingMusic ||
            Time.unscaledTime < ignoreAutoNextUntil ||
            petController == null)
        {
            return;
        }

        if (UsesAndroidNativeMusic())
        {
            PollNativeMusicCompletion();
        }
        else if (petController.IsMusicFinished)
        {
            PlayRandomMusic();
        }
    }

    private void OnApplicationPause(bool paused)
    {
        DelayAutoNextCheck();
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        DelayAutoNextCheck();
    }

    public void InitializeFromCookie()
    {
        ApplyServerConfig();

        if (!isInitializing)
        {
            StartCoroutine(InitializeFromCookieRoutine());
        }
    }

    public void ToggleMusic()
    {
        SetMusicEnabled(!musicOn);
    }

    public void SetMusicEnabled(bool enabled)
    {
        musicOn = enabled;
        if (petController == null)
        {
            return;
        }

        petController.SetExternalMusicProviderActive(true);
        if (enabled)
        {
            if (UsesAndroidNativeMusic() && nativeMusicPlaying)
            {
                androidMusicBridge.Resume();
                return;
            }

            if (petController.HasPausableExternalMusic)
            {
                petController.ResumeExternalMusic();
            }
            else
            {
                PlayRandomMusic();
            }

            return;
        }

        petController.PauseExternalMusic();
        if (UsesAndroidNativeMusic())
        {
            androidMusicBridge.Pause();
        }
    }

    public void ToggleLikeMode()
    {
        inDaily = false;
        inLike = !inLike;
        NotifyStatus("Netease like mode: " + inLike);
        PlayRandomMusic();
    }

    public void ToggleDailyMode()
    {
        inLike = false;
        inDaily = !inDaily;
        NotifyStatus("Netease daily mode: " + inDaily);
        PlayRandomMusic();
    }

    public void UseMoodMode()
    {
        inLike = false;
        inDaily = false;
        NotifyStatus("Netease mood mode.");
    }

    public void PlayRandomMusic()
    {
        if (!musicOn)
        {
            return;
        }

        if (!isNeteaseLoaded)
        {
            NotifyStatus("Netease is not ready.");
            return;
        }

        if (!isChangingMusic)
        {
            StartCoroutine(PlayRandomMusicRoutine());
        }
    }

    public void ReloadNeteaseMusic()
    {
        if (!isInitializing)
        {
            allMusic.Clear();
            playlistNameToId.Clear();
            isNeteaseLoaded = false;
            InitializeFromCookie();
        }
    }

    public void SetApiBaseUrl(string value)
    {
        apiBaseUrl = NormalizeApiBaseUrl(value, apiBaseUrl);
        if (serverConfig != null)
        {
            serverConfig.SetNeteaseApiBaseUrl(apiBaseUrl);
        }
    }

    public void SetNeteaseCellphone(string value)
    {
        loginCellphone = (value ?? "").Trim();
        if (serverConfig != null)
        {
            serverConfig.SetNeteaseCellphone(loginCellphone);
        }
    }

    public void SetNeteaseCountryCode(string value)
    {
        value = (value ?? "").Trim();
        loginCountryCode = string.IsNullOrWhiteSpace(value) ? "86" : value;
        if (serverConfig != null)
        {
            serverConfig.SetNeteaseCountryCode(loginCountryCode);
        }
    }

    public void RequestNeteaseLoginCode()
    {
        if (!isRequestingCaptcha)
        {
            StartCoroutine(RequestNeteaseLoginCodeRoutine());
        }
    }

    public void SubmitNeteaseLoginCode(string captcha)
    {
        if (!isSubmittingCaptcha)
        {
            StartCoroutine(SubmitNeteaseLoginCodeRoutine(captcha));
        }
    }

    public void SubmitNeteaseLoginCode(string cellphone, string captcha)
    {
        SetNeteaseCellphone(cellphone);
        SubmitNeteaseLoginCode(captcha);
    }

    private void DelayAutoNextCheck()
    {
        ignoreAutoNextUntil = Time.unscaledTime + Mathf.Max(0f, resumeAutoNextDelay);
    }

    private IEnumerator InitializeFromCookieRoutine()
    {
        isInitializing = true;
        RebuildMoodMap();

        yield return DesktopPetResourcePath.EnsureWritableModelFile(cookieFilePath, false, path => resolvedCookiePath = path);
        if (string.IsNullOrWhiteSpace(resolvedCookiePath) || !File.Exists(resolvedCookiePath))
        {
            NotifyStatus("Netease cookie file not found: " + cookieFilePath);
            if (requestCaptchaWhenCookieMissing)
            {
                yield return RequestNeteaseLoginCodeRoutine();
            }

            isInitializing = false;
            yield break;
        }

        LoadCookieFile(resolvedCookiePath);
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            NotifyStatus("Netease cookie is empty.");
            isInitializing = false;
            yield break;
        }

        yield return RequestJson("/user/account", json =>
        {
            userName = json.SelectToken("profile.nickname")?.Value<string>() ?? "";
            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = json.SelectToken("body.profile.nickname")?.Value<string>() ?? "";
            }
        });

        if (string.IsNullOrWhiteSpace(userName))
        {
            NotifyStatus("Netease account load failed.");
            isInitializing = false;
            yield break;
        }

        yield return LoadUserPlaylists();
        yield return LoadOldTemplatePlaylist(likedPlaylistKey, userName + likedPlaylistNameSuffix);
        yield return LoadOldTemplatePlaylist(dailyPlaylistKey, dailyPlaylistKey);

        foreach (var pair in moodToPlaylistName)
        {
            yield return LoadOldTemplatePlaylist(pair.Key, pair.Value);
        }

        isNeteaseLoaded = true;
        isInitializing = false;
        if (petController != null)
        {
            petController.SetExternalMusicProviderActive(true);
        }

        NotifyStatus("Netease loaded. User: " + userName);
    }

    private IEnumerator RequestNeteaseLoginCodeRoutine()
    {
        isRequestingCaptcha = true;

        if (string.IsNullOrWhiteSpace(loginCellphone))
        {
            NotifyLoginFailed("Netease cookie missing. Set cellphone before requesting captcha.");
            isRequestingCaptcha = false;
            yield break;
        }

        var path = "/captcha/sent?phone=" + UnityWebRequest.EscapeURL(loginCellphone);
        if (!string.IsNullOrWhiteSpace(loginCountryCode) && !string.Equals(loginCountryCode, "86", StringComparison.Ordinal))
        {
            path += "&ctcode=" + UnityWebRequest.EscapeURL(loginCountryCode);
        }

        var ok = false;
        yield return RequestJsonWithoutCookie(path, json =>
        {
            ok = IsNeteaseOk(json);
        });

        if (ok)
        {
            NotifyStatus("Netease captcha sent.");
            CaptchaSent?.Invoke();
        }
        else
        {
            NotifyLoginFailed("Netease captcha request finished; please check phone/server response.");
        }

        isRequestingCaptcha = false;
    }

    private IEnumerator SubmitNeteaseLoginCodeRoutine(string captcha)
    {
        isSubmittingCaptcha = true;

        captcha = (captcha ?? "").Trim();
        if (string.IsNullOrWhiteSpace(loginCellphone) || string.IsNullOrWhiteSpace(captcha))
        {
            NotifyLoginFailed("Netease cellphone or captcha is empty.");
            isSubmittingCaptcha = false;
            yield break;
        }

        yield return EnsureCookiePathReady();

        var path = "/login/cellphone?phone=" + UnityWebRequest.EscapeURL(loginCellphone) +
                   "&captcha=" + UnityWebRequest.EscapeURL(captcha);
        if (!string.IsNullOrWhiteSpace(loginCountryCode) && !string.Equals(loginCountryCode, "86", StringComparison.Ordinal))
        {
            path += "&ctcode=" + UnityWebRequest.EscapeURL(loginCountryCode);
        }

        var loggedIn = false;
        yield return RequestJsonWithoutCookie(path, json =>
        {
            loggedIn = IsNeteaseOk(json) ||
                       json.SelectToken("account.id") != null ||
                       json.SelectToken("body.account.id") != null ||
                       json.SelectToken("profile.nickname") != null ||
                       json.SelectToken("body.profile.nickname") != null;
        });

        if (!loggedIn || string.IsNullOrWhiteSpace(cookieHeader))
        {
            NotifyLoginFailed("Netease captcha login failed.");
            isSubmittingCaptcha = false;
            yield break;
        }

        SaveCookieFile();
        NotifyStatus("Netease captcha login succeeded.");
        LoginSucceeded?.Invoke();
        isSubmittingCaptcha = false;
        ReloadNeteaseMusic();
    }

    private IEnumerator LoadUserPlaylists()
    {
        var userId = "";
        yield return RequestJson("/user/account", accountJson =>
        {
            userId = accountJson.SelectToken("account.id")?.Value<string>() ??
                     accountJson.SelectToken("body.account.id")?.Value<string>() ?? "";
        });

        if (!string.IsNullOrWhiteSpace(userId))
        {
            yield return LoadUserPlaylistById(userId);
        }
    }

    private IEnumerator LoadUserPlaylistById(string userId)
    {
        yield return RequestJson("/user/playlist?uid=" + UnityWebRequest.EscapeURL(userId), json =>
        {
            playlistNameToId.Clear();
            var playlists = json.SelectToken("playlist") as JArray ?? json.SelectToken("body.playlist") as JArray;
            if (playlists == null)
            {
                return;
            }

            foreach (var item in playlists)
            {
                var name = item["name"]?.Value<string>() ?? "";
                var id = item["id"]?.Value<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                {
                    playlistNameToId[name] = id;
                }
            }
        });
    }

    private IEnumerator LoadOldTemplatePlaylist(string key, string playlistName)
    {
        if (string.Equals(playlistName, dailyPlaylistKey, StringComparison.OrdinalIgnoreCase))
        {
            yield return LoadDailyPlaylist(key);
            yield break;
        }

        if (!playlistNameToId.TryGetValue(playlistName, out var playlistId))
        {
            allMusic[key] = new List<Track>();
            NotifyStatus("Netease playlist not found: " + playlistName);
            yield break;
        }

        yield return RequestJson("/playlist/detail?id=" + UnityWebRequest.EscapeURL(playlistId), json =>
        {
            var tracks = json.SelectToken("playlist.tracks") as JArray ??
                         json.SelectToken("body.playlist.tracks") as JArray;
            allMusic[key] = ParseTracks(tracks);
        });
    }

    private IEnumerator LoadDailyPlaylist(string key)
    {
        yield return RequestJson("/recommend/songs", json =>
        {
            var tracks = json.SelectToken("data.dailySongs") as JArray ??
                         json.SelectToken("body.data.dailySongs") as JArray;
            allMusic[key] = ParseTracks(tracks);
        });
    }

    private IEnumerator PlayRandomMusicRoutine()
    {
        isChangingMusic = true;

        var playlistKey = SelectPlaylistKey();
        if (!allMusic.TryGetValue(playlistKey, out var tracks) || tracks.Count == 0)
        {
            NotifyStatus("Netease playlist is empty: " + playlistKey);
            isChangingMusic = false;
            yield break;
        }

        var track = PickRandomTrack(tracks);
        if (track == null)
        {
            NotifyStatus("Netease has no legal track: " + playlistKey);
            isChangingMusic = false;
            yield break;
        }

        track.played = true;
        nowMusicName = track.name;
        NotifyStatus("Netease selected: " + nowMusicName);

        string songUrl = "";
        yield return RequestJson(
            "/song/url/v1?id=" + UnityWebRequest.EscapeURL(track.id) + "&level=standard",
            json =>
            {
                var data = json.SelectToken("data") as JArray ?? json.SelectToken("body.data") as JArray;
                if (data != null && data.Count > 0)
                {
                    songUrl = data[0]?["url"]?.Value<string>() ?? "";
                }
            });

        if (string.IsNullOrWhiteSpace(songUrl))
        {
            NotifyStatus("Netease song url is empty: " + nowMusicName);
            isChangingMusic = false;
            yield break;
        }

        yield return LoadAndPlayAudio(songUrl, nowMusicName);
        isChangingMusic = false;
    }

    private IEnumerator LoadAndPlayAudio(string songUrl, string songName)
    {
        CacheController();
        if (petController == null)
        {
            NotifyStatus("Pet controller is missing.");
            yield break;
        }

        var path = Path.Combine(Application.temporaryCachePath, tempMusicFileName);
        songUrl = NormalizeSongUrl(songUrl);

        using (var request = UnityWebRequest.Get(songUrl))
        {
            UnityWebRequestAsyncOperation operation;
            try
            {
                operation = request.SendWebRequest();
            }
            catch (InvalidOperationException exc)
            {
                NotifyStatus("Netease music download blocked by Unity connection policy: " + exc.Message);
                yield break;
            }

            yield return operation;
            if (request.result != UnityWebRequest.Result.Success)
            {
                NotifyStatus("Netease music download failed: " + request.error);
                yield break;
            }

            File.WriteAllBytes(path, request.downloadHandler.data);
        }

        if (UsesAndroidNativeMusic())
        {
            petController.StopUnityMusicSource();
            nativeMusicPlaying = true;
            nextNativeCompletionPollAt = Time.unscaledTime + Mathf.Max(0.5f, nativeCompletionPollInterval);
            androidMusicBridge.Play(path, songName);
            SongChanged?.Invoke(songName);
            NotifyStatus("Netease native playing: " + songName);
            yield break;
        }

        using var audioRequest = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, downloadedAudioType);
        yield return audioRequest.SendWebRequest();
        if (audioRequest.result != UnityWebRequest.Result.Success)
        {
            NotifyStatus("Netease audio decode failed: " + audioRequest.error);
            yield break;
        }

        var clip = DownloadHandlerAudioClip.GetContent(audioRequest);
        if (clip == null)
        {
            NotifyStatus("Netease audio clip is empty.");
            yield break;
        }

        clip.name = songName;
        clip.hideFlags = HideFlags.None;
        petController.PlayExternalMusic(clip, songName);
        SongChanged?.Invoke(songName);
        NotifyStatus("Netease playing: " + songName);
    }

    private string SelectPlaylistKey()
    {
        if (inLike)
        {
            return likedPlaylistKey;
        }

        if (inDaily)
        {
            return dailyPlaylistKey;
        }

        var mood = petController != null ? petController.CurrentMood : "normal";
        if (!string.IsNullOrWhiteSpace(mood) && allMusic.ContainsKey(mood))
        {
            return mood;
        }

        return allMusic.ContainsKey("normal") ? "normal" : dailyPlaylistKey;
    }

    private static Track PickRandomTrack(List<Track> tracks)
    {
        var legal = tracks.FindAll(track => track != null && !track.played);
        if (legal.Count == 0)
        {
            foreach (var track in tracks)
            {
                if (track != null)
                {
                    track.played = false;
                }
            }

            legal = tracks.FindAll(track => track != null && !track.played);
        }

        if (legal.Count == 0)
        {
            return null;
        }

        return legal[UnityEngine.Random.Range(0, legal.Count)];
    }

    private static List<Track> ParseTracks(JArray tracks)
    {
        var result = new List<Track>();
        if (tracks == null)
        {
            return result;
        }

        foreach (var item in tracks)
        {
            var id = item["id"]?.Value<string>() ?? "";
            var name = item["name"]?.Value<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(id))
            {
                result.Add(new Track { id = id, name = name, played = false });
            }
        }

        return result;
    }

    private IEnumerator RequestJson(string pathAndQuery, Action<JObject> onSuccess)
    {
        yield return RequestJsonCore(pathAndQuery, true, onSuccess);
    }

    private IEnumerator RequestJsonWithoutCookie(string pathAndQuery, Action<JObject> onSuccess)
    {
        yield return RequestJsonCore(pathAndQuery, false, onSuccess);
    }

    private IEnumerator RequestJsonCore(string pathAndQuery, bool includeCookie, Action<JObject> onSuccess)
    {
        var url = BuildApiUrl(pathAndQuery);
        var safePath = RedactSensitiveQuery(pathAndQuery);
        UnityWebRequest request = null;
        yield return SendApiRequest(url, includeCookie, useApiCertificateCompatibility, completedRequest => request = completedRequest);

        if (request == null)
        {
            NotifyStatus("Netease request failed: " + safePath + " request not created");
            yield break;
        }

        var shouldRetryCertificate = request.result != UnityWebRequest.Result.Success &&
                                     retryApiWithCertificateBypass &&
                                     !useApiCertificateCompatibility &&
                                     IsCertificateError(request.error);

        if (shouldRetryCertificate)
        {
            useApiCertificateCompatibility = true;
            NotifyCertificateHintIfNeeded(request.error, true);
            request.Dispose();
            request = null;

            NotifyStatus("Netease API certificate verification failed, retrying with compatibility mode: " + safePath);
            yield return SendApiRequest(url, includeCookie, true, completedRequest => request = completedRequest);
        }

        if (request == null)
        {
            NotifyStatus("Netease request failed: " + safePath + " compatibility request not created");
            yield break;
        }

        if (request.result != UnityWebRequest.Result.Success)
        {
            NotifyCertificateHintIfNeeded(request.error, false);
            NotifyStatus("Netease request failed: " + safePath + " " + BuildRequestErrorDetail(request));
            request.Dispose();
            yield break;
        }

        try
        {
            var json = JObject.Parse(request.downloadHandler.text);
            onSuccess?.Invoke(json);
        }
        catch (Exception exc)
        {
            NotifyStatus("Netease json parse failed: " + safePath + " " + exc.Message);
        }
        finally
        {
            request.Dispose();
        }
    }

    private IEnumerator SendApiRequest(string url, bool includeCookie, bool bypassCertificate, Action<UnityWebRequest> onCompleted)
    {
        var request = UnityWebRequest.Get(url);
        if (includeCookie)
        {
            request.SetRequestHeader("Cookie", cookieHeader);
        }

        if (bypassCertificate)
        {
            request.certificateHandler = new AcceptAllCertificateHandler();
            request.disposeCertificateHandlerOnDispose = true;
        }

        yield return request.SendWebRequest();
        UpdateCookieFromResponse(request);
        onCompleted?.Invoke(request);
    }

    private IEnumerator EnsureCookiePathReady()
    {
        if (!string.IsNullOrWhiteSpace(resolvedCookiePath))
        {
            yield break;
        }

        yield return DesktopPetResourcePath.EnsureWritableModelFile(cookieFilePath, false, path => resolvedCookiePath = path);
        if (string.IsNullOrWhiteSpace(resolvedCookiePath))
        {
            resolvedCookiePath = Path.IsPathRooted(cookieFilePath)
                ? cookieFilePath
                : DesktopPetResourcePath.GetWritableModelPath(cookieFilePath);
        }
    }

    private static bool IsNeteaseOk(JObject json)
    {
        if (json == null)
        {
            return false;
        }

        var code = json.SelectToken("code")?.Value<int?>() ??
                   json.SelectToken("body.code")?.Value<int?>() ??
                   json.SelectToken("data.code")?.Value<int?>();
        return code == 200;
    }

    private string BuildApiUrl(string pathAndQuery)
    {
        var baseUrl = apiBaseUrl.TrimEnd('/');
        if (pathAndQuery.StartsWith("/", StringComparison.Ordinal))
        {
            return baseUrl + pathAndQuery;
        }

        return baseUrl + "/" + pathAndQuery;
    }

    private static string BuildRequestErrorDetail(UnityWebRequest request)
    {
        var message = request.error ?? "";
        var body = request.downloadHandler != null ? request.downloadHandler.text : "";
        var summary = SummarizeResponseBody(body);
        return string.IsNullOrWhiteSpace(summary) ? message : message + " response=" + summary;
    }

    private static string SummarizeResponseBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        body = body.Trim();
        try
        {
            var json = JObject.Parse(body);
            var code = json.SelectToken("code")?.ToString() ??
                       json.SelectToken("body.code")?.ToString() ??
                       json.SelectToken("data.code")?.ToString();
            var message = json.SelectToken("message")?.ToString() ??
                          json.SelectToken("msg")?.ToString() ??
                          json.SelectToken("body.message")?.ToString() ??
                          json.SelectToken("body.msg")?.ToString() ??
                          json.SelectToken("data.message")?.ToString() ??
                          json.SelectToken("data.msg")?.ToString();
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(code))
            {
                parts.Add("code=" + code);
            }

            if (!string.IsNullOrWhiteSpace(message))
            {
                parts.Add("message=" + message);
            }

            if (parts.Count > 0)
            {
                return string.Join(", ", parts);
            }
        }
        catch
        {
        }

        body = body.Replace("\r", " ").Replace("\n", " ");
        return body.Length <= 200 ? body : body.Substring(0, 200) + "...";
    }

    private static string RedactSensitiveQuery(string pathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(pathAndQuery))
        {
            return pathAndQuery;
        }

        var questionIndex = pathAndQuery.IndexOf('?');
        if (questionIndex < 0)
        {
            return pathAndQuery;
        }

        var path = pathAndQuery.Substring(0, questionIndex);
        var query = pathAndQuery.Substring(questionIndex + 1);
        var parameters = query.Split('&');
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = parameters[i];
            var equalIndex = parameter.IndexOf('=');
            if (equalIndex <= 0)
            {
                continue;
            }

            var key = parameter.Substring(0, equalIndex);
            if (IsSensitiveQueryKey(key))
            {
                parameters[i] = key + "=***";
            }
        }

        return path + "?" + string.Join("&", parameters);
    }

    private static bool IsSensitiveQueryKey(string key)
    {
        return string.Equals(key, "phone", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "captcha", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "password", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerator LoadServerConfigThenInitialize()
    {
        CacheServerConfig();
        if (serverConfig != null && !serverConfig.IsLoaded)
        {
            yield return serverConfig.LoadRoutine();
        }

        ApplyServerConfig();
        if (initializeOnStart)
        {
            InitializeFromCookie();
        }
    }

    private void ApplyServerConfig()
    {
        CacheServerConfig();
        if (serverConfig != null && serverConfig.IsLoaded)
        {
            apiBaseUrl = NormalizeApiBaseUrl(serverConfig.NeteaseApiBaseUrl, apiBaseUrl);
            loginCellphone = (serverConfig.NeteaseCellphone ?? "").Trim();
            loginCountryCode = string.IsNullOrWhiteSpace(serverConfig.NeteaseCountryCode)
                ? "86"
                : serverConfig.NeteaseCountryCode.Trim();
        }
    }

    private void CacheServerConfig()
    {
        if (serverConfig == null)
        {
            serverConfig = GetComponent<DesktopPetServerConfig>();
        }

        if (serverConfig == null)
        {
            serverConfig = GetComponentInParent<DesktopPetServerConfig>();
        }

        if (serverConfig == null)
        {
            serverConfig = FindFirstObjectByType<DesktopPetServerConfig>();
        }

        if (serverConfig == null)
        {
            serverConfig = gameObject.AddComponent<DesktopPetServerConfig>();
        }
    }

    private void CacheAndroidMusicBridge()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (androidMusicBridge == null)
        {
            androidMusicBridge = AndroidNativeMusicBridge.Instance;
        }
#endif
    }

    private void BindAndroidMusicBridge()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        CacheAndroidMusicBridge();
        if (androidMusicBridge == null)
        {
            return;
        }

        androidMusicBridge.MusicCompleted.RemoveListener(HandleNativeMusicCompleted);
        androidMusicBridge.MusicCompleted.AddListener(HandleNativeMusicCompleted);
        androidMusicBridge.MusicError.RemoveListener(HandleNativeMusicError);
        androidMusicBridge.MusicError.AddListener(HandleNativeMusicError);
#endif
    }

    private void UnbindAndroidMusicBridge()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (androidMusicBridge == null)
        {
            return;
        }

        androidMusicBridge.MusicCompleted.RemoveListener(HandleNativeMusicCompleted);
        androidMusicBridge.MusicError.RemoveListener(HandleNativeMusicError);
#endif
    }

    private bool UsesAndroidNativeMusic()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        return androidMusicBridge != null;
#else
        return false;
#endif
    }

    private void HandleNativeMusicCompleted()
    {
        nativeMusicPlaying = false;
        if (musicOn && autoPlayWhenMusicEnds)
        {
            PlayRandomMusic();
        }
    }

    private void HandleNativeMusicError(string error)
    {
        nativeMusicPlaying = false;
        NotifyStatus("Native music error: " + error);
    }

    private void PollNativeMusicCompletion()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!nativeMusicPlaying || androidMusicBridge == null || Time.unscaledTime < nextNativeCompletionPollAt)
        {
            return;
        }

        nextNativeCompletionPollAt = Time.unscaledTime + Mathf.Max(0.5f, nativeCompletionPollInterval);
        if (!androidMusicBridge.IsPlaying())
        {
            NotifyStatus("Netease native completed by poll.");
            HandleNativeMusicCompleted();
        }
#endif
    }

    private static string NormalizeApiBaseUrl(string value, string fallback)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (value.IndexOf("://", StringComparison.Ordinal) < 0)
        {
            value = "http://" + value;
        }

        return value.TrimEnd('/');
    }

    private string NormalizeSongUrl(string songUrl)
    {
        if (preferHttpsSongUrl && songUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "https://" + songUrl.Substring("http://".Length);
        }

        return songUrl;
    }

    private static bool IsCertificateError(string error)
    {
        return !string.IsNullOrWhiteSpace(error) &&
               (error.IndexOf("Cert verify failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                error.IndexOf("SSL CA certificate error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                error.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private void RebuildMoodMap()
    {
        moodToPlaylistName.Clear();
        if (moodPlaylists == null)
        {
            return;
        }

        foreach (var item in moodPlaylists)
        {
            if (item != null && !string.IsNullOrWhiteSpace(item.mood) && !string.IsNullOrWhiteSpace(item.playlistName))
            {
                moodToPlaylistName[item.mood] = item.playlistName;
            }
        }
    }

    private void CacheController()
    {
        if (petController == null)
        {
            petController = GetComponent<DesktopPetMigratedController>();
        }

        if (petController == null)
        {
            petController = GetComponentInParent<DesktopPetMigratedController>();
        }

        if (petController == null)
        {
            petController = GetComponentInChildren<DesktopPetMigratedController>();
        }

        if (petController == null)
        {
            petController = FindFirstObjectByType<DesktopPetMigratedController>();
        }
    }

    private void LoadCookieFile(string path)
    {
        cookieValues.Clear();
        cookieHeader = "";

        var text = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!text.StartsWith("{", StringComparison.Ordinal))
        {
            cookieHeader = text;
            MergeCookieHeader(text);
            return;
        }

        var json = JObject.Parse(text);
        var cookieToken = json["cookie"];
        if (cookieToken == null)
        {
            return;
        }

        if (cookieToken.Type == JTokenType.String)
        {
            cookieHeader = cookieToken.Value<string>() ?? "";
            MergeCookieHeader(cookieHeader);
            return;
        }

        if (cookieToken.Type == JTokenType.Object)
        {
            foreach (var prop in ((JObject)cookieToken).Properties())
            {
                cookieValues[prop.Name] = prop.Value.Value<string>() ?? "";
            }

            cookieHeader = BuildCookieHeader();
        }
    }

    private void UpdateCookieFromResponse(UnityWebRequest request)
    {
        var setCookie = request.GetResponseHeader("Set-Cookie");
        if (string.IsNullOrWhiteSpace(setCookie))
        {
            return;
        }

        if (!MergeSetCookieHeader(setCookie))
        {
            return;
        }

        cookieHeader = BuildCookieHeader();
        SaveCookieFile();
        NotifyStatus("Netease cookie refreshed.");
    }

    private bool MergeSetCookieHeader(string setCookie)
    {
        var changed = false;
        var segments = setCookie.Split(',');
        for (var i = 0; i < segments.Length; i++)
        {
            var firstPart = segments[i].Split(';')[0].Trim();
            if (string.IsNullOrWhiteSpace(firstPart) || !firstPart.Contains("="))
            {
                continue;
            }

            var index = firstPart.IndexOf('=');
            var key = firstPart.Substring(0, index).Trim();
            var value = firstPart.Substring(index + 1).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!cookieValues.TryGetValue(key, out var oldValue) || oldValue != value)
            {
                cookieValues[key] = value;
                changed = true;
            }
        }

        return changed;
    }

    private void MergeCookieHeader(string header)
    {
        var parts = header.Split(';');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (string.IsNullOrWhiteSpace(part) || !part.Contains("="))
            {
                continue;
            }

            var index = part.IndexOf('=');
            var key = part.Substring(0, index).Trim();
            var value = part.Substring(index + 1).Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                cookieValues[key] = value;
            }
        }
    }

    private string BuildCookieHeader()
    {
        var parts = new List<string>();
        foreach (var pair in cookieValues)
        {
            parts.Add(pair.Key + "=" + pair.Value);
        }

        return string.Join("; ", parts);
    }

    private void SaveCookieFile()
    {
        if (string.IsNullOrWhiteSpace(resolvedCookiePath))
        {
            return;
        }

        try
        {
            var json = new JObject();
            var cookieObject = new JObject();
            foreach (var pair in cookieValues)
            {
                cookieObject[pair.Key] = pair.Value;
            }

            json["cookie"] = cookieObject;
            var tempPath = resolvedCookiePath + ".tmp";
            File.WriteAllText(tempPath, json.ToString(), System.Text.Encoding.UTF8);
            File.Copy(tempPath, resolvedCookiePath, true);
            File.Delete(tempPath);
        }
        catch (Exception exc)
        {
            NotifyStatus("Netease cookie save failed: " + exc.Message);
        }
    }

    private void NotifyStatus(string message)
    {
        Debug.Log(message, this);
        StatusChanged?.Invoke(message);
    }

    private void NotifyLoginFailed(string message)
    {
        NotifyStatus(message);
        LoginFailed?.Invoke(message);
    }

    private void NotifyCertificateHintIfNeeded(string error, bool willRetry)
    {
        if (!IsCertificateError(error) || certificateCompatibilityNoticeShown)
        {
            return;
        }

        certificateCompatibilityNoticeShown = true;
        if (willRetry)
        {
            NotifyStatus("Netease HTTPS certificate verification failed. Retrying API requests with certificate compatibility mode.");
        }
        else if (retryApiWithCertificateBypass)
        {
            NotifyStatus("Netease HTTPS certificate verification failed. Certificate compatibility mode did not resolve the request.");
        }
        else
        {
            NotifyStatus("Netease HTTPS certificate verification failed. Enable certificate compatibility mode or use an HTTP/local API endpoint.");
        }
    }
}
