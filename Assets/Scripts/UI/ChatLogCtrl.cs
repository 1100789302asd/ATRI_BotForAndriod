using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ChatLogCtrl : MonoBehaviour
{
    string history = "";
    public TMP_Text tp;
    [SerializeField] private ScrollRect scrollRect;
    [SerializeField] private RectTransform contentRoot;
    [SerializeField] private bool scrollToBottom = true;

    private void Awake()
    {
        CacheLayoutReferences();
    }

    private void Reset()
    {
        CacheLayoutReferences();
    }

    public void AddMessage(string role, string text)
    {
        if (tp == null)
        {
            return;
        }

        history += role + " —> " + text;
        history += "\n";
        tp.text = history;
        RefreshLayout();
    }

    private void CacheLayoutReferences()
    {
        if (tp == null)
        {
            tp = GetComponentInChildren<TMP_Text>(true);
        }

        if (scrollRect == null)
        {
            scrollRect = GetComponentInParent<ScrollRect>();
        }

        if (contentRoot == null)
        {
            if (scrollRect != null && scrollRect.content != null)
            {
                contentRoot = scrollRect.content;
            }
            else if (tp != null)
            {
                contentRoot = tp.rectTransform.parent as RectTransform;
            }
        }
    }

    private void RefreshLayout()
    {
        CacheLayoutReferences();

        tp.ForceMeshUpdate();
        Canvas.ForceUpdateCanvases();

        if (tp.rectTransform != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(tp.rectTransform);
        }

        if (contentRoot != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(contentRoot);
        }

        Canvas.ForceUpdateCanvases();

        if (scrollToBottom && scrollRect != null)
        {
            scrollRect.verticalNormalizedPosition = 0f;
        }
    }
}
