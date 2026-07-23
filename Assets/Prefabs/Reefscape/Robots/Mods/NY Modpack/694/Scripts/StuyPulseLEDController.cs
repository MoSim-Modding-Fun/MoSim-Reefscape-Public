using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using RobotFramework.Controllers.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYPowerhousePack._694
{
    /// <summary>
    /// 694-specific LED controller. Auto align (reef or station) takes over the strip first, mirroring how
    /// the real robot's alignment commands require the LED subsystem and pre-empt LEDDefaultCommand while
    /// they're running. Below that sits the real LEDDefaultCommand's own priority chain (github.com/StuyPulse/Aunt-Mary):
    /// scoring, climbed/climbing, climb open, algae intake, froggy coral intake, processor, has-coral, then
    /// off. Reads only the public state already exposed by ReefscapeRobotBase and the game piece
    /// controllers, so it can be dropped onto the existing 694 rig without editing it. Colors default to
    /// the values used in the real robot's Settings.LED constants.
    ///
    /// LED surfaces are wired the same way as GRRLights/LEDStripController: drag the LED mesh GameObject(s)
    /// into `leds`, and this script instantiates one shared material and assigns it to each of their
    /// renderers at Start, then drives that material's emission color.
    /// </summary>
    public class StuyPulseLEDController : MonoBehaviour
    {
        [Header("LED Surfaces")]
        [Tooltip("Drag every GameObject (with a Renderer) that makes up the main LED strip here.")]
        [SerializeField] private GameObject[] leds;

        [Tooltip("Optional: the subset of the strip that lights up for the left side during a side-specific state (align left, etc). Falls back to leds if empty.")]
        [SerializeField] private GameObject[] leftAccentLeds;

        [Tooltip("Optional: the subset of the strip that lights up for the right side. Falls back to leds if empty.")]
        [SerializeField] private GameObject[] rightAccentLeds;

        [Tooltip("Optional shader to build a fresh material from (same idea as GRRLights/LEDStripController's shaderGraphShader). If left empty, this clones whatever material is already on each LED mesh instead.")]
        [SerializeField] private Shader ledShader;

        [Header("Intensity")]
        [SerializeField] private float onIntensity = 20f;
        [SerializeField] private float offIntensity = 0f;
        [SerializeField] private float blinkPeriod = 0.5f;

        [Header("Colors (defaults match Settings.LED from the real robot code)")]
        [SerializeField] private Color scoreColor = Color.green;
        [SerializeField] private Color climbOpenColor = Color.yellow;
        [SerializeField] private Color climbingColor = Color.green;
        [SerializeField] private Color intakeAlgaeColor = Color.green;
        [SerializeField] private Color froggyIntakeCoralColor = Color.red;
        [SerializeField] private Color coralStationAlignColor = Color.red;
        [SerializeField] private Color reefAlignLeftColor = Color.yellow;
        [SerializeField] private Color reefAlignRightColor = Color.red;
        [SerializeField] private Color processorColor = new Color(0.5f, 0f, 0.5f); // purple
        [SerializeField] private Color hasCoralColor = Color.blue;
        [SerializeField] private Color disabledColorBlue = Color.blue;
        [SerializeField] private Color disabledColorRed = Color.red;

        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        private ReefscapeRobotBase _base;
        private StuyPulseAutoAlign _autoAlign;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData> _pieces;

        private Material _stripMaterial;
        private Material _leftMaterial;
        private Material _rightMaterial;

        private float _scoreFlashUntil;
        private bool _hadCoral;
        private bool _hadAlgae;

        private void Start()
        {
            _base = GetComponent<ReefscapeRobotBase>();
            _autoAlign = GetComponent<StuyPulseAutoAlign>();
            _pieces = GetComponent<RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>>();

            _stripMaterial = BuildSharedMaterial(leds);
            _leftMaterial = leftAccentLeds is { Length: > 0 } ? BuildSharedMaterial(leftAccentLeds) : _stripMaterial;
            _rightMaterial = rightAccentLeds is { Length: > 0 } ? BuildSharedMaterial(rightAccentLeds) : _stripMaterial;
        }

        // Same idea as GRRLights/LEDStripController: one shared, runtime-instanced material assigned across
        // every renderer in the group, so setting a color on it updates all of them at once.
        private Material BuildSharedMaterial(GameObject[] objects)
        {
            if (objects == null || objects.Length == 0) return null;

            Material shared = null;
            foreach (var obj in objects)
            {
                if (obj == null || !obj.TryGetComponent<Renderer>(out var renderer)) continue;

                shared ??= ledShader != null ? new Material(ledShader) : new Material(renderer.sharedMaterial);
                renderer.material = shared;
            }

            return shared;
        }

        private void Update()
        {
            if (_base == null) return;

            var coral = _pieces != null ? _pieces.GetPieceByName(ReefscapeGamePieceType.Coral.ToString()) : null;
            var algae = _pieces != null ? _pieces.GetPieceByName(ReefscapeGamePieceType.Algae.ToString()) : null;

            var hasCoral = coral != null && coral.HasPiece();
            var hasAlgae = algae != null && algae.HasPiece();

            // A piece was carried and is now gone while we were actively placing - treat it as a score, same
            // edge-detection idiom used by GRRLights (340) for its "just scored" flash.
            if ((_hadCoral && !hasCoral || _hadAlgae && !hasAlgae) &&
                _base.CurrentSetpoint == ReefscapeSetpoints.Place)
            {
                _scoreFlashUntil = Time.time + 0.4f;
            }

            _hadCoral = hasCoral;
            _hadAlgae = hasAlgae;

            var blink = Time.time % blinkPeriod > blinkPeriod / 2f ? onIntensity : offIntensity;

            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                SetAll(_base.Alliance == Alliance.Blue ? disabledColorBlue : disabledColorRed, offIntensity);
                return;
            }

            // Auto align owns the LEDs while it's actually driving the robot, same as the real LEDApplyPattern
            // command taking over from LEDDefaultCommand for the duration of an alignment command.
            if (_autoAlign != null && _autoAlign.ReefAlignActive())
            {
                var left = _autoAlign.ReefAlignLeft();
                SetSides(left ? reefAlignLeftColor : Color.black, left ? Color.black : reefAlignRightColor, blink);
            }
            else if (_autoAlign != null && _autoAlign.StationAlignActive())
            {
                SetAll(coralStationAlignColor, onIntensity);
            }
            else if (Time.time < _scoreFlashUntil)
            {
                SetAll(scoreColor, onIntensity);
            }
            else if (_base.CurrentSetpoint == ReefscapeSetpoints.Climbed)
            {
                SetAll(climbingColor, blink);
            }
            else if (_base.CurrentSetpoint == ReefscapeSetpoints.Climb)
            {
                SetAll(climbOpenColor, onIntensity);
            }
            else if (_base.CurrentRobotMode == ReefscapeRobotMode.Algae && _base.IsIntaking && !hasAlgae &&
                     _base.CurrentSetpoint is ReefscapeSetpoints.Intake or ReefscapeSetpoints.HighAlgae
                         or ReefscapeSetpoints.LowAlgae or ReefscapeSetpoints.Stack)
            {
                SetAll(intakeAlgaeColor, onIntensity);
            }
            else if (_base.CurrentIntakeMode == ReefscapeIntakeMode.L1 && _base.CurrentSetpoint == ReefscapeSetpoints.Intake && !hasCoral)
            {
                SetAll(froggyIntakeCoralColor, onIntensity);
            }
            else if (_base.CurrentSetpoint == ReefscapeSetpoints.Processor)
            {
                SetAll(processorColor, onIntensity);
            }
            else if (hasCoral)
            {
                SetAll(hasCoralColor, blink);
            }
            else
            {
                SetAll(Color.black, offIntensity);
            }
        }

        private void SetAll(Color color, float intensity)
        {
            Set(_stripMaterial, color, intensity);
            Set(_leftMaterial, color, intensity);
            Set(_rightMaterial, color, intensity);
        }

        private void SetSides(Color leftColor, Color rightColor, float intensity)
        {
            Set(_leftMaterial, leftColor, intensity);
            Set(_rightMaterial, rightColor, intensity);
        }

        private void Set(Material material, Color color, float intensity)
        {
            if (material == null) return;
            material.SetColor(EmissionColor, color * intensity);
        }
    }
}
