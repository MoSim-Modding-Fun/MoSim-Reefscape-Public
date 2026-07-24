using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using RobotFramework.Components;
using RobotFramework.Controllers.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.RoboGym._3950
{
    public class RoboGymLights : MonoBehaviour
    {
        [Header("LED Strips")]
        public GameObject[] strips;

        [Header("Textures")]
        public Shader shaderGraphShader;
        public Texture red;
        public Texture green;
        public Texture white;
        public Texture disabled;

        [SerializeField] private GenericElevator elevator;
        [SerializeField] private ReefscapeAutoAlign align;

        private ReefscapeRobotBase _base;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        private Material _material;

        private void Start()
        {
            _base = GetComponent<ReefscapeRobotBase>();
            _coralController = GetComponent<RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>>()
                ?.GetPieceByName(ReefscapeGamePieceType.Coral.ToString());

            _material = new Material(shaderGraphShader);
            foreach (var strip in strips)
            {
                strip.GetComponentInChildren<Renderer>().material = _material;
            }
        }

        private void Update()
        {
            if (_base == null || _coralController == null) return;

            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                Set(disabled, 20f);
                _material.SetFloat("_X", Time.time * 0.08f);
                return;
            }

            bool coral = _coralController.HasPiece();
            bool elevatorUp = elevator != null && elevator.GetElevatorHeight() > 1f;
            bool aligning = _base.AutoAlignLeftAction.IsPressed() || _base.AutoAlignRightAction.IsPressed();

            if (align.getDistance() < 0.35f && aligning)
            {
                Set(white, 20f);
            }
            else if (aligning)
            {
                Set(white, Blink(6f));
            }
            else if (coral)
            {
                Set(green, elevatorUp ? Blink(5f) : 20f);
            }
            else
            {
                Set(red, 20f);
            }
        }

        private static float Blink(float hz)
        {
            return Time.time % (1f / hz) > 1f / (hz * 2f) ? 20f : 0f;
        }

        private void Set(Texture texture, float intensity)
        {
            _material.SetFloat("_X", 0f);
            _material.SetFloat("_Y", 0f);
            _material.SetFloat("_intensity", intensity);
            if (texture != null) _material.SetTexture("_Texture2D", texture);
        }
    }
}
