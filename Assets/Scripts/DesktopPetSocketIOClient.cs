using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using SocketIOClient;
using SocketIOClient.Newtonsoft.Json;
using SocketIOClient.Transport;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Socket.IO adapter for the old Python desktop-pet server.
/// It sends DesktopPetChatBridge socket payloads and forwards ack responses back to the chat bridge.
/// </summary>
public sealed class DesktopPetSocketIOClient : MonoBehaviour
{
    [Serializable]
    private sealed class VoiceProduceSocketPayload
    {
        public VoiceProduceData data;
        public string username;
    }

    [Serializable]
    private sealed class VoiceProduceData
    {
        public string text;
        public string text_lang;
        public string ref_audio_name;
        public string prompt_text;
        public string prompt_lang;
        public int top_k = 15;
        public float top_p = 1f;
        public float temperature = 1f;
        public string text_split_method = "cut1";
        public string pet_id;
        public string return_format;
    }

    [Serializable]
    private sealed class SocketFailurePayload
    {
        public string error = "";
        public string detail = "";
    }

    [Header("Server")]
    [SerializeField] private string serverUrl = "http://127.0.0.1:5000";
    [SerializeField] private DesktopPetServerConfig serverConfig;
    [SerializeField] private bool loadServerConfigOnStart = true;
    [SerializeField] private EngineIO engineIoVersion = EngineIO.V4;
    [SerializeField] private TransportProtocol transport = TransportProtocol.WebSocket;
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private float socketAnswerTimeoutSeconds = 90f;

    [Header("Bridge")]
    [SerializeField] private DesktopPetChatBridge chatBridge;
    [SerializeField] private bool bindOnEnable = true;

    [Header("Voice Synthesis")]
    [SerializeField] private DesktopPetMigratedController petController;
    [SerializeField] private string username = "";
    [SerializeField] private string petId = "";
    [SerializeField] private string voiceProduceEventName = "voice_produce";
    [SerializeField] private string refAudioSuffix = ".wav";
    [SerializeField] private string textLanguage = "\u65e5\u82f1\u6df7\u5408";
    [SerializeField] private string promptLanguage = "\u65e5\u82f1\u6df7\u5408";
    [SerializeField] private float voiceRequestTimeoutSeconds = 75f;

    [Header("Debug")]
    [SerializeField] private bool logTraffic = true;
    [SerializeField] private bool writeVoiceLogToRenderDebugFile = true;
    [SerializeField] private string sharedDebugLogFileName = "DesktopPetRenderDebug.txt";
    [SerializeField] private string testVoiceText = "こんにちは";
    [SerializeField] private string testVoiceMood = "normal";

    private SocketIOUnity socket;
    private bool bridgeBound;
    private bool isLoadingServerConfig;
    private int socketRequestSerial;
    private int activeSocketRequestId;
    private Coroutine socketTimeoutCoroutine;
    private int voiceRequestSerial;
    private int activeVoiceRequestId;
    private Coroutine voiceTimeoutCoroutine;
    private readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
    private readonly ConcurrentQueue<Action> pendingSocketActions = new ConcurrentQueue<Action>();

    public bool IsConnected
    {
        get { return socket != null && socket.Connected; }
    }

    private void Reset()
    {
        CacheBridge();
        CacheServerConfig();
    }

    private void Awake()
    {
        CacheBridge();
        CachePetController();
        CacheServerConfig();
    }

    private void OnEnable()
    {
        if (bindOnEnable)
        {
            BindBridge();
        }
    }

    private void Start()
    {
        if (loadServerConfigOnStart)
        {
            StartCoroutine(LoadServerConfigThenConnect());
            return;
        }

        if (connectOnStart)
        {
            Connect();
        }
    }

    private void Update()
    {
        while (mainThreadActions.TryDequeue(out var action))
        {
            action?.Invoke();
        }
    }

    private async void OnDisable()
    {
        UnbindBridge();

        if (socket != null && socket.Connected)
        {
            await socket.DisconnectAsync();
        }
    }

    private void OnDestroy()
    {
        socket?.Dispose();
        socket = null;
    }

    public void Connect()
    {
        ApplyServerConfig();

        if (socket != null)
        {
            if (socket.Connected)
            {
                return;
            }

            socket.Connect();
            return;
        }

        socket = new SocketIOUnity(new Uri(serverUrl), new SocketIOOptions
        {
            EIO = engineIoVersion,
            Transport = transport
        });
        socket.JsonSerializer = new NewtonsoftJsonSerializer();

        socket.OnConnected += (_, _) =>
        {
            EnqueueMain(() =>
            {
                Log("Socket.IO connected.");
                FlushPendingSocketActions();
            });
        };
        socket.OnDisconnected += (_, reason) => Log("Socket.IO disconnected: " + reason);
        socket.OnError += (_, error) => EnqueueMain(() => ReportError("Socket.IO error: " + error));
        socket.OnReconnectAttempt += (_, attempt) => Log("Socket.IO reconnect attempt: " + attempt);

        socket.Connect();
    }

    public void SetServerUrl(string value)
    {
        serverUrl = NormalizeServerUrl(value, serverUrl);
        if (serverConfig != null)
        {
            serverConfig.SetSocketServerUrl(serverUrl);
        }

        if (socket != null)
        {
            socket.Disconnect();
            socket.Dispose();
            socket = null;
        }
    }

    public void Disconnect()
    {
        socket?.Disconnect();
    }

    public void BindBridge()
    {
        CacheBridge();

        if (bridgeBound || chatBridge == null)
        {
            return;
        }

        chatBridge.SocketAnswerRequested.AddListener(SendSocketRequest);
        chatBridge.VoiceSynthesisRequested.AddListener(SendVoiceSynthesisRequest);
        bridgeBound = true;
    }

    public void UnbindBridge()
    {
        if (!bridgeBound || chatBridge == null)
        {
            return;
        }

        chatBridge.SocketAnswerRequested.RemoveListener(SendSocketRequest);
        chatBridge.VoiceSynthesisRequested.RemoveListener(SendVoiceSynthesisRequest);
        bridgeBound = false;
    }

    public void SendSocketRequest(string eventName, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            ReportError("Socket.IO event name is empty.");
            return;
        }

        var payload = JsonUtility.FromJson<DesktopPetChatBridge.SocketCallPayload>(payloadJson);
        var requestId = BeginSocketRequestTimeout(eventName);

        if (socket == null || !socket.Connected)
        {
            Connect();
            pendingSocketActions.Enqueue(() =>
            {
                if (requestId == activeSocketRequestId)
                {
                    EmitSocketRequest(eventName, payloadJson, payload, requestId);
                }
            });
            Log("Socket.IO request queued until connected: " + eventName);
            return;
        }

        EmitSocketRequest(eventName, payloadJson, payload, requestId);
    }

    private void EmitSocketRequest(string eventName, string payloadJson, DesktopPetChatBridge.SocketCallPayload payload, int requestId)
    {
        if (socket == null || !socket.Connected)
        {
            ReportSocketAckError(requestId, eventName, "Socket.IO client is not connected.");
            return;
        }

        Log("Socket.IO emit: " + eventName + " " + payloadJson);

        socket.Emit(eventName, response => HandleSocketAnswerAck(requestId, eventName, response), payload);
    }

    public void SendVoiceSynthesisRequest(string jaText, string mood)
    {
        if (string.IsNullOrWhiteSpace(jaText))
        {
            ReportError("Voice synthesis skipped because Japanese text is empty.");
            return;
        }

        if (string.IsNullOrWhiteSpace(petId))
        {
            ReportError("Pet Id is empty. voice_produce needs this to find server_pets/{pet_id}/voice_refs/{mood}.wav.");
            return;
        }

        var payload = new VoiceProduceSocketPayload
        {
            username = username,
            data = new VoiceProduceData
            {
                text = jaText,
                text_lang = textLanguage,
                ref_audio_name = BuildRefAudioName(mood),
                prompt_text = "",
                prompt_lang = promptLanguage,
                pet_id = petId,
                return_format = "wav"
            }
        };

        var requestId = BeginVoiceRequestTimeout();

        if (socket == null || !socket.Connected)
        {
            Connect();
            pendingSocketActions.Enqueue(() =>
            {
                if (requestId == activeVoiceRequestId)
                {
                    EmitVoiceSynthesisRequest(payload, jaText, mood, requestId);
                }
            });
            Log("Socket.IO voice request queued until connected.");
            return;
        }

        EmitVoiceSynthesisRequest(payload, jaText, mood, requestId);
    }

    private void EmitVoiceSynthesisRequest(VoiceProduceSocketPayload payload, string jaText, string mood, int requestId)
    {
        if (socket == null || !socket.Connected)
        {
            ReportVoiceError(requestId, "Socket.IO client is not connected; cannot request voice synthesis.");
            return;
        }

        Log("Socket.IO emit: " + voiceProduceEventName + " " + JsonUtility.ToJson(payload));

        socket.Emit(voiceProduceEventName, response => HandleVoiceSynthesisAck(requestId, response, jaText, mood), payload);
    }

    public void TestVoiceSynthesis()
    {
        SendVoiceSynthesisRequest(testVoiceText, testVoiceMood);
    }

    private void HandleSocketAnswerAck(int requestId, string eventName, SocketIOResponse response)
    {
        if (requestId != activeSocketRequestId)
        {
            Log("Socket.IO ack ignored because request is no longer active. requestId=" + requestId + ", active=" + activeSocketRequestId);
            return;
        }

        if (!TryGetFirstRawJson(response, out var answerJson, out var error))
        {
            EnqueueMain(() => ReportSocketAckError(requestId, eventName, error));
            return;
        }

        Log("Socket.IO ack: " + answerJson);

        EnqueueMain(() =>
        {
            if (requestId != activeSocketRequestId)
            {
                return;
            }

            CompleteSocketRequest(requestId);
            if (chatBridge == null)
            {
                ReportError("Chat bridge is missing; cannot consume Socket.IO answer: " + eventName);
                return;
            }

            chatBridge.ReceiveSocketAnswerJson(answerJson);
        });
    }

    private void HandleVoiceSynthesisAck(int requestId, SocketIOResponse response, string jaText, string mood)
    {
        if (requestId != activeVoiceRequestId)
        {
            Log("voice_produce ack ignored because request is no longer active. requestId=" + requestId + ", active=" + activeVoiceRequestId);
            return;
        }

        LogVoiceAck(response);

        if (!TryExtractVoiceBytes(response, out var wavBytes, out var error))
        {
            EnqueueMain(() => ReportVoiceError(requestId, error));
            return;
        }

        Log("voice_produce received " + wavBytes.Length + " bytes.");
        EnqueueMain(() =>
        {
            if (requestId == activeVoiceRequestId)
            {
                StartCoroutine(LoadAndSpeakVoice(wavBytes, jaText, mood, requestId));
            }
        });
    }

    private void ReportSocketAckError(int requestId, string eventName, string message)
    {
        if (requestId != activeSocketRequestId)
        {
            Log("Socket.IO ack error ignored because request is no longer active. requestId=" + requestId + ", active=" + activeSocketRequestId);
            return;
        }

        CompleteSocketRequest(requestId);
        ReportError("Failed to process Socket.IO ack: " + eventName + ". " + message);
    }

    private void CacheBridge()
    {
        if (chatBridge == null)
        {
            chatBridge = GetComponent<DesktopPetChatBridge>();
        }

        if (chatBridge == null)
        {
            chatBridge = GetComponentInParent<DesktopPetChatBridge>();
        }

        if (chatBridge == null)
        {
            chatBridge = GetComponentInChildren<DesktopPetChatBridge>();
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

    private IEnumerator LoadServerConfigThenConnect()
    {
        if (isLoadingServerConfig)
        {
            yield break;
        }

        isLoadingServerConfig = true;
        CacheServerConfig();
        if (serverConfig != null && !serverConfig.IsLoaded)
        {
            yield return serverConfig.LoadRoutine();
        }

        ApplyServerConfig();
        isLoadingServerConfig = false;

        if (connectOnStart)
        {
            Connect();
        }
    }

    private void ApplyServerConfig()
    {
        CacheServerConfig();
        if (serverConfig != null && serverConfig.IsLoaded)
        {
            serverUrl = NormalizeServerUrl(serverConfig.SocketServerUrl, serverUrl);
        }
    }

    private static string NormalizeServerUrl(string value, string fallback)
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

    private void CachePetController()
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

        if (petController == null && chatBridge != null)
        {
            petController = FindFirstObjectByType<DesktopPetMigratedController>();
        }
    }

    private IEnumerator LoadAndSpeakVoice(byte[] wavBytes, string jaText, string mood, int requestId)
    {
        CachePetController();

        if (petController == null)
        {
            ReportVoiceError(requestId, "Pet controller is missing; cannot play synthesized voice.");
            yield break;
        }

        var path = Path.Combine(Application.temporaryCachePath, "desktop_pet_voice_" + requestId + ".wav");
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, wavBytes);
        Log("Saved synthesized voice to temp file: " + path);

        using var request = UnityWebRequestMultimedia.GetAudioClip(DesktopPetResourcePath.ToRequestUrl(path), AudioType.WAV);
        yield return request.SendWebRequest();

        if (requestId != activeVoiceRequestId)
        {
            yield break;
        }

        if (request.result != UnityWebRequest.Result.Success)
        {
            ReportVoiceError(requestId, "Failed to decode synthesized voice: " + request.error);
            yield break;
        }

        var clip = DownloadHandlerAudioClip.GetContent(request);
        if (clip == null)
        {
            ReportVoiceError(requestId, "Decoded synthesized voice is empty.");
            yield break;
        }

        clip.name = "SynthesizedVoice";
        clip.hideFlags = HideFlags.None;
        if (chatBridge != null)
        {
            chatBridge.FlushPendingAiLogMessage();
        }

        petController.Speak(clip, "", mood);
        CompleteVoiceRequest(requestId);
        Log("Synthesized voice playback requested. Length: " + clip.length.ToString("0.00") + "s.");
    }

    private int BeginVoiceRequestTimeout()
    {
        var requestId = ++voiceRequestSerial;
        activeVoiceRequestId = requestId;

        if (voiceTimeoutCoroutine != null)
        {
            StopCoroutine(voiceTimeoutCoroutine);
        }

        voiceTimeoutCoroutine = StartCoroutine(VoiceRequestTimeoutRoutine(requestId));
        return requestId;
    }

    private IEnumerator VoiceRequestTimeoutRoutine(int requestId)
    {
        yield return new WaitForSecondsRealtime(Mathf.Max(1f, voiceRequestTimeoutSeconds));

        if (requestId != activeVoiceRequestId)
        {
            yield break;
        }

        activeVoiceRequestId = 0;
        voiceTimeoutCoroutine = null;
        ReportError("Voice synthesis timed out after " + voiceRequestTimeoutSeconds.ToString("0") + " seconds.");
    }

    private void CompleteVoiceRequest(int requestId)
    {
        if (requestId != activeVoiceRequestId)
        {
            return;
        }

        StopVoiceTimeout(requestId);
        activeVoiceRequestId = 0;
    }

    private void StopVoiceTimeout(int requestId)
    {
        if (requestId != activeVoiceRequestId)
        {
            return;
        }

        if (voiceTimeoutCoroutine != null)
        {
            StopCoroutine(voiceTimeoutCoroutine);
            voiceTimeoutCoroutine = null;
        }
    }

    private void ReportVoiceError(int requestId, string message)
    {
        if (requestId != 0 && requestId != activeVoiceRequestId)
        {
            Log("Voice error ignored because request is no longer active. requestId=" + requestId + ", active=" + activeVoiceRequestId + ", message=" + message);
            return;
        }

        CompleteVoiceRequest(requestId);
        ReportError(message);
    }

    private int BeginSocketRequestTimeout(string eventName)
    {
        var requestId = ++socketRequestSerial;
        activeSocketRequestId = requestId;

        if (socketTimeoutCoroutine != null)
        {
            StopCoroutine(socketTimeoutCoroutine);
        }

        socketTimeoutCoroutine = StartCoroutine(SocketRequestTimeoutRoutine(requestId, eventName));
        return requestId;
    }

    private IEnumerator SocketRequestTimeoutRoutine(int requestId, string eventName)
    {
        yield return new WaitForSecondsRealtime(Mathf.Max(1f, socketAnswerTimeoutSeconds));

        if (requestId != activeSocketRequestId)
        {
            yield break;
        }

        activeSocketRequestId = 0;
        socketTimeoutCoroutine = null;
        ReportError("Socket.IO request timed out: " + eventName + " after " + socketAnswerTimeoutSeconds.ToString("0") + " seconds.");
    }

    private void CompleteSocketRequest(int requestId)
    {
        if (requestId != activeSocketRequestId)
        {
            return;
        }

        activeSocketRequestId = 0;
        if (socketTimeoutCoroutine != null)
        {
            StopCoroutine(socketTimeoutCoroutine);
            socketTimeoutCoroutine = null;
        }
    }

    private static bool TryExtractVoiceBytes(SocketIOResponse response, out byte[] wavBytes, out string error)
    {
        return TryExtractBinaryBytes(response, "voice_produce", out wavBytes, out error);
    }

    private static bool TryGetFirstRawJson(SocketIOResponse response, out string rawJson, out string error)
    {
        rawJson = "";
        error = "";

        if (response == null || response.Count == 0)
        {
            error = "ack is empty.";
            return false;
        }

        rawJson = response.GetValue().GetRawText();
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            error = "ack json is empty.";
            return false;
        }

        return true;
    }

    private static bool TryExtractBinaryBytes(SocketIOResponse response, string eventName, out byte[] bytes, out string error)
    {
        bytes = null;
        error = "";

        if (response == null)
        {
            error = eventName + " ack is empty.";
            return false;
        }

        if (response.InComingBytes != null && response.InComingBytes.Count > 0)
        {
            bytes = response.InComingBytes[0];
            if (bytes != null && bytes.Length > 0)
            {
                if (!LooksLikeWav(bytes))
                {
                    error = eventName + " returned binary data, but it is not a WAV file. " +
                            "Unity requested return_format=wav; please confirm the server version supports WAV return. " +
                            "Header: " + FormatBytes(bytes, 16);
                    return false;
                }

                return true;
            }

            error = eventName + " binary ack was empty.";
            return false;
        }

        if (response.Count == 0)
        {
            error = eventName + " ack has no binary data or json payload.";
            return false;
        }

        try
        {
            bytes = response.GetValue<byte[]>();
            if (bytes != null && bytes.Length > 0)
            {
                if (!LooksLikeWav(bytes))
                {
                    error = eventName + " returned byte[] data, but it is not a WAV file. " +
                            "Unity requested return_format=wav; please confirm the server version supports WAV return. " +
                            "Header: " + FormatBytes(bytes, 16);
                    return false;
                }

                return true;
            }
        }
        catch
        {
            // Fall through to error parsing below.
        }

        var raw = "";
        try
        {
            raw = response.GetValue().GetRawText();
            var failure = JsonUtility.FromJson<SocketFailurePayload>(raw);
            if (failure != null && !string.IsNullOrWhiteSpace(failure.error))
            {
                error = eventName + " failed: " + failure.error;
                if (!string.IsNullOrWhiteSpace(failure.detail))
                {
                    error += " Detail: " + failure.detail;
                }

                return false;
            }
        }
        catch (Exception exc)
        {
            raw = "<raw read failed: " + exc.Message + ">";
        }

        error = eventName + " failed. Ack count: " + response.Count +
                ", binary count: " + (response.InComingBytes != null ? response.InComingBytes.Count : 0) +
                ", raw: " + raw;
        return false;
    }

    private static bool LooksLikeWav(byte[] bytes)
    {
        return bytes != null &&
               bytes.Length >= 12 &&
               bytes[0] == (byte)'R' &&
               bytes[1] == (byte)'I' &&
               bytes[2] == (byte)'F' &&
               bytes[3] == (byte)'F' &&
               bytes[8] == (byte)'W' &&
               bytes[9] == (byte)'A' &&
               bytes[10] == (byte)'V' &&
               bytes[11] == (byte)'E';
    }

    private static string FormatBytes(byte[] bytes, int maxCount)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return "<empty>";
        }

        maxCount = Mathf.Clamp(maxCount, 1, bytes.Length);
        var parts = new string[maxCount];
        for (var i = 0; i < maxCount; i++)
        {
            parts[i] = bytes[i].ToString("X2");
        }

        return string.Join(" ", parts);
    }

    private string BuildRefAudioName(string mood)
    {
        mood = NormalizeVoiceMood(mood);
        return mood + refAudioSuffix;
    }

    private static string NormalizeVoiceMood(string mood)
    {
        mood = (mood ?? "").Trim().Trim('*').Trim();
        if (string.IsNullOrWhiteSpace(mood))
        {
            return "normal";
        }

        return string.Equals(mood, "\u59D4\u5C48\u60F3\u54ED", StringComparison.OrdinalIgnoreCase) ? "sad" : mood;
    }

    private void EnqueueMain(Action action)
    {
        mainThreadActions.Enqueue(action);
    }

    private void FlushPendingSocketActions()
    {
        while (pendingSocketActions.TryDequeue(out var action))
        {
            action?.Invoke();
        }
    }

    private void ReportError(string message)
    {
        Debug.LogWarning(message, this);
        AppendVoiceDebugFile("[VoiceError] " + message);

        if (chatBridge != null)
        {
            chatBridge.FailPendingVoiceMessage(message);
        }
    }

    private void Log(string message)
    {
        AppendVoiceDebugFile("[Voice] " + message);

        if (logTraffic)
        {
            Debug.Log(message, this);
        }
    }

    private void LogVoiceAck(SocketIOResponse response)
    {
        if (response == null)
        {
            if (logTraffic)
            {
                Debug.Log("voice_produce ack: <null>", this);
            }

            AppendVoiceDebugFile("[VoiceAck] <null>");
            return;
        }

        var binaryCount = response.InComingBytes != null ? response.InComingBytes.Count : 0;
        var raw = "<unreadable>";
        if (response.Count > 0)
        {
            try
            {
                raw = response.GetValue().GetRawText();
            }
            catch (Exception exc)
            {
                raw = "<raw read failed: " + exc.Message + ">";
            }
        }

        var message = "Count: " + response.Count + ", binary count: " + binaryCount + ", raw: " + raw;
        if (logTraffic)
        {
            Debug.Log("voice_produce ack. " + message, this);
        }

        AppendVoiceDebugFile("[VoiceAck] " + message);
    }

    private void AppendVoiceDebugFile(string line)
    {
        if (!writeVoiceLogToRenderDebugFile)
        {
            return;
        }

        try
        {
            var path = Path.Combine(Application.persistentDataPath, sharedDebugLogFileName);
            File.AppendAllText(path, Environment.NewLine + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line, System.Text.Encoding.UTF8);
        }
        catch
        {
            // File logging is diagnostic only.
        }
    }

}

public sealed class DesktopPetServerConfig : MonoBehaviour
{
    [Serializable]
    public sealed class Data
    {
        public string socketServerUrl = "http://127.0.0.1:5000";
        public string neteaseApiBaseUrl = "http://127.0.0.1:3000";
    }

    [SerializeField] private string configPath = "config/server_config.json";
    [SerializeField] private bool createMissingConfigFile = true;

    private Data data = new Data();
    private string resolvedConfigPath = "";
    private bool isLoaded;

    public string SocketServerUrl
    {
        get { return data.socketServerUrl; }
    }

    public string NeteaseApiBaseUrl
    {
        get { return data.neteaseApiBaseUrl; }
    }

    public bool IsLoaded
    {
        get { return isLoaded; }
    }

    public IEnumerator LoadRoutine()
    {
        yield return DesktopPetResourcePath.EnsureWritableModelFile(configPath, createMissingConfigFile, path => resolvedConfigPath = path);

        if (!string.IsNullOrWhiteSpace(resolvedConfigPath) && File.Exists(resolvedConfigPath))
        {
            var json = File.ReadAllText(resolvedConfigPath, System.Text.Encoding.UTF8);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var loaded = JsonUtility.FromJson<Data>(json);
                    if (loaded != null)
                    {
                        data = loaded;
                    }
                }
                catch (Exception exc)
                {
                    Debug.LogWarning("Failed to load server config: " + exc.Message, this);
                }
            }
        }

        if (UsesLoopbackUrl(data.socketServerUrl) || UsesLoopbackUrl(data.neteaseApiBaseUrl))
        {
            var packagedJson = "";
            yield return DesktopPetResourcePath.ReadPackagedModelText(configPath, text => packagedJson = text);
            if (!string.IsNullOrWhiteSpace(packagedJson))
            {
                try
                {
                    var packaged = JsonUtility.FromJson<Data>(packagedJson);
                    if (packaged != null &&
                        (!UsesLoopbackUrl(packaged.socketServerUrl) || !UsesLoopbackUrl(packaged.neteaseApiBaseUrl)))
                    {
                        data = packaged;
                    }
                }
                catch (Exception exc)
                {
                    Debug.LogWarning("Failed to load packaged server config: " + exc.Message, this);
                }
            }
        }

        NormalizeData();
        isLoaded = true;
        Save();
    }

    public void SetSocketServerUrl(string value)
    {
        data.socketServerUrl = NormalizeUrl(value, data.socketServerUrl);
        Save();
    }

    public void SetNeteaseApiBaseUrl(string value)
    {
        data.neteaseApiBaseUrl = NormalizeUrl(value, data.neteaseApiBaseUrl);
        Save();
    }

    public void Save()
    {
        if (string.IsNullOrWhiteSpace(resolvedConfigPath))
        {
            resolvedConfigPath = Path.IsPathRooted(configPath)
                ? configPath
                : DesktopPetResourcePath.GetWritableModelPath(configPath);
        }

        try
        {
            var directory = Path.GetDirectoryName(resolvedConfigPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(resolvedConfigPath, JsonUtility.ToJson(data, true), System.Text.Encoding.UTF8);
        }
        catch (Exception exc)
        {
            Debug.LogWarning("Failed to save server config: " + exc.Message, this);
        }
    }

    private void NormalizeData()
    {
        data.socketServerUrl = NormalizeUrl(data.socketServerUrl, "http://127.0.0.1:5000");
        data.neteaseApiBaseUrl = NormalizeUrl(data.neteaseApiBaseUrl, "http://127.0.0.1:3000");
    }

    private static string NormalizeUrl(string value, string fallback)
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

    private static bool UsesLoopbackUrl(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        return value.Contains("127.0.0.1") || value.Contains("localhost");
    }
}
