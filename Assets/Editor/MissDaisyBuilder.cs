using System.Collections.Generic;
using System.IO;
using System.Linq;
using Games.Reefscape.GamePieceSystem;
using Prefabs.Reefscape.Robots.Mods.MissDaisy._341;
using RobotFramework.Components;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Editor
{
    /// <summary>
    /// Builds the 341 Miss Daisy mod (prefab, main-menu prefab, setpoints, metadata, modpack, Addressables group) from
    /// Models/MissDaisy.fbx, whose mechanism nodes (Drivebase, Intake, Stage1, Carriage, Arm, Claw, Climber) have their
    /// origins on their pivots. Root components and their values come from the 971 Spartans prefab, like the other mods.
    /// Rerunnable: setpoint assets that already exist keep their (tuned) values; the prefabs are rebuilt.
    ///   Unity -batchmode -projectPath . -executeMethod Editor.MissDaisyBuilder.Build -quit
    /// </summary>
    public static class MissDaisyBuilder
    {
        private const string ModFolder = "Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy";
        private const string Dir = ModFolder + "/341";
        private const string Fbx = Dir + "/Models/MissDaisy.fbx";
        private const string TemplatePrefab = "Assets/Prefabs/Reefscape/Robots/Mods/Spartans/971/971.prefab";
        private const string TemplateDir = "Assets/Prefabs/Reefscape/Robots/Mods/Spartans/971";
        private const string DrivetrainPrefab = "Assets/Prefabs/Reefscape/Robots/Drivetrain.prefab";
        private const string CoralPrefab = "Assets/Prefabs/Reefscape/Coral.prefab";
        private const string NoBounce = "Assets/Materials/PhysicsMaterials/NoBounce.physicMaterial";
        private const string Grip = "Assets/Materials/PhysicsMaterials/Grip.physicMaterial";
        private const string SuperGrip = "Assets/Materials/PhysicsMaterials/SuperGrip.physicMaterial";
        private const string LowFriction = "Assets/Materials/PhysicsMaterials/LowFrictionMaterial.physicMaterial";
        private const string GroupName = "MissDaisy";
        private const string TemplateGroup = "Lanternfly Mod";

        private const float WheelOffset = 0.2667f; // module wheel centres, m (CAD)
        private const float AlignOffsetInches = 10.7f; // bumper on the reef face (measured)
        private const float RollerContactRadius = 0.026f; // m: about a cm into a lying coral, so it is pressed on and gripped at first touch
        private const bool OverwriteSetpoints = true; // the table is what the PlayMode match test verifies

        // CAD (Blender, Z up, m) -> model-root local Unity (Y up, front = +Z = the intake side).
        private static Vector3 B2U(float x, float y, float z) => new(-x, z, -y);
        private const float InchesPerMeter = 39.3700787f;

        [MenuItem("Tools/Mods/Build Miss Daisy (341)")]
        public static void Build()
        {
            // a Library cloned with a project copy can lose this script's link ("referenced script is missing" on the robot)
            AssetDatabase.ImportAsset(Dir + "/MissDaisy.cs", ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(TemplateDir + "/Spartans.cs", ImportAssetOptions.ForceUpdate); // the template's too
            ConfigureModel();
            var setpoints = MakeSetpoints();
            CopyIfMissing(TemplateDir + "/Bumper Materials/Blue Bumpers.mat", Dir + "/Bumper Materials/Blue Bumpers.mat");
            CopyIfMissing(TemplateDir + "/Bumper Materials/Red Bumpers.mat", Dir + "/Bumper Materials/Red Bumpers.mat");
            // 341's bumper colours
            foreach (var (mat, hex) in new[] { ("Blue Bumpers", "#0d49d0"), ("Red Bumpers", "#c80c27") })
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>($"{Dir}/Bumper Materials/{mat}.mat");
                if (!m || !ColorUtility.TryParseHtmlString(hex, out var colour)) continue;
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", colour);
                if (m.HasProperty("_Color")) m.SetColor("_Color", colour);
                EditorUtility.SetDirty(m);
            }
            var cards = new List<GameObject>();
            // Miss Daisy's own tips, each with an in-game shot of it (the template's cards were another robot's)
            foreach (var (card, text, image) in new[]
                     {
                         ("TutorialCard.prefab", "Miss Daisy XXIII can score on both sides!", "MissDaisy Both Sides.png"),
                         ("TutorialCard 1.prefab", "Miss Daisy XXIII can intake coral from the ground and humans!", "MissDaisy Human Player.png"),
                         ("TutorialCard 2.prefab", "Miss Daisy XXIII tosses the algae into the barge!", "MissDaisy Barge.png"),
                     })
            {
                CopyIfMissing(TemplateDir + "/TutorialCards/" + card, Dir + "/TutorialCards/" + card);
                SetCard(Dir + "/TutorialCards/" + card, text, Dir + "/TutorialCards/" + image);
                cards.Add(AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/TutorialCards/" + card));
            }

            var robot = BuildRobot(setpoints);
            var robotAsset = PrefabUtility.SaveAsPrefabAsset(robot, Dir + "/341.prefab");
            var menu = BuildMainMenu(robot);
            var menuAsset = PrefabUtility.SaveAsPrefabAsset(menu, Dir + "/MainMenu341.prefab");
            Object.DestroyImmediate(menu);
            Object.DestroyImmediate(robot);

            var meta = MakeMetadata(robotAsset, menuAsset, cards);
            MakeModpack(meta);
            MakeAddressablesGroup();
            AssetDatabase.SaveAssets();
            Debug.Log("MISSDAISY BUILD OK");
        }

        /// <summary>Opens the Reefscape scene with Miss Daisy as the spawned robot (scene not saved): press Play.
        ///   Unity -projectPath . -executeMethod Editor.MissDaisyBuilder.OpenPlaytest</summary>
        [MenuItem("Tools/Mods/Playtest Miss Daisy (341)")]
        public static void OpenPlaytest()
        {
            UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/Reefscape.unity");
            var spawn = Object.FindFirstObjectByType<GameSystems.Management.RobotSpawnController>();
            if (!spawn)
            {
                Debug.LogWarning("MISSDAISY no RobotSpawnController in the Reefscape scene");
                return;
            }

            var so = new SerializedObject(spawn);
            so.FindProperty("robotOverride").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<Games.Reefscape.Robots.ReefscapeRobotMetadataSO>(Dir + "/MissDaisy.asset");
            so.ApplyModifiedPropertiesWithoutUndo();
            Selection.activeObject = spawn.gameObject;
            Debug.Log("MISSDAISY playtest ready: press Play (robot override = Miss Daisy)");
        }

        /// <summary>Renders the built prefab from a few angles to PNGs (batchmode without -nographics).
        ///   Unity -batchmode -projectPath . -executeMethod Editor.MissDaisyBuilder.Snapshot -quit  (env MISSDAISY_SHOTS=dir)</summary>
        public static void Snapshot()
        {
            var outDir = System.Environment.GetEnvironmentVariable("MISSDAISY_SHOTS") ?? "Temp/MissDaisyShots";
            Directory.CreateDirectory(outDir);
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
            var robot = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/341.prefab"));
            var blue = robot.GetComponent<Games.Reefscape.Robots.ReefscapeRobotBase>();
            var bso = new SerializedObject(blue);
            var bm = bso.FindProperty("blueBumperMaterial").objectReferenceValue as Material;
            foreach (var r in robot.GetComponentsInChildren<MeshRenderer>().Where(r => r.name.Contains("Bumper Noodles")))
                r.sharedMaterial = bm;
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
            var centre = new Vector3(0, 0.55f, 0);
            foreach (var (name, dir) in new[] { ("front_left", new Vector3(-1f, 0.7f, 1.2f)), ("back_right", new Vector3(1.1f, 0.8f, -1.2f)),
                                                ("side", new Vector3(1f, 0.25f, 0f)), ("top", new Vector3(0.1f, 1f, 0.15f)) })
            {
                cam.transform.position = centre + dir.normalized * 3.4f;
                cam.transform.LookAt(centre);
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(outDir, name + ".png"), tex.EncodeToPNG());
            }

            RenderTexture.active = null;
            Debug.Log("MISSDAISY SHOTS " + outDir);
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

            // Materials as editable assets next to the model (Unity matches them by name on reimport).
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

        private static Dictionary<string, MissDaisySetpoint> MakeSetpoints()
        {
            // elevator m, shoulder / wrist / intake / climber deg from the starting configuration. Straight from 341's
            // RobotPoseDefinitions (FRC2025-Public), in this robot's terms: elevator m as theirs (carriage travel; their
            // shoulder pivot sits at height + 0.32 m, this model's at + 0.315 m); shoulder = -theirs ("+ = toward the
            // front", the scoring side); wrist = theirs + 90 (their -pi/2 coral-scoring wrist is this model's 0); intake
            // = theirs - 90 (their pi/2 is stowed, 0 deployed); climber = theirs (0 stowed flat inside the drive base,
            // as this model is; 165 deployed out the front, up and over; 77 climbed).
            const float ClimberStow = 0f;
            var values = new (string name, float e, float s, float w, float i, float c)[]
            {
                ("Stow", 0.02f, 0f, 0f, 0f, ClimberStow),                 // STOW
                ("Ground Intake", 0.2544f, -126.5f, 90f, -90f, ClimberStow), // GROUND_INTAKE: claw over the deployed intake
                ("HP Intake", 0.2408f, -38f, 90f, 0f, ClimberStow),       // PLAYER_INTAKE: 0.19 m + 2 in
                ("L1", 0.234f, 85f, 90f, 0f, ClimberStow),                 // claw plate 3 cm over the trough lip (y 0.457), coral just over one on the lip; a coral it cannot shove back lifts it (MissDaisy.cs); 341's 55 at elevator 0 could not reach past the lip
                ("L2", 0.316f, 43f, 0f, 0f, ClimberStow),                 // L2_PRESCORE
                ("L2 Score", 0.2906f, 73f, 0f, 0f, ClimberStow),          // L2_SCORE: 0.316 m - 1 in
                ("L3", 0.6746f, 40f, 0f, 0f, ClimberStow),                // L3_PRESCORE: 0.7 m - 1 in
                ("L3 Score", 0.63f, 73f, 0f, 0f, ClimberStow),            // L3_SCORE
                // L4_PRESCORE / L4_SCORE angles; the elevator 0.115 m above their 1.2245 / 1.2054 m: at theirs the coral
                // ends below this field's L4 post top and 12 cm short of it (PlayMode), here it drops on from above
                ("L4", 1.34f, 42f, 0f, 0f, ClimberStow),
                ("L4 Score", 1.34f, 67f, 0f, 0f, ClimberStow),
                // the algae poses' angles; the elevator 4-7 cm above theirs, where this model's claw meets the ball
                ("Algae Holding", 0.40f, 0f, 180f, 0f, ClimberStow),     // ALGAE_HOLDING: holding algae it rides 0.4 m up, arm up, wrist over (not STOW)
                ("Ground Algae", 0.145f, 112f, 90f, 0f, ClimberStow),     // ALGAE_GROUND_INTAKE (0.101 m)
                ("Lollipop", 0.29f, 101.93f, 90f, 0f, ClimberStow),       // ALGAE_UPRIGHT_INTAKE (0.25 m)
                ("Low Algae", 0.70f, 100f, 180f, 0f, ClimberStow),        // L2_ALGAE (0.65 m): from above
                ("High Algae", 1.11f, 100f, 180f, 0f, ClimberStow),       // L3_ALGAE (1.04 m): from above
                ("Processor", 0.085f, 85f, 90f, 0f, ClimberStow),         // PROCESSOR
                ("Fling Start", 0.5f, 130f, 90f, 0f, ClimberStow),        // ALGAE_FLING
                ("Fling End", 1.2254f, -20f, 90f, 0f, ClimberStow),       // ALGAE_FLINGUP: 1.2 m + 1 in
                ("Climb Prep", 0f, -90f, 0f, -90f, 180.2f),         // 341's CLIMB_POST arm and elevator from the start (bottom, arm flat over the intake: clear of the cage swinging in over the robot, and no arm move between Climb and Climbed); harpoon deployed flat out at 180, so it slides up the posts further before it lifts
                ("Climbed", 0f, -90f, 0f, -90f, 77f),             // CLIMB_POST: only the climber moves, to CLIMBER_POST_ANGLE
            };
            Directory.CreateDirectory(Dir + "/Setpoints");
            var map = new Dictionary<string, MissDaisySetpoint>();
            foreach (var v in values)
            {
                var path = $"{Dir}/Setpoints/{v.name}.asset";
                var sp = AssetDatabase.LoadAssetAtPath<MissDaisySetpoint>(path);
                if (!sp)
                {
                    sp = ScriptableObject.CreateInstance<MissDaisySetpoint>();
                    AssetDatabase.CreateAsset(sp, path);
                }
                else if (!OverwriteSetpoints) { map[v.name] = sp; continue; }

                sp.elevatorHeight = v.e;
                sp.shoulderAngle = v.s;
                sp.wristAngle = v.w;
                sp.intakeAngle = v.i;
                sp.climberAngle = v.c;
                EditorUtility.SetDirty(sp);

                map[v.name] = sp;
            }

            return map;
        }

        // ------------------------------------------------------------------ robot

        private static GameObject BuildRobot(Dictionary<string, MissDaisySetpoint> sp)
        {
            var template = PrefabUtility.LoadPrefabContents(TemplatePrefab);
            var robotLayer = LayerMask.NameToLayer("Robot");

            var root = new GameObject("341") { tag = "BlueRobot" };
            // Every root component of the template except its robot script (values and asset references kept).
            foreach (var src in template.GetComponents<Component>())
            {
                if (!src || src is Transform || src is Games.Reefscape.Robots.ReefscapeRobotBase) continue; // (null: its robot script, unlinked in a cloned Library)
                UnityEditorInternal.ComponentUtility.CopyComponent(src);
                var have = root.GetComponent(src.GetType());
                if (have)
                    UnityEditorInternal.ComponentUtility.PasteComponentValues(have);
                else
                    UnityEditorInternal.ComponentUtility.PasteComponentAsNew(root);
            }

            var md = root.AddComponent<MissDaisy>();
            // (a cloned Library can leave the template's robot script unlinked: then the last build of this robot's values)
            var baseSrc = template.GetComponent<Games.Reefscape.Robots.ReefscapeRobotBase>();
            if (!baseSrc) baseSrc = AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/341.prefab").GetComponent<Games.Reefscape.Robots.ReefscapeRobotBase>();
            var mdSo = new SerializedObject(md);
            var it = new SerializedObject(baseSrc).GetIterator();
            if (it.NextVisible(true))
            {
                do
                {
                    if (it.name != "m_Script" && mdSo.FindProperty(it.propertyPath) != null)
                        mdSo.CopyFromSerializedProperty(it);
                } while (it.NextVisible(false));
            }

            mdSo.ApplyModifiedPropertiesWithoutUndo();

            // Drivetrain (shared prefab), wheels moved onto this robot's modules.
            var dt = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(DrivetrainPrefab), root.transform);
            var tdt = template.transform.Find("Drivetrain");
            dt.transform.localPosition = tdt.localPosition;
            dt.transform.localRotation = tdt.localRotation;
            dt.tag = "BlueRobot";
            foreach (var group in new[] { "Wheels", "Raycast" })
            {
                foreach (var (n, x, z) in new[] { ("FL", -1, 1), ("FR", 1, 1), ("BL", -1, -1), ("BR", 1, -1) })
                {
                    var w = dt.transform.Find($"{group}/{n}");
                    w.localPosition = new Vector3(x * WheelOffset, w.localPosition.y, z * WheelOffset);
                }
            }

            // Model: wheel bottoms on the drivetrain's ground plane.
            var wc = dt.GetComponentInChildren<WheelCollider>();
            var ground = wc.transform.TransformPoint(wc.center).y - wc.radius -
                         wc.suspensionDistance * (1 - wc.suspensionSpring.targetPosition) - root.transform.position.y;
            var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Fbx), root.transform);
            PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            model.transform.localPosition = new Vector3(0, ground - 0.003f, 0);
            var mt = model.transform;
            OrientModel(mt, root.transform);
            // The real robot's front is its scoring side, with the floor intake at the back: the CAD is intake-forward,
            // so turn the model round before anything is built on it (joint frames stay square to the root, and
            // robots spawn facing their reef, scoring side first).
            mt.RotateAround(root.transform.position, root.transform.up, 180f);
            var modelOffset = new Vector3(0, ground - 0.003f, 0);
            Vector3 Turn(Vector3 v) => new(-v.x, v.y, -v.z);
            Vector3 P(float x, float y, float z) => root.transform.TransformPoint(Turn(B2U(x, y, z)) + modelOffset);

            Transform Node(string n) => mt.GetComponentsInChildren<Transform>(true).First(t => t.name == n);
            // the climber's drive chains run from the frame's motor sprocket: they stay with the frame, not swing with the
            // climber
            foreach (var r in mt.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name.StartsWith("32 links #35 chain")).ToList())
                r.transform.SetParent(Node("Drivebase"), true);
            // no energy chain on the elevator (the CAD's 41-link semi-enclosed cable carrier)
            foreach (var r in mt.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name.Contains("Link Semi-Enclosed")).ToList())
                Object.DestroyImmediate(r.gameObject);
            var drivebase = Node("Drivebase");
            var intake = Node("Intake");
            var stage1 = Node("Stage1");
            // The guide-roller axles and bearings in the moving stages' bearing blocks: the CAD left them on the frame, standing
            // in mid air as the stages rode up. Which stage: how far out they sit, next to that stage's rails (carriage blocks
            // at +-0.0635, first stage at +-0.095). The frame's own are up at the top of its uprights.
            foreach (var r in mt.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.transform.parent && r.transform.parent.name == "Drivebase" &&
                         (r.name.StartsWith("WCP-0474") || r.name.StartsWith("WCP-0199-007") || r.name.StartsWith("WCP-0212") || r.name.StartsWith("WCP-0039") || r.name.StartsWith("WCP-0199-004"))).ToList())
            {
                var at = root.transform.InverseTransformPoint(WorldBounds(r).center);
                if (at.y > 0.6f || at.x > -0.1f || at.x < -0.28f) continue;
                var side = Mathf.Abs(at.z);
                if (side > 0.05f && side < 0.09f) r.transform.SetParent(Node("Carriage"), true);
                else if (side >= 0.09f && side < 0.118f) r.transform.SetParent(stage1, true);
            }
            // the two shoulder screws through the stage's top tube, which its two R4 pulleys turn on: the CAD left them and
            // their spacers and bearings on the frame, standing still while the stage and its pulleys rode up
            foreach (var r in mt.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name is "91259A101_Alloy Steel Shoulder Screw" or "91259A101_Alloy Steel Shoulder Screw.003"
                         or "Nylon Spacer" or "Nylon Spacer.003" or "R4 Bearing.004" or "R4 Bearing.005" or "R4 Bearing.014" or "R4 Bearing.015").ToList())
                r.transform.SetParent(stage1, true);
            var carriage = Node("Carriage");
            var arm = Node("Arm");
            var claw = Node("Claw");
            var climber = Node("Climber");
            var pivotErr = (climber.position - P(0, 0.2715f, 0.1825f)).magnitude + (arm.position - P(0, 0, 0.3175f)).magnitude;
            Debug.Log($"MISSDAISY climber pivot {climber.position} (expect ~{P(0, 0.2715f, 0.1825f)}), arm {arm.position} err {pivotErr:F4}");
            if (pivotErr > 0.01f) throw new System.Exception("MISSDAISY model axes do not match the CAD mapping");

            var elevatorGo = new GameObject("Elevator");
            elevatorGo.transform.SetParent(root.transform, false);
            drivebase.SetParent(root.transform, true);
            intake.SetParent(root.transform, true);
            climber.SetParent(root.transform, true);
            stage1.SetParent(elevatorGo.transform, true);
            carriage.SetParent(elevatorGo.transform, true); // continuous: both stages ride on the root
            arm.SetParent(carriage, true);
            claw.SetParent(arm, true);
            Object.DestroyImmediate(model);
            // Joint frames must line up with the robot (axis X = right): square every mechanism node, children stay put.
            foreach (var n in new[] { drivebase, intake, stage1, carriage, arm, claw, climber }) Square(n, root.transform);

            // Starting configuration: the CAD has the climber deployed out the back; it stows flat inside the frame.
            // Its parts turn about the pivot, the joint body stays square, so the climber reads 0 deg when stowed.
            foreach (var k in Enumerable.Range(0, climber.childCount).Select(climber.GetChild).ToList())
                k.RotateAround(climber.position, root.transform.right, -180f);

            var rootRb = root.GetComponent<Rigidbody>();
            rootRb.automaticCenterOfMass = true; // (off-centre it dragged and turned the robot when driving; it only moves while climbed)
            var intakeJ = MakeJoint(intake.gameObject, rootRb, angular: 'x');
            var stage1J = MakeJoint(stage1.gameObject, rootRb, linearY: true);
            var carriageJ = MakeJoint(carriage.gameObject, rootRb, linearY: true);
            var shoulderJ = MakeJoint(arm.gameObject, carriage.GetComponent<Rigidbody>(), angular: 'x');
            var wristJ = MakeJoint(claw.gameObject, arm.GetComponent<Rigidbody>(), angular: 'y');
            var climberJ = MakeJoint(climber.gameObject, rootRb, angular: 'x', climber: true);
            // Realistic masses for the moving stages (1 kg each against a 98 kg robot made the joints give way when the
            // hanging robot's cage leaned on the arm: the elevator was crushed below its bottom and the arm pushed
            // 13 deg down into the intake). Roughly 341's: tube stages ~3 kg, the arm with its motors ~3.5 kg, the claw
            // ~2 kg, the floor intake ~4 kg.
            foreach (var (j, m) in new[] { (stage1J, 3f), (carriageJ, 3f), (shoulderJ, 3.5f), (wristJ, 2f), (intakeJ, 4f) })
                j.GetComponent<Rigidbody>().mass = m;

            var elev = root.GetComponent<GenericElevator>();
            var eso = new SerializedObject(elev);
            var stages = eso.FindProperty("stages");
            stages.arraySize = 2;
            stages.GetArrayElementAtIndex(0).objectReferenceValue = stage1J;
            stages.GetArrayElementAtIndex(1).objectReferenceValue = carriageJ;
            eso.FindProperty("elevatorType").enumValueIndex = (int)RobotFramework.Enums.ElevatorType.Continuous;
            // kP 40 (the template's 15 trailed the moving profile by ~12 in at speed, then crept the last of it in)
            SetPid(eso, "pidConstants", 40f, 0f, 0f, 6f);
            // Continuous: the carriage climbs Stage 1 until its top meets Stage 1's top, then Stage 1 rides up with it
            // (GenericElevator starts Stage 1 once the carriage's height on the root passes stageHeight - carriageHeight).
            // Measured from the model so the carriage really reaches the top and Stage 1 stays in the base uprights.
            {
                Bounds Of(Transform t, System.Func<Transform, bool> skip = null)
                {
                    var rs = t.GetComponentsInChildren<MeshRenderer>(true).Where(r => skip == null || !skip(r.transform)).ToList();
                    var bb = WorldBounds(rs[0]);
                    foreach (var r in rs) bb.Encapsulate(WorldBounds(r));
                    return bb;
                }

                var s1 = Of(stage1);
                var car = Of(carriage, t => t.IsChildOf(arm));
                var baseTop = root.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name.StartsWith("2x1 Elevator Uprights"))
                    .Select(r => WorldBounds(r).max.y).DefaultIfEmpty(s1.max.y).Max();
                var carriageRise = s1.max.y - car.max.y;          // carriage travel inside Stage 1
                var carriageY0 = root.transform.InverseTransformPoint(carriage.position).y;
                var stageLen = (s1.max.y - s1.min.y) * InchesPerMeter;
                var stageTravel = baseTop - s1.min.y - 0.15f;     // Stage 1 keeps ~6 in inside the base uprights
                eso.FindProperty("stageHeight").floatValue = stageLen;
                eso.FindProperty("carriageHeight").floatValue = stageLen - (carriageY0 + carriageRise) * InchesPerMeter;
                eso.FindProperty("stageOverlap").floatValue = stageLen - stageTravel * InchesPerMeter;
                Debug.Log($"MISSDAISY elevator: Stage 1 {stageLen:F1} in, carriage rises {carriageRise * InchesPerMeter:F1} in inside it, " +
                          $"Stage 1 travel {stageTravel * InchesPerMeter:F1} in, max {(carriageRise + stageTravel) * InchesPerMeter:F1} in");
            }
            eso.FindProperty("referenceTransform").objectReferenceValue = null;
            // Heights from the starting pose (like 694's continuous elevator), not from the stage nodes' own origins.
            var ov = eso.FindProperty("setStageOverrides");
            ov.arraySize = 2;
            for (var i = 0; i < 2; i++)
            {
                var o = ov.GetArrayElementAtIndex(i);
                o.FindPropertyRelative("stageNum").intValue = i + 1;
                o.FindPropertyRelative("overrideAxis").enumValueIndex = (int)RobotFramework.Enums.JointAxis.Y;
                o.FindPropertyRelative("useStartingOffset").boolValue = true;
            }
            // the elevator's own sound (its clip and stage clicks come with the template's GenericElevator values)
            var elevAudio = root.AddComponent<AudioSource>();
            elevAudio.playOnAwake = false;
            elevAudio.spatialBlend = 1f;
            elevAudio.outputAudioMixerGroup = Resources.Load<UnityEngine.Audio.AudioMixerGroup>("MainAudioMixer").audioMixer.FindMatchingGroups("RobotSounds")[0];
            eso.FindProperty("elevatorSound").objectReferenceValue = elevAudio;
            eso.ApplyModifiedPropertiesWithoutUndo();

            // Auto-align stand-off for this arm: measured in PlayMode so the claw's coral lines up with the branches.
            var align = new SerializedObject(root.GetComponent<ReefscapeAutoAlign>());
            align.FindProperty("offset").vector3Value = new Vector3(0, 0, AlignOffsetInches);
            align.ApplyModifiedPropertiesWithoutUndo();

            var drive = new SerializedObject(root.GetComponent<RobotFramework.Controllers.Drivetrain.DriveController>());
            var dp = drive.FindProperty("drivetrainParent");
            dp.objectReferenceValue = dp.type.Contains("Transform") ? dt.transform : dt;
            drive.ApplyModifiedPropertiesWithoutUndo();

            // Intake: the front roller (3" compliant wheels) is on the sprung roller arms; the top-roller flap pivots on
            // its shaft, carries the belts and the 1.625" top roller, and is sprung down by surgical tubing. Floor coral
            // goes under the front roller, pushes the flap up and rides the belts to the top, where the claw takes it.
            // Physics rollers only touch game pieces; the meshes spin on GenericAnimationJoints.
            var F = WorldCentre(intake, n => n.StartsWith("am-3945"));
            var T = WorldCentre(intake, n => n.StartsWith("1.625\""));
            var toRamps = (P(0, -0.765f, -0.644f) - P(0, 0, 0)).normalized; // off the flap, toward the ramps (CAD)
            // Roller arms: the side plates with the green front roller (and the flap on its shaft) pivot on the intake's
            // 2x1 at the side-plate pins and are pulled down by the surgical tubing - a sprung HingeJoint. A coral pushed
            // under the front roller lifts the whole assembly. CAD: the plates turn on Shaft.006/.013 (round hole); the
            // lower pin (Shaft.012/.015) runs in a ~2 cm arc slot, ~25 deg of lift. On the arms: the plates, the front tube,
            // the roller's Kraken (NEO spacer, 12T pulley, 83T belt) and everything on the roller shaft (Shaft.010, wheels,
            // pulleys, spacers, bearings, wheel supports, retainers). The tubing mounts, ramps and plugs stay on the 2x1.
            var armPivot = P(0, -0.203f, 0.384f);
            var rollerArms = Empty("Roller Arms", intake, armPivot, root.transform.rotation);
            foreach (var r in OnAxis(intake, F, root.transform.right, 0.03f).Concat(intake.GetComponentsInChildren<MeshRenderer>(true)
                         .Where(r => new[] { "Roller Holder", "Coral Intake Front Tube", "Kraken X60 Brushless Motor", "NEO Spacer",
                             "12T x 15mm", "83T HTD Belt" }.Any(r.name.StartsWith))).Distinct().ToList())
                r.transform.SetParent(rollerArms, true);
            var armsRb = rollerArms.gameObject.AddComponent<Rigidbody>();
            armsRb.mass = 1.0f;
            armsRb.excludeLayers = LayerMask.GetMask("Robot");
            // lifts away from the ramps only (+X turns the roller toward the ramps when this dot is positive); floppy,
            // light tubing, preloaded onto its rest stop so the arms don't sag out
            var liftsNegative = Vector3.Dot(Vector3.Cross(root.transform.right, F - armPivot), toRamps) > 0;
            SprungHinge(rollerArms.gameObject, intake.GetComponent<Rigidbody>(), liftsNegative ? -1f : 1f, 25f, 12f, 2f, 8f,
                pivotDrag: 6f); // damped
            // 3" compliant wheels squash round the coral: a thin contact roller, on the arms, that only touches coral,
            // so a coral lying at an angle slides its end under it too
            var rollers = new List<GenericRoller> { MakeRoller(rollerArms, "Front Roller", F, RollerContactRadius, 0.62f, LayerMask.GetMask("Coral"), SuperGrip) };
            var rollerVisuals = new List<GenericAnimationJoint>
                { Spin(rollerArms, "Front Roller Wheels", F, root.transform.right, n => new[] { "am-3945", "27t HTD Pulley", "32t HTD Pulley",
                    "Shaft.010", "Spacer (Configurable)", "1/16\" 3/8\" Hex Acetal Spacer" }.Any(n.StartsWith)) };
            // the flap in the roller arms' own frame (a frame turned 180 deg from its connected body leaves a hinge
            // reading 180 deg, outside its limits, and rigid); which way is "lift" is given by the hinge axis' sign
            var flapRot = root.transform.rotation;
            var flapLiftSign = Vector3.Dot(Vector3.Cross(root.transform.right, T - F), toRamps) > 0 ? -1f : 1f;
            var flapHolder = Empty("Intake Flap Holder", rollerArms, F, flapRot);
            var flapT = Empty("Intake Flap", flapHolder, F, flapRot);
            // the flap: its plates, belts, standoff and everything on the top-roller shaft (not the roller Kraken: it is
            // bolted to the side plate on the same axis)
            foreach (var r in OnAxis(intake, T, root.transform.right, 0.03f).Where(r => !r.transform.IsChildOf(rollerArms)).Concat(intake.GetComponentsInChildren<MeshRenderer>(true).Where(r =>
                         r.name.StartsWith("Second Roller Pivot Plate") || r.name.StartsWith("81T HTD Belt") ||
                         r.name.StartsWith("Bearing Retainer Second Roller") || r.name == "Shaft.008")).Distinct().ToList())
                r.transform.SetParent(flapT, true);
            rollerVisuals.Add(Spin(flapT, "Top Roller Wheels", T, root.transform.right, n => new[] { "1.625\"", "16t HTD Pulley", "Shaft.007",
                "1/16\" 1/2\" Hex Acetal Spacer" }.Any(n.StartsWith)));
            Debug.Log("MISSDAISY roller arms: " + string.Join(", ", rollerArms.GetComponentsInChildren<MeshRenderer>(true)
                .Where(r => !r.transform.IsChildOf(flapHolder)).Select(r => r.name)));
            Debug.Log("MISSDAISY flap: " + string.Join(", ", flapHolder.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.name)));
            // The flap is a physical body on a sprung hinge at the front roller shaft (the surgical tubing pulls it down
            // onto its stop); a coral driven up under it lifts it by pushing on its belts and top roller.
            var along = (T - F).normalized;
            var coralOnly = LayerMask.GetMask("Coral");
            var flapRb = flapT.gameObject.AddComponent<Rigidbody>();
            flapRb.mass = 0.2f;
            flapRb.includeLayers = coralOnly;
            flapRb.excludeLayers = ~coralOnly;
            SprungHinge(flapT.gameObject, armsRb, flapLiftSign, 80f, 3f, 0.6f, 8f, pivotDrag: 1f); // light tubing the coral lifts (high enough for a coral going over the 2x1: at 40 it was trapped under the top roller). Its spring is MissDaisy.SpringHinges; drag 4 slowed its return
            // the belts' coral side, over the top half of the flap: near the pivot the 3" front wheels stand out past
            // the belt pulleys, and that end of the flap cannot lift out of the coral's way
            var flapLen = (T - F).magnitude;
            // the belts: driven like the rollers - three small motor rollers along the belt line, their coral side where
            // the belts run (a frame named for the belts marks the line)
            Empty("Flap Belts", flapT, F + along * flapLen * 0.725f + toRamps * 0.02f, Quaternion.LookRotation(along, -toRamps));
            const float beltRollerR = 0.012f;
            foreach (var (k, at) in new[] { (1, 0.5f), (2, 0.725f), (3, 0.95f) })
            {
                var br = MakeRoller(flapT, $"Flap Belts Roller {k}", F + along * flapLen * at + toRamps * (0.025f - beltRollerR), beltRollerR, 0.30f, coralOnly);
                br.GetComponent<Rigidbody>().mass = 0.03f; // light: the flap is light tubing-sprung and the coral lifts it
                rollers.Add(br);
            }
            // The channel's floor - the ramps the coral rides up on (the ramp meshes themselves let coral through): a
            // coral-only plane parallel to the belts, ~1.5 cm closer to them than a coral is thick, so the coral is
            // squeezed between floor and belts and has to push the flap up to pass, as on the real intake. It starts
            // past the roller entry, where a floor coral comes in under the front wheels.
            // (under the top half of the flap only: further forward it stood out low and pushed floor coral away)
            var channelFloor = Empty("Channel Floor", intake, F + along * flapLen * 0.725f + toRamps * (0.025f + 0.114f - 0.015f + 0.005f),
                Quaternion.LookRotation(along, -toRamps)).gameObject.AddComponent<BoxCollider>();
            channelFloor.size = new Vector3(0.30f, 0.01f, flapLen * 0.55f);
            channelFloor.includeLayers = coralOnly;
            channelFloor.excludeLayers = ~coralOnly;
            channelFloor.layerOverridePriority = 100;
            channelFloor.sharedMaterial = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(LowFriction);
            // the top roller: a motor roller (1.625" flex wheels)
            var topRoller = MakeRoller(flapT, "Flap Top Roller", T, 0.021f, 0.40f, coralOnly);
            topRoller.GetComponent<Rigidbody>().mass = 0.03f;
            rollers.Add(topRoller);

            // The roller arms rest 6 deg higher than the CAD pose (the green roller sat too low): turned about their
            // pivot with everything on them, before PlayMode gives the joint its rest pose.
            rollerArms.RotateAround(armPivot, root.transform.right, liftsNegative ? -6f : 6f);
            // the coral's path below goes round the rollers where they now are
            var repose = Quaternion.AngleAxis(liftsNegative ? -6f : 6f, root.transform.right);
            F = armPivot + repose * (F - armPivot);
            T = armPivot + repose * (T - armPivot);
            along = repose * along;
            toRamps = repose * toRamps;

            // The side ramps: walls solid to coral, converging from the roller's width to the channel's, so a coral the
            // rollers draw in off-centre slides to the middle (no force does it).
            foreach (var sx in new[] { -1f, 1f })
            {
                var lat = root.transform.right * sx;
                // (they start behind the roller, within its ends: starting ahead of it their edge met a coral's flank and shoved it away)
                var ramp0 = F + along * 0.02f + toRamps * 0.07f + lat * 0.31f;
                var ramp1 = F + along * 0.14f + toRamps * 0.07f + lat * 0.165f;
                var d = ramp1 - ramp0;
                var wall = Empty("Side Ramp", intake, ramp0 + d / 2, Quaternion.LookRotation(d.normalized, toRamps)).gameObject.AddComponent<BoxCollider>();
                wall.size = new Vector3(0.01f, 0.18f, d.magnitude);
                wall.includeLayers = coralOnly;
                wall.excludeLayers = ~coralOnly;
                wall.layerOverridePriority = 100;
                wall.sharedMaterial = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(LowFriction);
            }

            var coralLong = CoralLongAxis();
            var front = F;
            var outDir = (front - intake.position).normalized;
            // gripped between the two columns of 2.25" compliant wheels (shafts at CAD z 0.807; the beam break at 0.758)
            var clawState = Empty("Coral Claw State", claw, P(0, -0.0025f, 0.807f),
                Quaternion.FromToRotation(coralLong, root.transform.forward));
            var across = Quaternion.FromToRotation(coralLong, root.transform.right);
            // The top of the belts: the claw waits over this point at the Ground Intake setpoint.
            var exit = Empty("Coral Intake Exit", intake, T + along * 0.07f, across);
            // The floor coral's way in: squeezed under the front roller (1 cm into the compliant wheels, so it lifts the
            // roller arms), under the flap's top roller and belts, out of the top of the intake into the waiting claw. The
            // intake pulls toward a target that runs just ahead of the coral along it (MissDaisy.UpdateCoralCarrot), so
            // the coral goes round the rollers instead of straight over them, turning as it goes.
            var coralPath = new List<Object>
            {
                Empty("Coral Path 1 Front Roller", intake, F + toRamps * (RollerContactRadius + 0.057f - 0.005f), across),
            };
            var carrot = Empty("Coral Carrot", clawState, clawState.position, clawState.rotation);
            // Pickup zone: right at the front wheels (a coral starts moving only once the wheels touch it); maxDistance
            // only has to cover the carrot's lead.
            // the zone sits on the ramp side of the front roller: a coral lying on top of the intake is not taken (it
            // would be drawn through the roller from above)
            var groundIntake = MakeIntake("Ground Coral Intake", intake, front + outDir * 0.03f + toRamps * 0.04f,
                new Vector3(0.62f, 0.12f, 0.12f), carrot, coral: true, maxDistance: 14f, smooth: false, speed: 105f,
                accuracy: 0.4f); // about a cm: the catch moves it no further than that. The pull eases off into the claw (MissDaisy) so it cannot step past
            var hpIntake = MakeIntake("Claw Coral Intake", claw, P(0, -0.003f, 0.85f),
                new Vector3(0.24f, 0.36f, 0.44f), carrot, coral: true, maxDistance: 16f); // around the mouth, facing the chute: a coral falling down it is caught before it can strike the claw and tumble (the range has to reach the zone's corners, or it is taken and dropped over and over)
            var algaeState = Empty("Algae Claw State", claw, P(0, 0, 1.04f), Quaternion.identity);
            // Grabbed only once the ball touches the wheels: a thin zone on the wheel face (the hold is ~8 cm into them,
            // so the face is ~0.12 m in from the held ball's centre). The framework pulls anything in the zone, even
            // out of range, so the zone itself has to be that small.
            var algaeIntake = MakeIntake("Algae Intake", claw, P(0, 0, 0.93f), new Vector3(0.28f, 0.02f, 0.18f),
                algaeState, coral: false, maxDistance: 7f);
            var eject = Empty("Claw Eject", claw, P(0, 0, 0.84f), Quaternion.LookRotation(root.transform.up, root.transform.forward));
            // The claw's two columns of 2.25" compliant wheels (shafts at CAD x +-0.082, z 0.807, along y): solid to the
            // field, robots and algae; coral passes through them (compliant wheels: solid, a coral let go at stow floated on
            // them). (A piece held in the claw ignores the robot's colliders.)
            var clawWheelParts = new HashSet<MeshRenderer>();
            foreach (var sx in new[] { -1f, 1f })
            {
                var col = Empty("Claw Wheels Collider", claw, P(sx * 0.082f, -0.0075f, 0.807f), Quaternion.LookRotation(P(0, 1, 0) - P(0, 0, 0), root.transform.up))
                    .gameObject.AddComponent<CapsuleCollider>();
                col.direction = 2;      // along its forward: the shaft
                col.radius = 0.0286f;   // 2.25" wheels
                col.height = 0.16f;
                col.excludeLayers = LayerMask.GetMask("Coral", "Robot"); // (with a priority this mask replaces the body's no-Robot one)
                foreach (var part in OnAxis(claw, P(sx * 0.082f, -0.0075f, 0.807f), P(0, 1, 0) - P(0, 0, 0), 0.04f)) clawWheelParts.Add(part);
                col.layerOverridePriority = 100;
                col.sharedMaterial = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(NoBounce);
            }

            // Colliders: bumpers, belly pan, mast, arm, claw, intake, climber.
            var noBounce = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(NoBounce);
            var cols = new GameObject("Colliders").transform;
            cols.SetParent(drivebase, false);
            foreach (var (c, s) in new[]
                     {
                         (B2U(0, -0.391f, 0.086f), new Vector3(0.844f, 0.127f, 0.062f)),
                         (B2U(0, 0.391f, 0.086f), new Vector3(0.844f, 0.127f, 0.062f)),
                         (B2U(0.391f, 0, 0.086f), new Vector3(0.062f, 0.127f, 0.72f)),
                         (B2U(-0.391f, 0, 0.086f), new Vector3(0.062f, 0.127f, 0.72f)),
                         (B2U(0, 0, 0.047f), new Vector3(0.654f, 0.01f, 0.654f)),
                     })
            {
                var b = new GameObject("Box").AddComponent<BoxCollider>();
                b.transform.SetParent(cols, false);
                b.transform.position = root.transform.TransformPoint(Turn(c) + modelOffset);
                b.size = s;
                b.sharedMaterial = noBounce;
            }

            foreach (var t in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var n = t.name;
                if (n.StartsWith("2x1 Elevator Uprights") || n.StartsWith("Stage One Tall") || n.StartsWith("Carriage Left") ||
                    n.StartsWith("Carriage Right") || n.StartsWith("Intake Pivot 2x2") ||
                    n.StartsWith("Climber Arm"))
                {
                    t.gameObject.AddComponent<BoxCollider>().sharedMaterial = noBounce;
                }
            }

            // The intake's frame meets walls and field elements like the rest of the robot, but must not drag on the
            // carpet (same layer as the walls): one box round the deployed frame, built with the intake swung out and
            // its bottom kept above the carpet; game pieces pass through its channel, the rollers stay collision-free.
            {
                var parts = intake.GetComponentsInChildren<MeshRenderer>(true).Where(r =>
                    r.name.StartsWith("Coral Intake Front Tube") || r.name.StartsWith("Roller Holder With") ||
                    r.name.StartsWith("Main Coral Intake 2x1")).ToList();
                var stowedRot = intake.rotation;
                var deploy = sp["Ground Intake"].intakeAngle;
                intake.RotateAround(intake.position, root.transform.right, deploy);
                var fy = WorldCentre(intake, n => n.StartsWith("am-3945")).y;
                if (fy > intake.position.y) // wrong way round: deploy the other way
                {
                    intake.rotation = stowedRot;
                    intake.RotateAround(intake.position, root.transform.right, -deploy);
                }

                var bb = WorldBounds(parts[0]);
                foreach (var r in parts) bb.Encapsulate(WorldBounds(r));
                var floor = root.transform.position.y + ground + 0.02f;
                var min = bb.min;
                min.y = Mathf.Max(min.y, floor);
                var box = new Bounds();
                box.SetMinMax(min, bb.max);
                var frame = Empty("Intake Frame Collider", intake, box.center, root.transform.rotation).gameObject.AddComponent<BoxCollider>();
                frame.size = box.size;
                frame.sharedMaterial = noBounce;
                frame.excludeLayers = LayerMask.GetMask("Coral", "Robot"); // algae bounces off it
                frame.layerOverridePriority = 100; // its exclusions win over a game piece's own include layers
                Debug.Log($"MISSDAISY intake frame collider {box.size:F3} at deployed y {box.min.y:F3}..{box.max.y:F3} (carpet {floor - 0.02f:F3})");
                // The way up past the front roller, as on the real intake: flat against the orienter rollers, up their face
                // (the flap's belts lifting it), over the 2x1 they hang from and into the claw. (Laid along the belts at rest,
                // it ran straight through the 2x1.) Placed in the robot's frame with the intake swung out.
                {
                    var tube = parts.Where(r => r.name.StartsWith("Main Coral Intake 2x1")).Select(WorldBounds).ToList();
                    var tb = tube[0];
                    foreach (var b in tube) tb.Encapsulate(b);
                    Vector3 lo = root.transform.InverseTransformPoint(tb.min), hi = root.transform.InverseTransformPoint(tb.max);
                    float tubeTop = Mathf.Max(lo.y, hi.y), tubeMid = (lo.z + hi.z) / 2f, tubeOut = Mathf.Abs(lo.z) > Mathf.Abs(hi.z) ? lo.z : hi.z;
                    var outSign = Mathf.Sign(tubeOut - tubeMid);
                    const float coralR = 0.057f;
                    var face = tubeMid + outSign * Mathf.Max(0.016f + coralR, Mathf.Abs(tubeOut - tubeMid) + coralR) + outSign * 0.005f; // clear of the pegs and the tube's face
                    var over = tubeTop + coralR + 0.01f;
                    Transform At(string name, float y, float z) => Empty(name, intake, root.transform.TransformPoint(new Vector3(0f, y, z)), across);
                    var entry = coralPath[0];
                    coralPath.Clear();
                    coralPath.Add(entry);
                    coralPath.Add(At("Coral Path 1b Peg Face", tubeTop - 0.06f, face));
                    coralPath.Add(At("Coral Path 2 Up The Face", over, face));
                    coralPath.Add(At("Coral Path 3 Over The Tube", over, tubeMid));
                    Debug.Log($"MISSDAISY coral path: tube top {tubeTop:F3} mid z {tubeMid:F3} face z {face:F3} over y {over:F3}");
                }
                // The pickup zone, placed while deployed: a flat strip on the carpet just in front of the green roller, at a
                // lying coral's height - a coral resting on top of the intake is above it and is not taken.
                {
                    var zone = groundIntake.transform;
                    var fDep = WorldCentre(intake, n => n.StartsWith("am-3945"));
                    var outward = Vector3.ProjectOnPlane(fDep - root.transform.position, Vector3.up).normalized;
                    // Inside the front roller, on the middle of the channel: nothing but the real rollers (friction) and the
                    // side ramps bring a coral here. Only from here is it pulled up to the claw.
                    // (back past the second flap: a coral further in than the zone reached was no longer pulled, and stuck there)
                    zone.SetPositionAndRotation(new Vector3(root.transform.position.x, floor - 0.02f + 0.13f, fDep.z) - outward * 0.12f, root.transform.rotation);
                    zone.GetComponent<BoxCollider>().size = new Vector3(0.24f, 0.26f, 0.18f);
                    // The ramp a coral rides up from the carpet to the channel floor (the real intake's lexan ramps): without
                    // it a coral coming in end-on stopped with its nose against the floor's edge.
                    var rampTop = channelFloor.transform.TransformPoint(0f, 0f, -channelFloor.size.z / 2f);
                    // (its surface starts just under the carpet, ahead of the roller: a coral's blunt end meets the slope, not the plate's edge)
                    var rampFoot = new Vector3(rampTop.x, floor - 0.02f - 0.012f, fDep.z) + outward * 0.03f;
                    var rampDir = rampTop - rampFoot;
                    var ramp = Empty("Channel Ramp", intake, rampFoot + rampDir / 2f, Quaternion.LookRotation(rampDir.normalized, root.transform.up)).gameObject.AddComponent<BoxCollider>();
                    ramp.size = new Vector3(0.62f, 0.01f, rampDir.magnitude); // the roller's whole width: a coral on carpet at its end was just shoved along
                    ramp.includeLayers = coralOnly;
                    ramp.excludeLayers = ~coralOnly;
                    ramp.layerOverridePriority = 100;
                    ramp.sharedMaterial = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(LowFriction);
                }
                intake.rotation = stowedRot;
            }

            // Structure: every sizeable part of the frame, mast, carriage, arm and climber collides with the field and
            // other robots (fitted boxes). Parts round the claw mouth and the intake channel let game pieces through.
            {
                // (diagonal tubes too: a box round one is mostly air - the climber's turnbuckle box stood in the cage's way)
                var skip = new[] { "am-39", "1.625", "Bumper", "Screw", "Bolt", "Nut", "Washer", "Bearing", "Spacer", "Collar",
                                   "Rivet", "Belt", "Pulley", "LED", "Wire", "Tread", "Wheel", "Hook", "Shaft", "Turnbuckle" };
                var added = 0;
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true).ToList())
                {
                    // ("3\" Wheel Supports" are the roller arms' side plates, not wheels)
                    if (r.GetComponent<Collider>() || (skip.Any(k => r.name.Contains(k)) && !r.name.Contains("Support")) || r.transform.IsChildOf(dt.transform)) continue;
                    if (r.transform.IsChildOf(climber)) continue; // nothing may reach past the harpoon's hooks (it spins the cage)
                    if (r.GetComponentInParent<GenericAnimationJoint>()) continue; // spinning or sprung parts stay as they are
                    var size = WorldBounds(r).size;
                    var big = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                    var inIntake = r.transform.IsChildOf(intake);
                    var inClaw = r.transform.IsChildOf(claw);
                    // the intake's gussets, side and attachment plates too, and every part of the claw (at L1 its small
                    // plates and hubs went into the reef with no collider to stop them)
                    if (big < (inClaw ? 0.03f : inIntake ? 0.06f : 0.12f)) continue;
                    var c = r.gameObject.AddComponent<BoxCollider>();
                    c.sharedMaterial = noBounce;
                    // the coral's way through the intake and into the claw; the claw also holds algae sunk into its wheels.
                    // (Solid side plates or front tube trip a coral coming in off-centre or flat on the floor: the path
                    // centres it, as the real ramps do, so the intake lets coral through.)
                    // The intake's side plates and the bars holding it, outside the coral's channel (the 0.62 m roller), are
                    // solid to coral like the rest of the robot.
                    var lateral = Mathf.Abs(root.transform.InverseTransformPoint(WorldBounds(r).center).x);
                    // (not the roller's own end holders: a coral half off the roller stopped dead against one, when the roller
                    // pulling it in along the side ramp is what slides it to the middle)
                    if (inIntake && lateral > 0.26f && !r.transform.IsChildOf(rollerArms))
                    {
                        c.excludeLayers = LayerMask.GetMask("Robot");
                        c.layerOverridePriority = 100;
                    }
                    else if (r.transform.IsChildOf(claw) || inIntake)
                    {
                        // Robot too: with a priority set, this mask wins over the body's own excludeLayers=Robot, and the
                        // part would hit the robot's other bodies
                        c.excludeLayers = r.transform.IsChildOf(claw) && !clawWheelParts.Contains(r) ? LayerMask.GetMask("Algae", "Robot")
                            : r.transform.IsChildOf(claw) ? LayerMask.GetMask("Coral", "Algae", "Robot") : LayerMask.GetMask("Coral", "Robot");
                        c.layerOverridePriority = 100;
                    }
                    added++;
                }

                Debug.Log($"MISSDAISY structure colliders added: {added}");
                // The intake's plates: thin boxes along the mesh's real outline, not one box round each whole plate.
                var plateEdges = 0;
                foreach (var r in intake.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.GetComponent<BoxCollider>()).ToList())
                {
                    var size = Vector3.Scale(r.GetComponent<MeshFilter>().sharedMesh.bounds.size, r.transform.lossyScale);
                    var sorted = new[] { Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z) }.OrderBy(v => v).ToArray();
                    if (sorted[0] > 0.012f || sorted[1] < 0.04f || sorted[2] < 0.06f) continue; // flat plates only
                    plateEdges += PlateEdgeColliders(r);
                }
                Debug.Log($"MISSDAISY plate edge colliders: {plateEdges}");
                // The three orienter rollers: round. Solid to a coral only while it squares up (MissDaisy.cs).
                // (Over a coral's own exclusions, hence the priority.)
                foreach (var r in intake.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name.StartsWith("Orienter Roller")).ToList())
                {
                    var old = r.GetComponent<BoxCollider>();
                    if (old) Object.DestroyImmediate(old);
                    var b = r.GetComponent<MeshFilter>().sharedMesh.bounds;
                    var scale = r.transform.lossyScale;
                    var world = new[] { Mathf.Abs(b.size.x * scale.x), Mathf.Abs(b.size.y * scale.y), Mathf.Abs(b.size.z * scale.z) };
                    var axis = world[0] >= world[1] && world[0] >= world[2] ? 0 : world[1] >= world[2] ? 1 : 2;
                    var cap = r.gameObject.AddComponent<CapsuleCollider>();
                    cap.center = b.center;
                    cap.direction = axis;
                    var localLong = b.size[axis];
                    var localWide = axis == 0 ? b.size.y : b.size.x;
                    cap.height = localLong;
                    cap.radius = localWide / 2f;
                    cap.excludeLayers = LayerMask.GetMask("Coral", "Robot");
                    cap.layerOverridePriority = 200;
                    cap.sharedMaterial = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(LowFriction);
                }
                // the climber-side turnbuckle tube (Seb's playtest addition): a box fitted to the tube itself
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name == "Climber Side Turnbuckle 1x1" && !r.GetComponent<Collider>()))
                    r.gameObject.AddComponent<BoxCollider>().sharedMaterial = noBounce;
            }

            var rootBox = root.GetComponent<BoxCollider>();
            rootBox.center = new Vector3(0, ground + 0.086f, 0);
            rootBox.size = new Vector3(0.844f, 0.127f, 0.844f);

            // Climb, all colliders, from the CAD outline of 341's harpoon (top view, CAD x across / y along / z up):
            //  - two hooks stacked on the top plate share one pivot, the shoulder screw at the tip (0, 0.5245); a second
            //    screw (0, 0.4995) rides in an arc slot in each hook, ~50 deg of inward fold (maxHookTravel clamps it);
            //  - a hook's nose runs from the tip (0, 0.5425) out to its barb (0.085, 0.4925); the barb's back edge goes
            //    back in to (0.035, 0.44);
            //  - the top plate flares from 0.0425 at y 0.44 to 0.0775 at y 0.40, the bottom plate's spike from 0.048 at
            //    y 0.44 to 0.10 at y 0.42, with ears at 0.106 reaching forward to y 0.448.
            // Driven between two cage posts (0.076 off the cage centre, r 0.017), the posts push the noses in, pass the
            // barbs and drop into the pocket between the spike and the ears, held there by the barbs' back edges.
            // Swinging the climber over then pulls the robot up against the hanging cage - only contacts, no joints.
            var tipDir = (P(0, 0.54f, 0.2f) - P(0, 0.44f, 0.2f)).normalized; // along the harpoon, toward the tip
            var plateUp = (P(0, 0, 1) - P(0, 0, 0)).normalized;              // the plates' normal
            var harpoonRot = Quaternion.LookRotation(tipDir, plateUp);
            // The outline below is the deployed harpoon of the CAD it was measured on; the exported model is stowed
            // (harpoon folded in over the drive base). Everything is built in the deployed pose on this frame, which
            // is then turned about the climber's pivot onto the model's top plate.
            var harpoon = Empty("Harpoon Frame", climber, climber.position, climber.rotation);
            var mats = new Dictionary<string, PhysicMaterial>();
            PhysicMaterial Mat(string n) => mats.TryGetValue(n, out var m) ? m : mats[n] = AssetDatabase.LoadAssetAtPath<PhysicMaterial>($"Assets/Materials/PhysicsMaterials/{n}.physicMaterial");
            // 341's plates and funnel, each wall at its own plate's height (CAD outline, deployed pose, x across / y along)
            BoxCollider Edge(Transform parent, string name, Vector2 ea, Vector2 eb, float z0, float z1, string mat)
            {
                var pa = P(ea.x, ea.y, (z0 + z1) / 2);
                var d = P(eb.x, eb.y, (z0 + z1) / 2) - pa;
                var e = Empty(name, parent, pa + d / 2, Quaternion.LookRotation(d.normalized, plateUp)).gameObject.AddComponent<BoxCollider>();
                e.size = new Vector3(0.006f, Mathf.Max(z1 - z0, 0.012f), d.magnitude);
                e.sharedMaterial = Mat(mat);
                return e;
            }
            foreach (var sx in new[] { -1f, 1f })
            {
                // top plate (z 0.208-0.214): from the tip back along the spike, then flaring out
                Edge(harpoon, "Top Plate Edge", new(sx * 0.004f, 0.535f), new(sx * 0.0425f, 0.44f), 0.208f, 0.214f, "LowFrictionMaterial"); // the way in: a post slides along it (all grip, the first post seated jammed the second on the spike)
                Edge(harpoon, "Top Plate Flare", new(sx * 0.0425f, 0.44f), new(sx * 0.0775f, 0.40f), 0.208f, 0.214f, "LowFrictionMaterial");
                Edge(harpoon, "Top Plate Side", new(sx * 0.0775f, 0.40f), new(sx * 0.079f, 0.337f), 0.208f, 0.214f, "SuperGrip");
                // bottom plate (z 0.151-0.157): the spike between the posts, the pocket behind it and the ears outside
                Edge(harpoon, "Bottom Plate Spike", new(sx * 0.004f, 0.526f), new(sx * 0.048f, 0.44f), 0.151f, 0.157f, "LowFrictionMaterial");
                Edge(harpoon, "Bottom Plate Pocket", new(sx * 0.048f, 0.44f), new(sx * 0.10f, 0.42f), 0.151f, 0.157f, "LowFrictionMaterial");
                Edge(harpoon, "Bottom Plate Ear", new(sx * 0.106f, 0.448f), new(sx * 0.106f, 0.42f), 0.151f, 0.157f, "SuperGrip");
                Edge(harpoon, "Bottom Plate Side", new(sx * 0.106f, 0.42f), new(sx * 0.113f, 0.337f), 0.151f, 0.157f, "SuperGrip");
            }
            // 341's polycarbonate funnel wing (z 0.173-0.176, one side): its outline from the CAD hull
            var funnelPts = new Vector2[]
            {
                new(0.08f, 0.326f), new(0.08f, 0.382f), new(0.109f, 0.526f), new(0.134f, 0.557f), new(0.29f, 0.633f),
                new(0.302f, 0.634f), new(0.319f, 0.618f), new(0.319f, 0.462f), new(0.30f, 0.429f), new(0.095f, 0.321f)
            };
            for (var k = 0; k < funnelPts.Length; k++)
                // (not [1]-[2]: that hull edge is not a real wall, it closed the funnel-side pocket 1.7 cm narrower than a
                // post, so the non-elevator side was harder to latch)
                if (k != 1) Edge(harpoon, "Funnel Edge", funnelPts[k], funnelPts[(k + 1) % funnelPts.Length], 0.173f, 0.176f,
                    k < 4 ? "LowFrictionMaterial" : "NoBounce");

            // The latches: 341's two hooks, worked like 5449's clickers (the latch that works in this sim). Each folds
            // in about the shoulder screw at the tip (0, 0.5245) while a post pushes on a thin trigger strip just outside
            // its nose, and swings back behind the post once it has passed. 341's outline: the nose from the tip
            // (0, 0.5425) out to the barb (0.085, 0.4925), the barb's back edge in to (0.035, 0.44). Like 5449's: a
            // frictionless nose the post slides along, a grippy barb that holds it, and both hooks' walls standing the
            // depth of the two hook plates together (z 0.214-0.227) so a tilted post cannot slip between them.
            var climberHooks = new List<GenericAnimationJoint>();
            var hookMeshes = new List<(MeshRenderer mesh, Transform clicker)>();
            const float hook0 = 0.214f, hook1 = 0.227f;
            foreach (var h in climber.GetComponentsInChildren<MeshRenderer>(true)
                         .Where(r => r.name.StartsWith("Top Hook") || r.name.StartsWith("Bottom Hook")).ToList())
            {
                // CAD: the top hook is the -x one, the bottom hook the +x one
                var top = h.name.StartsWith("Top");
                var sx = top ? -1f : 1f;
                var pivot = P(0, 0.5245f, (hook0 + hook1) / 2);
                var barb = P(sx * 0.085f, 0.4925f, (hook0 + hook1) / 2);
                var outward = (P(sx, 0, 0) - P(0, 0, 0)).normalized;
                // the clicker turns about its local Y (+ while a post pushes it): that has to fold the barb in
                var hookRot = harpoonRot;
                if (Vector3.Dot(Vector3.Cross(hookRot * Vector3.up, barb - pivot), -outward) < 0)
                    hookRot = Quaternion.LookRotation(tipDir, -plateUp);
                var holder = Empty(h.name + " Holder", harpoon, pivot, hookRot);
                var clicker = Empty(h.name + " Clicker", holder, pivot, hookRot);
                hookMeshes.Add((h, clicker));
                var nose = Edge(clicker, "Hook Nose", new(0, 0.5425f), new(sx * 0.085f, 0.4925f), hook0, hook1, "NoFriction");
                var barbCol = Edge(clicker, "Hook Barb", new(sx * 0.085f, 0.4925f), new(sx * 0.035f, 0.44f), hook0, hook1, "SuperGrip");
                // the trigger strip the post folds the hook with, 6 mm outside the nose (toward the tip and out)
                var n = new Vector2(sx * 0.05f, 0.085f).normalized * 0.006f;
                var away = Edge(clicker, "Move Away", new(n.x, 0.5425f + n.y), new(sx * 0.085f + n.x, 0.4925f + n.y), hook0, hook1, "NoFriction");
                away.isTrigger = true;
                // A thicker strip (2 cm, was 6 mm). The hook only folds while a post overlaps the strip, and folding moves
                // the strip away from the post, so it settled at ~20 deg with the barb tip still in the post's way (the
                // post jammed on the tip and never seated). The real hook keeps folding until the post has passed it.
                // Seb's hand-fitted hook outline from the playtest (clicker frame): the barb turned to the real hook's
                // back edge, a second barb face behind it, the nose and trigger trimmed to the hook
                void Pose(Component c, Vector3 pos, Quaternion rot) => c.transform.SetLocalPositionAndRotation(pos, rot);
                var barb2 = Empty("Hook Barb (1)", clicker, clicker.position, clicker.rotation).gameObject.AddComponent<BoxCollider>();
                barb2.size = new Vector3(0.006f, 0.013f, 0.0725f);
                barb2.sharedMaterial = Mat("SuperGrip");
                // (the same for both hooks, the bottom hook's: they are mirror parts in mirrored frames. The top hook's own fit
                // had its barb 16 deg further round and a shorter trigger: it pushed a post back out as it shut.)
                Pose(barbCol, new Vector3(0.0526f, 0f, -0.0535f), new Quaternion(0f, 0.9954021f, 0f, -0.09578451f));
                Pose(barb2, new Vector3(0.049f, 0f, -0.0404f), new Quaternion(0f, 0.79996604f, 0f, -0.60004526f));
                nose.size = new Vector3(0.006f, 0.013f, 0.09263538f); nose.center = new Vector3(0f, 0f, -0.0029900374f);
                away.size = new Vector3(0.02f, 0.013f, 0.10432891f); away.center = new Vector3(0f, 0f, 0.0028567913f);
                var joint = clicker.gameObject.AddComponent<GenericAnimationJoint>();
                var jso = new SerializedObject(joint);
                jso.FindProperty("moveAwayCollider").objectReferenceValue = away;
                jso.ApplyModifiedPropertiesWithoutUndo();
                climberHooks.Add(joint);
            }

            // Scoring: posts in the pockets between the spike and the ears, behind the barbs.
            var scoring = Empty("Cage Scoring Trigger", harpoon, P(0, 0.45f, 0.19f), harpoonRot)
                .gameObject.AddComponent<BoxCollider>(); // centred: OverlapBoxBounds ignores BoxCollider.center
            scoring.isTrigger = true;
            scoring.size = new Vector3(0.24f, 0.10f, 0.08f);

            // onto the model: the CAD top plate's centre (0, 0.4365, 0.211) turned about the pivot to the model's
            {
                var lat = (P(1, 0, 0) - P(0, 0, 0)).normalized;
                var plate = climber.GetComponentsInChildren<MeshRenderer>(true).First(r => r.name.StartsWith("Top Plate"));
                var turn = Vector3.SignedAngle(Vector3.ProjectOnPlane(P(0, 0.4365f, 0.211f) - climber.position, lat),
                    Vector3.ProjectOnPlane(WorldBounds(plate).center - climber.position, lat), lat);
                harpoon.RotateAround(climber.position, lat, turn);
                foreach (var (mesh, clicker) in hookMeshes) mesh.transform.SetParent(clicker, true); // the hooks turn on their pivots
                Debug.Log($"MISSDAISY harpoon colliders turned {turn:F1} deg onto the model (stowed)");
            }
            var climb = new SerializedObject(root.GetComponent<Games.Reefscape.Scoring.Scorers.ClimbScorer>());
            climb.FindProperty("cageScoringTrigger").objectReferenceValue = scoring;
            climb.ApplyModifiedPropertiesWithoutUndo();

            var leds = root.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name.StartsWith("30\" LED strip"))
                .Select(r => r.gameObject).ToArray();
            var led = new SerializedObject(root.GetComponents<Component>().First(c => c.GetType().Name == "LedStripController"));
            var ledArr = led.FindProperty("leds");
            ledArr.arraySize = leds.Length;
            for (var i = 0; i < leds.Length; i++) ledArr.GetArrayElementAtIndex(i).objectReferenceValue = leds[i];
            led.ApplyModifiedPropertiesWithoutUndo();

            // Robot script.
            var so = new SerializedObject(md);
            so.FindProperty("elevator").objectReferenceValue = elev;
            so.FindProperty("shoulder").objectReferenceValue = shoulderJ;
            so.FindProperty("wrist").objectReferenceValue = wristJ;
            so.FindProperty("intakePivot").objectReferenceValue = intakeJ;
            so.FindProperty("climber").objectReferenceValue = climberJ;
            SetArray(so, "intakeRollers", rollers.Cast<Object>().ToList());
            SetArray(so, "intakeRollerVisuals", rollerVisuals.Cast<Object>().ToList());
            SetArray(so, "climberHooks", climberHooks.Cast<Object>().ToList());
            SetArray(so, "coralPath", coralPath);
            so.FindProperty("rollerArmsBody").objectReferenceValue = armsRb;
            so.FindProperty("intakeFlapBody").objectReferenceValue = flapRb;
            so.FindProperty("coralCarrot").objectReferenceValue = carrot;
            // Sounds, from the shared robot audio: the rollers (floor intake or claw) and the climb hooks latching
            AudioClip Clip(string n) => AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Imports/Audio/" + n);
            // (as much as other robots have, e.g. 9496: the elevator, one roller loop, a latch click - no motor whine
            // on every joint)
            so.FindProperty("intakeRollerClip").objectReferenceValue = Clip("1986Rollers.wav");
            so.FindProperty("hookLatchClip").objectReferenceValue = Clip("2910ClimbLatch.wav");
            so.FindProperty("coralPathAxis").vector3Value = CoralLongAxis();
            so.FindProperty("clawEntry").floatValue = 0.04f; // (at 8 cm the last leg into the claw cut through the 2x1)
            // Max is the cruise speed in rad/s (the error is in degrees); a modest kP eases the last ~10 deg instead of
            // slamming into the setpoint.
            SetPid(so, "shoulderPid", 0.8f, 0f, 0.02f, 14f); // held to 341's MotionMagic profile in MissDaisy (a Max of 7 capped it at 7 rad/s)
            SetPid(so, "wristPid", 0.5f, 0f, 0f, 14f);
            SetPid(so, "intakePid", 1.0f, 0f, 0f, 9f); // 9 rad/s top, easing over the last ~9 deg
            SetPid(so, "climberPid", 0.3f, 0f, 0.01f, 1.6f); // the pull: steady and heavy (3 rad/s snapped the robot up, 1.2 dragged)
            SetPid(so, "climberDeployPid", 0.5f, 0f, 0.01f, 4f); // deploying, unloaded: quick
            foreach (var (field, asset) in new[]
                     {
                         ("stow", "Stow"), ("groundIntake", "Ground Intake"), ("hpIntake", "HP Intake"), ("l1", "L1"),
                         ("l2", "L2"), ("l3", "L3"), ("l4", "L4"), ("groundAlgae", "Ground Algae"), ("lollipop", "Lollipop"),
                         ("lowAlgae", "Low Algae"), ("algaeHolding", "Algae Holding"), ("highAlgae", "High Algae"), ("processor", "Processor"),
                         ("l2Score", "L2 Score"), ("l3Score", "L3 Score"), ("l4Score", "L4 Score"),
                         ("flingStart", "Fling Start"), ("flingEnd", "Fling End"), ("climbPrep", "Climb Prep"), ("climbed", "Climbed")
                     })
                so.FindProperty(field).objectReferenceValue = sp[asset];
            so.FindProperty("groundCoralIntake").objectReferenceValue = groundIntake;
            so.FindProperty("hpCoralIntake").objectReferenceValue = hpIntake;
            so.FindProperty("algaeIntake").objectReferenceValue = algaeIntake;
            // Floor coral is pulled past the front roller and up the belts (lifting the flap) to the top of the intake,
            // then handed up into the waiting claw; no smoothing: it locks on.
            SetState(so, "clawCoralState", "Coral Claw", 2, 60f, clawState, false, 360f); // turns smoothly (0 snaps it)
            SetState(so, "algaeState", "Algae Claw", 1, 20f, algaeState, false, 180f);
            so.FindProperty("clawEject").objectReferenceValue = eject;
            // Claw compliant wheels spin on their own shafts (claw local Z), the two sides in opposite senses.
            var clawWheels = claw.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name.StartsWith("am-3949")).ToList()
                .Select(r =>
                {
                    var c = WorldBounds(r).center;
                    var sense = claw.InverseTransformPoint(c).x > 0 ? 1f : -1f;
                    return (Object)Spin(claw, r.name + " Spin", c, claw.forward * sense, n => n == r.name, r);
                }).ToList();
            SetArray(so, "clawWheels", clawWheels);
            {
                var wheels = clawWheels.Cast<GenericAnimationJoint>().Select(w => w.transform.position).ToList();
                var l = wheels.OrderBy(w => Vector3.Dot(w, claw.right)).First();
                var rgt = wheels.OrderBy(w => Vector3.Dot(w, claw.right)).Last();
                var mid = (l + rgt) * 0.5f;
                var half = Mathf.Abs(Vector3.Dot(rgt - l, claw.right)) * 0.5f;
                var wheelR = 0.0508f; // 4" compliant wheels
                var reach = Mathf.Sqrt(Mathf.Max(0.01f, Mathf.Pow(0.2f + wheelR - 0.08f, 2) - half * half));
                var outward = Vector3.ProjectOnPlane(algaeState.position - mid, claw.right).normalized;
                algaeState.position = mid + outward * reach;
                Debug.Log($"MISSDAISY algae held {reach:F3} m out from the claw wheels (half spacing {half:F3})");
            }

            var bumpers = root.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name.Contains("Bumper Noodles")).ToArray();
            var bp = so.FindProperty("bumperMeshes");
            bp.arraySize = bumpers.Length;
            for (var i = 0; i < bumpers.Length; i++) bp.GetArrayElementAtIndex(i).objectReferenceValue = bumpers[i];
            so.FindProperty("blueBumperMaterial").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Bumper Materials/Blue Bumpers.mat");
            so.FindProperty("redBumperMaterial").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<Material>(Dir + "/Bumper Materials/Red Bumpers.mat");
            so.ApplyModifiedPropertiesWithoutUndo();

            ApplyPalette(root, dt.transform, bumpers);
            // The team number on all four bumpers. The model's numbers are faces on the side bumpers' second material slot,
            // following the noodle's curve; a side without one gets a copy of that same geometry turned round to it.
            {
                var have = new HashSet<Vector2Int>();
                MeshRenderer source = null;
                var sourceSlot = 0;
                Vector2Int Side(Vector3 c) => Mathf.Abs(c.x) > Mathf.Abs(c.z) ? new Vector2Int((int)Mathf.Sign(c.x), 0) : new Vector2Int(0, (int)Mathf.Sign(c.z));
                foreach (var b in bumpers)
                {
                    var mesh = b.GetComponent<MeshFilter>().sharedMesh;
                    for (var i = 1; i < mesh.subMeshCount; i++)
                    {
                        have.Add(Side(root.transform.InverseTransformPoint(b.transform.TransformPoint(mesh.GetSubMesh(i).bounds.center))));
                        source ??= b;
                        sourceSlot = i;
                    }
                }
                Debug.Log($"MISSDAISY bumper numbers in the model on sides: {string.Join(" ", have)}");
                if (source)
                {
                    var mesh = source.GetComponent<MeshFilter>().sharedMesh;
                    var tris = mesh.GetTriangles(sourceSlot);
                    var used = tris.Distinct().ToArray();
                    var toRoot = root.transform.worldToLocalMatrix * source.transform.localToWorldMatrix;
                    var verts = mesh.vertices;
                    var normals = mesh.normals;
                    var pts = used.Select(k => toRoot.MultiplyPoint3x4(verts[k])).ToArray();
                    var mid = pts.Aggregate(Vector3.zero, (a, v) => a + v) / pts.Length;
                    var from = Side(mid);
                    var fromDir = new Vector3(from.x, 0f, from.y);
                    float Face(Vector3 dir) => bumpers.Max(b =>
                    {
                        var m = b.GetComponent<MeshFilter>().sharedMesh.bounds;
                        var toRootB = root.transform.worldToLocalMatrix * b.transform.localToWorldMatrix;
                        return new[] { -1f, 1f }.SelectMany(x => new[] { -1f, 1f }.SelectMany(y => new[] { -1f, 1f }.Select(z =>
                            Vector3.Dot(toRootB.MultiplyPoint3x4(m.center + Vector3.Scale(m.extents, new Vector3(x, y, z))), dir)))).Max();
                    });
                    // (a recessed number comes out to the face, just proud of it)
                    var recess = Face(fromDir) - pts.Max(v => Vector3.Dot(v, fromDir));
                    foreach (var side in new[] { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down }.Where(d => !have.Contains(d)))
                    {
                        var dir = new Vector3(side.x, 0f, side.y);
                        var turn = Quaternion.FromToRotation(fromDir, dir);
                        var shift = dir * (Face(dir) - Face(fromDir) + Mathf.Max(recess, 0f) + 0.001f);
                        var index = new Dictionary<int, int>();
                        for (var k = 0; k < used.Length; k++) index[used[k]] = k;
                        var path = $"{Dir}/Bumper Number {side.x} {side.y}.asset";
                        var copy = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                        if (!copy)
                        {
                            copy = new Mesh();
                            AssetDatabase.CreateAsset(copy, path);
                        }
                        copy.Clear();
                        copy.name = $"Bumper Number {side}";
                        var lateral = Vector3.ProjectOnPlane(new Vector3(mid.x, 0f, mid.z), fromDir); // centred on the side
                        copy.vertices = pts.Select(v => turn * (v - lateral) + shift).ToArray();
                        copy.normals = used.Select(k => turn * toRoot.MultiplyVector(normals[k]).normalized).ToArray();
                        copy.triangles = tris.Select(k => index[k]).ToArray();
                        copy.RecalculateBounds();
                        EditorUtility.SetDirty(copy);
                        var go = new GameObject($"Bumper Number {side}");
                        go.transform.SetParent(root.transform, false);
                        go.AddComponent<MeshFilter>().sharedMesh = copy;
                        var mr = go.AddComponent<MeshRenderer>();
                        mr.sharedMaterial = source.sharedMaterials[sourceSlot];
                        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    }
                }
            }
            MissDaisyPanels.Add(root.transform, v => P(v.x, v.y, v.z)); // 341's art panels (visual only; after the palette)
            // The limelight on top of the elevator (the HP one): dark gray, turned round to face the other way
            {
                var top = root.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name == "mesh30420_mesh");
                var darkGray = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/DarkGray.mat");
                if (top)
                {
                    top.sharedMaterials = Enumerable.Repeat(darkGray, top.sharedMaterials.Length).ToArray();
                    top.transform.RotateAround(WorldBounds(top).center, root.transform.up, 180f);
                }
            }

            // every object of the robot on the Robot layer, the drivetrain included (other layers let game pieces
            // collide with the robot's own parts and break intaking)
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = robotLayer;
            root.layer = robotLayer;

            PrefabUtility.UnloadPrefabContents(template);
            return root;
        }

        // Pivots of the mechanism nodes in the CAD (Blender, m): the same table as export_unity.py.
        private static readonly (string node, Vector3 cad)[] Pivots =
        {
            ("Intake", new(0f, -0.216f, 0.1525f)), ("Stage1", new(-0.19f, 0f, 0.12f)), ("Carriage", new(-0.19f, 0f, 0.26f)),
            ("Arm", new(0f, 0f, 0.3175f)), ("Claw", new(0f, -0.0025f, 0.47f)), ("Climber", new(0f, 0.2715f, 0.1825f)),
        };

        // Unity's axis handling of a Blender FBX differs with the import settings: pick the axis-aligned turn of the model
        // root that puts every mechanism pivot where the CAD -> Unity mapping (B2U) says, and refuse to build otherwise.
        private static void OrientModel(Transform model, Transform root)
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
            Debug.Log($"MISSDAISY model root turned {best.rot.eulerAngles}, pivot error {best.err:F4} m");
            if (best.err > 0.01f) throw new System.Exception("MISSDAISY no model orientation matches the CAD pivots");
        }

        // ------------------------------------------------------------------ materials (house palette)

        private const string Mats = "Assets/Materials/";

        // Shared robot materials the other mods paint with; the anodized blacks are copied next to this robot so the
        // mod does not depend on another mod's bundle.
        private static readonly (string path, Color c)[] Palette =
        {
            (Mats + "Aluminum.mat", new Color(0.80f, 0.81f, 0.83f)), (Mats + "DarkSteel.mat", new Color(0.42f, 0.42f, 0.42f)),
            (Mats + "LightBlack.mat", new Color(0.18f, 0.18f, 0.18f)),
            (Mats + "Yellow.mat", new Color(1f, 0.78f, 0f)), // 341's printed parts are bright yellow on the robot (Mats + "REV 40A.mat", new Color(1f, 0.6f, 0f)),
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

        // CAD colour of an imported material: the Onshape names carry it ("r_g_b_x_transparency"), library finishes
        // keep it in the material colour.
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

        private static Material Paint(Material m, string part)
        {
            if (!m) return m;
            // Real-robot colours (team photos): yellow claw wheels, rope pulleys and spool; green intake wheels.
            if (part.StartsWith("am-3949") || part.Contains("R4 Pull") || part.StartsWith("Spool Tube") ||
                part.StartsWith("Spool End Cap"))
                return Mat(Mats + "Yellow.mat");
            if (part.StartsWith("am-3945") || part.Contains("Flex Wheel"))
                return Mat(Mats + "GreenFlexWheel.mat");
            var (c, clear) = CadColour(m);
            if (part.StartsWith("X2 Molded Wheel") || part.Contains("Tread")) return Mat(Mats + "BlackFlexWheel.mat");
            Color.RGBToHSV(c, out _, out var sat, out var val);
            if (clear > 0.05f) return Mat(val < 0.3f ? Mats + "SmokedPolycarb.mat" : Mats + "Polycarb.mat");
            if (sat < 0.15f)
            {
                if (val >= 0.55f) return Mat(Mats + "Aluminum.mat");
                if (val >= 0.36f) return Mat(Mats + "DarkSteel.mat");
                if (val >= 0.15f) return AnodizedBlack(false);
                return AnodizedBlack(true);
            }

            var best = Palette.OrderBy(p => Mathf.Pow(p.c.r - c.r, 2) + Mathf.Pow(p.c.g - c.g, 2) + Mathf.Pow(p.c.b - c.b, 2)).First();
            return Mat(best.path);
        }

        private static void ApplyPalette(GameObject root, Transform skip, MeshRenderer[] keep)
        {
            var count = new Dictionary<string, int>();
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.transform.IsChildOf(skip) || keep.Contains(r)) continue;
                var part = System.Text.RegularExpressions.Regex.Replace(r.name, @"(\.\d{3})+$", "");
                var mats = r.sharedMaterials.Select(m => Paint(m, part)).ToArray();
                r.sharedMaterials = mats;
                foreach (var m in mats.Where(m => m)) count[m.name] = count.TryGetValue(m.name, out var n) ? n + 1 : 1;
            }

            Debug.Log("MISSDAISY palette " + string.Join(", ", count.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}")));
        }

        private static void Square(Transform node, Transform root)
        {
            if (Quaternion.Angle(node.rotation, root.rotation) < 0.01f) return;
            var kids = Enumerable.Range(0, node.childCount).Select(node.GetChild).ToList();
            foreach (var k in kids) k.SetParent(null, true);
            node.rotation = root.rotation;
            foreach (var k in kids) k.SetParent(node, true);
        }

        private static GenericJoint MakeJoint(GameObject go, Rigidbody connected, char angular = ' ', bool linearY = false,
            bool climber = false)
        {
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 1;
            rb.drag = 0;
            rb.angularDrag = 0.05f;
            rb.useGravity = true;
            rb.excludeLayers = LayerMask.GetMask("Robot");
            // Owner rule: the automatic inertia tensor stays on for every joint body except the climber's, which gets a
            // steady (1,1,1) tensor and mass 2 (as 1690's ClimberPivot).
            if (climber)
            {
                rb.mass = 2;
                rb.automaticInertiaTensor = false;
                rb.inertiaTensor = Vector3.one;
                rb.inertiaTensorRotation = Quaternion.identity;
            }
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
            cj.angularZMotion = ConfigurableJointMotion.Locked;
            return go.AddComponent<GenericJoint>();
        }

        // Physics roller: a capsule on a motor-driven HingeJoint that collides with nothing (no dragging the robot).
        // A sprung one-way hinge (surgical tubing): a ConfigurableJoint free only about the body's local X (times
        // liftSign, so + is "lift"), limited to 0..maxLift deg, its spring drive aimed preload deg past the rest stop.
        // (HingeJoints nested in the jointed intake bodies read NaN and stay rigid; the framework's joints are these.)
        private static void SprungHinge(GameObject go, Rigidbody connected, float liftSign, float maxLift, float spring,
            float damper, float preload, float restLift = 0f, float pivotDrag = 3f)
        {
            // the pivot's friction damps the swing (a flap that flops about in the air looks wrong)
            go.GetComponent<Rigidbody>().angularDrag = pivotDrag;
            var cj = go.AddComponent<ConfigurableJoint>();
            cj.connectedBody = connected;
            cj.anchor = Vector3.zero;
            cj.axis = Vector3.right * -liftSign; // a ConfigurableJoint turns the other way about its axis (PlayMode: with +liftSign both drooped into the channel)
            cj.secondaryAxis = Vector3.up;
            cj.xMotion = cj.yMotion = cj.zMotion = ConfigurableJointMotion.Locked;
            cj.angularYMotion = cj.angularZMotion = ConfigurableJointMotion.Locked;
            cj.angularXMotion = ConfigurableJointMotion.Limited;
            // the joint's X limits: low <= 0 <= high; lift is the high side (the drive's target is past the low stop)
            cj.lowAngularXLimit = new SoftJointLimit { limit = restLift }; // the rest stop (gravity holds it there)
            cj.highAngularXLimit = new SoftJointLimit { limit = maxLift };
            cj.angularXDrive = new JointDrive { positionSpring = spring, positionDamper = damper, maximumForce = float.MaxValue };
            cj.targetRotation = Quaternion.AngleAxis(-preload, Vector3.right); // aims preload deg below the rest stop (PlayMode: + rested the flap on its 40 deg stop)
        }

        // Replaces a flat part's box with thin boxes along its outline, each turned to match its edge (the convex outline of the
        // mesh in its own plane, simplified to about 4 mm), and fills it in with boxes laid along its longest edge over where
        // the plate really is (its own faces, notches and holes left out). Returns how many it made.
        private static int PlateEdgeColliders(MeshRenderer plate)
        {
            var mesh = plate.GetComponent<MeshFilter>().sharedMesh;
            var verts = mesh.vertices;
            if (verts.Length < 3) return 0;
            var old = plate.GetComponent<BoxCollider>();
            var size = mesh.bounds.size;
            // the flat axis in the mesh's own frame; u, v span the plate
            var thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
            int u = (thin + 1) % 3, v = (thin + 2) % 3;
            var scale = Mathf.Max(1e-6f, Mathf.Abs(plate.transform.lossyScale[u]));
            var pts = verts.Select(p => new Vector2(p[u], p[v])).Distinct().OrderBy(p => p.x).ThenBy(p => p.y).ToList();
            float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            var hull = new List<Vector2>();
            foreach (var p in pts)
            {
                while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            var lower = hull.Count + 1;
            for (var i = pts.Count - 2; i >= 0; i--)
            {
                while (hull.Count >= lower && Cross(hull[^2], hull[^1], pts[i]) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(pts[i]);
            }
            hull.RemoveAt(hull.Count - 1);
            if (hull.Count < 3) return 0;
            // drop outline points that hardly change it
            var tolerance = 0.004f / scale;
            for (var changed = true; changed && hull.Count > 3;)
            {
                changed = false;
                for (var i = 0; i < hull.Count; i++)
                {
                    Vector2 a = hull[(i + hull.Count - 1) % hull.Count], p = hull[i], b = hull[(i + 1) % hull.Count];
                    if (Mathf.Abs(Cross(a, b, p)) / Mathf.Max((b - a).magnitude, 1e-6f) >= tolerance) continue;
                    hull.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
            var width = 0.012f / scale;
            var depth = Mathf.Max(size[thin], 0.006f / scale);
            var mid = mesh.bounds.center[thin];
            var centre2 = hull.Aggregate(Vector2.zero, (acc, p) => acc + p) / hull.Count;
            Vector3 To3(Vector2 p, float along) { var w = Vector3.zero; w[u] = p.x; w[v] = p.y; w[thin] = along; return w; }
            var made = 0;
            for (var i = 0; i < hull.Count; i++)
            {
                Vector2 a = hull[i], b = hull[(i + 1) % hull.Count];
                var edge = b - a;
                if (edge.magnitude < 0.01f / scale) continue;
                var inward = new Vector2(-edge.y, edge.x).normalized;
                if (Vector2.Dot(inward, centre2 - a) < 0f) inward = -inward;
                var go = new GameObject("Plate Edge");
                go.transform.SetParent(plate.transform, false);
                go.transform.localPosition = To3((a + b) / 2f + inward * width / 2f, mid);
                go.transform.localRotation = Quaternion.LookRotation(To3(edge.normalized, 0f), To3(Vector2.zero, 1f));
                go.layer = plate.gameObject.layer;
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(width, depth, edge.magnitude);
                if (old)
                {
                    box.sharedMaterial = old.sharedMaterial;
                    box.excludeLayers = old.excludeLayers;
                    box.includeLayers = old.includeLayers;
                    box.layerOverridePriority = old.layerOverridePriority;
                }
                made++;
            }
            // filled in: a grid along the longest outline edge, cells under the plate's faces, merged into rectangles
            var longest = 0;
            for (var i = 1; i < hull.Count; i++)
                if ((hull[(i + 1) % hull.Count] - hull[i]).sqrMagnitude > (hull[(longest + 1) % hull.Count] - hull[longest]).sqrMagnitude) longest = i;
            var e1 = (hull[(longest + 1) % hull.Count] - hull[longest]).normalized;
            var e2 = new Vector2(-e1.y, e1.x);
            var tris = mesh.triangles;
            var flat = new List<(Vector2 a, Vector2 b, Vector2 c)>();
            for (var k = 0; k < tris.Length; k += 3)
            {
                Vector2 P(int i) => new(Vector2.Dot(new Vector2(verts[i][u], verts[i][v]), e1), Vector2.Dot(new Vector2(verts[i][u], verts[i][v]), e2));
                var t = (P(tris[k]), P(tris[k + 1]), P(tris[k + 2]));
                if (Mathf.Abs(Cross(t.Item1, t.Item2, t.Item3)) > 1e-10f) flat.Add(t);
            }
            float lo1 = flat.Min(t => Mathf.Min(t.a.x, Mathf.Min(t.b.x, t.c.x))), hi1 = flat.Max(t => Mathf.Max(t.a.x, Mathf.Max(t.b.x, t.c.x)));
            float lo2 = flat.Min(t => Mathf.Min(t.a.y, Mathf.Min(t.b.y, t.c.y))), hi2 = flat.Max(t => Mathf.Max(t.a.y, Mathf.Max(t.b.y, t.c.y)));
            bool Inside(Vector2 q, (Vector2 a, Vector2 b, Vector2 c) t)
            {
                float d1 = Cross(t.a, t.b, q), d2 = Cross(t.b, t.c, q), d3 = Cross(t.c, t.a, q);
                return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
            }
            var rects = new List<(float x, float y, float w, float h)>();
            for (var cell = 0.005f / scale; cell < 0.05f / scale; cell *= 1.5f)
            {
                int nx = Mathf.CeilToInt((hi1 - lo1) / cell), ny = Mathf.CeilToInt((hi2 - lo2) / cell);
                if (nx * ny > 40000) continue;
                var on = new bool[nx, ny];
                foreach (var t in flat)
                {
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(t.a.x, Mathf.Min(t.b.x, t.c.x)) - lo1) / cell)), x1 = Mathf.Min(nx - 1, Mathf.FloorToInt((Mathf.Max(t.a.x, Mathf.Max(t.b.x, t.c.x)) - lo1) / cell));
                    int y0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(t.a.y, Mathf.Min(t.b.y, t.c.y)) - lo2) / cell)), y1 = Mathf.Min(ny - 1, Mathf.FloorToInt((Mathf.Max(t.a.y, Mathf.Max(t.b.y, t.c.y)) - lo2) / cell));
                    for (var x = x0; x <= x1; x++)
                        for (var y = y0; y <= y1; y++)
                            if (!on[x, y] && Inside(new Vector2(lo1 + (x + 0.5f) * cell, lo2 + (y + 0.5f) * cell), t)) on[x, y] = true;
                }
                rects.Clear();
                var used = new bool[nx, ny];
                for (var y = 0; y < ny; y++)
                    for (var x = 0; x < nx; x++)
                    {
                        if (!on[x, y] || used[x, y]) continue;
                        var w = 1;
                        while (x + w < nx && on[x + w, y] && !used[x + w, y]) w++;
                        var h = 1;
                        while (y + h < ny && Enumerable.Range(x, w).All(i => on[i, y + h] && !used[i, y + h])) h++;
                        for (var i = x; i < x + w; i++)
                            for (var j = y; j < y + h; j++) used[i, j] = true;
                        rects.Add((lo1 + x * cell, lo2 + y * cell, w * cell, h * cell));
                    }
                if (rects.Count <= 24) break;
            }
            foreach (var r in rects)
            {
                var go = new GameObject("Plate Fill");
                go.transform.SetParent(plate.transform, false);
                go.transform.localPosition = To3(e1 * (r.x + r.w / 2f) + e2 * (r.y + r.h / 2f), mid);
                go.transform.localRotation = Quaternion.LookRotation(To3(e1, 0f), To3(Vector2.zero, 1f));
                go.layer = plate.gameObject.layer;
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(r.h, depth, r.w);
                if (old)
                {
                    box.sharedMaterial = old.sharedMaterial;
                    box.excludeLayers = old.excludeLayers;
                    box.includeLayers = old.includeLayers;
                    box.layerOverridePriority = old.layerOverridePriority;
                }
                made++;
            }
            if (made > 0 && old) Object.DestroyImmediate(old);
            return made;
        }

        private static GenericRoller MakeRoller(Transform parent, string name, Vector3 centre, float radius, float length, int touches = 0, string material = Grip)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(centre, parent.root.rotation);
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = 0.2f;
            rb.includeLayers = touches;
            rb.excludeLayers = ~touches; // only what it is told to touch (no dragging the robot along the carpet)
            var hj = go.AddComponent<HingeJoint>();
            hj.connectedBody = parent.GetComponent<Rigidbody>();
            hj.axis = Vector3.right;
            hj.useMotor = true;
            hj.motor = new JointMotor { targetVelocity = 0, force = 3.402823e+38f, freeSpin = false };
            var cap = go.AddComponent<CapsuleCollider>();
            cap.direction = 0;
            cap.radius = radius;
            cap.height = length;
            cap.includeLayers = touches;
            cap.excludeLayers = ~touches;
            cap.sharedMaterial = AssetDatabase.LoadAssetAtPath<PhysicMaterial>(material); // (all-grip belts locked a coral off-centre: it could not slide to the middle)
            return go.AddComponent<GenericRoller>();
        }

        // Visual roller: the matching meshes spin about `axis` through `centre` (GenericAnimationJoint.VelocityRoller
        // turns its local X).
        private static GenericAnimationJoint Spin(Transform parent, string name, Vector3 centre, Vector3 axis,
            System.Func<string, bool> part, MeshRenderer only = null, Transform search = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(centre, Quaternion.FromToRotation(Vector3.right, axis.normalized));
            var parts = only ? new List<MeshRenderer> { only } :
                (search ? search : parent).GetComponentsInChildren<MeshRenderer>(true).Where(r => part(r.name)).ToList();
            if (parts.Count == 0) Debug.LogWarning($"MISSDAISY no parts for {name}");
            foreach (var r in parts) r.transform.SetParent(go.transform, true);
            return go.AddComponent<GenericAnimationJoint>();
        }

        // Meshes whose bounds centre lies within `r` of the line through `centre` along `axis` (parts on one shaft).
        private static List<MeshRenderer> OnAxis(Transform search, Vector3 centre, Vector3 axis, float r)
        {
            return search.GetComponentsInChildren<MeshRenderer>(true).Where(m =>
                Vector3.ProjectOnPlane(WorldBounds(m).center - centre, axis).magnitude < r).ToList();
        }

        private static Vector3 WorldCentre(Transform parent, System.Func<string, bool> part)
        {
            var parts = parent.GetComponentsInChildren<MeshRenderer>(true).Where(r => part(r.name)).ToList();
            var bb = WorldBounds(parts[0]);
            foreach (var r in parts) bb.Encapsulate(WorldBounds(r));
            return bb.center;
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
            // The framework corrects the piece's velocity by intakeForce x 0.0254 of the error each step: above 1 that
            // overshoots and grows (a coral could cross the intake in two frames and tunnel through the flap), so the
            // coral pull stays under it.
            so.FindProperty("intakeForce").floatValue = coral ? 30f : 196.9f;
            so.FindProperty("usePlanarTolerancing").boolValue = false; // distance mode: the claw target turns with the arm
            so.FindProperty("boxExtents").vector3Value = coral ? Vector3.zero : new Vector3(5, 3, 3);
            so.FindProperty("maxDistance").floatValue = maxDistance > 0 ? maxDistance : coral ? 2.76f : 20f; // algae is grabbed while driving in
            so.FindProperty("accuracy").floatValue = accuracy > 0 ? accuracy : coral ? 0.53f : 3f; // a carpet ball settles ~2 in off the claw target
            so.FindProperty("roationForce").floatValue = coral ? 1.5f : 0f;   // squares the coral up gently, no wild spin
            so.FindProperty("maxRotationSpeed").floatValue = coral ? 3f : 0f;   // rad/s: turns visibly, no snap
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
            var menu = new GameObject("MainMenu341");
            menu.AddComponent<Animator>();
            var copy = Object.Instantiate(robot, menu.transform);
            copy.name = "341";
            var dt = copy.transform.Find("Drivetrain");
            if (dt) Object.DestroyImmediate(dt.gameObject);
            // Scripts first, then joints, then bodies and colliders: only meshes stay.
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
            var path = Dir + "/MissDaisy.asset";
            var meta = AssetDatabase.LoadAssetAtPath<Games.Reefscape.Robots.ReefscapeRobotMetadataSO>(path);
            if (!meta)
            {
                meta = ScriptableObject.CreateInstance<Games.Reefscape.Robots.ReefscapeRobotMetadataSO>();
                AssetDatabase.CreateAsset(meta, path);
            }

            var so = new SerializedObject(meta);
            so.FindProperty("teamNumber").intValue = 341;
            so.FindProperty("teamName").stringValue = "Miss Daisy";
            so.FindProperty("robotPrefab").objectReferenceValue = robot;
            so.FindProperty("robotPreloadGamePiece").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(CoralPrefab);
            so.FindProperty("mainMenuPrefab").objectReferenceValue = menu;
            so.FindProperty("hasAlternateRobot").boolValue = false;
            // the team's own image (from the Roundout pack's Miss Daisy)
            var image = Sprite(Dir + "/341 Team.png");
            if (image) so.FindProperty("robotImage").objectReferenceValue = image;
            var tc = so.FindProperty("tutorialCards");
            tc.arraySize = cards.Count;
            for (var i = 0; i < cards.Count; i++) tc.GetArrayElementAtIndex(i).objectReferenceValue = cards[i];
            so.FindProperty("autoClimbs").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(meta);
            return meta;
        }

        private static Sprite Sprite(string path)
        {
            if (AssetImporter.GetAtPath(path) is TextureImporter ti && ti.textureType != TextureImporterType.Sprite)
            {
                ti.textureType = TextureImporterType.Sprite;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        // A tutorial card's text and picture (the card prefab's "Text" and "Image" children)
        private static void SetCard(string cardPath, string text, string imagePath)
        {
            var card = PrefabUtility.LoadPrefabContents(cardPath);
            var label = card.GetComponentsInChildren<TMPro.TMP_Text>(true).FirstOrDefault();
            if (label) label.text = text;
            var picture = card.GetComponentsInChildren<UnityEngine.UI.Image>(true).FirstOrDefault(i => i.gameObject != card && i.name == "Image");
            var sprite = Sprite(imagePath);
            if (picture && sprite) picture.sprite = sprite;
            PrefabUtility.SaveAsPrefabAsset(card, cardPath);
            PrefabUtility.UnloadPrefabContents(card);
        }

        private static void MakeModpack(Object meta)
        {
            var path = ModFolder + "/MissDaisy.asset";
            var pack = AssetDatabase.LoadAssetAtPath<Games.Reefscape.GameManagement.ReefscapeBaseModpackSo>(path);
            if (!pack)
            {
                pack = ScriptableObject.CreateInstance<Games.Reefscape.GameManagement.ReefscapeBaseModpackSo>();
                AssetDatabase.CreateAsset(pack, path);
            }

            var so = new SerializedObject(pack);
            so.FindProperty("modpackName").stringValue = "Miss Daisy";
            so.FindProperty("modpackVersion").stringValue = "1.0.0";
            so.FindProperty("authorName").stringValue = "Seb";
            so.FindProperty("description").stringValue = "Team 341 Miss Daisy XXIII (2025)";
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
