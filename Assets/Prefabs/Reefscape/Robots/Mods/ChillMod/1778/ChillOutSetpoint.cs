using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.ChillMod._1778
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/Chill Setpoint", order = 0)]
    public class ChillOutSetpoint : ScriptableObject
    {
        [Tooltip("Inches")] public float elevatorHeight;
        [Tooltip("Degrees")] public float armAngle;
        [Tooltip("Degrees")] public float intakeAngle;
    }
}