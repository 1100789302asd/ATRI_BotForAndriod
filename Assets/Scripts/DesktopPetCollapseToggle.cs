using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Android-friendly expand/collapse toggle for a Live2D desktop pet.
/// Put this on a manually created UI ball. This script does not create canvases or UI objects.
/// </summary>
public sealed class DesktopPetCollapseToggle : MonoBehaviour
{
    [Header("Targets")]
    [SerializeField] private GameObject petRoot;
    [SerializeField] private RectTransform ball;
    [SerializeField] private AndroidFloatingBallBridge androidFloatingBall;

    [Header("Ball")]
    [SerializeField] private Vector2 ballSize = new Vector2(72f, 72f);
    [SerializeField] private Vector2 collapsedAnchoredPosition = new Vector2(72f, 160f);
    [SerializeField] private Vector2 expandedAnchoredPosition = new Vector2(72f, 160f);
    [SerializeField] private Color collapsedColor = new Color(0.2f, 0.65f, 1f, 0.92f);
    [SerializeField] private Color expandedColor = new Color(1f, 0.45f, 0.65f, 0.92f);

    [Header("State")]
    [SerializeField] private bool startCollapsed = true;

    private Image ballImage;
    private Button ballButton;
    private bool isCollapsed;

    private void Awake()
    {
        if (androidFloatingBall == null)
        {
            androidFloatingBall = AndroidFloatingBallBridge.Instance;
        }

        EnsureBall();
        SetCollapsed(startCollapsed);
    }

    public void Toggle()
    {
        SetCollapsed(!isCollapsed);
    }

    public void Expand()
    {
        SetCollapsed(false);
    }

    public void Collapse()
    {
        SetCollapsed(true);
    }

    private void SetCollapsed(bool collapsed)
    {
        isCollapsed = collapsed;

        if (petRoot != null)
        {
            petRoot.SetActive(!collapsed);
        }

        if (ball != null)
        {
            ball.gameObject.SetActive(true);
            ball.sizeDelta = ballSize;
            ball.anchoredPosition = collapsed ? collapsedAnchoredPosition : expandedAnchoredPosition;
        }

        if (ballImage != null)
        {
            ballImage.color = collapsed ? collapsedColor : expandedColor;
        }

        if (androidFloatingBall != null)
        {
            if (collapsed)
            {
                androidFloatingBall.ShowFloatingBall();
            }
            else
            {
                androidFloatingBall.HideFloatingBall();
            }
        }
    }

    private void EnsureBall()
    {
        if (ball == null)
        {
            ball = transform as RectTransform;
        }

        if (ball != null)
        {
            ballImage = ball.GetComponent<Image>();
            if (ballImage == null)
            {
                ballImage = ball.gameObject.AddComponent<Image>();
            }

            ballImage.raycastTarget = true;

            ballButton = ball.GetComponent<Button>();
            if (ballButton == null)
            {
                ballButton = ball.gameObject.AddComponent<Button>();
            }

            ballButton.onClick.RemoveListener(Toggle);
            ballButton.onClick.AddListener(Toggle);
        }
    }
}
