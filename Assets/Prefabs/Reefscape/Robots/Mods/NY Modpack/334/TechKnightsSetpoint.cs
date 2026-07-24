using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.TechKnights._334
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/TechKnights Setpoint", order = 0)]
    public class TechKnightsSetpoint : ScriptableObject
    {
        [Tooltip("Inches")] public float elevatorHeight;
        [Tooltip("Degrees")] public float endEffectorAngle;
        [Tooltip("Degrees")] public float intakeAngle;
    }
}