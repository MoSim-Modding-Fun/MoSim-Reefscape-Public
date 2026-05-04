using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.Spartans._971
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/Spartan Setpoint", order = 0)]
    public class SpartansSetpoint : ScriptableObject
    {
        [Tooltip("Inches")] public float elevatorHeight;
        [Tooltip("Degrees")] public float armAngle;
        [Tooltip("Degrees")] public float wristAngle;
        [Tooltip("Degrees")] public float intakeAngle;
        [Tooltip("Degrees")] public float climberAngle;
    }
}