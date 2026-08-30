using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NomadV2Mod._6995
{
    [CreateAssetMenu(
        fileName = "Setpoint",
        menuName = "Robot/NomadV2 Setpoint",
        order = 0
    )]
    public class NomadV2Setpoint : ScriptableObject
    {
        [Header("Arm")]
        [Tooltip("Degrees")]
        public float armAngle;

        [Header("Wrist")]
        [Tooltip("Degrees")]
        public float wristAngle;

        [Header("Elevator / Arm Extension")]
        [Tooltip("Inches")]
        public float armDistance;
    }
}