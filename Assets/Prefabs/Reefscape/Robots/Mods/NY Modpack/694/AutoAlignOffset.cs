using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYPowerhousePack
{
    [CreateAssetMenu(fileName = "AutoAlignOffset", menuName = "Robot/StuyPulse AutoAlignOffset", order = 0)]
    public class AutoAlignOffset : ScriptableObject
    {
        [Tooltip("Inches")] public float xOffset;
        [Tooltip("Inches")] public float yOffset;
        [Tooltip("Inches")] public float zOffset;
        [Tooltip("Degrees")] public float Rotation;
    }
}