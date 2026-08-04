using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class NeteaseCaptchaDialog : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private NeteaseCloudMusicClient neteaseClient;
    [SerializeField] private TMP_InputField captchaInput;
    [SerializeField] private TMP_InputField cellphoneInput;
    [SerializeField] private TMP_InputField countryCodeInput;
    [SerializeField] private Button submitButton;
    [SerializeField] private Button resendButton;
    [SerializeField] private Button cancelButton;
    [SerializeField] private TMP_Text statusText;

    [Header("Behavior")]
    [SerializeField] private bool hideOnAwake = true;
    [SerializeField] private bool bindOnEnable = true;
    [SerializeField] private string phoneStatus = "\u8bf7\u5148\u8f93\u5165\u7f51\u6613\u4e91\u624b\u673a\u53f7";
    [SerializeField] private string emptyPhoneStatus = "\u8bf7\u8f93\u5165\u7f51\u6613\u4e91\u624b\u673a\u53f7";
    [SerializeField] private string defaultStatus = "\u8bf7\u8f93\u5165\u7f51\u6613\u4e91\u9a8c\u8bc1\u7801";
    [SerializeField] private string sendingStatus = "\u6b63\u5728\u53d1\u9001\u9a8c\u8bc1\u7801...";
    [SerializeField] private string sentStatus = "\u9a8c\u8bc1\u7801\u5df2\u53d1\u9001\uff0c\u8bf7\u67e5\u770b\u624b\u673a";
    [SerializeField] private string submittingStatus = "\u6b63\u5728\u767b\u5f55...";
    [SerializeField] private string emptyCodeStatus = "\u8bf7\u8f93\u5165\u9a8c\u8bc1\u7801";
    [SerializeField] private string successStatus = "\u7f51\u6613\u4e91\u767b\u5f55\u6210\u529f";
    [SerializeField] private string clientMissingStatus = "\u7f51\u6613\u4e91\u5ba2\u6237\u7aef\u672a\u7ed1\u5b9a";

    private bool bound;
    private bool waitingForCaptcha;

    private void Awake()
    {
        CacheReferences();
        BindButtons();

        if (hideOnAwake)
        {
            Hide();
        }
    }

    private void OnEnable()
    {
        if (bindOnEnable)
        {
            BindClient();
        }
    }

    private void OnDisable()
    {
        UnbindClient();
    }

    public void Show()
    {
        gameObject.SetActive(true);
        SetStatus(NeedsPhoneNumber() && !waitingForCaptcha ? phoneStatus : defaultStatus);
        SetButtonsInteractable(true);
        FocusFirstEmptyInput();
    }

    public void Hide()
    {
        gameObject.SetActive(false);
    }

    public void ShowLoginFailed(string message)
    {
        CacheReferences();
        HandleLoginFailed(message);
    }

    public void RequestCode()
    {
        CacheReferences();
        if (neteaseClient == null)
        {
            SetStatus(clientMissingStatus);
            return;
        }

        if (!ApplyPhoneInputToClient())
        {
            SetStatus(emptyPhoneStatus);
            FocusPhoneInput();
            return;
        }

        SetStatus(sendingStatus);
        SetButtonsInteractable(false);
        neteaseClient.RequestNeteaseLoginCode();
    }

    public void SubmitCode()
    {
        CacheReferences();
        if (neteaseClient == null)
        {
            SetStatus(clientMissingStatus);
            return;
        }

        var hadPhoneNumber = !NeedsPhoneNumber();
        if (!ApplyPhoneInputToClient())
        {
            SetStatus(emptyPhoneStatus);
            FocusPhoneInput();
            return;
        }

        if (!hadPhoneNumber && !waitingForCaptcha)
        {
            RequestCode();
            return;
        }

        var code = captchaInput != null ? captchaInput.text.Trim() : "";
        if (string.IsNullOrWhiteSpace(code))
        {
            SetStatus(emptyCodeStatus);
            FocusCaptchaInput();
            return;
        }

        SetStatus(submittingStatus);
        SetButtonsInteractable(false);
        neteaseClient.SubmitNeteaseLoginCode(code);
    }

    private void HandleCaptchaSent()
    {
        waitingForCaptcha = true;
        ClearCaptchaInput();
        Show();
        SetStatus(sentStatus);
        SetButtonsInteractable(true);
    }

    private void HandleLoginSucceeded()
    {
        waitingForCaptcha = false;
        SetStatus(successStatus);
        ClearCaptchaInput();
        Hide();
    }

    private void HandleLoginFailed(string message)
    {
        if (RequiresPhonePrompt(message))
        {
            waitingForCaptcha = false;
        }

        gameObject.SetActive(true);
        SetStatus(string.IsNullOrWhiteSpace(message)
            ? (NeedsPhoneNumber() && !waitingForCaptcha ? phoneStatus : defaultStatus)
            : message);
        SetButtonsInteractable(true);
        FocusFirstEmptyInput();
    }

    private void BindClient()
    {
        CacheReferences();
        if (bound || neteaseClient == null)
        {
            return;
        }

        neteaseClient.CaptchaSent.AddListener(HandleCaptchaSent);
        neteaseClient.LoginSucceeded.AddListener(HandleLoginSucceeded);
        neteaseClient.LoginFailed.AddListener(HandleLoginFailed);
        bound = true;
    }

    private void UnbindClient()
    {
        if (!bound || neteaseClient == null)
        {
            return;
        }

        neteaseClient.CaptchaSent.RemoveListener(HandleCaptchaSent);
        neteaseClient.LoginSucceeded.RemoveListener(HandleLoginSucceeded);
        neteaseClient.LoginFailed.RemoveListener(HandleLoginFailed);
        bound = false;
    }

    private void BindButtons()
    {
        if (submitButton != null)
        {
            submitButton.onClick.RemoveListener(SubmitCode);
            submitButton.onClick.AddListener(SubmitCode);
        }

        if (resendButton != null)
        {
            resendButton.onClick.RemoveListener(RequestCode);
            resendButton.onClick.AddListener(RequestCode);
        }

        if (cancelButton != null)
        {
            cancelButton.onClick.RemoveListener(Hide);
            cancelButton.onClick.AddListener(Hide);
        }

        if (captchaInput != null)
        {
            captchaInput.onSubmit.RemoveListener(SubmitCodeFromInput);
            captchaInput.onSubmit.AddListener(SubmitCodeFromInput);
        }

        if (cellphoneInput != null)
        {
            cellphoneInput.onSubmit.RemoveListener(RequestCodeFromInput);
            cellphoneInput.onSubmit.AddListener(RequestCodeFromInput);
        }
    }

    private void SubmitCodeFromInput(string _)
    {
        SubmitCode();
    }

    private void RequestCodeFromInput(string _)
    {
        RequestCode();
    }

    private void CacheReferences()
    {
        if (neteaseClient == null)
        {
            neteaseClient = FindFirstObjectByType<NeteaseCloudMusicClient>();
        }

        AutoAssignInputFields();
    }

    private void AutoAssignInputFields()
    {
        var inputs = new List<TMP_InputField>(GetComponentsInChildren<TMP_InputField>(true));
        inputs.RemoveAll(input => input == null);
        inputs.Sort(CompareInputPosition);

        if (inputs.Count == 0)
        {
            return;
        }

        if (inputs.Count >= 2)
        {
            var topInput = inputs[0];
            var lowerInput = inputs[1];

            if (cellphoneInput == null)
            {
                cellphoneInput = topInput;
            }

            if (captchaInput == null || captchaInput == cellphoneInput)
            {
                captchaInput = lowerInput;
            }

            return;
        }

        if (captchaInput == null)
        {
            captchaInput = inputs[0];
        }
    }

    private static int CompareInputPosition(TMP_InputField left, TMP_InputField right)
    {
        var leftY = GetInputY(left);
        var rightY = GetInputY(right);
        var yCompare = rightY.CompareTo(leftY);
        if (yCompare != 0)
        {
            return yCompare;
        }

        return string.Compare(left.name, right.name, System.StringComparison.OrdinalIgnoreCase);
    }

    private static float GetInputY(TMP_InputField input)
    {
        var rect = input != null ? input.transform as RectTransform : null;
        return rect != null ? rect.anchoredPosition.y : 0f;
    }

    private bool ApplyPhoneInputToClient()
    {
        if (neteaseClient == null)
        {
            return false;
        }

        var cellphone = GetCellphoneInputValue();
        if (string.IsNullOrWhiteSpace(cellphone))
        {
            return false;
        }

        neteaseClient.SetNeteaseCellphone(cellphone);
        var countryCode = GetCountryCodeInputValue();
        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            neteaseClient.SetNeteaseCountryCode(countryCode);
        }

        return true;
    }

    private string GetCellphoneInputValue()
    {
        if (cellphoneInput != null)
        {
            return cellphoneInput.text.Trim();
        }

        if (neteaseClient != null && !string.IsNullOrWhiteSpace(neteaseClient.LoginCellphone))
        {
            return neteaseClient.LoginCellphone.Trim();
        }

        return waitingForCaptcha || captchaInput == null ? "" : captchaInput.text.Trim();
    }

    private string GetCountryCodeInputValue()
    {
        if (countryCodeInput != null && !string.IsNullOrWhiteSpace(countryCodeInput.text))
        {
            return countryCodeInput.text.Trim();
        }

        return neteaseClient != null ? neteaseClient.LoginCountryCode : "86";
    }

    private bool NeedsPhoneNumber()
    {
        return neteaseClient == null || string.IsNullOrWhiteSpace(neteaseClient.LoginCellphone);
    }

    private static bool RequiresPhonePrompt(string message)
    {
        return !string.IsNullOrWhiteSpace(message) &&
               message.IndexOf("cellphone", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void SetButtonsInteractable(bool interactable)
    {
        if (submitButton != null)
        {
            submitButton.interactable = interactable;
        }

        if (resendButton != null)
        {
            resendButton.interactable = interactable;
        }

        if (cancelButton != null)
        {
            cancelButton.interactable = interactable;
        }
    }

    private void SetStatus(string message)
    {
        if (statusText != null)
        {
            statusText.text = message ?? "";
        }
    }

    private void FocusFirstEmptyInput()
    {
        if (NeedsPhoneNumber() && !waitingForCaptcha)
        {
            FocusPhoneInput();
            return;
        }

        FocusCaptchaInput();
    }

    private void FocusPhoneInput()
    {
        FocusInput(cellphoneInput != null ? cellphoneInput : captchaInput);
    }

    private void FocusCaptchaInput()
    {
        FocusInput(captchaInput);
    }

    private static void FocusInput(TMP_InputField input)
    {
        if (input == null)
        {
            return;
        }

        input.ActivateInputField();
        input.Select();
    }

    private void ClearCaptchaInput()
    {
        if (captchaInput != null)
        {
            captchaInput.text = "";
        }
    }
}
