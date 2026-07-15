using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYPowerhousePack
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/StuyPulse Setpoint", order = 0)]
    public class StuyPulseSetpoint : ScriptableObject
    {
        [Tooltip("Inches")] public float elevatorHeight;
        [Tooltip("Degrees")] public float eeArmAngle;
        [Tooltip("Degrees")] public float froggyAngle;
        [Tooltip("Degrees")] public float climbPivot1Angle;
        [Tooltip("Degrees")] public float climbPivot2Angle;
    }
}
