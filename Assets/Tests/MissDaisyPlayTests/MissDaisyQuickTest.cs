// Plays 341 Miss Daisy like a driver in the Reefscape scene and checks real points, not just piece movement:
// floor coral driven into, HP station, coral on L1-L4 via auto-align, reef algae driven into (low and high),
// floor algae backed into, processor and barge, deep cage climb. Logs "[MM]" lines; every check ends in a
// "RESULT <name> PASS|FAIL ..." line.
//   Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testFilter MissDaisyMatchTest -testResults r.xml -logFile l.log
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using Games.Reefscape.Scoring;
using Games.Reefscape.Scoring.Scorers;
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

public class MissDaisyQuickTest
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const string Meta = "Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy/341/MissDaisy.asset";
    ReefscapeRobotBase _robot;
    Keyboard _kb;
    Key[] _held = Array.Empty<Key>();
    Vector3 _wDir, _dDir; // world direction the robot moves with W / D held
    readonly List<string> _results = new();

    static void L(string s) => Debug.Log("[MM] " + s);

    void Result(string name, bool pass, string detail)
    {
        var line = $"RESULT {name} {(pass ? "PASS" : "FAIL")} {detail}";
        _results.Add(line);
        L(line);
    }

    void Keys(params Key[] keys)
    {
        _held = keys;
        InputSystem.QueueStateEvent(_kb, new KeyboardState(keys));
    }

    void Hold(params Key[] keys) => Keys(_held.Union(keys).ToArray());
    void Release(params Key[] keys) => Keys(_held.Except(keys).ToArray());

    IEnumerator Tap(Key k)
    {
        Hold(k);
        yield return new WaitForSeconds(0.15f);
        Release(k);
        yield return new WaitForSeconds(0.15f);
    }

    Node NodeOf(string piece) => _robot.GetComponent<ReefscapeRobotGamePieceController>().GetPieceByName(piece);
    bool HasCoral => NodeOf("Coral").HasPiece();
    bool HasAlgae => NodeOf("Algae").HasPiece();
    Transform Find(string n) => _robot.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);
    object Field(string name) => _robot.GetType().GetField(name, F)?.GetValue(_robot);

    string Local(Vector3 w)
    {
        var l = _robot.transform.InverseTransformPoint(w);
        return $"({l.x:F2},{l.y:F2},{l.z:F2})";
    }

    string Status()
    {
        var s = $"sp={_robot.CurrentSetpoint} mode={_robot.CurrentRobotMode} drop={_robot.CurrentCoralStationMode.DropType}";
        if (Field("elevator") is GenericElevator el) s += $" elev={el.GetElevatorHeight():F1}in";
        foreach (var (j, a) in new[] { ("shoulder", JointAxis.X), ("wrist", JointAxis.Y), ("intakePivot", JointAxis.X), ("climber", JointAxis.X) })
            if (Field(j) is GenericJoint g) s += $" {j}={g.GetSingleAxisAngle(a):F0}";
        return s + $" coral={HasCoral} algae={HasAlgae}";
    }

    // ---------------------------------------------------------------- score data

    static readonly FieldInfo BlueData = typeof(GameSystems.Scoring.ScoreHandler<ReefscapeScoreData, ReefscapeScoreUI>).GetField("BlueScoreData", F);
    static readonly FieldInfo RedData = typeof(GameSystems.Scoring.ScoreHandler<ReefscapeScoreData, ReefscapeScoreUI>).GetField("RedScoreData", F);

    ReefscapeScoreData Score()
    {
        var h = Object.FindFirstObjectByType<ReefscapeScoreHandler>();
        if (!h) return new ReefscapeScoreData();
        return (ReefscapeScoreData)(_robot.Alliance == MoSimCore.Enums.Alliance.Blue ? BlueData : RedData).GetValue(h);
    }

    static string Pts(ReefscapeScoreData d) =>
        $"coral={d.CoralPoints} trough={d.TroughPoints} net={d.NetPoints} proc={d.ProcessorPoints} climb={d.ClimbPoints} park={d.ParkPoints} total={d.TotalPoints}";

    static readonly FieldInfo Branches = typeof(ReefPoleScorer).GetField("_branches", F);

    int ScoredBranches(string height = null) => Object.FindObjectsByType<ReefPoleScorer>(FindObjectsSortMode.None)
        .SelectMany(p => ((Array)Branches.GetValue(p) ?? Array.Empty<object>()).Cast<object>())
        .Count(b => (bool)b.GetType().GetProperty("Scored").GetValue(b) &&
                    (height == null || b.GetType().GetProperty("TargetHeight").GetValue(b).ToString() == height));

    // ---------------------------------------------------------------- robot handling

    PieceCtrl FreePiece(string name, Vector3 near, float maxDist = 99f)
    {
        var held = _robot.GetComponentsInChildren<PieceCtrl>().ToHashSet();
        return Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None)
            .Where(p => p.PieceName == name && !held.Contains(p) && p.gameObject.activeInHierarchy &&
                        (p.transform.position - near).magnitude < maxDist)
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
        forward.y = 0;
        var rot = Quaternion.LookRotation(forward.normalized, Vector3.up);
        pos.y = t.position.y;
        foreach (var rb in _robot.GetComponentsInChildren<Rigidbody>())
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        t.SetPositionAndRotation(pos, rot);
        Physics.SyncTransforms();
    }

    // Drive with the keyboard toward a world direction (the drive is field-oriented: keys were probed at the start).
    IEnumerator Drive(Vector3 worldDir, float seconds)
    {
        worldDir.y = 0;
        worldDir.Normalize();
        var keys = new List<Key>();
        var w = Vector3.Dot(worldDir, _wDir);
        var d = Vector3.Dot(worldDir, _dDir);
        if (w > 0.38f) keys.Add(Key.W);
        if (w < -0.38f) keys.Add(Key.S);
        if (d > 0.38f) keys.Add(Key.D);
        if (d < -0.38f) keys.Add(Key.A);
        Hold(keys.ToArray());
        yield return new WaitForSeconds(seconds);
        Release(keys.ToArray());
        yield return new WaitForSeconds(0.4f);
    }

    IEnumerator Reset(Games.Reefscape.Enums.ReefscapeRobotMode mode, DropType drop)
    {
        Keys();
        if (HasCoral || HasAlgae)
        {
            yield return Tap(Key.K);
            yield return new WaitForSeconds(1.5f);
        }
        if (_robot.CurrentRobotMode != mode) yield return Tap(Key.Semicolon);
        if (_robot.CurrentCoralStationMode.DropType != drop) yield return Tap(Key.V);
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(1.5f);
    }

    Vector3 Open => new Vector3(-2.5f, 0, 1.6f); // open carpet on the blue side, away from the reef

    // ---------------------------------------------------------------- pieces into the robot

    // Floor coral: lying across the robot's path 1.1 m ahead, the robot drives into it holding intake.
    IEnumerator GetFloorCoral(string label)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        var coral = FreePiece("Coral", t.position);
        Put(coral, t.position + t.forward * 1.1f + Vector3.up * 0.06f, Along(coral, t.right));
        yield return new WaitForSeconds(0.5f);
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2f);
        var end = Time.time + 4f;
        while (Time.time < end && !HasCoral)
            yield return Drive(t.forward, 0.25f);
        yield return new WaitForSeconds(1f);
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1.5f);
        Result(label, HasCoral && NodeOf("Coral").atTarget, $"{Status()} coral={Local(coral.transform.position)}");
    }

    // HP station: robot backed up to the chute facing it (front side) and holding intake until the station drops.
    IEnumerator GetStationCoral(string label, float facing)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Station);
        var station = Object.FindObjectsByType<CoralStation>(FindObjectsSortMode.None).First(s => s.Alliance == _robot.Alliance);
        var spawn = (Transform)typeof(CoralStation).GetField("coralSpawnpoint", F).GetValue(station);
        var sf = spawn.forward;
        sf.y = 0;
        sf.Normalize();
        PlaceRobot(spawn.position + sf * 0.8f, -sf * facing);
        yield return new WaitForSeconds(0.5f);
        Hold(Key.LeftShift);
        var end = Time.time + 8f;
        while (Time.time < end && !HasCoral) yield return new WaitForSeconds(0.25f);
        yield return new WaitForSeconds(0.5f);
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1f);
        Result(label, HasCoral, Status());
    }

    AlignNode Face(int i) => Object.FindObjectsByType<AlignNode>(FindObjectsSortMode.None)
        .Where(n => n.GetComponentInParent<Reef>() && n.GetComponentInParent<Reef>().Alliance == _robot.Alliance)
        .OrderBy(n => n.transform.position.x).ThenBy(n => n.transform.position.z).ElementAt(i);

    Vector3 ReefCentre(AlignNode face) => face.GetComponentInParent<Reef>().transform.position;

    // ---------------------------------------------------------------- scoring

    // Coral on the reef: raise 1.3 m out (facing the reef face), then hold auto-align (it drives in), then place.
    IEnumerator ScoreCoral(string level, Key key, int face)
    {
        var f = Face(face);
        var toReef = ReefCentre(f) - f.transform.position;
        toReef.y = 0;
        toReef.Normalize();
        PlaceRobot(f.transform.position - toReef * 1.3f, toReef);
        yield return new WaitForSeconds(0.5f);
        var before = Score();
        var bBranch = ScoredBranches(level == "L1" ? null : level);
        var bTrough = before.TroughPoints;
        yield return Tap(key);
        yield return new WaitForSeconds(2.5f);
        Hold(Key.Q);
        yield return new WaitForSeconds(3f);
        L($"score {level} at node: {Status()} robot->node {Local(f.LeftNode.transform.position)}");
        var piece = NodeOf("Coral").controller;
        var br = Object.FindObjectsByType<ReefPoleScorer>(FindObjectsSortMode.None)
            .SelectMany(p => ((Array)Branches.GetValue(p)).Cast<object>())
            .Where(b => level == "L1" || b.GetType().GetProperty("TargetHeight").GetValue(b).ToString() == level)
            .OrderBy(b => ((Vector3)b.GetType().GetField("center").GetValue(b) - (piece ? piece.transform.position : Vector3.zero)).sqrMagnitude).First();
        var bc = (Vector3)br.GetType().GetField("center").GetValue(br);
        var bq = (Quaternion)br.GetType().GetField("rotation").GetValue(br);
        string Rel() => piece ? (Quaternion.Inverse(bq) * (piece.transform.position - bc)).ToString("F2") : "-";
        L($"score {level} coral(robot)={(piece ? Local(piece.transform.position) : "-")} branch(robot)={Local(bc)} coralInBranch={Rel()} coralAxis(robot)={(piece ? _robot.transform.InverseTransformDirection(piece.transform.rotation * Vector3.forward).ToString("F2") : "-")}");
        yield return Tap(Key.K);
        for (var k = 0; k < 6; k++)
        {
            yield return new WaitForSeconds(0.5f);
            L($"score {level} +{(k + 1) * 0.5f:F1}s coralInBranch={Rel()} coral(robot)={(piece ? Local(piece.transform.position) : "-")} branchesScored={ScoredBranches()}");
        }
        Release(Key.Q);
        var after = Score();
        if (level == "L1")
            Result($"score {level}", after.TroughPoints > bTrough, $"trough {bTrough}->{after.TroughPoints} | {Pts(after)}");
        else
            Result($"score {level}", ScoredBranches(level) > bBranch, $"{level} branches {bBranch}->{ScoredBranches(level)} | {Pts(after)}");
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(1f);
    }

    // Reef algae: algae sitting on the reef face; stand 1.3 m out, pick the level by its height, hold intake and
    // drive in.
    IEnumerator GetReefAlgae(string label, bool high)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Algae, DropType.Station);
        var reefAlgae = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None)
            .Where(p => p.PieceName == "Algae")
            .Select(p => (p, face: Object.FindObjectsByType<AlignNode>(FindObjectsSortMode.None)
                .Where(n => n.GetComponentInParent<Reef>() && n.GetComponentInParent<Reef>().Alliance == _robot.Alliance)
                .OrderBy(n => (n.transform.position - p.transform.position).sqrMagnitude).First()))
            .Where(x => Vector2.Distance(new Vector2(x.face.transform.position.x, x.face.transform.position.z),
                                         new Vector2(x.p.transform.position.x, x.p.transform.position.z)) < 1.2f)
            .OrderBy(x => x.p.transform.position.y).ToList();
        L($"reef algae heights: {string.Join(", ", reefAlgae.Select(x => x.p.transform.position.y.ToString("F2")))}");
        var pick = reefAlgae.FirstOrDefault(x => high ? x.p.transform.position.y > 1.1f : x.p.transform.position.y < 1.1f);
        if (pick.p == null)
        {
            Result(label, false, "no algae at that height on our reef");
            yield break;
        }
        var toReef = ReefCentre(pick.face) - pick.face.transform.position;
        toReef.y = 0;
        toReef.Normalize();
        PlaceRobot(pick.face.transform.position - toReef * 1.3f, toReef);
        yield return new WaitForSeconds(0.5f);
        yield return Tap(high ? Key.O : Key.I);
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2.5f);
        L($"{label} before approach: {Status()} algae(robot)={Local(pick.p.transform.position)} target={Local(Find("Algae Claw State").position)}");
        Hold(Key.Q);
        var end = Time.time + 4f;
        while (Time.time < end && !HasAlgae) yield return new WaitForSeconds(0.2f);
        L($"{label} at reef: {Status()} algae(robot)={Local(pick.p.transform.position)} target={Local(Find("Algae Claw State").position)}");
        yield return new WaitForSeconds(0.5f);
        Release(Key.Q, Key.LeftShift);
        yield return Drive(-toReef, 0.8f);
        yield return new WaitForSeconds(1f);
        Result(label, HasAlgae, $"{Status()} algae(robot)={Local(pick.p.transform.position)}");
    }

    // Floor algae: ball 1.2 m behind the robot; algae mode, hold intake, back into it.
    IEnumerator GetFloorAlgae(string label)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Algae, DropType.Station);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        var algae = FreePiece("Algae", t.position);
        Put(algae, t.position - t.forward * 1.2f + Vector3.up * 0.25f, Quaternion.identity);
        yield return new WaitForSeconds(1f);
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2.5f);
        var end = Time.time + 4f;
        while (Time.time < end && !HasAlgae)
            yield return Drive(-t.forward, 0.25f);
        yield return new WaitForSeconds(1f);
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1f);
        Result(label, HasAlgae, $"{Status()} algae(robot)={Local(algae.transform.position)}");
    }

    // Processor: drive up to the alliance processor facing it, L1 (-> Processor) and place.
    IEnumerator ScoreProcessor()
    {
        var proc = Object.FindObjectsByType<ProcessorScorer>(FindObjectsSortMode.None).First(p => p.Alliance == _robot.Alliance);
        var trig = (BoxCollider)typeof(ProcessorScorer).GetField("scoreTrigger", F).GetValue(proc);
        var target = trig ? trig.bounds.center : proc.transform.position;
        // the processor opening is in the side wall: approach straight from the field side, facing the wall
        var wall = new Vector3(0, 0, Mathf.Sign(target.z));
        PlaceRobot(new Vector3(target.x, 0, target.z) - wall * 1.6f, wall);
        yield return new WaitForSeconds(0.5f);
        var before = Score();
        yield return Tap(Key.U);
        yield return new WaitForSeconds(2f);
        L($"processor start robot={_robot.transform.position:F2} target={target:F2}");
        yield return Drive(wall, 1.5f);
        L($"processor after drive robot={_robot.transform.position:F2}");
        L($"processor approach: {Status()} trigger(robot)={Local(target)}");
        yield return Tap(Key.K);
        yield return new WaitForSeconds(3.5f);
        var after = Score();
        Result("score processor", after.TotalPoints > before.TotalPoints, $"{Pts(before)} -> {Pts(after)}");
    }

    // Barge: stand at the barge facing the net, L4 (-> Barge) and throw.
    IEnumerator ScoreBarge()
    {
        var barge = Object.FindObjectsByType<BargeScorer>(FindObjectsSortMode.None).First(b => b.Alliance == _robot.Alliance);
        var box = barge.GetComponent<BoxCollider>();
        var target = box ? box.bounds.center : barge.transform.position;
        var ourSide = Mathf.Sign(Object.FindObjectsByType<Reef>(FindObjectsSortMode.None).First(r => r.Alliance == _robot.Alliance).transform.position.x);
        var toNet = new Vector3(-ourSide, 0, 0);
        PlaceRobot(new Vector3(ourSide * 1.6f, 0, target.z), toNet);
        yield return new WaitForSeconds(0.5f);
        var before = Score();
        yield return Tap(Key.P);
        yield return new WaitForSeconds(2.5f);
        var end = Time.time + 3f;
        while (Time.time < end && Mathf.Abs(_robot.transform.position.x) > 0.72f)
            yield return Drive(toNet, 0.15f);
        yield return new WaitForSeconds(1f);
        L($"barge: {Status()} net(robot)={Local(target)}");
        yield return Tap(Key.K);
        yield return new WaitForSeconds(3f);
        var after = Score();
        Result("score barge", after.NetPoints > before.NetPoints, $"{Pts(before)} -> {Pts(after)}");
        yield return Tap(Key.Z);
    }

    // Deep cage: back the deployed climber into an alliance cage, climb, check off the ground with the cage caught.
    IEnumerator Climb()
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Station);
        // our side of the barge is the side our reef is on; back into the cage nearest the robot from there
        var ourSide = Mathf.Sign(Object.FindObjectsByType<Reef>(FindObjectsSortMode.None).First(r => r.Alliance == _robot.Alliance).transform.position.x);
        var cage = GameObject.FindGameObjectsWithTag("Cage").OrderBy(c => Mathf.Abs(c.transform.position.x)).ThenBy(c => Mathf.Abs(c.transform.position.z)).First();
        var cs = _robot.GetComponent<ClimbScorer>();
        var cp = cage.transform.position;
        var away = new Vector3(ourSide, 0, 0);
        PlaceRobot(new Vector3(cp.x + away.x * 1.1f, 0, cp.z), away);
        yield return new WaitForSeconds(0.5f);
        yield return Tap(Key.C);
        yield return new WaitForSeconds(3f);
        var end = Time.time + 4f;
        while (Time.time < end && !cs.ScoringTriggered)
            yield return Drive(-away, 0.2f);
        L($"climb at cage: {Status()} triggered={cs.ScoringTriggered} cage(robot)={Local(cp)}");
        yield return Tap(Key.C);
        yield return new WaitForSeconds(4f);
        var dc = _robot.GetComponent<RobotFramework.Controllers.Drivetrain.DriveController>();
        Result("climb", cs.ScoringTriggered && !dc.IsTouchingGround,
            $"{Status()} triggered={cs.ScoringTriggered} touchingGround={dc.IsTouchingGround} robotY={_robot.transform.position.y:F2}");
    }

    [UnityTest]
    [Timeout(1500000)]
    public IEnumerator MissDaisyQuick()
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
        yield return new WaitUntil(() => RobotLoader.Instance && RobotLoader.Instance.AllRobotsLoaded || Time.realtimeSinceStartup - t0 > 60);
        SceneManager.sceneLoaded += (s, m) =>
        {
            var rsc = Object.FindFirstObjectByType<RobotSpawnController>();
            if (rsc) typeof(RobotSpawnController).GetField("robotOverride", F)?.SetValue(rsc, meta);
        };
        yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/Reefscape.unity", new LoadSceneParameters(LoadSceneMode.Single));
        t0 = Time.realtimeSinceStartup;
        yield return new WaitUntil(() => (_robot = Object.FindFirstObjectByType<ReefscapeRobotBase>()) || Time.realtimeSinceStartup - t0 > 30);
        Assert.NotNull(_robot, "robot not spawned");
        foreach (var i in _robot.GetComponentsInChildren<ReefscapeGamePieceIntake>(true)) i.debugLog = true;
        var pi = _robot.GetComponent<PlayerInput>();
        try { pi.SwitchCurrentControlScheme("Robot 1", _kb); } catch (Exception e) { L("scheme " + e.Message); }
        yield return new WaitForSeconds(1f);
        new GameObject("KeepEnabled").AddComponent<MissDaisyKeepEnabled>().timer = Object.FindFirstObjectByType<BaseTimerManager>();
        yield return new WaitForSeconds(2f);

        // layers: every robot object on Robot (a stray layer lets pieces hit the robot's own parts)
        var robotLayer = LayerMask.NameToLayer("Robot");
        var off = _robot.GetComponentsInChildren<Transform>(true)
            .Where(x => x.gameObject.layer != robotLayer && !x.GetComponentInParent<PieceCtrl>() &&
                        !x.GetComponentsInChildren<Collider>(true).Any(c => c.CompareTag("Coral") || c.CompareTag("Algae")))
            .Select(x => x.name).ToList();
        Result("robot layer", off.Count == 0, $"{off.Count} objects off the Robot layer: {string.Join(", ", off.Take(8))}");

        // probe the drive: which world direction W and D move the robot
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var p0 = _robot.transform.position;
        Hold(Key.W);
        yield return new WaitForSeconds(0.5f);
        Release(Key.W);
        yield return new WaitForSeconds(0.5f);
        _wDir = (_robot.transform.position - p0).normalized;
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        p0 = _robot.transform.position;
        Hold(Key.D);
        yield return new WaitForSeconds(0.5f);
        Release(Key.D);
        yield return new WaitForSeconds(0.5f);
        _dDir = (_robot.transform.position - p0).normalized;
        L($"drive probe: W -> {_wDir:F2}, D -> {_dDir:F2}");

        yield return GetFloorCoral("floor coral quick");
        var node = NodeOf("Coral");
        for (var k = 0; k < 3; k++)
        {
            var pc = node.controller;
            string Chain(Transform t) { var s = ""; while (t) { s = t.name + "/" + s; t = t.parent; } return s; }
            L($"quick k={k} has={node.HasPiece()} at={node.atTarget} st={node.currentStateNum} was={node.wasMovingTo} " +
              $"piece={(pc ? pc.name + " " + Local(pc.transform.position) + " parent=" + Chain(pc.transform.parent) : "null")} " +
              $"claw={Local(Find("Coral Claw State").position)} exit={Local(Find("Coral Intake Exit").position)}");
            if (k == 0) { PlaceRobot(_robot.transform.position + _robot.transform.right * 1.0f, _robot.transform.forward); yield return new WaitForSeconds(1f); }
            if (k == 1) { yield return Tap(Key.I); yield return new WaitForSeconds(3f); }
        }
        L("SUMMARY\n" + string.Join("\n", _results));
        L($"DONE pass={_results.Count(r => r.Contains(" PASS "))} fail={_results.Count(r => r.Contains(" FAIL "))}");
    }
}
#endif
