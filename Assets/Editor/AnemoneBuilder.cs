using System.Collections.Generic;
using System.IO;
using System.Linq;
using Games.Reefscape.GamePieceSystem;
using Prefabs.Reefscape.Robots.Mods.SPAM._180;
using RobotFramework.Components;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Editor
{
    /// <summary>
    /// Builds the 180 S.P.A.M. Anemone mod (prefab, main-menu prefab, setpoints, metadata, modpack, Addressables group)
    /// from Models/Anemone.fbx, whose mechanism nodes (Drivebase, Stage2, Carriage, Arm, IntakeBottomBar, IntakeTopBar,
    /// IntakeHead, Climber) have their origins on their pivots (MoSim-Models-and-Cads/180 2025/_work/export_unity.py).
    /// Root components and their values come from the official 9496 Lynk walkthrough mod, like the other mods.
    /// Rerunnable: the setpoint table below is written over the assets; the prefabs are rebuilt.
    ///   Unity -batchmode -projectPath . -executeMethod Editor.AnemoneBuilder.Build -quit
    /// </summary>
    public static class AnemoneBuilder
    {
        private const string ModFolder = "Assets/Prefabs/Reefscape/Robots/Mods/SPAM";
        private const string Dir = ModFolder + "/180";
        private const string Fbx = Dir + "/Models/Anemone.fbx";
        private const string TemplatePrefab = "Assets/Prefabs/Reefscape/Robots/Mods/LynkModOfficial/9496/9496.prefab";
        private const string CardTemplate = "Assets/Prefabs/Reefscape/MainMenu/TutorialCardTemplate.prefab";
        private const string DrivetrainPrefab = "Assets/Prefabs/Reefscape/Robots/Drivetrain.prefab";
        private const string CoralPrefab = "Assets/Prefabs/Reefscape/Coral.prefab";
        private const string NoBounce = "Assets/Materials/PhysicsMaterials/NoBounce.physicMaterial";
        private const string LowFriction = "Assets/Materials/PhysicsMaterials/LowFrictionMaterial.physicMaterial";
        private const string GroupName = "SPAM";
        private const string TemplateGroup = "Lanternfly Mod";

        private const float WheelRadius = 0.0381f; // the WCP modules' 3 in wheels, m (CAD: centres 38.1 mm up, 76.2 mm across)
        private const float AlignOffsetInches = 10.7f;
        private const float InchesPerMeter = 39.3700787f;

        // CAD (Blender, Z up, m) -> model-root local Unity (Y up); the model is then turned round so the scoring side
        // (CAD +Y) is the front (+Z) and the floor intake (CAD -Y) the back.
        private static Vector3 B2U(float x, float y, float z) => new(-x, z, -y);

        // CAD measurements (m): pivots of the mechanism nodes (as in export_unity.py) and the parts the builder places
        // things on.
        private static readonly (string node, Vector3 cad)[] Pivots =
        {
            ("Stage2", new(0.194f, 0f, 0.023f)), ("Carriage", new(0.194f, 0f, 0.07f)), ("Arm", new(0f, 0f, 0.5605f)),
            ("IntakeCrank", new(0f, -0.297f, 0.174f)), ("IntakeBottomBar", new(0f, -0.297f, 0.174f)),
            ("IntakeTopBar", new(0f, -0.195f, 0.2185f)),
            ("IntakeHead", new(0f, -0.584f, 0.1915f)), ("Climber", new(-0.327f, -0.11f, 0.399f)),
        };
        private static readonly Vector3 HeadTopPin = new(0f, -0.5245f, 0.239f);
        // the kicker (180M-11 Bottom Roller arms, belt and 2" roller) pivots on the top bar's middle hex shaft, and the
        // bottom bar's steel control pin (180M-62) rides in a short radial slot in each arm: two bars set its angle
        private static readonly Vector3 KickerPin = new(0f, -0.3748f, 0.2297f);
        private static readonly Vector3 KickerControlPin = new(0f, -0.4400f, 0.1830f);
        private const float TubeCadAngle = 51.3f;     // arm tube axis above +Y (PCA of arm_tube)
        private const float TubeTop = 0.42f;          // tube top end, m along the axis from the pivot
        private const float TubeBottom = -0.176f;     // bellmouth lip
        private const float CoralHoldU = TubeTop - 0.15f - 0.02f; // first guess at the held coral's centre (the stow is set by hand below)
        private static readonly Vector2 AlgaeWheelA = new(0.194f, 0.921f), AlgaeWheelB = new(-0.205f, 0.884f); // (y, z)
        private const float AlgaeWheelX = -0.125f, AlgaeWheelRadius = 0.0635f, AlgaeRadius = 0.2032f;

        [MenuItem("Tools/Mods/Build Anemone (180)")]
        public static void Build()
        {
            ConfigureModel();
            var setpoints = MakeSetpoints();
            var cards = MakeTutorialCards();

            var robot = BuildRobot(setpoints);
            var robotAsset = PrefabUtility.SaveAsPrefabAsset(robot, Dir + "/180.prefab");
            var menu = BuildMainMenu(robot);
            var menuAsset = PrefabUtility.SaveAsPrefabAsset(menu, Dir + "/MainMenu180.prefab");
            Object.DestroyImmediate(menu);
            Object.DestroyImmediate(robot);

            var meta = MakeMetadata(robotAsset, menuAsset, cards);
            MakeModpack(meta);
            MakeAddressablesGroup();
            AssetDatabase.SaveAssets();
            Debug.Log("ANEMONE BUILD OK");
        }

        /// <summary>Opens the Reefscape scene with Anemone as the spawned robot (scene not saved): press Play.</summary>
        [MenuItem("Tools/Mods/Playtest Anemone (180)")]
        public static void OpenPlaytest()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/Reefscape.unity");
            var spawn = Object.FindFirstObjectByType<GameSystems.Management.RobotSpawnController>();
            if (!spawn)
            {
                Debug.LogWarning("ANEMONE no RobotSpawnController in the Reefscape scene");
                return;
            }

            var so = new SerializedObject(spawn);
            so.FindProperty("robotOverride").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<Games.Reefscape.Robots.ReefscapeRobotMetadataSO>(Dir + "/Anemone.asset");
            so.ApplyModifiedPropertiesWithoutUndo();
            Selection.activeObject = spawn.gameObject;
            Debug.Log("ANEMONE playtest ready: press Play (robot override = Anemone)");
        }

        /// <summary>For launching the editor straight into a playtest (rebuilds Anemone first):
        ///   Unity -projectPath &lt;playtest copy&gt; -executeMethod Editor.AnemoneBuilder.PlaytestNow</summary>
        public static void PlaytestNow()
        {
            Build(); // the latest of everything (the mod folder is shared with the main project)
            OpenPlaytest();
            EditorApplication.delayCall += EditorApplication.EnterPlaymode;
        }

        /// <summary>For launching the editor to look at the robot: rebuilds Anemone and opens 180.prefab in Prefab Mode.
        ///   Unity -projectPath &lt;playtest copy&gt; -executeMethod Editor.AnemoneBuilder.OpenPrefab</summary>
        public static void OpenPrefab()
        {
            Build();
            EditorApplication.delayCall += () => AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/180.prefab"));
        }

        /// <summary>Renders the built prefab from a few angles to PNGs (batchmode without -nographics), once per pose:
        /// ANEMONE_POSES="stow:|l4:arm=-16,elev=1.46|climb:climb=95" (keys arm, elev, climb).
        ///   Unity -batchmode -projectPath . -executeMethod Editor.AnemoneBuilder.Snapshot -quit  (env ANEMONE_SHOTS=dir)</summary>
        public static void Snapshot()
        {
            var outDir = System.Environment.GetEnvironmentVariable("ANEMONE_SHOTS") ?? "Temp/AnemoneShots";
            var poses = System.Environment.GetEnvironmentVariable("ANEMONE_POSES") ?? "stow:";
            Directory.CreateDirectory(outDir);
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            sun.transform.rotation = Quaternion.Euler(50, -30, 0);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f);
            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.2f);
            cam.fieldOfView = 30;
            var rt = new RenderTexture(1200, 1000, 24);
            cam.targetTexture = rt;
            foreach (var entry in poses.Split('|'))
            {
                var name = entry.Split(':')[0];
                var spec = entry.Contains(':') ? entry.Substring(entry.IndexOf(':') + 1) : "";
                var robot = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/180.prefab"));
                var bm = new SerializedObject(robot.GetComponent<Anemone>()).FindProperty("blueBumperMaterial").objectReferenceValue as Material;
                foreach (var r in BumperRenderers(robot.transform))
                {
                    var m = r.sharedMaterials;
                    m[0] = bm;
                    r.sharedMaterials = m;
                }
                if (!string.IsNullOrEmpty(spec)) Pose(robot.transform, spec);
                var centre = new Vector3(0, spec.Contains("elev=1") ? 1.1f : 0.7f, 0);
                foreach (var (view, dir) in new[] { ("front_left", new Vector3(-1f, 0.6f, 1.2f)), ("back_right", new Vector3(1.1f, 0.7f, -1.2f)),
                                                    ("left", new Vector3(-1f, 0.15f, 0f)), ("right", new Vector3(1f, 0.15f, 0f)),
                                                    ("front", new Vector3(0.05f, 0.35f, 1f)), ("top", new Vector3(0.1f, 1f, 0.15f)) })
                {
                    cam.transform.position = centre + dir.normalized * (spec.Contains("elev=1") ? 5.2f : 4.2f);
                    cam.transform.LookAt(centre);
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    File.WriteAllBytes(Path.Combine(outDir, $"{name}_{view}.png"), tex.EncodeToPNG());
                }
                Object.DestroyImmediate(robot);
            }

            RenderTexture.active = null;
            Debug.Log("ANEMONE SHOTS " + outDir);
        }

        /// <summary>The held coral at a few depths in the tube (top end 2, 7 and 12 cm below the lip), from the side,
        /// the front and above, so the hold can be picked by eye. Env ANEMONE_SHOTS=dir.</summary>
        public static void CoralDepths()
        {
            var outDir = System.Environment.GetEnvironmentVariable("ANEMONE_SHOTS") ?? "Temp/AnemoneShots";
            Directory.CreateDirectory(outDir);
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            sun.transform.rotation = Quaternion.Euler(50, -30, 0);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f);
            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.2f);
            cam.fieldOfView = 30;
            var rt = new RenderTexture(800, 800, 24);
            cam.targetTexture = rt;
            var robot = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/180.prefab"));
            // the arm at stow, as held coral is carried
            Pose(robot.transform, "arm=46.4");
            Transform T(string n) => robot.GetComponentsInChildren<Transform>(true).First(t => t.name == n);
            var hold = T("Coral Tube State");
            var up = T("Coral Eject").forward;
            var lip = T("Coral Eject").position;
            var coralPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Reefscape/Coral.prefab");
            foreach (var below in new[] { 0.02f, 0.07f, 0.12f })
            {
                var coral = (GameObject)PrefabUtility.InstantiatePrefab(coralPrefab);
                foreach (var c in coral.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                var rb = coral.GetComponent<Rigidbody>();
                if (rb) rb.isKinematic = true;
                coral.transform.SetPositionAndRotation(hold.position, hold.rotation);
                // its top end `below` under the lip: move along the tube by the difference from the hold now
                var meshTop = coral.GetComponentsInChildren<MeshFilter>().SelectMany(f => f.sharedMesh.vertices.Select(v => f.transform.TransformPoint(v))).Max(p => Vector3.Dot(p - lip, up));
                coral.transform.position += up * (-below - meshTop);
                var c0 = lip - up * 0.17f;
                foreach (var (view, dir) in new[] { ("side", robot.transform.right), ("front", robot.transform.forward + robot.transform.right * 0.3f), ("top", up + robot.transform.right * 0.15f) })
                {
                    cam.transform.position = c0 + dir.normalized * 1.3f;
                    cam.transform.LookAt(c0);
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    File.WriteAllBytes(Path.Combine(outDir, $"coral_{below * 100:F0}cm_{view}.png"), tex.EncodeToPNG());
                }
                Object.DestroyImmediate(coral);
            }
            RenderTexture.active = null;
            Debug.Log("ANEMONE CORAL DEPTHS " + outDir);
        }

        /// <summary>Renders the intake as built: its meshes, then its colliders (red blocks coral, blue lets it through,
        /// yellow capsules are rollers). Env ANEMONE_SHOTS=dir.</summary>
        public static void IntakeColliders()
        {
            var outDir = System.Environment.GetEnvironmentVariable("ANEMONE_SHOTS") ?? "Temp/AnemoneShots";
            Directory.CreateDirectory(outDir);
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            sun.transform.rotation = Quaternion.Euler(50, -30, 0);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f);
            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.2f);
            cam.orthographic = true;
            var rt = new RenderTexture(1000, 800, 24);
            cam.targetTexture = rt;
            var robot = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/180.prefab"));
            var intake = robot.GetComponentsInChildren<Transform>(true).First(t => t.name == "Intake");
            var meshes = robot.GetComponentsInChildren<Renderer>(true).ToList();
            foreach (var r in meshes) r.enabled = r.transform.IsChildOf(intake);
            var shown = meshes.Where(r => r.enabled).ToList();
            var bounds = shown.Select(r => r.bounds).Aggregate((x, y) => { x.Encapsulate(y); return x; });
            var lit = shown.Select(r => r.sharedMaterial).First(m => m).shader;
            Material Mat(Color c) { var m = new Material(lit); m.SetColor("_BaseColor", c); m.SetColor("_Color", c); return m; }
            var coral = LayerMask.NameToLayer("Coral");
            var proxies = new List<GameObject>();
            foreach (var c in intake.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger))
            {
                GameObject g;
                if (c is BoxCollider b)
                {
                    g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    g.transform.SetPositionAndRotation(b.transform.TransformPoint(b.center), b.transform.rotation);
                    g.transform.localScale = Vector3.Scale(b.transform.lossyScale, b.size);
                }
                else if (c is CapsuleCollider k)
                {
                    g = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    var axis = k.direction == 0 ? Vector3.right : k.direction == 1 ? Vector3.up : Vector3.forward;
                    g.transform.SetPositionAndRotation(k.transform.TransformPoint(k.center), k.transform.rotation * Quaternion.FromToRotation(Vector3.up, axis));
                    g.transform.localScale = new Vector3(k.radius * 2, k.height / 2, k.radius * 2);
                }
                else continue;
                Object.DestroyImmediate(g.GetComponent<Collider>());
                var passes = (c.excludeLayers.value & (1 << coral)) != 0;
                g.GetComponent<Renderer>().sharedMaterial = Mat(c is CapsuleCollider ? new Color(1f, 0.8f, 0.1f) : passes ? new Color(0.25f, 0.5f, 1f) : new Color(1f, 0.25f, 0.2f));
                proxies.Add(g);
            }
            cam.orthographicSize = bounds.extents.magnitude * 0.7f;
            foreach (var (view, dir) in new[] { ("side", Vector3.right), ("iso", new Vector3(1f, 0.7f, -0.9f)), ("front", new Vector3(0.2f, 0.4f, -1f)) })
            {
                cam.transform.position = bounds.center + robot.transform.TransformDirection(dir.normalized) * 4f;
                cam.transform.LookAt(bounds.center);
                foreach (var mode in new[] { "mesh", "col" })
                {
                    foreach (var r in shown) r.enabled = mode == "mesh";
                    foreach (var q in proxies) q.SetActive(mode == "col");
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    File.WriteAllBytes(Path.Combine(outDir, $"intake_{view}_{mode}.png"), tex.EncodeToPNG());
                }
            }
            RenderTexture.active = null;
            Debug.Log($"ANEMONE INTAKE COLLIDERS {proxies.Count}");
        }

        /// <summary>Renders the end-effector chute as built: its meshes, then its wall plates (red). Env ANEMONE_SHOTS=dir.</summary>
        public static void ChuteColliders()
        {
            var outDir = System.Environment.GetEnvironmentVariable("ANEMONE_SHOTS") ?? "Temp/AnemoneShots";
            Directory.CreateDirectory(outDir);
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.2f;
            sun.transform.rotation = Quaternion.Euler(50, -30, 0);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.6f);
            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.2f);
            cam.orthographic = true;
            var rt = new RenderTexture(1000, 1000, 24);
            cam.targetTexture = rt;

            var robot = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/180.prefab"));
            var arm = robot.GetComponentsInChildren<Transform>(true).First(t => t.name == "Arm");
            var bore = arm.GetComponentsInChildren<Transform>(true).First(t => t.name == "Chute Walls");
            // the chute alone: tube, bellmouth and sushi rollers, against the walls and roller contacts
            var meshes = robot.GetComponentsInChildren<Renderer>(true).ToList();
            foreach (var r in meshes) r.enabled = r.transform.IsChildOf(arm) && (r.name.StartsWith("arm_tube") || r.name.StartsWith("bellmouth") || r.name.StartsWith("am-4590"));
            var armMeshes = meshes.Where(r => r.enabled).ToList();
            var bounds = armMeshes.Select(r => r.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; });

            var lit = armMeshes.Select(r => r.sharedMaterial).First(m => m).shader;
            Material Mat(Color c)
            {
                var m = new Material(lit);
                m.SetColor("_BaseColor", c);
                m.SetColor("_Color", c);
                return m;
            }
            var proxies = new List<GameObject>();
            var lines = new List<string>();
            var cols = bore.GetComponentsInChildren<Collider>(true).ToList();
            foreach (var c in cols)
            {
                GameObject g;
                if (c is BoxCollider b)
                {
                    g = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    g.transform.SetPositionAndRotation(b.transform.TransformPoint(b.center), b.transform.rotation);
                    g.transform.localScale = Vector3.Scale(b.transform.lossyScale, b.size);
                }
                else if (c is CapsuleCollider k)
                {
                    g = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    var axis = k.direction == 0 ? Vector3.right : k.direction == 1 ? Vector3.up : Vector3.forward;
                    g.transform.SetPositionAndRotation(k.transform.TransformPoint(k.center), k.transform.rotation * Quaternion.FromToRotation(Vector3.up, axis));
                    g.transform.localScale = new Vector3(k.radius * 2, k.height / 2, k.radius * 2);
                }
                else continue;
                Object.DestroyImmediate(g.GetComponent<Collider>());
                g.GetComponent<Renderer>().sharedMaterial = Mat(c is CapsuleCollider ? new Color(1f, 0.75f, 0.1f) : new Color(1f, 0.25f, 0.2f));
                proxies.Add(g);
            }
            lines.Add($"{cols.Count} wall plates");

            var centre = bounds.center;
            cam.orthographicSize = bounds.extents.magnitude * 0.75f;
            var views = new List<(string, Vector3, Vector3)>
            {
                ("side", -bore.right, bore.up), ("iso", (-bore.right + bore.up * 0.8f + bore.forward * 0.5f), bore.forward),
                ("under", (-bore.right * 0.6f - bore.up + bore.forward * 0.3f), bore.forward), ("down_the_bore", bore.forward, bore.up),
            };
            foreach (var (view, dir, upv) in views)
            {
                cam.transform.position = centre + dir.normalized * 4f;
                cam.transform.LookAt(centre, upv);
                if (view == "down_the_bore") cam.orthographicSize = 0.11f; else cam.orthographicSize = bounds.extents.magnitude * 0.75f;
                foreach (var mode in new[] { "mesh", "col" })
                {
                    foreach (var r in armMeshes) r.enabled = mode == "mesh";
                    foreach (var p in proxies) p.SetActive(mode == "col");
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    File.WriteAllBytes(Path.Combine(outDir, $"chute_{view}_{mode}.png"), tex.EncodeToPNG());
                }
            }
            RenderTexture.active = null;
            Debug.Log("ANEMONE CHUTE COLLIDERS\n" + string.Join("\n", lines));
        }

        // Puts the mechanisms of an un-simulated robot into a pose for a snapshot (joint frames as built).
        private static void Pose(Transform robot, string spec)
        {
            var kv = spec.Split(',').Select(s => s.Split('=')).ToDictionary(p => p[0], p => float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture));
            var anemone = robot.GetComponent<Anemone>();
            var so = new SerializedObject(anemone);
            float F(string f) => so.FindProperty(f).floatValue;
            Transform T(string n) => robot.GetComponentsInChildren<Transform>(true).First(t => t.name == n);
            if (kv.TryGetValue("elev", out var e))
            {
                T("Stage2").localPosition += Vector3.up * e / 2;
                T("Carriage").localPosition += Vector3.up * e / 2;
            }
            if (kv.TryGetValue("arm", out var a))
                T("Arm").localRotation = Quaternion.AngleAxis(F("armSign") * (a - F("armCadAngle")), Vector3.right);
            if (kv.TryGetValue("climb", out var c))
                T("Climber").localRotation = Quaternion.AngleAxis(F("climberSign") * c, Vector3.forward);
        }

        // ------------------------------------------------------------------ assets

        private static void ConfigureModel()
        {
            var mi = (ModelImporter)AssetImporter.GetAtPath(Fbx);
            mi.globalScale = 1;
            mi.useFileScale = true;
            mi.bakeAxisConversion = false; // like the other mods; BuildRobot measures the model's orientation
            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.CalculateMikk;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.weldVertices = true;
            mi.isReadable = false;
            mi.addCollider = false;
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.SaveAndReimport();

            Directory.CreateDirectory(Dir + "/Materials");
            var extracted = false;
            foreach (var mat in AssetDatabase.LoadAllAssetsAtPath(Fbx).OfType<Material>())
            {
                var path = $"{Dir}/Materials/{Safe(mat.name)}.mat";
                if (File.Exists(path)) continue;
                AssetDatabase.ExtractAsset(mat, path);
                extracted = true;
            }

            if (extracted) AssetDatabase.ImportAsset(Fbx, ImportAssetOptions.ForceUpdate);
        }

        private static string Safe(string n) => string.Concat(n.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        private static void CopyIfMissing(string from, string to)
        {
            if (File.Exists(to)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            AssetDatabase.CopyAsset(from, to);
        }

        private static Dictionary<string, AnemoneSetpoint> MakeSetpoints()
        {
            // elevator m (carriage travel), arm deg (tube's top end above horizontal toward the front: 180's arm pivot
            // angle), intake deg (driven bar folded up from deployed), climber deg (folded out from upright).
            // 180's own presets (ElevatorSubsystem, ElevatorArmPivotSubsystem, IntakeCoralPivotSubsystem, Climber),
            // changed only where MoSim's field needs it (marked). Intake: 0 = extend, stow ~85 (180's SimVisuals draw
            // it 90 deg up; this four-bar toggles just past that); climber 95 = deployed, back up past upright to climb.
            const float IntakeStow = 85f;
            var values = new (string name, float e, float a, float i, float c)[]
            {
                ("Stow", 0f, 46.4f, IntakeStow, 0f),               // STOW + receiving
                ("Ground Intake", 0f, 46.4f, 0f, 0f),              // receivePosition + intake extend
                ("HP Intake", 0.038f, 42.1f, IntakeStow, 0f),      // receiveHP (1.5 in) + receivingHP
                // L1_SCORE, the end effector tipping up as the coral leaves, flipping it into the trough; elevator 3 cm lower than
                // 180's 0.212 m: the tipped coral's nose then passes under MoSim's L2 branch tip (PlayMode)
                ("L1", 0.18f, -16.7f, IntakeStow, 0f),
                ("L2", 0.353f, -8f, IntakeStow, 0f),               // 0.302 m + 2 in, L2_SCORE
                ("L3", 0.735f, -8f, IntakeStow, 0f),               // L2 + 16 in - 2.4 cm, L3_SCORE: at L2 + 16 in the coral's lip clips the tip of the MoSim branch
                ("L4", 1.43f, -16f, IntakeStow, 0f),               // L4 (1.46 m) - 3 cm, L4_SCORE: at 1.46 the coral's lip clips the top of the MoSim post; lower, the post enters its hollow nose
                ("Ground Algae", 0f, -35f, IntakeStow, 0f),        // LOLLIPOP_INTAKE: 180 takes algae off the ground only from a stack
                ("Lollipop", 0f, -35f, IntakeStow, 0f),            // LOLLIPOP_INTAKE
                // 180 descores at the L2 / L3 pose; MoSim's reef algae sits 0.25 m lower against the wheel pocket, so
                // the elevator is lower at the same arm angle
                // (+9 cm: the bottom wheel then meets MoSim's ball from the bumper-against-the-reef pose and pulls it
                // up into the top wheel)
                ("Low Algae", 0.24f, -8f, IntakeStow, 0f),
                ("High Algae", 0.64f, -8f, IntakeStow, 0f),
                ("Processor", 0f, -40f, IntakeStow, 0f),           // PROCESSOR
                ("Net Approach", 0.05f, 46.4f, IntakeStow, 0f),    // lining up at the barge: the ball carried at ~1 m (footage)
                ("Net", 1.445f, 6f, IntakeStow, 0f),               // NET (1.47 m - 1 in), netScore (8 - 2): the ball sits on top
                ("Climb Prep", 0.381f, -37.2f, IntakeStow, 95f),   // CLIMB (15 in), arm CLIMB, intake stowed, climber deployed
                // out, then back up through upright and over the chassis (180's climb direction; as far as 1690's
                // MoSim climber swings, 95 -> 245 from its hanging stow)
                // CLIMB_STOW. An upright held at both the hook plate and the flange turns with the arm, so the cage only lies
                // across the robot with the arm back over the chassis: 165 deg from deployed in MoSim (past the 92.5 deg of
                // 180's MAX_CLIMB in code, on an encoder whose ratio to the arm isn't known). Hangs 20 cm up, tilted 19 deg
                // (Einstein R3 M9: 5-15 cm, nearly level); at -60 it was left with one wheel down in 1 of 2 runs (PlayMode)
                ("Climbed", 0.381f, -37.2f, IntakeStow, -70f),
            };
            Directory.CreateDirectory(Dir + "/Setpoints");
            var map = new Dictionary<string, AnemoneSetpoint>();
            foreach (var v in values)
            {
                var path = $"{Dir}/Setpoints/{v.name}.asset";
                var sp = AssetDatabase.LoadAssetAtPath<AnemoneSetpoint>(path);
                if (!sp)
                {
                    sp = ScriptableObject.CreateInstance<AnemoneSetpoint>();
                    AssetDatabase.CreateAsset(sp, path);
                }

                sp.elevatorHeight = v.e;
                sp.armAngle = v.a;
                sp.intakeAngle = v.i;
                sp.climberAngle = v.c;
                EditorUtility.SetDirty(sp);
                map[v.name] = sp;
            }

            return map;
        }

        // Three cards on the shared template, with this robot's own pictures (TutorialCards/*.png, rendered by
        // Snapshot) once they exist.
        private static List<GameObject> MakeTutorialCards()
        {
            var cards = new[]
            {
                ("TutorialCard", "intake.png", "Anemone picks coral off the floor with its four-bar intake and feeds it end-first up " +
                                               "into the arm tube. Robot Special switches to the station."),
                ("TutorialCard 1", "score.png", "Coral scores out of the top of the tube on the front. With no coral, L2 / L3 pull " +
                                                "reef algae into the wheels over the tube."),
                ("TutorialCard 2", "climb.png", "Climb folds the climber out of the left side: drive a cage post into the hook, " +
                                                "then climb again to pull up."),
            };
            Directory.CreateDirectory(Dir + "/TutorialCards");
            var list = new List<GameObject>();
            foreach (var (name, image, text) in cards)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(CardTemplate));
                go.name = name;
                go.GetComponentInChildren<TMPro.TMP_Text>(true).text = text;
                var png = $"{Dir}/TutorialCards/{image}";
                if (AssetImporter.GetAtPath(png) is TextureImporter ti && ti.textureType != TextureImporterType.Sprite)
                {
                    ti.textureType = TextureImporterType.Sprite;
                    ti.SaveAndReimport();
                }

                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(png);
                if (sprite) go.transform.Find("Image").GetComponent<UnityEngine.UI.Image>().sprite = sprite;
                list.Add(PrefabUtility.SaveAsPrefabAsset(go, $"{Dir}/TutorialCards/{name}.prefab"));
                Object.DestroyImmediate(go);
            }

            return list;
        }

        // ------------------------------------------------------------------ robot

        private static GameObject BuildRobot(Dictionary<string, AnemoneSetpoint> sp)
        {
            var template = PrefabUtility.LoadPrefabContents(TemplatePrefab);
            var robotLayer = LayerMask.NameToLayer("Robot");

            var root = new GameObject("180") { tag = "BlueRobot" };
            foreach (var src in template.GetComponents<Component>())
            {
                if (src is Transform || src is Games.Reefscape.Robots.ReefscapeRobotBase) continue;
                UnityEditorInternal.ComponentUtility.CopyComponent(src);
                var have = root.GetComponent(src.GetType());
                if (have)
                    UnityEditorInternal.ComponentUtility.PasteComponentValues(have);
                else
                    UnityEditorInternal.ComponentUtility.PasteComponentAsNew(root);
            }

            var robot = root.AddComponent<Anemone>();
            var baseSrc = template.GetComponent<Games.Reefscape.Robots.ReefscapeRobotBase>();
            var robotSo = new SerializedObject(robot);
            var it = new SerializedObject(baseSrc).GetIterator();
            if (it.NextVisible(true))
            {
                do
                {
                    if (it.name != "m_Script" && robotSo.FindProperty(it.propertyPath) != null)
                        robotSo.CopyFromSerializedProperty(it);
                } while (it.NextVisible(false));
            }

            robotSo.ApplyModifiedPropertiesWithoutUndo();

            // Drivetrain (shared prefab), wheels moved onto this robot's modules.
            var dt = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(DrivetrainPrefab), root.transform);
            var tdt = template.transform.Find("Drivetrain");
            dt.transform.localPosition = tdt.localPosition;
            dt.transform.localRotation = tdt.localRotation;
            dt.tag = "BlueRobot";
            foreach (var group in new[] { "Wheels", "Raycast" })
            {
                // the wheel centres, measured from the CAD (the modules are turned, so each corner differs by under 1 mm)
                foreach (var (n, x, z) in new[] { ("FL", -0.3334f, 0.3326f), ("FR", 0.3326f, 0.3334f), ("BL", -0.3326f, -0.3334f), ("BR", 0.3334f, -0.3326f) })
                {
                    var w = dt.transform.Find($"{group}/{n}");
                    w.localPosition = new Vector3(x, w.localPosition.y, z);
                    if (w.TryGetComponent<WheelCollider>(out var wheel)) wheel.radius = WheelRadius;
                }
            }
            // the drive's ground casts are sized from this
            var driveSo = new SerializedObject(root.GetComponent<RobotFramework.Controllers.Drivetrain.DriveController>());
            driveSo.FindProperty("wheelDiameter").floatValue = WheelRadius * 2f / 0.0254f;
            driveSo.ApplyModifiedPropertiesWithoutUndo();

            // Model: wheel bottoms on the drivetrain's ground plane, turned so the scoring side is the front.
            var wc = dt.GetComponentInChildren<WheelCollider>();
            var ground = wc.transform.TransformPoint(wc.center).y - wc.radius -
                         wc.suspensionDistance * (1 - wc.suspensionSpring.targetPosition) - root.transform.position.y;
            var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Fbx), root.transform);
            PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            var modelOffset = new Vector3(0, ground - 0.003f, 0);
            model.transform.localPosition = modelOffset;
            var mt = model.transform;
            OrientModel(mt);
            mt.RotateAround(root.transform.position, root.transform.up, 180f);
            Vector3 Turn(Vector3 v) => new(-v.x, v.y, -v.z);
            Vector3 P(float x, float y, float z) => root.transform.TransformPoint(Turn(B2U(x, y, z)) + modelOffset);
            Vector3 Pv(Vector3 c) => P(c.x, c.y, c.z);
            Vector3 D(float x, float y, float z) => root.transform.TransformDirection(Turn(B2U(x, y, z))).normalized;

            Transform Node(string n) => mt.GetComponentsInChildren<Transform>(true).First(t => t.name == n);
            var drivebase = Node("Drivebase");
            var stage2 = Node("Stage2");
            var carriage = Node("Carriage");
            var arm = Node("Arm");
            var crank = Node("IntakeCrank");
            var bar = Node("IntakeBottomBar");
            var topBar = Node("IntakeTopBar");
            var head = Node("IntakeHead");
            var climber = Node("Climber");
            var pivotErr = Pivots.Sum(p => (Node(p.node).position - Pv(p.cad)).magnitude);
            Debug.Log($"ANEMONE pivots after the turn: error {pivotErr:F4} m");
            if (pivotErr > 0.01f) throw new System.Exception("ANEMONE model axes do not match the CAD mapping");

            // the black pocketed pivot mounts on each side of the intake are frame structure (the export took them
            // for bottom-bar parts by name)
            foreach (var r in bar.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name.StartsWith("180M-04 Bottom Bar Pivot") ||
                                                                                          r.name.StartsWith("180M-19 Bottom Bar Pivot")).ToList())
                r.transform.SetParent(drivebase, true);

            var elevatorGo = new GameObject("Elevator");
            elevatorGo.transform.SetParent(root.transform, false);
            var intakeGo = new GameObject("Intake");
            intakeGo.transform.SetParent(root.transform, false);
            drivebase.SetParent(root.transform, true);
            climber.SetParent(root.transform, true);
            stage2.SetParent(elevatorGo.transform, true);
            carriage.SetParent(stage2, true);  // cascade: each stage rides on the one below
            arm.SetParent(carriage, true);
            crank.SetParent(intakeGo.transform, true);
            bar.SetParent(intakeGo.transform, true);
            topBar.SetParent(intakeGo.transform, true);
            head.SetParent(intakeGo.transform, true);
            Object.DestroyImmediate(model);
            foreach (var n in new[] { drivebase, stage2, carriage, arm, crank, bar, topBar, head, climber }) Square(n, root.transform);
            // The kicker: 180's bottom roller on its arms. In this CAD it hangs from the top bar's middle shaft and the
            // bottom bar's control pin rides in a slot in each arm, so the four-bar carries and turns it (no motor of
            // its own). Its parts came out of the CAD on the head, the bottom bar and the top bar (the pulleys and hubs
            // on its pivot): everything low between the bar and the roller, the arms and belt, and whatever sits on the
            // pivot axis.
            var kicker = Empty("Kicker", intakeGo.transform, Pv(KickerPin), root.transform.rotation);
            {
                var region = new Bounds();
                region.SetMinMax(Vector3.Min(Pv(new Vector3(-0.25f, -0.52f, 0.03f)), Pv(new Vector3(0.25f, -0.35f, 0.16f))),
                                 Vector3.Max(Pv(new Vector3(-0.25f, -0.52f, 0.03f)), Pv(new Vector3(0.25f, -0.35f, 0.16f))));
                bool OnPivot(MeshRenderer r)
                {
                    var c = root.transform.InverseTransformPoint(WorldBounds(r).center) - root.transform.InverseTransformPoint(kicker.position);
                    return new Vector2(c.y, c.z).magnitude < 0.01f && Mathf.Abs(c.x) > 0.18f && Mathf.Abs(c.x) < 0.25f;
                }
                var parts = head.GetComponentsInChildren<MeshRenderer>(true).Concat(bar.GetComponentsInChildren<MeshRenderer>(true))
                    .Concat(topBar.GetComponentsInChildren<MeshRenderer>(true))
                    .Where(r => r.name.StartsWith("180M-11") || r.name.StartsWith("180M-14") || region.Contains(WorldBounds(r).center) ||
                                r.transform.IsChildOf(topBar) && OnPivot(r) && !r.name.StartsWith("80t")).ToList();
                foreach (var r in parts) r.transform.SetParent(kicker, true);
                Debug.Log($"ANEMONE kicker parts: {parts.Count} ({string.Join(", ", parts.Select(r => r.name).Distinct().Take(12))})");
            }
            var rootRb = root.GetComponent<Rigidbody>();

            // ---- joints
            var stage2J = MakeJoint(stage2.gameObject, rootRb, linearY: true);
            var carriageJ = MakeJoint(carriage.gameObject, stage2.GetComponent<Rigidbody>(), linearY: true);
            var armJ = MakeJoint(arm.gameObject, carriage.GetComponent<Rigidbody>(), angular: 'x');
            var climberJ = MakeJoint(climber.gameObject, rootRb, angular: 'z');
            {
                // 1690's climber body: mass 2, automatic tensor off (the only joint with it off)
                var crb = climber.GetComponent<Rigidbody>();
                crb.mass = 2f;
                FixedTensor(crb);
            }
            // Intake four-bar (180M): the bottom bar is driven on the crank shaft (GenericJoint) and the crank rides
            // on it rigidly. (A sprung hinge for the crank's arc-slot give let the crank wander off the bar stowed, and
            // a knock then flipped the loop through.) The top bar swings on its frame pin and the 4 Bar Head rides on
            // both bars' pins (HingeJoints close the loop).
            crank.SetParent(bar, true);
            var crankJ = MakeJoint(bar.gameObject, rootRb, angular: 'x');
            {
                // the four-bar holds where it is while the robot is disabled (180's pivot motor brakes): the framework's
                // brake mode caps the drive at 3x the force it had that frame, which is nothing just after a spawn, and
                // the intake then dropped on every match reset
                var cso = new SerializedObject(crankJ);
                cso.FindProperty("ignoreDisabledMode").boolValue = true;
                cso.ApplyModifiedPropertiesWithoutUndo();
            }
            var barRb = bar.GetComponent<Rigidbody>();
            barRb.mass = 1.3f;
            var topRb = Body(topBar.gameObject, 0.5f);
            Hinge(topBar.gameObject, rootRb, Vector3.zero);
            var headRb = Body(head.gameObject, 1f);
            // The head's pins are held by the bars, not the head: the framework's JointStackStabilizer pulls each
            // joint's own body to spin with its connected body, and the head turns against both bars.
            var barHeadHinge = Hinge(bar.gameObject, headRb, bar.InverseTransformPoint(head.position));
            // The loop closes at the head's top pin with a joint that holds only the pin's place in the plane: four
            // parallel hinges over-constrain a planar four-bar, and PhysX then locks it part way (PlayMode: stuck at
            // 6-30 deg with the pins pulled 1-3 cm apart, no contacts). The head stays square on its bottom pin.
            var headTopHinge = PlanarPin(topBar.gameObject, headRb, topBar.InverseTransformPoint(Pv(HeadTopPin)));
            // the kicker: pinned on the top bar's shaft; its slot joint on the bottom bar's pin is added once posed
            Body(kicker.gameObject, 0.4f);
            Hinge(kicker.gameObject, topRb, Vector3.zero);
            // the intake's links stay stiff and out of walls when the robot drives the deployed head into one (with the
            // default solver the chain flexed and the head sank into the wall)
            foreach (var rb in new[] { barRb, topRb, headRb, kicker.GetComponent<Rigidbody>() })
            {
                rb.solverIterations = 30;
                rb.solverVelocityIterations = 12;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }

            // signs: + arm = tube end up; + intake = folded up (the head pin rises); + climber = folded out (left)
            var tubeDir = D(0, Mathf.Cos(TubeCadAngle * Mathf.Deg2Rad), Mathf.Sin(TubeCadAngle * Mathf.Deg2Rad));
            var armSign = Vector3.Dot(Quaternion.AngleAxis(1f, root.transform.right) * tubeDir - tubeDir, root.transform.up) > 0 ? 1f : -1f;
            var headPin = head.position - bar.position;
            var intakeSign = Vector3.Dot(Quaternion.AngleAxis(1f, root.transform.right) * headPin - headPin, root.transform.up) > 0 ? 1f : -1f;
            var climbTip = root.transform.up;
            var climberSign = Vector3.Dot(Quaternion.AngleAxis(1f, root.transform.forward) * climbTip - climbTip, -root.transform.right) > 0 ? 1f : -1f;
            Debug.Log($"ANEMONE signs arm {armSign} intake {intakeSign} climber {climberSign}");

            // ---- elevator: cascade, Stage 2 on the root and the carriage on Stage 2, each half the travel
            var elev = root.GetComponent<GenericElevator>();
            var eso = new SerializedObject(elev);
            var stages = eso.FindProperty("stages");
            stages.arraySize = 2;
            stages.GetArrayElementAtIndex(0).objectReferenceValue = stage2J;
            stages.GetArrayElementAtIndex(1).objectReferenceValue = carriageJ;
            eso.FindProperty("elevatorType").enumValueIndex = (int)RobotFramework.Enums.ElevatorType.Cascade;
            eso.FindProperty("elevatorAxis").enumValueIndex = (int)RobotFramework.Enums.JointAxis.Y;
            eso.FindProperty("flipped").boolValue = false;
            var pid = eso.FindProperty("pidConstants");
            pid.FindPropertyRelative("kP").floatValue = 15f; // 971's cascade
            pid.FindPropertyRelative("kI").floatValue = 0f;
            pid.FindPropertyRelative("kD").floatValue = 0f;
            pid.FindPropertyRelative("Max").floatValue = 4f;
            eso.FindProperty("referenceTransform").objectReferenceValue = null;
            var ov = eso.FindProperty("setStageOverrides");
            ov.arraySize = 2;
            for (var i = 0; i < 2; i++)
            {
                var o = ov.GetArrayElementAtIndex(i);
                o.FindPropertyRelative("stageNum").intValue = i + 1;
                o.FindPropertyRelative("overrideAxis").enumValueIndex = (int)RobotFramework.Enums.JointAxis.Y;
                o.FindPropertyRelative("useStartingOffset").boolValue = true;
            }
            eso.ApplyModifiedPropertiesWithoutUndo();

            var align = new SerializedObject(root.GetComponent<ReefscapeAutoAlign>());
            align.FindProperty("offset").vector3Value = new Vector3(0, 0, AlignOffsetInches);
            align.ApplyModifiedPropertiesWithoutUndo();

            var drive = new SerializedObject(root.GetComponent<RobotFramework.Controllers.Drivetrain.DriveController>());
            var dp = drive.FindProperty("drivetrainParent");
            dp.objectReferenceValue = dp.type.Contains("Transform") ? dt.transform : dt;
            drive.ApplyModifiedPropertiesWithoutUndo();

            // ---- spinning wheels (visual): every wheel mesh grouped by the shaft it sits on
            var intakeSpins = SpinShafts(head, "Head", n => n.StartsWith("am-3945") || n.StartsWith("am-3462") || n.StartsWith("4IN Compliant"), root.transform.right)
                .Concat(SpinShafts(kicker, "Kicker", n => n.StartsWith("am-3462"), root.transform.right))
                .Concat(SpinShafts(drivebase, "Indexer", n => n.StartsWith("am-3945") || n.StartsWith("am-3462"), null)).ToList();
            // the sushi rollers' shafts run side to side across the chute (mesh: long along the robot's X)
            var tubeSpins = SpinShafts(arm, "Tube", n => n.StartsWith("am-4590"), root.transform.right);
            var algaeSpins = SpinShafts(arm, "Algae", n => n.StartsWith("Part 1"), root.transform.right);
            // everything on a wheel group's axle turns with it: the hex shaft, hubs, spacers and pulleys
            var onAxle = new System.Text.RegularExpressions.Regex(@"^(180M-60|\.375 ThunderHex|\.500 Hex|18-tooth|20t HTD|Versahub|.*[Pp]ulley|.*[Pp]ully)");
            var riders = 0;
            foreach (var spin in intakeSpins.Concat(tubeSpins).Concat(algaeSpins))
            {
                var a = spin.transform.right;
                foreach (var r in spin.transform.parent.GetComponentsInChildren<MeshRenderer>(true).ToList())
                {
                    if (!onAxle.IsMatch(r.name) || r.GetComponentInParent<GenericAnimationJoint>()) continue;
                    if (Vector3.ProjectOnPlane(WorldBounds(r).center - spin.transform.position, a).magnitude > 0.006f) continue;
                    r.transform.SetParent(spin.transform, true);
                    riders++;
                }
            }
            Debug.Log($"ANEMONE axle parts turning with their wheels: {riders}");
            Debug.Log($"ANEMONE spinners: intake {intakeSpins.Count}, tube {tubeSpins.Count}, algae {algaeSpins.Count}");
            var headRollers = new List<CapsuleCollider>();

            // ---- game pieces
            var pivot = arm.position;
            // the chute's walls, traced from its mesh; the bore sits a few mm off the PCA axis through the pivot
            pivot = ChuteWalls(arm, pivot, tubeDir, root.transform.right,
                arm.GetComponentsInChildren<MeshFilter>(true).Where(f => f.name.StartsWith("arm_tube") || f.name.StartsWith("bellmouth")).ToList(),
                AssetDatabase.LoadAssetAtPath<PhysicMaterial>(LowFriction),
                TubeBottom - 0.01f, TubeTop + 0.015f);
            Vector3 Tube(float u) => pivot + tubeDir * u;
            var coralLong = CoralLongAxis();
            var across = Quaternion.FromToRotation(coralLong, root.transform.right);
            // end-first poses are the across pose swung end-up (no twist about the coral's length): a turn between
            // them is then a plain 90 deg swing of its long axis, not a 150 deg tumble
            Quaternion EndFirst(Vector3 dir) => Quaternion.FromToRotation(root.transform.right, dir) * across;
            var alongTube = EndFirst(tubeDir);
            // held in the tube with ~2 cm of its top end inside the lip (180's middle beam break)
            // the coral sits up the tube with its top end just inside the lip (barely visible from the top)
            var coralHold = Empty("Coral Tube State", arm, Tube(CoralHoldU), alongTube);
            // the stow as set by hand in the editor (deeper down the tube, hidden)
            coralHold.localPosition = new Vector3(0.001f, 0.0399f, 0.0332f);
            coralHold.localEulerAngles = new Vector3(128.7f, 0f, 128.7f);
            var holdU = Vector3.Dot(coralHold.position - pivot, tubeDir);
            Debug.Log($"ANEMONE coral stow {holdU:F3} m up the tube (top {TubeTop}), off-axis {Vector3.ProjectOnPlane(coralHold.position - pivot, tubeDir).magnitude * 1000:F1} mm, " +
                      $"long axis {Vector3.Angle(coralHold.rotation * CoralLongAxis(), tubeDir):F1} deg off the tube");
            var coralEject = Empty("Coral Eject", arm, Tube(TubeTop), Quaternion.LookRotation(tubeDir, root.transform.up));
            // Floor coral, stepped through the robot by game piece states (as 1690's and 694's mods do), along the real
            // channel measured from CAD sections (Anemone, intake deployed, arm at the receive pose). Lying across, it
            // is pinched under the front roller against the carpet, squeezed between the front roller and the kicker,
            // under the 4" roller and over the bumper's top edge (the compliant wheels and the bumper give ~2 cm), into
            // the indexer channel (floor and ceiling plates 12.5 cm apart at 49 deg). There the uptake's wheel stacks,
            // spinning about the channel's normal, turn it end-first in the channel's plane as it climbs. All its turning
            // is done there: from the channel's top it keeps the stow's rotation (the arm holds it at the receive pose)
            // and only slides, onto the tube's line, in at the bellmouth and up the tube once the arm waits at the receive pose.
            var receive = Quaternion.AngleAxis(armSign * (sp["Ground Intake"].armAngle - TubeCadAngle), root.transform.right);
            var tubeAtReceive = receive * tubeDir;
            // the channel: its floor plate's surface from CAD (y, z) through F along d; the coral's centre 62.5 mm off it
            Vector3 Channel(float s, float x = 0f) => P(x, -0.4028f + 0.6561f * s, 0.1632f + 0.7547f * s);
            var upChannel = D(0, 0.6561f, 0.7547f);
            // turned th deg in the channel's plane, from lying across toward end-first up the channel
            Quaternion Th(float th) => EndFirst(Mathf.Cos(th * Mathf.Deg2Rad) * root.transform.right + Mathf.Sin(th * Mathf.Deg2Rad) * upChannel);
            var holdRecv = receive * coralHold.rotation;
            var floorStates = new[]
            {
                ("Coral Intake", Empty("Coral Intake State", head, P(0, -0.605f, 0.057f), across), 50f, 0.001f),
                ("Coral Pinch", Empty("Coral Pinch State", head, P(0, -0.548f, 0.075f), across), 50f, 0.001f),
                ("Coral Kick", Empty("Coral Kick State", head, P(0, -0.527f, 0.125f), across), 50f, 0.001f),
                ("Coral Under", Empty("Coral Under State", head, P(0, -0.495f, 0.155f), across), 50f, 0.001f),
                ("Coral Over", Empty("Coral Over State", head, P(0, -0.445f, 0.195f), across), 50f, 0.001f),
                ("Coral Entry", Empty("Coral Entry State", drivebase, P(0, -0.400f, 0.235f), across), 50f, 0.001f),
                // the turn, searched in the channel's plane between the uptake's shafts (rigid, the channel's full depth)
                // and its 3" wheels (near the floor and ceiling, where the coral is ~39 mm wide; they give 15 mm): the
                // lower stacks swing one end up and the coral pivots off to the side, then climbs at ~60 deg and
                // straightens end-first between the upper stacks
                ("Coral Channel", Empty("Coral Channel State", drivebase, Channel(0.100f), across), 60f, 0.001f),
                ("Coral Pivot 1", Empty("Coral Pivot 1 State", drivebase, Channel(0.100f, -0.050f), Th(15f)), 40f, 0.001f),
                ("Coral Pivot 2", Empty("Coral Pivot 2 State", drivebase, Channel(0.100f, -0.055f), Th(20f)), 40f, 0.001f),
                ("Coral Pivot 3", Empty("Coral Pivot 3 State", drivebase, Channel(0.110f, -0.070f), Th(45f)), 40f, 0.001f),
                ("Coral Pivot 4", Empty("Coral Pivot 4 State", drivebase, Channel(0.125f, -0.075f), Th(55f)), 40f, 0.001f),
                ("Coral Climb 1", Empty("Coral Climb 1 State", drivebase, Channel(0.180f, -0.055f), Th(60f)), 60f, 0.001f),
                ("Coral Climb 2", Empty("Coral Climb 2 State", drivebase, Channel(0.255f, -0.010f), Th(60f)), 60f, 0.001f),
                // end-first and already in the stow's rotation (the tube's line, 2.6 deg off the channel's): from here
                // the coral only slides, onto the tube's line and up it
                ("Coral End First", Empty("Coral End First State", drivebase, Channel(0.300f, -0.005f), holdRecv), 60f, 0.001f),
                ("Coral Mouth", Empty("Coral Mouth State", arm, Tube(TubeBottom + 0.10f), coralHold.rotation), 60f, 0.001f),
            };
            // the catch: a thin strip on the front roller's face (3" wheels, centre CAD y -0.60 z 0.134), where a coral
            // lying on the carpet touches them; nothing is pulled before that. Planar tolerance as 1690's.
            var intakeTarget = Empty("Coral Intake Target", head, floorStates[0].Item2.position, root.transform.rotation);
            var groundIntake = MakeIntake("Ground Coral Intake", head, P(0, -0.655f, 0.06f), new Vector3(0.38f, 0.06f, 0.03f),
                intakeTarget, coral: true, smooth: true, speed: 60f);
            {
                var gso = new SerializedObject(groundIntake);
                gso.FindProperty("usePlanarTolerancing").boolValue = true;
                gso.FindProperty("boxExtents").vector3Value = new Vector3(16f, 5f, 5f);
                gso.FindProperty("maxDistance").floatValue = 1f;
                gso.FindProperty("accuracy").floatValue = 1f;
                gso.FindProperty("intakeForce").floatValue = 118f;
                gso.FindProperty("useRotaion").boolValue = false;
                gso.ApplyModifiedPropertiesWithoutUndo();
            }
            // station coral: caught by the sushi rollers anywhere in the top of the tube, from just above the lip
            // down the bore (a coral dropped in falls past the lip), and pulled to the hold point
            var hpIntake = MakeIntake("Tube Coral Intake", arm, Tube((TubeTop + 0.06f + holdU - 0.10f) / 2), new Vector3(0.15f, 0.15f, TubeTop + 0.06f - (holdU - 0.10f)),
                coralHold, coral: true, maxDistance: 12f);
            hpIntake.transform.rotation = Quaternion.LookRotation(tubeDir, root.transform.up);

            // algae: cradled on the two wheel pairs of the V over the tube's top end
            var wa = P(AlgaeWheelX, AlgaeWheelA.x, AlgaeWheelA.y);
            var wb = P(AlgaeWheelX, AlgaeWheelB.x, AlgaeWheelB.y);
            var mid = (wa + wb) / 2;
            var halfGap = (wa - wb).magnitude / 2;
            var outN = Vector3.Cross(root.transform.right, wa - wb).normalized;
            if (Vector3.Dot(outN, mid - pivot) < 0) outN = -outN;
            var sink = 0.06f; // well into the compliant wheels: 180's "Mickey Mouse" hugs the ball
            var reach = Mathf.Sqrt(Mathf.Pow(AlgaeRadius + AlgaeWheelRadius - sink, 2) - halfGap * halfGap);
            var algaeHold = Empty("Algae State", arm, mid + outN * reach, Quaternion.identity);
            // taken only once the ball touches a wheel: the spinning wheel then drags it into the V. The zones are
            // shells round each wheel shaft (set with the wheels' colliders below), so nothing is pulled from a distance.
            var algaeIntake = MakeIntake("Algae Intake", arm, mid, Vector3.one * 0.01f, algaeHold, coral: false, maxDistance: 16f);
            var algaeEject = Empty("Algae Eject", arm, algaeHold.position, Quaternion.LookRotation(outN, root.transform.up));

            // Which way each wheel group turns when its rollers run forward: the side touching the piece moves the way
            // the piece goes there (along the floor coral's path for the intake, kicker and uptake/indexer stacks, up the
            // tube for the sushi rollers, into the V for the algae wheels). Positive spin is Rotate(+, local X).
            float SpinSign(GenericAnimationJoint spin, Vector3 contact, Vector3 along)
            {
                var a = spin.transform.right;
                var r = Vector3.ProjectOnPlane(contact - spin.transform.position, a);
                if (r.sqrMagnitude < 1e-8f) return 1f;
                return Vector3.Dot(Quaternion.AngleAxis(1f, a) * r - r, along) >= 0 ? 1f : -1f;
            }
            var coralPath = floorStates.Take(floorStates.Length - 1).Select(f => f.Item2.position).ToList();
            (Vector3 at, Vector3 along) OnPath(Vector3 p)
            {
                var best = (at: coralPath[0], along: (coralPath[1] - coralPath[0]).normalized); var bd = float.MaxValue;
                for (var k = 0; k + 1 < coralPath.Count; k++)
                {
                    var ab = coralPath[k + 1] - coralPath[k];
                    if (ab.sqrMagnitude < 1e-8f) continue;
                    var q = coralPath[k] + ab * Mathf.Clamp01(Vector3.Dot(p - coralPath[k], ab) / ab.sqrMagnitude);
                    if ((q - p).sqrMagnitude < bd) { bd = (q - p).sqrMagnitude; best = (q, ab.normalized); }
                }
                return best;
            }
            var intakeSigns = intakeSpins.Select(sp => { var (at, along) = OnPath(sp.transform.position); return SpinSign(sp, at, along); }).ToList();
            var tubeSigns = tubeSpins.Select(sp => SpinSign(sp, pivot + tubeDir * Vector3.Dot(sp.transform.position - pivot, tubeDir), tubeDir)).ToList();
            var algaeSigns = algaeSpins.Select(sp => SpinSign(sp, algaeHold.position, -outN)).ToList();
            Debug.Log($"ANEMONE wheel spin: intake {string.Join(" ", intakeSpins.Select((sp, k) => $"{sp.name}:{intakeSigns[k]:+0;-0}"))} | " +
                      $"tube {string.Join(" ", tubeSigns.Select(x => x.ToString("+0;-0")))} | algae {string.Join(" ", algaeSigns.Select(x => x.ToString("+0;-0")))}");
            Debug.Log($"ANEMONE algae held {reach:F3} m off the wheel line (half gap {halfGap:F3})");

            // ---- colliders
            var noBounce = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(NoBounce);
            var cols = new GameObject("Colliders").transform;
            cols.SetParent(drivebase, false);
            foreach (var (c, s) in new[]
                     {
                         (new Vector3(0, 0.433f, 0.076f), new Vector3(0.924f, 0.057f, 0.114f)), // bumpers (CAD x, y, z)
                         (new Vector3(0, -0.433f, 0.076f), new Vector3(0.924f, 0.057f, 0.114f)),
                         (new Vector3(0.433f, 0, 0.076f), new Vector3(0.057f, 0.81f, 0.114f)),
                         (new Vector3(-0.433f, 0, 0.076f), new Vector3(0.057f, 0.81f, 0.114f)),
                         (new Vector3(0, 0, 0.015f), new Vector3(0.756f, 0.756f, 0.01f)),        // belly pan
                     })
            {
                var b = new GameObject("Box").AddComponent<BoxCollider>();
                b.transform.SetParent(cols, false);
                b.transform.position = P(c.x, c.y, c.z);
                b.size = new Vector3(s.x, s.z, s.y);
                b.sharedMaterial = noBounce;
            }

            // Structure: every sizeable part of the frame, elevator, carriage, arm (the coral holder) and intake meets
            // the field and other robots (boxes fitted to each part). The intake's side plates and bars stop coral like
            // the real ones (they are outboard of its way in); the rest of the head, the indexer, the carriage and the
            // arm let held pieces through (a coral let go beside the arm pivot must not be shoved out of an overlap).
            {
                var skip = new[] { "am-3", "am-4", "4IN", "Bumper", "bumper", "Screw", "Bolt", "Nut", "Washer", "Bearing", "Spacer",
                                   "Collar", "Rivet", "Belt", "Pulley", "pulley", "tooth", "Chain", "LED", "Wire", "Tread", "Wheel",
                                   "Shaft", "shaft", "Hex", "Spring", "Part 1.005" };
                var indexer = new Bounds();
                indexer.SetMinMax(Vector3.Min(P(-0.24f, -0.40f, 0.10f), P(0.24f, -0.12f, 0.50f)),
                                  Vector3.Max(P(-0.24f, -0.40f, 0.10f), P(0.24f, -0.12f, 0.50f)));
                var added = 0;
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true).ToList())
                {
                    if (r.GetComponent<Collider>() || skip.Any(k => r.name.Contains(k)) || r.transform.IsChildOf(dt.transform)) continue;
                    if (r.transform.IsChildOf(climber) || r.name.StartsWith("arm_tube") || r.name.StartsWith("bellmouth")) continue; // own colliders
                    if (r.GetComponentInParent<GenericAnimationJoint>()) continue;
                    if (r.name.StartsWith("180M-60")) continue; // hex shafts: capsules below
                    var wb2 = WorldBounds(r);
                    var big = Mathf.Max(wb2.size.x, Mathf.Max(wb2.size.y, wb2.size.z));
                    if (big < 0.12f) continue;
                    // the intake and indexer pass coral, and so do the carriage and stages round the tube: a coral let go
                    // with its tail beside the arm pivot must not be shoved out of an overlap (PlayMode: random misses)
                    var sidePlate = r.transform.IsChildOf(intakeGo.transform) && System.Text.RegularExpressions.Regex.IsMatch(r.name, @"^180M-(0[5-9]|1[01])\b") &&
                                    Mathf.Abs(root.transform.InverseTransformPoint(wb2.center).x) > 0.17f;
                    // the kicker meets coral; the head's rollers and inner plates let a caught coral be pulled in
                    // between them (its outer side plates still stop coral, see sidePlate)
                    var solidIntake = r.transform.IsChildOf(kicker);
                    var passCoral = !sidePlate && !solidIntake && (r.transform.IsChildOf(intakeGo.transform) || r.transform.IsChildOf(elevatorGo.transform) ||
                                                   (r.transform.IsChildOf(drivebase) && indexer.Contains(wb2.center)));
                    // intake parts: plates traced by many boxes (as 1690's intake), anything thicker one box along its own axes
                    var traced = r.transform.IsChildOf(intakeGo.transform) || (r.transform.IsChildOf(drivebase) && indexer.Contains(wb2.center));
                    var cs = traced ? PlateBoxes(r) : new List<Collider> { r.gameObject.AddComponent<BoxCollider>() };
                    foreach (var c in cs)
                    {
                    c.sharedMaterial = noBounce;
                    if (passCoral)
                    {
                        // the arm also holds algae in its wheels
                        // Robot stays excluded too: at override priority this collider's own mask beats its body's,
                        // and without Robot in it the robot's parts hit each other (the head jammed on the indexer floor)
                        // algae meets them all (a ball being let go passes the robot briefly: Anemone.PassRobotBriefly)
                        c.excludeLayers = LayerMask.GetMask("Coral", "Robot");
                        c.layerOverridePriority = 100;
                    }
                    }
                    added += cs.Count;
                }

                Debug.Log($"ANEMONE structure colliders added: {added}");

                // The intake's and indexer's hex shafts (above and between the compliant wheels): a capsule along each,
                // solid to coral, so a coral rides over them and never through them. The head's let the coral they have
                // caught through, as its rollers do.
                var shafts = 0;
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true).ToList())
                {
                    if (!(r.name.StartsWith("180M-60") || r.name.Contains("ThunderHex") || r.name.StartsWith(".500 Hex"))) continue;
                    var sb = WorldBounds(r);
                    if (!(r.transform.IsChildOf(intakeGo.transform) || (r.transform.IsChildOf(drivebase) && indexer.Contains(sb.center)))) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (!mf || !mf.sharedMesh) continue;
                    var ax = root.transform.right; // every intake and indexer shaft runs side to side
                    var pts = mf.sharedMesh.vertices.Select(v => r.transform.TransformPoint(v)).ToList();
                    var o = sb.center;
                    var lo = pts.Min(q => Vector3.Dot(q - o, ax));
                    var hi = pts.Max(q => Vector3.Dot(q - o, ax));
                    if (hi - lo < 0.04f) continue;
                    var still = r.GetComponentInParent<GenericAnimationJoint>() is { } spin ? spin.transform.parent : r.transform.parent;
                    var cap = Empty($"{r.name} Shaft", still, o + ax * (lo + hi) / 2, root.transform.rotation).gameObject.AddComponent<CapsuleCollider>();
                    cap.direction = 0;
                    cap.radius = Mathf.Max(0.005f, pts.Max(q => Vector3.ProjectOnPlane(q - o, ax).magnitude));
                    cap.height = Mathf.Max(hi - lo, 2 * cap.radius);
                    cap.sharedMaterial = noBounce;
                    if (r.transform.IsChildOf(head)) headRollers.Add(cap);
                    shafts++;
                }
                Debug.Log($"ANEMONE shaft capsules: {shafts}");

                // the head's rollers and the kicker roller: a capsule round each shaft's wheels (on a still sibling,
                // so the spinning visuals don't move a collider)
                // (and the uptake's wheel stacks in the indexer: the coral is squeezed between them, not through them)
                foreach (var shaft in intakeSpins)
                {
                    var ax = shaft.transform.right;
                    var pts = shaft.GetComponentsInChildren<MeshFilter>(true)
                        .SelectMany(f => f.sharedMesh.vertices.Select(v => f.transform.TransformPoint(v))).ToList();
                    if (pts.Count == 0) continue;
                    var o = shaft.transform.position;
                    var lo = pts.Min(q => Vector3.Dot(q - o, ax));
                    var hi = pts.Max(q => Vector3.Dot(q - o, ax));
                    var radius = pts.Max(q => Vector3.ProjectOnPlane(q - o, ax).magnitude);
                    var cap = Empty($"{shaft.name} Roller", shaft.transform.parent, o + ax * (lo + hi) / 2, shaft.transform.rotation)
                        .gameObject.AddComponent<CapsuleCollider>();
                    cap.direction = 0;
                    cap.radius = radius;
                    cap.height = Mathf.Max(hi - lo, 2 * radius);
                    cap.sharedMaterial = noBounce;
                    // the head's rollers meet coral (a standing one gets knocked over); the robot script lets the one they
                    // have caught through them
                    if (shaft.transform.IsChildOf(head)) headRollers.Add(cap);
                }
                // the algae wheels: what the ball rides on and is taken by (coral leaving the tube passes them)
                var algaeTouch = new List<BoxCollider>();
                foreach (var shaft in algaeSpins)
                {
                    var ax = shaft.transform.right;
                    var pts = shaft.GetComponentsInChildren<MeshFilter>(true)
                        .SelectMany(f => f.sharedMesh.vertices.Select(v => f.transform.TransformPoint(v))).ToList();
                    if (pts.Count == 0) continue;
                    var o = shaft.transform.position;
                    var lo = pts.Min(q => Vector3.Dot(q - o, ax));
                    var hi = pts.Max(q => Vector3.Dot(q - o, ax));
                    var cap = Empty($"{shaft.name} Wheel", shaft.transform.parent, o + ax * (lo + hi) / 2, shaft.transform.rotation)
                        .gameObject.AddComponent<CapsuleCollider>();
                    cap.direction = 0;
                    cap.radius = pts.Max(q => Vector3.ProjectOnPlane(q - o, ax).magnitude);
                    cap.height = Mathf.Max(hi - lo, 2 * cap.radius);
                    cap.sharedMaterial = noBounce;
                    cap.excludeLayers = LayerMask.GetMask("Coral", "Robot");
                    cap.layerOverridePriority = 100;
                    var touch = Empty($"{shaft.name} Touch", algaeIntake.transform, cap.transform.position, cap.transform.rotation)
                        .gameObject.AddComponent<BoxCollider>();
                    touch.isTrigger = true;
                    touch.size = new Vector3(cap.height, 2 * cap.radius + 0.03f, 2 * cap.radius + 0.03f);
                    algaeTouch.Add(touch);
                }
                {
                    var iso = new SerializedObject(algaeIntake);
                    Object.DestroyImmediate(algaeIntake.GetComponent<BoxCollider>());
                    SetArray(iso, "intakeCollider", algaeTouch.Cast<Object>().ToList());
                    iso.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            // The floor coral's way through the robot, swept against its solid parts with the intake deployed (as built
            // here) and the arm at the receive pose: a coral may squeeze between compliant wheels (no colliders) and
            // rollers (capsules), never through a plate, tube or frame part.
            {
                var armTurn = armSign * (sp["Ground Intake"].armAngle - TubeCadAngle);
                arm.RotateAround(arm.position, root.transform.right, armTurn);
                Physics.SyncTransforms();
                // Each leg turns the coral at the rate that ends the turn as it arrives (the framework moves it at a
                // constant speed and turns it at a constant rate: a fixed rate left turns running on into the next leg,
                // sweeping the coral through parts the path was cleared of). No turn: hold it (0.001, as 1690/694).
                for (var k = 1; k < floorStates.Length; k++)
                {
                    var (n, t, v, _) = floorStates[k];
                    var from = floorStates[k - 1].Item2;
                    var secs = (t.position - from.position).magnitude / (v * 0.0254f);
                    var deg = Quaternion.Angle(from.rotation, t.rotation);
                    floorStates[k] = (n, t, v, deg < 0.5f || secs <= 0 ? 0.001f : deg / secs * 1.05f);
                    Debug.Log($"ANEMONE leg {floorStates[k - 1].Item1} -> {n}: {(t.position - from.position).magnitude * 100:F1} cm, {deg:F0} deg, {floorStates[k].Item4:F0} deg/s");
                }
                var legs = floorStates.Select(f => (f.Item1, f.Item2)).Append(("Tube", coralHold)).ToList();
                for (var k = 0; k + 1 < legs.Count; k++)
                {
                    var hits = new Dictionary<string, int>();
                    var firstAt = new Dictionary<string, float>();
                    for (var i = 0; i <= 30; i++)
                    {
                        var t = i / 30f;
                        var pos = Vector3.Lerp(legs[k].Item2.position, legs[k + 1].Item2.position, t);
                        var rot = Quaternion.Slerp(legs[k].Item2.rotation, legs[k + 1].Item2.rotation, t);
                        var ax = rot * coralLong;
                        // as the planner: rigid parts at 45 mm, the compliant wheel stacks at 20 mm (they squash)
                        static bool Soft(Collider c) => c is CapsuleCollider && c.name.EndsWith(" Roller");
                        foreach (var c in Physics.OverlapCapsule(pos - ax * 0.102f, pos + ax * 0.102f, 0.045f, ~0, QueryTriggerInteraction.Ignore).Where(c => !Soft(c))
                                     .Concat(Physics.OverlapCapsule(pos - ax * 0.102f, pos + ax * 0.102f, 0.020f, ~0, QueryTriggerInteraction.Ignore).Where(Soft)))
                        {
                            if (!c.transform.IsChildOf(root.transform)) continue;
                            if (c.transform.IsChildOf(arm) && c.name != "Chute Wall") continue;
                            var n = c.GetComponentInParent<MeshRenderer>() ? c.GetComponentInParent<MeshRenderer>().name : c.name;
                            hits[n] = hits.TryGetValue(n, out var h) ? h + 1 : 1;
                            if (!firstAt.ContainsKey(n)) firstAt[n] = t;
                        }
                    }
                    Debug.Log($"ANEMONE path {legs[k].Item1} -> {legs[k + 1].Item1}: " +
                              (hits.Count == 0 ? "clear" : string.Join(", ", hits.OrderByDescending(h => h.Value).Select(h => $"{h.Key} x{h.Value} from t={firstAt[h.Key]:F2}"))));
                }
                arm.RotateAround(arm.position, root.transform.right, -armTurn);
                Physics.SyncTransforms();
            }

            // The head must not drag on the carpet: its colliders stop 2 cm above it when deployed.
            {
                var floor = root.transform.position.y + ground + 0.02f;
                foreach (var b in head.GetComponentsInChildren<BoxCollider>(true).Concat(kicker.GetComponentsInChildren<BoxCollider>(true)).Where(b => !b.isTrigger).ToList())
                {
                    var lo = Enumerable.Range(0, 8).Min(k => b.transform.TransformPoint(b.center + Vector3.Scale(b.size / 2,
                        new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1))).y);
                    if (lo < floor) Object.DestroyImmediate(b);
                }
            }

            var rootBox = root.GetComponent<BoxCollider>();
            rootBox.center = new Vector3(0, ground + 0.076f, 0);
            rootBox.size = new Vector3(0.924f, 0.114f, 0.924f);

            // ---- climber: a hook on the folding arm, contacts only (as 1690's side climber). Deployed (flat out of
            // the left side) the hook end sits ~13 cm past the bumper at 0.4 m; the arm then swings back up past
            // upright with the cage on the hook. The scoring zone sits in the hook.
            // The climber's two pry bars, fitted to the CAD (Part 3, Part 4): the lower plate just above the hinge and
            // the hook's top crossbar (with its down-turned lip) both stick out ~8 cm toward the front, parallel to the
            // hinge axis, ~15 cm apart. They bear on the cage the way 1690's and 3950's pairs of bars do. The cage
            // trigger sits in the gap between them.
            BoxCollider HookBox(string name, Vector3 cadMin, Vector3 cadMax)
            {
                var c = (cadMin + cadMax) / 2;
                var s = cadMax - cadMin;
                var bx = Empty(name, climber, Pv(c), root.transform.rotation).gameObject.AddComponent<BoxCollider>();
                bx.size = new Vector3(s.x, s.z, s.y);
                // the hook's faces grip the cage like the grabber's rubber wheels (with 0.6 the cage slid out as the arm turned)
                bx.sharedMaterial = AssetDatabase.LoadAssetAtPath<PhysicMaterial>("Assets/Materials/PhysicsMaterials/Grip.physicMaterial");
                return bx;
            }

            // Measured from CAD sections of Part 3: a 5 cm wide tube, a stem of the same width at its tip, and on the stem
            // a thin flange 10 cm wide. An upright sits beside the stem, between the arm's face and the flange's overhang.
            HookBox("Arm Bar", new(-0.3524f, -0.1206f, 0.4544f), new(-0.3016f, -0.0952f, 0.6576f));
            HookBox("Top Stem", new(-0.3524f, -0.0952f, 0.600f), new(-0.3016f, -0.0445f, 0.650f));
            // the flange itself is 3 mm; thickened behind its face so a contact can't tunnel through it
            HookBox("Top Flange", new(-0.3778f, -0.0500f, 0.594f), new(-0.2762f, -0.0413f, 0.6576f));
            // Part 4, the lower lock: a 6 mm plate (thickened to 16 mm) with a half-round notch on its inboard edge
            // (CAD y -0.095..-0.057, ~3.8 cm, a cage post's width) behind a hook tip, so the post drops in and stays
            HookBox("Lower Plate Back", new(-0.352f, -0.121f, 0.443f), new(-0.292f, -0.095f, 0.459f));
            // trimmed to the plate's outboard edge (as hand-fitted in the editor) so the post can drop into the notch
            HookBox("Lower Plate Spine", new(-0.3054f, -0.095f, 0.443f), new(-0.2944f, -0.057f, 0.459f));
            HookBox("Lower Plate Tip", new(-0.352f, -0.060f, 0.443f), new(-0.293f, -0.048f, 0.459f));
            // the whole pry-bar envelope (PlayMode: a trigger only in the gap misses the cage post while hanging)
            var grabber = Empty("Cage Trigger", climber, Pv(new Vector3(-0.327f, -0.08f, 0.555f)), root.transform.rotation)
                .gameObject.AddComponent<BoxCollider>();
            grabber.isTrigger = true;
            grabber.size = new Vector3(0.12f, 0.24f, 0.11f); // centred: OverlapBoxBounds ignores BoxCollider.center
            var climb = new SerializedObject(root.GetComponent<Games.Reefscape.Scoring.Scorers.ClimbScorer>());
            climb.FindProperty("cageScoringTrigger").objectReferenceValue = grabber;
            climb.FindProperty("autoClimbTriggers").arraySize = 0;
            climb.ApplyModifiedPropertiesWithoutUndo();

            var led = new SerializedObject(root.GetComponents<Component>().First(c => c.GetType().Name == "LedStripController"));
            led.FindProperty("leds").arraySize = 0;
            led.ApplyModifiedPropertiesWithoutUndo();

            // ---- the intake rests stowed, inside the frame (the CAD has it deployed). As the LinkagePoser tool does:
            // each link's children turn about its pivot and its joint object only moves, so every joint keeps the
            // identity rotation it measures from and reads 0 stowed. Everything on the head was placed deployed above.
            var intakeRest = sp["Stow"].intakeAngle;
            {
                var right = root.transform.right;
                void Swing(Transform link, Vector3 pivot, float deg)
                {
                    foreach (var k in Enumerable.Range(0, link.childCount).Select(link.GetChild).ToList())
                        k.RotateAround(pivot, right, deg);
                }

                var shaft = bar.position;
                var pinB = head.position;
                var pinT = Pv(HeadTopPin);
                var lenT = (pinT - topBar.position).magnitude;
                var lenC = (pinT - pinB).magnitude;
                var turn = intakeSign * intakeRest;
                var pinB2 = shaft + Quaternion.AngleAxis(turn, right) * (pinB - shaft);
                // the top bar's pin: on its circle about the frame pin and the head's about the new bottom pin, on the
                // same branch as the CAD pose (the one nearer the top pin turned with the bars)
                Vector2 Yz(Vector3 w) { var l = root.transform.InverseTransformPoint(w); return new Vector2(l.z, l.y); }
                Vector3 W(Vector2 v) => root.transform.TransformPoint(new Vector3(root.transform.InverseTransformPoint(pinT).x, v.y, v.x));
                var o = Yz(topBar.position);
                var c2 = Yz(pinB2);
                var dist = (c2 - o).magnitude;
                var along = (lenT * lenT - lenC * lenC + dist * dist) / (2 * dist);
                var h = Mathf.Sqrt(Mathf.Max(0, lenT * lenT - along * along));
                var foot = o + (c2 - o) * along / dist;
                var perp = new Vector2(-(c2 - o).y, (c2 - o).x) / dist;
                var guess = Yz(shaft + Quaternion.AngleAxis(turn, right) * (pinT - shaft));
                var t2 = (foot + perp * h - guess).sqrMagnitude < (foot - perp * h - guess).sqrMagnitude ? foot + perp * h : foot - perp * h;
                var pinT2 = W(t2);

                var kp0 = kicker.position;
                var cp0 = Pv(KickerControlPin);
                var topTurn = Vector3.SignedAngle(pinT - topBar.position, pinT2 - topBar.position, right);
                Swing(bar, shaft, turn);
                Swing(topBar, topBar.position, topTurn);
                var headTurn = Vector3.SignedAngle(pinT - pinB, pinT2 - pinB2, right);
                // the head rides on its bottom pin: everything on it goes along, then turns about the pin
                head.position = pinB2;
                Swing(head, pinB2, headTurn);
                headTopHinge.anchor = headTopHinge.transform.InverseTransformPoint(pinT2);
                barHeadHinge.anchor = bar.InverseTransformPoint(pinB2);
                // the kicker rides on the top bar's shaft and turns until its slot is back on the bottom bar's pin
                var kp1 = topBar.position + Quaternion.AngleAxis(topTurn, right) * (kp0 - topBar.position);
                var cp1 = shaft + Quaternion.AngleAxis(turn, right) * (cp0 - shaft);
                var kickTurn = Vector3.SignedAngle(cp0 - kp0, cp1 - kp1, right);
                kicker.position = kp1;
                Swing(kicker, kp1, kickTurn);
                // the slot: free along its length (radial from the pivot) and across the robot, held square to it in the
                // side plane, so the pin sets the angle without over-constraining the loop
                var slotDir = Quaternion.AngleAxis(kickTurn, right) * (cp0 - kp0).normalized;
                var slot = kicker.gameObject.AddComponent<ConfigurableJoint>();
                slot.connectedBody = barRb;
                slot.anchor = kicker.InverseTransformPoint(cp1);
                slot.axis = kicker.InverseTransformDirection(slotDir);
                slot.secondaryAxis = kicker.InverseTransformDirection(right);
                slot.autoConfigureConnectedAnchor = true;
                // only as far along as the slot runs (the pin moves ~12 mm in it from deployed to stowed): free along
                // its whole line, a knock at spawn swung the kicker right over with the pin on the pivot's far side
                var slotTravel = ((cp1 - kp1).magnitude - (cp0 - kp0).magnitude);
                slot.anchor = kicker.InverseTransformPoint(cp1 - slotDir * slotTravel / 2); // the slot's middle
                slot.autoConfigureConnectedAnchor = false;
                slot.connectedAnchor = bar.InverseTransformPoint(cp1);                     // the bar's pin
                slot.xMotion = ConfigurableJointMotion.Limited;
                slot.linearLimit = new SoftJointLimit { limit = Mathf.Abs(slotTravel) / 2 + 0.004f };
                slot.yMotion = ConfigurableJointMotion.Free;
                slot.zMotion = ConfigurableJointMotion.Locked;
                slot.angularXMotion = slot.angularYMotion = slot.angularZMotion = ConfigurableJointMotion.Free;
                Debug.Log($"ANEMONE kicker stowed: turned {kickTurn:F1} deg on the top bar's shaft, pin {((cp1 - kp1).magnitude - (cp0 - kp0).magnitude) * 1000:F1} mm along its slot");
                Debug.Log($"ANEMONE intake rests stowed: bars {turn:F1} deg, top bar to {(pinT2 - topBar.position).magnitude:F3} m pin, " +
                          $"head turned {headTurn:F1} deg, loop error {Mathf.Abs((pinT2 - pinB2).magnitude - lenC):F4} m");
            }

            // ---- robot script
            var so = new SerializedObject(robot);
            SetArray(so, "headRollerColliders", headRollers.Cast<Object>().ToList());
            SetArray(so, "chuteWalls", arm.GetComponentsInChildren<Collider>(true).Where(c => c.name == "Chute Wall").Cast<Object>().ToList());
            so.FindProperty("coralLongLocal").vector3Value = coralLong;
            so.FindProperty("elevator").objectReferenceValue = elev;
            so.FindProperty("arm").objectReferenceValue = armJ;
            so.FindProperty("intakeBar").objectReferenceValue = crankJ;
            so.FindProperty("climber").objectReferenceValue = climberJ;
            so.FindProperty("armCadAngle").floatValue = TubeCadAngle;
            so.FindProperty("armSign").floatValue = armSign;
            so.FindProperty("intakeSign").floatValue = intakeSign;
            so.FindProperty("intakeRestAngle").floatValue = intakeRest;
            // 180 is a floor-intake robot: spawn in ground mode (V toggles to the station)
            so.FindProperty("<CurrentCoralStationMode>k__BackingField").FindPropertyRelative("<DropType>k__BackingField").enumValueIndex =
                (int)Games.Reefscape.FieldScripts.DropType.Ground;
            so.FindProperty("climberSign").floatValue = climberSign;
            // 180 carries a coral and an algae at once: in algae mode the level keys pick the algae setpoints even
            // with a coral held (the framework's super cycler)
            so.FindProperty("superCycler").boolValue = true;
            // sounds: the rollers, the algae wheels' stall, a coral seating, the hook taking the cage, and the arm,
            // intake and climber motors (the elevator's and the drive's are set on their components)
            AudioClip Clip(string f) => AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Imports/Audio/" + f);
            so.FindProperty("rollerClip").objectReferenceValue = Clip("1986Rollers.wav");
            so.FindProperty("algaeStallClip").objectReferenceValue = Clip("1986Stall.wav");
            so.FindProperty("coralSeatClip").objectReferenceValue = Clip("1986EndEffectorDrop.wav");
            so.FindProperty("cageHookClip").objectReferenceValue = Clip("2910ClimbLatch.wav");
            foreach (var j in new[] { armJ, crankJ, climberJ })
            {
                var jso = new SerializedObject(j);
                jso.FindProperty("movementAudioClip").objectReferenceValue = Clip("2910armPivot.wav");
                jso.ApplyModifiedPropertiesWithoutUndo();
            }
            SetArray(so, "intakeRollerVisuals", intakeSpins.Cast<Object>().ToList());
            SetFloats(so, "intakeRollerSigns", intakeSigns);
            SetFloats(so, "tubeRollerSigns", tubeSigns);
            SetFloats(so, "algaeWheelSigns", algaeSigns);
            SetArray(so, "tubeRollerVisuals", tubeSpins.Cast<Object>().ToList());
            SetArray(so, "algaeWheelVisuals", algaeSpins.Cast<Object>().ToList());
            {
                var fs = so.FindProperty("floorCoralStates");
                fs.arraySize = floorStates.Length;
                for (var i = 0; i < floorStates.Length; i++)
                {
                    var s = fs.GetArrayElementAtIndex(i);
                    var (name, target, speed, angular) = floorStates[i];
                    s.FindPropertyRelative("name").stringValue = name;
                    s.FindPropertyRelative("stateNum").intValue = i + 1;
                    s.FindPropertyRelative("stateMoveSpeed").floatValue = speed;
                    s.FindPropertyRelative("stateTarget").objectReferenceValue = target;
                    s.FindPropertyRelative("smoothHandoff").boolValue = true;
                    s.FindPropertyRelative("angularSpeed").floatValue = angular;
                }
            }
            // "good enough" motion: Max is the cruise speed in rad/s (error in degrees)
            SetPid(so, "armPid", 0.35f, 0f, 0.01f, 7f);
            // a closed four-bar: at 7 rad/s the solver let the loop stretch and lock part way
            SetPid(so, "intakePid", 0.4f, 0f, 0f, 6f);
            SetPid(so, "climberPid", 1f, 0f, 0.00001f, 2f); // 1690's climber
            foreach (var (field, asset) in new[]
                     {
                         ("stow", "Stow"), ("groundIntake", "Ground Intake"), ("hpIntake", "HP Intake"), ("l1", "L1"),
                         ("l2", "L2"), ("l3", "L3"), ("l4", "L4"), ("groundAlgae", "Ground Algae"), ("lollipop", "Lollipop"),
                         ("lowAlgae", "Low Algae"), ("highAlgae", "High Algae"), ("processor", "Processor"), ("net", "Net"), ("netApproach", "Net Approach"),
                         ("climbPrep", "Climb Prep"), ("climbed", "Climbed")
                     })
                so.FindProperty(field).objectReferenceValue = sp[asset];
            so.FindProperty("groundCoralIntake").objectReferenceValue = groundIntake;
            so.FindProperty("hpCoralIntake").objectReferenceValue = hpIntake;
            so.FindProperty("algaeIntake").objectReferenceValue = algaeIntake;
            SetState(so, "coralState", "Coral Tube", floorStates.Length + 1, 40f, coralHold, false, 360f);
            SetState(so, "algaeState", "Algae Wheels", 1, 20f, algaeHold, false, 180f);
            so.FindProperty("coralEject").objectReferenceValue = coralEject;
            so.FindProperty("algaeEject").objectReferenceValue = algaeEject;

            var bumpers = BumperRenderers(root.transform).ToArray();
            var bp = so.FindProperty("bumperMeshes");
            bp.arraySize = bumpers.Length;
            for (var i = 0; i < bumpers.Length; i++) bp.GetArrayElementAtIndex(i).objectReferenceValue = bumpers[i];
            so.FindProperty("blueBumperMaterial").objectReferenceValue = Mat(Mats + "NavyBlue.mat");
            so.FindProperty("redBumperMaterial").objectReferenceValue = Mat(Mats + "Red.mat");
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log($"ANEMONE bumpers {bumpers.Length}: " + string.Join(", ", bumpers.Select(b => $"{b.name} ({b.sharedMaterials.Length} mats)")));

            ApplyPalette(root, dt.transform, bumpers, head, climber);

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = robotLayer;
            root.layer = robotLayer;

            PrefabUtility.UnloadPrefabContents(template);
            return root;
        }

        // the bumper fabric: the four big bumper_stp side meshes
        private static IEnumerable<MeshRenderer> BumperRenderers(Transform root) =>
            root.GetComponentsInChildren<MeshRenderer>(true).Where(r =>
            {
                if (!r.name.StartsWith("bumper_stp")) return false;
                var b = WorldBounds(r);
                return Mathf.Max(b.size.x, b.size.z) > 0.8f && Mathf.Min(b.size.x, b.size.z) > 0.05f;
            });

        // Unity's axis handling of a Blender FBX differs with the import settings: pick the axis-aligned turn of the
        // model root that puts every mechanism pivot where the CAD -> Unity mapping (B2U) says.
        private static void OrientModel(Transform model)
        {
            var nodes = model.GetComponentsInChildren<Transform>(true);
            var best = (err: float.MaxValue, rot: Quaternion.identity);
            for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
            for (var k = 0; k < 4; k++)
            {
                model.localRotation = Quaternion.Euler(90 * i, 90 * j, 90 * k);
                var err = 0f;
                foreach (var (n, cad) in Pivots)
                {
                    var t = nodes.First(x => x.name == n);
                    var want = model.parent.TransformPoint(model.localPosition + B2U(cad.x, cad.y, cad.z));
                    err += (t.position - want).magnitude;
                }

                if (err < best.err) best = (err, model.localRotation);
            }

            model.localRotation = best.rot;
            Debug.Log($"ANEMONE model root turned {best.rot.eulerAngles}, pivot error {best.err:F4} m");
            if (best.err > 0.01f) throw new System.Exception("ANEMONE no model orientation matches the CAD pivots");
        }

        // ------------------------------------------------------------------ materials (house palette)

        private const string Mats = "Assets/Materials/";

        private static readonly (string path, Color c)[] Palette =
        {
            (Mats + "Aluminum.mat", new Color(0.80f, 0.81f, 0.83f)), (Mats + "DarkSteel.mat", new Color(0.42f, 0.42f, 0.42f)),
            (Mats + "LightBlack.mat", new Color(0.18f, 0.18f, 0.18f)), (Mats + "Yellow.mat", new Color(1f, 0.78f, 0f)),
            (Mats + "Red.mat", new Color(1f, 0.10f, 0f)), (Mats + "DarkRed.mat", new Color(0.73f, 0.11f, 0f)),
            (Mats + "GreenFlexWheel.mat", new Color(0.29f, 0.90f, 0.22f)), (Mats + "DarkBlue.mat", new Color(0f, 0.11f, 0.73f)),
            (Mats + "Bronze.mat", new Color(1f, 0.66f, 0.28f)), (Mats + "Tan.mat", new Color(0.66f, 0.61f, 0.35f)),
            (Mats + "White Plastic.mat", new Color(1f, 1f, 1f)), (Mats + "Black.mat", new Color(0f, 0f, 0f)),
        };

        private static Material Mat(string path) => AssetDatabase.LoadAssetAtPath<Material>(path);

        private static Material AnodizedBlack(bool glossy)
        {
            var name = glossy ? "Anodized Aluminum - Glossy Black" : "Anodized Aluminum - Black";
            var dst = $"{Dir}/Materials/{name}.mat";
            CopyIfMissing($"Assets/Prefabs/Reefscape/Robots/Mods/China Modpack/5449/Materials/{name}.mat", dst);
            return Mat(dst);
        }

        private static (Color c, float clear) CadColour(Material m)
        {
            var parts = m.name.Replace(" (Instance)", "").Split('_');
            if (parts.Length == 5 && parts.All(x => float.TryParse(x, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _)))
            {
                var v = parts.Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                return (new Color(v[0], v[1], v[2]), v[4]);
            }

            var col = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.color;
            return (col, col.a < 0.99f ? 1 - col.a : 0);
        }

        // The real robot (FUN Behind the Bumpers, 2025): black anodized everywhere (frame, elevator, carriage, arm,
        // gussets, chain); clear polycarb intake plates, bars and indexer; black spiky wheels on the intake head and
        // green compliant wheels in the indexer; a bare aluminum climber; black algae wheels; alliance bumpers.
        private static readonly System.Text.RegularExpressions.Regex PolycarbParts =
            new(@"^180M-(0[6-9]|1[0-3]|15|17)\b");
        private static readonly string[] Hardware =
            { "Screw", "Bolt", "Nut", "Washer", "Rivet", "Shaft", "shaft", "Hex", "Bearing", "bearing", "Spacer", "Collar", "Pin" };
        private static readonly string[] Motors = { "Kraken", "Falcon", "Minion", "NEO", "X44", "X60", "Talon", "WCP-0940", "MAXPlanetary" };

        private static Material Paint(Material m, string part, bool onHead, bool onClimber)
        {
            if (!m) return m;
            if (onClimber || part == "Part 1") return Mat(Mats + "Aluminum.mat"); // the climber post and arm
            if (part.StartsWith("am-3945") || part.StartsWith("am-3462") || part.StartsWith("4IN Compliant"))
                return Mat(Mats + (onHead ? "BlackFlexWheel.mat" : "GreenFlexWheel.mat"));
            if (part.StartsWith("Part 1.00") && part != "Part 1.005") return Mat(Mats + "Black.mat"); // algae wheels
            if (part.Contains("Tread") || part.Contains("Wheel")) return Mat(Mats + "BlackFlexWheel.mat");
            if (PolycarbParts.IsMatch(part)) return Mat(Mats + "Polycarb.mat");
            var (c, clear) = CadColour(m);
            Color.RGBToHSV(c, out var hue, out var sat, out var val);
            if (clear > 0.05f) return Mat(val < 0.3f ? Mats + "SmokedPolycarb.mat" : Mats + "Polycarb.mat");
            if (Hardware.Any(part.Contains)) return Mat(val >= 0.55f ? Mats + "Aluminum.mat" : Mats + "DarkSteel.mat");
            if (Motors.Any(part.Contains))
                return sat < 0.15f && val >= 0.55f ? Mat(Mats + "Aluminum.mat") : AnodizedBlack(true);
            // electronics and wiring keep their own colours (CAN green, warning yellow ...); red and blue CAD
            // accents are black on the robot
            var redOrBlue = hue < 0.05f || hue > 0.93f || (hue > 0.55f && hue < 0.72f);
            if (sat > 0.5f && !redOrBlue)
                return Mat(Palette.OrderBy(p => Mathf.Pow(p.c.r - c.r, 2) + Mathf.Pow(p.c.g - c.g, 2) + Mathf.Pow(p.c.b - c.b, 2)).First().path);
            return AnodizedBlack(val < 0.12f);
        }

        private static void ApplyPalette(GameObject root, Transform skip, MeshRenderer[] bumpers, Transform head, Transform climber)
        {
            var count = new Dictionary<string, int>();
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.transform.IsChildOf(skip)) continue;
                var part = System.Text.RegularExpressions.Regex.Replace(r.name, @"(\.\d{3})+$", "");
                if (r.name.StartsWith("Part 1.00")) part = r.name; // the algae wheels and belly pan share the base name
                Material[] mats;
                if (bumpers.Contains(r))
                {
                    // slot 0 is swapped for the alliance colour; the numbers (any other slot) are white
                    mats = r.sharedMaterials.Select((m, i) => i == 0 ? m : Mat(Mats + "White Plastic.mat")).ToArray();
                }
                else
                    mats = r.sharedMaterials.Select(m => Paint(m, part, r.transform.IsChildOf(head), r.transform.IsChildOf(climber))).ToArray();
                r.sharedMaterials = mats;
                foreach (var m in mats.Where(m => m)) count[m.name] = count.TryGetValue(m.name, out var n) ? n + 1 : 1;
            }

            Debug.Log("ANEMONE palette " + string.Join(", ", count.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}")));
        }

        // ------------------------------------------------------------------ helpers

        private static void Square(Transform node, Transform root)
        {
            if (Quaternion.Angle(node.rotation, root.rotation) < 0.01f) return;
            var kids = Enumerable.Range(0, node.childCount).Select(node.GetChild).ToList();
            foreach (var k in kids) k.SetParent(null, true);
            node.rotation = root.rotation;
            foreach (var k in kids) k.SetParent(node, true);
        }

        private static GenericJoint MakeJoint(GameObject go, Rigidbody connected, char angular = ' ', bool linearY = false)
        {
            Body(go, 1f);
            var cj = go.AddComponent<ConfigurableJoint>();
            cj.connectedBody = connected;
            cj.anchor = Vector3.zero;
            cj.axis = Vector3.right;
            cj.secondaryAxis = Vector3.up;
            cj.autoConfigureConnectedAnchor = true;
            cj.xMotion = ConfigurableJointMotion.Locked;
            cj.yMotion = linearY ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            cj.zMotion = ConfigurableJointMotion.Locked;
            cj.angularXMotion = angular == 'x' ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            cj.angularYMotion = angular == 'y' ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            cj.angularZMotion = angular == 'z' ? ConfigurableJointMotion.Free : ConfigurableJointMotion.Locked;
            return go.AddComponent<GenericJoint>();
        }

        // Joint bodies keep Unity's automatic inertia tensor; only the climber's is fixed (FixedTensor), as on 1690's.
        private static Rigidbody Body(GameObject go, float mass)
        {
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.drag = 0;
            rb.angularDrag = 0.05f;
            rb.useGravity = true;
            rb.excludeLayers = LayerMask.GetMask("Robot");
            return rb;
        }

        private static void FixedTensor(Rigidbody rb)
        {
            rb.automaticInertiaTensor = false;
            rb.inertiaTensor = Vector3.one;
            rb.inertiaTensorRotation = Quaternion.identity;
        }

        // A passive pin: free about the robot's X axis through `anchor` (local to the body).
        private static HingeJoint Hinge(GameObject go, Rigidbody connected, Vector3 anchor)
        {
            var hj = go.AddComponent<HingeJoint>();
            hj.connectedBody = connected;
            hj.anchor = anchor;
            hj.axis = Vector3.right;
            hj.autoConfigureConnectedAnchor = true;
            return hj;
        }

        // A pin that only keeps two bodies' points together in the robot's YZ plane (free along and about its axis,
        // free to twist): closes a planar linkage loop without redundant constraints. No drive force, so the joint
        // stabilizer's damping does nothing on it.
        private static ConfigurableJoint PlanarPin(GameObject go, Rigidbody connected, Vector3 anchor)
        {
            var cj = go.AddComponent<ConfigurableJoint>();
            cj.connectedBody = connected;
            cj.anchor = anchor;
            cj.axis = Vector3.right;
            cj.secondaryAxis = Vector3.up;
            cj.autoConfigureConnectedAnchor = true;
            cj.xMotion = ConfigurableJointMotion.Free;
            cj.yMotion = ConfigurableJointMotion.Locked;
            cj.zMotion = ConfigurableJointMotion.Locked;
            cj.angularXMotion = ConfigurableJointMotion.Free;
            cj.angularYMotion = ConfigurableJointMotion.Free;
            cj.angularZMotion = ConfigurableJointMotion.Free;
            var none = new JointDrive { positionSpring = 0, positionDamper = 0, maximumForce = 0 };
            cj.xDrive = cj.yDrive = cj.zDrive = cj.angularXDrive = cj.angularYZDrive = none;
            return cj;
        }

        // Wheels that share a shaft spin together: each wheel's axle is the least-spread direction of its vertices
        // (or `axis` when given), and wheels whose axles lie on one line make one VelocityRoller (it turns local X).
        private static List<GenericAnimationJoint> SpinShafts(Transform search, string label, System.Func<string, bool> part, Vector3? axis)
        {
            var wheels = search.GetComponentsInChildren<MeshRenderer>(true).Where(r => part(r.name) && !r.GetComponentInParent<GenericAnimationJoint>())
                .Select(r => (r, c: WorldBounds(r).center, a: axis ?? Axle(r))).ToList();
            var groups = new List<List<(MeshRenderer r, Vector3 c, Vector3 a)>>();
            foreach (var w in wheels)
            {
                var g = groups.FirstOrDefault(g => Mathf.Abs(Vector3.Dot(g[0].a, w.a)) > 0.97f &&
                                                   Vector3.ProjectOnPlane(w.c - g[0].c, g[0].a).magnitude < 0.012f);
                if (g != null) g.Add(w);
                else groups.Add(new List<(MeshRenderer, Vector3, Vector3)> { w });
            }

            var spins = new List<GenericAnimationJoint>();
            for (var i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var c = g.Aggregate(Vector3.zero, (s, w) => s + w.c) / g.Count;
                var parent = g[0].r.transform.parent;
                var go = new GameObject($"{label} Shaft {i + 1}");
                go.transform.SetParent(parent, false);
                go.transform.SetPositionAndRotation(c, Quaternion.FromToRotation(Vector3.right, g[0].a));
                foreach (var w in g) w.r.transform.SetParent(go.transform, true);
                spins.Add(go.AddComponent<GenericAnimationJoint>());
            }

            return spins;
        }

        // the axle of a wheel mesh: the eigenvector of its vertex spread with the smallest eigenvalue (Jacobi)
        // A plate traced as 1690's intake plates are: in the plate's own plane its triangles are rasterised on a 12 mm
        // grid and the filled cells merged into rectangles, each a thin box (curves, cut-outs and holes follow the
        // mesh). A part that isn't a thin plate gets one box along its principal axes.
        private static List<Collider> PlateBoxes(MeshRenderer r)
        {
            const float Cell = 0.012f;
            Mesh mesh = null;
            Vector3[] v = null;
            try { mesh = r.GetComponent<MeshFilter>().sharedMesh; v = mesh.vertices; } catch { }
            if (v == null || v.Length < 4) return new List<Collider> { r.gameObject.AddComponent<BoxCollider>() };
            var w = v.Select(p => r.transform.TransformPoint(p)).ToArray();
            var axes = PrincipalAxes(w);
            {
                // a round part (a shaft, a round tube, a roller): one capsule along it
                var m = w.Aggregate(Vector3.zero, (acc, p) => acc + p) / w.Length;
                var along = axes[2];
                var rad = w.Select(p => Vector3.ProjectOnPlane(p - m, along).magnitude).OrderBy(x => x).ToArray();
                var len = w.Max(p => Vector3.Dot(p - m, along)) - w.Min(p => Vector3.Dot(p - m, along));
                var rMax = rad[rad.Length - 1];
                var rLow = rad[(int)(rad.Length * 0.1f)];
                if (len > 3 * 2 * rMax && rMax > 0.004f && (rMax - rLow) / rMax < 0.2f)
                {
                    var mid = m + along * (w.Max(p => Vector3.Dot(p - m, along)) + w.Min(p => Vector3.Dot(p - m, along))) / 2;
                    var go = new GameObject("Capsule");
                    go.transform.SetParent(r.transform, false);
                    go.transform.SetPositionAndRotation(mid, Quaternion.LookRotation(along, axes[1]));
                    go.transform.localScale = new Vector3(1 / r.transform.lossyScale.x, 1 / r.transform.lossyScale.y, 1 / r.transform.lossyScale.z);
                    var cap = go.AddComponent<CapsuleCollider>();
                    cap.direction = 2;
                    cap.radius = rMax;
                    cap.height = Mathf.Max(len, 2 * rMax);
                    return new List<Collider> { cap };
                }
            }
            var n = axes[0]; // thickness
            var a = axes[2]; var b = axes[1];
            float Dot(Vector3 p, Vector3 d) => Vector3.Dot(p, d);
            var thick = w.Max(p => Dot(p, n)) - w.Min(p => Dot(p, n));
            var la = w.Max(p => Dot(p, a)) - w.Min(p => Dot(p, a));
            var lb = w.Max(p => Dot(p, b)) - w.Min(p => Dot(p, b));
            if (thick > 0.013f || lb < 0.03f) return VoxelBoxes(r, w, mesh.triangles, axes).Cast<Collider>().ToList();

            float a0 = w.Min(p => Dot(p, a)), b0 = w.Min(p => Dot(p, b)), nMid = (w.Max(p => Dot(p, n)) + w.Min(p => Dot(p, n))) / 2;
            int na = Mathf.CeilToInt(la / Cell) + 1, nb = Mathf.CeilToInt(lb / Cell) + 1;
            var fill = new bool[na, nb];
            var tri = mesh.triangles;
            for (var t = 0; t < tri.Length; t += 3)
            {
                var p = new Vector2[3];
                for (var k = 0; k < 3; k++) p[k] = new Vector2(Dot(w[tri[t + k]], a) - a0, Dot(w[tri[t + k]], b) - b0) / Cell;
                int i0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p[0].x, Mathf.Min(p[1].x, p[2].x)))), i1 = Mathf.Min(na - 1, Mathf.CeilToInt(Mathf.Max(p[0].x, Mathf.Max(p[1].x, p[2].x))));
                int j0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(p[0].y, Mathf.Min(p[1].y, p[2].y)))), j1 = Mathf.Min(nb - 1, Mathf.CeilToInt(Mathf.Max(p[0].y, Mathf.Max(p[1].y, p[2].y))));
                float Cross(Vector2 o, Vector2 x, Vector2 y) => (x.x - o.x) * (y.y - o.y) - (x.y - o.y) * (y.x - o.x);
                for (var i = i0; i <= i1; i++)
                for (var j = j0; j <= j1; j++)
                {
                    var c = new Vector2(i + 0.5f, j + 0.5f);
                    float d1 = Cross(p[0], p[1], c), d2 = Cross(p[1], p[2], c), d3 = Cross(p[2], p[0], c);
                    if (!((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0))) fill[i, j] = true;
                }
            }
            // greedy rectangles: grow along a, then along b while the whole row is filled
            var used = new bool[na, nb];
            var boxes = new List<BoxCollider>();
            var rot = Quaternion.LookRotation(a, b);
            for (var j = 0; j < nb; j++)
            for (var i = 0; i < na; i++)
            {
                if (!fill[i, j] || used[i, j]) continue;
                var i2 = i;
                while (i2 + 1 < na && fill[i2 + 1, j] && !used[i2 + 1, j]) i2++;
                var j2 = j;
                while (j2 + 1 < nb && Enumerable.Range(i, i2 - i + 1).All(x => fill[x, j2 + 1] && !used[x, j2 + 1])) j2++;
                for (var x = i; x <= i2; x++)
                for (var y = j; y <= j2; y++) used[x, y] = true;
                var ca = a0 + (i + i2 + 1) * Cell / 2;
                var cb = b0 + (j + j2 + 1) * Cell / 2;
                var go = new GameObject("Plate Box");
                go.transform.SetParent(r.transform, false);
                go.transform.SetPositionAndRotation(a * ca + b * cb + n * nMid, rot);
                go.transform.localScale = new Vector3(1 / r.transform.lossyScale.x, 1 / r.transform.lossyScale.y, 1 / r.transform.lossyScale.z);
                var box = go.AddComponent<BoxCollider>();
                // local: x = right (= -n or n), y = b, z = a
                box.size = new Vector3(Mathf.Max(thick, 0.004f), (j2 - j + 1) * Cell, (i2 - i + 1) * Cell);
                boxes.Add(box);
            }
            return boxes.Count > 0 ? boxes.Cast<Collider>().ToList() : new List<Collider> { ObbBox(r) };
        }

        // A part that isn't one flat plate (a bent sheet, a bracket, a block): its surface voxelised on an 8 mm grid along
        // its principal axes and the filled voxels merged into boxes, so a bent sheet becomes a bent shell of boxes
        // rather than one box filling the space it wraps.
        private static List<BoxCollider> VoxelBoxes(MeshRenderer r, Vector3[] w, int[] tri, Vector3[] axes)
        {
            const float Cell = 0.008f;
            var rot = Quaternion.LookRotation(axes[2], axes[1]);
            Vector3 X = rot * Vector3.right, Y = rot * Vector3.up, Z = rot * Vector3.forward;
            Vector3 L(Vector3 p) => new Vector3(Vector3.Dot(p, X), Vector3.Dot(p, Y), Vector3.Dot(p, Z));
            var lw = w.Select(L).ToArray();
            var lo = lw.Aggregate(Vector3.Min) - Vector3.one * (Cell / 2);
            var hi = lw.Aggregate(Vector3.Max) + Vector3.one * (Cell / 2);
            var n = Vector3Int.CeilToInt((hi - lo) / Cell);
            n = Vector3Int.Max(n, Vector3Int.one);
            if ((long)n.x * n.y * n.z > 400000) return new List<BoxCollider> { ObbBox(r) };
            var fill = new bool[n.x, n.y, n.z];
            void Mark(Vector3 p)
            {
                var c = Vector3Int.FloorToInt((p - lo) / Cell);
                if (c.x >= 0 && c.y >= 0 && c.z >= 0 && c.x < n.x && c.y < n.y && c.z < n.z) fill[c.x, c.y, c.z] = true;
            }
            for (var t = 0; t < tri.Length; t += 3)
            {
                Vector3 a = lw[tri[t]], b = lw[tri[t + 1]], c = lw[tri[t + 2]];
                var steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max((b - a).magnitude, (c - a).magnitude) / (Cell * 0.5f)), 1, 200);
                for (var i = 0; i <= steps; i++)
                for (var j = 0; j <= steps - i; j++)
                    Mark(a + (b - a) * (i / (float)steps) + (c - a) * (j / (float)steps));
            }
            var used = new bool[n.x, n.y, n.z];
            var boxes = new List<BoxCollider>();
            for (var z = 0; z < n.z; z++)
            for (var y = 0; y < n.y; y++)
            for (var x = 0; x < n.x; x++)
            {
                if (!fill[x, y, z] || used[x, y, z]) continue;
                var x2 = x;
                while (x2 + 1 < n.x && fill[x2 + 1, y, z] && !used[x2 + 1, y, z]) x2++;
                var y2 = y;
                bool Row(int yy, int zz) { for (var xx = x; xx <= x2; xx++) if (!fill[xx, yy, zz] || used[xx, yy, zz]) return false; return true; }
                while (y2 + 1 < n.y && Row(y2 + 1, z)) y2++;
                var z2 = z;
                bool Slab(int zz) { for (var yy = y; yy <= y2; yy++) if (!Row(yy, zz)) return false; return true; }
                while (z2 + 1 < n.z && Slab(z2 + 1)) z2++;
                for (var xx = x; xx <= x2; xx++)
                for (var yy = y; yy <= y2; yy++)
                for (var zz = z; zz <= z2; zz++) used[xx, yy, zz] = true;
                var cmid = lo + new Vector3(x + x2 + 1, y + y2 + 1, z + z2 + 1) * (Cell / 2);
                var go = new GameObject("Voxel Box");
                go.transform.SetParent(r.transform, false);
                go.transform.SetPositionAndRotation(X * cmid.x + Y * cmid.y + Z * cmid.z, rot);
                go.transform.localScale = new Vector3(1 / r.transform.lossyScale.x, 1 / r.transform.lossyScale.y, 1 / r.transform.lossyScale.z);
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(x2 - x + 1, y2 - y + 1, z2 - z + 1) * Cell;
                boxes.Add(box);
            }
            return boxes.Count > 0 ? boxes : new List<BoxCollider> { ObbBox(r) };
        }

        // A BoxCollider (on a child) fitted to a part along its principal axes, so a slanted plate gets a thin box.
        private static BoxCollider ObbBox(MeshRenderer r)
        {
            Vector3[] v;
            try { v = r.GetComponent<MeshFilter>().sharedMesh.vertices; } catch { v = null; }
            if (v == null || v.Length < 4) return r.gameObject.AddComponent<BoxCollider>();
            var w = v.Select(p => r.transform.TransformPoint(p)).ToArray();
            var axes = PrincipalAxes(w);
            var rot = Quaternion.LookRotation(axes[2], axes[1]);
            Vector3 X = rot * Vector3.right, Y = rot * Vector3.up, Z = rot * Vector3.forward;
            Vector3 lo = new(w.Min(p => Vector3.Dot(p, X)), w.Min(p => Vector3.Dot(p, Y)), w.Min(p => Vector3.Dot(p, Z)));
            Vector3 hi = new(w.Max(p => Vector3.Dot(p, X)), w.Max(p => Vector3.Dot(p, Y)), w.Max(p => Vector3.Dot(p, Z)));
            var mid = (lo + hi) / 2;
            var go = new GameObject("Box");
            go.transform.SetParent(r.transform, false);
            go.transform.SetPositionAndRotation(X * mid.x + Y * mid.y + Z * mid.z, rot);
            go.transform.localScale = new Vector3(1 / r.transform.lossyScale.x, 1 / r.transform.lossyScale.y, 1 / r.transform.lossyScale.z);
            var box = go.AddComponent<BoxCollider>();
            box.size = Vector3.Max(hi - lo, Vector3.one * 0.003f);
            return box;
        }

        // Unit principal axes of a point cloud, least spread first (Jacobi on the covariance).
        private static Vector3[] PrincipalAxes(Vector3[] w)
        {
            var m = w.Aggregate(Vector3.zero, (a, p) => a + p) / w.Length;
            var a3 = new double[3, 3];
            foreach (var p in w)
            {
                var d = p - m;
                for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                    a3[i, j] += d[i] * d[j];
            }
            var vec = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (var sweep = 0; sweep < 30; sweep++)
            for (var p = 0; p < 2; p++)
            for (var q = p + 1; q < 3; q++)
            {
                if (System.Math.Abs(a3[p, q]) < 1e-12) continue;
                var theta = 0.5 * System.Math.Atan2(2 * a3[p, q], a3[q, q] - a3[p, p]);
                double c = System.Math.Cos(theta), s = System.Math.Sin(theta);
                for (var k = 0; k < 3; k++) { double akp = a3[k, p], akq = a3[k, q]; a3[k, p] = c * akp - s * akq; a3[k, q] = s * akp + c * akq; }
                for (var k = 0; k < 3; k++) { double apk = a3[p, k], aqk = a3[q, k]; a3[p, k] = c * apk - s * aqk; a3[q, k] = s * apk + c * aqk; }
                for (var k = 0; k < 3; k++) { double vkp = vec[k, p], vkq = vec[k, q]; vec[k, p] = c * vkp - s * vkq; vec[k, q] = s * vkp + c * vkq; }
            }
            var order = Enumerable.Range(0, 3).OrderBy(i => a3[i, i]).ToArray();
            var e = order.Select(i => new Vector3((float)vec[0, i], (float)vec[1, i], (float)vec[2, i]).normalized).ToArray();
            e[1] = Vector3.Cross(e[2], e[0]).normalized; // right-handed and exactly orthogonal
            e[0] = Vector3.Cross(e[1], e[2]).normalized;
            return e;
        }

        private static Vector3 Axle(MeshRenderer r)
        {
            var mesh = r.GetComponent<MeshFilter>().sharedMesh;
            Vector3[] v;
            try { v = mesh.vertices; } catch { v = null; }
            if (v == null || v.Length < 6)
            {
                var s = mesh.bounds.size;
                var l = s.x <= s.y && s.x <= s.z ? Vector3.right : s.y <= s.z ? Vector3.up : Vector3.forward;
                return r.transform.TransformDirection(l).normalized;
            }

            var w = v.Select(p => r.transform.TransformPoint(p)).ToArray();
            var m = w.Aggregate(Vector3.zero, (a, p) => a + p) / w.Length;
            var a3 = new double[3, 3];
            foreach (var p in w)
            {
                var d = p - m;
                for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                    a3[i, j] += d[i] * d[j];
            }

            var vec = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (var sweep = 0; sweep < 30; sweep++)
            for (var p = 0; p < 2; p++)
            for (var q = p + 1; q < 3; q++)
            {
                if (System.Math.Abs(a3[p, q]) < 1e-12) continue;
                var theta = 0.5 * System.Math.Atan2(2 * a3[p, q], a3[q, q] - a3[p, p]);
                double c = System.Math.Cos(theta), s = System.Math.Sin(theta);
                for (var k = 0; k < 3; k++)
                {
                    double akp = a3[k, p], akq = a3[k, q];
                    a3[k, p] = c * akp - s * akq;
                    a3[k, q] = s * akp + c * akq;
                }
                for (var k = 0; k < 3; k++)
                {
                    double apk = a3[p, k], aqk = a3[q, k];
                    a3[p, k] = c * apk - s * aqk;
                    a3[q, k] = s * apk + c * aqk;
                }
                for (var k = 0; k < 3; k++)
                {
                    double vkp = vec[k, p], vkq = vec[k, q];
                    vec[k, p] = c * vkp - s * vkq;
                    vec[k, q] = s * vkp + c * vkq;
                }
            }

            var min = Enumerable.Range(0, 3).OrderBy(i => a3[i, i]).First();
            return new Vector3((float)vec[0, min], (float)vec[1, min], (float)vec[2, min]).normalized;
        }

        private static void SetFloats(SerializedObject so, string field, List<float> items)
        {
            var a = so.FindProperty(field);
            a.arraySize = items.Count;
            for (var i = 0; i < items.Count; i++) a.GetArrayElementAtIndex(i).floatValue = items[i];
        }

        private static void SetArray(SerializedObject so, string field, List<Object> items)
        {
            var a = so.FindProperty(field);
            a.arraySize = items.Count;
            for (var i = 0; i < items.Count; i++) a.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
        }

        // Renderer.bounds is stale in an editor script right after re-parenting: take the mesh's own bounds instead.
        private static Bounds WorldBounds(MeshRenderer r)
        {
            var mb = r.GetComponent<MeshFilter>().sharedMesh.bounds;
            var m = r.transform.localToWorldMatrix;
            var b = new Bounds(m.MultiplyPoint3x4(mb.center), Vector3.zero);
            for (var i = 0; i < 8; i++)
                b.Encapsulate(m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return b;
        }

        // The chute, as 6941's dye rotor walls: many thin boxes on the real surface, which meet the pieces. Rays cast
        // out from the tube axis against the chute meshes (every 1 cm along it, every 15 deg round it) find the inner
        // wall; each ray direction's hits become plates, straight while the wall is (the bellmouth flare bends them).
        // The bore's centre is fitted first (circles through the hits) and returned: held coral sits on it.
        private static Vector3 ChuteWalls(Transform arm, Vector3 pivot, Vector3 dir, Vector3 side,
                                          List<MeshFilter> meshes, PhysicMaterial wallMat, float uMin, float uMax)
        {
            const int Rays = 24;
            const float Du = 0.01f;
            var tris = new List<Vector3>();
            foreach (var mf in meshes)
            {
                var v = mf.sharedMesh.vertices;
                var m = mf.transform.localToWorldMatrix;
                tris.AddRange(mf.sharedMesh.triangles.Select(i => m.MultiplyPoint3x4(v[i])));
            }
            if (tris.Count == 0) throw new System.Exception("ANEMONE chute: no mesh data");
            var up = Vector3.Cross(dir, side).normalized;
            Vector3 Radial(int k) => side * Mathf.Cos(k * 2 * Mathf.PI / Rays) + up * Mathf.Sin(k * 2 * Mathf.PI / Rays);

            // distances to every face a ray crosses, nearest first (Moller-Trumbore)
            List<float> Hits(Vector3 o, Vector3 d)
            {
                var hits = new List<float>();
                for (var i = 0; i < tris.Count; i += 3)
                {
                    Vector3 e1 = tris[i + 1] - tris[i], e2 = tris[i + 2] - tris[i], p = Vector3.Cross(d, e2);
                    var det = Vector3.Dot(e1, p);
                    if (Mathf.Abs(det) < 1e-9f) continue;
                    var tv = o - tris[i];
                    var a = Vector3.Dot(tv, p) / det;
                    if (a < 0 || a > 1) continue;
                    var q = Vector3.Cross(tv, e1);
                    var b = Vector3.Dot(d, q) / det;
                    if (b < 0 || a + b > 1) continue;
                    var t = Vector3.Dot(e2, q) / det;
                    if (t > 0.03f && t < 0.15f) hits.Add(t);
                }
                hits.Sort();
                return hits;
            }

            var steps = Mathf.RoundToInt((uMax - uMin) / Du);
            // 1: fit the bore's centre (a circle through each well-closed slice's wall hits)
            var offs = new List<Vector2>();
            for (var s = 0; s <= steps; s++)
            {
                var o = pivot + dir * (uMin + s * Du);
                var pts = new List<Vector2>();
                for (var k = 0; k < Rays; k++)
                {
                    var h = Hits(o, Radial(k));
                    if (h.Count > 0) pts.Add(new Vector2(Mathf.Cos(k * 2 * Mathf.PI / Rays), Mathf.Sin(k * 2 * Mathf.PI / Rays)) * h[0]);
                }
                if (pts.Count < Rays * 3 / 4) continue;
                // algebraic circle fit: x^2 + y^2 + D x + E y + F = 0
                double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxz = 0, syz = 0, sz = 0;
                foreach (var p in pts)
                {
                    double x = p.x, y = p.y, z = x * x + y * y;
                    sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y; sxz += x * z; syz += y * z; sz += z;
                }
                var mtx = new Matrix4x4(new Vector4((float)sxx, (float)sxy, (float)sx, 0), new Vector4((float)sxy, (float)syy, (float)sy, 0),
                                        new Vector4((float)sx, (float)sy, pts.Count, 0), new Vector4(0, 0, 0, 1));
                var sol = mtx.inverse.MultiplyVector(new Vector3((float)-sxz, (float)-syz, (float)-sz));
                var c = new Vector2(-sol.x / 2, -sol.y / 2);
                var rad = Mathf.Sqrt(c.sqrMagnitude - sol.z);
                if (rad > 0.055f && rad < 0.07f) offs.Add(c);
            }
            if (offs.Count == 0) throw new System.Exception("ANEMONE chute: bore not found");
            var off = offs.Aggregate(Vector2.zero, (a, b) => a + b) / offs.Count;
            var axis = pivot + side * off.x + up * off.y;

            // 2: the walls, from the bore's centre
            var holder = Empty("Chute Walls", arm, axis, Quaternion.LookRotation(dir, up));
            var plates = 0;
            var rIn = new List<float>();
            for (var k = 0; k < Rays; k++)
            {
                var radial = Radial(k);
                var set = Empty($"Set {k * 360 / Rays}", holder, axis, holder.rotation);
                var run = new List<(float u, float r, float t)>();
                void Flush()
                {
                    if (run.Count == 0) return;
                    var a = run[0];
                    var b = run[run.Count - 1];
                    var pa = axis + dir * a.u + radial * a.r;
                    var pb = axis + dir * b.u + radial * b.r;
                    var along = run.Count > 1 ? (pb - pa).normalized : dir;
                    pa -= along * Du / 2;
                    pb += along * Du / 2;
                    var nrm = (radial - Vector3.Project(radial, along)).normalized;
                    var thick = Mathf.Max(0.006f, run.Max(x => x.t));
                    var rMid = (a.r + b.r) / 2;
                    var box = Empty("Chute Wall", set, (pa + pb) / 2 + nrm * thick / 2, Quaternion.LookRotation(along, nrm))
                        .gameObject.AddComponent<BoxCollider>();
                    box.size = new Vector3(2 * rMid * Mathf.Tan(Mathf.PI / Rays) * 1.06f, thick, (pb - pa).magnitude);
                    box.sharedMaterial = wallMat;
                    // Robot stays out, see the structure colliders
                    box.excludeLayers = LayerMask.GetMask("Robot");
                    box.layerOverridePriority = 100;
                    plates++;
                    run.Clear();
                }
                for (var s = 0; s <= steps; s++)
                {
                    var u = uMin + s * Du;
                    var h = Hits(axis + dir * u, radial);
                    if (h.Count == 0) { Flush(); continue; }
                    // never inside a coral (r 57 mm) plus 2 mm: the mesh pinches to 55.7 mm at a few ridges, and a
                    // loose coral wedged there (PlayMode: a station drop jammed vibrating in the tube)
                    var t = h.Count > 1 && h[1] - h[0] < 0.012f ? h[1] - h[0] : 0.004f;
                    var r = Mathf.Max(h[0], 0.059f);
                    if (run.Count > 0)
                    {
                        // straight while every sample stays within 1.5 mm of the chord from the run's start
                        var a = run[0];
                        var bent = Mathf.Abs(r - run[run.Count - 1].r) > 0.004f ||
                                   run.Any(x => Mathf.Abs(x.r - Mathf.Lerp(a.r, r, (x.u - a.u) / (u - a.u))) > 0.0015f);
                        if (bent)
                        {
                            var last = run[run.Count - 1];
                            Flush();
                            if (Mathf.Abs(r - last.r) <= 0.004f) run.Add(last); // a bend: the next plate starts where this one ends
                        }
                    }
                    run.Add((u, r, t));
                    rIn.Add(r);
                }
                Flush();
                if (set.childCount == 0) Object.DestroyImmediate(set.gameObject);
            }

            Debug.Log($"ANEMONE chute: bore {off.x * 1000:F1}/{off.y * 1000:F1} mm off the PCA axis ({offs.Count} slices), " +
                      $"{plates} wall plates, inner radius {rIn.Min() * 1000:F1}..{rIn.Max() * 1000:F1} mm");
            return axis;
        }

        private static Transform Empty(string name, Transform parent, Vector3 pos, Quaternion rot)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.SetPositionAndRotation(pos, rot);
            return t;
        }

        private static ReefscapeGamePieceIntake MakeIntake(string name, Transform parent, Vector3 pos, Vector3 size,
            Transform target, bool coral, float maxDistance = -1f, bool? smooth = null, float speed = -1f, float accuracy = -1f)
        {
            var t = Empty(name, parent, pos, parent.root.rotation);
            var box = t.gameObject.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.size = size;
            var intake = t.gameObject.AddComponent<ReefscapeGamePieceIntake>();
            var so = new SerializedObject(intake);
            var c = so.FindProperty("intakeCollider");
            c.arraySize = 1;
            c.GetArrayElementAtIndex(0).objectReferenceValue = box;
            so.FindProperty("target").objectReferenceValue = target;
            // Values from the official 2910 robot's coral and algae intakes.
            so.FindProperty("intakeSpeed").floatValue = speed > 0 ? speed : coral ? 84.6f : 39.4f;
            so.FindProperty("intakeForce").floatValue = coral ? 90.5f : 196.9f;
            so.FindProperty("usePlanarTolerancing").boolValue = false; // distance mode: the targets turn with the arm
            so.FindProperty("boxExtents").vector3Value = coral ? Vector3.zero : new Vector3(5, 3, 3);
            so.FindProperty("maxDistance").floatValue = maxDistance > 0 ? maxDistance : coral ? 2.76f : 20f;
            so.FindProperty("accuracy").floatValue = accuracy > 0 ? accuracy : coral ? 0.53f : 3f;
            so.FindProperty("roationForce").floatValue = coral ? 1.5f : 0f;
            so.FindProperty("maxRotationSpeed").floatValue = coral ? 3f : 0f;
            so.FindProperty("useRotaion").boolValue = coral;
            so.FindProperty("smoothHandoff").boolValue = smooth ?? !coral;
            so.ApplyModifiedPropertiesWithoutUndo();
            return intake;
        }

        private static Vector3 CoralLongAxis()
        {
            var coral = AssetDatabase.LoadAssetAtPath<GameObject>(CoralPrefab);
            var mf = coral.GetComponentInChildren<MeshFilter>();
            var s = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
            var local = s.x >= s.y && s.x >= s.z ? Vector3.right : s.y >= s.z ? Vector3.up : Vector3.forward;
            return coral.transform.InverseTransformDirection(mf.transform.TransformDirection(local)).normalized;
        }

        private static void SetPid(SerializedObject so, string field, float p, float i, float d, float max)
        {
            var pid = so.FindProperty(field);
            pid.FindPropertyRelative("kP").floatValue = p;
            pid.FindPropertyRelative("kI").floatValue = i;
            pid.FindPropertyRelative("kD").floatValue = d;
            pid.FindPropertyRelative("Max").floatValue = max;
            pid.FindPropertyRelative("Isaturation").floatValue = 0;
        }

        private static void SetState(SerializedObject so, string field, string name, int num, float speed, Transform target,
            bool smooth, float angular)
        {
            var s = so.FindProperty(field);
            s.FindPropertyRelative("name").stringValue = name;
            s.FindPropertyRelative("stateNum").intValue = num;
            s.FindPropertyRelative("stateMoveSpeed").floatValue = speed;
            s.FindPropertyRelative("stateTarget").objectReferenceValue = target;
            s.FindPropertyRelative("smoothHandoff").boolValue = smooth;
            s.FindPropertyRelative("angularSpeed").floatValue = angular;
        }

        // ------------------------------------------------------------------ main menu, metadata, modpack, addressables

        private static GameObject BuildMainMenu(GameObject robot)
        {
            var menu = new GameObject("MainMenu180");
            menu.AddComponent<Animator>();
            var copy = Object.Instantiate(robot, menu.transform);
            copy.name = "180";
            var dt = copy.transform.Find("Drivetrain");
            if (dt) Object.DestroyImmediate(dt.gameObject);
            foreach (var c in copy.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(c);
            foreach (var c in copy.GetComponentsInChildren<Joint>(true)) Object.DestroyImmediate(c);
            foreach (var c in copy.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(c);
            foreach (var c in copy.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            foreach (var c in copy.GetComponentsInChildren<Component>(true))
                if (c && !(c is Transform || c is MeshFilter || c is MeshRenderer)) Object.DestroyImmediate(c);
            foreach (var t in menu.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 0;
            return menu;
        }

        private static Games.Reefscape.Robots.ReefscapeRobotMetadataSO MakeMetadata(GameObject robot, GameObject menu,
            List<GameObject> cards)
        {
            var path = Dir + "/Anemone.asset";
            var meta = AssetDatabase.LoadAssetAtPath<Games.Reefscape.Robots.ReefscapeRobotMetadataSO>(path);
            if (!meta)
            {
                meta = ScriptableObject.CreateInstance<Games.Reefscape.Robots.ReefscapeRobotMetadataSO>();
                AssetDatabase.CreateAsset(meta, path);
            }

            var so = new SerializedObject(meta);
            so.FindProperty("teamNumber").intValue = 180;
            so.FindProperty("teamName").stringValue = "S.P.A.M.";
            so.FindProperty("robotPrefab").objectReferenceValue = robot;
            so.FindProperty("robotPreloadGamePiece").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(CoralPrefab);
            so.FindProperty("mainMenuPrefab").objectReferenceValue = menu;
            so.FindProperty("hasAlternateRobot").boolValue = false;
            if (AssetImporter.GetAtPath(Dir + "/Anemone.png") is TextureImporter ti && ti.textureType != TextureImporterType.Sprite)
            {
                ti.textureType = TextureImporterType.Sprite;
                ti.SaveAndReimport();
            }

            var image = AssetDatabase.LoadAssetAtPath<Sprite>(Dir + "/Anemone.png");
            if (image) so.FindProperty("robotImage").objectReferenceValue = image;
            var tc = so.FindProperty("tutorialCards");
            tc.arraySize = cards.Count;
            for (var i = 0; i < cards.Count; i++) tc.GetArrayElementAtIndex(i).objectReferenceValue = cards[i];
            so.FindProperty("autoClimbs").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(meta);
            return meta;
        }

        private static void MakeModpack(Object meta)
        {
            var path = ModFolder + "/SPAM.asset";
            var pack = AssetDatabase.LoadAssetAtPath<Games.Reefscape.GameManagement.ReefscapeBaseModpackSo>(path);
            if (!pack)
            {
                pack = ScriptableObject.CreateInstance<Games.Reefscape.GameManagement.ReefscapeBaseModpackSo>();
                AssetDatabase.CreateAsset(pack, path);
            }

            var so = new SerializedObject(pack);
            so.FindProperty("modpackName").stringValue = "S.P.A.M.";
            so.FindProperty("modpackVersion").stringValue = "1.0.0";
            so.FindProperty("authorName").stringValue = "Seb";
            so.FindProperty("description").stringValue = "Team 180 S.P.A.M. Anemone (2025)";
            var robots = so.FindProperty("robots");
            robots.arraySize = 1;
            robots.GetArrayElementAtIndex(0).objectReferenceValue = meta;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pack);
        }

        private static void MakeAddressablesGroup()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var group = settings.FindGroup(GroupName);
            if (!group)
            {
                var template = settings.FindGroup(TemplateGroup);
                group = settings.CreateGroup(GroupName, false, false, true, template.Schemas, template.SchemaTypes.ToArray());
            }

            group.GetSchema<BundledAssetGroupSchema>().IncludeInBuild = false;
            if (!settings.GetLabels().Contains("modpack_metadata")) settings.AddLabel("modpack_metadata");
            var entry = settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(ModFolder), group);
            entry.address = ModFolder;
            entry.SetLabel("modpack_metadata", true, true);
            EditorUtility.SetDirty(settings);
        }
    }
}
