using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.RoboGym._3950
{
    [CreateAssetMenu(fileName = "Setpoint", menuName = "Robot/RoboGym Setpoint", order = 0)]
    public class RoboGymSetpoint : ScriptableObject
    {
        [Tooltip("Inches")] public float elevatorHeight;
    }
}