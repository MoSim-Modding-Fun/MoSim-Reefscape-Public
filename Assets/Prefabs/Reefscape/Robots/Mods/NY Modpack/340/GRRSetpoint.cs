using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.GRR._340
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/GRR Setpoint", order = 0)]
    public class GRRSetpoint : ScriptableObject
    {
        [Tooltip("Deg")] public float wristTarget;
        [Tooltip("Inch")] public float elevatorDistance;
        [Tooltip("Deg")] public float climberTarget;
    }
}