// 341's printed art panels on the Miss Daisy prefab, cut from their print template
// (FinishedeNewArtPanelsTemplate.pdf): visual only, no colliders, kept apart from the physics in MissDaisyBuilder.
//  - the sponsor panel (page 1): its face between the two bend lines, from the top edge down to the fold line
//    (25.5" x 6.5", the ends slanting in 1" toward the top). It stands along the elevator side of the frame, its
//    bottom edge on the bumper top (the strip below the fold, with its cut-outs for the bumper mounts, sits behind
//    the bumper and is left out).
//  - the "Team 341 Miss Daisy" banner (page 2, 11.125" x 4.125"): under the elevator's top cross, beside the
//    green-yellow block at its end (the reveal video's close-up).
using System;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    public static class MissDaisyPanels
    {
        private const string Dir = "Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy/341/Panels";
        private const float Inch = 0.0254f;
        private const float Thickness = 0.125f * Inch;

        // cad: CAD point (x across, y along, z up from the floor, m) to world
        public static void Add(Transform root, Func<Vector3, Vector3> cad)
        {
            var holder = new GameObject("Art Panels").transform;
            holder.SetParent(root, false);
            // just outside the elevator-side frame rail (CAD x -0.33), facing out; bottom on the bumper top (~0.15 m)
            var outward = (cad(new Vector3(-1, 0, 0)) - cad(Vector3.zero)).normalized;
            Panel(holder, "Sponsor Panel", cad(new Vector3(-0.333f, 0f, 0.150f + 3.25f * Inch)), outward, 25.5f * Inch, 6.5f * Inch);
            // the banner hangs under the elevator's top cross (CAD z 1.019), on its outer edge, LEDs along its top
            Panel(holder, "Miss Daisy Banner", cad(new Vector3(-0.330f, 0f, 1.019f - 2.0625f * Inch)), outward, 11.125f * Inch, 4.125f * Inch);
        }

        private static void Panel(Transform parent, string name, Vector3 centre, Vector3 outward, float width, float height)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, true);
            // local -z outward, +x to the right as seen from outside
            go.transform.SetPositionAndRotation(centre, Quaternion.LookRotation(-outward, Vector3.up));
            go.AddComponent<MeshFilter>().sharedMesh = Slab(name, width, height);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new[] { Material(name), BackMaterial(name) };
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.AddComponent<BoxCollider>(); // a solid 1/8" panel (fitted to the slab)
        }

        // Printed 1/8" polycarbonate: the art upright on the outside face (submesh 0), the back and edges plain yellow
        // (submesh 1). Saved beside the texture so the prefab keeps it.
        private static Mesh Slab(string name, float w, float h)
        {
            float x = w / 2, y = h / 2, z = Thickness / 2;
            var v = new System.Collections.Generic.List<Vector3>();
            var uv = new System.Collections.Generic.List<Vector2>();
            var front = new System.Collections.Generic.List<int>();
            var back = new System.Collections.Generic.List<int>();
            var tris = front;
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
            {
                var i = v.Count;
                v.AddRange(new[] { a, b, c, d });
                uv.AddRange(new[] { ua, ub, uc, ud });
                tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 }); // clockwise seen from the front
                tris = back; // everything after the outside face
            }
            Vector2 U(float u, float t) => new(u, t);
            // outside (-z), seen from -z: bottom-left, top-left, top-right, bottom-right
            Quad(new(-x, -y, -z), new(-x, y, -z), new(x, y, -z), new(x, -y, -z), U(0, 0), U(0, 1), U(1, 1), U(1, 0));
            // back (+z), seen from +z the same print shows mirrored
            Quad(new(x, -y, z), new(x, y, z), new(-x, y, z), new(-x, -y, z), U(1, 0), U(1, 1), U(0, 1), U(0, 0));
            // edges: top, bottom, left, right
            Quad(new(-x, y, -z), new(-x, y, z), new(x, y, z), new(x, y, -z), U(0, 1), U(0, 1), U(1, 1), U(1, 1));
            Quad(new(x, -y, -z), new(x, -y, z), new(-x, -y, z), new(-x, -y, -z), U(1, 0), U(1, 0), U(0, 0), U(0, 0));
            Quad(new(-x, -y, z), new(-x, y, z), new(-x, y, -z), new(-x, -y, -z), U(0, 0), U(0, 1), U(0, 1), U(0, 0));
            Quad(new(x, -y, -z), new(x, y, -z), new(x, y, z), new(x, -y, z), U(1, 0), U(1, 1), U(1, 1), U(1, 0));
            var path = $"{Dir}/{name} Slab.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (!mesh)
            {
                mesh = new Mesh { name = name + " Slab" };
                AssetDatabase.CreateAsset(mesh, path);
            }
            mesh.Clear();
            mesh.SetVertices(v);
            mesh.SetUVs(0, uv);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(front, 0);
            mesh.SetTriangles(back, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);
            return mesh;
        }

        // the back: the house yellow, cut to the same outline as the print (a white mask with the print's alpha)
        private static Material BackMaterial(string name)
        {
            var maskPath = $"{Dir}/{name} Back.png";
            var importer = (TextureImporter)AssetImporter.GetAtPath(maskPath);
            if (importer && !importer.alphaIsTransparency)
            {
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }
            var matPath = $"{Dir}/{name} Back.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (!mat)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            var yellow = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Yellow.mat");
            mat.SetColor("_BaseColor", yellow ? yellow.GetColor("_BaseColor") : new Color(1f, 0.957f, 0f));
            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath));
            mat.SetFloat("_Smoothness", 0.55f);
            mat.SetFloat("_AlphaClip", 1f);
            mat.SetFloat("_Cutoff", 0.5f);
            mat.EnableKeyword("_ALPHATEST_ON");
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material Material(string name)
        {
            var texPath = $"{Dir}/{name}.png";
            var importer = (TextureImporter)AssetImporter.GetAtPath(texPath);
            if (importer.maxTextureSize != 2048 || !importer.alphaIsTransparency)
            {
                importer.maxTextureSize = 2048;
                importer.alphaIsTransparency = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }
            var matPath = $"{Dir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (!mat)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            mat.SetFloat("_Smoothness", 0.55f); // printed polycarbonate
            // the slanted ends are cut out of the texture's alpha
            mat.SetFloat("_AlphaClip", 1f);
            mat.SetFloat("_Cutoff", 0.5f);
            mat.EnableKeyword("_ALPHATEST_ON");
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
