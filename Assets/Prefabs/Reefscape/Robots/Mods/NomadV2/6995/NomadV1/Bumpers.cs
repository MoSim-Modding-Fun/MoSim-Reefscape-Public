using UnityEngine;

public class BumperMaterialSync : MonoBehaviour
{
    private MeshRenderer bumperRenderer;

    private void Start()
    {
        bumperRenderer = GetComponent<MeshRenderer>();
    }

    private void LateUpdate()
    {
        if (bumperRenderer == null)
            return;

        Material[] materials = bumperRenderer.materials;

        if (materials.Length < 2)
            return;

        materials[1] = materials[0];

        bumperRenderer.materials = materials;
    }
}