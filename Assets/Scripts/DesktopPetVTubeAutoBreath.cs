using System;
using Live2D.Cubism.Core;
using Live2D.Cubism.Framework;
using UnityEngine;

/// <summary>
/// Recreates the Cubism-style automatic breathing used by live2d-py.
/// Attach this to the Live2D model root, or to a parent object that contains one CubismModel.
/// </summary>
public sealed class DesktopPetVTubeAutoBreath : MonoBehaviour, ICubismUpdatable
{
    [Serializable]
    public sealed class BreathParameter
    {
        public string parameterId;
        public bool enabled = true;
        public float offset;
        public float peak = 1f;
        public float cycleSeconds = 4f;
        [Range(0f, 1f)] public float weight = 0.5f;

        [Header("Runtime")]
        public bool parameterFound;
        public float currentValue;

        private CubismParameter parameter;

        public void ClearCache()
        {
            parameter = null;
            parameterFound = false;
            currentValue = 0f;
        }

        public void Cache(CubismModel model)
        {
            parameter = null;

            if (model == null || string.IsNullOrEmpty(parameterId))
            {
                parameterFound = false;
                currentValue = 0f;
                return;
            }

            parameter = model.Parameters.FindById(parameterId);
            parameterFound = parameter != null;
            currentValue = parameter != null ? parameter.Value : 0f;
        }

        public void Apply(float time)
        {
            if (!enabled || parameter == null || cycleSeconds <= 0f)
            {
                return;
            }

            var value = offset + (peak * Mathf.Sin((time / cycleSeconds) * Mathf.PI * 2f));
            parameter.AddToValue(value, weight);
            currentValue = parameter.Value;
        }
    }

    [Header("Target")]
    [SerializeField] private CubismModel model;

    [Header("Playback")]
    [SerializeField] private bool playOnStart = true;
    [SerializeField] private BreathParameter[] breathParameters =
    {
        new BreathParameter
        {
            parameterId = "ParamAngleX",
            offset = 0f,
            peak = 15f,
            cycleSeconds = 6.5345f,
            weight = 0.5f
        },
        new BreathParameter
        {
            parameterId = "ParamAngleY",
            offset = 0f,
            peak = 8f,
            cycleSeconds = 3.5345f,
            weight = 0.5f
        },
        new BreathParameter
        {
            parameterId = "ParamAngleZ",
            offset = 0f,
            peak = 10f,
            cycleSeconds = 5.5345f,
            weight = 0.5f
        },
        new BreathParameter
        {
            parameterId = "ParamBodyAngleX",
            offset = 0f,
            peak = 4f,
            cycleSeconds = 15.5345f,
            weight = 0.5f
        },
        new BreathParameter
        {
            parameterId = "ParamBreath",
            offset = 0.5f,
            peak = 0.5f,
            cycleSeconds = 3.2345f,
            weight = 0.5f
        }
    };

    [Header("Debug")]
    [SerializeField] private bool logValues;
    [SerializeField] private float logInterval = 1f;

    private float logTimer;

    public bool HasUpdateController { get; set; }

    public int ExecutionOrder
    {
        get { return CubismUpdateExecutionOrder.CubismLookController + 5; }
    }

    public bool NeedsUpdateOnEditing
    {
        get { return false; }
    }

    private void Reset()
    {
        CacheModel();
    }

    private void Awake()
    {
        CacheModel();
    }

    private void OnEnable()
    {
        CacheModel();
        RefreshUpdateController();
    }

    private void Start()
    {
        CacheModel();
        RefreshUpdateController();
    }

    private void LateUpdate()
    {
        if (!HasUpdateController)
        {
            OnLateUpdate();
        }
    }

    public void OnLateUpdate()
    {
        if (!playOnStart || breathParameters == null)
        {
            return;
        }

        for (var i = 0; i < breathParameters.Length; i++)
        {
            if (breathParameters[i] != null)
            {
                breathParameters[i].Apply(Time.time);
            }
        }

        if (logValues && logInterval > 0f)
        {
            logTimer += Time.deltaTime;
            if (logTimer >= logInterval)
            {
                logTimer = 0f;
                LogParameterValues();
            }
        }
    }

    public void SetPlaying(bool isPlaying)
    {
        playOnStart = isPlaying;
    }

    private void CacheModel()
    {
        if (model == null)
        {
            model = GetComponent<CubismModel>();
        }

        if (model == null)
        {
            model = GetComponentInChildren<CubismModel>();
        }

        if (model == null)
        {
            model = GetComponentInParent<CubismModel>();
        }
        CacheParameters();
    }

    private void CacheParameters()
    {
        if (breathParameters != null)
        {
            for (var i = 0; i < breathParameters.Length; i++)
            {
                if (breathParameters[i] != null)
                {
                    breathParameters[i].Cache(model);
                }
            }
        }
    }

    private void RefreshUpdateController()
    {
        if (model == null)
        {
            HasUpdateController = false;
            return;
        }

        var updateController = model.GetComponent<CubismUpdateController>();
        HasUpdateController = updateController != null;

        if (updateController != null)
        {
            updateController.Refresh();
        }
    }

    private void LogParameterValues()
    {
        if (breathParameters == null)
        {
            return;
        }

        for (var i = 0; i < breathParameters.Length; i++)
        {
            var parameter = breathParameters[i];
            if (parameter != null && parameter.parameterFound)
            {
                Debug.Log($"{nameof(DesktopPetVTubeAutoBreath)} {parameter.parameterId} = {parameter.currentValue:F3}", this);
            }
        }
    }
}
