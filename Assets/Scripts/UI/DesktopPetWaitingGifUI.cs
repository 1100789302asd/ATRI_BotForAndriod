using UnityEngine;
using UnityEngine.UI;

public sealed class DesktopPetWaitingGifUI : MonoBehaviour
{
    [Header("Target")]
    [SerializeField] private Image image;
    [SerializeField] private GameObject root;

    [Header("Frames")]
    [SerializeField] private Sprite[] frames;
    [SerializeField] private string resourcesFolder = "UI/loading_frames";
    [SerializeField] private float frameSeconds = 0.1f;

    private int frameIndex;
    private float timer;
    private bool playing;

    private void Reset()
    {
        CacheComponents();
    }

    private void Awake()
    {
        CacheComponents();
        LoadFramesIfNeeded();
        Hide();
    }

    private void Update()
    {
        if (!playing || frames == null || frames.Length == 0)
        {
            return;
        }

        timer += Time.deltaTime;
        if (timer < frameSeconds)
        {
            return;
        }

        timer = 0f;
        frameIndex = (frameIndex + 1) % frames.Length;
        ApplyFrame();
    }

    public void Show()
    {
        CacheComponents();
        LoadFramesIfNeeded();

        frameIndex = 0;
        timer = 0f;
        playing = true;

        if (root != null)
        {
            root.SetActive(true);
        }

        ApplyFrame();
    }

    public void Hide()
    {
        playing = false;

        if (root != null)
        {
            root.SetActive(false);
        }
    }

    private void CacheComponents()
    {
        if (image == null)
        {
            image = GetComponent<Image>();
        }

        if (root == null)
        {
            root = gameObject;
        }
    }

    private void LoadFramesIfNeeded()
    {
        if (frames != null && frames.Length > 0)
        {
            return;
        }

        frames = Resources.LoadAll<Sprite>(resourcesFolder);
        if (frames == null || frames.Length == 0)
        {
            var textures = Resources.LoadAll<Texture2D>(resourcesFolder);
            if (textures != null && textures.Length > 0)
            {
                System.Array.Sort(textures, (a, b) => string.CompareOrdinal(a.name, b.name));
                frames = new Sprite[textures.Length];
                for (var i = 0; i < textures.Length; i++)
                {
                    var texture = textures[i];
                    frames[i] = Sprite.Create(
                        texture,
                        new Rect(0f, 0f, texture.width, texture.height),
                        new Vector2(0.5f, 0.5f));
                }
            }
        }

        if (frames == null || frames.Length == 0)
        {
            Debug.LogWarning("Waiting animation frames not found: Resources/" + resourcesFolder, this);
            return;
        }

        System.Array.Sort(frames, (a, b) => string.CompareOrdinal(a.name, b.name));
    }

    private void ApplyFrame()
    {
        if (image != null && frames != null && frames.Length > 0)
        {
            image.sprite = frames[Mathf.Clamp(frameIndex, 0, frames.Length - 1)];
        }
    }
}
