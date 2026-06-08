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
    public sealed class ChatPart
    {
        public string text;
    }

    [Serializable]
    public sealed class ChatMessage
    {
        public string role;
        public ChatPart[] parts;
    }

    [Serializable]
    public sealed class SocketCallPayload
    {
        public ChatMessage[] data;
        public string username;
    }

    [Serializable]
    public sealed class ChatEnvelope
    {
        public string role;
        public ChatPart[] parts;
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

    [Header("Controller")]
    [SerializeField] private DesktopPetMigratedController petController;
    [SerializeField] private ChatLogCtrl chatLog;
    [SerializeField] private bool bindOnEnable = true;

    [Header("Socket.IO Payload")]
    [SerializeField] private bool emitSocketRequestEvent = true;
    [SerializeField] private string getAnswerEventName = "get_answer";
    [SerializeField] private string username = "";

    [Header("History")]
    [SerializeField] private bool keepHistory = true;
    [SerializeField] private int maxHistoryEntries = 30;

    [Header("History Files")]
    [SerializeField] private bool loadHistoryOnAwake = true;
    [SerializeField] private string[] presetHistoryPaths = { "config/prompt.txt" };
    [SerializeField] private string dialogueHistoryPath = "config/chat_history.txt";
    [SerializeField] private bool createMissingDialogueHistoryFile = true;
    [SerializeField] private bool saveHistoryOnExit = true;
    [SerializeField] private string userHistoryLabel = "superpai";
    [SerializeField] private string modelHistoryLabel = "robot";

    [Header("Debug")]
    [SerializeField] private bool logChatFlow = true;

    [Header("Events")]
    public UnityEvent<string> UserTextReceived;
    public UnityEvent<string> AiAnswerReceived;
    public UnityEvent<string> RequestFailed;
    public SocketAnswerRequestEvent SocketAnswerRequested;
    public ParsedReplyEvent ReplyParsed;
    public VoiceSynthesisRequestEvent VoiceSynthesisRequested;
    public UnityEvent WaitingStarted;
    public UnityEvent WaitingFinished;

    private readonly List<ChatMessage> presetHistory = new List<ChatMessage>();
    private readonly List<ChatMessage> dialogueHistory = new List<ChatMessage>();
    private readonly List<ChatMessage> currentSessionHistory = new List<ChatMessage>();
    private readonly List<ChatMessage> history = new List<ChatMessage>();
    private DateTime? chatBeginTime;
    private bool isBound;
    private bool historySaved;
    private string pendingAiLogRole = "";
    private string pendingAiLogText = "";

    private void Reset()
    {
        CacheController();
    }

    public void LoadHistory()
    {
        StartCoroutine(LoadHistoryRoutine());
    }

    public void ClearDialogueHistory()
    {
        dialogueHistory.Clear();
        currentSessionHistory.Clear();
        chatBeginTime = null;
        historySaved = false;
        RebuildHistory();
    }

    public void SaveHistoryOnExit()
    {
        if (!saveHistoryOnExit)
        {
            return;
        }

        SaveCurrentSessionHistory();
    }

    public void SaveHistoryNow()
    {
        SaveCurrentSessionHistory();
    }

    private void SaveCurrentSessionHistory()
    {
        if (historySaved || currentSessionHistory.Count == 0)
        {
            return;
        }

        historySaved = true;
        try
        {
            AppendCurrentSessionHistory();
        }
        catch (Exception exc)
        {
            Debug.LogWarning("Failed to save chat history: " + exc.Message, this);
        }
    }

    private System.Collections.IEnumerator LoadHistoryRoutine()
    {
        presetHistory.Clear();
        dialogueHistory.Clear();

        yield return LoadPresetHistory();
        yield return LoadDialogueHistory();
        RebuildHistory();

        Log("History loaded. preset=" + presetHistory.Count + ", dialogue=" + dialogueHistory.Count + ", total=" + history.Count);
    }

    private System.Collections.IEnumerator LoadPresetHistory()
    {
        if (presetHistoryPaths == null)
        {
            yield break;
        }

        for (var i = 0; i < presetHistoryPaths.Length; i++)
        {
            var text = "";
            yield return DesktopPetResourcePath.ReadPackagedModelText(presetHistoryPaths[i], loaded => text = loaded);
            if (!string.IsNullOrWhiteSpace(text))
            {
                presetHistory.Add(CreateMessage("user", text));
                Log("Preset history loaded: " + presetHistoryPaths[i]);
            }
            else
            {
                Log("Preset history empty or missing: " + presetHistoryPaths[i]);
            }
        }
    }

    private System.Collections.IEnumerator LoadDialogueHistory()
    {
        var path = "";
        yield return DesktopPetResourcePath.EnsureWritableModelFile(dialogueHistoryPath, createMissingDialogueHistoryFile, readyPath => path = readyPath);
        var text = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? File.ReadAllText(path, System.Text.Encoding.UTF8)
            : "";

        if (!string.IsNullOrWhiteSpace(text))
        {
            dialogueHistory.Add(CreateMessage("user", text));
            Log("Dialogue history loaded: " + path);
        }
        else
        {
            Log("Dialogue history empty or missing: " + dialogueHistoryPath);
        }
    }

    private void Awake()
    {
        CacheController();
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
        SaveHistoryOnExit();
    }

    private void OnDestroy()
    {
        SaveHistoryOnExit();
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
        //当且仅当模型等待用户回复，并且已经开启主动模式的情况下，才开始计时，时间到了就允许模型主动开口。
        if(petController.isInitiative && Time.time-petController.lastSpeakTime>petController.speakInterval && petController.isUserSpeakingTurn)
        {
            AddHistory("user","heart_beat");
            petController.isUserSpeakingTurn=false;
            SocketAnswerRequested?.Invoke(getAnswerEventName, BuildSocketPayloadJson());
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
        AddChatLogMessage("user", text);
        WaitingStarted?.Invoke();

        if (!emitSocketRequestEvent)
        {
            WaitingFinished?.Invoke();
            return;
        }   
        petController.isUserSpeakingTurn=false;
        SocketAnswerRequested?.Invoke(getAnswerEventName, BuildSocketPayloadJson());
    }

    public void ReceiveAiAnswer(ChatMessage message)
    {
        var answer = GetMessageText(message);
        AddHistory(message);
        if (string.IsNullOrWhiteSpace(answer)  || answer=="wait")
        {
            WaitingFinished?.Invoke();
            petController.SetUserSpeakingStatus();
            return;
        }
        
        AiAnswerReceived?.Invoke(answer);

        ParseOldTemplateAnswer(answer, out var cnText, out var mood, out var jaText);
        Log("AI reply parsed. cn=\"" + cnText + "\", mood=\"" + mood + "\", ja=\"" + jaText + "\"");
        var displayRole = string.IsNullOrWhiteSpace(message.role) ? "model" : message.role;
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
            role = "model",
            parts = new[] { new ChatPart { text = answer } }
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
        return JsonUtility.ToJson(BuildSocketPayload());
    }

    public SocketCallPayload BuildSocketPayload()
    {
        return new SocketCallPayload
        {
            data = keepHistory ? history.ToArray() : Array.Empty<ChatMessage>(),
            username = username
        };
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
        while (dialogueHistory.Count > maxHistoryEntries)
        {
            dialogueHistory.RemoveAt(0);
        }

        RebuildHistory();
    }

    private static string GetMessageText(ChatMessage message)
    {
        if (message == null || message.parts == null || message.parts.Length == 0 || message.parts[0] == null)
        {
            return "";
        }

        return message.parts[0].text ?? "";
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
        history.AddRange(dialogueHistory);
    }

    private static ChatMessage CreateMessage(string role, string text)
    {
        return new ChatMessage
        {
            role = role,
            parts = new[] { new ChatPart { text = text } }
        };
    }

    private void AppendCurrentSessionHistory()
    {
        var path = Path.IsPathRooted(dialogueHistoryPath)
            ? dialogueHistoryPath
            : DesktopPetResourcePath.GetWritableModelPath(dialogueHistoryPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new StringBuilder();
        builder.Append(FormatHistoryTime(chatBeginTime ?? DateTime.Now));
        builder.Append("-->唤醒robot\n");

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

        File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
        currentSessionHistory.Clear();
        chatBeginTime = null;
        Log("Chat history saved: " + path);
    }

    private static string FormatHistoryTime(DateTime time)
    {
        return time.ToString("yyyy-MM-dd HH:mm:ss");
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

    void Update()
    {
        CheckHeartBeat();
    }
}
