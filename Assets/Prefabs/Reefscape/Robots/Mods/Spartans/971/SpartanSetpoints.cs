using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.Spartans._971
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/Spartan Setpoint", order = 0)]
    public class SpartansSetpoint : ScriptableObject
    {
        [Tooltip("Inches")] public float elevatorHeight;
        [Tooltip("Radians")] public float armAngle;
        [Tooltip("Radians")] public float wristAngle;
        [Tooltip("Radians")] public float intakeAngle;
        [Tooltip("Radians")] public float climberAngle;
    }
}