using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.Lanternfly._9999
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/Lanternfly Setpoint", order = 0)]
    public class LanternflySetpoint : ScriptableObject
    {
        [Tooltip("Inches")] public float elevatorHeight;
        [Tooltip("Degrees")] public float armAngle;
		[Tooltip("Degrees")] public float funnelAngle;
        [Tooltip("Degrees")] public float climbAngle;
    }
}