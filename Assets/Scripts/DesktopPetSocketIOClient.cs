using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
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
        public string token;
        public string model_name;
        public string pet_id;
        public string special;
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
        public string model_name;
        public string pet_id;
        public string return_format;
    }

    [Serializable]
    private sealed class SpeechRecognitionSocketPayload
    {
        public SpeechRecognitionData data;
        public string username;
        public string token;
        public string model_name;
        public string pet_id;
        public string special;
    }

    [Serializable]
    private sealed class SpeechRecognitionData
    {
        public string audio;
        public string encoding = "pcm_s16le";
        public int sample_rate = 16000;
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
    [SerializeField] private TransportProtocol transport = TransportProtocol.Polling;
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private float socketAnswerTimeoutSeconds = 90f;

    [Header("Bridge")]
    [SerializeField] private DesktopPetChatBridge chatBridge;
    [SerializeField] private bool bindOnEnable = true;

    [Header("Voice Synthesis")]
    [SerializeField] private DesktopPetMigratedController petController;
    [SerializeField] private string username = "";
    [SerializeField] private string authToken = "";
    [SerializeField] private string petId = "";
    [SerializeField] private string modelName = "deepseek";
    [SerializeField] private string voiceProduceEventName = "voice_produce";
    [SerializeField] private string refAudioSuffix = ".wav";
    [SerializeField] private string textLanguage = "ja";
    [SerializeField] private string promptLanguage = "ja";
    [SerializeField] private float voiceRequestTimeoutSeconds = 75f;

    [Header("Speech Recognition")]
    [SerializeField] private string speechRecognitionEventName = "audio_translate";
    [SerializeField] private float speechRecognitionTimeoutSeconds = 75f;

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
    private int speechRecognitionRequestSerial;
    private int activeSpeechRecognitionRequestId;
    private Coroutine speechRecognitionTimeoutCoroutine;
    private readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
    private readonly ConcurrentQueue<Action> pendingSocketActions = new ConcurrentQueue<Action>();

    public bool IsConnected
    {
        get { return socket != null && socket.Connected; }
    }

    public string ModelName
    {
        get { return modelName; }
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

    public void SetModelName(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        modelName = string.IsNullOrWhiteSpace(value) ? "deepseek" : value;
        Log("AI model switched to: " + modelName);
    }

    public void SetUserAuth(string value, string token)
    {
        username = string.IsNullOrWhiteSpace(value) ? username : value.Trim();
        authToken = token ?? "";
        Log("AI user switched to: " + username);
    }

    public void UseGptModel()
    {
        SetModelName("gpt");
    }

    public void UseGeminiModel()
    {
        SetModelName("gemini");
    }

    public void UseDeepSeekModel()
    {
        SetModelName("deepseek");
    }

    public void SetGeminiModelEnabled(bool enabled)
    {
        SetModelName(enabled ? "gemini" : "deepseek");
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
        chatBridge.DialogueHistoryRequested.AddListener(SendDialogueHistoryRequest);
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
        chatBridge.DialogueHistoryRequested.RemoveListener(SendDialogueHistoryRequest);
        bridgeBound = false;
    }

    public void SendDialogueHistoryRequest(string eventName, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            ReportError("Dialogue history Socket.IO event name is empty.");
            return;
        }

        var payload = ParsePayloadJson(payloadJson, out var payloadError);
        if (payload == null)
        {
            ReportError(payloadError);
            return;
        }

        if (socket == null || !socket.Connected)
        {
            Connect();
            pendingSocketActions.Enqueue(() => EmitDialogueHistoryRequest(eventName, payloadJson, payload));
            Log("Socket.IO dialogue history request queued until connected: " + eventName);
            return;
        }

        EmitDialogueHistoryRequest(eventName, payloadJson, payload);
    }

    public bool SendDialogueHistoryRequestBlocking(string eventName, string payloadJson, float timeoutSeconds, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(eventName))
        {
            error = "Dialogue history Socket.IO event name is empty.";
            return false;
        }

        var payload = ParsePayloadJson(payloadJson, out var payloadError);
        if (payload == null)
        {
            error = payloadError;
            return false;
        }
        var timeoutMs = Mathf.Max(1, Mathf.RoundToInt(timeoutSeconds * 1000f));

        if (socket == null || !socket.Connected)
        {
            error = "Socket.IO client is not connected.";
            return false;
        }

        var completed = new ManualResetEventSlim(false);
        var ackJson = "";
        var ackError = "";

        try
        {
            socket.Emit(eventName, response =>
            {
                if (TryGetFirstRawJson(response, out var rawJson, out var rawError))
                {
                    ackJson = rawJson;
                }
                else
                {
                    ackError = rawError;
                }

                completed.Set();
            }, payload);
        }
        catch (Exception exc)
        {
            error = exc.Message;
            completed.Dispose();
            return false;
        }

        if (!completed.Wait(timeoutMs))
        {
            error = "Socket.IO " + eventName + " timed out.";
            completed.Dispose();
            return false;
        }

        completed.Dispose();
        if (!string.IsNullOrWhiteSpace(ackError))
        {
            error = ackError;
            return false;
        }

        if (eventName.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (!DesktopPetChatBridge.IsDialogueHistorySuccessResponse(ackJson, out error))
            {
                return false;
            }
        }

        return true;
    }

    public bool SendSocketRequestBlocking(string eventName, string payloadJson, float timeoutSeconds, out string answerJson, out string error)
    {
        answerJson = "";
        error = "";
        if (string.IsNullOrWhiteSpace(eventName))
        {
            error = "Socket.IO event name is empty.";
            return false;
        }

        var payload = ParsePayloadJson(payloadJson, out var payloadError);
        if (payload == null)
        {
            error = payloadError;
            return false;
        }
        var timeoutMs = Mathf.Max(1, Mathf.RoundToInt(timeoutSeconds * 1000f));

        if (socket == null || !socket.Connected)
        {
            error = "Socket.IO client is not connected.";
            return false;
        }

        var completed = new ManualResetEventSlim(false);
        var ackJson = "";
        var ackError = "";

        try
        {
            socket.Emit(eventName, response =>
            {
                if (TryGetFirstRawJson(response, out var rawJson, out var rawError))
                {
                    ackJson = rawJson;
                }
                else
                {
                    ackError = rawError;
                }

                completed.Set();
            }, payload);
        }
        catch (Exception exc)
        {
            error = exc.Message;
            completed.Dispose();
            return false;
        }

        if (!completed.Wait(timeoutMs))
        {
            error = "Socket.IO " + eventName + " timed out.";
            completed.Dispose();
            return false;
        }

        completed.Dispose();
        if (!string.IsNullOrWhiteSpace(ackError))
        {
            error = ackError;
            return false;
        }

        answerJson = ackJson;
        return true;
    }

    private void EmitDialogueHistoryRequest(string eventName, string payloadJson, object payload)
    {
        if (socket == null || !socket.Connected)
        {
            ReportError("Socket.IO client is not connected; cannot request dialogue history.");
            return;
        }

        Log("Socket.IO emit: " + eventName + " " + payloadJson);
        socket.Emit(eventName, response => HandleDialogueHistoryAck(eventName, response), payload);
    }

    public void SendSocketRequest(string eventName, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            ReportError("Socket.IO event name is empty.");
            return;
        }

        var payload = ParsePayloadJson(payloadJson, out var payloadError);
        if (payload == null)
        {
            ReportError(payloadError);
            return;
        }
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

    private void EmitSocketRequest(string eventName, string payloadJson, object payload, int requestId)
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
            token = authToken,
            model_name = modelName,
            pet_id = petId,
            special = "",
            data = new VoiceProduceData
            {
                text = jaText,
                text_lang = textLanguage,
                ref_audio_name = BuildRefAudioName(mood),
                prompt_text = "",
                prompt_lang = promptLanguage,
                model_name = petId,
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

    public void SendSpeechRecognitionRequest(
        byte[] pcm16Audio,
        int sampleRate,
        Action<string> onCompleted,
        Action<string> onFailed)
    {
        if (pcm16Audio == null || pcm16Audio.Length == 0)
        {
            onFailed?.Invoke("Speech recognition audio is empty.");
            return;
        }

        var payload = new SpeechRecognitionSocketPayload
        {
            username = username,
            token = authToken,
            model_name = modelName,
            pet_id = petId,
            special = "",
            data = new SpeechRecognitionData
            {
                audio = Convert.ToBase64String(pcm16Audio),
                sample_rate = Mathf.Max(1, sampleRate)
            }
        };

        var requestId = BeginSpeechRecognitionTimeout(onFailed);
        if (socket == null || !socket.Connected)
        {
            Connect();
            pendingSocketActions.Enqueue(() =>
            {
                if (requestId == activeSpeechRecognitionRequestId)
                {
                    EmitSpeechRecognitionRequest(payload, pcm16Audio.Length, requestId, onCompleted, onFailed);
                }
            });
            Log("Socket.IO speech recognition request queued until connected.");
            return;
        }

        EmitSpeechRecognitionRequest(payload, pcm16Audio.Length, requestId, onCompleted, onFailed);
    }

    private void EmitSpeechRecognitionRequest(
        SpeechRecognitionSocketPayload payload,
        int audioByteCount,
        int requestId,
        Action<string> onCompleted,
        Action<string> onFailed)
    {
        if (socket == null || !socket.Connected)
        {
            ReportSpeechRecognitionError(requestId, "Socket.IO client is not connected; cannot request speech recognition.", onFailed);
            return;
        }

        Log("Socket.IO emit: " + speechRecognitionEventName + " pcm16Bytes=" + audioByteCount + ", sampleRate=" + payload.data.sample_rate);
        socket.Emit(
            speechRecognitionEventName,
            response => HandleSpeechRecognitionAck(requestId, response, onCompleted, onFailed),
            payload);
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

    private void HandleDialogueHistoryAck(string eventName, SocketIOResponse response)
    {
        if (!TryGetFirstRawJson(response, out var rawJson, out var error))
        {
            EnqueueMain(() => ReportDialogueHistoryAckError(eventName, error));
            return;
        }

        Log("Socket.IO dialogue history ack: " + rawJson);
        EnqueueMain(() =>
        {
            if (chatBridge == null)
            {
                ReportError("Chat bridge is missing; cannot forward dialogue history ack.");
                return;
            }

            if (IsWriteHistoryEvent(eventName))
            {
                if (eventName.IndexOf("summary", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    chatBridge.ReceiveSaveSummaryHistoryResultJson(rawJson);
                }
                else
                {
                    chatBridge.ReceiveSaveDialogueHistoryResultJson(rawJson);
                }
            }
            else
            {
                if (eventName.IndexOf("summary", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    chatBridge.ReceiveSummaryHistoryResultJson(rawJson);
                }
                else
                {
                    chatBridge.ReceiveDialogueHistoryResultJson(rawJson);
                }
            }
        });
    }

    private void ReportDialogueHistoryAckError(string eventName, string error)
    {
        if (chatBridge == null)
        {
            ReportError(error);
            return;
        }

        if (IsWriteHistoryEvent(eventName))
        {
            if (eventName.IndexOf("summary", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                chatBridge.ReceiveSaveSummaryHistoryResult(error);
            }
            else
            {
                chatBridge.ReceiveSaveDialogueHistoryResult(error);
            }
        }
        else
        {
            if (eventName.IndexOf("summary", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                chatBridge.ReceiveSummaryHistoryResult("", error);
            }
            else
            {
                chatBridge.ReceiveDialogueHistoryResult("", error);
            }
        }
    }

    private static bool IsWriteHistoryEvent(string eventName)
    {
        return eventName.IndexOf("save", StringComparison.OrdinalIgnoreCase) >= 0 ||
               eventName.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0;
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

    private void HandleSpeechRecognitionAck(
        int requestId,
        SocketIOResponse response,
        Action<string> onCompleted,
        Action<string> onFailed)
    {
        if (requestId != activeSpeechRecognitionRequestId)
        {
            Log("audio_translate ack ignored because request is no longer active. requestId=" + requestId + ", active=" + activeSpeechRecognitionRequestId);
            return;
        }

        if (!TryGetFirstRawJson(response, out var rawJson, out var responseError))
        {
            EnqueueMain(() => ReportSpeechRecognitionError(requestId, responseError, onFailed));
            return;
        }

        string text;
        string error;
        try
        {
            var payload = Newtonsoft.Json.Linq.JToken.Parse(rawJson);
            text = payload.SelectToken("text")?.ToString()?.Trim() ?? "";
            error = payload.SelectToken("error")?.ToString()?.Trim() ?? "";
        }
        catch (Exception exc)
        {
            text = "";
            error = "Speech recognition ack is invalid JSON: " + exc.Message;
        }

        EnqueueMain(() =>
        {
            if (requestId != activeSpeechRecognitionRequestId)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                ReportSpeechRecognitionError(requestId, error, onFailed);
                return;
            }

            CompleteSpeechRecognitionRequest(requestId);
            Log("audio_translate ack text=\"" + text + "\"");
            onCompleted?.Invoke(text);
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
            if (serverConfig.HasAppAuth)
            {
                SetUserAuth(serverConfig.AppUsername, serverConfig.AppAuthToken);
            }
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

    private int BeginSpeechRecognitionTimeout(Action<string> onFailed)
    {
        var requestId = ++speechRecognitionRequestSerial;
        activeSpeechRecognitionRequestId = requestId;

        if (speechRecognitionTimeoutCoroutine != null)
        {
            StopCoroutine(speechRecognitionTimeoutCoroutine);
        }

        speechRecognitionTimeoutCoroutine = StartCoroutine(SpeechRecognitionTimeoutRoutine(requestId, onFailed));
        return requestId;
    }

    private IEnumerator SpeechRecognitionTimeoutRoutine(int requestId, Action<string> onFailed)
    {
        yield return new WaitForSecondsRealtime(Mathf.Max(1f, speechRecognitionTimeoutSeconds));
        if (requestId != activeSpeechRecognitionRequestId)
        {
            yield break;
        }

        activeSpeechRecognitionRequestId = 0;
        speechRecognitionTimeoutCoroutine = null;
        var message = "Speech recognition timed out after " + speechRecognitionTimeoutSeconds.ToString("0") + " seconds.";
        Debug.LogWarning(message, this);
        AppendVoiceDebugFile("[SttError] " + message);
        onFailed?.Invoke(message);
    }

    private void CompleteSpeechRecognitionRequest(int requestId)
    {
        if (requestId != activeSpeechRecognitionRequestId)
        {
            return;
        }

        activeSpeechRecognitionRequestId = 0;
        if (speechRecognitionTimeoutCoroutine != null)
        {
            StopCoroutine(speechRecognitionTimeoutCoroutine);
            speechRecognitionTimeoutCoroutine = null;
        }
    }

    private void ReportSpeechRecognitionError(int requestId, string message, Action<string> onFailed)
    {
        if (requestId != activeSpeechRecognitionRequestId)
        {
            Log("Speech recognition error ignored because request is no longer active. requestId=" + requestId + ", active=" + activeSpeechRecognitionRequestId);
            return;
        }

        CompleteSpeechRecognitionRequest(requestId);
        Debug.LogWarning("Speech recognition failed: " + message, this);
        AppendVoiceDebugFile("[SttError] " + message);
        onFailed?.Invoke(message);
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

    private static object ParsePayloadJson(string payloadJson, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            error = "Socket.IO payload json is empty.";
            return null;
        }

        try
        {
            return Newtonsoft.Json.Linq.JToken.Parse(payloadJson);
        }
        catch (Exception exc)
        {
            error = "Socket.IO payload json is invalid: " + exc.Message;
            return null;
        }
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
        public string neteaseCellphone = "";
        public string neteaseCountryCode = "86";
        public bool appAuthRequired = true;
        public string appUsername = "";
        public string appAuthToken = "";
        public string memoryStorageMode = "cloud";
        public bool memoryStorageSelected;
        public string localMemoryFolder = "\u8bb0\u5fc6";
        public string localDialogueHistoryFile = "dialogue_history.txt";
        public string localSummaryHistoryFile = "summary_history.txt";
    }

    [Serializable]
    private sealed class ConfigFileData
    {
        public string socketServerUrl = "http://127.0.0.1:5000";
        public string neteaseApiBaseUrl = "http://127.0.0.1:3000";
        public bool appAuthRequired = true;
        public string localMemoryFolder = "\u8bb0\u5fc6";
        public string localDialogueHistoryFile = "dialogue_history.txt";
        public string localSummaryHistoryFile = "summary_history.txt";
    }

    [Serializable]
    private sealed class RuntimeState
    {
        public string neteaseCellphone = "";
        public string neteaseCountryCode = "86";
        public string appUsername = "";
        public string appAuthToken = "";
        public string memoryStorageMode = "cloud";
        public bool memoryStorageSelected;
    }

    [SerializeField] private string configPath = "config/server_config.json";
    [SerializeField] private string runtimeStatePath = "config/server_state.json";

    private Data data = new Data();
    private string resolvedConfigPath = "";
    private string resolvedRuntimeStatePath = "";
    private bool isLoaded;

    public string SocketServerUrl
    {
        get { return data.socketServerUrl; }
    }

    public string NeteaseApiBaseUrl
    {
        get { return data.neteaseApiBaseUrl; }
    }

    public string NeteaseCellphone
    {
        get { return data.neteaseCellphone; }
    }

    public string NeteaseCountryCode
    {
        get { return data.neteaseCountryCode; }
    }

    public bool AppAuthRequired
    {
        get { return data.appAuthRequired; }
    }

    public string AppUsername
    {
        get { return data.appUsername; }
    }

    public string AppAuthToken
    {
        get { return data.appAuthToken; }
    }

    public bool HasAppAuth
    {
        get { return !string.IsNullOrWhiteSpace(data.appUsername) && !string.IsNullOrWhiteSpace(data.appAuthToken); }
    }

    public string MemoryStorageMode
    {
        get { return data.memoryStorageMode; }
    }

    public bool MemoryStorageSelected
    {
        get { return data.memoryStorageSelected; }
    }

    public string LocalMemoryFolder
    {
        get { return data.localMemoryFolder; }
    }

    public string LocalDialogueHistoryFile
    {
        get { return data.localDialogueHistoryFile; }
    }

    public string LocalSummaryHistoryFile
    {
        get { return data.localSummaryHistoryFile; }
    }

    public bool IsLoaded
    {
        get { return isLoaded; }
    }

    public string ResolvedConfigPath
    {
        get { return resolvedConfigPath; }
    }

    public IEnumerator LoadRoutine()
    {
        yield return EnsureWritableConfigFile();

        if (!string.IsNullOrWhiteSpace(resolvedConfigPath) && File.Exists(resolvedConfigPath))
        {
            try
            {
                var json = File.ReadAllText(resolvedConfigPath, System.Text.Encoding.UTF8);
                var loaded = JsonUtility.FromJson<ConfigFileData>(json);
                if (loaded != null)
                {
                    ApplyConfigFileData(loaded);
                }
            }
            catch (Exception exc)
            {
                Debug.LogWarning("Failed to load writable server config: " + exc.Message, this);
            }
        }
        else
        {
            Debug.LogWarning("Writable server config was not found: " + resolvedConfigPath, this);
        }

        NormalizeData();
        LoadRuntimeState();
        NormalizeData();
        isLoaded = true;
        Debug.Log("Server config loaded from: " + resolvedConfigPath + ", socketServerUrl=" + data.socketServerUrl, this);
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

    public void SetNeteaseCellphone(string value)
    {
        data.neteaseCellphone = (value ?? "").Trim();
        Save();
    }

    public void SetNeteaseCountryCode(string value)
    {
        value = (value ?? "").Trim();
        data.neteaseCountryCode = string.IsNullOrWhiteSpace(value) ? "86" : value;
        Save();
    }

    public void SetMemoryStorageMode(string value)
    {
        data.memoryStorageMode = NormalizeMemoryStorageMode(value);
        data.memoryStorageSelected = true;
        Save();
    }

    public void SetAppAuth(string username, string token)
    {
        data.appUsername = (username ?? "").Trim();
        data.appAuthToken = token ?? "";
        Save();
    }

    public void ClearAppAuth()
    {
        data.appAuthToken = "";
        Save();
    }

    public void Save()
    {
        SaveConfigFile();
        SaveRuntimeState();
    }

    private IEnumerator EnsureWritableConfigFile()
    {
        if (Path.IsPathRooted(configPath))
        {
            resolvedConfigPath = configPath;
            yield break;
        }

        yield return DesktopPetResourcePath.EnsureWritableModelFile(configPath, false, path => resolvedConfigPath = path);
        if (!string.IsNullOrWhiteSpace(resolvedConfigPath))
        {
            yield break;
        }

        resolvedConfigPath = DesktopPetResourcePath.GetWritableModelPath(configPath);
        var directory = Path.GetDirectoryName(resolvedConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SaveConfigFile();
    }

    private void ApplyConfigFileData(ConfigFileData config)
    {
        if (config == null)
        {
            return;
        }

        data.socketServerUrl = config.socketServerUrl;
        data.neteaseApiBaseUrl = config.neteaseApiBaseUrl;
        data.appAuthRequired = config.appAuthRequired;
        data.localMemoryFolder = config.localMemoryFolder;
        data.localDialogueHistoryFile = config.localDialogueHistoryFile;
        data.localSummaryHistoryFile = config.localSummaryHistoryFile;
    }

    private void SaveConfigFile()
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

            var config = new ConfigFileData
            {
                socketServerUrl = data.socketServerUrl,
                neteaseApiBaseUrl = data.neteaseApiBaseUrl,
                appAuthRequired = data.appAuthRequired,
                localMemoryFolder = data.localMemoryFolder,
                localDialogueHistoryFile = data.localDialogueHistoryFile,
                localSummaryHistoryFile = data.localSummaryHistoryFile
            };
            File.WriteAllText(resolvedConfigPath, JsonUtility.ToJson(config, true), System.Text.Encoding.UTF8);
        }
        catch (Exception exc)
        {
            Debug.LogWarning("Failed to save server config: " + exc.Message, this);
        }
    }

    private void SaveRuntimeState()
    {
        if (string.IsNullOrWhiteSpace(resolvedRuntimeStatePath))
        {
            resolvedRuntimeStatePath = Path.IsPathRooted(runtimeStatePath)
                ? runtimeStatePath
                : DesktopPetResourcePath.GetWritableModelPath(runtimeStatePath);
        }

        try
        {
            var directory = Path.GetDirectoryName(resolvedRuntimeStatePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = new RuntimeState
            {
                neteaseCellphone = data.neteaseCellphone,
                neteaseCountryCode = data.neteaseCountryCode,
                appUsername = data.appUsername,
                appAuthToken = data.appAuthToken,
                memoryStorageMode = data.memoryStorageMode,
                memoryStorageSelected = data.memoryStorageSelected
            };
            File.WriteAllText(resolvedRuntimeStatePath, JsonUtility.ToJson(state, true), System.Text.Encoding.UTF8);
        }
        catch (Exception exc)
        {
            Debug.LogWarning("Failed to save server state: " + exc.Message, this);
        }
    }

    private void LoadRuntimeState()
    {
        resolvedRuntimeStatePath = Path.IsPathRooted(runtimeStatePath)
            ? runtimeStatePath
            : DesktopPetResourcePath.GetWritableModelPath(runtimeStatePath);
        if (!File.Exists(resolvedRuntimeStatePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(resolvedRuntimeStatePath, System.Text.Encoding.UTF8);
            var state = JsonUtility.FromJson<RuntimeState>(json);
            if (state == null)
            {
                return;
            }

            data.neteaseCellphone = state.neteaseCellphone;
            data.neteaseCountryCode = state.neteaseCountryCode;
            data.appUsername = state.appUsername;
            data.appAuthToken = state.appAuthToken;
            data.memoryStorageMode = state.memoryStorageMode;
            data.memoryStorageSelected = state.memoryStorageSelected;
        }
        catch (Exception exc)
        {
            Debug.LogWarning("Failed to load server state: " + exc.Message, this);
        }
    }

    private void NormalizeData()
    {
        data.socketServerUrl = NormalizeUrl(data.socketServerUrl, "http://127.0.0.1:5000");
        data.neteaseApiBaseUrl = NormalizeUrl(data.neteaseApiBaseUrl, "http://127.0.0.1:3000");
        data.neteaseCellphone = (data.neteaseCellphone ?? "").Trim();
        data.neteaseCountryCode = string.IsNullOrWhiteSpace(data.neteaseCountryCode) ? "86" : data.neteaseCountryCode.Trim();
        data.appUsername = (data.appUsername ?? "").Trim();
        data.appAuthToken = data.appAuthToken ?? "";
        data.memoryStorageMode = NormalizeMemoryStorageMode(data.memoryStorageMode);
        data.localMemoryFolder = string.IsNullOrWhiteSpace(data.localMemoryFolder) ? "\u8bb0\u5fc6" : data.localMemoryFolder.Trim();
        data.localDialogueHistoryFile = string.IsNullOrWhiteSpace(data.localDialogueHistoryFile) ? "dialogue_history.txt" : data.localDialogueHistoryFile.Trim();
        data.localSummaryHistoryFile = string.IsNullOrWhiteSpace(data.localSummaryHistoryFile) ? "summary_history.txt" : data.localSummaryHistoryFile.Trim();
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

    private static string NormalizeMemoryStorageMode(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        if (value == "local" || value == "\u672c\u5730")
        {
            return "local";
        }

        return "cloud";
    }
}
