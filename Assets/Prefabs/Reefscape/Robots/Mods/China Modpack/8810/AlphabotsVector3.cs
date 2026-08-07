using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.China_Modpack._8810
{
    [CreateAssetMenu(fileName = "Vector 3", menuName = "Robot/Vector 3", order = 0)]
    public class AlphabotsVector3 : ScriptableObject
    {
        [Tooltip("Vector3 idk man")] public UnityEngine.Vector3 vector3;
        [Tooltip("Degrees")] public float rotation;
    }
}