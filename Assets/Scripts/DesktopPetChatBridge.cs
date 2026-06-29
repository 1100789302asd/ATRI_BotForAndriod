using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Events;

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
    }

    [Serializable]
    public sealed class SocketCallPayload
    {
        public ChatMessage[] data;
        public string username;
        public string model_name;
        public string pet_id;
        public string special;
    }

    [Serializable]
    public sealed class HistoryServerPayload
    {
        public string username;
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
        public ChatMessage[] data;
        public string username;
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
    [SerializeField] private string petId = "atri";
    [SerializeField] private string modelName = "gpt";

    public string ModelName
    {
        get { return modelName; }
    }

    [Header("History")]
    [SerializeField] private bool keepHistory = true;
    [SerializeField] private int maxHistoryEntries = 50;
    [SerializeField] private bool useSummaryHistory = true;
    [SerializeField] private string summaryHistoryIntro = "接下来是我们对话历史的精炼版，你可以从中参考获得更详细的人设定位。";
    [SerializeField] private string summaryUpdatePrompt = "这是系统提示，对话即将结束。请把本次会话中值得长期记住的事实、偏好、关系变化和待办事项精炼总结出来，用 & 分隔多条。若没有值得长期记住的内容，只回复 NoSense。";

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
    [SerializeField] private float historyRequestTimeoutSeconds = 15f;

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
    private bool summaryHistoryUpdateSucceeded = true;
    private string pendingAiLogRole = "";
    private string pendingAiLogText = "";

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
        yield return LoadSummaryHistory();
        yield return LoadDialogueHistory();
        RebuildHistory();

        Log("History loaded. preset=" + presetHistory.Count + ", summary=" + summaryHistory.Count + ", dialogue=" + dialogueHistory.Count + ", total=" + history.Count);
    }

    private System.Collections.IEnumerator LoadPresetHistory()
    {
        if (presetHistoryPaths == null)
        {
            yield break;
        }

        for (var i = 0; i < presetHistoryPaths.Length; i++)
        {
            var path = "";
            yield return DesktopPetResourcePath.EnsureWritableModelFile(presetHistoryPaths[i], false, readyPath => path = readyPath);

            var text = "";
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                text = File.ReadAllText(path, System.Text.Encoding.UTF8);
            }
            else
            {
                yield return DesktopPetResourcePath.ReadPackagedModelText(presetHistoryPaths[i], loaded => text = loaded);
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                presetHistory.Add(CreateMessage("developer", text));
                Log("Preset history loaded: " + (!string.IsNullOrWhiteSpace(path) ? path : presetHistoryPaths[i]));
            }
            else
            {
                Log("Preset history empty or missing: " + presetHistoryPaths[i]);
            }
        }

        if (useSummaryHistory && !string.IsNullOrWhiteSpace(summaryHistoryIntro))
        {
            presetHistory.Add(CreateMessage("developer", summaryHistoryIntro));
        }

    }

    private System.Collections.IEnumerator LoadSummaryHistory()
    {
        if (!useSummaryHistory)
        {
            yield break;
        }

        yield return EnsureHistoryServerConfigLoaded();

        summaryHistoryLoadFinished = false;
        summaryHistoryLoadText = "";
        summaryHistoryLoadError = "";
        DialogueHistoryRequested?.Invoke(getSummaryHistoryEventName, BuildHistoryServerPayloadJson(""));

        var timeoutAt = Time.realtimeSinceStartup + Mathf.Max(1f, historyRequestTimeoutSeconds);
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

        dialogueHistoryLoadFinished = false;
        dialogueHistoryLoadText = "";
        dialogueHistoryLoadError = "";
        DialogueHistoryRequested?.Invoke(getDialogueHistoryEventName, BuildHistoryServerPayloadJson(""));

        var timeoutAt = Time.realtimeSinceStartup + Mathf.Max(1f, historyRequestTimeoutSeconds);
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

    private void OnApplicationQuit()
    {
    }

    private void OnDestroy()
    {
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
        // 当且仅当模型等待用户回复，并且已经开启主动模式的情况下，才开始计时，时间到了就允许模型主动开口。
        if(petController.isInitiative && Time.time-petController.lastSpeakTime>petController.speakInterval && petController.isUserSpeakingTurn)
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
        AddChatLogMessage("superpai", text);
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
            petController.SetUserSpeakingStatus();
            ReportFailure(answer);
            return;
        }

        if (IsHistoryRequireAnswer(answer))
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
        AddHistory(message);
        if (string.IsNullOrWhiteSpace(answer) || IsWaitAnswer(answer))
        {
            WaitingFinished?.Invoke();
            petController.SetUserSpeakingStatus();
            return;
        }
        
        AiAnswerReceived?.Invoke(answer);

        ParseOldTemplateAnswer(answer, out var cnText, out var mood, out var expression, out var jaText);
        Log("AI reply parsed. cn=\"" + cnText + "\", mood=\"" + mood + "\", expression=\"" + expression + "\", ja=\"" + jaText + "\"");
        var displayRole = string.IsNullOrWhiteSpace(message.role) ? "model" : "atri";
        ReplyParsed?.Invoke(cnText, mood, jaText);

        if (petController != null)
        {
            petController.ReceiveAiAnswer(answer);
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
            ReportFailure("AI answer has no Japanese text after '|', voice synthesis skipped. Raw answer: " + answer);
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

        var message = JsonUtility.FromJson<ChatMessage>(json);
        ReceiveAiAnswer(message);
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
        var messages = new List<ChatMessage>(BuildPromptHistory(false));
        messages.Add(CreateMessage("developer", summaryUpdatePrompt));
        return JsonUtility.ToJson(new SummaryHistoryServerPayload
        {
            data = messages.ToArray(),
            username = username,
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
            data = keepHistory ? BuildPromptHistory(includeFullHistory).ToArray() : Array.Empty<ChatMessage>(),
            username = username,
            model_name = modelName,
            pet_id = petId,
            special = ""
        };
    }

    private void RequestSocketAnswer(bool includeFullHistory)
    {
        pendingFullHistoryRequest = includeFullHistory;
        SocketAnswerRequested?.Invoke(getAnswerEventName, BuildSocketPayloadJson(includeFullHistory));
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
            value = "gpt";
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

    public void SetGeminiModelEnabled(bool enabled)
    {
        SetModelName(enabled ? "gemini" : "gpt");
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

        chatLog.AddMessage(role, text);
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
            history.Add(CreateMessage("developer", recentHistoryIntro));
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
            promptHistory.Add(CreateMessage("developer", recentHistoryIntro));
        }
        promptHistory.AddRange(dialogueHistory);
        promptHistory.Add(CreateMessage("developer", "【系统消息】你已经请求并且获得了全部对话历史。请基于这些历史正式回答用户，不要再次回复 history_require。"));
        return promptHistory;
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
        if (role == "developer" || role == "user" || role == "assistant")
        {
            return role;
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
        if (parsed.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            parsed.Add(CreateMessage("user", text));
        }

        dialogueHistory.AddRange(parsed);
        return parsed.Count;
    }

    private int AddSummaryHistoryFromText(string text)
    {
        var parsed = ParseDialogueHistoryText(text);
        if (parsed.Count == 0 && !string.IsNullOrWhiteSpace(text))
        {
            parsed.Add(CreateMessage("developer", text));
        }

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

            if (TryParseHistoryTimeMarker(line, out var time, out var marker))
            {
                messages.Add(CreateMessage("user", BuildHistoryTimeContext(time, marker)));
                continue;
            }

            if (TryParseHistorySpeakerLine(line, out var speaker, out var content))
            {
                var role = string.Equals(speaker, userHistoryLabel, StringComparison.OrdinalIgnoreCase)
                    ? "user"
                    : "assistant";
                messages.Add(CreateMessage(role, content));
                continue;
            }

            messages.Add(CreateMessage("user", "【历史记录备注】" + line));
        }

        return messages;
    }

    private static bool TryParseHistoryTimeMarker(string line, out string time, out string marker)
    {
        time = "";
        marker = "";

        var separator = line.IndexOf("-->", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var candidateTime = line.Substring(0, separator).Trim();
        if (!DateTime.TryParse(candidateTime, out _))
        {
            return false;
        }

        time = candidateTime;
        marker = line.Substring(separator + 3).Trim();
        return true;
    }

    private static bool TryParseHistorySpeakerLine(string line, out string speaker, out string content)
    {
        speaker = "";
        content = "";

        var open = line.IndexOf(" [", StringComparison.Ordinal);
        var close = line.LastIndexOf(']');
        if (open <= 0 || close <= open + 2)
        {
            return false;
        }

        speaker = line.Substring(0, open).Trim();
        content = line.Substring(open + 2, close - open - 2).Trim();
        return !string.IsNullOrWhiteSpace(speaker) && !string.IsNullOrWhiteSpace(content);
    }

    private static string BuildHistoryTimeContext(string time, string marker)
    {
        marker = marker ?? "";
        if (marker.IndexOf("结束对话", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "【历史会话信息】上一段历史对话结束于 " + time + "。";
        }

        if (marker.IndexOf("唤醒", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "【历史会话信息】一段历史对话开始于 " + time + "，记录标记：" + marker + "。";
        }

        return "【历史会话信息】" + time + "：" + marker;
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
        yield return UpdateSummaryHistoryRoutine();
        if (!summaryHistoryUpdateSucceeded)
        {
            historySaveInProgress = false;
            yield break;
        }

        dialogueHistorySaveFinished = false;
        dialogueHistorySaveError = "";
        DialogueHistoryRequested?.Invoke(saveDialogueHistoryEventName, BuildHistoryServerPayloadJson(text));

        var timeoutAt = Time.realtimeSinceStartup + Mathf.Max(1f, historyRequestTimeoutSeconds);
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
        currentSessionHistory.Clear();
        chatBeginTime = null;
        Log("Chat history saved to server.");
    }

    private System.Collections.IEnumerator UpdateSummaryHistoryRoutine()
    {
        summaryHistoryUpdateSucceeded = true;
        if (!useSummaryHistory || currentSessionHistory.Count == 0)
        {
            yield break;
        }

        summaryHistorySaveFinished = false;
        summaryHistorySaveError = "";
        DialogueHistoryRequested?.Invoke(updateSummaryHistoryEventName, BuildSummaryUpdatePayloadJson());

        var saveTimeoutAt = Time.realtimeSinceStartup + Mathf.Max(1f, historyRequestTimeoutSeconds);
        while (!summaryHistorySaveFinished && Time.realtimeSinceStartup < saveTimeoutAt)
        {
            yield return null;
        }

        var saveError = summaryHistorySaveFinished ? summaryHistorySaveError : "Socket.IO update_summary_history timed out.";
        if (!string.IsNullOrWhiteSpace(saveError))
        {
            summaryHistoryUpdateSucceeded = false;
            Debug.LogWarning("Failed to save summary history: " + saveError, this);
            yield break;
        }

        Log("Summary history updated.");
    }

    private string BuildCurrentSessionHistoryText()
    {
        var builder = new StringBuilder();
        builder.Append(FormatHistoryTime(chatBeginTime ?? DateTime.Now));
        builder.Append("-->唤醒atri--移动端\n");

        for (var i = 0; i < currentSessionHistory.Count; i++)
        {
            var message = currentSessionHistory[i];
            var label = string.Equals(message.role, "user", StringComparison.OrdinalIgnoreCase)
                ? userHistoryLabel
                : modelHistoryLabel;

            builder.Append(label);
            builder.Append(" [");
            builder.Append(GetMessageText(message));
            builder.Append("]\n");
        }

        builder.Append(FormatHistoryTime(DateTime.Now));
        builder.Append("-->结束对话\n\n");
        return builder.ToString();
    }

    private string BuildSummaryHistoryText(string summary)
    {
        var builder = new StringBuilder();
        builder.Append(FormatHistoryTime(chatBeginTime ?? DateTime.Now));
        builder.Append("-->唤醒atri--移动端\n");
        builder.Append((summary ?? "").Replace("&", "\n"));
        builder.Append("\n");
        builder.Append(FormatHistoryTime(DateTime.Now));
        builder.Append("-->结束对话\n\n");
        return builder.ToString();
    }

    private static string FormatHistoryTime(DateTime time)
    {
        return time.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private string BuildHistoryServerPayloadJson(string text)
    {
        return JsonUtility.ToJson(new HistoryServerPayload
        {
            username = username,
            model_name = modelName,
            pet_id = petId,
            special = "",
            text = text ?? "",
            data = text ?? "",
            dialogue_history = text ?? ""
        });
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
        }
        else
        {
            serverUrl = NormalizeServerUrl(serverUrl, "http://127.0.0.1:5000");
        }
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
        mood = (mood ?? "").Trim().Trim('*').Trim();
        if (string.Equals(mood, "\u5F7B\u5E95\u9ED1\u5316", StringComparison.OrdinalIgnoreCase))
        {
            return "\u5F7B\u5E95\u574F\u6389";
        }

        return string.Equals(mood, "\u59D4\u5C48\u60F3\u54ED", StringComparison.OrdinalIgnoreCase) ? "sad" : mood;
    }

    void Update()
    {
        CheckHeartBeat();
    }
}
