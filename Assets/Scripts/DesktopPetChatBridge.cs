using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.UI;

/// <summary>
/// Dialogue bridge for the migrated desktop pet.
/// It listens to DesktopPetMigratedController.UserTextSubmitted and owns the chat flow boundary.
/// </summary>
public sealed class DesktopPetChatBridge : MonoBehaviour
{
    [Serializable]
    public sealed class ChatMessage
    {
        public string role;
        public string content;
        public string action;
        public string mood;
        public string expression;
    }

    [Serializable]
    public sealed class SocketCallPayload
    {
        public ChatEnvelope[] data;
        public string username;
        public string token;
        public string model_name;
        public string pet_id;
        public string special;
    }

    [Serializable]
    public sealed class HistoryServerPayload
    {
        public string username;
        public string token;
        public string model_name;
        public string pet_id;
        public string special;
        public string text;
        public string data;
        public string dialogue_history;
    }

    [Serializable]
    public sealed class SummaryHistoryServerPayload
    {
        public ChatEnvelope[] data;
        public string username;
        public string token;
        public string model_name;
        public string pet_id;
        public string special;
    }

    [Serializable]
    public sealed class ChatEnvelope
    {
        public string role;
        public string content;
    }

    [Serializable]
    private sealed class ModelReply
    {
        public string action;
        public string content;
        public string cn;
        public string tts;
        public string mood;
        public string expression;
        public bool rawTextFallback;
    }

    [Serializable]
    private sealed class AuthRequestPayload
    {
        public string username;
        public string password;
        public string token;
    }

    [Serializable]
    public sealed class AuthResponsePayload
    {
        public string username = "";
        public string token = "";
        public string error = "";
        public bool valid;
    }

    [Serializable]
    public sealed class ParsedReplyEvent : UnityEvent<string, string, string>
    {
    }

    [Serializable]
    public sealed class VoiceSynthesisRequestEvent : UnityEvent<string, string>
    {
    }

    [Serializable]
    public sealed class SocketAnswerRequestEvent : UnityEvent<string, string>
    {
    }

    [Serializable]
    public sealed class DialogueHistoryRequestEvent : UnityEvent<string, string>
    {
    }

    [Header("Controller")]
    [SerializeField] private DesktopPetMigratedController petController;
    [SerializeField] private DesktopPetSocketIOClient socketClient;
    [SerializeField] private ChatLogCtrl chatLog;
    [SerializeField] private bool bindOnEnable = true;

    [Header("Socket.IO Payload")]
    [SerializeField] private bool emitSocketRequestEvent = true;
    [SerializeField] private string getAnswerEventName = "get_answer";
    [SerializeField] private string username = "";
    [SerializeField] private string authToken = "";
    [SerializeField] private string petId = "atri";
    [SerializeField] private string modelName = "deepseek";

    public string ModelName
    {
        get { return modelName; }
    }

    [Header("History")]
    [SerializeField] private bool keepHistory = true;
    [SerializeField] private int maxHistoryEntries = 50;
    [SerializeField] private bool useSummaryHistory = true;
    [SerializeField] private string summaryHistoryIntro = "接下来是我们对话历史的精炼版，你可以从中参考获得更详细的人设定位。";
    [SerializeField] private string summaryUpdatePrompt = "[系统消息]:这是系统提示，对话即将结束。";
    [SerializeField] private string finalReplyFormatReminder = "[系统消息]:注意！你不能仿照对话历史的格式回复自然语言，那只是为了减少上下文的精简版。你必须严格遵循上述json格式要求！";

    [Header("History Files")]
    [SerializeField] private bool loadHistoryOnAwake = true;
    [SerializeField] private string[] presetHistoryPaths = { "config/rules.txt", "config/personal_setting.txt" };
    [SerializeField] private string recentHistoryIntro = "接下来是我们近期的聊天记录，有助于你了解最近发生了什么。";
    [SerializeField, HideInInspector] private string dialogueHistoryPath = "config/chat_history.txt";
    [SerializeField, HideInInspector] private bool createMissingDialogueHistoryFile = true;
    [SerializeField] private bool saveHistoryOnExit = true;
    [SerializeField] private string userHistoryLabel = "superpai";
    [SerializeField] private string modelHistoryLabel = "atri";

    [Header("History Server")]
    [SerializeField] private DesktopPetServerConfig serverConfig;
    [SerializeField] private string serverUrl = "http://127.0.0.1:5000";
    [SerializeField] private string getDialogueHistoryEventName = "get_dialogue_history";
    [SerializeField] private string getSummaryHistoryEventName = "get_summary_history";
    [SerializeField] private string saveDialogueHistoryEventName = "save_dialogue_history";
    [SerializeField] private string updateSummaryHistoryEventName = "update_summary_history";
    [SerializeField] private float historyRequestTimeoutSeconds = 60f;

    [Header("Debug")]
    [SerializeField] private bool logChatFlow = true;

    [Header("Events")]
    public UnityEvent<string> UserTextReceived;
    public UnityEvent<string> AiAnswerReceived;
    public UnityEvent<string> RequestFailed;
    public SocketAnswerRequestEvent SocketAnswerRequested;
    public ParsedReplyEvent ReplyParsed;
    public VoiceSynthesisRequestEvent VoiceSynthesisRequested;
    public DialogueHistoryRequestEvent DialogueHistoryRequested;
    public UnityEvent WaitingStarted;
    public UnityEvent WaitingFinished;

    private readonly List<ChatMessage> presetHistory = new List<ChatMessage>();
    private readonly List<ChatMessage> summaryHistory = new List<ChatMessage>();
    private readonly List<ChatMessage> dialogueHistory = new List<ChatMessage>();
    private readonly List<ChatMessage> currentSessionHistory = new List<ChatMessage>();
    private readonly List<ChatMessage> history = new List<ChatMessage>();
    private readonly List<ChatMessage> promptHistory = new List<ChatMessage>();
    private string[] availableMoodKeywords = Array.Empty<string>();
    private string[] availableExpressionKeywords = Array.Empty<string>();
    private bool pendingFullHistoryRequest;
    private DateTime? chatBeginTime;
    private bool isBound;
    private bool historySaved;
    private bool historySaveInProgress;
    private bool dialogueHistoryLoadFinished;
    private string dialogueHistoryLoadText = "";
    private string dialogueHistoryLoadError = "";
    private bool summaryHistoryLoadFinished;
    private string summaryHistoryLoadText = "";
    private string summaryHistoryLoadError = "";
    private bool dialogueHistorySaveFinished;
    private string dialogueHistorySaveError = "";
    private bool summaryHistorySaveFinished;
    private string summaryHistorySaveError = "";
    private string pendingAiLogRole = "";
    private string pendingAiLogText = "";
    private bool appAuthChoiceAnswered;
    private bool appAuthRequestInProgress;
    private GameObject appAuthDialog;
    private InputField appAuthUsernameInput;
    private InputField appAuthPasswordInput;
    private Text appAuthFeedbackText;
    private Button appAuthLoginButton;
    private Button appAuthRegisterButton;
    private bool memoryStorageChoiceAnswered;
    private GameObject memoryStorageChoiceDialog;

    private void Reset()
    {
        CacheController();
        CacheServerConfig();
        PreserveLegacyHistoryFileFields();
    }

    public void LoadHistory()
    {
        StartCoroutine(LoadHistoryRoutine());
    }

    public void ClearDialogueHistory()
    {
        summaryHistory.Clear();
        dialogueHistory.Clear();
        currentSessionHistory.Clear();
        chatBeginTime = null;
        historySaved = false;
        RebuildHistory();
    }

    public void SaveHistoryOnExit()
    {
        _ = saveHistoryOnExit;
    }

    public void SaveHistoryNow()
    {
        SaveCurrentSessionHistory();
    }

    private void SaveCurrentSessionHistory()
    {
        if (historySaved || historySaveInProgress || currentSessionHistory.Count == 0)
        {
            return;
        }

        try
        {
            StartCoroutine(AppendCurrentSessionHistoryRoutine());
        }
        catch (Exception exc)
        {
            Debug.LogWarning("Failed to save chat history: " + exc.Message, this);
        }
    }

    private System.Collections.IEnumerator LoadHistoryRoutine()
    {
        presetHistory.Clear();
        summaryHistory.Clear();
        dialogueHistory.Clear();

        yield return LoadPresetHistory();
        yield return EnsureAppAuthReady();
        yield return EnsureMemoryStorageModeSelected();
        yield return LoadSummaryHistory();
        yield return LoadDialogueHistory();
        RebuildHistory();

        Log("History loaded. preset=" + presetHistory.Count + ", summary=" + summaryHistory.Count + ", dialogue=" + dialogueHistory.Count + ", total=" + history.Count);
    }

    private System.Collections.IEnumerator LoadPresetHistory()
    {
        var rulesPath = GetPresetHistoryPath(0, "config/rules.txt");
        var rulesText = "";
        yield return LoadPresetText(rulesPath, text => rulesText = text);
        if (!string.IsNullOrWhiteSpace(rulesText))
        {
            presetHistory.Add(CreateMessage("system", rulesText));
            Log("Preset rules loaded: " + rulesPath);
        }
        else
        {
            Log("Preset rules empty or missing: " + rulesPath);
        }

        CacheController();
        if (petController != null)
        {
            yield return petController.LoadMoodExpressionConfiguration();
            availableMoodKeywords = petController.GetAvailableMoodNames();
            availableExpressionKeywords = petController.GetAvailableExpressionNames();
        }

        presetHistory.Add(CreateMessage("system", "这是可选心情列表————" + FormatKeywordList(GetAvailableMoodKeywords())));
        presetHistory.Add(CreateMessage("system", "这是可选表现列表————" + FormatKeywordList(GetAvailableExpressionKeywords())));

        var personalPath = GetPresetHistoryPath(1, "config/personal_setting.txt");
        var personalText = "";
        yield return LoadPresetText(personalPath, text => personalText = text);
        if (!string.IsNullOrWhiteSpace(personalText))
        {
            presetHistory.Add(CreateMessage("system", personalText));
            Log("Preset personal setting loaded: " + personalPath);
        }
        else
        {
            Log("Preset personal setting empty or missing: " + personalPath);
        }

        if (useSummaryHistory && !string.IsNullOrWhiteSpace(summaryHistoryIntro))
        {
            presetHistory.Add(CreateMessage("system", summaryHistoryIntro));
        }

    }

    private string GetPresetHistoryPath(int index, string fallback)
    {
        if (presetHistoryPaths == null || index < 0 || index >= presetHistoryPaths.Length)
        {
            return fallback;
        }

        return string.IsNullOrWhiteSpace(presetHistoryPaths[index]) ? fallback : presetHistoryPaths[index];
    }

    private System.Collections.IEnumerator LoadPresetText(string relativePath, Action<string> onLoaded)
    {
        var path = "";
        yield return DesktopPetResourcePath.EnsureWritableModelFile(relativePath, false, readyPath => path = readyPath);

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            onLoaded?.Invoke(File.ReadAllText(path, System.Text.Encoding.UTF8));
            yield break;
        }

        var text = "";
        yield return DesktopPetResourcePath.ReadPackagedModelText(relativePath, loaded => text = loaded);
        onLoaded?.Invoke(text);
    }

    private string[] GetAvailableMoodKeywords()
    {
        return availableMoodKeywords;
    }

    private string[] GetAvailableExpressionKeywords()
    {
        return availableExpressionKeywords;
    }

    private static string FormatKeywordList(string[] keywords)
    {
        if (keywords == null || keywords.Length == 0)
        {
            return "[]";
        }

        var builder = new StringBuilder();
        builder.Append('[');
        for (var i = 0; i < keywords.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append('\'');
            builder.Append((keywords[i] ?? "").Replace("\\", "\\\\").Replace("'", "\\'"));
            builder.Append('\'');
        }

        builder.Append(']');
        return builder.ToString();
    }

    private System.Collections.IEnumerator LoadSummaryHistory()
    {
        if (!useSummaryHistory)
        {
            yield break;
        }

        yield return EnsureHistoryServerConfigLoaded();
        if (UseLocalMemoryStorage())
        {
            var localMemoryText = LoadLocalMemoryText(serverConfig.LocalSummaryHistoryFile);
            if (!string.IsNullOrWhiteSpace(localMemoryText))
            {
                var parsedCount = AddSummaryHistoryFromText(localMemoryText);
                Log("Summary history loaded from local memory. parsed=" + parsedCount);
            }
            else
            {
                Log("Summary history empty in local memory.");
            }

            yield break;
        }

        summaryHistoryLoadFinished = false;
        summaryHistoryLoadText = "";
        summaryHistoryLoadError = "";
        DialogueHistoryRequested?.Invoke(getSummaryHistoryEventName, BuildHistoryServerPayloadJson(""));

        var timeoutAt = Time.realtimeSinceStartup + GetHistoryRequestTimeoutSeconds();
        while (!summaryHistoryLoadFinished && Time.realtimeSinceStartup < timeoutAt)
        {
            yield return null;
        }

        var text = summaryHistoryLoadText;
        var error = summaryHistoryLoadFinished ? summaryHistoryLoadError : "Socket.IO get_summary_history timed out.";

        if (!string.IsNullOrWhiteSpace(text))
        {
            var parsedCount = AddSummaryHistoryFromText(text);
            Log("Summary history loaded from server. parsed=" + parsedCount);
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            ReportFailure("Summary history load failed: " + error);
        }
        else
        {
            Log("Summary history empty on server.");
        }
    }

    private System.Collections.IEnumerator LoadDialogueHistory()
    {
        yield return EnsureHistoryServerConfigLoaded();
        if (UseLocalMemoryStorage())
        {
            var localMemoryText = LoadLocalMemoryText(serverConfig.LocalDialogueHistoryFile);
            if (!string.IsNullOrWhiteSpace(localMemoryText))
            {
                var parsedCount = AddDialogueHistoryFromText(localMemoryText);
                Log("Dialogue history loaded from local memory. parsed=" + parsedCount);
            }
            else
            {
                Log("Dialogue history empty in local memory.");
            }

            yield break;
        }

        dialogueHistoryLoadFinished = false;
        dialogueHistoryLoadText = "";
        dialogueHistoryLoadError = "";
        DialogueHistoryRequested?.Invoke(getDialogueHistoryEventName, BuildHistoryServerPayloadJson(""));

        var timeoutAt = Time.realtimeSinceStartup + GetHistoryRequestTimeoutSeconds();
        while (!dialogueHistoryLoadFinished && Time.realtimeSinceStartup < timeoutAt)
        {
            yield return null;
        }

        var text = dialogueHistoryLoadText;
        var error = dialogueHistoryLoadFinished ? dialogueHistoryLoadError : "Socket.IO get_dialogue_history timed out.";

        if (!string.IsNullOrWhiteSpace(text))
        {
            var parsedCount = AddDialogueHistoryFromText(text);
            Log("Dialogue history loaded from server. parsed=" + parsedCount);
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            ReportFailure("Dialogue history load failed: " + error);
        }
        else
        {
            Log("Dialogue history empty on server.");
        }
    }

    public void ReceiveDialogueHistoryResultJson(string json)
    {
        if (TryReadDialogueHistoryResponse(json, out var text, out var error))
        {
            ReceiveDialogueHistoryResult(text, "");
        }
        else
        {
            ReceiveDialogueHistoryResult("", error);
        }
    }

    public void ReceiveSummaryHistoryResultJson(string json)
    {
        if (TryReadDialogueHistoryResponse(json, out var text, out var error))
        {
            ReceiveSummaryHistoryResult(text, "");
        }
        else
        {
            ReceiveSummaryHistoryResult("", error);
        }
    }

    public void ReceiveDialogueHistoryResult(string text, string error)
    {
        dialogueHistoryLoadText = text ?? "";
        dialogueHistoryLoadError = error ?? "";
        dialogueHistoryLoadFinished = true;
    }

    public void ReceiveSummaryHistoryResult(string text, string error)
    {
        summaryHistoryLoadText = text ?? "";
        summaryHistoryLoadError = error ?? "";
        summaryHistoryLoadFinished = true;
    }

    public void ReceiveSaveDialogueHistoryResultJson(string json)
    {
        ReceiveSaveDialogueHistoryResult(IsSuccessResponse(json, out var error) ? "" : error);
    }

    public void ReceiveSaveSummaryHistoryResultJson(string json)
    {
        ReceiveSaveSummaryHistoryResult(IsSuccessResponse(json, out var error) ? "" : error);
    }

    public void ReceiveSaveDialogueHistoryResult(string error)
    {
        dialogueHistorySaveError = error ?? "";
        dialogueHistorySaveFinished = true;
    }

    public void ReceiveSaveSummaryHistoryResult(string error)
    {
        summaryHistorySaveError = error ?? "";
        summaryHistorySaveFinished = true;
    }

    public static bool IsDialogueHistorySuccessResponse(string response, out string error)
    {
        return IsSuccessResponse(response, out error);
    }

    private void Awake()
    {
        CacheController();
        CacheServerConfig();
        PreserveLegacyHistoryFileFields();
        if (loadHistoryOnAwake)
        {
            LoadHistory();
        }
    }

    private void OnEnable()
    {
        if (bindOnEnable)
        {
            Bind();
        }
    }

    private void OnDisable()
    {
        Unbind();
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused && saveHistoryOnExit)
        {
            SaveCurrentSessionHistory();
        }
    }

    private void OnApplicationQuit()
    {
        if (saveHistoryOnExit)
        {
            SaveCurrentSessionHistory();
        }
    }

    private void OnDestroy()
    {
        if (saveHistoryOnExit)
        {
            SaveCurrentSessionHistory();
        }
    }

    public void Bind()
    {
        CacheController();

        if (isBound || petController == null)
        {
            return;
        }

        petController.UserTextSubmitted.AddListener(HandleUserTextSubmitted);
        isBound = true;
    }

    public void Unbind()
    {
        if (!isBound || petController == null)
        {
            return;
        }

        petController.UserTextSubmitted.RemoveListener(HandleUserTextSubmitted);
        isBound = false;
    }
    public void CheckHeartBeat()
    {
        if (petController != null && petController.isInitiative && Time.time - petController.lastSpeakTime > petController.speakInterval && petController.isUserSpeakingTurn)
        {
            AddHistory("user","heart_beat");
            petController.isUserSpeakingTurn=false;
            RequestSocketAnswer(false);
        }
    }
    public void HandleUserTextSubmitted(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        UserTextReceived?.Invoke(text);
        AddHistory("user", text);
        AddChatLogMessage(GetUserChatLogLabel(), text);
        WaitingStarted?.Invoke();

        if (!emitSocketRequestEvent)
        {
            WaitingFinished?.Invoke();
            return;
        }   
        petController.isUserSpeakingTurn=false;
        RequestSocketAnswer(false);
    }

    public void ReceiveAiAnswer(ChatMessage message)
    {
        var answer = GetMessageText(message);
        if (IsModelFailureAnswer(answer))
        {
            pendingFullHistoryRequest = false;
            WaitingFinished?.Invoke();
            if (petController != null)
            {
                petController.SetUserSpeakingStatus();
            }
            ReportFailure(answer);
            return;
        }

        if (!TryParseModelReply(answer, out var reply, out var parseError))
        {
            pendingFullHistoryRequest = false;
            WaitingFinished?.Invoke();
            if (petController != null)
            {
                petController.SetUserSpeakingStatus();
            }
            ReportFailure("AI answer is not valid reply JSON: " + parseError + " Raw answer: " + answer);
            return;
        }

        if (IsHistoryRequireAction(reply.action))
        {
            if (!pendingFullHistoryRequest)
            {
                Log("AI requested full dialogue history.");
                RequestSocketAnswer(true);
                return;
            }

            pendingFullHistoryRequest = false;
            WaitingFinished?.Invoke();
            ReportFailure("AI requested full dialogue history again after it was already provided.");
            return;
        }

        pendingFullHistoryRequest = false;
        AddHistory(CreateAssistantHistoryMessage(reply));
        if (IsWaitAction(reply.action))
        {
            WaitingFinished?.Invoke();
            if (petController != null)
            {
                petController.SetUserSpeakingStatus();
            }
            return;
        }
        
        var cnText = reply.cn;
        var mood = reply.mood;
        var expression = reply.expression;
        var jaText = reply.tts;
        AiAnswerReceived?.Invoke(cnText);

        if (reply.rawTextFallback)
        {
            Log("AI raw text fallback. text=\"" + cnText + "\"");
        }
        else
        {
            Log("AI reply parsed. cn=\"" + cnText + "\", mood=\"" + mood + "\", expression=\"" + expression + "\", tts=\"" + jaText + "\"");
        }
        var displayRole = string.IsNullOrWhiteSpace(message.role) ? "model" : "atri";
        ReplyParsed?.Invoke(cnText, mood, jaText);

        if (petController != null)
        {
            petController.ReceiveParsedAiAnswer(cnText, expression);
        }

        if (!string.IsNullOrWhiteSpace(jaText))
        {
            Log("Requesting voice synthesis. mood=\"" + mood + "\", text=\"" + jaText + "\"");
            pendingAiLogRole = displayRole;
            pendingAiLogText = cnText;
            VoiceSynthesisRequested?.Invoke(jaText, mood);
        }
        else
        {
            AddChatLogMessage(displayRole, cnText);
            WaitingFinished?.Invoke();
            if (reply.rawTextFallback)
            {
                ReportFailure("AI returned plain text instead of reply JSON; displayed text only, voice synthesis skipped. Raw answer: " + answer);
            }
            else
            {
                ReportFailure("AI JSON reply has no tts text, voice synthesis skipped. Raw answer: " + answer);
            }
        }
    }

    public void FlushPendingAiLogMessage()
    {
        if (string.IsNullOrWhiteSpace(pendingAiLogText))
        {
            WaitingFinished?.Invoke();
            return;
        }

        AddChatLogMessage(string.IsNullOrWhiteSpace(pendingAiLogRole) ? "model" : pendingAiLogRole, pendingAiLogText);
        pendingAiLogRole = "";
        pendingAiLogText = "";
        WaitingFinished?.Invoke();
    }

    public void FailPendingVoiceMessage(string reason)
    {
        FlushPendingAiLogMessage();
        if (!string.IsNullOrWhiteSpace(reason))
        {
            ReportFailure(reason);
        }
    }

    public void ReceiveAiAnswer(string answer)
    {
        ReceiveAiAnswer(new ChatMessage
        {
            role = "assistant",
            content = answer
        });
    }

    public void ReceiveSocketAnswerJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            WaitingFinished?.Invoke();
            ReportFailure("Empty Socket.IO answer.");
            return;
        }

        if (!TryReadAnswerResponse(json, out var answer, out var error))
        {
            WaitingFinished?.Invoke();
            ReportFailure(error);
            return;
        }

        ReceiveAiAnswer(new ChatMessage
        {
            role = "assistant",
            content = answer
        });
    }

    public string BuildSocketPayloadJson()
    {
        return BuildSocketPayloadJson(false);
    }

    public string BuildSocketPayloadJson(bool includeFullHistory)
    {
        return JsonUtility.ToJson(BuildSocketPayload(includeFullHistory));
    }

    public string BuildSummaryUpdatePayloadJson()
    {
        var messages = BuildRequestMessages(false);
        messages.Add(CreateMessage("user", summaryUpdatePrompt));
        return JsonUtility.ToJson(new SummaryHistoryServerPayload
        {
            data = ToChatEnvelopes(messages),
            username = username,
            token = authToken,
            model_name = modelName,
            pet_id = petId,
            special = FormatHistoryTime(chatBeginTime ?? DateTime.Now)
        });
    }

    public SocketCallPayload BuildSocketPayload()
    {
        return BuildSocketPayload(false);
    }

    public SocketCallPayload BuildSocketPayload(bool includeFullHistory)
    {
        return new SocketCallPayload
        {
            data = keepHistory ? ToChatEnvelopes(BuildRequestMessages(includeFullHistory)) : Array.Empty<ChatEnvelope>(),
            username = username,
            token = authToken,
            model_name = modelName,
            pet_id = petId,
            special = ""
        };
    }

    private static ChatEnvelope[] ToChatEnvelopes(IReadOnlyList<ChatMessage> messages)
    {
        if (messages == null || messages.Count == 0)
        {
            return Array.Empty<ChatEnvelope>();
        }

        var envelopes = new ChatEnvelope[messages.Count];
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            envelopes[i] = new ChatEnvelope
            {
                role = NormalizePromptRole(message?.role),
                content = message?.content ?? ""
            };
        }

        return envelopes;
    }

    private void RequestSocketAnswer(bool includeFullHistory)
    {
        pendingFullHistoryRequest = includeFullHistory;
        var payloadJson = BuildSocketPayloadJson(includeFullHistory);
        Debug.Log("AI request context. includeFullHistory=" + includeFullHistory + ", event=" + getAnswerEventName + ", payload=" + payloadJson, this);
        SocketAnswerRequested?.Invoke(getAnswerEventName, payloadJson);
    }

    public void ClearHistory()
    {
        presetHistory.Clear();
        dialogueHistory.Clear();
        currentSessionHistory.Clear();
        history.Clear();
        chatBeginTime = null;
        historySaved = false;
    }

    public void SetModelName(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "deepseek";
        }

        modelName = value;
        CacheController();
        if (socketClient != null)
        {
            socketClient.SetModelName(modelName);
        }

        Log("AI model switched to: " + modelName);
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

    public void SetModelByIndex(int index)
    {
        switch (index)
        {
            case 0:
                SetModelName("gpt");
                break;
            case 1:
                SetModelName("gemini");
                break;
            case 2:
                SetModelName("deepseek");
                break;
            default:
                Debug.LogWarning("Unsupported AI model dropdown index: " + index, this);
                break;
        }
    }

    public void SetGeminiModelEnabled(bool enabled)
    {
        SetModelName(enabled ? "gemini" : "deepseek");
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

        if (chatLog == null)
        {
            chatLog = FindFirstObjectByType<ChatLogCtrl>();
        }

        if (socketClient == null)
        {
            socketClient = GetComponent<DesktopPetSocketIOClient>();
        }

        if (socketClient == null)
        {
            socketClient = GetComponentInParent<DesktopPetSocketIOClient>();
        }

        if (socketClient == null)
        {
            socketClient = FindFirstObjectByType<DesktopPetSocketIOClient>();
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

    private void PreserveLegacyHistoryFileFields()
    {
        _ = dialogueHistoryPath;
        _ = createMissingDialogueHistoryFile;
        _ = userHistoryLabel;
        _ = modelHistoryLabel;
    }

    private void AddHistory(string role, string text)
    {
        AddHistory(CreateMessage(role, text));
    }

    private void AddHistory(ChatMessage message)
    {
        if (!keepHistory || message == null)
        {
            return;
        }

        dialogueHistory.Add(message);
        currentSessionHistory.Add(message);
        if (!chatBeginTime.HasValue)
        {
            chatBeginTime = DateTime.Now;
        }

        historySaved = false;
        RebuildHistory();
    }

    private static string GetMessageText(ChatMessage message)
    {
        if (message == null)
        {
            return "";
        }

        return message.content ?? "";
    }

    private static ChatMessage CreateAssistantHistoryMessage(ModelReply reply)
    {
        var message = CreateMessage("assistant", reply != null ? reply.content : "");
        if (reply == null)
        {
            return message;
        }

        message.action = reply.action;
        if (IsReplyAction(reply.action))
        {
            message.mood = reply.mood;
            message.expression = reply.expression;
        }

        return message;
    }

    private bool TryParseModelReply(string text, out ModelReply reply, out string error)
    {
        reply = null;
        error = "";

        try
        {
            var payload = Newtonsoft.Json.Linq.JObject.Parse((text ?? "").Trim());
            var action = (payload.SelectToken("action")?.ToString() ?? "reply").Trim();
            if (string.IsNullOrWhiteSpace(action))
            {
                action = "reply";
            }

            action = action.ToLowerInvariant();
            if (!IsReplyAction(action) && !IsWaitAction(action) && !IsHistoryRequireAction(action))
            {
                error = "unsupported action: " + action;
                return false;
            }

            reply = new ModelReply
            {
                action = action,
                cn = (payload.SelectToken("cn") ?? payload.SelectToken("content"))?.ToString()?.Trim() ?? "",
                tts = (payload.SelectToken("tts") ?? payload.SelectToken("ja"))?.ToString()?.Trim() ?? "",
                mood = NormalizeMoodText(payload.SelectToken("mood")?.ToString()),
                expression = NormalizeMoodText(payload.SelectToken("expression")?.ToString())
            };

            if (!IsReplyAction(action))
            {
                reply.content = string.IsNullOrWhiteSpace(reply.cn) ? action : reply.cn;
                return true;
            }

            if (string.IsNullOrWhiteSpace(reply.cn) || string.IsNullOrWhiteSpace(reply.tts))
            {
                error = "reply action requires cn and tts";
                return false;
            }

            if (!ContainsKeyword(availableMoodKeywords, reply.mood))
            {
                reply.mood = availableMoodKeywords.Length > 0 ? availableMoodKeywords[0] : "normal";
            }

            if (!ContainsKeyword(availableExpressionKeywords, reply.expression))
            {
                reply.expression = ContainsKeyword(availableExpressionKeywords, "normal")
                    ? "normal"
                    : availableExpressionKeywords.Length > 0 ? availableExpressionKeywords[0] : "";
            }

            reply.content = reply.cn;
            return true;
        }
        catch (Exception exc)
        {
            var fallbackText = (text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(fallbackText))
            {
                error = exc.Message;
                return false;
            }

            reply = new ModelReply
            {
                action = "reply",
                content = fallbackText,
                cn = fallbackText,
                tts = "",
                mood = "normal",
                expression = "normal",
                rawTextFallback = true
            };
            return true;
        }
    }

    private static bool ContainsKeyword(string[] keywords, string value)
    {
        if (keywords == null)
        {
            return false;
        }

        for (var i = 0; i < keywords.Length; i++)
        {
            if (string.Equals(keywords[i], value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReplyAction(string action)
    {
        return string.Equals(action, "reply", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistoryRequireAction(string action)
    {
        return string.Equals(action, "history_require", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWaitAction(string action)
    {
        return string.Equals(action, "wait", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistoryRequireAnswer(string text)
    {
        return (text ?? "").IndexOf("history_require", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsWaitAnswer(string text)
    {
        return (text ?? "").IndexOf("wait", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsModelFailureAnswer(string text)
    {
        text = text ?? "";
        return text.IndexOf("模型请求超时或连接中断", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("联网模型请求失败", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("Upstream request failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("InternalServerError", StringComparison.OrdinalIgnoreCase) >= 0 ||
               text.IndexOf("Error code: 502", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void AddChatLogMessage(string role, string text)
    {
        if (chatLog == null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        role = ResolveChatLogRole(role);
        chatLog.AddMessage(role, text);
    }

    private string ResolveChatLogRole(string role)
    {
        if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, "superpai", StringComparison.OrdinalIgnoreCase))
        {
            return GetUserChatLogLabel();
        }

        return string.IsNullOrWhiteSpace(role) ? "model" : role;
    }

    private string GetUserChatLogLabel()
    {
        if (!string.IsNullOrWhiteSpace(username))
        {
            return username.Trim();
        }

        return string.IsNullOrWhiteSpace(userHistoryLabel) ? "user" : userHistoryLabel.Trim();
    }

    private void RebuildHistory()
    {
        history.Clear();

        if (!keepHistory)
        {
            return;
        }

        history.AddRange(presetHistory);
        history.AddRange(summaryHistory);
        if (!string.IsNullOrWhiteSpace(recentHistoryIntro))
        {
            history.Add(CreateMessage("system", recentHistoryIntro));
        }
        var oldHistoryStart = Mathf.Max(0, dialogueHistory.Count - currentSessionHistory.Count - Mathf.Max(0, maxHistoryEntries));
        for (var i = oldHistoryStart; i < dialogueHistory.Count; i++)
        {
            history.Add(dialogueHistory[i]);
        }
    }

    private List<ChatMessage> BuildPromptHistory(bool includeFullHistory)
    {
        if (!includeFullHistory)
        {
            return history;
        }

        promptHistory.Clear();
        promptHistory.AddRange(presetHistory);
        promptHistory.AddRange(summaryHistory);
        if (!string.IsNullOrWhiteSpace(recentHistoryIntro))
        {
            promptHistory.Add(CreateMessage("system", recentHistoryIntro));
        }
        promptHistory.AddRange(dialogueHistory);
        promptHistory.Add(CreateMessage("user", "[系统消息]:你已经请求并且获得了全部对话历史"));
        return promptHistory;
    }

    private List<ChatMessage> BuildRequestMessages(bool includeFullHistory)
    {
        var messages = new List<ChatMessage>(BuildPromptHistory(includeFullHistory));
        if (!string.IsNullOrWhiteSpace(finalReplyFormatReminder))
        {
            messages.Add(CreateMessage("user", finalReplyFormatReminder));
        }

        return messages;
    }

    private static ChatMessage CreateMessage(string role, string text)
    {
        return new ChatMessage
        {
            role = NormalizePromptRole(role),
            content = text
        };
    }

    private static string NormalizePromptRole(string role)
    {
        role = (role ?? "").Trim().ToLowerInvariant();
        if (role == "system" || role == "user" || role == "assistant")
        {
            return role;
        }

        if (role == "developer")
        {
            return "system";
        }

        if (role == "model" || role == "atri")
        {
            return "assistant";
        }

        return "user";
    }

    private int AddDialogueHistoryFromText(string text)
    {
        var parsed = ParseDialogueHistoryText(text);

        dialogueHistory.AddRange(parsed);
        return parsed.Count;
    }

    private int AddSummaryHistoryFromText(string text)
    {
        var parsed = ParseDialogueHistoryText(text);

        summaryHistory.AddRange(parsed);
        return parsed.Count;
    }

    private List<ChatMessage> ParseDialogueHistoryText(string text)
    {
        var messages = new List<ChatMessage>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return messages;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryParseHistoryJsonLine(line, messages))
            {
                Debug.LogWarning("Dialogue history line is not valid JSONL and was ignored: " + line);
            }
        }

        return messages;
    }

    private static bool TryParseHistoryJsonLine(string line, List<ChatMessage> messages)
    {
        try
        {
            var obj = Newtonsoft.Json.Linq.JObject.Parse(line);
            var eventType = obj.SelectToken("type")?.ToString() ?? "";
            var startedAt = obj.SelectToken("started_at")?.ToString() ?? "";
            var endedAt = obj.SelectToken("ended_at")?.ToString() ?? "";
            var marker = (obj.SelectToken("marker") ?? obj.SelectToken("source"))?.ToString() ?? "";

            if (eventType.EndsWith("session_start", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(startedAt))
            {
                messages.Add(CreateMessage("system", "【历史会话信息】一段历史对话开始于 " + startedAt + "，记录标记：" + marker + "。"));
            }

            var items = obj.SelectToken("messages") as Newtonsoft.Json.Linq.JArray;
            if (items != null)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var messageObj = items[i] as Newtonsoft.Json.Linq.JObject;
                    if (messageObj == null)
                    {
                        continue;
                    }

                    var role = messageObj.SelectToken("role")?.ToString() ?? "user";
                    var content = (messageObj.SelectToken("content") ?? messageObj.SelectToken("cn") ?? messageObj.SelectToken("text"))?.ToString() ?? "";
                    content = content.Trim();
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    messages.Add(CreateMessage(role, content));
                }
            }

            if (eventType.EndsWith("session_end", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(endedAt))
            {
                messages.Add(CreateMessage("system", "【历史会话信息】上一段历史对话结束于 " + endedAt + "。"));
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TrimHistoryList(List<ChatMessage> messages, int maxEntries)
    {
        if (messages == null || maxEntries <= 0)
        {
            return;
        }

        while (messages.Count > maxEntries)
        {
            messages.RemoveAt(0);
        }
    }

    private System.Collections.IEnumerator AppendCurrentSessionHistoryRoutine()
    {
        var text = BuildCurrentSessionHistoryText();

        historySaveInProgress = true;
        yield return EnsureHistoryServerConfigLoaded();
        if (UseLocalMemoryStorage())
        {
            if (!TryAppendLocalMemoryText(serverConfig.LocalDialogueHistoryFile, text, out var localError))
            {
                historySaveInProgress = false;
                Debug.LogWarning("Failed to save chat history locally: " + localError, this);
                yield break;
            }

            historySaveInProgress = false;
            historySaved = true;
            Log("Chat history saved to local memory.");
            if (useSummaryHistory)
            {
                Log("Summary history update skipped in local memory mode.");
            }

            currentSessionHistory.Clear();
            chatBeginTime = null;
            RebuildHistory();
            yield break;
        }

        dialogueHistorySaveFinished = false;
        dialogueHistorySaveError = "";
        DialogueHistoryRequested?.Invoke(saveDialogueHistoryEventName, BuildHistoryServerPayloadJson(text));

        var timeoutAt = Time.realtimeSinceStartup + GetHistoryRequestTimeoutSeconds();
        while (!dialogueHistorySaveFinished && Time.realtimeSinceStartup < timeoutAt)
        {
            yield return null;
        }

        historySaveInProgress = false;
        var error = dialogueHistorySaveFinished ? dialogueHistorySaveError : "Socket.IO save_dialogue_history timed out.";
        if (!string.IsNullOrWhiteSpace(error))
        {
            Debug.LogWarning("Failed to save chat history: " + error, this);
            yield break;
        }

        historySaved = true;
        Log("Chat history saved to server.");
        yield return UpdateSummaryHistoryRoutine();
        currentSessionHistory.Clear();
        chatBeginTime = null;
        RebuildHistory();
    }

    private System.Collections.IEnumerator UpdateSummaryHistoryRoutine()
    {
        if (!useSummaryHistory || currentSessionHistory.Count == 0)
        {
            yield break;
        }

        summaryHistorySaveFinished = false;
        summaryHistorySaveError = "";
        DialogueHistoryRequested?.Invoke(updateSummaryHistoryEventName, BuildSummaryUpdatePayloadJson());

        var saveTimeoutAt = Time.realtimeSinceStartup + GetHistoryRequestTimeoutSeconds();
        while (!summaryHistorySaveFinished && Time.realtimeSinceStartup < saveTimeoutAt)
        {
            yield return null;
        }

        var saveError = summaryHistorySaveFinished ? summaryHistorySaveError : "Socket.IO update_summary_history timed out.";
        if (!string.IsNullOrWhiteSpace(saveError))
        {
            Debug.LogWarning("Failed to save summary history: " + saveError, this);
            yield break;
        }

        Log("Summary history updated.");
    }

    private string BuildCurrentSessionHistoryText()
    {
        var builder = new StringBuilder();
        var startedAt = FormatHistoryTime(chatBeginTime ?? DateTime.Now);
        AppendHistoryRow(builder, "dialogue_session_start", Array.Empty<ChatMessage>(), startedAt, "", "唤醒atri--移动端");

        for (var i = 0; i < currentSessionHistory.Count; i++)
        {
            AppendHistoryRow(builder, "dialogue_message", new[] { currentSessionHistory[i] });
        }

        var endedAt = FormatHistoryTime(DateTime.Now);
        AppendHistoryRow(builder, "dialogue_session_end", Array.Empty<ChatMessage>(), "", endedAt, "");

        return builder.ToString();
    }

    private string BuildSummaryHistoryText(string summary)
    {
        var builder = new StringBuilder();
        var startedAt = FormatHistoryTime(chatBeginTime ?? DateTime.Now);
        AppendHistoryRow(builder, "summary_session_start", Array.Empty<ChatMessage>(), startedAt, "", "唤醒atri--移动端");

        var parts = (summary ?? "").Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var content = parts[i].Trim();
            if (!string.IsNullOrWhiteSpace(content))
            {
                AppendHistoryRow(builder, "summary_message", new[] { CreateMessage("user", content) });
            }
        }

        var endedAt = FormatHistoryTime(DateTime.Now);
        AppendHistoryRow(builder, "summary_session_end", Array.Empty<ChatMessage>(), "", endedAt, "");

        return builder.ToString();
    }

    private static string FormatHistoryTime(DateTime time)
    {
        return time.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static void AppendHistoryRow(
        StringBuilder builder,
        string type,
        ChatMessage[] messages,
        string startedAt = "",
        string endedAt = "",
        string marker = "")
    {
        var row = new Newtonsoft.Json.Linq.JObject
        {
            ["type"] = type ?? "dialogue_message"
        };

        if (!string.IsNullOrWhiteSpace(startedAt))
        {
            row["started_at"] = startedAt;
        }

        if (!string.IsNullOrWhiteSpace(endedAt))
        {
            row["ended_at"] = endedAt;
        }

        if (!string.IsNullOrWhiteSpace(marker))
        {
            row["marker"] = marker;
        }

        var array = new Newtonsoft.Json.Linq.JArray();
        for (var i = 0; messages != null && i < messages.Length; i++)
        {
            var message = messages[i];
            if (message == null || string.IsNullOrWhiteSpace(message.content))
            {
                continue;
            }

            var item = new Newtonsoft.Json.Linq.JObject
            {
                ["role"] = NormalizePromptRole(message.role),
                ["content"] = message.content
            };

            if (!string.IsNullOrWhiteSpace(message.action))
            {
                item["action"] = message.action;
            }

            if (!string.IsNullOrWhiteSpace(message.mood))
            {
                item["mood"] = message.mood;
            }

            if (!string.IsNullOrWhiteSpace(message.expression))
            {
                item["expression"] = message.expression;
            }

            array.Add(item);
        }

        row["messages"] = array;
        builder.Append(row.ToString(Newtonsoft.Json.Formatting.None));
        builder.Append('\n');
    }

    private string BuildHistoryServerPayloadJson(string text)
    {
        return JsonUtility.ToJson(new HistoryServerPayload
        {
            username = username,
            token = authToken,
            model_name = modelName,
            pet_id = petId,
            special = "",
            text = text ?? "",
            data = text ?? "",
            dialogue_history = text ?? ""
        });
    }

    private float GetHistoryRequestTimeoutSeconds()
    {
        return Mathf.Max(60f, historyRequestTimeoutSeconds);
    }

    private System.Collections.IEnumerator EnsureHistoryServerConfigLoaded()
    {
        CacheServerConfig();
        if (serverConfig != null && !serverConfig.IsLoaded)
        {
            yield return serverConfig.LoadRoutine();
        }

        if (serverConfig != null && serverConfig.IsLoaded)
        {
            serverUrl = NormalizeServerUrl(serverConfig.SocketServerUrl, serverUrl);
            Log("Server config path: " + serverConfig.ResolvedConfigPath + ", auth url: " + serverUrl);
            ApplyServerAuthConfig();
        }
        else
        {
            serverUrl = NormalizeServerUrl(serverUrl, "http://127.0.0.1:5000");
        }
    }

    private System.Collections.IEnumerator EnsureAppAuthReady()
    {
        yield return EnsureHistoryServerConfigLoaded();
        if (serverConfig == null || !serverConfig.AppAuthRequired)
        {
            yield break;
        }

        if (serverConfig.HasAppAuth)
        {
            var verified = false;
            yield return VerifySavedAppAuth(result => verified = result);
            if (verified)
            {
                ApplyServerAuthConfig();
                yield break;
            }

            serverConfig.ClearAppAuth();
        }

        appAuthChoiceAnswered = false;
        ShowAppAuthDialog();
        while (!appAuthChoiceAnswered)
        {
            yield return null;
        }
    }

    private void ApplyServerAuthConfig()
    {
        if (serverConfig == null || !serverConfig.HasAppAuth)
        {
            return;
        }

        username = serverConfig.AppUsername;
        authToken = serverConfig.AppAuthToken;
        userHistoryLabel = username;
        CacheController();
        if (socketClient != null)
        {
            socketClient.SetUserAuth(username, authToken);
        }
    }

    private System.Collections.IEnumerator VerifySavedAppAuth(Action<bool> onCompleted)
    {
        if (serverConfig == null || !serverConfig.HasAppAuth)
        {
            onCompleted?.Invoke(false);
            yield break;
        }

        var payload = JsonUtility.ToJson(new AuthRequestPayload
        {
            username = serverConfig.AppUsername,
            token = serverConfig.AppAuthToken
        });

        string responseText = "";
        string error = "";
        yield return PostAuthJson("/api/verify", payload, (ok, text, err) =>
        {
            responseText = text;
            error = err;
        });

        if (!string.IsNullOrWhiteSpace(error))
        {
            Log("Saved auth verify failed: " + error);
            onCompleted?.Invoke(false);
            yield break;
        }

        try
        {
            var response = JsonUtility.FromJson<AuthResponsePayload>(responseText);
            onCompleted?.Invoke(response != null && response.valid);
        }
        catch
        {
            onCompleted?.Invoke(false);
        }
    }

    private void ShowAppAuthDialog()
    {
        if (appAuthDialog != null)
        {
            Destroy(appAuthDialog);
        }

        EnsureEventSystem();

        appAuthDialog = new GameObject("AppAuthDialog");
        var canvas = appAuthDialog.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;
        var scaler = appAuthDialog.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);
        scaler.matchWidthOrHeight = 0.5f;
        appAuthDialog.AddComponent<GraphicRaycaster>();

        var root = appAuthDialog.GetComponent<RectTransform>();
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero;
        root.offsetMax = Vector2.zero;

        var blocker = CreateRect("Blocker", root);
        var blockerImage = blocker.gameObject.AddComponent<Image>();
        blockerImage.color = new Color(0f, 0f, 0f, 0.66f);
        blocker.anchorMin = Vector2.zero;
        blocker.anchorMax = Vector2.one;
        blocker.offsetMin = Vector2.zero;
        blocker.offsetMax = Vector2.zero;

        var panel = CreateRect("Panel", root);
        panel.anchorMin = new Vector2(0.5f, 0.5f);
        panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(760f, 520f);
        panel.anchoredPosition = Vector2.zero;
        var panelImage = panel.gameObject.AddComponent<Image>();
        panelImage.color = new Color(0.12f, 0.13f, 0.15f, 0.98f);

        AddText(panel, "\u767b\u5f55 ATRI \u8d26\u53f7", 36, new Vector2(0f, 185f), new Vector2(660f, 58f), TextAnchor.MiddleCenter);
        AddText(panel, "\u7528\u4e8e\u9694\u79bb AI \u8bb0\u5fc6\u548c\u8bed\u97f3\u8bf7\u6c42", 23, new Vector2(0f, 135f), new Vector2(660f, 42f), TextAnchor.MiddleCenter);

        appAuthUsernameInput = AddInputField(panel, "\u7528\u6237\u540d", new Vector2(0f, 55f), false);
        appAuthPasswordInput = AddInputField(panel, "\u5bc6\u7801", new Vector2(0f, -35f), true);
        if (serverConfig != null && !string.IsNullOrWhiteSpace(serverConfig.AppUsername))
        {
            appAuthUsernameInput.text = serverConfig.AppUsername;
        }

        appAuthFeedbackText = AddText(panel, "", 22, new Vector2(0f, -105f), new Vector2(650f, 38f), TextAnchor.MiddleCenter);
        appAuthFeedbackText.color = new Color(1f, 0.82f, 0.35f, 1f);

        appAuthLoginButton = AddChoiceButton(panel, "\u767b\u5f55", new Vector2(-170f, -180f), () => StartAppAuthRequest(false));
        appAuthRegisterButton = AddChoiceButton(panel, "\u6ce8\u518c", new Vector2(170f, -180f), () => StartAppAuthRequest(true));
    }

    private InputField AddInputField(RectTransform parent, string placeholder, Vector2 position, bool password)
    {
        var rect = CreateRect("InputField", parent);
        rect.sizeDelta = new Vector2(600f, 64f);
        rect.anchoredPosition = position;

        var image = rect.gameObject.AddComponent<Image>();
        image.color = new Color(0.95f, 0.96f, 0.98f, 1f);

        var input = rect.gameObject.AddComponent<InputField>();
        input.targetGraphic = image;
        input.contentType = password ? InputField.ContentType.Password : InputField.ContentType.Standard;
        input.lineType = InputField.LineType.SingleLine;

        var textRect = CreateRect("Text", rect);
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(18f, 4f);
        textRect.offsetMax = new Vector2(-18f, -4f);
        var text = textRect.gameObject.AddComponent<Text>();
        text.font = GetBuiltinUiFont();
        text.fontSize = 25;
        text.alignment = TextAnchor.MiddleLeft;
        text.color = Color.black;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        input.textComponent = text;

        var placeholderRect = CreateRect("Placeholder", rect);
        placeholderRect.anchorMin = Vector2.zero;
        placeholderRect.anchorMax = Vector2.one;
        placeholderRect.offsetMin = new Vector2(18f, 4f);
        placeholderRect.offsetMax = new Vector2(-18f, -4f);
        var placeholderText = placeholderRect.gameObject.AddComponent<Text>();
        placeholderText.font = GetBuiltinUiFont();
        placeholderText.fontSize = 25;
        placeholderText.alignment = TextAnchor.MiddleLeft;
        placeholderText.color = new Color(0f, 0f, 0f, 0.45f);
        placeholderText.text = placeholder;
        input.placeholder = placeholderText;

        return input;
    }

    private void StartAppAuthRequest(bool register)
    {
        if (appAuthRequestInProgress)
        {
            return;
        }

        StartCoroutine(AppAuthRequestRoutine(register));
    }

    private System.Collections.IEnumerator AppAuthRequestRoutine(bool register)
    {
        var name = appAuthUsernameInput != null ? appAuthUsernameInput.text.Trim() : "";
        var password = appAuthPasswordInput != null ? appAuthPasswordInput.text : "";
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(password))
        {
            SetAppAuthFeedback("\u8bf7\u8f93\u5165\u7528\u6237\u540d\u548c\u5bc6\u7801");
            yield break;
        }

        appAuthRequestInProgress = true;
        SetAppAuthButtonsInteractable(false);
        SetAppAuthFeedback(register ? "\u6b63\u5728\u6ce8\u518c..." : "\u6b63\u5728\u767b\u5f55...");

        var payload = JsonUtility.ToJson(new AuthRequestPayload
        {
            username = name,
            password = password
        });

        string responseText = "";
        string error = "";
        yield return PostAuthJson(register ? "/api/register" : "/api/login", payload, (ok, text, err) =>
        {
            responseText = text;
            error = err;
        });

        appAuthRequestInProgress = false;
        SetAppAuthButtonsInteractable(true);

        if (!string.IsNullOrWhiteSpace(error))
        {
            SetAppAuthFeedback(error);
            yield break;
        }

        AuthResponsePayload response = null;
        try
        {
            response = JsonUtility.FromJson<AuthResponsePayload>(responseText);
        }
        catch
        {
            SetAppAuthFeedback("\u670d\u52a1\u5668\u54cd\u5e94\u65e0\u6cd5\u89e3\u6790");
            yield break;
        }

        if (response == null || string.IsNullOrWhiteSpace(response.username) || string.IsNullOrWhiteSpace(response.token))
        {
            SetAppAuthFeedback(string.IsNullOrWhiteSpace(response?.error) ? "\u767b\u5f55\u54cd\u5e94\u7f3a\u5c11 token" : response.error);
            yield break;
        }

        serverConfig.SetAppAuth(response.username, response.token);
        ApplyServerAuthConfig();
        if (appAuthDialog != null)
        {
            Destroy(appAuthDialog);
            appAuthDialog = null;
        }

        appAuthChoiceAnswered = true;
        Log("App auth completed for user: " + response.username);
    }

    private System.Collections.IEnumerator PostAuthJson(string endpoint, string payload, Action<bool, string, string> onCompleted)
    {
        var url = NormalizeServerUrl(serverUrl, "http://127.0.0.1:5000") + endpoint;
        using (var request = new UnityWebRequest(url, "POST"))
        {
            var body = Encoding.UTF8.GetBytes(payload ?? "{}");
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = Mathf.CeilToInt(GetHistoryRequestTimeoutSeconds());
            UnityWebRequestAsyncOperation operation;
            try
            {
                operation = request.SendWebRequest();
            }
            catch (InvalidOperationException exc)
            {
                onCompleted?.Invoke(false, "", FormatAuthRequestException(url, exc));
                yield break;
            }

            yield return operation;

            var text = request.downloadHandler != null ? request.downloadHandler.text : "";
            if (request.result == UnityWebRequest.Result.Success)
            {
                onCompleted?.Invoke(true, text, "");
                yield break;
            }

            var message = TryReadAuthError(text);
            if (string.IsNullOrWhiteSpace(message))
            {
                message = request.error;
            }

            onCompleted?.Invoke(false, text, message);
        }
    }

    private static string TryReadAuthError(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return "";
        }

        try
        {
            var payload = JsonUtility.FromJson<AuthResponsePayload>(responseText);
            return payload?.error ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string FormatAuthRequestException(string url, Exception exc)
    {
        var message = exc != null ? exc.Message : "";
        if (!string.IsNullOrWhiteSpace(message) &&
            message.IndexOf("Insecure connection not allowed", StringComparison.OrdinalIgnoreCase) >= 0 &&
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "当前 APK 禁止 HTTP 请求。请用本项目重新打包，或把服务器地址改成 HTTPS：" + url;
        }

        return string.IsNullOrWhiteSpace(message) ? "认证请求失败" : message;
    }

    private void SetAppAuthButtonsInteractable(bool interactable)
    {
        if (appAuthLoginButton != null)
        {
            appAuthLoginButton.interactable = interactable;
        }

        if (appAuthRegisterButton != null)
        {
            appAuthRegisterButton.interactable = interactable;
        }
    }

    private void SetAppAuthFeedback(string message)
    {
        if (appAuthFeedbackText != null)
        {
            appAuthFeedbackText.text = message ?? "";
        }
    }

    private System.Collections.IEnumerator EnsureMemoryStorageModeSelected()
    {
        yield return EnsureHistoryServerConfigLoaded();
        if (serverConfig == null || serverConfig.MemoryStorageSelected)
        {
            yield break;
        }

        memoryStorageChoiceAnswered = false;
        ShowMemoryStorageChoiceDialog();
        while (!memoryStorageChoiceAnswered)
        {
            yield return null;
        }
    }

    private void ShowMemoryStorageChoiceDialog()
    {
        if (memoryStorageChoiceDialog != null)
        {
            Destroy(memoryStorageChoiceDialog);
        }

        EnsureEventSystem();

        memoryStorageChoiceDialog = new GameObject("MemoryStorageChoiceDialog");
        var canvas = memoryStorageChoiceDialog.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue;
        var scaler = memoryStorageChoiceDialog.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);
        scaler.matchWidthOrHeight = 0.5f;
        memoryStorageChoiceDialog.AddComponent<GraphicRaycaster>();

        var root = memoryStorageChoiceDialog.GetComponent<RectTransform>();
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero;
        root.offsetMax = Vector2.zero;

        var blocker = CreateRect("Blocker", root);
        var blockerImage = blocker.gameObject.AddComponent<Image>();
        blockerImage.color = new Color(0f, 0f, 0f, 0.62f);
        blocker.anchorMin = Vector2.zero;
        blocker.anchorMax = Vector2.one;
        blocker.offsetMin = Vector2.zero;
        blocker.offsetMax = Vector2.zero;

        var panel = CreateRect("Panel", root);
        panel.anchorMin = new Vector2(0.5f, 0.5f);
        panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(720f, 360f);
        panel.anchoredPosition = Vector2.zero;
        var panelImage = panel.gameObject.AddComponent<Image>();
        panelImage.color = new Color(0.12f, 0.13f, 0.15f, 0.98f);

        AddText(panel, "\u9996\u6b21\u4f7f\u7528\u8bf7\u9009\u62e9\u8bb0\u5fc6\u4fdd\u5b58\u4f4d\u7f6e", 34, new Vector2(0f, 105f), new Vector2(640f, 56f), TextAnchor.MiddleCenter);
        AddText(panel, "\u4e91\u7aef\uff1a\u7ee7\u7eed\u4f7f\u7528\u670d\u52a1\u7aef\u5386\u53f2\u8bb0\u5f55\u3002\n\u672c\u5730\uff1a\u5386\u53f2\u8bb0\u5f55\u4fdd\u5b58\u5728\u672c\u673a\u8bb0\u5fc6\u6587\u4ef6\u5939\u3002", 24, new Vector2(0f, 25f), new Vector2(620f, 96f), TextAnchor.MiddleCenter);

        AddChoiceButton(panel, "\u4e91\u7aef\u8bb0\u5fc6", new Vector2(-170f, -105f), () => ChooseMemoryStorageMode("cloud"));
        AddChoiceButton(panel, "\u672c\u5730\u8bb0\u5fc6", new Vector2(170f, -105f), () => ChooseMemoryStorageMode("local"));
    }

    private void ChooseMemoryStorageMode(string mode)
    {
        if (serverConfig != null)
        {
            serverConfig.SetMemoryStorageMode(mode);
        }

        if (memoryStorageChoiceDialog != null)
        {
            Destroy(memoryStorageChoiceDialog);
            memoryStorageChoiceDialog = null;
        }

        memoryStorageChoiceAnswered = true;
        Log("Memory storage mode selected: " + mode);
    }

    private static void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null)
        {
            return;
        }

        var eventSystem = new GameObject("EventSystem");
        eventSystem.AddComponent<EventSystem>();
        eventSystem.AddComponent<StandaloneInputModule>();
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        return obj.AddComponent<RectTransform>();
    }

    private static Text AddText(RectTransform parent, string text, int fontSize, Vector2 position, Vector2 size, TextAnchor alignment)
    {
        var rect = CreateRect("Text", parent);
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
        var label = rect.gameObject.AddComponent<Text>();
        label.font = GetBuiltinUiFont();
        label.text = text;
        label.fontSize = fontSize;
        label.alignment = alignment;
        label.color = Color.white;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        return label;
    }

    private static Button AddChoiceButton(RectTransform parent, string text, Vector2 position, UnityEngine.Events.UnityAction action)
    {
        var rect = CreateRect("ChoiceButton", parent);
        rect.sizeDelta = new Vector2(240f, 68f);
        rect.anchoredPosition = position;

        var image = rect.gameObject.AddComponent<Image>();
        image.color = new Color(0.24f, 0.43f, 0.78f, 1f);

        var button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(action);

        AddText(rect, text, 26, Vector2.zero, rect.sizeDelta, TextAnchor.MiddleCenter);
        return button;
    }

    private static Font GetBuiltinUiFont()
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font != null)
        {
            return font;
        }

        return Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    private bool UseLocalMemoryStorage()
    {
        return serverConfig != null &&
               string.Equals(serverConfig.MemoryStorageMode, "local", StringComparison.OrdinalIgnoreCase);
    }

    private string LoadLocalMemoryText(string fileName)
    {
        var path = GetLocalMemoryFilePath(fileName);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return "";
        }

        try
        {
            return File.ReadAllText(path, System.Text.Encoding.UTF8);
        }
        catch (Exception exc)
        {
            Debug.LogWarning("Failed to load local memory file: " + path + " " + exc.Message, this);
            return "";
        }
    }

    private bool TryAppendLocalMemoryText(string fileName, string text, out string error)
    {
        error = "";
        var path = GetLocalMemoryFilePath(fileName);
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "local memory file path is empty.";
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, text ?? "", System.Text.Encoding.UTF8);
            return true;
        }
        catch (Exception exc)
        {
            error = exc.Message;
            return false;
        }
    }

    private string GetLocalMemoryFilePath(string fileName)
    {
        var folder = serverConfig != null ? serverConfig.LocalMemoryFolder : "\u8bb0\u5fc6";
        folder = string.IsNullOrWhiteSpace(folder) ? "\u8bb0\u5fc6" : folder.Trim();
        fileName = string.IsNullOrWhiteSpace(fileName) ? "dialogue_history.txt" : fileName.Trim();

        if (Path.IsPathRooted(fileName))
        {
            return fileName;
        }

        var baseFolder = Path.IsPathRooted(folder)
            ? folder
            : DesktopPetResourcePath.GetWritableModelPath(folder);

        return Path.Combine(baseFolder, GetSafeLocalUserFolderName(), fileName);
    }

    private string GetSafeLocalUserFolderName()
    {
        var value = string.IsNullOrWhiteSpace(username) ? "guest" : username.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        value = Path.GetFileName(value);
        return string.IsNullOrWhiteSpace(value) ? "guest" : value;
    }

    private static bool TryReadDialogueHistoryResponse(string response, out string text, out string error)
    {
        text = "";
        error = "";
        response = response ?? "";

        if (string.IsNullOrWhiteSpace(response))
        {
            return true;
        }

        try
        {
            var token = Newtonsoft.Json.Linq.JToken.Parse(response);
            if (token is Newtonsoft.Json.Linq.JArray array)
            {
                if (array.Count > 0 && IsSuccessToken(array[0]))
                {
                    text = array.Count > 1 ? array[1]?.ToString() ?? "" : "";
                    return true;
                }

                error = array.Count > 1 ? array[1]?.ToString() ?? response : response;
                return false;
            }

            if (token is Newtonsoft.Json.Linq.JObject obj)
            {
                var status = obj.SelectToken("status") ?? obj.SelectToken("result") ?? obj.SelectToken("code");
                if (status == null || IsSuccessToken(status))
                {
                    text = (obj.SelectToken("data") ?? obj.SelectToken("text") ?? obj.SelectToken("history"))?.ToString() ?? "";
                    return true;
                }

                error = (obj.SelectToken("message") ?? obj.SelectToken("error") ?? obj.SelectToken("detail"))?.ToString() ?? response;
                return false;
            }
        }
        catch
        {
            text = response;
            return true;
        }

        error = response;
        return false;
    }

    private static bool TryReadAnswerResponse(string response, out string text, out string error)
    {
        text = "";
        error = "";
        response = response ?? "";

        if (string.IsNullOrWhiteSpace(response))
        {
            error = "answer response is empty.";
            return false;
        }

        try
        {
            var token = Newtonsoft.Json.Linq.JToken.Parse(response);
            if (token is Newtonsoft.Json.Linq.JValue value)
            {
                text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return true;
                }

                error = "answer response string is empty.";
                return false;
            }

            if (token is Newtonsoft.Json.Linq.JObject obj)
            {
                var errorToken = obj.SelectToken("error") ?? obj.SelectToken("detail");
                if (errorToken != null && obj.SelectToken("content") == null)
                {
                    error = errorToken.ToString();
                    return false;
                }

                text = (obj.SelectToken("content") ?? obj.SelectToken("text") ?? obj.SelectToken("data"))?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return true;
                }
            }
        }
        catch
        {
            text = response;
            return true;
        }

        error = "answer response has no content: " + response;
        return false;
    }

    private static bool IsSuccessResponse(string response, out string error)
    {
        error = "";
        response = response ?? "";

        if (string.IsNullOrWhiteSpace(response))
        {
            return true;
        }

        if (string.Equals(response.Trim(), "success", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var token = Newtonsoft.Json.Linq.JToken.Parse(response);
            if (token is Newtonsoft.Json.Linq.JValue value)
            {
                if (IsSuccessToken(value))
                {
                    return true;
                }

                error = value.ToString();
                return false;
            }

            if (token is Newtonsoft.Json.Linq.JArray array)
            {
                if (array.Count == 0 || IsSuccessToken(array[0]))
                {
                    return true;
                }

                error = array.Count > 1 ? array[1]?.ToString() ?? response : response;
                return false;
            }

            if (token is Newtonsoft.Json.Linq.JObject obj)
            {
                var objectError = obj.SelectToken("error") ?? obj.SelectToken("detail") ?? obj.SelectToken("message");
                var status = obj.SelectToken("status") ?? obj.SelectToken("result") ?? obj.SelectToken("code");
                if (objectError != null && (status == null || !IsSuccessToken(status)))
                {
                    error = objectError.ToString();
                    return false;
                }

                if (status == null || IsSuccessToken(status))
                {
                    return true;
                }

                error = (obj.SelectToken("message") ?? obj.SelectToken("error") ?? obj.SelectToken("detail"))?.ToString() ?? response;
                return false;
            }
        }
        catch
        {
            error = response;
            return false;
        }

        return true;
    }

    private static bool IsSuccessToken(Newtonsoft.Json.Linq.JToken token)
    {
        if (token == null)
        {
            return false;
        }

        var value = token.ToString();
        return string.Equals(value, "success", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "200", StringComparison.OrdinalIgnoreCase);
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

    private void ReportFailure(string message)
    {
        Debug.LogWarning(message, this);
        WaitingFinished?.Invoke();
        RequestFailed?.Invoke(message);
    }

    private void Log(string message)
    {
        if (logChatFlow)
        {
            Debug.Log(message, this);
        }
    }

    private static void ParseOldTemplateAnswer(string answer, out string cnText, out string mood, out string expression, out string jaText)
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
            expression = NormalizeMoodText(left.Substring(moodEnd + 1));
            if (string.IsNullOrWhiteSpace(expression))
            {
                expression = mood;
            }

            return;
        }

        cnText = left.Trim();
        mood = "normal";
        expression = "normal";
    }

    private static string NormalizeMoodText(string mood)
    {
        return (mood ?? "").Trim().Trim('*').Trim();
    }

    void Update()
    {
        CheckHeartBeat();
    }
}
