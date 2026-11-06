// Headless play-mode check of 341 Miss Daisy in the Reefscape scene (based on RobotPlayTests/IntakePlayTest.cs).
// Logs "[MD]" lines: the pose at every setpoint (where the intake, claw and climber actually end up), a floor coral
// flowing through the intake into the claw (speed sampled every physics step), HP coral, algae, and releases.
//   Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testFilter MissDaisyPlayTest -testResults r.xml -logFile l.log
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using GameSystems.Management;
using MoSimCore.BaseClasses.GameManagement.TimerManagement;
using NUnit.Framework;
using RobotFramework;
using RobotFramework.Components;
using RobotFramework.Enums;
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
using Node = RobotFramework.Controllers.GamePieceSystem.RobotGamePieceController<
    Games.Reefscape.GamePieceSystem.ReefscapeGamePiece,
    Games.Reefscape.GamePieceSystem.ReefscapeGamePieceData>.GamePieceControllerNode;

// Holds the match timer paused with robots enabled for the whole test.
public class MissDaisyKeepEnabled : MonoBehaviour
{
    public BaseTimerManager timer;
    static readonly PropertyInfo State = typeof(BaseTimerManager).GetProperty("CurrentRobotState");

    void Update()
    {
        if (!timer) return;
        timer.PauseTimer();
        State.SetValue(timer, MoSimCore.Enums.RobotState.Enabled);
    }
}

// Logs what a body's colliders touch (throttled), to find what blocks a mechanism.
public class MissDaisyContactLog : MonoBehaviour
{
    float _next;

    void OnCollisionStay(Collision c)
    {
        if (Time.time < _next) return;
        _next = Time.time + 0.5f;
        Debug.Log($"[MD] CONTACT {name} <- {c.collider.name} (layer {LayerMask.LayerToName(c.collider.gameObject.layer)}, " +
                  $"body {(c.rigidbody ? c.rigidbody.name : "static")}) via {c.GetContact(0).thisCollider.name} impulse={c.impulse.magnitude:F2}");
    }
}

public class MissDaisyPlayTest
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const string Meta = "Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy/341/MissDaisy.asset";
    ReefscapeRobotBase _robot;
    Keyboard _kb;
    Key[] _held = Array.Empty<Key>();

    static void L(string s) => Debug.Log("[MD] " + s);

    object Field(string name) => _robot.GetType().GetField(name, F)?.GetValue(_robot);

    void Keys(params Key[] keys)
    {
        _held = keys;
        InputSystem.QueueStateEvent(_kb, new KeyboardState(keys));
    }

    IEnumerator Tap(Key k)
    {
        Keys(_held.Append(k).ToArray());
        yield return new WaitForSeconds(0.15f);
        Keys(_held.Where(x => x != k).ToArray());
        yield return new WaitForSeconds(0.15f);
    }

    Node NodeOf(string piece) => _robot.GetComponent<ReefscapeRobotGamePieceController>().GetPieceByName(piece);

    Transform Find(string n) => _robot.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);

    string Local(Transform x)
    {
        if (!x) return "-";
        var l = _robot.transform.InverseTransformPoint(x.position);
        return $"({l.x:F2},{l.y:F2},{l.z:F2})";
    }

    string Status()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"t={Time.time:F2} sp={_robot.CurrentSetpoint} mode={_robot.CurrentRobotMode} drop={_robot.CurrentCoralStationMode.DropType} ");
        if (Field("elevator") is GenericElevator el) sb.Append($"elev={el.GetElevatorHeight():F1}in ");
        foreach (var (j, a) in new[] { ("shoulder", JointAxis.X), ("wrist", JointAxis.Y), ("intakePivot", JointAxis.X), ("climber", JointAxis.X) })
            if (Field(j) is GenericJoint g) sb.Append($"{j}={g.GetSingleAxisAngle(a):F1} ");
        foreach (var n in new[] { "Coral", "Algae" })
        {
            var node = NodeOf(n);
            sb.Append($"| {n} st={node.currentStateNum} to={node.movingTo} was={node.wasMovingTo} at={node.atTarget} ");
        }
        foreach (var i in _robot.GetComponentsInChildren<ReefscapeGamePieceIntake>())
            sb.Append($"| {i.name} req={i.requestIntake} has={i.hasGamePiece} sec={i.securedGamePiece} ");
        return sb.ToString();
    }

    string Pose()
    {
        var eject = Find("Claw Eject");
        var f = eject ? _robot.transform.InverseTransformDirection(eject.forward) : Vector3.zero;
        return $"frontRoller={Local(Find("Front Roller"))} topRoller={Local(Find("Top Roller"))} groundBox={Local(Find("Ground Coral Intake"))} " +
               $"clawCoral={Local(Find("Coral Claw State"))} clawBox={Local(Find("Claw Coral Intake"))} algae={Local(Find("Algae Claw State"))} " +
               $"ejectDir=({f.x:F2},{f.y:F2},{f.z:F2}) cage={Local(Find("Cage Trigger"))} arm={Local(Find("Arm"))}";
    }

    PieceCtrl FreePiece(string name, Vector3 near)
    {
        var held = _robot.GetComponentsInChildren<PieceCtrl>().ToHashSet();
        return Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None)
            .Where(p => p.PieceName == name && !held.Contains(p) && p.gameObject.activeInHierarchy)
            .OrderBy(p => (p.transform.position - near).sqrMagnitude).FirstOrDefault();
    }

    static void Put(PieceCtrl p, Vector3 pos, Quaternion rot)
    {
        var rb = p.GetComponent<Rigidbody>();
        if (rb)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.position = pos;
            rb.rotation = rot;
        }
        p.transform.SetPositionAndRotation(pos, rot);
    }

    static Quaternion Along(PieceCtrl p, Vector3 dir)
    {
        var mf = p.GetComponentInChildren<MeshFilter>();
        var ext = mf ? Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale) : Vector3.one;
        var axis = ext.x >= ext.y && ext.x >= ext.z ? Vector3.right : ext.y >= ext.z ? Vector3.up : Vector3.forward;
        return Quaternion.FromToRotation(axis, dir);
    }

    void PlaceRobot(Vector3 pos, Vector3 forward)
    {
        var t = _robot.transform;
        var rot = Quaternion.LookRotation(new Vector3(forward.x, 0, forward.z), Vector3.up);
        pos.y = t.position.y;
        foreach (var rb in _robot.GetComponentsInChildren<Rigidbody>())
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        t.SetPositionAndRotation(pos, rot);
        Physics.SyncTransforms();
    }

    IEnumerator DropHeld()
    {
        if (NodeOf("Coral").HasPiece() || NodeOf("Algae").HasPiece())
        {
            yield return Tap(Key.K);
            yield return new WaitForSeconds(1.5f);
        }
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(2f);
    }

    IEnumerator PoseAt(string label, Key key, float wait = 3f)
    {
        if (key != Key.None) yield return Tap(key);
        yield return new WaitForSeconds(wait);
        L($"POSE {label} {Status()} {Pose()}");
    }


    // Coral into the claw through the HP intake (station mode): the test's way to load a piece.
    IEnumerator GiveCoral()
    {
        if (NodeOf("Coral").HasPiece()) yield break;
        if (_robot.CurrentRobotMode == Games.Reefscape.Enums.ReefscapeRobotMode.Algae) yield return Tap(Key.Semicolon);
        if (_robot.CurrentCoralStationMode.DropType != DropType.Station) yield return Tap(Key.V);
        Keys(Key.LeftShift);
        yield return new WaitForSeconds(2f);
        var box = Find("Claw Coral Intake");
        var c = FreePiece("Coral", box.position);
        Put(c, box.position + Vector3.up * 0.15f, Along(c, Find("Coral Claw State").right));
        var end = Time.time + 3f;
        while (Time.time < end && !NodeOf("Coral").HasPiece()) yield return new WaitForSeconds(0.1f);
        Keys();
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(1.5f);
        L($"give coral held={NodeOf("Coral").HasPiece()}");
    }

    static readonly FieldInfo Branches = typeof(Games.Reefscape.Scoring.Scorers.ReefPoleScorer).GetField("_branches", F);

    static System.Collections.Generic.IEnumerable<(Games.Reefscape.Scoring.Scorers.ReefPoleScorer pole, object branch, string h, bool scored, Vector3 c)> AllBranches()
    {
        foreach (var pole in Object.FindObjectsByType<Games.Reefscape.Scoring.Scorers.ReefPoleScorer>(FindObjectsSortMode.None))
        foreach (var b in (Array)Branches.GetValue(pole) ?? Array.Empty<object>())
        {
            var bt = b.GetType();
            yield return (pole, b, bt.GetProperty("TargetHeight").GetValue(b).ToString(), (bool)bt.GetProperty("Scored").GetValue(b),
                (Vector3)bt.GetField("center").GetValue(b));
        }
    }

    // The pose the robot's own auto-align drives to: the reef face's left node plus the ReefscapeAutoAlign offset.
    (Vector3 pos, Quaternion rot) AlignPose(AlignNode face)
    {
        var aa = _robot.GetComponent<ReefscapeAutoAlign>();
        var node = face.LeftNode.transform;
        var pos = node.position + node.rotation * (aa.offset * 0.0254f);
        var rot = node.rotation * Quaternion.Euler(0, aa.rotation, 0);
        var reef = face.GetComponentInParent<Reef>() ? face.GetComponentInParent<Reef>().transform.position : face.transform.position;
        if (Vector3.Dot(rot * Vector3.forward, reef - pos) < 0) rot *= Quaternion.Euler(0, 180, 0);
        return (pos, rot);
    }

    // Robot at the auto-align pose of a reef face, the level's setpoint, then place: does a branch score?
    IEnumerator ScoreAt(string level, Key key)
    {
        yield return GiveCoral();
        var face = Object.FindObjectsByType<AlignNode>(FindObjectsSortMode.None)
            .Where(n => n.GetComponentInParent<Reef>() && n.GetComponentInParent<Reef>().Alliance == _robot.Alliance)
            .OrderBy(n => n.transform.position.x).First();
        var (pos, rot) = AlignPose(face);
        // raise first, then move in, as a driver does (the held coral would catch the lower branches)
        PlaceRobot(pos - rot * Vector3.forward * 0.6f, rot * Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        yield return Tap(key);
        yield return new WaitForSeconds(3f);
        PlaceRobot(pos, rot * Vector3.forward);
        // hold auto-align (left) as a driver does: the robot's own controller keeps it on the node
        Keys(Key.Q);
        yield return new WaitForSeconds(1.5f);
        var piece = NodeOf("Coral").controller;
        var target = AllBranches().Where(b => b.h == level).OrderBy(b => (b.c - piece.transform.position).sqrMagnitude).First();
        var bt = target.branch.GetType();
        var brot = (Quaternion)bt.GetField("rotation").GetValue(target.branch);
        Vector3 Rel() => Quaternion.Inverse(brot) * (piece.transform.position - target.c);
        var axisW = brot * Vector3.forward;
        var pr = _robot.transform.InverseTransformPoint(target.c);
        L($"score {level} {Status()} coralInBranch={Rel():F3} branchAxis(robot)={_robot.transform.InverseTransformDirection(axisW):F2} " +
          $"branch(robot-local)={pr:F3} coral(robot-local)={Local(piece.transform)} offset={_robot.GetComponent<ReefscapeAutoAlign>().offset:F1}");
        var before = AllBranches().Count(b => b.scored);
        yield return Tap(Key.K);
        for (var k = 0; k < 10 && piece; k++)
        {
            L($"score {level} +{k * 0.25f:F2}s coralInBranch={Rel():F2} scoredNow={AllBranches().Count(b => b.scored)}");
            yield return new WaitForSeconds(0.25f);
        }
        Keys();
        var after = AllBranches().Count(b => b.scored);
        L($"RESULT score {level} branchesScored {before}->{after}");
        PlaceRobot(new Vector3(-2.2f, 0, 2.0f), Vector3.forward);
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(1.5f);
    }

    [UnityTest]
    [Timeout(900000)]
    public IEnumerator MissDaisy()
    {
        var meta = AssetDatabase.LoadAssetAtPath<RobotMetadataSO>(Meta);
        Assert.NotNull(meta, Meta);
        LogAssert.ignoreFailingMessages = true;
        PlayerPrefs.SetInt("GameMode", 0);
        PlayerPrefs.SetInt("MultiplayerMode", 0);
        PlayerPrefs.SetInt("PerspectiveAutoAlign", 0);
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        InputSystem.settings.editorInputBehaviorInPlayMode =
            InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        _kb = InputSystem.AddDevice<Keyboard>();

        Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Reefscape/MainMenu/RobotLoader.prefab"));
        var t0 = Time.realtimeSinceStartup;
        yield return new WaitUntil(() => RobotLoader.Instance && RobotLoader.Instance.AllRobotsLoaded ||
                                         Time.realtimeSinceStartup - t0 > 60);
        SceneManager.sceneLoaded += (s, m) =>
        {
            var rsc = Object.FindFirstObjectByType<RobotSpawnController>();
            if (rsc) typeof(RobotSpawnController).GetField("robotOverride", F)?.SetValue(rsc, meta);
        };
        yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/Reefscape.unity",
            new LoadSceneParameters(LoadSceneMode.Single));
        t0 = Time.realtimeSinceStartup;
        yield return new WaitUntil(() => (_robot = Object.FindFirstObjectByType<ReefscapeRobotBase>()) ||
                                         Time.realtimeSinceStartup - t0 > 30);
        Assert.NotNull(_robot, "robot not spawned");
        L($"robot {_robot.name} {_robot.GetType().Name}");
        foreach (var n in new[] { "Intake", "Front Roller", "Top Roller", "Climber", "Claw" })
            if (Find(n)) Find(n).gameObject.AddComponent<MissDaisyContactLog>();
        var ij = Field("intakePivot") as GenericJoint;
        for (var k = 0; k < 15; k++)
        {
            L($"spawn t={Time.time:F2} intake={(ij ? ij.GetSingleAxisAngle(JointAxis.X) : -1):F1} rbAngVel={(ij ? ij.GetComponent<Rigidbody>().angularVelocity.magnitude : -1):F2} " +
              $"frontRoller={Local(Find("Front Roller"))} state={MoSimCore.BaseClasses.GameManagement.BaseGameManager.Instance.RobotState}");
            yield return new WaitForSeconds(0.2f);
        }
        foreach (var i in _robot.GetComponentsInChildren<ReefscapeGamePieceIntake>(true)) i.debugLog = true;
        var pi = _robot.GetComponent<PlayerInput>();
        try { pi.SwitchCurrentControlScheme("Robot 1", _kb); } catch (Exception e) { L("scheme " + e.Message); }
        yield return new WaitForSeconds(1f);
        new GameObject("KeepEnabled").AddComponent<MissDaisyKeepEnabled>().timer = Object.FindFirstObjectByType<BaseTimerManager>();
        yield return new WaitForSeconds(2f);
        L($"state {MoSimCore.BaseClasses.GameManagement.BaseGameManager.Instance.RobotState} robotY={_robot.transform.position.y:F3}");
        PlaceRobot(new Vector3(-2.2f, 0, 2.0f), Vector3.forward);
        yield return new WaitForSeconds(1f);

        // ---- poses (preloaded coral in the claw)
        yield return PoseAt("start", Key.None, 1f);
        L($"RESULT preload coral held={NodeOf("Coral").HasPiece()}");
        yield return PoseAt("L1", Key.U);
        yield return PoseAt("L2", Key.I);
        yield return PoseAt("L3", Key.O);
        yield return PoseAt("L4", Key.P);
        yield return Tap(Key.K);
        yield return new WaitForSeconds(0.4f);
        L($"RESULT L4 release coral held={NodeOf("Coral").HasPiece()}");
        yield return PoseAt("stow", Key.Z);

        // ---- floor coral: straight through the intake into the claw
        if (_robot.CurrentCoralStationMode.DropType != DropType.Ground) yield return Tap(Key.V);
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(1f);
        Keys(Key.LeftShift);
        yield return new WaitForSeconds(3f);
        L($"POSE groundIntake {Status()} {Pose()}");
        var t = _robot.transform;
        var gi = Find("Ground Coral Intake");
        var claw = Find("Coral Claw State");
        var coral = FreePiece("Coral", t.position);
        Assert.NotNull(coral, "no free coral");
        var p = gi.position;
        Put(coral, new Vector3(p.x, 0.07f, p.z) + t.forward * 0.12f, Along(coral, t.right));
        var rb = coral.GetComponent<Rigidbody>();
        float minSpeed = 99f, tMoved = -1f, tHeld = -1f;
        var last = coral.transform.position;
        var end = Time.time + 8f;
        var log = 0f;
        while (Time.time < end)
        {
            yield return new WaitForFixedUpdate();
            // Speed from displacement: once secured the piece is carried kinematically (rigidbody velocity 0).
            var speed = (coral.transform.position - last).magnitude / Time.fixedDeltaTime;
            last = coral.transform.position;
            var held = NodeOf("Coral").HasPiece() && NodeOf("Coral").atTarget;
            if (tMoved < 0 && speed > 0.2f) tMoved = Time.time;
            if (tMoved > 0 && tHeld < 0 && (coral.transform.position - claw.position).magnitude > 0.03f) minSpeed = Mathf.Min(minSpeed, speed);
            if (held && tHeld < 0) tHeld = Time.time;
            if (Time.time >= log)
            {
                L($"ground {Status()} coral={Local(coral.transform)} speed={speed:F2} distClaw={(coral.transform.position - claw.position).magnitude:F2}");
                log = Time.time + 0.25f;
            }
            if (tHeld > 0 && Time.time > tHeld + 0.5f) break;
        }
        Keys();
        yield return new WaitForSeconds(1.5f);
        L($"RESULT ground coral moved@{tMoved:F2} heldInClaw@{tHeld:F2} minSpeedOnTheWay={minSpeed:F2} heldAfterStow={NodeOf("Coral").HasPiece()} coral={Local(coral.transform)}");

        // ---- HP coral at the station, both facings
        yield return DropHeld();
        if (_robot.CurrentCoralStationMode.DropType != DropType.Station) yield return Tap(Key.V);
        yield return Tap(Key.Z);
        var station = Object.FindObjectsByType<CoralStation>(FindObjectsSortMode.None).First(s => s.Alliance == _robot.Alliance);
        var spawn = (Transform)typeof(CoralStation).GetField("coralSpawnpoint", F).GetValue(station);
        var hpBox = Find("Claw Coral Intake");
        foreach (var facing in new[] { 1f, -1f })
        {
            var sf = spawn.forward;
            sf.y = 0;
            sf.Normalize();
            PlaceRobot(spawn.position + sf * 0.75f, -sf * facing);
            yield return new WaitForSeconds(1f);
            Keys(Key.LeftShift);
            yield return new WaitForSeconds(2f);
            L($"POSE hp{facing} {Status()} {Pose()} spawn={Local(spawn)}");
            end = Time.time + 8f;
            while (Time.time < end && !NodeOf("Coral").HasPiece())
            {
                var c = FreePiece("Coral", hpBox.position);
                L($"hp{facing} {Status()} nearestCoral={(c ? Local(c.transform) : "-")}");
                yield return new WaitForSeconds(0.5f);
            }
            Keys();
            yield return new WaitForSeconds(1f);
            L($"RESULT hp coral facing={facing} held={NodeOf("Coral").HasPiece()}");
            yield return DropHeld();
        }

        // ---- scoring coral on the reef
        foreach (var (lvl, key) in new[] { ("L2", Key.I), ("L3", Key.O), ("L4", Key.P) })
            yield return ScoreAt(lvl, key);

        // ---- algae: ground, lollipop, low and high reef; processor and barge releases
        PlaceRobot(new Vector3(-2.2f, 0, 2.0f), Vector3.forward);
        if (_robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Algae) yield return Tap(Key.Semicolon);
        var ai = Find("Algae Intake");
        foreach (var (label, key) in new[] { ("groundAlgae", Key.None), ("lollipop", Key.U), ("lowAlgae", Key.I), ("highAlgae", Key.O) })
        {
            yield return DropHeld();
            if (key != Key.None) yield return Tap(key);
            Keys(Key.LeftShift);
            yield return new WaitForSeconds(3f);
            L($"POSE {label} {Status()} {Pose()}");
            var algae = FreePiece("Algae", t.position);
            Assert.NotNull(algae, "no free algae");
            var outDir = (ai.position - t.position);
            outDir.y = 0;
            Put(algae, ai.position + outDir.normalized * 0.1f, Quaternion.identity);
            end = Time.time + 4f;
            var at = Find("Algae Claw State");
            while (Time.time < end && !NodeOf("Algae").HasPiece())
            {
                L($"{label} algae={Local(algae.transform)} target={Local(at)} dist={(algae.transform.position - at.position).magnitude:F3} " +
                  $"vel={algae.GetComponent<Rigidbody>()?.velocity.magnitude:F2} intakeHas={ai.GetComponent<ReefscapeGamePieceIntake>().hasGamePiece}");
                yield return new WaitForSeconds(0.25f);
            }
            Keys();
            yield return new WaitForSeconds(1.5f);
            L($"RESULT {label} held={NodeOf("Algae").HasPiece()} {Status()}");
            if (!NodeOf("Algae").HasPiece()) continue;
            var score = label is "groundAlgae" or "lowAlgae" ? Key.U : Key.P;
            var heldAlgae = NodeOf("Algae").controller;
            yield return PoseAt(label + (score == Key.U ? "Proc" : "Barge"), score, 2.5f);
            yield return Tap(Key.K);
            yield return new WaitForSeconds(0.3f);
            var arb = heldAlgae.GetComponent<Rigidbody>();
            var v = arb ? t.InverseTransformDirection(arb.velocity) : Vector3.zero;
            L($"RESULT {label} {(score == Key.U ? "processor" : "barge")} released={!NodeOf("Algae").HasPiece()} vel(robot-local)={v:F2}");
            yield return new WaitForSeconds(1f);
        }

        // ---- climb
        yield return DropHeld();
        yield return PoseAt("climbPrep", Key.C);
        yield return PoseAt("climbed", Key.C);
        L("DONE");
    }
}
#endif
