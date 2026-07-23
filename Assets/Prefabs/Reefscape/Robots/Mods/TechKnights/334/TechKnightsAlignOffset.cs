using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.TechKnights._334
{
    [CreateAssetMenu(fileName = "AlignOffset", menuName = "Robot/TechKnights Align Offset", order = 0)]
    public class TechKnightsAlignOffset : ScriptableObject
    {
        [Tooltip("Vector 3")] public Vector3 alignOffset;
    }
}