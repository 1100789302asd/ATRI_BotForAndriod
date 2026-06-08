using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Mobile-friendly chat input submitter. Bind a send button to Submit, or let this script bind it automatically.
/// </summary>
public sealed class DesktopPetChatInputSubmitter : MonoBehaviour
{
    [Header("Targets")]
    [SerializeField] private DesktopPetMigratedController petController;
    [SerializeField] private TMP_InputField tmpInputField;
    [SerializeField] private InputField legacyInputField;
    [SerializeField] private Button sendButton;

    [Header("Submit")]
    [SerializeField] private bool clearAfterSubmit = true;
    [SerializeField] private bool deactivateKeyboardAfterSubmit;

    [Header("Mobile Keyboard")]
    [SerializeField] private bool liftAboveKeyboard = true;
    [SerializeField] private RectTransform panelToMove;
    [SerializeField] private float keyboardPadding = 24f;
    [SerializeField] private float fallbackKeyboardHeightRatio = 0.42f;
    [SerializeField] private float moveLerpSpeed = 16f;

    private Canvas rootCanvas;
    private Vector2 panelOriginalAnchoredPosition;
    private bool hasPanelOriginalPosition;
    private bool inputFocused;

    private void Reset()
    {
        CacheReferences();
    }

    private void Awake()
    {
        CacheReferences();
        CachePanelPosition();
    }

    private void OnEnable()
    {
        Bind();
    }

    private void OnDisable()
    {
        Unbind();
        RestorePanelPosition(immediate: true);
    }

    private void Update()
    {
        RefreshInputFocus();
        UpdateKeyboardAvoidance();
    }

    public void Submit()
    {
        if (petController == null)
        {
            return;
        }

        var text = GetText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        petController.SubmitUserText(text);

        if (clearAfterSubmit)
        {
            SetText("");
        }

        if (deactivateKeyboardAfterSubmit)
        {
            DeactivateInput();
            inputFocused = false;
            RestorePanelPosition(immediate: false);
        }
        else
        {
            ActivateInput();
        }
    }

    public void ActivateInput()
    {
        if (tmpInputField != null)
        {
            tmpInputField.ActivateInputField();
            return;
        }

        if (legacyInputField != null)
        {
            legacyInputField.ActivateInputField();
        }
    }

    public void ClearInput()
    {
        SetText("");
    }

    private void Bind()
    {
        if (sendButton != null)
        {
            sendButton.onClick.RemoveListener(Submit);
            sendButton.onClick.AddListener(Submit);
        }

        if (tmpInputField != null)
        {
            tmpInputField.onSubmit.RemoveListener(SubmitFromString);
            tmpInputField.onSubmit.AddListener(SubmitFromString);
            tmpInputField.onSelect.RemoveListener(HandleInputSelected);
            tmpInputField.onSelect.AddListener(HandleInputSelected);
            tmpInputField.onDeselect.RemoveListener(HandleInputDeselected);
            tmpInputField.onDeselect.AddListener(HandleInputDeselected);
        }

        if (legacyInputField != null)
        {
            legacyInputField.onSubmit.RemoveListener(SubmitFromString);
            legacyInputField.onSubmit.AddListener(SubmitFromString);
            legacyInputField.onValueChanged.RemoveListener(HandleLegacyInputChanged);
            legacyInputField.onValueChanged.AddListener(HandleLegacyInputChanged);
            legacyInputField.onEndEdit.RemoveListener(HandleInputDeselected);
            legacyInputField.onEndEdit.AddListener(HandleInputDeselected);
        }
    }

    private void Unbind()
    {
        if (sendButton != null)
        {
            sendButton.onClick.RemoveListener(Submit);
        }

        if (tmpInputField != null)
        {
            tmpInputField.onSubmit.RemoveListener(SubmitFromString);
            tmpInputField.onSelect.RemoveListener(HandleInputSelected);
            tmpInputField.onDeselect.RemoveListener(HandleInputDeselected);
        }

        if (legacyInputField != null)
        {
            legacyInputField.onSubmit.RemoveListener(SubmitFromString);
            legacyInputField.onValueChanged.RemoveListener(HandleLegacyInputChanged);
            legacyInputField.onEndEdit.RemoveListener(HandleInputDeselected);
        }
    }

    private void SubmitFromString(string _)
    {
        Submit();
    }

    private void HandleInputSelected(string _)
    {
        inputFocused = true;
        CachePanelPosition();
    }

    private void HandleInputDeselected(string _)
    {
        inputFocused = false;
        RestorePanelPosition(immediate: false);
    }

    private void HandleLegacyInputChanged(string _)
    {
        inputFocused = legacyInputField != null && legacyInputField.isFocused;
    }

    private void RefreshInputFocus()
    {
        inputFocused =
            (tmpInputField != null && tmpInputField.isFocused) ||
            (legacyInputField != null && legacyInputField.isFocused);
    }

    private void CacheReferences()
    {
        if (petController == null)
        {
            petController = FindFirstObjectByType<DesktopPetMigratedController>();
        }

        if (tmpInputField == null)
        {
            tmpInputField = GetComponentInChildren<TMP_InputField>(true);
        }

        if (legacyInputField == null)
        {
            legacyInputField = GetComponentInChildren<InputField>(true);
        }

        if (sendButton == null)
        {
            sendButton = GetComponentInChildren<Button>(true);
        }

        if (panelToMove == null)
        {
            panelToMove = transform as RectTransform;
        }

        if (rootCanvas == null)
        {
            rootCanvas = GetComponentInParent<Canvas>();
        }
    }

    private string GetText()
    {
        if (tmpInputField != null)
        {
            return tmpInputField.text;
        }

        if (legacyInputField != null)
        {
            return legacyInputField.text;
        }

        return "";
    }

    private void SetText(string text)
    {
        if (tmpInputField != null)
        {
            tmpInputField.text = text;
        }

        if (legacyInputField != null)
        {
            legacyInputField.text = text;
        }
    }

    private void DeactivateInput()
    {
        if (tmpInputField != null)
        {
            tmpInputField.DeactivateInputField();
        }

        if (legacyInputField != null)
        {
            legacyInputField.DeactivateInputField();
        }
    }

    private void CachePanelPosition()
    {
        if (panelToMove == null || hasPanelOriginalPosition)
        {
            return;
        }

        panelOriginalAnchoredPosition = panelToMove.anchoredPosition;
        hasPanelOriginalPosition = true;
    }

    private void UpdateKeyboardAvoidance()
    {
        if (!liftAboveKeyboard || panelToMove == null)
        {
            return;
        }

        var shouldLift = inputFocused && IsKeyboardVisible();
        if (!shouldLift)
        {
            RestorePanelPosition(immediate: false);
            return;
        }

        CachePanelPosition();

        var scaleFactor = rootCanvas != null && rootCanvas.scaleFactor > 0f ? rootCanvas.scaleFactor : 1f;
        var keyboardHeight = GetKeyboardHeight();
        var target = panelOriginalAnchoredPosition + new Vector2(0f, keyboardHeight / scaleFactor + keyboardPadding);
        panelToMove.anchoredPosition = Vector2.Lerp(
            panelToMove.anchoredPosition,
            target,
            1f - Mathf.Exp(-moveLerpSpeed * Time.unscaledDeltaTime));
    }

    private void RestorePanelPosition(bool immediate)
    {
        if (panelToMove == null || !hasPanelOriginalPosition)
        {
            return;
        }

        if (immediate)
        {
            panelToMove.anchoredPosition = panelOriginalAnchoredPosition;
            return;
        }

        panelToMove.anchoredPosition = Vector2.Lerp(
            panelToMove.anchoredPosition,
            panelOriginalAnchoredPosition,
            1f - Mathf.Exp(-moveLerpSpeed * Time.unscaledDeltaTime));
    }

    private bool IsKeyboardVisible()
    {
#if UNITY_ANDROID || UNITY_IOS
        return TouchScreenKeyboard.visible;
#else
        return false;
#endif
    }

    private float GetKeyboardHeight()
    {
#if UNITY_ANDROID || UNITY_IOS
        var height = TouchScreenKeyboard.area.height;
        if (height <= 0f && TouchScreenKeyboard.visible)
        {
            height = Screen.height * fallbackKeyboardHeightRatio;
        }

        return height;
#else
        return 0f;
#endif
    }
}
