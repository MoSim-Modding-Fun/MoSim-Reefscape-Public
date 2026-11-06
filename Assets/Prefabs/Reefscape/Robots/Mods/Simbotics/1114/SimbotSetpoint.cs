using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.Simbotics._1114
{
    /// <summary>A lift pose, in the units of 1114's code (ElevatorConstants, ArmConstants).</summary>
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/Simbot Suzuki Setpoint", order = 0)]
    public class SimbotSetpoint : ScriptableObject
    {
        [Tooltip("Their code's elevator units (about 0.78 real inches each, see SimbotSuzuki.CodeInch)")]
        public float elevatorHeight;
        [Tooltip("Degrees: 0 forward, +90 up, -90 down. The arm passes through the carriage to reach -92.")]
        public float armAngle;
    }
}
