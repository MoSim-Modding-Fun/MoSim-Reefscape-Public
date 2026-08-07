using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.China_Modpack._8810
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/Alphabot Setpoint", order = 0)]
    public class AlphabotsSetpoint : ScriptableObject
    {
        [Tooltip("Inches")] public float elevatorHeight;
        [Tooltip("Degrees")] public float armAngle;
    }
}