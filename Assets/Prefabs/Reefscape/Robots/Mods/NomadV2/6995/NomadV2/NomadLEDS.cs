using MoSimCore.Enums;
using RobotFramework.Controllers.GamePieceSystem;
using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using MoSimCore.BaseClasses.GameManagement;
using System.Collections;
using UnityEngine;

namespace RobotFramework.Controllers.Lighting
{
    public class CustomLedStripController : MonoBehaviour
    {
        public enum LedEffect
        {
            Solid,
            Blink,
            Breathe
        }

        [System.Serializable]
        public class LedStateSettings
        {
            public Color color = Color.white;
            public LedEffect effect = LedEffect.Solid;

            [Min(0.01f)]
            public float speed = 1f;

            [Min(0f)]
            public float intensity = 20f;

            [Range(0f, 1f)]
            public float breatheMinimum = 0.12f;
        }

        [Header("LED Objects")]
        [SerializeField] private GameObject[] leds;

        [Header("Shader")]
        [SerializeField] private Shader shaderGraphShader;

        [Header("Alliance Idle - Stow + No Game Piece")]
        [SerializeField] private LedStateSettings blueAllianceIdle = new LedStateSettings { color = Color.blue, effect = LedEffect.Solid, speed = 1f, intensity = 20f };
        [SerializeField] private LedStateSettings redAllianceIdle = new LedStateSettings { color = Color.red, effect = LedEffect.Solid, speed = 1f, intensity = 20f };

        [Header("Alliance Detection")]
        [SerializeField] private float allianceLockDelay = 0.35f;

        [Header("Disabled")]
        [SerializeField]
        private LedStateSettings disabled = new LedStateSettings
        {
            color = Color.green,
            effect = LedEffect.Breathe,
            speed = 0.75f,
            intensity = 20f
        };

        [Header("Intake")]
        [SerializeField]
        private LedStateSettings intake = new LedStateSettings
        {
            color = Color.white,
            effect = LedEffect.Solid,
            speed = 1f,
            intensity = 20f
        };

        [Header("Has Coral")]
        [SerializeField]
        private LedStateSettings hasCoral = new LedStateSettings
        {
            color = new Color(1f, 0.65f, 0f),
            effect = LedEffect.Blink,
            speed = 2f,
            intensity = 20f
        };

        [Header("Has Algae")]
        [SerializeField]
        private LedStateSettings hasAlgae = new LedStateSettings
        {
            color = new Color(0f, 0.9f, 0.65f),
            effect = LedEffect.Breathe,
            speed = 0.8f,
            intensity = 20f
        };

        [Header("Coral Mode")]
        [SerializeField]
        private LedStateSettings coralMode = new LedStateSettings
        {
            color = Color.yellow,
            effect = LedEffect.Solid,
            speed = 1f,
            intensity = 20f
        };

        [Header("Algae Mode")]
        [SerializeField]
        private LedStateSettings algaeMode = new LedStateSettings
        {
            color = new Color(0.07f, 0.3f, 0.9f),
            effect = LedEffect.Solid,
            speed = 1f,
            intensity = 20f
        };

        [Header("L1 Mode")]
        [SerializeField]
        private LedStateSettings l1Mode = new LedStateSettings
        {
            color = new Color(1f, 0.25f, 0f),
            effect = LedEffect.Solid,
            speed = 1f,
            intensity = 20f
        };

        [Header("Prep Climb")]
        [SerializeField]
        private LedStateSettings prepClimb = new LedStateSettings
        {
            color = Color.white,
            effect = LedEffect.Solid,
            speed = 1f,
            intensity = 20f
        };

        [Header("Prep Climb Rainbow Slide")]
        [Tooltip("Enable the custom rainbow sliding effect while in Prep Climb.")]
        [SerializeField] private bool prepClimbRainbowSlide = true;

        [Tooltip("How fast the rainbow texture slides across the LED strip.")]
        [SerializeField] private float prepClimbRainbowSpeed = 1.5f;

        [Tooltip("Direction of the rainbow slide. Use X or Y depending on how the LED UVs are laid out.")]
        [SerializeField] private Vector2 prepClimbRainbowDirection = new Vector2(1f, 0f);

        [Tooltip("Brightness used for the rainbow effect.")]
        [SerializeField] private float prepClimbRainbowIntensity = 20f;

        [Header("Climbed")]
        [SerializeField]
        private LedStateSettings climbed = new LedStateSettings
        {
            color = Color.red,
            effect = LedEffect.Blink,
            speed = 3f,
            intensity = 20f
        };

        [Header("Auto Aligning")]
        [SerializeField]
        private LedStateSettings autoAligning = new LedStateSettings
        {
            color = Color.blue,
            effect = LedEffect.Solid,
            speed = 1f,
            intensity = 20f
        };

        [Header("Processor")]
        [SerializeField]
        private LedStateSettings processor = new LedStateSettings
        {
            color = Color.red,
            effect = LedEffect.Blink,
            speed = 1.5f,
            intensity = 20f
        };

        private Material _ledMaterial;
        private Texture2D _runtimeTexture;
        private Texture2D _rainbowTexture;
        private Color _lastTextureColor = new Color(-1f, -1f, -1f, -1f);
        private Vector2 _blueReef;
        private Vector2 _redReef;
        private bool _isBlueAlliance;
        private bool _allianceLocked;

        protected RobotGamePieceControllerBase GamePieceManager { get; private set; }
        protected Games.Reefscape.Robots.ReefscapeRobotBase ReefscapeRobotBase { get; private set; }

        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _algaeController;

        private void Start()
        {
            GamePieceManager = GetComponent<RobotGamePieceControllerBase>();
            ReefscapeRobotBase = GetComponent<Games.Reefscape.Robots.ReefscapeRobotBase>();

            if (shaderGraphShader == null)
            {
                Debug.LogError("CustomLedStripController: Shader Graph Shader is not assigned.");
                enabled = false;
                return;
            }

            _ledMaterial = new Material(shaderGraphShader);

            _runtimeTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _runtimeTexture.name = "Runtime LED Color";
            _runtimeTexture.wrapMode = TextureWrapMode.Repeat;
            _runtimeTexture.filterMode = FilterMode.Bilinear;

            _rainbowTexture = CreateRainbowTexture(256);

            _ledMaterial.SetTexture("_Texture2D", _runtimeTexture);
            _ledMaterial.SetFloat("_X", 0f);
            _ledMaterial.SetFloat("_Y", 0f);

            if (leds != null)
            {
                foreach (var led in leds)
                {
                    if (led == null)
                        continue;

                    var renderer = led.GetComponent<Renderer>();
                    if (renderer != null)
                        renderer.material = _ledMaterial;
                }
            }

            var robotGamePieceController =
                GetComponent<RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>>();

            if (robotGamePieceController != null)
            {
                _coralController =
                    robotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Coral.ToString());

                _algaeController =
                    robotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Algae.ToString());
            }

            GameObject blueReefObject = GameObject.Find("BlueReef");
            GameObject redReefObject = GameObject.Find("RedReef");

            if (blueReefObject != null) _blueReef = ToVector2(blueReefObject.transform.position);
            else Debug.LogError("CustomLedStripController: Could not find BlueReef.");

            if (redReefObject != null) _redReef = ToVector2(redReefObject.transform.position);
            else Debug.LogError("CustomLedStripController: Could not find RedReef.");

            StartCoroutine(LockAllianceAfterSpawn());
            ApplyState(disabled);
        }

        private void Update()
        {
            if (_ledMaterial == null || _runtimeTexture == null)
                return;

            LedStateSettings currentState = GetCurrentState();

            if (currentState == prepClimb && prepClimbRainbowSlide)
            {
                ApplyPrepClimbRainbow();
                return;
            }

            ApplyState(currentState);
        }

        private LedStateSettings GetCurrentState()
        {
            bool robotDisabled =
                ReefscapeRobotBase == null ||
                GamePieceManager == null ||
                BaseGameManager.Instance == null ||
                BaseGameManager.Instance.RobotState == RobotState.Disabled;

            bool hasAlgaePiece = _algaeController != null && _algaeController.HasPiece();
            bool hasCoralPiece = _coralController != null && _coralController.HasPiece();

            // Alliance idle also applies while Disabled.
            // Once the alliance has been locked, an empty robot shows its
            // alliance color whether it is enabled in Stow or disabled.
            if (_allianceLocked &&
                !hasAlgaePiece &&
                !hasCoralPiece &&
                (robotDisabled || ReefscapeRobotBase.CurrentSetpoint == ReefscapeSetpoints.Stow))
            {
                return _isBlueAlliance ? blueAllianceIdle : redAllianceIdle;
            }

            // Before alliance detection finishes, use the normal Disabled effect.
            if (robotDisabled)
                return disabled;

            if (ReefscapeRobotBase.CurrentSetpoint == ReefscapeSetpoints.Climb)
                return prepClimb;

            if (ReefscapeRobotBase.CurrentSetpoint == ReefscapeSetpoints.Climbed)
                return climbed;

            if (ReefscapeRobotBase.AutoAlignLeftAction.IsPressed() ||
                ReefscapeRobotBase.AutoAlignRightAction.IsPressed())
                return autoAligning;

            if (hasAlgaePiece &&
                ReefscapeRobotBase.CurrentSetpoint == ReefscapeSetpoints.Processor)
                return processor;

            if (hasAlgaePiece)
                return hasAlgae;

            if (hasCoralPiece)
                return hasCoral;

            if (ReefscapeRobotBase.IntakeAction.IsPressed() &&
                ReefscapeRobotBase.CurrentIntakeMode == ReefscapeIntakeMode.Normal)
                return intake;

            if (ReefscapeRobotBase.CurrentRobotMode == ReefscapeRobotMode.Algae)
                return algaeMode;

            if (ReefscapeRobotBase.CurrentIntakeMode == ReefscapeIntakeMode.L1)
                return l1Mode;

            if (ReefscapeRobotBase.CurrentRobotMode == ReefscapeRobotMode.Coral)
                return coralMode;

            return disabled;
        }

        private IEnumerator LockAllianceAfterSpawn()
        {
            if (allianceLockDelay > 0f)
                yield return new WaitForSeconds(allianceLockDelay);

            Vector2 spawnPosition = ToVector2(transform.position);
            float blueDistance = Vector2.Distance(spawnPosition, _blueReef);
            float redDistance = Vector2.Distance(spawnPosition, _redReef);

            _isBlueAlliance = blueDistance < redDistance;
            _allianceLocked = true;

            Debug.Log(_isBlueAlliance
                ? "Custom LED Controller: BLUE alliance locked."
                : "Custom LED Controller: RED alliance locked.");
        }

        private Vector2 ToVector2(Vector3 value) => new Vector2(value.x, value.z);

        private void ApplyState(LedStateSettings state)
        {
            if (state == null)
                return;

            Color outputColor = GetEffectColor(state);

            _ledMaterial.SetTexture("_Texture2D", _runtimeTexture);
            _ledMaterial.SetFloat("_X", 0f);
            _ledMaterial.SetFloat("_Y", 0f);
            _ledMaterial.SetFloat("_intensity", state.intensity);

            SetRuntimeTextureColor(outputColor);
        }

        private Color GetEffectColor(LedStateSettings state)
        {
            switch (state.effect)
            {
                case LedEffect.Blink:
                    {
                        float phase = Mathf.Repeat(Time.time * state.speed, 1f);
                        return phase < 0.5f ? state.color : Color.black;
                    }

                case LedEffect.Breathe:
                    {
                        float wave = (Mathf.Sin(Time.time * state.speed * Mathf.PI * 2f) + 1f) * 0.5f;
                        float brightness = Mathf.Lerp(state.breatheMinimum, 1f, wave);

                        return new Color(
                            state.color.r * brightness,
                            state.color.g * brightness,
                            state.color.b * brightness,
                            state.color.a
                        );
                    }

                case LedEffect.Solid:
                default:
                    return state.color;
            }
        }

        private void ApplyPrepClimbRainbow()
        {
            if (_rainbowTexture == null)
                return;

            float timeOffset = Time.time * prepClimbRainbowSpeed;

            _ledMaterial.SetTexture("_Texture2D", _rainbowTexture);
            _ledMaterial.SetFloat("_X", prepClimbRainbowDirection.x * timeOffset);
            _ledMaterial.SetFloat("_Y", prepClimbRainbowDirection.y * timeOffset);
            _ledMaterial.SetFloat("_intensity", prepClimbRainbowIntensity);
        }

        private Texture2D CreateRainbowTexture(int width)
        {
            Texture2D texture = new Texture2D(width, 1, TextureFormat.RGBA32, false);
            texture.name = "Runtime Rainbow LED";
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Bilinear;

            for (int x = 0; x < width; x++)
            {
                float hue = (float)x / width;
                Color color = Color.HSVToRGB(hue, 1f, 1f);
                texture.SetPixel(x, 0, color);
            }

            texture.Apply(false, false);
            return texture;
        }

        private void SetRuntimeTextureColor(Color color)
        {
            if (ApproximatelySameColor(_lastTextureColor, color))
                return;

            _runtimeTexture.SetPixel(0, 0, color);
            _runtimeTexture.Apply(false, false);
            _lastTextureColor = color;
        }

        private bool ApproximatelySameColor(Color a, Color b)
        {
            const float tolerance = 0.002f;

            return Mathf.Abs(a.r - b.r) < tolerance &&
                   Mathf.Abs(a.g - b.g) < tolerance &&
                   Mathf.Abs(a.b - b.b) < tolerance &&
                   Mathf.Abs(a.a - b.a) < tolerance;
        }

        private void OnDestroy()
        {
            if (_ledMaterial != null)
                Destroy(_ledMaterial);

            if (_runtimeTexture != null)
                Destroy(_runtimeTexture);

            if (_rainbowTexture != null)
                Destroy(_rainbowTexture);
        }
    }
}
