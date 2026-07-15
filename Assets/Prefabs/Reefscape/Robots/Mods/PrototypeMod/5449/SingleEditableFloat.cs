using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.PrototypeMod._5449
{
    [CreateAssetMenu(fileName = "Float", menuName = "Robot/EditableFloat", order = 0)]
    public class SingleEditableFloat : ScriptableObject
    {
        [Tooltip("Units")] public float value;
    }
}