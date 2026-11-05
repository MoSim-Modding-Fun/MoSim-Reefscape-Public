// Close-up side renders of the 341 Miss Daisy prefab with its climber turned to given angles (no physics), to check
// which way the harpoon points at each climber setpoint.
//   Unity -batchmode -projectPath . -executeMethod Editor.MissDaisyRender.Climber -quit   (not -nographics)
//   env MD_RENDER_DIR (output folder), MD_RENDER_ANGLES (comma list, default 0,9,97,174)
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Editor
{
    public static class MissDaisyRender
    {
        // The floor intake deployed (Ground Intake: -90), with the coral path's waypoints, the idle carrot and the claw
        // state marked by small spheres (red, then orange, yellow, green, blue, white), side views.
        public static void IntakePath()
        {
            var dir = Environment.GetEnvironmentVariable("MD_RENDER_DIR") ?? "Temp/MissDaisyRender";
            Directory.CreateDirectory(dir);
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy/341/341.prefab");
            var robot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var all = robot.GetComponentsInChildren<Transform>(true);
            var intake = all.First(t => t.name == "Intake");
            intake.localRotation *= Quaternion.AngleAxis(-90f, Vector3.right);
            var colors = new[] { Color.red, new Color(1f, 0.5f, 0f), Color.yellow, Color.green, Color.blue, Color.white };
            var names = new[] { "Coral Path 1 Front Roller", "Coral Path 1b Belts", "Coral Path 2 Top Roller", "Coral Path 3 Past Flap", "Ground Coral Intake", "Coral Claw State" };
            for (var i = 0; i < names.Length; i++)
            {
                var t = all.FirstOrDefault(x => x.name == names[i]);
                if (!t) continue;
                var sph = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sph.transform.position = t.position;
                sph.transform.localScale = Vector3.one * 0.03f;
                var m = new Material(Shader.Find("Unlit/Color")) { color = colors[i] };
                sph.GetComponent<Renderer>().sharedMaterial = m;
                Debug.Log($"MISSDAISY RENDER intake path {names[i]} at (robot) {robot.transform.InverseTransformPoint(t.position):F3}");
            }
            foreach (var c in robot.GetComponentsInChildren<Collider>(true).Where(c => c.name is "Front Roller" or "Flap Top Roller" or "Flap Belts" or "Channel Floor"))
                Debug.Log($"MISSDAISY RENDER intake part {c.name} centre (robot) {robot.transform.InverseTransformPoint(c.bounds.center):F3} " +
                          $"min y {c.bounds.min.y - robot.transform.position.y:F3} size {c.bounds.size:F3}");
            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.fieldOfView = 30;
            cam.backgroundColor = new Color(0.8f, 0.85f, 0.9f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var rt = new RenderTexture(1280, 960, 24);
            cam.targetTexture = rt;
            var look = intake.position - robot.transform.forward * 0.25f;
            foreach (var (tag, from) in new[] { ("side", look + robot.transform.right * 1.3f), ("side2", look - robot.transform.right * 1.3f) })
            {
                cam.transform.position = from;
                cam.transform.LookAt(look);
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                File.WriteAllBytes(Path.Combine(dir, $"intake-path-{tag}.png"), tex.EncodeToPNG());
            }
            Debug.Log("MISSDAISY RENDER done");
        }

        // 5449's climber (the one that latches in this sim), deployed to its climb-prep angle (-90): every collider on
        // it and on its StationaryColliders in the robot's frame (+z its front), for copying the latch.
        public static void Probe5449()
        {
            var dir = Environment.GetEnvironmentVariable("MD_RENDER_DIR") ?? "Temp/MissDaisyRender";
            Directory.CreateDirectory(dir);
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Reefscape/Robots/Mods/China Modpack/5449/5449.prefab");
            var robot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            robot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var all = robot.GetComponentsInChildren<Transform>(true);
            var pivot = all.First(t => t.name == "ClimberPivot");
            var cj = pivot.GetComponent<ConfigurableJoint>();
            Debug.Log($"MISSDAISY 5449 pivot (robot) {robot.transform.InverseTransformPoint(pivot.position):F4} axis {cj.axis} rest euler {pivot.localEulerAngles:F1}");
            pivot.localRotation *= Quaternion.AngleAxis(-90f, cj.axis);
            Physics.SyncTransforms();
            foreach (var t in all.Where(t => t.name is "ClickerHolder" or "Clicker" or "OtherClicker" or "StationaryColliders" or "ColliderThing"))
            {
                var r = Quaternion.Inverse(robot.transform.rotation) * t.rotation;
                Debug.Log($"MISSDAISY 5449 xf {t.name} at(robot)={robot.transform.InverseTransformPoint(t.position):F4} right={r * Vector3.right:F3} up={r * Vector3.up:F3} fwd={r * Vector3.forward:F3}");
            }
            var stat = all.FirstOrDefault(t => t.name == "StationaryColliders");
            foreach (var c in robot.GetComponentsInChildren<Collider>(true))
            {
                var onClimber = c.transform.IsChildOf(pivot);
                if (!onClimber && !(stat && c.transform.IsChildOf(stat))) continue;
                var path = c.transform.name;
                for (var t = c.transform.parent; t && t != robot.transform; t = t.parent) path = t.name + "/" + path;
                var size = c is BoxCollider b ? Vector3.Scale(b.size, c.transform.lossyScale) : c.bounds.size;
                var centre = c is BoxCollider b2 ? c.transform.TransformPoint(b2.center) : c.bounds.center;
                var rot = Quaternion.Inverse(robot.transform.rotation) * c.transform.rotation;
                Debug.Log($"MISSDAISY 5449 col {(onClimber ? "climber" : "stationary")} {path} {c.GetType().Name} trigger={c.isTrigger} " +
                          $"layer={LayerMask.LayerToName(c.gameObject.layer)} mat={(c.sharedMaterial ? c.sharedMaterial.name : "-")} " +
                          $"centre(robot)={robot.transform.InverseTransformPoint(centre):F4} size={size:F4} " +
                          $"right={rot * Vector3.right:F3} up={rot * Vector3.up:F3} fwd={rot * Vector3.forward:F3} exclude={c.excludeLayers.value} prio={c.layerOverridePriority}");
            }
            var cs = robot.GetComponent<Games.Reefscape.Scoring.Scorers.ClimbScorer>();
            var trig = new SerializedObject(cs).FindProperty("cageScoringTrigger").objectReferenceValue as BoxCollider;
            if (trig) Debug.Log($"MISSDAISY 5449 scoring trigger centre(robot) {robot.transform.InverseTransformPoint(trig.transform.position):F4} size {Vector3.Scale(trig.size, trig.transform.lossyScale):F4} on climber={trig.transform.IsChildOf(pivot)}");
            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.fieldOfView = 35;
            cam.backgroundColor = new Color(0.8f, 0.85f, 0.9f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var rt = new RenderTexture(1280, 960, 24);
            cam.targetTexture = rt;
            var look = pivot.position;
            foreach (var (tag, from) in new[] { ("side", look + robot.transform.right * 1.6f), ("3q", look + robot.transform.right * 1.0f - robot.transform.forward * 1.0f + Vector3.up * 0.6f), ("top", look + Vector3.up * 1.8f + robot.transform.forward * 0.01f) })
            {
                cam.transform.position = from;
                cam.transform.LookAt(look);
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                File.WriteAllBytes(Path.Combine(dir, $"5449-climber-{tag}.png"), tex.EncodeToPNG());
            }
            Debug.Log("MISSDAISY RENDER done");
        }

        // The art panels: the robot from the panel side, three-quarter and straight on, lit (MD_RENDER_DIR).
        // Parts left on the drivebase high up in the mast, and what the moving elevator stage holds there.
        public static void Mast()
        {
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy/341/341.prefab");
            var robot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            robot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var r in robot.GetComponentsInChildren<MeshRenderer>(true).Where(m => robot.transform.InverseTransformPoint(m.bounds.center).y > 0.28f).OrderBy(m => m.bounds.center.y))
            {
                var path = r.transform.parent ? r.transform.parent.name : "-";
                var c = robot.transform.InverseTransformPoint(r.bounds.center);
                if (path == "Drivebase" || path == "Stage1" || path == "Carriage") Debug.Log($"MISSDAISY MAST {path} | {r.name} | centre {c:F3} size {r.bounds.size:F3} mat {(r.sharedMaterial ? r.sharedMaterial.name : "-")}");
            }
            Debug.Log("MISSDAISY RENDER done");
        }

        // Where each swerve module's wheel really is (from the module mesh) against the drivetrain's wheel colliders and casts.
        public static void Wheels()
        {
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy/341/341.prefab");
            var robot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            robot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var r in robot.GetComponentsInChildren<MeshRenderer>(true).Where(m => m.name.Contains("WCP Swerve X2i")))
            {
                var verts = r.GetComponent<MeshFilter>().sharedMesh.vertices.Select(v => robot.transform.InverseTransformPoint(r.transform.TransformPoint(v))).ToArray();
                if (verts.Length == 0) { Debug.Log($"MISSDAISY WHEEL {r.name}: mesh not readable"); continue; }
                var low = verts.Min(v => v.y);
                // the tread: the lowest vertices are its bottom; everything within a wheel diameter under the module's plate
                var band = verts.Where(v => v.y < low + 0.012f).ToArray();
                Vector3 Mid(Vector3[] a) => new((a.Min(v => v.x) + a.Max(v => v.x)) / 2f, (a.Min(v => v.y) + a.Max(v => v.y)) / 2f, (a.Min(v => v.z) + a.Max(v => v.z)) / 2f);
                var contact = Mid(band);
                // a horizontal slice across the wheel's middle, 2 in up: its outline is the tire's width and length there
                var mid = verts.Where(v => Mathf.Abs(v.y - (low + 0.0508f)) < 0.006f && Mathf.Abs(v.x - contact.x) < 0.08f && Mathf.Abs(v.z - contact.z) < 0.08f).ToArray();
                Debug.Log($"MISSDAISY WHEEL {r.name}: bottom y={low:F4} contact (x,z)=({contact.x:F4},{contact.z:F4}) band size ({band.Max(v => v.x) - band.Min(v => v.x):F3},{band.Max(v => v.z) - band.Min(v => v.z):F3}) " +
                          $"slice centre ({(mid.Length > 0 ? Mid(mid).x : 0f):F4},{(mid.Length > 0 ? Mid(mid).z : 0f):F4}) slice size ({(mid.Length > 0 ? mid.Max(v => v.x) - mid.Min(v => v.x) : 0f):F3},{(mid.Length > 0 ? mid.Max(v => v.z) - mid.Min(v => v.z) : 0f):F3}) mesh bounds centre {robot.transform.InverseTransformPoint(r.bounds.center):F4}");
            }
            foreach (var t in robot.GetComponentsInChildren<Transform>(true).Where(t => t.parent && (t.parent.name == "Wheels" || t.parent.name == "Raycast")))
            {
                var wc = t.GetComponent<WheelCollider>();
                Debug.Log($"MISSDAISY WHEEL drivetrain {t.parent.name}/{t.name} at {robot.transform.InverseTransformPoint(t.position):F4}" + (wc ? $" radius {wc.radius:F4} bottom y={robot.transform.InverseTransformPoint(t.TransformPoint(wc.center)).y - wc.radius - wc.suspensionDistance * (1 - wc.suspensionSpring.targetPosition):F4}" : ""));
            }
            Debug.Log("MISSDAISY RENDER done");
        }

        // Close-ups of the four bumper faces and the camera on top of the elevator.
        public static void Bumpers()
        {
            var dir = Environment.GetEnvironmentVariable("MD_RENDER_DIR") ?? "Temp/MissDaisyRender";
            Directory.CreateDirectory(dir);
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy/341/341.prefab");
            var robot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            robot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.fieldOfView = 35;
            cam.backgroundColor = new Color(0.8f, 0.85f, 0.9f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            var top = robot.GetComponentsInChildren<MeshRenderer>(true).FirstOrDefault(r => r.name == "mesh30420_mesh");
            var y = 0.08f;
            var shots = new System.Collections.Generic.List<(string, Vector3, Vector3)>
            {
                ("bumper-x+", new Vector3(1.3f, y + 0.12f, 0.25f), new Vector3(0.42f, y, 0f)),
                ("bumper-x-", new Vector3(-1.3f, y + 0.12f, 0.25f), new Vector3(-0.42f, y, 0f)),
                ("bumper-z+", new Vector3(0.25f, y + 0.12f, 1.3f), new Vector3(0f, y, 0.42f)),
                ("bumper-z-", new Vector3(0.25f, y + 0.12f, -1.3f), new Vector3(0f, y, -0.42f)),
            };
            if (top)
            {
                var c = top.bounds.center;
                shots.Add(("camera-a", c + new Vector3(0.45f, 0.15f, 0.45f), c));
                shots.Add(("camera-b", c + new Vector3(0.45f, 0.15f, -0.45f), c));
                shots.Add(("camera-c", c + new Vector3(-0.45f, 0.15f, 0.45f), c));
            }
            foreach (var (tag, from, at) in shots)
            {
                cam.transform.position = from;
                cam.transform.LookAt(at);
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                File.WriteAllBytes(Path.Combine(dir, $"{tag}.png"), tex.EncodeToPNG());
            }
            Debug.Log("MISSDAISY RENDER done");
        }

        public static void Panels()
        {
            var dir = Environment.GetEnvironmentVariable("MD_RENDER_DIR") ?? "Temp/MissDaisyRender";
            Directory.CreateDirectory(dir);
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy/341/341.prefab");
            var robot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            robot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.fieldOfView = 35;
            cam.backgroundColor = new Color(0.8f, 0.85f, 0.9f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var rt = new RenderTexture(1280, 960, 24);
            cam.targetTexture = rt;
            var look = robot.transform.position + Vector3.up * 0.35f;
            var r = robot.transform.right; var f = robot.transform.forward;
            var top = robot.transform.position + Vector3.up * 0.97f;
            foreach (var (tag, from, at) in new[]
                     {
                         ("side", look - r * 2.4f + Vector3.up * 0.1f, look),
                         ("3q-front", look - r * 1.7f + f * 1.7f + Vector3.up * 0.9f, look),
                         ("3q-back", look - r * 1.7f - f * 1.7f + Vector3.up * 0.9f, look),
                         ("other-side", look + r * 2.4f + Vector3.up * 0.4f, look),
                         ("banner", top - r * 1.0f + f * 0.4f - Vector3.up * 0.15f, top)
                     })
            {
                cam.transform.position = from;
                cam.transform.LookAt(at);
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                File.WriteAllBytes(Path.Combine(dir, $"panels-{tag}.png"), tex.EncodeToPNG());
            }
            foreach (var p in robot.GetComponentsInChildren<MeshRenderer>(true).Where(m => m.transform.parent && m.transform.parent.name == "Art Panels"))
                Debug.Log($"MISSDAISY RENDER panel {p.name} centre (robot) {robot.transform.InverseTransformPoint(p.bounds.center):F3} size {p.bounds.size:F3}");
            Debug.Log("MISSDAISY RENDER done");
        }

        // Which of the robot's own colliders sit around the harpoon's post pocket with the climber deployed (165 deg,
        // Climb Prep): anything there meets the cage posts before the hooks do.
        public static void ClimbClearance()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy/341/341.prefab");
            var robot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var all = robot.GetComponentsInChildren<Transform>(true);
            var climber = all.First(t => t.name == "Climber");
            climber.localRotation *= Quaternion.AngleAxis(165f, Vector3.right);
            Physics.SyncTransforms();
            var trig = all.First(t => t.name == "Cage Scoring Trigger").GetComponent<BoxCollider>();
            var zone = trig.bounds;
            zone.Expand(0.30f);
            Debug.Log($"MISSDAISY CLEAR pocket (robot) {robot.transform.InverseTransformPoint(trig.bounds.center):F3} size {trig.bounds.size:F3}");
            foreach (var c in robot.GetComponentsInChildren<Collider>(true))
            {
                if (c.isTrigger || c.transform.IsChildOf(climber) || !c.enabled) continue;
                if (!c.bounds.Intersects(zone)) continue;
                var path = c.transform.name;
                for (var t = c.transform.parent; t && t != robot.transform; t = t.parent) path = t.name + "/" + path;
                Debug.Log($"MISSDAISY CLEAR in the way? {path} ({c.GetType().Name}) centre (robot) {robot.transform.InverseTransformPoint(c.bounds.center):F3} " +
                          $"size {c.bounds.size:F3} layer {LayerMask.LayerToName(c.gameObject.layer)} exclude {c.excludeLayers.value}");
            }
            Debug.Log("MISSDAISY CLEAR done");
        }

        public static void Climber()
        {
            var dir = Environment.GetEnvironmentVariable("MD_RENDER_DIR") ?? "Temp/MissDaisyRender";
            var angles = (Environment.GetEnvironmentVariable("MD_RENDER_ANGLES") ?? "0,9,97,174")
                .Split(',').Select(float.Parse).ToArray();
            Directory.CreateDirectory(dir);
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy/341/341.prefab");
            var robot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            robot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var climber = robot.GetComponentsInChildren<Transform>(true).First(t => t.name == "Climber");
            var rest = climber.localRotation;
            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.fieldOfView = 40;
            cam.backgroundColor = new Color(0.8f, 0.85f, 0.9f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var rt = new RenderTexture(1280, 960, 24);
            cam.targetTexture = rt;
            foreach (var a in angles)
            {
                climber.localRotation = rest * Quaternion.AngleAxis(a, Vector3.right); // the joint's axis (local X)
                foreach (var (tag, from) in new[]
                         {
                             ("side", climber.position + robot.transform.right * 1.6f + Vector3.up * 0.2f + robot.transform.forward * 0.1f),
                             ("3q", climber.position + robot.transform.right * 1.1f + robot.transform.forward * 1.1f + Vector3.up * 0.8f),
                             ("top", climber.position + Vector3.up * 2.2f + robot.transform.forward * 0.01f)
                         })
                {
                    cam.transform.position = from;
                    cam.transform.LookAt(climber.position + robot.transform.forward * 0.1f);
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    File.WriteAllBytes(Path.Combine(dir, $"climber-{a:000}-{tag}.png"), tex.EncodeToPNG());
                }
                Vector3 At(string n) => robot.transform.InverseTransformPoint(robot.GetComponentsInChildren<Renderer>(true)
                    .First(r => r.name.StartsWith(n)).bounds.center);
                Debug.Log($"MISSDAISY RENDER climber {a}: rest euler {rest.eulerAngles:F0}; meshes (robot, +z front): top plate {At("Top Plate"):F2} " +
                          $"top hook {At("Top Hook"):F2} funnel {At("Funnel"):F2}; hook holder transform " +
                          $"{robot.transform.InverseTransformPoint(robot.GetComponentsInChildren<Transform>(true).First(t => t.name.StartsWith("Top Hook Holder")).position):F2}; " +
                          $"hook nose collider {robot.transform.InverseTransformPoint(robot.GetComponentsInChildren<BoxCollider>(true).First(c => c.name == "Hook Nose").bounds.center):F2}");
            }
            Debug.Log("MISSDAISY RENDER done");
        }
    }
}
