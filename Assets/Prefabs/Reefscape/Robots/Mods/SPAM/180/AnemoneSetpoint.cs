using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.SPAM._180
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/Anemone Setpoint", order = 0)]
    public class AnemoneSetpoint : ScriptableObject
    {
        [Tooltip("Carriage travel, meters (0 = bottom)")] public float elevatorHeight;
        [Tooltip("Degrees the arm tube's top end points above horizontal, toward the scoring side (180's arm pivot angle)")]
        public float armAngle;
        [Tooltip("Degrees the intake's driven bar is folded up from deployed (0 = deployed)")] public float intakeAngle;
        [Tooltip("Degrees the climber arm is folded out from upright (90 = flat out the side)")] public float climberAngle;
    }
}
