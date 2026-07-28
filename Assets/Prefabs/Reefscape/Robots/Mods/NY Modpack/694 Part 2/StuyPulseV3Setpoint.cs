using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYModpack._694
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/StuyV3 Setpoint", order = 0)]
    public class StuyPulsev3Setpoint : ScriptableObject
    {
        [Tooltip("Deg")] public float armAngle;
        [Tooltip("Inch")] public float elevatorHeight;
        [Tooltip("Deg")] public float intakeAngle;
        [Tooltip("Deg")] public float climberAngle;
    }
}
