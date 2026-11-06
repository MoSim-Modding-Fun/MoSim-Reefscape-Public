// Measures where 341 Miss Daisy actually stops against the reef (stowed, auto-align held, then pushed in), and
// logs every branch, the reef algae and the processor / barge in the robot's frame at that pose. "[MG]" lines.
//   Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testFilter MissDaisyGeometryProbe ...
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.Robots;
using Games.Reefscape.Scoring;
using Games.Reefscape.Scoring.Scorers;
using GameSystems.Management;
using MoSimCore.BaseClasses.GameManagement.TimerManagement;
using NUnit.Framework;
using RobotFramework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using PieceCtrl = RobotFramework.Controllers.GamePieceSystem.GamePieceController<
    Games.Reefscape.GamePieceSystem.ReefscapeGamePiece, Games.Reefscape.GamePieceSystem.ReefscapeGamePieceData>;

public class MissDaisyGeometryProbe
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    ReefscapeRobotBase _robot;
    Keyboard _kb;

    static void L(string s) => Debug.Log("[MG] " + s);

    string Loc(Vector3 w)
    {
        var l = _robot.transform.InverseTransformPoint(w);
        return $"({l.x:F3},{l.y:F3},{l.z:F3})";
    }

    string Dir(Vector3 d)
    {
        var l = _robot.transform.InverseTransformDirection(d);
        return $"({l.x:F2},{l.y:F2},{l.z:F2})";
    }

    void Place(Vector3 pos, Vector3 fwd)
    {
        fwd.y = 0;
        foreach (var rb in _robot.GetComponentsInChildren<Rigidbody>()) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        pos.y = _robot.transform.position.y;
        _robot.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(fwd.normalized));
        Physics.SyncTransforms();
    }

    [UnityTest]
    [Timeout(600000)]
    public IEnumerator Probe()
    {
        var meta = AssetDatabase.LoadAssetAtPath<RobotMetadataSO>("Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy/341/MissDaisy.asset");
        LogAssert.ignoreFailingMessages = true;
        PlayerPrefs.SetInt("GameMode", 0);
        PlayerPrefs.SetInt("MultiplayerMode", 0);
        PlayerPrefs.SetInt("PerspectiveAutoAlign", 0);
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        _kb = InputSystem.AddDevice<Keyboard>();
        Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Reefscape/MainMenu/RobotLoader.prefab"));
        var t0 = Time.realtimeSinceStartup;
        yield return new WaitUntil(() => RobotLoader.Instance && RobotLoader.Instance.AllRobotsLoaded || Time.realtimeSinceStartup - t0 > 60);
        SceneManager.sceneLoaded += (s, m) =>
        {
            var rsc = Object.FindFirstObjectByType<RobotSpawnController>();
            if (rsc) typeof(RobotSpawnController).GetField("robotOverride", F)?.SetValue(rsc, meta);
        };
        yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/Reefscape.unity", new LoadSceneParameters(LoadSceneMode.Single));
        t0 = Time.realtimeSinceStartup;
        yield return new WaitUntil(() => (_robot = Object.FindFirstObjectByType<ReefscapeRobotBase>()) || Time.realtimeSinceStartup - t0 > 30);
        try { _robot.GetComponent<PlayerInput>().SwitchCurrentControlScheme("Robot 1", _kb); } catch (Exception) { }
        yield return new WaitForSeconds(1f);
        new GameObject("KeepEnabled").AddComponent<MissDaisyKeepEnabled>().timer = Object.FindFirstObjectByType<BaseTimerManager>();
        yield return new WaitForSeconds(2f);
        // drop the preload so it can't touch anything
        InputSystem.QueueStateEvent(_kb, new KeyboardState(Key.K));
        yield return new WaitForSeconds(0.2f);
        InputSystem.QueueStateEvent(_kb, new KeyboardState());
        yield return new WaitForSeconds(1f);

        var faces = Object.FindObjectsByType<AlignNode>(FindObjectsSortMode.None)
            .Where(n => n.GetComponentInParent<Reef>() && n.GetComponentInParent<Reef>().Alliance == _robot.Alliance).ToList();
        var branches = Object.FindObjectsByType<ReefPoleScorer>(FindObjectsSortMode.None)
            .SelectMany(p => ((Array)typeof(ReefPoleScorer).GetField("_branches", F).GetValue(p)).Cast<object>()).ToList();
        var aa = _robot.GetComponent<ReefscapeAutoAlign>();
        L($"alliance {_robot.Alliance} faces {faces.Count} align offset {aa.offset}");
        foreach (var face in faces.Take(2))
        {
            var reef = face.GetComponentInParent<Reef>().transform.position;
            var toReef = reef - face.transform.position;
            toReef.y = 0;
            toReef.Normalize();
            foreach (var push in new[] { false, true })
            {
                Place(face.transform.position - toReef * 1.3f, toReef);
                yield return new WaitForSeconds(0.5f);
                InputSystem.QueueStateEvent(_kb, new KeyboardState(Key.Q));
                yield return new WaitForSeconds(3.5f);
                L($"face {face.name} autoalign: leftNode(robot)={Loc(face.LeftNode.transform.position)} rightNode={Loc(face.RightNode.transform.position)}");
                if (push)
                {
                    InputSystem.QueueStateEvent(_kb, new KeyboardState(Key.Q, Key.W, Key.D, Key.S, Key.A));
                    InputSystem.QueueStateEvent(_kb, new KeyboardState());
                    // push toward the reef with whichever key drives that way: try each for 1 s and keep the closest
                    var best = float.MaxValue;
                    foreach (var k in new[] { Key.W, Key.S, Key.A, Key.D })
                    {
                        var start = _robot.transform.position;
                        InputSystem.QueueStateEvent(_kb, new KeyboardState(k));
                        yield return new WaitForSeconds(1f);
                        InputSystem.QueueStateEvent(_kb, new KeyboardState());
                        yield return new WaitForSeconds(0.4f);
                        var d = Vector3.Dot(_robot.transform.position - start, toReef);
                        L($"push {k}: moved {d:F3} toward reef; leftNode(robot)={Loc(face.LeftNode.transform.position)}");
                        if (d < 0.02f) Place(start, toReef);
                    }
                }
                InputSystem.QueueStateEvent(_kb, new KeyboardState());
                yield return new WaitForSeconds(0.5f);
                L($"face {face.name} push={push} final: leftNode(robot)={Loc(face.LeftNode.transform.position)} faceNode={Loc(face.transform.position)}");
                foreach (var b in branches.OrderBy(b => (((Vector3)b.GetType().GetField("center").GetValue(b)) - _robot.transform.position).sqrMagnitude).Take(6))
                {
                    var c = (Vector3)b.GetType().GetField("center").GetValue(b);
                    var r = (Quaternion)b.GetType().GetField("rotation").GetValue(b);
                    var h = (Vector3)b.GetType().GetField("bounds").GetValue(b);
                    L($"  branch {b.GetType().GetProperty("TargetHeight").GetValue(b)} centre={Loc(c)} axis={Dir(r * Vector3.forward)} half={h:F3}");
                }
                foreach (var a in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Algae")
                             .OrderBy(p => (p.transform.position - _robot.transform.position).sqrMagnitude).Take(3))
                    L($"  algae {Loc(a.transform.position)}");
                foreach (var tr in Object.FindObjectsByType<TroughScorer>(FindObjectsSortMode.None).Take(1))
                    L($"  trough scorer {Loc(tr.transform.position)}");
            }
        }

        foreach (var p in Object.FindObjectsByType<ProcessorScorer>(FindObjectsSortMode.None))
        {
            var trig = (BoxCollider)typeof(ProcessorScorer).GetField("scoreTrigger", F).GetValue(p);
            L($"processor {p.Alliance} trigger centre(world)={(trig ? trig.bounds.center : p.transform.position):F3} size={(trig ? trig.bounds.size : Vector3.zero):F3} fwd={(trig ? trig.transform.forward : p.transform.forward):F2}");
        }
        foreach (var b in Object.FindObjectsByType<BargeScorer>(FindObjectsSortMode.None))
        {
            var box = b.GetComponent<BoxCollider>();
            L($"barge {b.Alliance} centre(world)={(box ? box.bounds.center : b.transform.position):F3} size={(box ? box.bounds.size : Vector3.zero):F3}");
        }
        foreach (var c in GameObject.FindGameObjectsWithTag("Cage").Take(6))
            L($"cage {c.name} at {c.transform.position:F2} rb={(c.GetComponentInParent<Rigidbody>() ? c.GetComponentInParent<Rigidbody>().name : "none")}");
        foreach (var z in new[] { "BlueBargeZone", "RedBargeZone" })
            foreach (var g in GameObject.FindGameObjectsWithTag(z)) L($"{z} {g.name} {(g.GetComponent<Collider>() ? g.GetComponent<Collider>().bounds.ToString() : "")}");
        L("DONE");
    }
}
#endif
