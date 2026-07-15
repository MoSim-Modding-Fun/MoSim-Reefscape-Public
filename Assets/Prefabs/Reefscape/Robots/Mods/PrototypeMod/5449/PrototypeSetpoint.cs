using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.PrototypeMod._5449
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/Prototype Setpoint", order = 0)]
    public class PrototypeSetpoint : ScriptableObject
    {
        [Tooltip("Inches")] public float elevatorHeight;
        [Tooltip("Degrees")] public float armAngle;
        [Tooltip("Degrees")] public float funnelAngle;
        [Tooltip("Degrees")] public float climberAngle;
    }
}