using UnityEngine;

[RequireComponent(typeof(Collider2D))]
public sealed class HitBoxCustom : MonoBehaviour
{
    public enum HitAction
    {
        Head,
        Body,
        PrefabVoice,
        RandomExpression,
        RandomMotion
    }

    [SerializeField] private HitAction action = HitAction.Body;
    [SerializeField] private int priority;

    private Collider2D cachedCollider;

    public HitAction Action
    {
        get { return action; }
    }

    public int Priority
    {
        get { return priority; }
    }

    private void Awake()
    {
        CacheCollider();
    }

    private void Reset()
    {
        CacheCollider();
        if (cachedCollider != null)
        {
            cachedCollider.isTrigger = true;
        }
    }

    public bool ContainsScreenPoint(Camera camera, Vector2 screenPoint)
    {
        CacheCollider();
        if (camera == null || cachedCollider == null || !isActiveAndEnabled || !cachedCollider.enabled)
        {
            return false;
        }

        var worldPoint = camera.ScreenToWorldPoint(new Vector3(screenPoint.x, screenPoint.y, -camera.transform.position.z));
        return cachedCollider.OverlapPoint(worldPoint);
    }

    private void CacheCollider()
    {
        if (cachedCollider == null)
        {
            cachedCollider = GetComponent<Collider2D>();
        }
    }
}
