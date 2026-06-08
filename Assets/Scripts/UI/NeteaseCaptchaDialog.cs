using TMPro;
using UnityEngine;
using UnityEngine.UI;

public sealed class NeteaseCaptchaDialog : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private NeteaseCloudMusicClient neteaseClient;
    [SerializeField] private TMP_InputField captchaInput;
    [SerializeField] private Button submitButton;
    [SerializeField] private Button resendButton;
    [SerializeField] private Button cancelButton;
    [SerializeField] private TMP_Text statusText;

    [Header("Behavior")]
    [SerializeField] private bool hideOnAwake = true;
    [SerializeField] private bool bindOnEnable = true;
    [SerializeField] private string defaultStatus = "请输入网易云验证码";
    [SerializeField] private string sendingStatus = "正在发送验证码...";
    [SerializeField] private string sentStatus = "验证码已发送，请查看手机";
    [SerializeField] private string submittingStatus = "正在登录...";
    [SerializeField] private string emptyCodeStatus = "请输入验证码";
    [SerializeField] private string successStatus = "网易云登录成功";

    private bool bound;

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
        SetStatus(defaultStatus);
        SetButtonsInteractable(true);
        FocusInput();
    }

    public void Hide()
    {
        gameObject.SetActive(false);
    }

    public void RequestCode()
    {
        CacheReferences();
        if (neteaseClient == null)
        {
            SetStatus("网易云客户端未绑定");
            return;
        }

        SetStatus(sendingStatus);
        SetButtonsInteractable(false);
        neteaseClient.RequestNeteaseLoginCode();
    }

    public void SubmitCode()
    {
        CacheReferences();
        var code = captchaInput != null ? captchaInput.text.Trim() : "";
        if (string.IsNullOrWhiteSpace(code))
        {
            SetStatus(emptyCodeStatus);
            FocusInput();
            return;
        }

        if (neteaseClient == null)
        {
            SetStatus("网易云客户端未绑定");
            return;
        }

        SetStatus(submittingStatus);
        SetButtonsInteractable(false);
        neteaseClient.SubmitNeteaseLoginCode(code);
    }

    private void HandleCaptchaSent()
    {
        Show();
        SetStatus(sentStatus);
        SetButtonsInteractable(true);
    }

    private void HandleLoginSucceeded()
    {
        SetStatus(successStatus);
        ClearInput();
        Hide();
    }

    private void HandleLoginFailed(string message)
    {
        Show();
        SetStatus(string.IsNullOrWhiteSpace(message) ? defaultStatus : message);
        SetButtonsInteractable(true);
        FocusInput();
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
    }

    private void SubmitCodeFromInput(string _)
    {
        SubmitCode();
    }

    private void CacheReferences()
    {
        if (neteaseClient == null)
        {
            neteaseClient = FindFirstObjectByType<NeteaseCloudMusicClient>();
        }

        if (captchaInput == null)
        {
            captchaInput = GetComponentInChildren<TMP_InputField>(true);
        }
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

    private void FocusInput()
    {
        if (captchaInput == null)
        {
            return;
        }

        captchaInput.ActivateInputField();
        captchaInput.Select();
    }

    private void ClearInput()
    {
        if (captchaInput != null)
        {
            captchaInput.text = "";
        }
    }
}
