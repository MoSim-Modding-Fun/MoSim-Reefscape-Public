using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.MissDaisy._341
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/Miss Daisy Setpoint", order = 0)]
    public class MissDaisySetpoint : ScriptableObject
    {
        [Tooltip("Meters")] public float elevatorHeight;
        [Tooltip("Degrees")] public float shoulderAngle;
        [Tooltip("Degrees")] public float wristAngle;
        [Tooltip("Degrees")] public float intakeAngle;
        [Tooltip("Degrees")] public float climberAngle;
    }
}
