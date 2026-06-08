using Live2D.Cubism.Core;
using UnityEngine;

/// <summary>
/// Minimal desktop-pet controller example for a Live2D Cubism model.
/// Attach this to the root object that has CubismModel, then wire Animator if your template uses one.
/// </summary>
public sealed class DesktopPetTemplateExample : MonoBehaviour
{
    [Header("Live2D")]
    [SerializeField] private CubismModel model;
    [SerializeField] private Animator animator;

    [Header("Behavior")]
    [SerializeField] private bool lookAtMouse = true;
    [SerializeField] private float idleInterval = 6f;
    [SerializeField] private string idleTrigger = "Idle";
    [SerializeField] private string tapTrigger = "Tap";

    [Header("Parameter Ids")]
    [SerializeField] private string angleX = "ParamAngleX";
    [SerializeField] private string angleY = "ParamAngleY";
    [SerializeField] private string bodyAngleX = "ParamBodyAngleX";

    private float nextIdleTime;

    private void Reset()
    {
        model = GetComponent<CubismModel>();
        animator = GetComponent<Animator>();
    }

    private void Awake()
    {
        if (model == null)
        {
            model = GetComponent<CubismModel>();
        }

        if (animator == null)
        {
            animator = GetComponent<Animator>();
        }
    }

    private void Update()
    {
        if (lookAtMouse)
        {
            UpdateLookAtMouse();
        }

        if (Time.time >= nextIdleTime)
        {
            Trigger(idleTrigger);
            nextIdleTime = Time.time + idleInterval;
        }

        if (Input.GetMouseButtonDown(0))
        {
            OnPetTapped();
        }
    }

    public void OnPetTapped()
    {
        Trigger(tapTrigger);
        SetParameter("ParamEyeLOpen", 0.15f);
        SetParameter("ParamEyeROpen", 0.15f);
    }

    private void UpdateLookAtMouse()
    {
        Vector3 viewport = Camera.main.ScreenToViewportPoint(Input.mousePosition);
        float x = Mathf.Clamp((viewport.x - 0.5f) * 60f, -30f, 30f);
        float y = Mathf.Clamp((viewport.y - 0.5f) * 60f, -30f, 30f);

        SetParameter(angleX, x);
        SetParameter(angleY, y);
        SetParameter(bodyAngleX, x * 0.35f);
    }

    private void SetParameter(string parameterId, float value)
    {
        if (model == null || string.IsNullOrEmpty(parameterId))
        {
            return;
        }

        CubismParameter parameter = model.Parameters.FindById(parameterId);
        if (parameter != null)
        {
            parameter.Value = value;
        }
    }

    private void Trigger(string triggerName)
    {
        if (animator != null && !string.IsNullOrEmpty(triggerName))
        {
            animator.SetTrigger(triggerName);
        }
    }
}
