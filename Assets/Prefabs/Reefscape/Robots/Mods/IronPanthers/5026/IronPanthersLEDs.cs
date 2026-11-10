using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using Games.Reefscape.Scoring.Scorers;
using RobotFramework.Controllers.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.IronPanthers._5026
{
    // The team's RGB subsystem (subsystems/rgb/RGB.java): one strip, the first message that applies wins.
    // Purple strobe on the cage; with coral, the selected level's strobe, or a green pulse; otherwise a rainbow.
    public class IronPanthersLEDs : MonoBehaviour
    {
        [SerializeField] private GameObject[] leds;
        [SerializeField] private Shader shaderGraphShader;
        [SerializeField] private float intensity = 20f;
        [SerializeField] private float strobeHz = 4f, pulseHz = 1f;
        [SerializeField] private float rainbowScroll = 0.5f;

        // RGBConstants.Colors
        private static readonly Color Purple = new Color32(127, 0, 127, 255), Blue = new Color32(0, 0, 255, 255),
            Orange = new Color32(255, 35, 0, 255), Yellow = new Color32(255, 107, 0, 255), Red = new Color32(255, 0, 0, 255),
            Green = new Color32(0, 255, 0, 255);

        private static readonly int TextureProp = Shader.PropertyToID("_Texture2D"), XProp = Shader.PropertyToID("_X"),
            YProp = Shader.PropertyToID("_Y"), IntensityProp = Shader.PropertyToID("_intensity");

        private Material _material;
        private Texture2D _solid, _rainbow;
        private ReefscapeRobotBase _robot;
        private ClimbScorer _climb;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coral;
        private ReefscapeSetpoints _level = ReefscapeSetpoints.Stow;
        public string Message { get; private set; }

        private void Start()
        {
            _robot = GetComponent<ReefscapeRobotBase>();
            _climb = GetComponentInChildren<ClimbScorer>();
            _coral = GetComponent<RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>>().GetPieceByName(ReefscapeGamePieceType.Coral.ToString());
            _material = new Material(shaderGraphShader);
            foreach (var led in leds) led.GetComponent<Renderer>().sharedMaterial = _material;
            _solid = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _rainbow = new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat };
            for (var i = 0; i < 64; i++) _rainbow.SetPixel(i, 0, Color.HSVToRGB(i / 64f, 1f, 1f));
            _rainbow.Apply();
        }

        private void Update()
        {
            if (_robot.CurrentSetpoint is ReefscapeSetpoints.L1 or ReefscapeSetpoints.L2 or ReefscapeSetpoints.L3 or ReefscapeSetpoints.L4)
                _level = _robot.CurrentSetpoint;

            if (_climb && _climb.ScoringTriggered) Strobe("climb", Purple);
            else if (!_coral.HasPiece()) Rainbow();
            else
                switch (_level)
                {
                    case ReefscapeSetpoints.L2: Strobe("L2", Orange); break;
                    case ReefscapeSetpoints.L3: Strobe("L3", Yellow); break;
                    case ReefscapeSetpoints.L4: Strobe("L4", Blue); break;
                    case ReefscapeSetpoints.L1: Strobe("L1", Red); break;
                    default: Pulse(); break;
                }

            if (!_coral.HasPiece()) _level = ReefscapeSetpoints.Stow;
        }

        private void Show(string message, Color color, float brightness)
        {
            Message = message;
            _solid.SetPixel(0, 0, color);
            _solid.Apply();
            _material.SetTexture(TextureProp, _solid);
            _material.SetFloat(XProp, 0f);
            _material.SetFloat(YProp, 0f);
            _material.SetFloat(IntensityProp, intensity * brightness);
        }

        private void Strobe(string message, Color color) => Show(message, color, Mathf.Repeat(Time.time * strobeHz, 1f) < 0.5f ? 1f : 0f);

        private void Pulse() => Show("coral", Green, 0.5f + 0.5f * Mathf.Sin(Time.time * pulseHz * 2f * Mathf.PI));

        private void Rainbow()
        {
            Message = "rainbow";
            _material.SetTexture(TextureProp, _rainbow);
            _material.SetFloat(XProp, rainbowScroll);
            _material.SetFloat(YProp, 0f);
            _material.SetFloat(IntensityProp, intensity);
        }
    }
}
